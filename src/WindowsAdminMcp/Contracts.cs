using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<AdminOperation>))]
internal enum AdminOperation { Status, Job, Diagnostics, UserWorker, Maintenance, File, FileWrite, FileReceipt, FileManage }

[JsonConverter(typeof(JsonStringEnumConverter<MaintenanceAction>))]
internal enum MaintenanceAction { Drain, Resume }

internal sealed record MaintenanceReply(
    [property: JsonPropertyName("status")] StatusCode Status,
    [property: JsonPropertyName("draining")] bool Draining,
    [property: JsonPropertyName("cancelled_queued")] int CancelledQueued = 0,
    [property: JsonPropertyName("cancel_requested")] int CancelRequested = 0);

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
    string? TraceId = null, bool IncludeInterruptedCommand = false, string? ResourceKey = null,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] MaintenanceAction? Maintenance = null,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] NativeFileRequest? File = null,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] FileWriteRequest? FileWrite = null,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] FileReceiptRequest? FileReceipt = null,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] FileManageRequest? FileManage = null,
    [property: JsonPropertyName("trace_timing"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)]
        bool TraceTiming = false);

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
    [property:JsonPropertyName("admission_open")] bool? AdmissionOpen = null,
    [property:JsonPropertyName("timing")] ToolTiming? Timing = null,
    [property:JsonPropertyName("diagnostics"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
        DoctorSnapshot? Diagnostics = null,
    [property:JsonPropertyName("tunnel_metrics"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
        TunnelMetricsSnapshot? TunnelMetrics = null);


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
    [property:JsonPropertyName("ipc_calls")] int? IpcCalls=null,
    [property:JsonPropertyName("transport"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
        TransportTrace? Transport=null);

// Metadata only: never include command text, paths, stdout, credentials or window content.
// Each duration is measured by a monotonic clock within one process; no clocks
// from different processes/machines are subtracted to manufacture attribution.
[JsonConverter(typeof(JsonStringEnumConverter<TransportTimingSource>))]
public enum TransportTimingSource { MONOTONIC_PROCESS_LOCAL }

public sealed record TransportTrace(
    [property:JsonPropertyName("diagnostics_version")] int DiagnosticsVersion,
    [property:JsonPropertyName("trace_id")] string TraceId,
    [property:JsonPropertyName("timing_source")] TransportTimingSource TimingSource,
    [property:JsonPropertyName("request_bytes")] int? RequestBytes,
    [property:JsonPropertyName("response_bytes")] int? ResponseBytes,
    [property:JsonPropertyName("frontend_encode_ms")] double? FrontendEncodeMs,
    [property:JsonPropertyName("ipc_connect_ms")] double? IpcConnectMs,
    [property:JsonPropertyName("ipc_write_ms")] double? IpcWriteMs,
    [property:JsonPropertyName("ipc_read_ms")] double? IpcReadMs,
    [property:JsonPropertyName("frontend_decode_ms")] double? FrontendDecodeMs,
    [property:JsonPropertyName("service_read_ms")] double? ServiceReadMs,
    [property:JsonPropertyName("service_decode_ms")] double? ServiceDecodeMs,
    [property:JsonPropertyName("service_auth_ms")] double? ServiceAuthMs,
    [property:JsonPropertyName("service_dispatch_ms")] double? ServiceDispatchMs,
    [property:JsonPropertyName("frontend_unattributed_ms")] double? FrontendUnattributedMs);
