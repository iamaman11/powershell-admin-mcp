using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Documented Toolhelp32 snapshot: one bounded native read for process ancestry
// and thread counts. PID ancestry is observed metadata, never an identity token
// authorizing a later mutation (PIDs can be reused).
internal static class SystemProcessQueries
{
    private const uint Th32csSnapProcess = 0x00000002;
    private const int ErrorNoMoreFiles = 18;
    private const int MaxProcesses = 100000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

    internal sealed record Entry(int? ParentPid, int ThreadCount);

    internal static IReadOnlyDictionary<int, Entry>? Read(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var snapshot = new SafeFileHandle(CreateToolhelp32Snapshot(Th32csSnapProcess, 0), true);
        if (snapshot.IsInvalid) return null;
        var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
        if (!Process32First(snapshot.DangerousGetHandle(), ref entry))
            return Marshal.GetLastPInvokeError() == ErrorNoMoreFiles ? new Dictionary<int, Entry>() : null;

        var rows = new Dictionary<int, Entry>();
        do
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count == MaxProcesses) return null;
            if (entry.ProcessId <= int.MaxValue && entry.ThreadCount <= int.MaxValue)
                rows[(int)entry.ProcessId] = new(
                    entry.ParentProcessId is > 0 and <= int.MaxValue ? (int)entry.ParentProcessId : null,
                    (int)entry.ThreadCount);
            entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
        }
        while (Process32Next(snapshot.DangerousGetHandle(), ref entry));
        return Marshal.GetLastPInvokeError() == ErrorNoMoreFiles ? rows : null;
    }
}
