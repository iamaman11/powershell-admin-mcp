using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<JobAction>))]
public enum JobAction { submit, status, list, logs, cancel, resume, reconcile, prune }
[JsonConverter(typeof(JsonStringEnumConverter<JobState>))]
public enum JobState { QUEUED, RUNNING, CANCEL_REQUESTED, WAITING_FOR_RESUME, WAITING_FOR_REBOOT, COMPLETED, FAILED, CANCELLED, NEEDS_RECONCILIATION, RECONCILED }

[JsonConverter(typeof(JsonStringEnumConverter<ReconciliationDecision>))]
public enum ReconciliationDecision { EFFECT_CONFIRMED, NO_EFFECT_CONFIRMED, UNKNOWN }

public sealed record ReconciliationRecord(
    [property:JsonPropertyName("decision")] ReconciliationDecision Decision,
    [property:JsonPropertyName("evidence")] string Evidence,
    [property:JsonPropertyName("recorded_utc")] string RecordedUtc,
    [property:JsonPropertyName("ipc_caller_sid")] string IpcCallerSid,
    [property:JsonPropertyName("correlation_id")] string CorrelationId);

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
    [property:JsonPropertyName("reboot_evidence"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] BootEvidenceCode? RebootEvidence = null,
    [property:JsonPropertyName("reconciliation")] ReconciliationRecord? Reconciliation = null,
    [property:JsonPropertyName("history_pruned_utc")] string? HistoryPrunedUtc = null,
    [property:JsonPropertyName("interrupted_source_available")] bool? InterruptedSourceAvailable = null,
    [property:JsonPropertyName("execution_context")] CommandContext ExecutionContext = CommandContext.SYSTEM,
    [property:JsonPropertyName("execution_sid")] string? ExecutionSid = null,
    [property:JsonPropertyName("execution_session_id")] int? ExecutionSessionId = null,
    [property:JsonPropertyName("waiting_for_user")] bool? WaitingForUser = null,
    [property:JsonPropertyName("interrupted_command")] string? InterruptedCommand = null,
    [property:JsonPropertyName("resource_key")] string? ResourceKey = null,
    [property:JsonPropertyName("queue_reason")] QueueWaitReason? QueueReason = null);

public sealed record JobReply(
    [property:JsonPropertyName("status")] StatusCode Status,
    [property:JsonPropertyName("correlation_id")] string CorrelationId,
    [property:JsonPropertyName("job")] JobInfo? Job = null,
    [property:JsonPropertyName("jobs")] JobInfo[]? Jobs = null,
    [property:JsonPropertyName("operation_key")] string? OperationKey = null,
    [property:JsonPropertyName("native_error")] int? NativeError = null,
    [property:JsonPropertyName("next_before_seq")] long? NextBeforeSeq = null,
    [property:JsonPropertyName("store")] StoreSummary? Store = null,
    [property:JsonPropertyName("pruned_count")] int? PrunedCount = null,
    [property:JsonPropertyName("timing")] ToolTiming? Timing = null,
    [property:JsonPropertyName("tombstoned_count"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int? TombstonedCount = null);

[JsonConverter(typeof(JsonStringEnumConverter<StoreStorageState>))]
public enum StoreStorageState { AVAILABLE, WARNING, FULL, READBACK_FAILED }

public sealed record StoreStorageSummary(
    long? DbBytes, long? WalBytes, long? PageCount, long? FreelistPages,
    long? Tombstoned, long MetadataLimit, long BytesLimit, int TombstoneDays,
    StoreStorageState Status);

public sealed record StoreSummary(long Total,long Queued,long Running,long CancelRequested,long WaitingForResume,
    long WaitingForReboot,long NeedsReconciliation,long Completed,long Failed,long Cancelled,int SchemaVersion,int? ActiveLimit,int TotalLimit,
    long Detailed,long Pruned,long Reconciled,int RetentionDays,ExecutionBudget Budget,
    StoreStorageSummary? Storage=null)
{
    public bool CapacityWarning => Detailed>=TotalLimit*8/10 || (Storage?.Status is StoreStorageState.WARNING or StoreStorageState.FULL);
}
internal sealed record AdminResponse(StatusCode Code, string Correlation, AdminStatus? Info = null, JobReply? Job = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] StoreSummary? Store = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] WorkerReply? Worker = null,long? ServiceElapsedMs=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] MaintenanceReply? Maintenance = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] NativeFileReply? File = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] FileWriteReply? FileWrite = null);

[JsonConverter(typeof(JsonStringEnumConverter<QueueWaitReason>))]
public enum QueueWaitReason { USER_UNAVAILABLE, RECOVERY_HOLD, RESOURCE_PRESSURE, RESOURCE_BUSY, READY_OR_EARLIER_JOB }
