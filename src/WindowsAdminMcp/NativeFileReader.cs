using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

// One bounded read-only native owner, invoked either by the elevated USER
// frontend or by the existing SYSTEM SCM pipe. No PowerShell/base64 subprocess.
internal static class NativeFileReader
{
    private const uint ReadAttributes = 0x80, GenericRead = 0x80000000;
    private const uint ShareRead = 1, ShareWrite = 2, ShareDelete = 4;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000,
        Overlapped = 0x40000000;
    private const uint ReparseAttribute = 0x400, DirectoryAttribute = 0x10;
    private static readonly BigInteger FingerprintMask = (BigInteger.One << 256) - 1;
    private static readonly StringComparer NameComparer = StringComparer.Ordinal;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleInfo
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh,
            WrittenLow, WrittenHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo { public uint Attributes, ReparseTag; }
    private sealed record CursorV1(int V, string PathHash, string Identity,
        string Fingerprint, string After, string Context);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleInfo info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out AttributeTagInfo info, uint size);

    internal static bool Valid(NativeFileRequest r) =>
        !string.IsNullOrWhiteSpace(r.Path) && r.Path.Length <= 32767 && !r.Path.Contains('\0') &&
        Path.IsPathFullyQualified(r.Path) &&
        r.ExecutionContext is (CommandContext.USER or CommandContext.SYSTEM) &&
        (r.Action switch
        {
            NativeFileAction.STAT => r.Cursor is null && r.Offset == 0 && r.Length == 65536 &&
                r.PageSize == 50 && r.ExpectedVersion is null,
            NativeFileAction.LIST => r.PageSize is >= 1 and <= 100 && !r.IncludeSha256 &&
                r.Offset == 0 && r.Length == 65536 && r.ExpectedVersion is null &&
                r.Cursor is (null or { Length: <= 1024 }),
            NativeFileAction.READ_CHUNK => r.Length is >= 1 and <= 262144 && r.Offset >= 0 &&
                r.Cursor is null && !r.IncludeSha256 && r.PageSize == 50 &&
                (r.ExpectedVersion is null or { Length: >= 1 and <= 128 }),
            _ => false
        });

    private static string FullPath(string path)
    {
        string full = Path.GetFullPath(path);
        // Refuse observed junctions/symlink parents. The final handle itself
        // is opened OPEN_REPARSE_POINT, so the final component never follows.
        for (string? parent = Path.GetDirectoryName(full); parent is not null;
            parent = Path.GetDirectoryName(parent))
        {
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new NativeFileException(NativeFileCode.REPARSE_POINT);
        }
        return full;
    }

    private static SafeFileHandle Open(string full, bool readData)
    {
        var handle = CreateFile(full, readData ? GenericRead : ReadAttributes,
            ShareRead | ShareWrite | ShareDelete,
            IntPtr.Zero, OpenExisting,
            OpenReparsePoint | BackupSemantics | (readData ? Overlapped : 0), IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return handle;
    }

    private static ByHandleInfo Observe(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (info.IndexHigh == 0 && info.IndexLow == 0)
            throw new NativeFileException(NativeFileCode.CAPABILITY_UNAVAILABLE);
        return info;
    }

    private static string Version(ByHandleInfo i) =>
        $"{i.Volume:x8}:{i.IndexHigh:x8}{i.IndexLow:x8}:{i.SizeHigh:x8}{i.SizeLow:x8}:{i.WrittenHigh:x8}{i.WrittenLow:x8}";
    // Compare the identity of the handle actually used for COPY with the
    // earlier STAT receipt. A pathname may be replaced between those steps.
    internal static string? VersionOf(SafeFileHandle handle)
    {
        try { return Version(Observe(handle)); }
        catch (NativeFileException) { return null; }
    }
    private static long Size(ByHandleInfo i) => (long)(((ulong)i.SizeHigh << 32) | i.SizeLow);
    private static DateTimeOffset When(uint low, uint high) =>
        DateTimeOffset.FromFileTime((long)(((ulong)high << 32) | low));
    private static NativeFileKind Kind(ByHandleInfo i) => (i.Attributes & ReparseAttribute) != 0
        ? NativeFileKind.REPARSE_POINT : (i.Attributes & DirectoryAttribute) != 0
            ? NativeFileKind.DIRECTORY : NativeFileKind.FILE;

    internal static async Task<NativeFileReply> ExecuteAsync(NativeFileRequest request,
        CancellationToken cancellationToken)
    {
        string currentStage = "RESOLVE";
        NativeFileReply Error(NativeFileCode code, int? native = null, string stage = "RESOLVE") =>
            new(code, request.ExecutionContext, DateTimeOffset.UtcNow, NativeError:native, Stage:stage);
        if (!Valid(request)) return Error(NativeFileCode.INVALID_ARGUMENT, stage: "VALIDATE");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string full = FullPath(request.Path);
            currentStage = request.Action.ToString();
            return request.Action switch
            {
                NativeFileAction.STAT => await Stat(full, request, cancellationToken),
                NativeFileAction.LIST => List(full, request, cancellationToken),
                NativeFileAction.READ_CHUNK => await ReadChunk(full, request, cancellationToken),
                _ => Error(NativeFileCode.INVALID_ARGUMENT, stage: "VALIDATE")
            };
        }
        catch (NativeFileException e) { return Error(e.Code); }
        catch (Win32Exception e) { return Error(MapNative(e.NativeErrorCode), e.NativeErrorCode); }
        catch (UnauthorizedAccessException) { return Error(NativeFileCode.ACCESS_DENIED); }
        catch (System.Security.SecurityException) { return Error(NativeFileCode.ACCESS_DENIED); }
        catch (FileNotFoundException) { return Error(NativeFileCode.NOT_FOUND); }
        catch (DirectoryNotFoundException) { return Error(NativeFileCode.NOT_FOUND); }
        catch (OperationCanceledException) { return Error(NativeFileCode.TIMEOUT, stage:"READ"); }
        catch (JsonException) { return Error(NativeFileCode.INVALID_CURSOR, stage:"VALIDATE"); }
        catch (FormatException) { return Error(NativeFileCode.INVALID_CURSOR, stage:"VALIDATE"); }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or PathTooLongException)
        { return Error(currentStage == "RESOLVE" && e is (ArgumentException or PathTooLongException)
            ? NativeFileCode.INVALID_ARGUMENT : NativeFileCode.IO_ERROR, stage:currentStage); }
    }

    private static NativeFileCode MapNative(int error) => error switch
    {
        2 or 3 => NativeFileCode.NOT_FOUND,
        5 or 1314 => NativeFileCode.ACCESS_DENIED,
        32 or 33 => NativeFileCode.SHARING_VIOLATION,
        1 or 50 => NativeFileCode.CAPABILITY_UNAVAILABLE,
        87 or 123 or 206 => NativeFileCode.INVALID_ARGUMENT,
        _ => NativeFileCode.IO_ERROR
    };

    private static async Task<NativeFileReply> Stat(string full, NativeFileRequest r,
        CancellationToken ct)
    {
        using var handle = Open(full, r.IncludeSha256);
        var original = Observe(handle);
        string? sha = null;
        if (r.IncludeSha256)
        {
            if (Kind(original) != NativeFileKind.FILE)
                return new(NativeFileCode.NOT_FILE, r.ExecutionContext, DateTimeOffset.UtcNow);
            using var stream = new FileStream(handle, FileAccess.Read, 81920, isAsync: true);
            sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            if (Version(original) != Version(Observe(stream.SafeFileHandle)))
                return new(NativeFileCode.CHANGED_SINCE_READ, r.ExecutionContext, DateTimeOffset.UtcNow);
        }
        string? owner = null;
        NativeFileOwnerCode ownerStatus = NativeFileOwnerCode.NOT_REQUESTED;
        // Never ask a high-level path accessor for the target's owner when
        // the observed final component is a link. The handle itself is safe.
        if (Kind(original) != NativeFileKind.REPARSE_POINT)
        {
            try
            {
                FileSystemSecurity security = Kind(original) == NativeFileKind.DIRECTORY
                    ? new DirectoryInfo(full).GetAccessControl() : new FileInfo(full).GetAccessControl();
                owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
                ownerStatus = owner is null ? NativeFileOwnerCode.UNAVAILABLE : NativeFileOwnerCode.OBSERVED;
            }
            catch (UnauthorizedAccessException) { ownerStatus = NativeFileOwnerCode.ACCESS_DENIED; }
            catch (System.Security.SecurityException) { ownerStatus = NativeFileOwnerCode.ACCESS_DENIED; }
            catch (IOException) { ownerStatus = NativeFileOwnerCode.UNAVAILABLE; }
        }
        uint? tag = null;
        if (Kind(original) == NativeFileKind.REPARSE_POINT &&
            GetFileInformationByHandleEx(handle, 9, out var reparse, 8)) tag = reparse.ReparseTag;
        var info = new NativeFileInfo(Kind(original), Size(original),
            When(original.CreatedLow, original.CreatedHigh),
            When(original.WrittenLow, original.WrittenHigh),
            ((FileAttributes)original.Attributes).ToString(), Version(original), tag,
            owner, ownerStatus, sha);
        return new(NativeFileCode.COMPLETED, r.ExecutionContext, DateTimeOffset.UtcNow, Info:info);
    }

    private static async Task<NativeFileReply> ReadChunk(string full, NativeFileRequest r,
        CancellationToken ct)
    {
        using var handle = Open(full, readData:true);
        var original = Observe(handle);
        if (Kind(original) != NativeFileKind.FILE)
            return new(Kind(original) == NativeFileKind.REPARSE_POINT ? NativeFileCode.REPARSE_POINT :
                NativeFileCode.NOT_FILE, r.ExecutionContext, DateTimeOffset.UtcNow);
        string version = Version(original);
        if (r.ExpectedVersion is not null && r.ExpectedVersion != version)
            return new(NativeFileCode.CHANGED_SINCE_READ, r.ExecutionContext, DateTimeOffset.UtcNow,
                Version:version, Stage:"VALIDATE");
        long size = Size(original);
        if (r.Offset > size)
            return new(NativeFileCode.INVALID_ARGUMENT, r.ExecutionContext, DateTimeOffset.UtcNow, Stage:"VALIDATE");
        byte[] data = new byte[(int)Math.Min(r.Length, size - r.Offset)];
        int read = await RandomAccess.ReadAsync(handle, data, r.Offset, ct);
        if (Version(original) != Version(Observe(handle)))
            return new(NativeFileCode.CHANGED_SINCE_READ, r.ExecutionContext, DateTimeOffset.UtcNow,
                Stage:"VERIFY");
        return new(NativeFileCode.COMPLETED, r.ExecutionContext, DateTimeOffset.UtcNow,
            Offset:r.Offset, ActualLength:read, Eof:r.Offset + read >= size,
            DataBase64:Convert.ToBase64String(data, 0, read),
            ChunkSha256:Convert.ToHexString(SHA256.HashData(data.AsSpan(0, read))),
            Version:version);
    }

    private static NativeFileReply List(string full, NativeFileRequest r, CancellationToken ct)
    {
        using var handle = Open(full, readData:false);
        var original = Observe(handle);
        if (Kind(original) != NativeFileKind.DIRECTORY)
            return new(Kind(original) == NativeFileKind.REPARSE_POINT ? NativeFileCode.REPARSE_POINT :
                NativeFileCode.NOT_DIRECTORY, r.ExecutionContext, DateTimeOffset.UtcNow);
        string identity = Version(original);
        string pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)));
        CursorV1? previous = null;
        if (r.Cursor is { } cursor)
        {
            try { previous = JsonSerializer.Deserialize<CursorV1>(Convert.FromBase64String(cursor), PipeWire.Json); }
            catch (Exception e) when (e is FormatException or JsonException)
            { return new(NativeFileCode.INVALID_CURSOR, r.ExecutionContext, DateTimeOffset.UtcNow); }
            if (previous is null || previous.V != 1 || previous.PathHash != pathHash ||
                previous.Context != r.ExecutionContext.ToString() ||
                string.IsNullOrEmpty(previous.After) || previous.After.Length > 255 ||
                Path.GetFileName(previous.After) != previous.After ||
                string.IsNullOrWhiteSpace(previous.Identity) ||
                string.IsNullOrWhiteSpace(previous.Fingerprint))
                return new(NativeFileCode.INVALID_CURSOR, r.ExecutionContext, DateTimeOffset.UtcNow);
            if (previous.Identity != identity)
                return new(NativeFileCode.CHANGED_SINCE_LIST, r.ExecutionContext, DateTimeOffset.UtcNow);
        }
        BigInteger fingerprint = BigInteger.Zero;
        int count = 0;
        int eligibleCount = 0;
        var top = new SortedSet<string>(NameComparer);
        foreach (string entry in Directory.EnumerateFileSystemEntries(full))
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(entry);
            var attributes = File.GetAttributes(entry);
            long written = File.GetLastWriteTimeUtc(entry).ToFileTimeUtc();
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{name}\0{(int)attributes}\0{written}"));
            fingerprint = (fingerprint + new BigInteger(digest, isUnsigned:true, isBigEndian:true)) & FingerprintMask;
            count++;
            if (previous is not null && NameComparer.Compare(name, previous.After) <= 0) continue;
            eligibleCount++;
            top.Add(name);
            if (top.Count > r.PageSize) top.Remove(top.Max!);
        }
        string signature = $"{count:x8}:{fingerprint:x64}";
        if (Version(original) != Version(Observe(handle)) ||
            previous is not null && previous.Fingerprint != signature)
            return new(NativeFileCode.CHANGED_SINCE_LIST, r.ExecutionContext, DateTimeOffset.UtcNow);
        var entries = new List<NativeFileEntry>(top.Count);
        foreach (string name in top)
        {
            string path = Path.Combine(full, name);
            var attributes = File.GetAttributes(path);
            var kind = (attributes & FileAttributes.ReparsePoint) != 0 ? NativeFileKind.REPARSE_POINT :
                (attributes & FileAttributes.Directory) != 0 ? NativeFileKind.DIRECTORY : NativeFileKind.FILE;
            long? size = kind == NativeFileKind.FILE ? new FileInfo(path).Length : null;
            entries.Add(new(name, kind, size, attributes.ToString(), File.GetLastWriteTimeUtc(path)));
        }
        if (Version(original) != Version(Observe(handle)))
            return new(NativeFileCode.CHANGED_SINCE_LIST, r.ExecutionContext, DateTimeOffset.UtcNow);
        bool more = eligibleCount > entries.Count;
        string? next = null;
        if (more)
            next = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new CursorV1(1, pathHash, identity, signature, entries[^1].Name,
                    r.ExecutionContext.ToString()), PipeWire.Json)));
        return new(NativeFileCode.COMPLETED, r.ExecutionContext, DateTimeOffset.UtcNow,
            Entries:[.. entries], NextCursor:next, DirectoryVersion:identity);
    }

    private sealed class NativeFileException(NativeFileCode code) : Exception
    { internal NativeFileCode Code { get; } = code; }
}
