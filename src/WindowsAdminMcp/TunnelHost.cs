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
    internal const string VendorSha256 = VendorLock.ExecutableSha256;
    private const string Source = "WindowsAdminMCP";
    private enum Code
    {
        STARTED, EXITED, INTERACTIVE_ADMIN_REQUIRED, INSTALL_PATH_INVALID,
        VENDOR_INTEGRITY_FAILED, SERVICE_UNAVAILABLE, ALREADY_RUNNING,
        CREDENTIAL_INVALID, START_FAILED, WAITING_FOR_TUN, TUN_READY, TUN_LOST, CONTROL_PLANE_ACCESS_DENIED, USER_WORKER_STOPPED,
        PROFILE_STOPPED, PROFILE_EXITED, PROFILE_CONFIG_INVALID
    }

    internal sealed record Credential(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("tunnel_id")] string TunnelId,
        [property: JsonPropertyName("runtime_key")] string RuntimeKey,
        [property: JsonPropertyName("profile_id")] string ProfileId = TunnelProfiles.Primary,
        [property: JsonPropertyName("health_port")] int HealthPort = TunnelProfiles.PrimaryPort);

    internal static bool Valid(Credential? c) => c is
        { Version: 1, TunnelId: not null, RuntimeKey: not null } &&
        Regex.IsMatch(c.TunnelId, @"\Atunnel_[a-f0-9]{32}\z", RegexOptions.CultureInvariant) &&
        !string.IsNullOrWhiteSpace(c.RuntimeKey) && c.RuntimeKey.Length <= 16384 &&
        !c.RuntimeKey.Contains('\0') && !c.RuntimeKey.Contains('\r') && !c.RuntimeKey.Contains('\n') &&
        TunnelProfiles.ValidName(c.ProfileId) && c.HealthPort is >= 1024 and <= 65535;

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
            "--health.listen-addr",$"127.0.0.1:{credential.HealthPort}",
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
        int Report(Code code,int exit,int? native=null,int? vendorPid=null,TunReadiness? dependency=null,int? vendorExitCode=null,string? profile=null)
        {
            // Only allowlisted fields, no exception messages, arguments or credential data.
            string line = JsonSerializer.Serialize(new { status=code.ToString(),correlation_id=correlation,
                exit_code=exit,native_error=native,vendor_pid=vendorPid,vendor_exit_code=vendorExitCode,dependency_state=dependency?.ToString(),profile_id=profile });
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
                var status=await AdminTools.Status(cancellationToken: CancellationToken.None);
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
            // A single scheduler-owned supervisor, one USER worker, N isolated
            // official vendor Job Objects. A tunnel failure never stops its peers.
            stage=Code.ALREADY_RUNNING;
            var existing=Process.GetProcessesByName("tunnel-client-runtime");
            try{if(existing.Length!=0)return Report(stage,4);}
            finally{foreach(var process in existing)process.Dispose();}
            string dataRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"WindowsAdminMCP");
            var active=new Dictionary<string,TunnelSession>(StringComparer.Ordinal);
            var denied=new Dictionary<string,string>(StringComparer.Ordinal);
            var retryAfter=new Dictionary<string,DateTimeOffset>(StringComparer.Ordinal);
            TunReadiness? previous=null;
            bool configInvalidReported=false;
            try
            {
                while(true)
                {
                    if(workerTask.IsCompleted)return WorkerEnded(workerTask);
                    var dependency=TunDependency.Observe();
                    if(dependency!=previous)
                    {
                        Report(dependency==TunReadiness.READY?Code.TUN_READY:Code.WAITING_FOR_TUN,0,dependency:dependency);
                        previous=dependency;
                    }
                    bool serviceReady=false;
                    if(dependency==TunReadiness.READY)
                    {
                        try
                        {
                            var status=await AdminTools.Status(cancellationToken:CancellationToken.None);
                            if(status.Status==StatusCode.OK && status.ServicePid is int currentPid && status.ExecutionSid=="S-1-5-18")
                            {
                                using var service=Process.GetProcessById(currentPid);
                                serviceReady=string.Equals(service.MainModule?.FileName,frontend,StringComparison.OrdinalIgnoreCase);
                            }
                        }
                        catch(Exception e) when(e is Win32Exception or ArgumentException or InvalidOperationException or UnauthorizedAccessException) { }
                    }
                    if(!serviceReady)
                    {
                        foreach(var session in active.Values)await session.StopAsync();
                        if(active.Count!=0)Report(Code.TUN_LOST,0,dependency:dependency);
                        active.Clear();
                        await Task.Delay(TimeSpan.FromSeconds(5));
                        continue;
                    }

                    IReadOnlyList<TunnelProfiles.Profile> profiles;
                    try
                    {
                        stage=Code.CREDENTIAL_INVALID;
                        profiles=TunnelProfiles.Load(dataRoot,identity.User);
                        configInvalidReported=false;
                    }
                    catch(Exception e) when(e is IOException or UnauthorizedAccessException or CryptographicException or
                        JsonException or ArgumentException or InvalidDataException or System.Security.SecurityException)
                    {
                        if(!configInvalidReported)Report(Code.PROFILE_CONFIG_INVALID,2);
                        configInvalidReported=true;
                        await Task.Delay(TimeSpan.FromSeconds(5));
                        continue; // Keep already running healthy peers until configuration is repaired.
                    }
                    var configured=profiles.ToDictionary(p=>p.Id,StringComparer.Ordinal);
                    foreach(string id in denied.Keys.ToArray())if(!configured.ContainsKey(id))denied.Remove(id);
                    foreach(var (id,session) in active.ToArray())
                    {
                        if(!configured.TryGetValue(id,out var profile) || profile.Fingerprint!=session.Fingerprint)
                        {
                            await session.StopAsync();active.Remove(id);
                            Report(Code.PROFILE_STOPPED,0,profile:id);
                        }
                    }
                    foreach(var profile in profiles)
                    {
                        if(denied.TryGetValue(profile.Id,out var blocked) && blocked!=profile.Fingerprint)denied.Remove(profile.Id);
                        if(active.TryGetValue(profile.Id,out var session))
                        {
                            if(session.Child.Process.HasExited)
                            {
                                int exit=session.Child.Process.ExitCode;
                                Report(Code.PROFILE_EXITED,TunnelLifetime.UnexpectedExitCode(exit),vendorPid:session.Child.Process.Id,vendorExitCode:exit,profile:profile.Id);
                                await session.StopAsync();active.Remove(profile.Id);
                                retryAfter[profile.Id]=DateTimeOffset.UtcNow.AddSeconds(5);
                            }
                            else if(await TunnelLifetime.AccessDeniedAsync(profile.Port))
                            {
                                denied[profile.Id]=profile.Fingerprint;
                                Report(Code.CONTROL_PLANE_ACCESS_DENIED,0,vendorPid:session.Child.Process.Id,profile:profile.Id);
                                await session.StopAsync();active.Remove(profile.Id);
                            }
                        }
                        if(active.ContainsKey(profile.Id) || denied.ContainsKey(profile.Id) ||
                            (retryAfter.TryGetValue(profile.Id,out var retry) && retry>DateTimeOffset.UtcNow))continue;
                        ProcessStartInfo info=StartInfo(vendor,frontend,profile.Credential);
                        try
                        {
                            stage=Code.START_FAILED;
                            var child=NativeChildProcess.Start(info);
                            child.Input.Close();
                            active.Add(profile.Id,new TunnelSession(profile.Fingerprint,child));
                            Report(Code.STARTED,0,vendorPid:child.Process.Id,profile:profile.Id);
                        }
                        catch(Exception e) when(e is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
                        {
                            Report(Code.START_FAILED,5,e is Win32Exception w?w.NativeErrorCode:e.HResult,profile:profile.Id);
                            retryAfter[profile.Id]=DateTimeOffset.UtcNow.AddSeconds(10);
                        }
                        finally {info.Environment.Remove("CONTROL_PLANE_API_KEY");}
                    }
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
            finally {foreach(var session in active.Values)await session.StopAsync();}
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

    private sealed class TunnelSession
    {
        internal string Fingerprint { get; }
        internal NativeChildProcess Child { get; }
        private readonly Task drain;
        internal TunnelSession(string fingerprint,NativeChildProcess child)
        {
            Fingerprint=fingerprint;Child=child;
            drain=Task.WhenAll(Task.Run(()=>child.Output.CopyTo(Stream.Null)),
                Task.Run(()=>child.Error.CopyTo(Stream.Null)));
        }
        internal async Task StopAsync()
        {
            try
            {
                Child.StopTree();
                Child.Process.WaitForExit(5000);
                await drain.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch(Exception e) when(e is IOException or Win32Exception or InvalidOperationException or TimeoutException)
            { /* Job Object disposal is the final tree cleanup. */ }
            finally {Child.Dispose();}
        }
    }

    internal static void VerifyDirectory(string path,SecurityIdentifier? current)
    {
        var item=new DirectoryInfo(path);
        if((item.Attributes&FileAttributes.ReparsePoint)!=0)throw new UnauthorizedAccessException();
        VerifyAcl(item.GetAccessControl(),current);
    }
    internal static void VerifyFile(string path,SecurityIdentifier? current)
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
