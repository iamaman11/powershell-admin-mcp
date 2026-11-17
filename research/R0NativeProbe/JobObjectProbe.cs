using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal static partial class JobObjectProbe
{
    private const uint CreateSuspended = 0x4;
    private const uint CreateNoWindow = 0x08000000;
    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformation = 9;
    private const int BasicProcessIdList = 3;

    public static void Run()
    {
        using var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw LastError("CreateJobObject");
        var limits = new ExtendedLimits();
        limits.Basic.LimitFlags = KillOnJobClose;
        if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
            throw LastError("SetInformationJobObject");

        // Use a fixed, inert child tree. Assignment happens BEFORE first instruction.
        string shell = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var startup = new StartupInfo { cb = (uint)Marshal.SizeOf<StartupInfo>() };
        var command = new StringBuilder("\"" + shell + "\" /d /c ping.exe -n 60 127.0.0.1 >nul");
        if (!CreateProcessW(shell, command, IntPtr.Zero, IntPtr.Zero, false,
            CreateSuspended | CreateNoWindow, IntPtr.Zero, null, ref startup, out var child))
            throw LastError("CreateProcess");
        using var process = new SafeFileHandle(child.Process, true);
        using var thread = new SafeFileHandle(child.Thread, true);
        var members = new List<Process>();
        try
        {
            if (!AssignProcessToJobObject(job, process)) throw LastError("AssignProcessToJobObject");
            if (ResumeThread(thread) == uint.MaxValue) throw LastError("ResumeThread");
            int[] ids = [];
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                ids = ReadProcessIds(job);
                if (ids.Length >= 2) break;
                Thread.Sleep(50);
            }
            if (ids.Length < 2 || !ids.Contains((int)child.ProcessId))
                throw new InvalidOperationException("Root and descendant not observed in job");
            foreach (int id in ids)
            {
                var member = Process.GetProcessById(id);
                members.Add(member);
                _ = member.Handle; // retain process identity across PID reuse
            }
            job.Dispose(); // OS must kill the root and ALL observed descendants.
            foreach (var member in members)
                if (!member.WaitForExit(5000))
                    throw new InvalidOperationException("Observed child survived job close");
            Console.WriteLine($"PASS jobobject.suspended_assign_and_kill_tree members={ids.Length}");
        }
        finally
        {
            job.Dispose();
            _ = TerminateProcess(process, 1); // also clean up if assignment failed
            foreach (var member in members) member.Dispose();
        }
    }

    private static int[] ReadProcessIds(SafeFileHandle job)
    {
        const int capacity = 64;
        uint size = (uint)(8 + IntPtr.Size * capacity);
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!QueryInformationJobObject(job, BasicProcessIdList, buffer, size, out _))
                throw LastError("QueryInformationJobObject");
            int count = Marshal.ReadInt32(buffer, 4);
            if (count < 0 || count > capacity) throw new InvalidOperationException("Invalid process count");
            return Enumerable.Range(0, count)
                .Select(i => checked((int)Marshal.ReadIntPtr(buffer, 8 + IntPtr.Size * i).ToInt64())).ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static Win32Exception LastError(string operation) => new(Marshal.GetLastWin32Error(), operation);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint cb;
        public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2;
        public IntPtr ReservedPointer, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, IntPtr info, uint length, out uint returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
}
