using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<JobAction>))]
public enum JobAction { submit, status, list, logs, cancel, resume }
[JsonConverter(typeof(JsonStringEnumConverter<JobState>))]
public enum JobState { QUEUED, RUNNING, CANCEL_REQUESTED, WAITING_FOR_RESUME, COMPLETED, FAILED, CANCELLED, NEEDS_RECONCILIATION }

public sealed record JobStep(
    [property:JsonPropertyName("command")] string Command,
    [property:JsonPropertyName("timeout_seconds")] int TimeoutSeconds=30,
    [property:JsonPropertyName("pause_after")] bool PauseAfter=false);

public sealed record JobInfo(
    [property:JsonPropertyName("job_id")] string JobId,
    [property:JsonPropertyName("operation_key")] string OperationKey,
    [property:JsonPropertyName("state")] JobState State,
    [property:JsonPropertyName("command_sha256")] string CommandHash,
    [property:JsonPropertyName("created_utc")] string CreatedUtc,
    [property:JsonPropertyName("updated_utc")] string UpdatedUtc,
    [property:JsonPropertyName("execution")] ExecutionResult? Execution = null,
    [property:JsonPropertyName("completed_steps")] int CompletedSteps = 0,
    [property:JsonPropertyName("total_steps")] int TotalSteps = 1,
    [property:JsonPropertyName("result_step")] int? ResultStep = null);

public sealed record JobReply(
    [property:JsonPropertyName("status")] StatusCode Status,
    [property:JsonPropertyName("correlation_id")] string CorrelationId,
    [property:JsonPropertyName("job")] JobInfo? Job = null,
    [property:JsonPropertyName("jobs")] JobInfo[]? Jobs = null,
    [property:JsonPropertyName("operation_key")] string? OperationKey = null,
    [property:JsonPropertyName("native_error")] int? NativeError = null);

internal sealed record AdminResponse(StatusCode Code, string Correlation, AdminStatus? Info = null, JobReply? Job = null);
