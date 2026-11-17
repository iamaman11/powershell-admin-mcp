using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<ExecutionState>))]
public enum ExecutionState
{
    COMPLETED, COMMAND_ERROR, INVALID_ARGUMENT, POWERSHELL_NOT_FOUND,
    EXECUTION_FAILED, TIMEOUT_BEFORE_EXECUTION, CANCELLED_BEFORE_EXECUTION,
    TIMEOUT_UNKNOWN_EFFECT, CANCELLED_UNKNOWN_EFFECT, OUTPUT_INCOMPLETE
}

public sealed record ExecutionResult(
    ExecutionState Status, string CommandSha256, long ElapsedMs,
    int? ExitCode = null, string Stdout = "", string Stderr = "",
    string[]? Warnings = null, bool OutputTruncated = false,
    string? Stage = null, int? NativeError = null);

internal static class PowerShellRunner
{
    private const int OutputLimit = 16000;
    internal static string Executable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "PowerShell", "7", "pwsh.exe");

    // Constant command-line argument. Command text itself travels exclusively over stdin.
    private const string Bootstrap = """
        $ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';$WarningPreference='Continue';
        $PSNativeCommandUseErrorActionPreference=$true;$PSStyle.OutputRendering='PlainText';
        [Console]::InputEncoding=[Text.UTF8Encoding]::new($false);[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);
        $OutputEncoding=[Text.UTF8Encoding]::new($false);$global:LASTEXITCODE=$null;
        try {
          $script:unhandledMcpError=$false;
          $body=[ScriptBlock]::Create([Console]::In.ReadToEnd());
          & $body 2>&1 3>&1 | ForEach-Object {
            if($_ -is [System.Management.Automation.WarningRecord]){[Console]::Error.WriteLine('MCP_WARNING:'+ $_.Message)}
            elseif($_ -is [System.Management.Automation.ErrorRecord]){
              if($_.FullyQualifiedErrorId -eq 'NativeCommandError'){[Console]::Error.WriteLine($_.ToString())}
              else{$script:unhandledMcpError=$true;[Console]::Error.WriteLine($_.ToString())}
            }else{$_}
          };
          if($script:unhandledMcpError){exit 1};
          if($null -ne $global:LASTEXITCODE -and $global:LASTEXITCODE -ne 0){exit $global:LASTEXITCODE};
          exit 0
        }catch{
          [Console]::Error.WriteLine($_.ToString());
          if($null -ne $global:LASTEXITCODE -and $global:LASTEXITCODE -ne 0){exit $global:LASTEXITCODE};
          exit 1
        }
        """;

    internal static async Task<ExecutionResult> RunAsync(string command, int timeoutSeconds, string? cwd, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command))).ToLowerInvariant();
        ExecutionResult Fail(ExecutionState state, string stage, int? code = null)
            => new(state, hash, clock.ElapsedMilliseconds, Stage: stage, NativeError: code);
        cwd ??= Environment.SystemDirectory;
        if (command.Length is < 1 or > 12000 || timeoutSeconds is < 1 or > 120 ||
            !Path.IsPathFullyQualified(cwd) || !Directory.Exists(cwd))
            return Fail(ExecutionState.INVALID_ARGUMENT, "VALIDATION");
        string executable = Executable;
        if (!File.Exists(executable)) return Fail(ExecutionState.POWERSHELL_NOT_FOUND, "MACHINE_WIDE_PWSH");
        if (cancel.IsCancellationRequested) return Fail(ExecutionState.CANCELLED_BEFORE_EXECUTION, "BEFORE_START");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        string line = "\"" + executable + "\" -NoLogo -NoProfile -NonInteractive -Command \"" +
            Bootstrap.Replace("\r", " ").Replace("\n", " ") + "\"";
        NativePowerShell child;
        try { child = NativePowerShell.Start(executable, line, cwd); }
        catch (NativeFailure e) { return Fail(ExecutionState.EXECUTION_FAILED, e.Stage, e.NativeErrorCode); }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return Fail(ExecutionState.EXECUTION_FAILED, "PROCESS_START", e.HResult); }
        using (child)
        {
            var @out = DrainAsync(child.Output);
            var err = DrainAsync(child.Error);
            var sent = SendAsync(child.Input, command);
            ExecutionState? interrupted = null;
            int? exit = null;
            try
            {
                await child.Process.WaitForExitAsync(deadline.Token);
                exit = child.Process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                interrupted = cancel.IsCancellationRequested ? ExecutionState.CANCELLED_UNKNOWN_EFFECT :
                    ExecutionState.TIMEOUT_UNKNOWN_EFFECT;
            }
            finally { child.StopTree(); } // closes inherited endpoints even for orphaned descendants
            try
            {
                await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await Task.WhenAll(@out, err, sent).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException)
            {
                return Fail(interrupted ?? ExecutionState.OUTPUT_INCOMPLETE, "PROCESS_OR_STREAM_DRAIN", e.HResult);
            }
            var output = await @out;
            var errors = await err;
            var warnings = new List<string>();
            var diagnostic = new StringBuilder();
            foreach (string text in errors.Text.Split('\n'))
            {
                if (text.StartsWith("MCP_WARNING:", StringComparison.Ordinal))
                    warnings.Add(text[12..].TrimEnd('\r'));
                else if (text.Length > 0) diagnostic.AppendLine(text.TrimEnd('\r'));
            }
            ExecutionState state = interrupted ?? (await sent ?
                (exit == 0 ? ExecutionState.COMPLETED : ExecutionState.COMMAND_ERROR) : ExecutionState.OUTPUT_INCOMPLETE);
            return new(state, hash, clock.ElapsedMilliseconds, exit, output.Text, diagnostic.ToString(),
                warnings.ToArray(), output.Truncated || errors.Truncated,
                interrupted is not null ? "PROCESS_INTERRUPTED" : null);
        }
    }

    private static async Task<bool> SendAsync(Stream input, string command)
    {
        try
        {
            await input.WriteAsync(Encoding.UTF8.GetBytes(command));
            await input.FlushAsync();
            return true;
        }
        catch (IOException) { return false; }
        finally { input.Dispose(); }
    }

    private sealed record Capture(string Text, bool Truncated);
    private static async Task<Capture> DrainAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        char[] buffer = new char[4096];
        var retained = new StringBuilder();
        bool truncated = false;
        while (await reader.ReadAsync(buffer.AsMemory()) is int count && count > 0)
        {
            int keep = Math.Min(count, OutputLimit - retained.Length);
            retained.Append(buffer, 0, keep);
            truncated |= keep != count;
        }
        if (retained.Length > 0 && char.IsHighSurrogate(retained[^1])) { retained.Length--; truncated = true; }
        return new(retained.ToString(), truncated);
    }
}
