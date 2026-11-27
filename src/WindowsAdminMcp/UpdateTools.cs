using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

[JsonConverter(typeof(JsonStringEnumConverter<UpdateToolAction>))]
public enum UpdateToolAction { REQUEST, STATUS, ACCEPT, FORGET_LEGACY, PREVIEW_BACKUPS, CLEANUP_BACKUPS }

[JsonConverter(typeof(JsonStringEnumConverter<UpdateToolCode>))]
public enum UpdateToolCode
{
    REQUESTED, RUNNING, COMPLETED, NO_UPDATE, FAILED, NOT_REQUESTED,
    REQUEST_UNKNOWN, UNAUTHORIZED, SERVICE_UNAVAILABLE, PACKAGE_UNVERIFIED,
    OPERATOR_UNAVAILABLE, OPERATOR_FAILED, INVALID_ARGUMENT, CONFLICT, NO_PENDING_ACCEPTANCE
}

public sealed record UpdateToolResult(
    [property: JsonPropertyName("status")] UpdateToolCode Status,
    [property: JsonPropertyName("operation_id")] string? OperationId = null,
    [property: JsonPropertyName("stage")] string? Stage = null,
    [property: JsonPropertyName("installed_commit")] string? InstalledCommit = null,
    [property: JsonPropertyName("operation_commit")] string? OperationCommit = null,
    [property: JsonPropertyName("rollback_pending")] bool? RollbackPending = null,
    [property: JsonPropertyName("task_state")] string? TaskState = null,
    [property: JsonPropertyName("last_task_result")] int? LastTaskResult = null,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("operation_action"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        UpdateToolAction? OperationAction = null,
    [property: JsonPropertyName("deleted_count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DeletedCount = null,
    [property: JsonPropertyName("remaining_count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RemainingCount = null,
    // stage describes the last installer trial, never the current live state.
    [property: JsonPropertyName("current_service_status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        StatusCode? CurrentServiceStatus = null,
    [property: JsonPropertyName("current_local_operational_ready"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool? CurrentLocalOperationalReady = null,
    [property: JsonPropertyName("current_observed_utc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        DateTimeOffset? CurrentObservedUtc = null,
    [property: JsonPropertyName("plan_sha256"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? PlanSha256 = null,
    [property: JsonPropertyName("candidate_count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? CandidateCount = null,
    [property: JsonPropertyName("candidate_bytes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long? CandidateBytes = null,
    [property: JsonPropertyName("retained_count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? RetainedCount = null,
    [property: JsonPropertyName("reclaimed_bytes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long? ReclaimedBytes = null,
    [property: JsonPropertyName("candidate_names"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string[]? CandidateNames = null);

internal sealed record BackupRetentionPlanWire(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("installed_commit")] string InstalledCommit,
    [property: JsonPropertyName("plan_sha256")] string PlanSha256,
    [property: JsonPropertyName("candidate_count")] int CandidateCount,
    [property: JsonPropertyName("candidate_bytes")] long CandidateBytes,
    [property: JsonPropertyName("retained_count")] int RetainedCount,
    [property: JsonPropertyName("retained_bytes")] long RetainedBytes,
    [property: JsonPropertyName("rollback_pending")] bool RollbackPending,
    [property: JsonPropertyName("candidate_names")] string[] CandidateNames);

internal sealed record UpdateTaskWire(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("operation_id")] string? OperationId = null,
    [property: JsonPropertyName("stage")] string? Stage = null,
    [property: JsonPropertyName("commit")] string? Commit = null,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("rollback_pending")] bool? RollbackPending = null,
    [property: JsonPropertyName("task_state")] string? TaskState = null,
    [property: JsonPropertyName("task")] string? Task = null,
    [property: JsonPropertyName("last_task_result")] int? LastTaskResult = null,
    [property: JsonPropertyName("operation_action")] UpdateToolAction? OperationAction = null,
    [property: JsonPropertyName("deleted_count")] int? DeletedCount = null,
    [property: JsonPropertyName("remaining_count")] int? RemainingCount = null,
    [property: JsonPropertyName("plan_sha256")] string? PlanSha256 = null,
    [property: JsonPropertyName("candidate_count")] int? CandidateCount = null,
    [property: JsonPropertyName("candidate_bytes")] long? CandidateBytes = null,
    [property: JsonPropertyName("retained_count")] int? RetainedCount = null,
    [property: JsonPropertyName("reclaimed_bytes")] long? ReclaimedBytes = null);

[McpServerToolType]
public sealed class UpdateTools
{
    [McpServerTool(Name = "powershell_update", UseStructuredContent = true),
     Description("Scheduler update, exact-commit acceptance, STATUS, backup preview/cleanup. CLEANUP_BACKUPS requires plan_sha256 and installed commit; preserves active files/rollback.")]
    public static async Task<UpdateToolResult> Update(UpdateToolAction action,
        bool remote_acceptance_confirmed = false, string? expected_commit = null,
        string? plan_sha256 = null,
        CancellationToken cancellationToken = default)
    {
        if (!ValidArguments(action, remote_acceptance_confirmed, expected_commit, plan_sha256))
            return new(UpdateToolCode.INVALID_ARGUMENT, Detail: "INVALID_ACTION_ARGUMENTS");
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsAdminMCP");
        string native = Path.Combine(root, "native", "WindowsAdminMCP.exe");
        if (!string.Equals(Environment.ProcessPath, native, StringComparison.OrdinalIgnoreCase))
            return new(UpdateToolCode.OPERATOR_UNAVAILABLE, Detail: "NOT_INSTALLED_FRONTEND");
        using var identity = WindowsIdentity.GetCurrent();
        using var self = Process.GetCurrentProcess();
        if (identity.IsSystem || self.SessionId <= 0 ||
            !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return new(UpdateToolCode.UNAUTHORIZED);

        // Full manifest/ACL/hash verification includes the new Scheduler helper.
        var package = PackageIntegrity.Observe(root);
        bool pending = File.Exists(Path.Combine(root, ".rollback", "journal.json"));
        if (package.Status != PackageCode.VERIFIED)
            return new(UpdateToolCode.PACKAGE_UNVERIFIED, RollbackPending: pending,
                Detail: package.Status.ToString());

        if (action == UpdateToolAction.FORGET_LEGACY &&
            (!string.Equals(expected_commit, package.Commit, StringComparison.Ordinal) || pending))
            return new(UpdateToolCode.CONFLICT, InstalledCommit: package.Commit,
                RollbackPending: pending, Detail: "LEGACY_CLEANUP_REQUIRES_ACCEPTED_EXACT_COMMIT");
        if (action == UpdateToolAction.ACCEPT)
        {
            if (!string.Equals(expected_commit, package.Commit, StringComparison.Ordinal))
                return new(UpdateToolCode.CONFLICT, InstalledCommit: package.Commit,
                    RollbackPending: pending, Detail: "EXPECTED_COMMIT_MISMATCH");
            if (!pending)
                return new(UpdateToolCode.NO_PENDING_ACCEPTANCE, InstalledCommit: package.Commit,
                    RollbackPending: false);
        }
        if (action == UpdateToolAction.CLEANUP_BACKUPS &&
            !string.Equals(expected_commit, package.Commit, StringComparison.Ordinal))
            return new(UpdateToolCode.CONFLICT, InstalledCommit: package.Commit,
                RollbackPending: pending, Detail: "EXPECTED_COMMIT_MISMATCH");
        if (action is UpdateToolAction.REQUEST or UpdateToolAction.ACCEPT or
            UpdateToolAction.FORGET_LEGACY or UpdateToolAction.CLEANUP_BACKUPS)
        {
            var service = await AdminTools.Status(cancellationToken: cancellationToken);
            if (service.Status != StatusCode.OK || service.ExecutionContext != CommandContext.USER ||
                service.UserContextAvailable != true || service.Frontend?.Elevated != true)
                return new(UpdateToolCode.SERVICE_UNAVAILABLE, InstalledCommit: package.Commit,
                    RollbackPending: pending, Detail: service.Status.ToString());
        }
        bool preview = action == UpdateToolAction.PREVIEW_BACKUPS;
        string script = Path.Combine(root, "native", "installer", preview ?
            "BackupRetention.ps1" : "Update-OperatorTask.ps1");
        if (!File.Exists(script) || !File.Exists(PowerShellRunner.Executable))
            return new(UpdateToolCode.OPERATOR_UNAVAILABLE, InstalledCommit: package.Commit,
                RollbackPending: pending);
        string? operationId = action is UpdateToolAction.STATUS or UpdateToolAction.PREVIEW_BACKUPS
            ? null : Guid.NewGuid().ToString("N");
        using var process = new Process();
        var start = new ProcessStartInfo(PowerShellRunner.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = root
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-File", script,
                      "-Action", preview ? "Plan" : action == UpdateToolAction.STATUS ? "Status" : "Start" })
            start.ArgumentList.Add(arg);
        if (preview)
        {
            start.ArgumentList.Add("-ExpectedCommit");
            start.ArgumentList.Add(package.Commit!);
        }
        if (operationId is not null)
        {
            start.ArgumentList.Add("-OperationId");
            start.ArgumentList.Add(operationId);
        }
        if (action == UpdateToolAction.FORGET_LEGACY)
        {
            start.ArgumentList.Add("-Operation");
            start.ArgumentList.Add("forget");
            start.ArgumentList.Add("-ExpectedCommit");
            start.ArgumentList.Add(expected_commit!);
        }
        if (action == UpdateToolAction.ACCEPT)
        {
            start.ArgumentList.Add("-Operation");
            start.ArgumentList.Add("accept");
            start.ArgumentList.Add("-ExpectedCommit");
            start.ArgumentList.Add(expected_commit!);
            start.ArgumentList.Add("-RemoteAcceptanceConfirmed");
        }
        if (action == UpdateToolAction.CLEANUP_BACKUPS)
        {
            start.ArgumentList.Add("-Operation");
            start.ArgumentList.Add("prune");
            start.ArgumentList.Add("-ExpectedCommit");
            start.ArgumentList.Add(expected_commit!);
            start.ArgumentList.Add("-ExpectedPlanSha256");
            start.ArgumentList.Add(plan_sha256!);
        }
        start.Environment.Remove("CONTROL_PLANE_API_KEY");
        start.Environment.Remove("OPENAI_API_KEY");
        process.StartInfo = start;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(preview ? 60 : 15));
        try
        {
            if (!process.Start()) throw new Win32Exception("OPERATOR_LAUNCH_FAILED");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string response = await stdout;
            string errors = await stderr;
            if (process.ExitCode != 0 || response.Length > 8192)
            {
                string detail = System.Text.RegularExpressions.Regex.Match(errors,
                    @"UPDATE_[A-Z0-9_]+", System.Text.RegularExpressions.RegexOptions.CultureInvariant).Value;
                return new(action is not (UpdateToolAction.STATUS or UpdateToolAction.PREVIEW_BACKUPS) ? UpdateToolCode.REQUEST_UNKNOWN :
                    UpdateToolCode.OPERATOR_FAILED, operationId, InstalledCommit: package.Commit,
                    RollbackPending: pending, Detail: detail.Length == 0 ? "OPERATOR_FAILED" : detail,
                    OperationAction: action == UpdateToolAction.STATUS ? null : action);
            }
            if (preview)
            {
                var plan = JsonSerializer.Deserialize<BackupRetentionPlanWire>(response, PipeWire.Json);
                if (plan is null || plan.Status != "READY" || plan.InstalledCommit != package.Commit ||
                    !IsSha256(plan.PlanSha256) || plan.CandidateCount is < 0 or > 512 ||
                    plan.CandidateBytes < 0 || plan.RetainedCount is < 0 or > 512 ||
                    plan.RetainedBytes < 0 || plan.RollbackPending != pending ||
                    plan.CandidateNames is null || plan.CandidateNames.Length != plan.CandidateCount ||
                    plan.CandidateNames.Distinct(StringComparer.Ordinal).Count() != plan.CandidateCount ||
                    plan.CandidateNames.Any(name => !System.Text.RegularExpressions.Regex.IsMatch(name,
                        @"^\.(trial|accepted)-backup-[a-f0-9]{40}$",
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
                    return new(UpdateToolCode.OPERATOR_FAILED, InstalledCommit: package.Commit,
                        RollbackPending: pending, Detail: "BACKUP_PLAN_UNVERIFIED");
                return new(UpdateToolCode.COMPLETED, Stage: "backup_plan",
                    InstalledCommit: package.Commit, RollbackPending: pending,
                    PlanSha256: plan.PlanSha256, CandidateCount: plan.CandidateCount,
                    CandidateBytes: plan.CandidateBytes, RetainedCount: plan.RetainedCount,
                    CandidateNames: plan.CandidateNames);
            }
            var wire = JsonSerializer.Deserialize<UpdateTaskWire>(response, PipeWire.Json);
            if (wire is null || !Enum.TryParse<UpdateToolCode>(wire.Status, false, out var code))
                return new(UpdateToolCode.OPERATOR_FAILED, operationId, InstalledCommit: package.Commit,
                    Detail: "OPERATOR_PROTOCOL_ERROR");
            var result=new UpdateToolResult(code, wire.OperationId ?? operationId, wire.Stage, package.Commit,
                wire.Commit, wire.RollbackPending ?? pending, wire.TaskState, wire.LastTaskResult, wire.Reason,
                wire.OperationAction ?? (action == UpdateToolAction.STATUS ? null : action),
                wire.DeletedCount, wire.RemainingCount,
                PlanSha256: wire.PlanSha256, CandidateCount: wire.CandidateCount,
                CandidateBytes: wire.CandidateBytes, RetainedCount: wire.RetainedCount,
                ReclaimedBytes: wire.ReclaimedBytes);
            if(action!=UpdateToolAction.STATUS)return result;
            // Historical trial outcome and live state are separate observations.
            // Never infer current readiness from the saved TRIAL_NOT_READY stage.
            var current=await AdminTools.Status(include_diagnostics:true,cancellationToken:cancellationToken);
            return WithCurrentReadiness(result,current);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or
                                  Win32Exception or JsonException or InvalidOperationException)
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (Exception x) when (x is InvalidOperationException or Win32Exception) { }
            return new(action is not (UpdateToolAction.STATUS or UpdateToolAction.PREVIEW_BACKUPS) ? UpdateToolCode.REQUEST_UNKNOWN :
                UpdateToolCode.OPERATOR_UNAVAILABLE, operationId, InstalledCommit: package.Commit,
                RollbackPending: pending, Detail: e.GetType().Name,
                OperationAction: action == UpdateToolAction.STATUS ? null : action);
        }
    }

    internal static UpdateToolResult WithCurrentReadiness(UpdateToolResult result,AdminStatus current) =>
        result with {CurrentServiceStatus=current.Status,
            CurrentLocalOperationalReady=current.Diagnostics?.LocalOperationalReady,
            CurrentObservedUtc=DateTimeOffset.UtcNow};

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-f0-9]{64}$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static bool ValidArguments(UpdateToolAction action, bool confirmed, string? commit,
        string? planSha256 = null) =>
        action switch
        {
            UpdateToolAction.REQUEST or UpdateToolAction.STATUS or UpdateToolAction.PREVIEW_BACKUPS =>
                !confirmed && commit is null && planSha256 is null,
            UpdateToolAction.ACCEPT => confirmed && commit is not null &&
                planSha256 is null &&
                System.Text.RegularExpressions.Regex.IsMatch(commit, "^[a-f0-9]{40}$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            UpdateToolAction.FORGET_LEGACY => !confirmed && planSha256 is null && commit is not null &&
                System.Text.RegularExpressions.Regex.IsMatch(commit, "^[a-f0-9]{40}$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            UpdateToolAction.CLEANUP_BACKUPS => !confirmed && commit is not null &&
                System.Text.RegularExpressions.Regex.IsMatch(commit, "^[a-f0-9]{40}$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant) &&
                IsSha256(planSha256),
            _ => false
        };
}
