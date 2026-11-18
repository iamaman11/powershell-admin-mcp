using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal sealed class AdminService(ILogger<AdminService> logger, JobCoordinator jobs) : BackgroundService
{
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe,out uint session);
    private static PipeSecurity Security()
    {
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
        return acl;
    }
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem) throw new InvalidOperationException("LocalSystem required.");
        jobs.Initialize();
        return base.StartAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Administrative service starting. PID {Pid}", Environment.ProcessId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task[] listeners = Enumerable.Range(0, 4).Select(_ => Listen(linked.Token)).Append(jobs.RunAsync(linked.Token)).ToArray();
        try
        {
            var completed = await Task.WhenAny(listeners);
            await completed;
            if (!stoppingToken.IsCancellationRequested) throw new InvalidOperationException("IPC listener stopped.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception e)
        {
            Environment.ExitCode = 1;
            logger.LogCritical("IPC listener failed: {ErrorType}", e.GetType().Name);
            throw;
        }
        finally
        {
            await linked.CancelAsync();
            try { await Task.WhenAll(listeners); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
    }
    private async Task Listen(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            using var pipe = NamedPipeServerStreamAcl.Create(ServiceIdentity.Pipe, PipeDirection.InOut, 4,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, Security());
            string correlation = Guid.NewGuid().ToString("N");
            try
            {
                await pipe.WaitForConnectionAsync(stop);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                var clock=Stopwatch.StartNew();
                string input = await PipeWire.ReadFrameAsync(pipe, deadline.Token);
                var request = JsonSerializer.Deserialize<AdminRequest>(input, PipeWire.Json);
                if(request?.TraceId is {} trace && Guid.TryParseExact(trace,"N",out _))correlation=trace;
                bool allowed = false;
                string? callerSid=null, logonSid=null;
                pipe.RunAsClient(() =>
                {
                    using var token = WindowsIdentity.GetCurrent(true);
                    allowed = token is not null && (token.IsSystem ||
                        new WindowsPrincipal(token).IsInRole(WindowsBuiltInRole.Administrator));
                    if(allowed && token is not null)
                    {
                        callerSid=token.User?.Value;
                        // Use only identity metadata, not impersonation for execution.
                        logonSid=token.Groups?.OfType<SecurityIdentifier>()
                            .FirstOrDefault(s=>s.Value.StartsWith("S-1-5-5-",StringComparison.Ordinal))?.Value;
                    }
                });
                UserSession? user=null;
                if(allowed && callerSid is not null && callerSid!="S-1-5-18" && logonSid is not null &&
                    GetNamedPipeClientSessionId(pipe.SafePipeHandle,out uint clientSession) && clientSession is >0 and <=int.MaxValue)
                    user=new(callerSid,(int)clientSession,logonSid);
                AdminResponse response;
                if (!allowed) response = new(StatusCode.ACCESS_DENIED, correlation);
                else
                {
                    if (request?.Operation == AdminOperation.Job && request.Worker is null)
                    {
                        try
                        {
                            var result = jobs.Dispatch(request, correlation,callerSid,user);
                            response = new(result.Status, correlation, Job: result);
                        }
                        catch(Microsoft.Data.Sqlite.SqliteException e)
                        {
                            var code=request.Action==JobAction.submit?StatusCode.ADMISSION_UNCONFIRMED:StatusCode.STORE_UNAVAILABLE;
                            logger.LogWarning("Store request failed {Correlation} code {Code}",correlation,e.SqliteErrorCode);
                            response=new(code,correlation,Job:new(code,correlation,OperationKey:request.Key,NativeError:e.SqliteErrorCode));
                        }
                    }
                    else if(request?.Operation==AdminOperation.UserWorker && request.Worker is {} worker &&
                        request.Action is null && request.ExecutionContext is null && request.Key is null && request.JobId is null &&
                        request.Command is null && request.Steps is null && request.WorkingDirectory is null &&
                        request.StepIndex is null && request.ExpectedCompletedSteps is null && request.StateFilter is null &&
                        request.PageSize is null && request.BeforeSeq is null && request.Decision is null &&
                        request.Evidence is null && request.RetainDays is null && !request.IncludeInterruptedCommand && request.ResourceKey is null && user is not null)
                    {
                        try
                        {
                            var result=jobs.DispatchWorker(worker,user);
                            response=new(result.Status,correlation,Worker:result);
                        }
                        catch(Microsoft.Data.Sqlite.SqliteException e)
                        {
                            logger.LogWarning("Worker store request failed {Correlation} code {Code}",correlation,e.SqliteErrorCode);
                            response=new(StatusCode.STORE_UNAVAILABLE,correlation,Worker:new(StatusCode.STORE_UNAVAILABLE));
                        }
                    }
                    else if (request?.Operation is (AdminOperation.Status or AdminOperation.Diagnostics) && request.Action is null &&
                        request.Command is null && request.Key is null && request.JobId is null &&
                        request.Steps is null && request.StepIndex is null && request.ExpectedCompletedSteps is null &&
                        request.StateFilter is null && request.PageSize is null && request.BeforeSeq is null &&
                        request.Decision is null && request.Evidence is null && request.RetainDays is null &&
                        request.ExecutionContext is null && request.Worker is null && !request.IncludeInterruptedCommand && request.ResourceKey is null)
                    {
                        using var actual = WindowsIdentity.GetCurrent();
                        var status = new AdminStatus(StatusCode.OK, correlation, Environment.ProcessId, Environment.MachineName,
                            actual.Name, actual.User?.Value, actual.IsSystem, started,
                            UserContextAvailable:jobs.UserAvailable(user),Versions:BuildIdentity.Observe());
                        try
                        {
                            response = new(StatusCode.OK, correlation, Info: status,
                                Store:request.Operation==AdminOperation.Diagnostics?jobs.Summary():null);
                        }
                        catch(Microsoft.Data.Sqlite.SqliteException e)
                        {
                            logger.LogWarning("Diagnostic store request failed {Correlation} code {Code}",correlation,e.SqliteErrorCode);
                            response=new(StatusCode.STORE_UNAVAILABLE,correlation,Info:status);
                        }
                    }
                    else response = new(StatusCode.IPC_PROTOCOL_ERROR, correlation);
                }
                response=response with {ServiceElapsedMs=clock.ElapsedMilliseconds};
                await PipeWire.WriteFrameAsync(pipe, JsonSerializer.Serialize(response, PipeWire.Json), deadline.Token);
                if(clock.ElapsedMilliseconds>250)
                    logger.LogWarning("Slow IPC {Correlation} status {Status} elapsed_ms {Elapsed}",correlation,response.Code,clock.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception e) when (e is IOException or OperationCanceledException or JsonException
                or DecoderFallbackException or UnauthorizedAccessException)
            {
                logger.LogDebug("IPC request ended. Correlation {Correlation} type {ErrorType}",
                    correlation, e.GetType().Name);
            }
        }
    }
}
