using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<AdminOperation>))]
internal enum AdminOperation { Status, Job, Diagnostics }

[JsonConverter(typeof(JsonStringEnumConverter<StatusCode>))]
public enum StatusCode
{
    OK, SERVICE_UNAVAILABLE, ACCESS_DENIED, IPC_TIMEOUT, IPC_PROTOCOL_ERROR, IPC_FAILED, INVALID_ARGUMENT, NOT_FOUND, CONFLICT, BUSY, STORE_FULL, NOT_CANCELLABLE, NOT_RESUMABLE, ADMISSION_UNCONFIRMED
}

internal sealed record AdminRequest([property: JsonRequired] AdminOperation Operation,
    JobAction? Action = null, string? Key = null, string? JobId = null, string? Command = null,
    int TimeoutSeconds = 30, string? WorkingDirectory = null, JobStep[]? Steps = null,
    int? StepIndex = null, int? ExpectedCompletedSteps = null,
    JobState? StateFilter = null, int? PageSize = null, long? BeforeSeq = null);

[JsonConverter(typeof(JsonStringEnumConverter<CommandContext>))]
public enum CommandContext { SYSTEM, UNCONFIRMED }

public sealed record FrontendContext(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("identity")] string Identity,
    [property: JsonPropertyName("sid")] string? Sid,
    [property: JsonPropertyName("session_id")] int SessionId,
    [property: JsonPropertyName("elevated")] bool Elevated);

public sealed record AdminStatus(
    [property: JsonPropertyName("status")] StatusCode Status,
    [property: JsonPropertyName("correlation_id")] string CorrelationId,
    [property: JsonPropertyName("service_pid")] int? ServicePid = null,
    [property: JsonPropertyName("host")] string? Host = null,
    [property: JsonPropertyName("execution_identity")] string? ExecutionIdentity = null,
    [property: JsonPropertyName("execution_sid")] string? ExecutionSid = null,
    [property: JsonPropertyName("elevated")] bool? Elevated = null,
    [property: JsonPropertyName("started_utc")] DateTimeOffset? StartedUtc = null,
    [property: JsonPropertyName("native_error")] int? NativeError = null,
    [property: JsonPropertyName("frontend")] FrontendContext? Frontend = null,
    [property: JsonPropertyName("command_context")] CommandContext? ExecutionContext = null);
