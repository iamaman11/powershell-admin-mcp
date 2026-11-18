using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<JobAction>))]
public enum JobAction { submit, status, list, logs, cancel, resume }
[JsonConverter(typeof(JsonStringEnumConverter<JobState>))]
public enum JobState { QUEUED, RUNNING, CANCEL_REQUESTED, WAITING_FOR_RESUME, WAITING_FOR_REBOOT, COMPLETED, FAILED, CANCELLED, NEEDS_RECONCILIATION }

public sealed record JobStep(
    [property:JsonPropertyName("command")] string Command,
    [property:JsonPropertyName("timeout_seconds")] int TimeoutSeconds=30,
    [property:JsonPropertyName("pause_after")] bool PauseAfter=false,
    [property:JsonPropertyName("reboot_after"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] bool RebootAfter=false,
    [property:JsonPropertyName("verify_after_reboot"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] bool VerifyAfterReboot=false);

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
    [property:JsonPropertyName("result_step")] int? ResultStep = null,
    [property:JsonPropertyName("reboot_evidence"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] BootEvidenceCode? RebootEvidence = null);

public sealed record JobReply(
    [property:JsonPropertyName("status")] StatusCode Status,
    [property:JsonPropertyName("correlation_id")] string CorrelationId,
    [property:JsonPropertyName("job")] JobInfo? Job = null,
    [property:JsonPropertyName("jobs")] JobInfo[]? Jobs = null,
    [property:JsonPropertyName("operation_key")] string? OperationKey = null,
    [property:JsonPropertyName("native_error")] int? NativeError = null,
    [property:JsonPropertyName("next_before_seq")] long? NextBeforeSeq = null);

internal sealed record StoreSummary(long Total,long Queued,long Running,long CancelRequested,long WaitingForResume,
    long WaitingForReboot,long NeedsReconciliation,long Completed,long Failed,long Cancelled,int SchemaVersion,int ActiveLimit,int TotalLimit);
internal sealed record AdminResponse(StatusCode Code, string Correlation, AdminStatus? Info = null, JobReply? Job = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] StoreSummary? Store = null);
