using System.Diagnostics;

static void Check(bool ok, string label)
{
    if (!ok) throw new InvalidOperationException("FAIL " + label);
    Console.WriteLine("PASS " + label);
}
static async Task<ExecutionResult> Run(string command, int timeout = 10, string? cwd = null, CancellationToken ct = default)
{
    var result = await PowerShellRunner.RunAsync(command, timeout, cwd, ct);
    Console.WriteLine($"runner status={result.Status} exit={result.ExitCode} stage={result.Stage} native_error={result.NativeError} elapsed_ms={result.ElapsedMs}");
    return result;
}
if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
    throw new InvalidOperationException("Isolated Windows CI only");
Check(File.Exists(PowerShellRunner.Executable), "r2.machine_wide_pwsh");
var simple = await Run("Write-Output 'hello'");
Check(simple.Status == ExecutionState.COMPLETED && simple.ExitCode == 0 && simple.Stdout.Trim() == "hello", "r2.stdout_exit");
string prefix = "Write-Output 'Проверка Unicode 🚀'\n#";
var unicode = await Run(prefix + new string('x', 11982 - prefix.Length));
Check(unicode.Status == ExecutionState.COMPLETED && unicode.Stdout.Trim() == "Проверка Unicode 🚀", "r2.long_unicode_stdin");
var explicitExit = await Run("exit 7");
Check(explicitExit.Status == ExecutionState.COMMAND_ERROR && explicitExit.ExitCode == 7, "r2.explicit_exit");
var thrown = await Run("throw 'EXPECTED_PROBE_FAILURE'");
Check(thrown.Status == ExecutionState.COMMAND_ERROR && thrown.Stderr.Contains("EXPECTED_PROBE_FAILURE"), "r2.exception");
var warning = await Run("Write-Warning 'expected warning'; Write-Output 'after'");
Check(warning.Status == ExecutionState.COMPLETED && warning.Warnings?.Contains("expected warning") == true &&
    warning.Stdout.Trim() == "after", "r2.warning_stream");
var nonTerminating = await Run("Write-Error 'expected error' -ErrorAction Continue; Write-Output 'after'");
Check(nonTerminating.Status == ExecutionState.COMMAND_ERROR && nonTerminating.Stderr.Contains("expected error"), "r2.nonterminating_error");
var large = await Run("[Console]::Out.Write(('a' * 100000)); [Console]::Error.Write(('b' * 100000))");
Check(large.Status == ExecutionState.COMPLETED && large.OutputTruncated && large.Stdout.Length == 16000 &&
    large.Stderr.TrimEnd().Length == 16000, "r2.bounded_concurrent_drain");
var cwd = await Run("(Get-Location).Path", cwd: Path.GetTempPath());
Check(cwd.Status == ExecutionState.COMPLETED && Path.TrimEndingDirectorySeparator(cwd.Stdout.Trim()) ==
    Path.TrimEndingDirectorySeparator(Path.GetTempPath()), "r2.working_directory");
var timeout = await Run("Start-Sleep -Seconds 30", 1);
Check(timeout.Status == ExecutionState.TIMEOUT_UNKNOWN_EFFECT && timeout.ElapsedMs < 10000, "r2.timeout");
using (var cancel = new CancellationTokenSource())
{
    cancel.Cancel();
    var before = await Run("Write-Output 'must not execute'", ct: cancel.Token);
    Check(before.Status == ExecutionState.CANCELLED_BEFORE_EXECUTION, "r2.cancel_before_launch");
}
using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
{
    var running = await Run("Start-Sleep -Seconds 30", ct: cancel.Token);
    Check(running.Status == ExecutionState.CANCELLED_UNKNOWN_EFFECT, "r2.cancel_running");
}
// No remote services or arbitrary processes: only a known inert local child.
var tree = await Run("$p=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\\ping.exe') -ArgumentList '-n','60','127.0.0.1' -PassThru; [Console]::Out.WriteLine($p.Id); Start-Sleep -Seconds 30", 4);
Check(tree.Status == ExecutionState.TIMEOUT_UNKNOWN_EFFECT && int.TryParse(tree.Stdout.Trim(), out _), "r2.child_pid_observed");
int pid = int.Parse(tree.Stdout.Trim(), System.Globalization.CultureInfo.InvariantCulture);
bool gone;
try { using var child = Process.GetProcessById(pid); gone = child.WaitForExit(5000); }
catch (ArgumentException) { gone = true; }
Check(gone, "r2.job_terminated_child");
Console.WriteLine("R2_POWERSHELL_RUNNER=PASS (actual candidate runner, direct CI; not exposed through MCP yet)");

using(var bitmap=new System.Drawing.Bitmap(2400,1800))
{
    using(var graphics=System.Drawing.Graphics.FromImage(bitmap))graphics.Clear(System.Drawing.Color.SteelBlue);
    var png=DesktopCapture.Encode(bitmap,1280);
    Check(png is not null && png.Width==1280 && png.Height==960 &&
        png.Png.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),"r5.synthetic_png_dimensions_and_signature");
}
Check(!DesktopCapture.ValidArguments(0,0,0,10,null,1280),"r5.invalid_region_pair");

var fixtureCredential=new TunnelHost.Credential(1,"tunnel_"+new string('a',32),"inert-test-key");
Check(TunnelHost.Valid(fixtureCredential)&&!TunnelHost.Valid(fixtureCredential with {Version=2})&&
    !TunnelHost.Valid(fixtureCredential with {RuntimeKey="bad\nkey"}),"r6.typed_tunnel_credential");
var launch=TunnelHost.StartInfo(@"C:\Program Files\WindowsAdminMCP\client\tunnel-client-runtime.exe",
    @"C:\Program Files\WindowsAdminMCP\native\WindowsAdminMCP.exe",fixtureCredential);
Check(launch.FileName.EndsWith(@"\client\tunnel-client-runtime.exe",StringComparison.Ordinal)&&
    launch.ArgumentList.Contains("command=\"C:/Program Files/WindowsAdminMCP/native/WindowsAdminMCP.exe\" stdio,channel=main")&&
    launch.ArgumentList.Contains("env:CONTROL_PLANE_API_KEY")&&
    !launch.ArgumentList.Any(a=>a.Contains("inert-test-key",StringComparison.Ordinal))&&
    launch.Environment["CONTROL_PLANE_API_KEY"]=="inert-test-key","r6.stable_vendor_binding_no_key_in_argv");

var up=System.Net.NetworkInformation.OperationalStatus.Up;
var down=System.Net.NetworkInformation.OperationalStatus.Down;
Check(TunDependency.Classify([up],1)==TunReadiness.READY,"r6.tun_ready");
Check(TunDependency.Classify([],1)==TunReadiness.ADAPTER_MISSING,"r6.tun_missing");
Check(TunDependency.Classify([down],1)==TunReadiness.ADAPTER_DOWN,"r6.tun_down");
Check(TunDependency.Classify([up],0)==TunReadiness.DATAPLANE_NOT_RUNNING,"r6.tun_stale_adapter");
Check(TunDependency.Classify([up,up],1)==TunReadiness.ADAPTER_AMBIGUOUS,"r6.tun_duplicate_adapter");
Check(TunDependency.Classify([up],2)==TunReadiness.DATAPLANE_AMBIGUOUS,"r6.tun_duplicate_process");

using(var denied=System.Text.Json.JsonDocument.Parse("""{"schema_version":1,"component":"control-plane","details":{"http_status":403,"consecutive_failures":1}}"""))
    Check(TunnelLifetime.IsAccessDenied(denied.RootElement),"r6.authorization_denial_terminal");
using(var stale=System.Text.Json.JsonDocument.Parse("""{"schema_version":1,"component":"control-plane","details":{"http_status":403,"consecutive_failures":0}}"""))
    Check(!TunnelLifetime.IsAccessDenied(stale.RootElement),"r6.no_stale_denial");
using(var other=System.Text.Json.JsonDocument.Parse("""{"schema_version":1,"component":"control-plane","details":{"http_status":503,"consecutive_failures":2}}"""))
    Check(!TunnelLifetime.IsAccessDenied(other.RootElement),"r6.transient_error_not_auth_denial");
ProcessStartInfo InertChild()
{
    var info=new ProcessStartInfo(PowerShellRunner.Executable){UseShellExecute=false,WorkingDirectory=Environment.SystemDirectory};
    foreach(string arg in new[]{"-NoLogo","-NoProfile","-NonInteractive","-Command","Start-Sleep -Seconds 60"})info.ArgumentList.Add(arg);
    return info;
}
using(var fixture=NativeChildProcess.Start(InertChild()))
{
    fixture.Input.Close();
    int checks=0;
    var end=await TunnelLifetime.WatchAsync(fixture.Process,()=>++checks==1?TunReadiness.READY:TunReadiness.ADAPTER_DOWN,
        ()=>Task.FromResult(false),TimeSpan.FromMilliseconds(50));
    Check(end==TunnelEnd.TUN_LOST && !fixture.Process.HasExited,"r6.tun_loss_detected_on_real_child");
    fixture.StopTree();
    Check(fixture.Process.WaitForExit(5000),"r6.owned_child_terminated");
}
using(var fixture=NativeChildProcess.Start(InertChild()))
{
    fixture.Input.Close();
    var end=await TunnelLifetime.WatchAsync(fixture.Process,()=>TunReadiness.READY,
        ()=>Task.FromResult(true),TimeSpan.FromMilliseconds(50));
    Check(end==TunnelEnd.ACCESS_DENIED,"r6.real_child_auth_stop");
    fixture.StopTree();
    Check(fixture.Process.WaitForExit(5000),"r6.auth_denied_child_terminated");
}
