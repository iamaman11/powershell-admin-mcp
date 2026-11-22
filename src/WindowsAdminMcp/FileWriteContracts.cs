using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<FileWriteAction>))]
public enum FileWriteAction { BEGIN, APPEND, COMMIT, ABORT, STATUS }

[JsonConverter(typeof(JsonStringEnumConverter<FileOverwritePolicy>))]
public enum FileOverwritePolicy { FAIL_IF_EXISTS, REPLACE_IF_MATCH }

[JsonConverter(typeof(JsonStringEnumConverter<FileWriteCode>))]
public enum FileWriteCode
{
    STAGED, COMMITTED, ABORTED, INVALID_ARGUMENT, CONFLICT, NOT_FOUND,
    ACCESS_DENIED, REPARSE_POINT, SHARING_VIOLATION, HASH_MISMATCH,
    CAPABILITY_UNAVAILABLE, STORE_FULL, TIMEOUT, IO_ERROR, EFFECT_UNKNOWN,
    SERVICE_UNAVAILABLE, USER_CONTEXT_UNAVAILABLE
}

public sealed record FileWriteReply(
    [property:JsonPropertyName("status")] FileWriteCode Status,
    [property:JsonPropertyName("operation_id")] string OperationId,
    [property:JsonPropertyName("execution_context")] CommandContext ExecutionContext,
    [property:JsonPropertyName("received_bytes")] long? ReceivedBytes=null,
    [property:JsonPropertyName("expected_bytes")] long? ExpectedBytes=null,
    [property:JsonPropertyName("final_sha256")] string? FinalSha256=null,
    [property:JsonPropertyName("effect_state")] WindowEffectState EffectState=WindowEffectState.NO_EFFECT,
    [property:JsonPropertyName("stage")] string? Stage=null,
    [property:JsonPropertyName("observed_at")] DateTimeOffset? ObservedAt=null,
    [property:JsonPropertyName("native_error")] int? NativeError=null)
{
    [JsonPropertyName("safe_to_retry")]
    public bool SafeToRetry=>EffectState==WindowEffectState.NO_EFFECT &&
        Status is FileWriteCode.INVALID_ARGUMENT or FileWriteCode.ACCESS_DENIED or FileWriteCode.STORE_FULL or
            FileWriteCode.SERVICE_UNAVAILABLE or FileWriteCode.USER_CONTEXT_UNAVAILABLE;
}

internal sealed record FileWriteRequest(FileWriteAction Action,string Path,string OperationId,
    CommandContext ExecutionContext,long? ExpectedBytes=null,string? ExpectedSha256=null,
    FileOverwritePolicy OverwritePolicy=FileOverwritePolicy.FAIL_IF_EXISTS,
    string? ExpectedVersion=null,string? OriginalSha256=null,
    long? Offset=null,string? DataBase64=null,string? ChunkSha256=null);
