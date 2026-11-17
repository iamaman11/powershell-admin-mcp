using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class ServiceIdentity
{
    internal const string Name = "WindowsAdminMCP";
    internal const string Pipe = "WindowsAdminMCP.v1";

    internal static uint RunningProcessId()
    {
        var status=ReadStatus();
        if(status is not {} s || s.CurrentState!=4 || s.ProcessId==0)throw new Win32Exception(1062);
        return s.ProcessId;
    }
    internal static void RequireStoppedOrAbsent()
    {
        var status=ReadStatus();
        if(status is {} s && (s.CurrentState!=1 || s.ProcessId!=0))
            throw new InvalidOperationException("Offline maintenance requires stopped SCM service.");
    }
    private static ServiceStatus? ReadStatus()
    {
        using var manager = OpenSCManagerW(null, null, 1);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var service = OpenServiceW(manager, Name, 4);
        if (service.IsInvalid)
        {
            int error=Marshal.GetLastWin32Error();
            if(error==1060)return null;
            throw new Win32Exception(error);
        }
        if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatus>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode,
            CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManagerW(string? machineName, string? databaseName, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenServiceW(ServiceHandle manager, string serviceName, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(ServiceHandle service, int infoLevel, out ServiceStatus status,
        uint size, out uint requiredSize);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
