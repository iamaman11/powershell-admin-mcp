using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal sealed class AdminService(ILogger<AdminService> logger, JobCoordinator jobs) : BackgroundService
{
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
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
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem) throw new InvalidOperationException("LocalSystem required.");
        jobs.Initialize();
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
                string input = await PipeWire.ReadFrameAsync(pipe, deadline.Token);
                bool allowed = false;
                pipe.RunAsClient(() =>
                {
                    using var token = WindowsIdentity.GetCurrent(true);
                    allowed = token is not null && (token.IsSystem ||
                        new WindowsPrincipal(token).IsInRole(WindowsBuiltInRole.Administrator));
                });
                AdminResponse response;
                if (!allowed) response = new(StatusCode.ACCESS_DENIED, correlation);
                else
                {
                    var request = JsonSerializer.Deserialize<AdminRequest>(input, PipeWire.Json);
                    if (request?.Operation == AdminOperation.Job)
                    {
                        var result = jobs.Dispatch(request, correlation);
                        response = new(result.Status, correlation, Job: result);
                    }
                    else if (request?.Operation == AdminOperation.Status && request.Action is null &&
                        request.Command is null && request.Key is null && request.JobId is null)
                    {
                        using var actual = WindowsIdentity.GetCurrent();
                        var status = new AdminStatus(StatusCode.OK, correlation, Environment.ProcessId, Environment.MachineName,
                            actual.Name, actual.User?.Value, actual.IsSystem, started);
                        response = new(StatusCode.OK, correlation, Info: status);
                    }
                    else response = new(StatusCode.IPC_PROTOCOL_ERROR, correlation);
                }
                await PipeWire.WriteFrameAsync(pipe, JsonSerializer.Serialize(response, PipeWire.Json), deadline.Token);
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
