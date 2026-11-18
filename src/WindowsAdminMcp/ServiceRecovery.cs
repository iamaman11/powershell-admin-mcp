using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

internal sealed record RecoveryAction(uint Type, uint DelayMs);
internal sealed record RecoveryObservation(bool Ready, uint ResetSeconds, bool NonCrashFailures,
    RecoveryAction[] Actions);

// SCM owns retries; the application has no second supervisor. Never reboot or run a command.
internal static class ServiceRecovery
{
    private static readonly RecoveryAction[] Expected = [new(1, 5000), new(1, 15000), new(1, 60000), new(0, 0)];
    internal static RecoveryObservation Observe()
    {
        using var manager = OpenSCManagerW(null, null, 1);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var service = OpenServiceW(manager, ServiceIdentity.Name, 1);
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr buffer = Query(service, 2);
        try
        {
            var config = Marshal.PtrToStructure<FailureActions>(buffer);
            if (config.Count > 64) throw new InvalidOperationException("SCM recovery action count invalid.");
            var actions = new RecoveryAction[config.Count];
            for (int i = 0; i < actions.Length; i++)
            {
                var action = Marshal.PtrToStructure<ActionConfig>(IntPtr.Add(config.Actions, i * Marshal.SizeOf<ActionConfig>()));
                actions[i] = new(action.Type, action.Delay);
            }
            IntPtr flags = Query(service, 4);
            try
            {
                bool nonCrash = Marshal.ReadInt32(flags) != 0;
                bool ready = config.Reset == 86400 && nonCrash && actions.SequenceEqual(Expected) &&
                    string.IsNullOrEmpty(Marshal.PtrToStringUni(config.Command)) &&
                    string.IsNullOrEmpty(Marshal.PtrToStringUni(config.RebootMessage));
                return new(ready, config.Reset, nonCrash, actions);
            }
            finally { Marshal.FreeHGlobal(flags); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static RecoveryObservation Configure()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Administrator required.");
        ServiceIdentity.RequireStoppedOrAbsent();
        using var manager = OpenSCManagerW(null, null, 1);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var service = OpenServiceW(manager, ServiceIdentity.Name, 2 | 16); // CONFIG + START (restart actions)
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        int size = Marshal.SizeOf<ActionConfig>();
        IntPtr memory = Marshal.AllocHGlobal(Expected.Length * size);
        try
        {
            for (int i = 0; i < Expected.Length; i++)
                Marshal.StructureToPtr(new ActionConfig { Type = Expected[i].Type, Delay = Expected[i].DelayMs }, IntPtr.Add(memory, i * size), false);
            var config = new FailureActions { Reset = 86400, Count = (uint)Expected.Length, Actions = memory };
            // Empty strings explicitly remove historical command/reboot-message settings.
            config.Command = Marshal.StringToHGlobalUni("");
            config.RebootMessage = Marshal.StringToHGlobalUni("");
            try
            {
                if (!ChangeServiceConfig2W(service, 2, ref config)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(config.Command); Marshal.FreeHGlobal(config.RebootMessage); }
            int flags = 1;
            if (!ChangeServiceConfig2W(service, 4, ref flags)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(memory); }
        var observed = Observe();
        if (!observed.Ready) throw new InvalidOperationException("SCM recovery readback failed.");
        return observed;
    }

    private static IntPtr Query(ServiceHandle service, uint level)
    {
        if (QueryServiceConfig2W(service, level, IntPtr.Zero, 0, out uint needed) || Marshal.GetLastWin32Error() != 122 || needed is 0 or > 65536)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr memory = Marshal.AllocHGlobal((int)needed);
        if (QueryServiceConfig2W(service, level, memory, needed, out _)) return memory;
        int error = Marshal.GetLastWin32Error();
        Marshal.FreeHGlobal(memory);
        throw new Win32Exception(error);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FailureActions { public uint Reset; public IntPtr RebootMessage, Command; public uint Count; public IntPtr Actions; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ActionConfig { public uint Type, Delay; }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2W(ServiceHandle service, uint level, IntPtr buffer, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(ServiceHandle service, uint level, ref FailureActions config);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(ServiceHandle service, uint level, ref int flag);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
