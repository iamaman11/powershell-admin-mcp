using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// A single native process tree, with only the three explicitly listed inherited
// handles. The job is assigned while the initial thread is still suspended.
internal sealed class NativePowerShell : IDisposable
{
    private const uint CreateSuspended = 0x4, CreateNoWindow = 0x08000000,
        ExtendedStartupInfoPresent = 0x00080000, CreateUnicodeEnvironment = 0x400;
    private const uint KillOnJobClose = 0x2000;
    private readonly SafeFileHandle job;
    internal Process Process { get; }
    internal FileStream Input { get; }
    internal FileStream Output { get; }
    internal FileStream Error { get; }

    private NativePowerShell(SafeFileHandle job, Process process, FileStream input, FileStream output, FileStream error)
        => (this.job, Process, Input, Output, Error) = (job, process, input, output, error);

    internal static NativePowerShell Start(string executable, string commandLine, string cwd)
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) { job.Dispose(); throw NativeFailure.Last("CREATE_JOB"); }
        Process? managed = null;
        FileStream? input = null, output = null, error = null;
        bool success = false;
        try
        {
            var limits = new ExtendedLimits();
            limits.Basic.LimitFlags = KillOnJobClose;
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw NativeFailure.Last("SET_JOB_LIMITS");

            using var stdin = new PipePair(parentReads: false);
            using var stdout = new PipePair(parentReads: true);
            using var stderr = new PipePair(parentReads: true);
            using var attributes = new HandleList(stdin.Child, stdout.Child, stderr.Child);
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo
                {
                    Size = (uint)Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    StdInput = stdin.Child.DangerousGetHandle(),
                    StdOutput = stdout.Child.DangerousGetHandle(),
                    StdError = stderr.Child.DangerousGetHandle()
                },
                AttributeList = attributes.Pointer
            };

            // The tunnel credential belongs to the tunnel, not a child shell.
            var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                if (entry.Key is string key && entry.Value is string value &&
                    !key.Equals("CONTROL_PLANE_API_KEY", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("OPENAI_API_KEY", StringComparison.OrdinalIgnoreCase))
                    env[key] = value;
            IntPtr environment = Marshal.StringToHGlobalUni(string.Join('\0', env.Select(e => $"{e.Key}={e.Value}")) + "\0\0");
            ProcessInformation child;
            try
            {
                if (!CreateProcessW(executable, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true,
                    CreateSuspended | CreateNoWindow | ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    environment, cwd, ref startup, out child))
                    throw NativeFailure.Last("CREATE_PROCESS");
            }
            finally { Marshal.FreeHGlobal(environment); }

            using var process = new SafeFileHandle(child.Process, true);
            using var thread = new SafeFileHandle(child.Thread, true);
            try
            {
                if (!AssignProcessToJobObject(job, process)) throw NativeFailure.Last("ASSIGN_JOB");
                managed = Process.GetProcessById(checked((int)child.ProcessId));
                _ = managed.Handle;
                input = stdin.TakeParent(FileAccess.Write);
                output = stdout.TakeParent(FileAccess.Read);
                error = stderr.TakeParent(FileAccess.Read);
                // Parent copies of the child endpoints must not delay EOF.
                stdin.Child.Dispose(); stdout.Child.Dispose(); stderr.Child.Dispose();
                if (ResumeThread(thread) == uint.MaxValue) throw NativeFailure.Last("RESUME_PROCESS");
                var result = new NativePowerShell(job, managed, input, output, error);
                success = true;
                return result;
            }
            catch
            {
                _ = TerminateProcess(process, 1); // also covers failure before assignment
                throw;
            }
        }
        finally
        {
            if (!success)
            {
                job.Dispose();
                input?.Dispose(); output?.Dispose(); error?.Dispose(); managed?.Dispose();
            }
        }
    }

    internal void StopTree() => job.Dispose(); // KILL_ON_JOB_CLOSE; no breakaway

    public void Dispose()
    {
        StopTree();
        Input.Dispose(); Output.Dispose(); Error.Dispose(); Process.Dispose();
    }

    private sealed class PipePair : IDisposable
    {
        private SafeFileHandle? parent;
        internal SafeFileHandle Child { get; }
        internal PipePair(bool parentReads)
        {
            var sa = new SecurityAttributes
            {
                Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
                InheritHandle = true
            };
            if (!CreatePipe(out var read, out var write, ref sa, 0))
                throw NativeFailure.Last("CREATE_PIPE");
            parent = parentReads ? read : write;
            Child = parentReads ? write : read;
            if (!SetHandleInformation(parent, 1, 0))
            {
                var failure = NativeFailure.Last("PROTECT_PIPE_HANDLE");
                Dispose();
                throw failure;
            }
        }
        internal FileStream TakeParent(FileAccess access)
        {
            var stream = new FileStream(parent ?? throw new InvalidOperationException(), access, 4096, false);
            parent = null;
            return stream;
        }
        public void Dispose() { parent?.Dispose(); Child.Dispose(); }
    }

    private sealed class HandleList : IDisposable
    {
        internal IntPtr Pointer { get; private set; }
        private IntPtr values;
        private bool initialized;
        internal HandleList(params SafeFileHandle[] handles)
        {
            nuint size = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == 0) throw NativeFailure.Last("SIZE_HANDLE_LIST");
            Pointer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                if (!InitializeProcThreadAttributeList(Pointer, 1, 0, ref size))
                    throw NativeFailure.Last("INIT_HANDLE_LIST");
                initialized = true;
                values = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
                for (int i = 0; i < handles.Length; i++) Marshal.WriteIntPtr(values, i * IntPtr.Size, handles[i].DangerousGetHandle());
                if (!UpdateProcThreadAttribute(Pointer, 0, (nuint)0x00020002, values,
                    (nuint)(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero))
                    throw NativeFailure.Last("SET_HANDLE_LIST");
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (initialized) DeleteProcThreadAttributeList(Pointer);
            if (Pointer != IntPtr.Zero) Marshal.FreeHGlobal(Pointer);
            if (values != IntPtr.Zero) Marshal.FreeHGlobal(values);
            Pointer = values = IntPtr.Zero;
            initialized = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length; public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }
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
        public BasicLimits Basic; public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2;
        public IntPtr ReservedPointer, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx { public StartupInfo Startup; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value,
        nuint size, IntPtr previousValue, IntPtr returnSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr environment, string cwd, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
}

internal sealed class NativeFailure(string stage, int code) : Win32Exception(code, stage)
{
    internal string Stage { get; } = stage;
    internal static NativeFailure Last(string stage) => new(stage, Marshal.GetLastWin32Error());
}
