using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<DoctorState>))]
internal enum DoctorState { OK, DEGRADED, NOT_TESTED }
[JsonConverter(typeof(JsonStringEnumConverter<DoctorReason>))]
internal enum DoctorReason
{
    OBSERVED, MISSING, ACCESS_OR_READBACK_FAILED, NOT_CANONICAL, HASH_MISMATCH,
    MULTIPLE_PROCESSES, SERVICE_NOT_CONFIRMED, STORE_NOT_CONFIRMED, STORE_AT_CAPACITY,
    RECONCILIATION_REQUIRED, NETWORK_NOT_READY, BOOT_UNPROVEN, REMOTE_NOT_TESTED,
    STARTUP_NOT_TESTED, DESKTOP_NOT_TESTED
}
[JsonConverter(typeof(JsonStringEnumConverter<DoctorComponent>))]
internal enum DoctorComponent { Native, PowerShell, Service, Store, Boot, Tun, Vendor, Remote, Startup, Desktop }
[JsonConverter(typeof(JsonStringEnumConverter<DoctorAction>))]
internal enum DoctorAction { NONE, VERIFY_INSTALLATION, CHECK_SERVICE, INSPECT_JOBS, CHECK_BOOT_LOG, CHECK_INTERNET,
    VERIFY_SUPPORTED_REMOTE_ACCESS, VERIFY_STARTUP, VERIFY_INTERACTIVE_DESKTOP }
internal sealed record DoctorCheck(DoctorComponent Component,DoctorState Status,DoctorReason Reason,DoctorAction NextAction);
internal sealed record DoctorReport(int SchemaVersion,DoctorState Status,string CorrelationId,DateTimeOffset ObservedAt,
    long ElapsedMs,DoctorCheck[] Checks,string RuntimeVersion,string? Executable,string PowerShellPath,
    AdminStatus? Service,StoreSummary? Store,BootObservation Boot,TunReadiness Tun,
    int? TunnelPid,string? VendorSha256,int SessionId,bool UserInteractive);

internal static class Doctor
{
    // No process start, screenshots, credentials, external requests, scheduler
    // changes or second SQLite connection. The service owns the store summary.
    internal static async Task<DoctorReport> ObserveAsync()
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
            store.Total>=store.TotalLimit || store.Queued+store.Running+store.CancelRequested+store.WaitingForResume+store.WaitingForReboot>=store.ActiveLimit
            ?DoctorReason.STORE_AT_CAPACITY:DoctorReason.OBSERVED;
        Add(DoctorComponent.Store,storeReason==DoctorReason.OBSERVED?DoctorState.OK:DoctorState.DEGRADED,storeReason,
            storeReason==DoctorReason.OBSERVED?DoctorAction.NONE:DoctorAction.INSPECT_JOBS);
        var boot=BootEvidence.Observe();
        bool bootOk=boot.Status==BootEvidenceCode.FULL_BOOT_OBSERVED;
        Add(DoctorComponent.Boot,bootOk?DoctorState.OK:DoctorState.DEGRADED,
            bootOk?DoctorReason.OBSERVED:DoctorReason.BOOT_UNPROVEN,bootOk?DoctorAction.NONE:DoctorAction.CHECK_BOOT_LOG);
        var tun=TunDependency.Observe();
        Add(DoctorComponent.Tun,tun==TunReadiness.READY?DoctorState.OK:DoctorState.DEGRADED,
            tun==TunReadiness.READY?DoctorReason.OBSERVED:DoctorReason.NETWORK_NOT_READY,tun==TunReadiness.READY?DoctorAction.NONE:DoctorAction.CHECK_INTERNET);
        string? vendorHash=null;int? tunnelPid=null;
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
                        tunnelPid=processes[0].Id;
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
        // Process/file existence is not remote authorization or actual MCP traffic.
        Add(DoctorComponent.Remote,DoctorState.NOT_TESTED,DoctorReason.REMOTE_NOT_TESTED,DoctorAction.VERIFY_SUPPORTED_REMOTE_ACCESS);
        Add(DoctorComponent.Startup,DoctorState.NOT_TESTED,DoctorReason.STARTUP_NOT_TESTED,DoctorAction.VERIFY_STARTUP);
        Add(DoctorComponent.Desktop,DoctorState.NOT_TESTED,DoctorReason.DESKTOP_NOT_TESTED,DoctorAction.VERIFY_INTERACTIVE_DESKTOP);
        using var self=Process.GetCurrentProcess();
        return new(1,checks.Any(c=>c.Status==DoctorState.DEGRADED)?DoctorState.DEGRADED:DoctorState.NOT_TESTED,
            Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow,clock.ElapsedMilliseconds,checks.ToArray(),
            RuntimeInformation.FrameworkDescription,Environment.ProcessPath,PowerShellRunner.Executable,
            response.Info,store,boot,tun,tunnelPid,vendorHash,self.SessionId,Environment.UserInteractive);
    }
}
