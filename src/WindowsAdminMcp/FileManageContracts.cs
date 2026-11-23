using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<FileManageAction>))]
public enum FileManageAction { MKDIR, COPY, MOVE, DELETE }

[JsonConverter(typeof(JsonStringEnumConverter<FileManageCode>))]
public enum FileManageCode
{
    COMPLETED, READY, ALREADY_EXISTS, NOT_FOUND, DESTINATION_EXISTS, SOURCE_CHANGED,
    INVALID_ARGUMENT, ACCESS_DENIED, REPARSE_POINT, SHARING_VIOLATION,
    CROSS_VOLUME, NOT_EMPTY, STORE_FULL, SERVICE_UNAVAILABLE,
    USER_CONTEXT_UNAVAILABLE, TIMEOUT, IO_ERROR, EFFECT_UNKNOWN, CONFLICT
}

public sealed record FileManageReply(
    [property:JsonPropertyName("status")] FileManageCode Status,
    [property:JsonPropertyName("operation_id")] string OperationId,
    [property:JsonPropertyName("action")] FileManageAction Action,
    [property:JsonPropertyName("execution_context")] CommandContext ExecutionContext,
    [property:JsonPropertyName("effect_state")] WindowEffectState EffectState=WindowEffectState.NO_EFFECT,
    [property:JsonPropertyName("stage")] string? Stage=null,
    [property:JsonPropertyName("observed_at")] DateTimeOffset? ObservedAt=null,
    [property:JsonPropertyName("elapsed_ms")] long? ElapsedMs=null,
    [property:JsonPropertyName("native_error")] int? NativeError=null)
{
    [JsonPropertyName("safe_to_retry")]
    public bool SafeToRetry => EffectState==WindowEffectState.NO_EFFECT &&
        Status is FileManageCode.INVALID_ARGUMENT or FileManageCode.NOT_FOUND or
            FileManageCode.ALREADY_EXISTS or FileManageCode.DESTINATION_EXISTS or
            FileManageCode.SOURCE_CHANGED or FileManageCode.REPARSE_POINT or
            FileManageCode.ACCESS_DENIED or FileManageCode.SHARING_VIOLATION or
            FileManageCode.STORE_FULL or FileManageCode.SERVICE_UNAVAILABLE or
            FileManageCode.USER_CONTEXT_UNAVAILABLE or FileManageCode.CROSS_VOLUME or
            FileManageCode.NOT_EMPTY;
}

internal sealed record FileManageRequest(FileManageAction Action,string Path,string OperationId,
    CommandContext ExecutionContext,string? Destination=null,bool Recursive=false,
    string? ExpectedVersion=null,string? ExpectedSha256=null,bool ReadOnly=false);
