using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<DoctorState>))]
public enum DoctorState { OK, DEGRADED, NOT_TESTED }
[JsonConverter(typeof(JsonStringEnumConverter<DoctorReason>))]
public enum DoctorReason
{
    OBSERVED, MISSING, ACCESS_OR_READBACK_FAILED, NOT_CANONICAL, HASH_MISMATCH,
    MULTIPLE_PROCESSES, SERVICE_NOT_CONFIRMED, STORE_NOT_CONFIRMED, STORE_AT_CAPACITY, STORE_CAPACITY_WARNING,
    RECONCILIATION_REQUIRED, NETWORK_NOT_READY, BOOT_UNPROVEN, REMOTE_NOT_TESTED,
    STARTUP_NOT_TESTED, DESKTOP_NOT_TESTED, EXTERNAL_RECEIPT_MATCHED, EXTERNAL_RECEIPT_INVALID, MANIFEST_UNVERIFIED, STARTUP_NOT_READY, STARTUP_READBACK_FAILED
}
[JsonConverter(typeof(JsonStringEnumConverter<DoctorComponent>))]
public enum DoctorComponent { Native, Package, PowerShell, Service, Store, Boot, Tun, Vendor, Remote, Startup, Desktop }
[JsonConverter(typeof(JsonStringEnumConverter<DoctorAction>))]
public enum DoctorAction { NONE, VERIFY_INSTALLATION, CHECK_SERVICE, INSPECT_JOBS, CHECK_BOOT_LOG, CHECK_INTERNET,
    VERIFY_SUPPORTED_REMOTE_ACCESS, VERIFY_STARTUP, VERIFY_INTERACTIVE_DESKTOP }
public sealed record DoctorCheck(DoctorComponent Component,DoctorState Status,DoctorReason Reason,DoctorAction NextAction);
public sealed record DoctorSnapshot(
    [property:JsonPropertyName("status")] DoctorState Status,
    [property:JsonPropertyName("checks")] DoctorCheck[] Checks,
    [property:JsonPropertyName("local_operational_ready")] bool LocalOperationalReady,
    [property:JsonPropertyName("user_worker_ready")] bool UserWorkerReady,
    [property:JsonPropertyName("reconciliation_count")] long? ReconciliationCount,
    [property:JsonPropertyName("installed_commit")] string? InstalledCommit,
    [property:JsonPropertyName("elapsed_ms")] long ElapsedMs)
{
    internal static DoctorSnapshot FromReport(DoctorReport report) => new(report.Status, report.Checks,
        report.LocalOperationalReady, report.UserWorkerReady, report.ReconciliationCount,
        report.InstalledCommit, report.ElapsedMs);
}
internal sealed record DoctorReport(int SchemaVersion,DoctorState Status,string CorrelationId,DateTimeOffset ObservedAt,
    long ElapsedMs,DoctorCheck[] Checks,string RuntimeVersion,string? Executable,string PowerShellPath,
    AdminStatus? Service,StoreSummary? Store,BootObservation Boot,TunReadiness Tun,
    int? TunnelPid,string? VendorSha256,int SessionId,bool UserInteractive,
    PackageObservation Package,StartupObservation? Startup,VersionIdentity Versions,string RequiredTunAdapter,string RequiredDataplanePath,
    EvidenceObservation RemoteEvidence,DateTimeOffset? TunnelStartedUtc,int? TunnelSessionId,
    bool LocalOperationalReady = false,bool UserWorkerReady = false,long? ReconciliationCount = null,
    DoctorState ExternalAcceptanceStatus = DoctorState.NOT_TESTED,string? InstalledCommit = null);

internal static class Doctor
{
    // No process start, screenshots, credentials, external requests, scheduler
    // changes or second SQLite connection. The service owns the store summary.
    internal static async Task<DoctorReport> ObserveAsync(string? evidencePath=null)
    {
        var clock=Stopwatch.StartNew();
        var checks=new List<DoctorCheck>();
        void Add(DoctorComponent c,DoctorState s,DoctorReason r,DoctorAction a=DoctorAction.NONE)
            => checks.Add(new(c,s,r,a));
        string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"WindowsAdminMCP");
        string native=Path.Combine(root,"native","WindowsAdminMCP.exe");
        string vendor=Path.Combine(root,"client","tunnel-client-runtime.exe");
        bool canonical=string.Equals(Environment.ProcessPath,native,StringComparison.OrdinalIgnoreCase);
        Add(DoctorComponent.Native,canonical?DoctorState.OK:DoctorState.DEGRADED,
            canonical?DoctorReason.OBSERVED:DoctorReason.NOT_CANONICAL,canonical?DoctorAction.NONE:DoctorAction.VERIFY_INSTALLATION);
        var package=PackageIntegrity.Observe(root);
        Add(DoctorComponent.Package,package.Status==PackageCode.VERIFIED?DoctorState.OK:DoctorState.DEGRADED,
            package.Status==PackageCode.VERIFIED?DoctorReason.OBSERVED:DoctorReason.MANIFEST_UNVERIFIED,
            package.Status==PackageCode.VERIFIED?DoctorAction.NONE:DoctorAction.VERIFY_INSTALLATION);
        bool pwsh=File.Exists(PowerShellRunner.Executable);
        Add(DoctorComponent.PowerShell,pwsh?DoctorState.OK:DoctorState.DEGRADED,pwsh?DoctorReason.OBSERVED:DoctorReason.MISSING,
            pwsh?DoctorAction.NONE:DoctorAction.VERIFY_INSTALLATION);
        var response=await AdminTools.Diagnostics(CancellationToken.None);
        bool service=response.Code==StatusCode.OK && response.Info is {Status:StatusCode.OK,ExecutionSid:"S-1-5-18",ServicePid:int};
        if(service)
        {
            try
            {
                using var process=Process.GetProcessById(response.Info!.ServicePid!.Value);
                service=string.Equals(process.MainModule?.FileName,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase);
            }
            catch(Exception e) when(e is Win32Exception or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.Security.SecurityException)
            {service=false;}
        }
        Add(DoctorComponent.Service,service?DoctorState.OK:DoctorState.DEGRADED,
            service?DoctorReason.OBSERVED:DoctorReason.SERVICE_NOT_CONFIRMED,service?DoctorAction.NONE:DoctorAction.CHECK_SERVICE);
        var store=service?response.Store:null;
        var storeReason=store is null?DoctorReason.STORE_NOT_CONFIRMED:
            store.NeedsReconciliation>0?DoctorReason.RECONCILIATION_REQUIRED:
            store.Detailed>=store.TotalLimit
            ?DoctorReason.STORE_AT_CAPACITY:store.CapacityWarning?DoctorReason.STORE_CAPACITY_WARNING:DoctorReason.OBSERVED;
        Add(DoctorComponent.Store,storeReason==DoctorReason.OBSERVED?DoctorState.OK:DoctorState.DEGRADED,storeReason,
            storeReason==DoctorReason.OBSERVED?DoctorAction.NONE:DoctorAction.INSPECT_JOBS);
        var boot=BootEvidence.Observe();
        bool bootOk=boot.Status==BootEvidenceCode.FULL_BOOT_OBSERVED;
        Add(DoctorComponent.Boot,bootOk?DoctorState.OK:DoctorState.DEGRADED,
            bootOk?DoctorReason.OBSERVED:DoctorReason.BOOT_UNPROVEN,bootOk?DoctorAction.NONE:DoctorAction.CHECK_BOOT_LOG);
        var tun=TunDependency.Observe();
        Add(DoctorComponent.Tun,tun==TunReadiness.READY?DoctorState.OK:DoctorState.DEGRADED,
            tun==TunReadiness.READY?DoctorReason.OBSERVED:DoctorReason.NETWORK_NOT_READY,tun==TunReadiness.READY?DoctorAction.NONE:DoctorAction.CHECK_INTERNET);
        string? vendorHash=null;int? tunnelPid=null,tunnelSession=null;DateTimeOffset? tunnelStarted=null;
        DoctorReason vendorReason=DoctorReason.MISSING;
        try
        {
            if(File.Exists(vendor))
            {
                using var stream=File.OpenRead(vendor);
                vendorHash=Convert.ToHexString(SHA256.HashData(stream));
                vendorReason=vendorHash==TunnelHost.VendorSha256?DoctorReason.OBSERVED:DoctorReason.HASH_MISMATCH;
            }
            var processes=Process.GetProcessesByName("tunnel-client-runtime");
            try
            {
                if(processes.Length>1) vendorReason=DoctorReason.MULTIPLE_PROCESSES;
                else if(processes.Length==1)
                {
                    if(string.Equals(processes[0].MainModule?.FileName,vendor,StringComparison.OrdinalIgnoreCase))
                        {tunnelPid=processes[0].Id;tunnelSession=processes[0].SessionId;
                        tunnelStarted=new DateTimeOffset(processes[0].StartTime.ToUniversalTime());}
                    else vendorReason=DoctorReason.NOT_CANONICAL;
                }
                else if(vendorReason==DoctorReason.OBSERVED)vendorReason=DoctorReason.MISSING;
            }
            finally{foreach(var process in processes)process.Dispose();}
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or System.Security.SecurityException)
        {vendorReason=DoctorReason.ACCESS_OR_READBACK_FAILED;}
        Add(DoctorComponent.Vendor,vendorReason==DoctorReason.OBSERVED?DoctorState.OK:DoctorState.DEGRADED,vendorReason,
            vendorReason==DoctorReason.OBSERVED?DoctorAction.NONE:DoctorAction.VERIFY_INSTALLATION);
        // Explicit external evidence is matched to the current installed commit,
        // service/tunnel generation and durable jobs. It is not a live cloud probe.
        var evidence=await AcceptanceEvidence.ObserveAsync(evidencePath,
            package.Status==PackageCode.VERIFIED?package.Commit:null,service?response.Info:null,
            vendorReason==DoctorReason.OBSERVED?tunnelPid:null,tunnelStarted,tunnelSession);
        Add(DoctorComponent.Remote,evidence.RemoteReceiptMatched?DoctorState.OK:DoctorState.NOT_TESTED,
            evidence.RemoteReceiptMatched?DoctorReason.EXTERNAL_RECEIPT_MATCHED:
            evidence.Status==EvidenceCode.NOT_SUPPLIED?DoctorReason.REMOTE_NOT_TESTED:DoctorReason.EXTERNAL_RECEIPT_INVALID,
            evidence.RemoteReceiptMatched?DoctorAction.NONE:DoctorAction.VERIFY_SUPPORTED_REMOTE_ACCESS);
        // Reuse the owner of Task Scheduler definition validation. Never execute
        // the installed script unless its complete protected package verifies.
        var startup=package.Status==PackageCode.VERIFIED && canonical && pwsh
            ?await ReadStartupAsync(root):null;
        var startupReason=startup is null?DoctorReason.STARTUP_READBACK_FAILED:
            startup.Ready?DoctorReason.OBSERVED:DoctorReason.STARTUP_NOT_READY;
        Add(DoctorComponent.Startup,startupReason==DoctorReason.OBSERVED?DoctorState.OK:DoctorState.DEGRADED,
            startupReason,startupReason==DoctorReason.OBSERVED?DoctorAction.NONE:DoctorAction.VERIFY_STARTUP);
        Add(DoctorComponent.Desktop,evidence.DesktopReceiptMatched?DoctorState.OK:DoctorState.NOT_TESTED,
            evidence.DesktopReceiptMatched?DoctorReason.EXTERNAL_RECEIPT_MATCHED:DoctorReason.DESKTOP_NOT_TESTED,
            evidence.DesktopReceiptMatched?DoctorAction.NONE:DoctorAction.VERIFY_INTERACTIVE_DESKTOP);
        using var self=Process.GetCurrentProcess();
        // Historical reconciliation is still a DEGRADED store check, but
        // never conflated with current admission capacity or service liveness.
        bool localReady=service && response.Info?.AdmissionOpen==true && store is not null &&
            store.Detailed < store.TotalLimit;
        return new(1,checks.Any(c=>c.Status==DoctorState.DEGRADED)?DoctorState.DEGRADED:
            checks.Any(c=>c.Status==DoctorState.NOT_TESTED)?DoctorState.NOT_TESTED:DoctorState.OK,
            Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow,clock.ElapsedMilliseconds,checks.ToArray(),
            RuntimeInformation.FrameworkDescription,Environment.ProcessPath,PowerShellRunner.Executable,
            response.Info,store,boot,tun,tunnelPid,vendorHash,self.SessionId,Environment.UserInteractive,package,startup,
            BuildIdentity.Observe(),TunDependency.AdapterName,TunDependency.DataplanePath,evidence,tunnelStarted,tunnelSession,
            localReady,service && response.Info?.UserContextAvailable==true,store?.NeedsReconciliation,
            evidence.RemoteReceiptMatched?DoctorState.OK:DoctorState.NOT_TESTED,
            package.Status==PackageCode.VERIFIED?package.Commit:null);
    }

    private static async Task<StartupObservation?> ReadStartupAsync(string root)
    {
        try
        {
            var info=new ProcessStartInfo(PowerShellRunner.Executable)
            {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,
                RedirectStandardError=true,WorkingDirectory=root
            };
            foreach(var arg in new[]{"-NoProfile","-NonInteractive","-File",
                Path.Combine(root,"native","NativeStartup.ps1"),"-Action","Status"})info.ArgumentList.Add(arg);
            info.Environment.Remove("CONTROL_PLANE_API_KEY");
            info.Environment.Remove("OPENAI_API_KEY");
            using var p=Process.Start(info);
            if(p is null)return null;
            try
            {
                var stdout=p.StandardOutput.ReadToEndAsync();
                var stderr=p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                var raw=await stdout;
                await stderr;
                if(p.ExitCode!=0||raw.Length>65536)return null;
                using var json=JsonDocument.Parse(raw);
                var r=json.RootElement;
                return new(r.GetProperty("startup_ready").GetBoolean(),
                    r.GetProperty("definition_valid").GetBoolean(),r.GetProperty("service_binding_valid").GetBoolean(),
                    r.GetProperty("service_start").ValueKind==JsonValueKind.String?r.GetProperty("service_start").GetString():null,
                    r.GetProperty("task").ValueKind==JsonValueKind.String?r.GetProperty("task").GetString():null,
                    r.GetProperty("logon_enabled").ValueKind==JsonValueKind.True,
                    r.GetProperty("recovery").ValueKind==JsonValueKind.Null?null:
                        r.GetProperty("recovery").Deserialize<RecoveryObservation>(PipeWire.Json));
            }
            finally{if(!p.HasExited){p.Kill(true);p.WaitForExit(5000);}}
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or
            TimeoutException or JsonException or KeyNotFoundException or System.Security.SecurityException)
        {return null;}
    }
}
internal sealed record StartupObservation(bool Ready,bool DefinitionValid,bool ServiceBindingValid,string? ServiceStart,string? Task,bool LogonEnabled,RecoveryObservation? Recovery);
