using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Per-target, same-volume staged replacement; no shell, second daemon, second
// SQLite connection or global maintenance lock. Journal receipts are bounded
// and survive a disposable MCP frontend exiting between dispatch and ACK.
// The actual USER or SYSTEM Windows token owns every filesystem operation.
internal static class NativeFileWriter
{
    internal const long MaxUploadBytes=64L*1024*1024;
    internal const int MaxChunkBytes=262144,MaxReceiptsPerDirectory=1024;

    private sealed record Journal(int V,string OperationId,string Path,CommandContext Context,
        long Length,string Sha256,FileOverwritePolicy Policy,string? OriginalVersion,
        string? OriginalSha256,long Received,string State,long LastOffset=-1,string? LastChunkHash=null);

    private static bool Digest(string? value)=>value is {Length:64} && value.All(Uri.IsHexDigit);
    internal static bool Valid(FileWriteRequest r)=>
        r.Path is {Length:>=1 and <=32767} && !r.Path.Contains('\0') && Path.IsPathFullyQualified(r.Path) &&
        Guid.TryParseExact(r.OperationId,"N",out _) &&
        r.ExecutionContext is CommandContext.SYSTEM or CommandContext.USER && Enum.IsDefined(r.Action) &&
        (r.Action switch
        {
            FileWriteAction.BEGIN=>r.ExpectedBytes is >=0 and <=MaxUploadBytes && Digest(r.ExpectedSha256) &&
                Enum.IsDefined(r.OverwritePolicy) && r.Offset is null && r.DataBase64 is null &&
                r.ChunkSha256 is null &&
                (r.OverwritePolicy==FileOverwritePolicy.FAIL_IF_EXISTS ?
                    r.ExpectedVersion is null && r.OriginalSha256 is null :
                    r.ExpectedVersion is {Length:>=1 and <=128} && Digest(r.OriginalSha256)),
            FileWriteAction.APPEND=>r.ExpectedBytes is null && r.ExpectedSha256 is null &&
                r.ExpectedVersion is null && r.OriginalSha256 is null &&
                r.OverwritePolicy==FileOverwritePolicy.FAIL_IF_EXISTS && r.Offset is >=0 and <=MaxUploadBytes &&
                r.DataBase64 is {Length:>=4 and <=349528} && Digest(r.ChunkSha256),
            _=>r.ExpectedBytes is null && r.ExpectedSha256 is null && r.ExpectedVersion is null &&
                r.OriginalSha256 is null && r.Offset is null && r.DataBase64 is null &&
                r.ChunkSha256 is null && r.OverwritePolicy==FileOverwritePolicy.FAIL_IF_EXISTS
        });

    private static string FullPath(string path)
    {
        var full=Path.GetFullPath(path);
        for(string? dir=Path.GetDirectoryName(full);dir is not null;dir=Path.GetDirectoryName(dir))
            if((File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0)
                throw new FileWriteFailure(FileWriteCode.REPARSE_POINT);
        if(File.Exists(full) && (File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
            throw new FileWriteFailure(FileWriteCode.REPARSE_POINT);
        return full;
    }

    private static string HashFile(string path)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static (string Stage,string Journal) Paths(string full,string id)
    {
        string fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full+"\0"+id)))[..32];
        string prefix=Path.Combine(Path.GetDirectoryName(full)!,".wmcp-"+fingerprint);
        return (prefix+".stage",prefix+".journal");
    }

    private static Journal Read(string path)
    {
        var file=new FileInfo(path);
        if(!file.Exists)throw new FileWriteFailure(FileWriteCode.NOT_FOUND);
        if(file.Length is <1 or >8192 || (file.Attributes&FileAttributes.ReparsePoint)!=0)
            throw new FileWriteFailure(FileWriteCode.EFFECT_UNKNOWN);
        var state=JsonSerializer.Deserialize<Journal>(File.ReadAllText(path),PipeWire.Json);
        if(state is null || state.V!=1 || !Digest(state.Sha256) || state.Length is <0 or >MaxUploadBytes ||
            state.Received<0 || state.Received>state.Length ||
            state.State is not ("STAGED" or "COMMITTING" or "COMMITTED" or "ABORTED"))
            throw new FileWriteFailure(FileWriteCode.EFFECT_UNKNOWN);
        return state;
    }

    private static void Save(string path,Journal state)
    {
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(state,PipeWire.Json);
            using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {
                output.Write(bytes);output.Flush(flushToDisk:true);
            }
            File.Move(temp,path,overwrite:true);
        }
        finally {if(File.Exists(temp))File.Delete(temp);}
    }

    internal static FileWriteReply Execute(FileWriteRequest request,CancellationToken ct=default)
    {
        bool targetMayHaveChanged=false;
        bool sidecarMayHaveChanged=false;
        void Persist(string path,Journal value)
        {sidecarMayHaveChanged=true;Save(path,value);}
        FileWriteReply Reply(FileWriteCode status,Journal? state=null,string? stage=null,int? native=null)=>
            new(status,request.OperationId,request.ExecutionContext,state?.Received,state?.Length,
                state?.Sha256,status==FileWriteCode.COMMITTED?WindowEffectState.EFFECT_VERIFIED:
                status==FileWriteCode.EFFECT_UNKNOWN?WindowEffectState.EFFECT_UNKNOWN:WindowEffectState.NO_EFFECT,
                stage,DateTimeOffset.UtcNow,native);
        if(!Valid(request))return Reply(FileWriteCode.INVALID_ARGUMENT,stage:"VALIDATE");
        try
        {
            ct.ThrowIfCancellationRequested();
            string full=FullPath(request.Path);
            var paths=Paths(full,request.OperationId);
            string mutexId=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..32];
            using var mutex=new Mutex(false,"Global\\WindowsAdminMCP.file."+mutexId);
            bool entered;
            try {entered=mutex.WaitOne(TimeSpan.FromSeconds(10));}
            catch(AbandonedMutexException){entered=true;}
            if(!entered)return Reply(FileWriteCode.TIMEOUT,stage:"LOCK");
            try
            {
                ct.ThrowIfCancellationRequested();
                if(request.Action==FileWriteAction.BEGIN)
                {
                    if(File.Exists(paths.Journal))
                    {
                        var existing=Read(paths.Journal);
                        if(existing.Path!=full || existing.OperationId!=request.OperationId ||
                            existing.Context!=request.ExecutionContext || existing.Length!=request.ExpectedBytes ||
                            !string.Equals(existing.Sha256,request.ExpectedSha256,StringComparison.OrdinalIgnoreCase) ||
                            existing.Policy!=request.OverwritePolicy || existing.OriginalVersion!=request.ExpectedVersion ||
                            !string.Equals(existing.OriginalSha256,request.OriginalSha256,StringComparison.OrdinalIgnoreCase))
                            return Reply(FileWriteCode.CONFLICT,existing,stage:"VALIDATE");
                        return Reply(existing.State=="COMMITTED"?FileWriteCode.COMMITTED:
                            existing.State=="ABORTED"?FileWriteCode.ABORTED:
                            existing.State=="COMMITTING"?FileWriteCode.EFFECT_UNKNOWN:
                            File.Exists(paths.Stage)?FileWriteCode.STAGED:FileWriteCode.EFFECT_UNKNOWN,existing);
                    }
                    string folder=Path.GetDirectoryName(full)!;
                    if(Directory.EnumerateFiles(folder,".wmcp-*.journal").Take(MaxReceiptsPerDirectory+1).Count()>=MaxReceiptsPerDirectory)
                        return Reply(FileWriteCode.STORE_FULL,stage:"VALIDATE");
                    bool exists=File.Exists(full);
                    if(request.OverwritePolicy==FileOverwritePolicy.FAIL_IF_EXISTS && exists)
                        return Reply(FileWriteCode.CONFLICT,stage:"VALIDATE");
                    if(request.OverwritePolicy==FileOverwritePolicy.REPLACE_IF_MATCH)
                    {
                        if(!exists)return Reply(FileWriteCode.NOT_FOUND,stage:"VALIDATE");
                        var stat=NativeFileReader.ExecuteAsync(new(NativeFileAction.STAT,full,
                            request.ExecutionContext,IncludeSha256:true),ct).GetAwaiter().GetResult();
                        if(stat.Status!=NativeFileCode.COMPLETED || stat.Info?.Version!=request.ExpectedVersion ||
                            !string.Equals(stat.Info?.Sha256,request.OriginalSha256,StringComparison.OrdinalIgnoreCase))
                            return Reply(FileWriteCode.CONFLICT,stage:"VALIDATE");
                    }
                    var journal=new Journal(1,request.OperationId,full,request.ExecutionContext,
                        request.ExpectedBytes!.Value,request.ExpectedSha256!.ToUpperInvariant(),
                        request.OverwritePolicy,request.ExpectedVersion,request.OriginalSha256?.ToUpperInvariant(),0,"STAGED");
                    Persist(paths.Journal,journal);
                    using(var stage=new FileStream(paths.Stage,FileMode.CreateNew,FileAccess.Write,FileShare.None))stage.Flush(true);
                    return Reply(FileWriteCode.STAGED,journal);
                }
                var saved=Read(paths.Journal);
                if(saved.OperationId!=request.OperationId || saved.Path!=full || saved.Context!=request.ExecutionContext)
                    return Reply(FileWriteCode.CONFLICT,saved,stage:"VALIDATE");
                if(File.Exists(paths.Stage) &&
                    (File.GetAttributes(paths.Stage)&FileAttributes.ReparsePoint)!=0)
                    return Reply(FileWriteCode.REPARSE_POINT,saved,stage:"VALIDATE");
                if(request.Action==FileWriteAction.STATUS || saved.State=="COMMITTED" || saved.State=="ABORTED")
                {
                    // Never infer an operation committed merely because an unrelated
                    // target happens to contain the requested bytes. A durable
                    // COMMITTING intent must precede the atomic filesystem change.
                    if(saved.State=="COMMITTING" && !File.Exists(paths.Stage) && File.Exists(full) &&
                        string.Equals(HashFile(full),saved.Sha256,StringComparison.OrdinalIgnoreCase))
                    {
                        saved=saved with {State="COMMITTED",Received=saved.Length};Persist(paths.Journal,saved);
                    }
                    // Atomic Move/Replace consumes the staged file on success.
                    // If it is still present and the original is demonstrably
                    // intact, no replacement occurred; the caller can safely
                    // prepare a *new* explicit commit after this STATUS readback.
                    else if(saved.State=="COMMITTING" && File.Exists(paths.Stage) &&
                        new FileInfo(paths.Stage).Length==saved.Length &&
                        string.Equals(HashFile(paths.Stage),saved.Sha256,StringComparison.OrdinalIgnoreCase))
                    {
                        bool unchanged=saved.Policy==FileOverwritePolicy.FAIL_IF_EXISTS
                            ?!File.Exists(full):false;
                        if(saved.Policy==FileOverwritePolicy.REPLACE_IF_MATCH && File.Exists(full))
                        {
                            var stat=NativeFileReader.ExecuteAsync(new(NativeFileAction.STAT,full,
                                saved.Context,IncludeSha256:true),ct).GetAwaiter().GetResult();
                            unchanged=stat.Status==NativeFileCode.COMPLETED &&
                                stat.Info?.Version==saved.OriginalVersion &&
                                string.Equals(stat.Info?.Sha256,saved.OriginalSha256,
                                    StringComparison.OrdinalIgnoreCase);
                        }
                        if(unchanged)
                        {saved=saved with {State="STAGED"};Persist(paths.Journal,saved);}
                    }
                    if(saved.State=="COMMITTING" || saved.State=="STAGED" && !File.Exists(paths.Stage))
                        return Reply(FileWriteCode.EFFECT_UNKNOWN,saved,stage:"READBACK");
                    return Reply(saved.State=="COMMITTED"?FileWriteCode.COMMITTED:
                        saved.State=="ABORTED"?FileWriteCode.ABORTED:FileWriteCode.STAGED,saved);
                }
                if(request.Action==FileWriteAction.ABORT)
                {
                    if(saved.State=="COMMITTING")
                        return Reply(FileWriteCode.EFFECT_UNKNOWN,saved,stage:"VERIFY");
                    if(File.Exists(paths.Stage))
                    {sidecarMayHaveChanged=true;File.Delete(paths.Stage);}
                    saved=saved with {State="ABORTED"};Persist(paths.Journal,saved);
                    return Reply(FileWriteCode.ABORTED,saved);
                }
                if(!File.Exists(paths.Stage))return Reply(FileWriteCode.EFFECT_UNKNOWN,saved,stage:"READBACK");
                if((File.GetAttributes(paths.Stage)&FileAttributes.ReparsePoint)!=0)
                    return Reply(FileWriteCode.REPARSE_POINT,saved,stage:"VALIDATE");
                if(request.Action==FileWriteAction.APPEND)
                {
                    if(saved.State=="COMMITTING")return Reply(FileWriteCode.EFFECT_UNKNOWN,saved,stage:"VERIFY");
                    byte[] bytes;
                    try {bytes=Convert.FromBase64String(request.DataBase64!);}
                    catch(FormatException){return Reply(FileWriteCode.INVALID_ARGUMENT,saved,stage:"VALIDATE");}
                    if(bytes.Length is <1 or >MaxChunkBytes ||
                        !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)),request.ChunkSha256,StringComparison.OrdinalIgnoreCase) ||
                        request.Offset!.Value>saved.Length-bytes.Length)
                        return Reply(FileWriteCode.INVALID_ARGUMENT,saved,stage:"VALIDATE");
                    using var stage=new FileStream(paths.Stage,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
                    if(request.Offset==saved.LastOffset &&
                        string.Equals(request.ChunkSha256,saved.LastChunkHash,StringComparison.OrdinalIgnoreCase) &&
                        stage.Length==saved.Received)return Reply(FileWriteCode.STAGED,saved);
                    if(request.Offset!=saved.Received)return Reply(FileWriteCode.CONFLICT,saved,stage:"VALIDATE");
                    if(stage.Length==saved.Received+bytes.Length)
                    {
                        stage.Position=saved.Received;
                        byte[] observed=new byte[bytes.Length];stage.ReadExactly(observed);
                        if(!CryptographicOperations.FixedTimeEquals(observed,bytes))
                            return Reply(FileWriteCode.EFFECT_UNKNOWN,saved,stage:"READBACK");
                    }
                    else if(stage.Length==saved.Received)
                    {
                        sidecarMayHaveChanged=true;
                        stage.Position=saved.Received;stage.Write(bytes);stage.Flush(true);
                    }
                    else return Reply(FileWriteCode.EFFECT_UNKNOWN,saved,stage:"READBACK");
                    saved=saved with {Received=saved.Received+bytes.Length,LastOffset=request.Offset.Value,
                        LastChunkHash=request.ChunkSha256!.ToUpperInvariant()};
                    Persist(paths.Journal,saved);
                    return Reply(FileWriteCode.STAGED,saved);
                }
                if(request.Action==FileWriteAction.COMMIT)
                {
                    if(saved.Received!=saved.Length || new FileInfo(paths.Stage).Length!=saved.Length)
                        return Reply(FileWriteCode.CONFLICT,saved,stage:"VERIFY");
                    if(!string.Equals(HashFile(paths.Stage),saved.Sha256,StringComparison.OrdinalIgnoreCase))
                        return Reply(FileWriteCode.HASH_MISMATCH,saved,stage:"VERIFY");
                    if(saved.Policy==FileOverwritePolicy.FAIL_IF_EXISTS)
                    {
                        if(File.Exists(full))return Reply(FileWriteCode.CONFLICT,saved,stage:"VERIFY");
                        if(saved.State!="COMMITTING")
                        { saved=saved with {State="COMMITTING"};Persist(paths.Journal,saved); }
                        targetMayHaveChanged=true;
                        File.Move(paths.Stage,full); // no replace: atomic same-volume rename
                    }
                    else
                    {
                        if(!File.Exists(full))return Reply(FileWriteCode.CONFLICT,saved,stage:"VERIFY");
                        using(var original=new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.Delete))
                        {
                            string hash=Convert.ToHexString(SHA256.HashData(original));
                            if(!string.Equals(hash,saved.OriginalSha256,StringComparison.OrdinalIgnoreCase))
                                return Reply(FileWriteCode.CONFLICT,saved,stage:"VERIFY");
                            var current=NativeFileReader.ExecuteAsync(new(NativeFileAction.STAT,full,
                                saved.Context,IncludeSha256:false),ct).GetAwaiter().GetResult();
                            if(current.Status!=NativeFileCode.COMPLETED || current.Info?.Version!=saved.OriginalVersion)
                                return Reply(FileWriteCode.CONFLICT,saved,stage:"VERIFY");
                            // Windows ReplaceFileW is atomic on the same volume.
                            if(saved.State!="COMMITTING")
                            { saved=saved with {State="COMMITTING"};Persist(paths.Journal,saved); }
                            targetMayHaveChanged=true;
                            File.Replace(paths.Stage,full,null,ignoreMetadataErrors:false);
                        }
                    }
                    saved=saved with {State="COMMITTED"};Persist(paths.Journal,saved);
                    return Reply(FileWriteCode.COMMITTED,saved,stage:"VERIFY");
                }
                return Reply(FileWriteCode.INVALID_ARGUMENT,saved,stage:"VALIDATE");
            }
            finally {mutex.ReleaseMutex();}
        }
        catch(FileWriteFailure e){return Reply(e.Code);}
        catch(UnauthorizedAccessException){return Reply(targetMayHaveChanged||sidecarMayHaveChanged?FileWriteCode.EFFECT_UNKNOWN:FileWriteCode.ACCESS_DENIED);}
        catch(System.Security.SecurityException){return Reply(targetMayHaveChanged||sidecarMayHaveChanged?FileWriteCode.EFFECT_UNKNOWN:FileWriteCode.ACCESS_DENIED);}
        catch(Win32Exception e){return Reply(targetMayHaveChanged||sidecarMayHaveChanged?FileWriteCode.EFFECT_UNKNOWN:
            e.NativeErrorCode is 32 or 33 ? FileWriteCode.SHARING_VIOLATION:
            FileWriteCode.IO_ERROR,native:e.NativeErrorCode);}
        catch(OperationCanceledException){return Reply(FileWriteCode.EFFECT_UNKNOWN,stage:"TIMEOUT");}
        catch(Exception e) when(e is IOException or JsonException or ArgumentException or NotSupportedException)
        {return Reply(targetMayHaveChanged||sidecarMayHaveChanged?FileWriteCode.EFFECT_UNKNOWN:
            e is IOException && (e.HResult&0xffff) is 32 or 33 ? FileWriteCode.SHARING_VIOLATION:
            FileWriteCode.IO_ERROR);}
    }

    private sealed class FileWriteFailure(FileWriteCode code):Exception
    { internal FileWriteCode Code { get; }=code; }
}
