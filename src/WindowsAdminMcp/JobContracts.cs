using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<JobAction>))]
public enum JobAction { submit, status, list, logs, cancel }
[JsonConverter(typeof(JsonStringEnumConverter<JobState>))]
public enum JobState { QUEUED, RUNNING, CANCEL_REQUESTED, COMPLETED, FAILED, CANCELLED, NEEDS_RECONCILIATION }

public sealed record JobInfo(
    [property:JsonPropertyName("job_id")] string JobId,
    [property:JsonPropertyName("operation_key")] string OperationKey,
    [property:JsonPropertyName("state")] JobState State,
    [property:JsonPropertyName("command_sha256")] string CommandHash,
    [property:JsonPropertyName("created_utc")] string CreatedUtc,
    [property:JsonPropertyName("updated_utc")] string UpdatedUtc,
    [property:JsonPropertyName("execution")] ExecutionResult? Execution = null);

public sealed record JobReply(
    [property:JsonPropertyName("status")] StatusCode Status,
    [property:JsonPropertyName("correlation_id")] string CorrelationId,
    [property:JsonPropertyName("job")] JobInfo? Job = null,
    [property:JsonPropertyName("jobs")] JobInfo[]? Jobs = null,
    [property:JsonPropertyName("operation_key")] string? OperationKey = null,
    [property:JsonPropertyName("native_error")] int? NativeError = null);

internal sealed record AdminResponse(StatusCode Code, string Correlation, AdminStatus? Info = null, JobReply? Job = null);
