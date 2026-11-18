using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

internal static class TunnelHost
{
    internal const string VendorSha256 = "DB590A52993C830A4C806108C3F25345EA986C6248210B86DF640E5081FF6A0B";
    private const string Source = "WindowsAdminMCP";
    private enum Code
    {
        STARTED, EXITED, INTERACTIVE_ADMIN_REQUIRED, INSTALL_PATH_INVALID,
        VENDOR_INTEGRITY_FAILED, SERVICE_UNAVAILABLE, ALREADY_RUNNING,
        CREDENTIAL_INVALID, START_FAILED, WAITING_FOR_TUN, TUN_READY, TUN_LOST, CONTROL_PLANE_ACCESS_DENIED, USER_WORKER_STOPPED
    }

    internal sealed record Credential(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("tunnel_id")] string TunnelId,
        [property: JsonPropertyName("runtime_key")] string RuntimeKey);

    internal static bool Valid(Credential? c) => c is
        { Version: 1, TunnelId: not null, RuntimeKey: not null } &&
        Regex.IsMatch(c.TunnelId, @"\Atunnel_[a-f0-9]{32}\z", RegexOptions.CultureInvariant) &&
        !string.IsNullOrWhiteSpace(c.RuntimeKey) && c.RuntimeKey.Length <= 16384 &&
        !c.RuntimeKey.Contains('\0') && !c.RuntimeKey.Contains('\r') && !c.RuntimeKey.Contains('\n');

    internal static ProcessStartInfo StartInfo(string vendor,string frontend,Credential credential)
    {
        if (!Valid(credential)) throw new InvalidOperationException("Invalid credential.");
        var info = new ProcessStartInfo(vendor)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(vendor)!
        };
        foreach (string arg in new[] {"run","--control-plane.tunnel-id",credential.TunnelId,
            "--control-plane.api-key","env:CONTROL_PLANE_API_KEY",
            "--mcp.command",$"command=\"{frontend.Replace('\\','/')}\" stdio,channel=main",
            "--health.listen-addr","127.0.0.1:8080",
            "--mcp.stdio-send-initialized-notification"})
            info.ArgumentList.Add(arg);
        info.Environment.Remove("OPENAI_API_KEY");
        info.Environment.Remove("WINDOWS_ADMIN_MCP_EXEC");
        info.Environment["CONTROL_PLANE_API_KEY"] = credential.RuntimeKey;
        return info;
    }

    internal static async Task<int> RunAsync()
    {
        string correlation = Guid.NewGuid().ToString("N");
        Code stage = Code.INTERACTIVE_ADMIN_REQUIRED;
        int Report(Code code,int exit,int? native=null,int? vendorPid=null,TunReadiness? dependency=null,int? vendorExitCode=null)
        {
            // Only allowlisted fields, no exception messages, arguments or credential data.
            string line = JsonSerializer.Serialize(new { status=code.ToString(),correlation_id=correlation,
                exit_code=exit,native_error=native,vendor_pid=vendorPid,vendor_exit_code=vendorExitCode,dependency_state=dependency?.ToString() });
            Console.Error.WriteLine(line);
            try { EventLog.WriteEntry(Source,line, exit==0 ? EventLogEntryType.Information : EventLogEntryType.Error); }
            catch (Exception e) when (e is System.Security.SecurityException or Win32Exception or
                InvalidOperationException or UnauthorizedAccessException or ArgumentException) { }
            return exit;
        }
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var self = Process.GetCurrentProcess();
            if(!Environment.UserInteractive || self.SessionId==0 ||
                !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                return Report(stage,2);
            stage=Code.INSTALL_PATH_INVALID;
            string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"WindowsAdminMCP");
            string frontend=Path.Combine(root,"native","WindowsAdminMCP.exe");
            string vendor=Path.Combine(root,"client","tunnel-client-runtime.exe");
            if(!string.Equals(Environment.ProcessPath,frontend,StringComparison.OrdinalIgnoreCase))return Report(stage,2);
            VerifyDirectory(root,identity.User);
            VerifyDirectory(Path.GetDirectoryName(frontend)!,identity.User);
            VerifyDirectory(Path.GetDirectoryName(vendor)!,identity.User);
            stage=Code.VENDOR_INTEGRITY_FAILED;
            VerifyFile(vendor,identity.User);
            using(var reader=File.OpenRead(vendor))
                if(!string.Equals(Convert.ToHexString(SHA256.HashData(reader)),VendorSha256,StringComparison.Ordinal))
                    return Report(stage,2);

            // SCM and the existing IPC contract are the source of truth, not a new health server.
            stage=Code.SERVICE_UNAVAILABLE;
            var clock=Stopwatch.StartNew();
            bool ready=false;
            do
            {
                var status=await AdminTools.Status(CancellationToken.None);
                if(status.Status==StatusCode.OK && status.ServicePid is int pid && status.ExecutionSid=="S-1-5-18")
                {
                    using var service=Process.GetProcessById(pid);
                    ready=string.Equals(service.MainModule?.FileName,frontend,StringComparison.OrdinalIgnoreCase);
                    if(ready)break;
                }
                await Task.Delay(500);
            }while(clock.Elapsed<TimeSpan.FromSeconds(30));
            if(!ready)return Report(stage,3);

            using var workerStop=new CancellationTokenSource();
            // USER jobs are local; an unavailable TUN does not own their lifetime.
            Task workerTask=UserWorker.RunAsync(workerStop.Token);
            bool workerFailureReported=false;
            int WorkerEnded(Task worker)
            {
                // The existing Task Scheduler is the restart owner. A stopped
                // USER worker must not leave a deceptively healthy tunnel host.
                workerFailureReported=true;
                return Report(Code.USER_WORKER_STOPPED,5,worker.Exception?.GetBaseException().HResult);
            }
            try
            {
            // Local dependency only: read OS state, never execute or reconfigure INTERNET.
            // Poll slowly while blocked and log transitions, not every observation.
            while (true)
            {
            TunReadiness? previous = null;
            while (true)
            {
                if(workerTask.IsCompleted)return WorkerEnded(workerTask);
                var dependency = TunDependency.Observe();
                if (dependency != previous)
                {
                    Report(dependency == TunReadiness.READY ? Code.TUN_READY : Code.WAITING_FOR_TUN,
                        0, dependency: dependency);
                    previous = dependency;
                }
                if (dependency == TunReadiness.READY)
                {
                    // The service may have stopped while the network was unavailable.
                    var status = await AdminTools.Status(CancellationToken.None);
                    if (status.Status == StatusCode.OK && status.ServicePid is int currentPid &&
                        status.ExecutionSid == "S-1-5-18")
                    {
                        using var service = Process.GetProcessById(currentPid);
                        if (string.Equals(service.MainModule?.FileName, frontend, StringComparison.OrdinalIgnoreCase))
                            break;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(5));
            }

            stage=Code.ALREADY_RUNNING;
            var existing=Process.GetProcessesByName("tunnel-client-runtime");
            try{if(existing.Length!=0)return Report(stage,4);}
            finally{foreach(var process in existing)process.Dispose();}

            stage=Code.CREDENTIAL_INVALID;
            string credentialPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "WindowsAdminMCP","tunnel-credential.dpapi");
            VerifyDirectory(Path.GetDirectoryName(credentialPath)!,identity.User);
            VerifyFile(credentialPath,identity.User);
            if(new FileInfo(credentialPath).Length is <=0 or >65536)return Report(stage,2);
            byte[] plaintext=ProtectedData.Unprotect(File.ReadAllBytes(credentialPath),null,DataProtectionScope.LocalMachine);
            ProcessStartInfo info;
            try
            {
                var credential=JsonSerializer.Deserialize<Credential>(plaintext);
                if(!Valid(credential))return Report(stage,2);
                info=StartInfo(vendor,frontend,credential!);
            }
            finally{CryptographicOperations.ZeroMemory(plaintext);}
            stage=Code.WAITING_FOR_TUN;
            // Revalidate immediately before starting the vendor, not after using the network.
            var finalDependency=TunDependency.Observe();
            if(finalDependency!=TunReadiness.READY)
            {
                info.Environment.Remove("CONTROL_PLANE_API_KEY");
                Report(stage,0,dependency:finalDependency);
                await Task.Delay(TimeSpan.FromSeconds(5));
                continue;
            }
            stage=Code.START_FAILED;
            NativeChildProcess child;
            try{child=NativeChildProcess.Start(info);}
            finally{info.Environment.Remove("CONTROL_PLANE_API_KEY");}
            using(child)
            {
                // Job Object closes the complete owned tree on task stop or launcher crash.
                // The vendor owns its protocol and network recovery while the dependency is alive.
                child.Input.Close();
                Report(Code.STARTED,0,vendorPid:child.Process.Id);
                var stdout=Task.Run(()=>child.Output.CopyTo(Stream.Null));
                var stderr=Task.Run(()=>child.Error.CopyTo(Stream.Null));
                var end=await TunnelLifetime.WatchAsync(child.Process,TunDependency.Observe,
                    TunnelLifetime.AccessDeniedAsync,TimeSpan.FromSeconds(5),workerTask);
                child.StopTree();
                if(!child.Process.WaitForExit(5000))return Report(Code.START_FAILED,5,vendorPid:child.Process.Id);
                await Task.WhenAll(stdout,stderr).WaitAsync(TimeSpan.FromSeconds(5));
                if(end==TunnelEnd.USER_WORKER_STOPPED)return WorkerEnded(workerTask);
                if(end==TunnelEnd.ACCESS_DENIED)
                    // An explicit operator action is required after an access denial.
                    return Report(Code.CONTROL_PLANE_ACCESS_DENIED,0,vendorPid:child.Process.Id);
                if(end==TunnelEnd.EXITED)
                    return Report(Code.EXITED,TunnelLifetime.UnexpectedExitCode(child.Process.ExitCode),vendorPid:child.Process.Id,vendorExitCode:child.Process.ExitCode);
                Report(Code.TUN_LOST,0,vendorPid:child.Process.Id);
            }
            await Task.Delay(TimeSpan.FromSeconds(5));
            }
            }
            finally
            {
                await workerStop.CancelAsync();
                try{await workerTask;}
                catch when(workerFailureReported && workerTask.IsFaulted)
                { /* already reported, return nonzero to Task Scheduler */ }
            }
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or Win32Exception or
            CryptographicException or JsonException or ArgumentException or InvalidOperationException or TimeoutException or System.Security.SecurityException)
        {return Report(stage,5,e is Win32Exception w?w.NativeErrorCode:e.HResult);}
    }

    private static void VerifyDirectory(string path,SecurityIdentifier? current)
    {
        var item=new DirectoryInfo(path);
        if((item.Attributes&FileAttributes.ReparsePoint)!=0)throw new UnauthorizedAccessException();
        VerifyAcl(item.GetAccessControl(),current);
    }
    private static void VerifyFile(string path,SecurityIdentifier? current)
    {
        var item=new FileInfo(path);
        if((item.Attributes&FileAttributes.ReparsePoint)!=0)throw new UnauthorizedAccessException();
        VerifyAcl(item.GetAccessControl(),current);
    }
    private static void VerifyAcl(FileSystemSecurity acl,SecurityIdentifier? current)
    {
        var admins=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
        var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
        if(acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            (!owner.Equals(admins)&&!owner.Equals(system)&&(current is null||!owner.Equals(current))))throw new UnauthorizedAccessException();
        foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType==AccessControlType.Allow &&
                !rule.IdentityReference.Equals(admins)&&!rule.IdentityReference.Equals(system))
                throw new UnauthorizedAccessException();
    }
}
