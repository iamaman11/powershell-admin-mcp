using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

[JsonConverter(typeof(JsonStringEnumConverter<FileReceiptPhase>))]
internal enum FileReceiptPhase { PREPARE, OBSERVE }

[JsonConverter(typeof(JsonStringEnumConverter<FileReceiptCode>))]
internal enum FileReceiptCode { READY, CONFLICT, NOT_FOUND, STORE_FULL, EFFECT_UNKNOWN, IO_ERROR }

[JsonConverter(typeof(JsonStringEnumConverter<FileReceiptState>))]
internal enum FileReceiptState { STAGED, COMMITTING, COMMITTED, ABORTED, EFFECT_UNKNOWN }

internal sealed record FileReceiptRequest(FileReceiptPhase Phase,FileWriteAction Action,
    string Path,string OperationId,CommandContext Context,
    long? TotalBytes=null,string? Sha256=null,
    FileOverwritePolicy Policy=FileOverwritePolicy.FAIL_IF_EXISTS,
    string? OriginalVersion=null,string? OriginalSha256=null,
    FileWriteReply? Observation=null)
{
    internal static FileReceiptRequest From(FileWriteRequest operation,FileReceiptPhase phase,
        FileWriteReply? observation=null)=>new(phase,operation.Action,operation.Path,operation.OperationId,
            operation.ExecutionContext,operation.Action==FileWriteAction.BEGIN?operation.ExpectedBytes:null,
            operation.Action==FileWriteAction.BEGIN?operation.ExpectedSha256:null,
            operation.OverwritePolicy,operation.ExpectedVersion,operation.OriginalSha256,observation);
}

internal sealed record FileReceiptReply(FileReceiptCode Code,FileReceiptState? State=null,
    long? ReceivedBytes=null)
{
    internal FileWriteCode AsFileCode()=>Code switch
    {
        FileReceiptCode.CONFLICT=>FileWriteCode.CONFLICT,
        FileReceiptCode.NOT_FOUND=>FileWriteCode.NOT_FOUND,
        FileReceiptCode.STORE_FULL=>FileWriteCode.STORE_FULL,
        FileReceiptCode.EFFECT_UNKNOWN=>FileWriteCode.EFFECT_UNKNOWN,
        FileReceiptCode.IO_ERROR=>FileWriteCode.IO_ERROR,
        _=>FileWriteCode.STAGED
    };
}

// The existing SCM JobCoordinator remains the *only* writer to this SQLite
// ledger. It owns immutable operation identities and the intent before COMMIT.
// A same-volume sidecar in the actual file directory independently proves the
// filesystem effect after a USER frontend or SYSTEM service is killed. STATUS
// reconciles the ledger; neither channel authorizes blind effect replay.
internal sealed partial class JobCoordinator
{
    private static string ReceiptHash(string value)=>Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToUpperInvariant();

    internal FileReceiptReply FileReceipt(FileReceiptRequest request,string actorSid)
    {
        if(!Enum.IsDefined(request.Phase) || !Enum.IsDefined(request.Action) ||
            request.Context is not (CommandContext.SYSTEM or CommandContext.USER) ||
            actorSid is not {Length:>=4 and <=256} ||
            !Guid.TryParseExact(request.OperationId,"N",out _) ||
            request.Path is not {Length:>=1 and <=32767} ||
            !Path.IsPathFullyQualified(request.Path) || request.Path.Contains('\0') ||
            request.Phase==FileReceiptPhase.OBSERVE &&
              (request.Observation is null || request.Observation.OperationId!=request.OperationId ||
               request.Observation.ExecutionContext!=request.Context))
            return new(FileReceiptCode.CONFLICT);
        string targetHash;
        try{targetHash=ReceiptHash(Path.GetFullPath(request.Path));}
        catch(Exception e) when(e is ArgumentException or NotSupportedException or IOException)
        {return new(FileReceiptCode.CONFLICT);}
        lock(gate)
        {
            try
            {
                using var tx=Db.BeginTransaction();
                string? knownActor=null,knownTarget=null,knownContext=null,knownSha=null,
                    knownPolicy=null,knownVersion=null,knownOriginalSha=null;
                long knownBytes=0,received=0;
                FileReceiptState state=FileReceiptState.STAGED;
                using(var read=Command("""
                    SELECT actor_sid,target_hash,context,total_bytes,sha256,policy,
                           original_version,original_sha256,state,received_bytes
                    FROM file_receipts WHERE operation_id=$id
                    """,tx,("$id",request.OperationId)))
                using(var rows=read.ExecuteReader())
                {
                    if(rows.Read())
                    {
                        knownActor=rows.GetString(0);knownTarget=rows.GetString(1);
                        knownContext=rows.GetString(2);knownBytes=rows.GetInt64(3);
                        knownSha=rows.GetString(4);knownPolicy=rows.GetString(5);
                        knownVersion=rows.IsDBNull(6)?null:rows.GetString(6);
                        knownOriginalSha=rows.IsDBNull(7)?null:rows.GetString(7);
                        state=Enum.Parse<FileReceiptState>(rows.GetString(8));
                        received=rows.GetInt64(9);
                    }
                }
                if(knownActor is null)
                {
                    if(request.Phase!=FileReceiptPhase.PREPARE || request.Action!=FileWriteAction.BEGIN)
                        return new(FileReceiptCode.NOT_FOUND);
                    if(request.TotalBytes is not >=0 and <=NativeFileWriter.MaxUploadBytes ||
                        request.Sha256 is not {Length:64} || !request.Sha256.All(Uri.IsHexDigit) ||
                        !Enum.IsDefined(request.Policy) ||
                        request.Policy==FileOverwritePolicy.REPLACE_IF_MATCH &&
                            (request.OriginalVersion is not {Length:>=1 and <=128} ||
                             request.OriginalSha256 is not {Length:64} ||
                             !request.OriginalSha256.All(Uri.IsHexDigit)))
                        return new(FileReceiptCode.CONFLICT);
                    if(AdmissionCapacity(tx) is StoreStorageState.FULL or StoreStorageState.READBACK_FAILED)
                        return new(FileReceiptCode.STORE_FULL);
                    using(var insert=Command("""
                        INSERT INTO file_receipts(operation_id,actor_sid,target_hash,context,total_bytes,
                            sha256,policy,original_version,original_sha256,state,received_bytes,created_utc,updated_utc)
                        VALUES($id,$actor,$target,$ctx,$bytes,$sha,$policy,$version,$original,
                            'STAGED',0,$now,$now)
                        """,tx,("$id",request.OperationId),("$actor",actorSid),
                        ("$target",targetHash),("$ctx",request.Context.ToString()),
                        ("$bytes",request.TotalBytes!.Value),("$sha",request.Sha256!.ToUpperInvariant()),
                        ("$policy",request.Policy.ToString()),
                        ("$version",(object?)request.OriginalVersion??DBNull.Value),
                        ("$original",(object?)request.OriginalSha256?.ToUpperInvariant()??DBNull.Value),
                        ("$now",Now())))insert.ExecuteNonQuery();
                    tx.Commit();
                    return new(FileReceiptCode.READY,FileReceiptState.STAGED,0);
                }
                if(knownActor!=actorSid || knownTarget!=targetHash ||
                    knownContext!=request.Context.ToString())return new(FileReceiptCode.CONFLICT);
                if(request.Action==FileWriteAction.BEGIN &&
                    (request.TotalBytes!=knownBytes ||
                     !string.Equals(request.Sha256,knownSha,StringComparison.OrdinalIgnoreCase) ||
                     request.Policy.ToString()!=knownPolicy ||
                     request.OriginalVersion!=knownVersion ||
                     !string.Equals(request.OriginalSha256,knownOriginalSha,StringComparison.OrdinalIgnoreCase)))
                    return new(FileReceiptCode.CONFLICT);
                // A native file-management operation uses the same bounded
                // identity ledger, with a hash of its complete immutable spec.
                // File-write STATUS/COMMIT omit Sha256 and remain compatible.
                if(request.Action!=FileWriteAction.BEGIN && request.Sha256 is not null &&
                    !string.Equals(request.Sha256,knownSha,StringComparison.OrdinalIgnoreCase))
                    return new(FileReceiptCode.CONFLICT);
                if(request.Phase==FileReceiptPhase.PREPARE)
                {
                    if(state is FileReceiptState.EFFECT_UNKNOWN or FileReceiptState.COMMITTING &&
                        request.Action is FileWriteAction.COMMIT or FileWriteAction.APPEND or FileWriteAction.ABORT)
                        return new(FileReceiptCode.EFFECT_UNKNOWN,state,received);
                    if(request.Action==FileWriteAction.COMMIT && state==FileReceiptState.STAGED)
                    {
                        using var intent=Command("""
                            UPDATE file_receipts SET state='COMMITTING',updated_utc=$now
                            WHERE operation_id=$id AND state='STAGED'
                            """,tx,("$now",Now()),("$id",request.OperationId));
                        intent.ExecuteNonQuery();
                        state=FileReceiptState.COMMITTING;
                    }
                }
                else
                {
                    FileWriteReply result=request.Observation!;
                    FileReceiptState next=result.Status switch
                    {
                        FileWriteCode.COMMITTED=>FileReceiptState.COMMITTED,
                        FileWriteCode.ABORTED=>FileReceiptState.ABORTED,
                        FileWriteCode.EFFECT_UNKNOWN=>FileReceiptState.EFFECT_UNKNOWN,
                        _=>FileReceiptState.STAGED
                    };
                    if(state is FileReceiptState.COMMITTED or FileReceiptState.ABORTED && next!=state)
                        return new(FileReceiptCode.CONFLICT,state,received);
                    if(result.ReceivedBytes is long observed &&
                        (observed<received || observed>knownBytes))
                        return new(FileReceiptCode.CONFLICT,state,received);
                    received=Math.Max(received,result.ReceivedBytes??received);
                    using var update=Command("""
                        UPDATE file_receipts SET state=$state,received_bytes=$received,updated_utc=$now
                        WHERE operation_id=$id
                        """,tx,("$state",next.ToString()),("$received",received),
                        ("$now",Now()),("$id",request.OperationId));
                    update.ExecuteNonQuery();state=next;
                }
                tx.Commit();
                return new(FileReceiptCode.READY,state,received);
            }
            catch(Exception e) when(e is SqliteException or IOException or InvalidOperationException)
            {return new(FileReceiptCode.IO_ERROR);}
        }
    }
}
