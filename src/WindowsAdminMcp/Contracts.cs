using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<AdminOperation>))]
internal enum AdminOperation { Status, Job, Diagnostics, UserWorker }

[JsonConverter(typeof(JsonStringEnumConverter<StatusCode>))]
public enum StatusCode
{
    OK, SERVICE_UNAVAILABLE, ACCESS_DENIED, IPC_TIMEOUT, IPC_PROTOCOL_ERROR, IPC_FAILED, INVALID_ARGUMENT, NOT_FOUND, CONFLICT, BUSY, STORE_FULL, NOT_CANCELLABLE, NOT_RESUMABLE, ADMISSION_UNCONFIRMED, NOT_RECONCILABLE, HISTORY_PRUNED, STORE_UNAVAILABLE, USER_CONTEXT_UNAVAILABLE, LEASE_LOST
}

internal sealed record AdminRequest([property: JsonRequired] AdminOperation Operation,
    JobAction? Action = null, string? Key = null, string? JobId = null, string? Command = null,
    int TimeoutSeconds = 30, string? WorkingDirectory = null, JobStep[]? Steps = null,
    int? StepIndex = null, int? ExpectedCompletedSteps = null,
    JobState? StateFilter = null, int? PageSize = null, long? BeforeSeq = null,
    ReconciliationDecision? Decision = null, string? Evidence = null, int? RetainDays = null,
    CommandContext? ExecutionContext = null, WorkerRequest? Worker = null,
    string? TraceId = null, bool IncludeInterruptedCommand = false);

[JsonConverter(typeof(JsonStringEnumConverter<CommandContext>))]
public enum CommandContext { SYSTEM, USER, UNCONFIRMED }

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
    [property: JsonPropertyName("command_context")] CommandContext? ExecutionContext = null,
    [property: JsonPropertyName("user_context_available")] bool? UserContextAvailable = null,
    [property:JsonPropertyName("versions")] VersionIdentity? Versions = null,
    [property:JsonPropertyName("timing")] ToolTiming? Timing = null);


// Service derives these fields from the authenticated local pipe client, not MCP input.
internal sealed record UserSession(string Sid,int SessionId,string LogonSid);
[JsonConverter(typeof(JsonStringEnumConverter<WorkerAction>))]
internal enum WorkerAction { poll, heartbeat, complete }
internal sealed record WorkerRequest(WorkerAction Action,string? LeaseId=null,ExecutionResult? Result=null);
internal sealed record UserWork(string LeaseId,string JobId,int Step,string Command,int TimeoutSeconds,string Cwd);
internal sealed record WorkerReply(StatusCode Status,UserWork? Work=null,bool CancelRequested=false);


public sealed record ToolTiming(
    [property:JsonPropertyName("started_utc")] DateTimeOffset? StartedUtc=null,
    [property:JsonPropertyName("tool_ms")] long? ToolMs=null,
    [property:JsonPropertyName("scm_ms")] long? ScmMs=null,
    [property:JsonPropertyName("ipc_ms")] long? IpcMs=null,
    [property:JsonPropertyName("service_ms")] long? ServiceMs=null,
    [property:JsonPropertyName("store_wait_ms")] long? StoreWaitMs=null,
    [property:JsonPropertyName("store_ms")] long? StoreMs=null,
    [property:JsonPropertyName("capture_ms")] long? CaptureMs=null,
    [property:JsonPropertyName("ipc_calls")] int? IpcCalls=null);
