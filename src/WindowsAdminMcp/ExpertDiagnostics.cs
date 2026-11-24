using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

// These diagnostics run under the MCP frontend's actual Windows token. They do
// not enable SeDebugPrivilege/SeSecurityPrivilege, impersonate SYSTEM, or write
// any ACLs. ACCESS_DENIED is evidence, not an empty security descriptor.

[JsonConverter(typeof(JsonStringEnumConverter<FileAclCode>))]
public enum FileAclCode
{
    COMPLETED, INVALID_ARGUMENT, NOT_FOUND, ACCESS_DENIED, QUERY_FAILED,
    RESOURCE_LIMIT, UNSUPPORTED_PLATFORM
}

[JsonConverter(typeof(JsonStringEnumConverter<FileDaclState>))]
public enum FileDaclState { PRESENT, EMPTY, NULL_DACL, NOT_PRESENT }

public sealed record FileAceInfo(
    [property: JsonPropertyName("ace_type")] string AceType,
    [property: JsonPropertyName("sid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sid,
    [property: JsonPropertyName("access_mask"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? AccessMask,
    [property: JsonPropertyName("is_inherited")] bool IsInherited,
    [property: JsonPropertyName("flags")] string Flags);

public sealed record FileAclResult(
    [property: JsonPropertyName("status")] FileAclCode Status,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("observed_utc")] DateTimeOffset ObservedUtc,
    [property: JsonPropertyName("owner_sid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OwnerSid = null,
    [property: JsonPropertyName("group_sid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GroupSid = null,
    [property: JsonPropertyName("dacl_state"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FileDaclState? DaclState = null,
    [property: JsonPropertyName("dacl_protected"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? DaclProtected = null,
    [property: JsonPropertyName("dacl_auto_inherited"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? DaclAutoInherited = null,
    [property: JsonPropertyName("ace_count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? AceCount = null,
    [property: JsonPropertyName("aces"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FileAceInfo[]? Aces = null,
    [property: JsonPropertyName("next_offset"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NextOffset = null,
    [property: JsonPropertyName("native_error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NativeError = null,
    [property: JsonPropertyName("elapsed_ms")] long ElapsedMs = 0);

internal static class NativeFileAcl
{
    private const uint OwnerSecurityInformation = 0x1;
    private const uint GroupSecurityInformation = 0x2;
    private const uint DaclSecurityInformation = 0x4;
    private const uint RequestedInformation = OwnerSecurityInformation |
        GroupSecurityInformation | DaclSecurityInformation;
    private const uint FileObject = 1;
    private const uint ErrorFileNotFound = 2, ErrorPathNotFound = 3, ErrorAccessDenied = 5;
    private const uint ErrorBadNetPath = 53, ErrorBadNetName = 67;
    private const int MaxAceCount = 4096;
    private const int MaxSddlChars = 65536;

    [DllImport("advapi32.dll", EntryPoint = "GetNamedSecurityInfoW", ExactSpelling = true,
        CharSet = CharSet.Unicode)]
    private static extern uint GetNamedSecurityInfo(string objectName, uint objectType,
        uint securityInformation, out IntPtr owner, out IntPtr group, out IntPtr dacl,
        IntPtr sacl, out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", EntryPoint = "ConvertSecurityDescriptorToStringSecurityDescriptorW",
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertToSddl(IntPtr securityDescriptor, uint revision,
        uint securityInformation, out IntPtr sddl, out uint length);

    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorDacl", ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool daclPresent,
        out IntPtr dacl, [MarshalAs(UnmanagedType.Bool)] out bool daclDefaulted);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    internal static bool Valid(string? path, int pageSize, int offset)
    {
        if (path is not { Length: >= 3 and <= 1024 } || pageSize is < 1 or > 64 ||
            offset is < 0 or > MaxAceCount || path.Contains('\0')) return false;
        // Only local drive paths. A path such as \\server\share or \\?\C:\...
        // can trigger network access or refer to an arbitrary device.
        if (path[1] != ':' || (path[2] != '\\' && path[2] != '/') ||
            !((path[0] >= 'A' && path[0] <= 'Z') ||
              (path[0] >= 'a' && path[0] <= 'z')) ||
            path.StartsWith("\\\\", StringComparison.Ordinal))
            return false;
        return true;
    }

    internal static FileAclResult Observe(string path, int pageSize, int offset,
        CancellationToken cancellationToken)
    {
        var at = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        FileAclResult Result(FileAclCode code, int? native = null) =>
            new(code, path, at, NativeError: native, ElapsedMs: watch.ElapsedMilliseconds);
        if (!Valid(path, pageSize, offset)) return Result(FileAclCode.INVALID_ARGUMENT);
        if (!OperatingSystem.IsWindows()) return Result(FileAclCode.UNSUPPORTED_PLATFORM);
        cancellationToken.ThrowIfCancellationRequested();
        IntPtr descriptor = IntPtr.Zero, sddlPtr = IntPtr.Zero;
        try
        {
            uint error = GetNamedSecurityInfo(path, FileObject, RequestedInformation,
                out _, out _, out _, IntPtr.Zero, out descriptor);
            if (error != 0)
                return Result(error is ErrorFileNotFound or ErrorPathNotFound or
                    ErrorBadNetPath or ErrorBadNetName ? FileAclCode.NOT_FOUND :
                    error == ErrorAccessDenied ? FileAclCode.ACCESS_DENIED : FileAclCode.QUERY_FAILED,
                    unchecked((int)error));
            if (descriptor == IntPtr.Zero) return Result(FileAclCode.QUERY_FAILED);
            if (!GetSecurityDescriptorDacl(descriptor, out bool present,
                out IntPtr nativeDacl, out _))
                return Result(FileAclCode.QUERY_FAILED, Marshal.GetLastPInvokeError());
            if (!ConvertToSddl(descriptor, 1, RequestedInformation,
                out sddlPtr, out uint length))
                return Result(FileAclCode.QUERY_FAILED, Marshal.GetLastPInvokeError());
            if (sddlPtr == IntPtr.Zero || length > MaxSddlChars)
                return Result(FileAclCode.RESOURCE_LIMIT);
            string? sddl = Marshal.PtrToStringUni(sddlPtr, checked((int)length));
            if (sddl is null) return Result(FileAclCode.QUERY_FAILED);
            var raw = new RawSecurityDescriptor(sddl);
            FileDaclState daclState = !present ? FileDaclState.NOT_PRESENT :
                nativeDacl == IntPtr.Zero ? FileDaclState.NULL_DACL :
                raw.DiscretionaryAcl is null || raw.DiscretionaryAcl.Count == 0 ?
                    FileDaclState.EMPTY : FileDaclState.PRESENT;
            int count = daclState == FileDaclState.PRESENT ? raw.DiscretionaryAcl!.Count : 0;
            if (count > MaxAceCount) return Result(FileAclCode.RESOURCE_LIMIT);
            var aces = new List<FileAceInfo>(Math.Min(pageSize, count));
            for (int i = offset; i < count && aces.Count < pageSize; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GenericAce ace = raw.DiscretionaryAcl![i];
                KnownAce? known = ace as KnownAce;
                aces.Add(new(ace.AceType.ToString(), known?.SecurityIdentifier?.Value,
                    known is null ? null : unchecked((uint)known.AccessMask),
                    (ace.AceFlags & AceFlags.Inherited) != 0, ace.AceFlags.ToString()));
            }
            int? next = offset + aces.Count < count ? offset + aces.Count : null;
            return new(FileAclCode.COMPLETED, path, at, raw.Owner?.Value, raw.Group?.Value,
                daclState, (raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0,
                (raw.ControlFlags & ControlFlags.DiscretionaryAclAutoInherited) != 0,
                count, [.. aces], next, ElapsedMs: watch.ElapsedMilliseconds);
        }
        catch (ArgumentException) { return Result(FileAclCode.QUERY_FAILED); }
        catch (OverflowException) { return Result(FileAclCode.RESOURCE_LIMIT); }
        catch (OutOfMemoryException) { return Result(FileAclCode.RESOURCE_LIMIT); }
        finally
        {
            if (sddlPtr != IntPtr.Zero) LocalFree(sddlPtr);
            if (descriptor != IntPtr.Zero) LocalFree(descriptor);
        }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<WaitChainCode>))]
public enum WaitChainCode
{
    COMPLETED, INVALID_ARGUMENT, NOT_FOUND, ACCESS_DENIED, QUERY_FAILED,
    TIMEOUT, RESOURCE_LIMIT, UNSUPPORTED_PLATFORM
}

[JsonConverter(typeof(JsonStringEnumConverter<WaitThreadCode>))]
public enum WaitThreadCode { COMPLETED, ACCESS_DENIED, NOT_FOUND, QUERY_FAILED, RESOURCE_LIMIT }

public sealed record WaitChainNode(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("object_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ObjectName,
    [property: JsonPropertyName("process_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? ProcessId,
    [property: JsonPropertyName("thread_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? ThreadId,
    [property: JsonPropertyName("wait_time_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? WaitTimeMs,
    [property: JsonPropertyName("context_switches"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? ContextSwitches);

public sealed record WaitChainThread(
    [property: JsonPropertyName("thread_id")] int ThreadId,
    [property: JsonPropertyName("status")] WaitThreadCode Status,
    [property: JsonPropertyName("cycle"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Cycle = null,
    [property: JsonPropertyName("nodes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WaitChainNode[]? Nodes = null,
    [property: JsonPropertyName("native_error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NativeError = null);

public sealed record ProcessWaitChainResult(
    [property: JsonPropertyName("status")] WaitChainCode Status,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("observed_utc")] DateTimeOffset ObservedUtc,
    [property: JsonPropertyName("threads"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WaitChainThread[]? Threads = null,
    [property: JsonPropertyName("total_threads"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TotalThreads = null,
    [property: JsonPropertyName("next_offset"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NextOffset = null,
    [property: JsonPropertyName("native_error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NativeError = null,
    [property: JsonPropertyName("elapsed_ms")] long ElapsedMs = 0);

internal static class NativeProcessWaitChain
{
    private const int MaxThreadsPerPage = 8, MaxThreadCount = 8192;
    private const uint WctMaxNodeCount = 16;
    private const int WctNodeBytesX64 = 280;
    private const uint WctThreadType = 8;
    private const int ErrorAccessDenied = 5, ErrorInvalidParameter = 87,
        ErrorMoreData = 234, ErrorNotFound = 1168;
    // Native WCT has no cancellation API for synchronous calls. A single worker
    // globally bounds outstanding snapshots; callers stop waiting at 4 s, but
    // only the worker releases the permit after it has actually finished.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr OpenThreadWaitChainSession(uint flags, IntPtr callback);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadWaitChain(IntPtr wct, IntPtr context,
        uint flags, uint threadId, ref uint nodeCount, IntPtr nodeInfoArray,
        [MarshalAs(UnmanagedType.Bool)] out bool isCycle);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern void CloseThreadWaitChainSession(IntPtr wct);

    internal static bool Valid(int pid, int pageSize, int offset) =>
        pid > 0 && pageSize is >= 1 and <= MaxThreadsPerPage &&
        offset is >= 0 and <= MaxThreadCount;

    internal static async Task<ProcessWaitChainResult> ObserveAsync(int pid, int pageSize,
        int offset, CancellationToken cancellationToken)
    {
        DateTimeOffset at = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        ProcessWaitChainResult Result(WaitChainCode code) =>
            new(code, pid, at, ElapsedMs: watch.ElapsedMilliseconds);
        if (!Valid(pid, pageSize, offset)) return Result(WaitChainCode.INVALID_ARGUMENT);
        if (!OperatingSystem.IsWindows()) return Result(WaitChainCode.UNSUPPORTED_PLATFORM);
        if (!Environment.Is64BitProcess) return Result(WaitChainCode.UNSUPPORTED_PLATFORM);
        if (!await Gate.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken))
            return Result(WaitChainCode.RESOURCE_LIMIT);
        // The semaphore belongs to the native worker, not the request's timeout.
        Task<ProcessWaitChainResult> worker;
        try
        {
            worker = Task.Run(() =>
            {
                try { return ObserveNative(pid, pageSize, offset, at, watch); }
                finally { Gate.Release(); }
            });
        }
        catch
        {
            Gate.Release();
            throw;
        }
        try { return await worker.WaitAsync(TimeSpan.FromSeconds(4), cancellationToken); }
        catch (TimeoutException) { return Result(WaitChainCode.TIMEOUT); }
    }

    private static ProcessWaitChainResult ObserveNative(int pid, int pageSize,
        int offset, DateTimeOffset at, Stopwatch watch)
    {
        ProcessWaitChainResult Result(WaitChainCode code, int? native = null) =>
            new(code, pid, at, NativeError: native, ElapsedMs: watch.ElapsedMilliseconds);
        try
        {
            using var process = Process.GetProcessById(pid);
            var allThreads = process.Threads.Cast<ProcessThread>()
                .Select(t => t.Id).OrderBy(id => id).ToArray();
            if (allThreads.Length > MaxThreadCount) return Result(WaitChainCode.RESOURCE_LIMIT);
            int[] ids = allThreads.Skip(offset).Take(pageSize).ToArray();
            IntPtr session = OpenThreadWaitChainSession(0, IntPtr.Zero);
            if (session == IntPtr.Zero)
            {
                int native = Marshal.GetLastPInvokeError();
                return Result(native == ErrorAccessDenied ? WaitChainCode.ACCESS_DENIED :
                    WaitChainCode.QUERY_FAILED, native);
            }
            try
            {
                var threads = new List<WaitChainThread>(ids.Length);
                foreach (int threadId in ids)
                {
                    if (watch.Elapsed > TimeSpan.FromSeconds(3))
                        return new(WaitChainCode.TIMEOUT, pid, at, [.. threads],
                            allThreads.Length, ElapsedMs: watch.ElapsedMilliseconds);
                    threads.Add(ReadThread(session, threadId, pid));
                }
                int? next = offset + ids.Length < allThreads.Length ? offset + ids.Length : null;
                return new(WaitChainCode.COMPLETED, pid, at, [.. threads], allThreads.Length,
                    next, ElapsedMs: watch.ElapsedMilliseconds);
            }
            finally { CloseThreadWaitChainSession(session); }
        }
        catch (ArgumentException) { return Result(WaitChainCode.NOT_FOUND); }
        catch (InvalidOperationException) { return Result(WaitChainCode.NOT_FOUND); }
        catch (Win32Exception e)
        {
            return Result(e.NativeErrorCode == ErrorAccessDenied ? WaitChainCode.ACCESS_DENIED :
                e.NativeErrorCode == ErrorInvalidParameter || e.NativeErrorCode == ErrorNotFound ?
                    WaitChainCode.NOT_FOUND : WaitChainCode.QUERY_FAILED, e.NativeErrorCode);
        }
        catch (OutOfMemoryException) { return Result(WaitChainCode.RESOURCE_LIMIT); }
    }

    private static WaitChainThread ReadThread(IntPtr session, int threadId, int expectedPid)
    {
        IntPtr memory = IntPtr.Zero;
        try
        {
            memory = Marshal.AllocHGlobal(checked((int)WctMaxNodeCount * WctNodeBytesX64));
            // Zero all buffers because a successful call may return fewer nodes.
            Marshal.Copy(new byte[(int)WctMaxNodeCount * WctNodeBytesX64], 0,
                memory, (int)WctMaxNodeCount * WctNodeBytesX64);
            uint count = WctMaxNodeCount;
            bool ok = GetThreadWaitChain(session, IntPtr.Zero, 0, (uint)threadId,
                ref count, memory, out bool cycle);
            if (!ok)
            {
                int error = Marshal.GetLastPInvokeError();
                WaitThreadCode code = error == ErrorAccessDenied ? WaitThreadCode.ACCESS_DENIED :
                    error == ErrorNotFound || error == ErrorInvalidParameter ? WaitThreadCode.NOT_FOUND :
                    error == ErrorMoreData ? WaitThreadCode.RESOURCE_LIMIT : WaitThreadCode.QUERY_FAILED;
                return new(threadId, code, NativeError: error);
            }
            if (count > WctMaxNodeCount) return new(threadId, WaitThreadCode.RESOURCE_LIMIT);
            var nodes = new List<WaitChainNode>((int)count);
            for (int i = 0; i < count; i++)
            {
                IntPtr item = IntPtr.Add(memory, i * WctNodeBytesX64);
                uint type = unchecked((uint)Marshal.ReadInt32(item, 0));
                uint status = unchecked((uint)Marshal.ReadInt32(item, 4));
                bool isThread = type == WctThreadType;
                // Native WAITCHAIN_NODE_INFO starts with two DWORD enums and
                // an eight-byte-aligned union. Thread object has four DWORDs;
                // lock object starts with 128 WCHARs.
                uint? ownerPid = isThread ? unchecked((uint)Marshal.ReadInt32(item, 8)) : null;
                if (i == 0 && ownerPid is uint firstPid && firstPid != expectedPid)
                    return new(threadId, WaitThreadCode.NOT_FOUND); // PID/TID reuse race
                string? name = null;
                if (!isThread)
                {
                    name = Marshal.PtrToStringUni(IntPtr.Add(item, 8), 128)?.TrimEnd('\0');
                    if (name?.Length == 0) name = null;
                }
                nodes.Add(new(ObjectType(type), ObjectStatus(status), name, ownerPid,
                    isThread ? unchecked((uint)Marshal.ReadInt32(item, 12)) : null,
                    isThread ? unchecked((uint)Marshal.ReadInt32(item, 16)) : null,
                    isThread ? unchecked((uint)Marshal.ReadInt32(item, 20)) : null));
            }
            return new(threadId, WaitThreadCode.COMPLETED, cycle, [.. nodes]);
        }
        finally { if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory); }
    }

    private static string ObjectType(uint value) => value switch
    {
        1 => "CRITICAL_SECTION", 2 => "SEND_MESSAGE", 3 => "MUTEX",
        4 => "ALPC", 5 => "COM", 6 => "THREAD_WAIT", 7 => "PROCESS_WAIT",
        8 => "THREAD", 9 => "COM_ACTIVATION", 10 => "UNKNOWN",
        11 => "SOCKET_IO", 12 => "SMB_IO", _ => "UNRECOGNIZED"
    };

    private static string ObjectStatus(uint value) => value switch
    {
        1 => "NO_ACCESS", 2 => "RUNNING", 3 => "BLOCKED", 4 => "PID_ONLY",
        5 => "PID_ONLY_RPCSS", 6 => "OWNED", 7 => "NOT_OWNED",
        8 => "ABANDONED", 9 => "UNKNOWN", 10 => "ERROR", _ => "UNRECOGNIZED"
    };
}

[McpServerToolType]
public sealed class ExpertDiagnosticsTools
{
    [McpServerTool(Name = "windows.file_acl", UseStructuredContent = true),
     Description("Read local file/directory owner SID, group SID and explicit DACL/ACE diagnostics through the Windows security descriptor API. Bounded page of ACEs, reports NULL/empty/missing DACL distinctly; no SACL, privilege escalation or ACL modification. USER frontend token.")]
    public static Task<FileAclResult> FileAcl(string path, int page_size = 32,
        int offset = 0, CancellationToken cancellationToken = default) =>
        Task.FromResult(NativeFileAcl.Observe(path, page_size, offset, cancellationToken));

    [McpServerTool(Name = "windows.process_wait_chain", UseStructuredContent = true),
     Description("Read Win32 Wait Chain Traversal per PID, bounded 1..8 threads/page, 16 nodes/thread, 4 s caller timeout, one global native worker. Genuine access errors and per-thread races; does not enable SeDebugPrivilege or mutate processes. USER frontend token.")]
    public static Task<ProcessWaitChainResult> ProcessWaitChain(int pid,
        int page_size = 4, int offset = 0, CancellationToken cancellationToken = default) =>
        NativeProcessWaitChain.ObserveAsync(pid, page_size, offset, cancellationToken);
}
