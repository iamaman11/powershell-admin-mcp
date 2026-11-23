using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

// Native Win32/.NET file operations under the actual USER or SYSTEM token.
// No shell, arbitrary path whitelist, service impersonation, or new database.
internal static class NativeFileManager
{
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true, EntryPoint="MoveFileExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source,string destination,uint flags);

    private static bool Digest(string? value)=>value is {Length:64} && value.All(Uri.IsHexDigit);

    internal static bool Valid(FileManageRequest r)
    {
        if(!Enum.IsDefined(r.Action) || r.ExecutionContext is not (CommandContext.USER or CommandContext.SYSTEM) ||
            !Guid.TryParseExact(r.OperationId,"N",out _) ||
            r.Path is not {Length:>=1 and <=32767} || r.Path.Contains('\0') ||
            !Path.IsPathFullyQualified(r.Path) ||
            ((r.Action is FileManageAction.COPY or FileManageAction.MOVE) !=
                (r.Destination is {Length:>=1 and <=32767} &&
                 !r.Destination.Contains('\0') && Path.IsPathFullyQualified(r.Destination))) ||
            r.Action is (FileManageAction.MKDIR or FileManageAction.DELETE) && r.Destination is not null ||
            (r.ExpectedVersion is null)!=(r.ExpectedSha256 is null) ||
            r.ExpectedVersion is not null && (r.ExpectedVersion.Length is <1 or >128 || !Digest(r.ExpectedSha256)) ||
            r.Action==FileManageAction.MKDIR && (r.Recursive || r.ExpectedVersion is not null) ||
            r.Action==FileManageAction.MOVE && r.Recursive)
            return false;
        try
        {
            string source=Path.GetFullPath(r.Path);
            string? destination=r.Destination is null?null:Path.GetFullPath(r.Destination);
            if(destination is not null &&
                (source.Equals(destination,StringComparison.OrdinalIgnoreCase) ||
                 destination.StartsWith(Path.TrimEndingDirectorySeparator(source)+Path.DirectorySeparatorChar,
                     StringComparison.OrdinalIgnoreCase)))return false;
            return true;
        }
        catch(Exception e) when(e is ArgumentException or NotSupportedException or IOException)
        {return false;}
    }

    // The receipt hash covers every effect-shaping input; same operation_id
    // with altered action/flags/CAS/destination is always a CONFLICT.
    internal static string SpecificationHash(FileManageRequest r)
    {
        string source=Path.GetFullPath(r.Path);
        string destination=r.Destination is null?"":Path.GetFullPath(r.Destination);
        string payload=string.Join('\0',"file-manage-v1",r.Action.ToString(),source,destination,
            r.Recursive?"1":"0",r.ExpectedVersion??"",r.ExpectedSha256?.ToUpperInvariant()??"");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    internal static FileManageReply Execute(FileManageRequest r,CancellationToken ct=default)
    {
        var clock=Stopwatch.StartNew();
        bool effectPossible=false;
        FileManageReply Result(FileManageCode code,string stage,int? native=null)=>
            new(code,r.OperationId,r.Action,r.ExecutionContext,
                code==FileManageCode.COMPLETED?WindowEffectState.EFFECT_VERIFIED:
                code==FileManageCode.EFFECT_UNKNOWN?WindowEffectState.EFFECT_UNKNOWN:
                WindowEffectState.NO_EFFECT,stage,DateTimeOffset.UtcNow,clock.ElapsedMilliseconds,native);
        if(!Valid(r) || r.ReadOnly)return Result(FileManageCode.INVALID_ARGUMENT,"VALIDATE");
        try
        {
            ct.ThrowIfCancellationRequested();
            string source=Path.GetFullPath(r.Path);
            string? dest=r.Destination is null?null:Path.GetFullPath(r.Destination);
            CheckReparse(source);
            if(dest is not null)CheckReparse(dest);
            bool file=File.Exists(source),directory=Directory.Exists(source);
            if(r.Action==FileManageAction.MKDIR)
            {
                if(file || directory)return Result(FileManageCode.ALREADY_EXISTS,"PREFLIGHT");
                effectPossible=true;Directory.CreateDirectory(source);
                return Directory.Exists(source)?Result(FileManageCode.COMPLETED,"VERIFY"):
                    Result(FileManageCode.EFFECT_UNKNOWN,"VERIFY");
            }
            if(!file && !directory)return Result(FileManageCode.NOT_FOUND,"PREFLIGHT");
            if(r.ExpectedVersion is not null)
            {
                if(directory)return Result(FileManageCode.INVALID_ARGUMENT,"PREFLIGHT");
                var stat=NativeFileReader.ExecuteAsync(new(NativeFileAction.STAT,source,
                    r.ExecutionContext,IncludeSha256:true),ct).GetAwaiter().GetResult();
                if(stat.Status!=NativeFileCode.COMPLETED || stat.Info?.Version!=r.ExpectedVersion ||
                    !string.Equals(stat.Info.Sha256,r.ExpectedSha256,StringComparison.OrdinalIgnoreCase))
                    return Result(FileManageCode.SOURCE_CHANGED,"PREFLIGHT");
            }
            if(r.Action==FileManageAction.DELETE)
            {
                if(directory && !r.Recursive && Directory.EnumerateFileSystemEntries(source).Any())
                    return Result(FileManageCode.NOT_EMPTY,"PREFLIGHT");
                if(directory && r.Recursive)CheckTree(source,ct);
                ct.ThrowIfCancellationRequested();
                effectPossible=true;
                if(directory)Directory.Delete(source,r.Recursive);
                else File.Delete(source);
                return !File.Exists(source) && !Directory.Exists(source)?
                    Result(FileManageCode.COMPLETED,"VERIFY"):Result(FileManageCode.EFFECT_UNKNOWN,"VERIFY");
            }
            if(dest is null)return Result(FileManageCode.INVALID_ARGUMENT,"VALIDATE");
            if(File.Exists(dest) || Directory.Exists(dest))
                return Result(FileManageCode.DESTINATION_EXISTS,"PREFLIGHT");
            string parent=Path.GetDirectoryName(dest)!;
            if(!Directory.Exists(parent))return Result(FileManageCode.NOT_FOUND,"PREFLIGHT");
            if(r.Action==FileManageAction.MOVE)
            {
                // MoveFileExW WITHOUT MOVEFILE_COPY_ALLOWED: an atomic rename on
                // one volume or explicit ERROR_NOT_SAME_DEVICE, never copy+delete.
                ct.ThrowIfCancellationRequested();
                effectPossible=true;
                if(!MoveFileEx(source,dest,0))throw new Win32Exception(Marshal.GetLastPInvokeError());
                return (file?File.Exists(dest):Directory.Exists(dest)) &&
                    !File.Exists(source) && !Directory.Exists(source)?
                    Result(FileManageCode.COMPLETED,"VERIFY"):Result(FileManageCode.EFFECT_UNKNOWN,"VERIFY");
            }
            if(directory && !r.Recursive)return Result(FileManageCode.INVALID_ARGUMENT,"PREFLIGHT");
            if(directory)CheckTree(source,ct);
            // Stage on destination volume; only the final rename is visible.
            string stage=Path.Combine(parent,".wmcp-copy-"+r.OperationId+".stage");
            if(File.Exists(stage) || Directory.Exists(stage))
                return Result(FileManageCode.EFFECT_UNKNOWN,"STAGE_EXISTS");
            bool stageCreated=false;
            try
            {
                ct.ThrowIfCancellationRequested();
                if(directory)
                {stageCreated=true;CopyTree(source,stage,ct);}
                else
                {
                    using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);
                    // The preflight STAT and this open are separate operations.
                    // Compare the *opened handle*, not the path again, before
                    // allowing the destination to become visible.
                    if(r.ExpectedVersion is not null &&
                        NativeFileReader.VersionOf(input.SafeFileHandle)!=r.ExpectedVersion)
                        return Result(FileManageCode.SOURCE_CHANGED,"SOURCE_HANDLE");
                    using var output=new FileStream(stage,FileMode.CreateNew,FileAccess.Write,FileShare.None,
                        65536,FileOptions.WriteThrough);
                    stageCreated=true;
                    string? copiedSha=CopyFileChecked(input,output,ct,r.ExpectedSha256 is not null);
                    if(r.ExpectedSha256 is not null &&
                        !string.Equals(copiedSha,r.ExpectedSha256,StringComparison.OrdinalIgnoreCase))
                        return Result(FileManageCode.SOURCE_CHANGED,"COPIED_HASH");
                    if(r.ExpectedVersion is not null &&
                        NativeFileReader.VersionOf(input.SafeFileHandle)!=r.ExpectedVersion)
                        return Result(FileManageCode.SOURCE_CHANGED,"SOURCE_HANDLE");
                    output.Flush(flushToDisk:true);
                }
                stageCreated=true;
                ct.ThrowIfCancellationRequested();
                if(File.Exists(dest) || Directory.Exists(dest))
                    return Result(FileManageCode.DESTINATION_EXISTS,"VERIFY");
                effectPossible=true;
                if(directory)Directory.Move(stage,dest);
                else File.Move(stage,dest);
                stageCreated=false;
                return (directory?Directory.Exists(dest):File.Exists(dest))?
                    Result(FileManageCode.COMPLETED,"VERIFY"):Result(FileManageCode.EFFECT_UNKNOWN,"VERIFY");
            }
            finally
            {
                // An interrupted copy may leave a hidden stage. Never delete
                // a preexisting stage, and never hide a cleanup uncertainty.
                if(stageCreated)
                {
                    try
                    {
                        if(directory && Directory.Exists(stage))Directory.Delete(stage,true);
                        else if(!directory && File.Exists(stage))File.Delete(stage);
                    }
                    catch(IOException){effectPossible=true;}
                    catch(UnauthorizedAccessException){effectPossible=true;}
                }
            }
        }
        catch(FileManageReparseException){return Result(FileManageCode.REPARSE_POINT,"VALIDATE");}
        catch(OperationCanceledException){return Result(FileManageCode.EFFECT_UNKNOWN,"TIMEOUT");}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or Win32Exception or
            ArgumentException or NotSupportedException)
        {
            int? native=e is Win32Exception w?w.NativeErrorCode:e is IOException io?io.HResult&0xFFFF:null;
            if(effectPossible && native is not (17 or 80 or 183))
                return Result(FileManageCode.EFFECT_UNKNOWN,"EFFECT_READBACK",native);
            FileManageCode code=e switch
            {
                UnauthorizedAccessException=>FileManageCode.ACCESS_DENIED,
                FileNotFoundException or DirectoryNotFoundException=>FileManageCode.NOT_FOUND,
                ArgumentException or NotSupportedException=>FileManageCode.INVALID_ARGUMENT,
                _=>native switch
                {
                    5=>FileManageCode.ACCESS_DENIED,17=>FileManageCode.CROSS_VOLUME,
                    32 or 33=>FileManageCode.SHARING_VIOLATION,
                    80 or 183=>FileManageCode.DESTINATION_EXISTS,
                    145=>FileManageCode.NOT_EMPTY,
                    _=>FileManageCode.IO_ERROR
                }
            };
            return Result(code,"NATIVE",native);
        }
    }

    private static void CheckReparse(string full)
    {
        // Iterate each existing component, including the target itself. No
        // unchecked traversal through junctions, mount points or symlinks.
        for(string? path=full;path is not null;path=Path.GetDirectoryName(path))
        {
            try
            {
                if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
                    throw new FileManageReparseException();
            }
            catch(FileNotFoundException){}
            catch(DirectoryNotFoundException){}
        }
    }

    private static void CheckTree(string root,CancellationToken ct)
    {
        foreach(string entry in Directory.EnumerateFileSystemEntries(root))
        {
            ct.ThrowIfCancellationRequested();
            var attributes=File.GetAttributes(entry);
            if((attributes&FileAttributes.ReparsePoint)!=0)throw new FileManageReparseException();
            if((attributes&FileAttributes.Directory)!=0)CheckTree(entry,ct);
        }
    }

    private static void CopyTree(string source,string destination,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        foreach(string entry in Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested();
            var attrs=File.GetAttributes(entry);
            if((attrs&FileAttributes.ReparsePoint)!=0)throw new FileManageReparseException();
            string target=Path.Combine(destination,Path.GetFileName(entry));
            if((attrs&FileAttributes.Directory)!=0)CopyTree(entry,target,ct);
            else
            {
                using var input=new FileStream(entry,FileMode.Open,FileAccess.Read,FileShare.Read);
                using var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,
                    FileShare.None,65536,FileOptions.WriteThrough);
                CopyFileChecked(input,output,ct,hash:false);output.Flush(flushToDisk:true);
            }
        }
    }

    // The outer file workflow is synchronous on a bounded background worker.
    // Stream.CopyTo ignores its CancellationToken; check it at each 64-KiB
    // boundary so an interrupted multi-GiB copy does not continue unbounded.
    // The optional digest proves that the bytes actually staged match CAS.
    internal static string? CopyFileChecked(Stream input,Stream output,CancellationToken ct,bool hash)
    {
        using IncrementalHash? digest=hash?IncrementalHash.CreateHash(HashAlgorithmName.SHA256):null;
        byte[] buffer=ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while(true)
            {
                ct.ThrowIfCancellationRequested();
                int count=input.Read(buffer,0,65536);
                ct.ThrowIfCancellationRequested();
                if(count==0)break;
                digest?.AppendData(buffer,0,count);
                output.Write(buffer,0,count);
            }
            ct.ThrowIfCancellationRequested();
            return digest is null?null:Convert.ToHexString(digest.GetHashAndReset());
        }
        finally {ArrayPool<byte>.Shared.Return(buffer,clearArray:true);}
    }

    private sealed class FileManageReparseException:Exception { }
}
