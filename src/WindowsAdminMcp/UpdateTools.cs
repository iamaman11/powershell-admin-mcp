using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

[JsonConverter(typeof(JsonStringEnumConverter<UpdateToolAction>))]
public enum UpdateToolAction { REQUEST, STATUS, ACCEPT }

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
        UpdateToolAction? OperationAction = null);

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
    [property: JsonPropertyName("operation_action")] UpdateToolAction? OperationAction = null);

[McpServerToolType]
public sealed class UpdateTools
{
    [McpServerTool(Name = "powershell_update", UseStructuredContent = true),
     Description("Request a verified Scheduler update, explicitly accept an exact installed commit, or read the durable outcome.")]
    public static async Task<UpdateToolResult> Update(UpdateToolAction action,
        bool remote_acceptance_confirmed = false, string? expected_commit = null,
        CancellationToken cancellationToken = default)
    {
        if (!ValidArguments(action, remote_acceptance_confirmed, expected_commit))
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

        if (action == UpdateToolAction.ACCEPT)
        {
            if (!string.Equals(expected_commit, package.Commit, StringComparison.Ordinal))
                return new(UpdateToolCode.CONFLICT, InstalledCommit: package.Commit,
                    RollbackPending: pending, Detail: "EXPECTED_COMMIT_MISMATCH");
            if (!pending)
                return new(UpdateToolCode.NO_PENDING_ACCEPTANCE, InstalledCommit: package.Commit,
                    RollbackPending: false);
        }
        if (action is UpdateToolAction.REQUEST or UpdateToolAction.ACCEPT)
        {
            var service = await AdminTools.Status(cancellationToken: cancellationToken);
            if (service.Status != StatusCode.OK || service.ExecutionContext != CommandContext.USER ||
                service.UserContextAvailable != true || service.Frontend?.Elevated != true)
                return new(UpdateToolCode.SERVICE_UNAVAILABLE, InstalledCommit: package.Commit,
                    RollbackPending: pending, Detail: service.Status.ToString());
        }
        string script = Path.Combine(root, "native", "installer", "Update-OperatorTask.ps1");
        if (!File.Exists(script) || !File.Exists(PowerShellRunner.Executable))
            return new(UpdateToolCode.OPERATOR_UNAVAILABLE, InstalledCommit: package.Commit,
                RollbackPending: pending);
        string? operationId = action == UpdateToolAction.STATUS ? null : Guid.NewGuid().ToString("N");
        using var process = new Process();
        var start = new ProcessStartInfo(PowerShellRunner.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = root
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-File", script,
                      "-Action", action == UpdateToolAction.STATUS ? "Status" : "Start" })
            start.ArgumentList.Add(arg);
        if (operationId is not null)
        {
            start.ArgumentList.Add("-OperationId");
            start.ArgumentList.Add(operationId);
        }
        if (action == UpdateToolAction.ACCEPT)
        {
            start.ArgumentList.Add("-Operation");
            start.ArgumentList.Add("accept");
            start.ArgumentList.Add("-ExpectedCommit");
            start.ArgumentList.Add(expected_commit!);
            start.ArgumentList.Add("-RemoteAcceptanceConfirmed");
        }
        start.Environment.Remove("CONTROL_PLANE_API_KEY");
        start.Environment.Remove("OPENAI_API_KEY");
        process.StartInfo = start;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
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
                return new(action != UpdateToolAction.STATUS ? UpdateToolCode.REQUEST_UNKNOWN :
                    UpdateToolCode.OPERATOR_FAILED, operationId, InstalledCommit: package.Commit,
                    RollbackPending: pending, Detail: detail.Length == 0 ? "OPERATOR_FAILED" : detail,
                    OperationAction: action == UpdateToolAction.STATUS ? null : action);
            }
            var wire = JsonSerializer.Deserialize<UpdateTaskWire>(response, PipeWire.Json);
            if (wire is null || !Enum.TryParse<UpdateToolCode>(wire.Status, false, out var code))
                return new(UpdateToolCode.OPERATOR_FAILED, operationId, InstalledCommit: package.Commit,
                    Detail: "OPERATOR_PROTOCOL_ERROR");
            return new(code, wire.OperationId ?? operationId, wire.Stage, package.Commit,
                wire.Commit, wire.RollbackPending ?? pending, wire.TaskState, wire.LastTaskResult, wire.Reason,
                wire.OperationAction ?? (action == UpdateToolAction.STATUS ? null : action));
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or
                                  Win32Exception or JsonException or InvalidOperationException)
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (Exception x) when (x is InvalidOperationException or Win32Exception) { }
            return new(action != UpdateToolAction.STATUS ? UpdateToolCode.REQUEST_UNKNOWN :
                UpdateToolCode.OPERATOR_UNAVAILABLE, operationId, InstalledCommit: package.Commit,
                RollbackPending: pending, Detail: e.GetType().Name,
                OperationAction: action == UpdateToolAction.STATUS ? null : action);
        }
    }

    internal static bool ValidArguments(UpdateToolAction action, bool confirmed, string? commit) =>
        action switch
        {
            UpdateToolAction.REQUEST or UpdateToolAction.STATUS => !confirmed && commit is null,
            UpdateToolAction.ACCEPT => confirmed && commit is not null &&
                System.Text.RegularExpressions.Regex.IsMatch(commit, "^[a-f0-9]{40}$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            _ => false
        };
}
