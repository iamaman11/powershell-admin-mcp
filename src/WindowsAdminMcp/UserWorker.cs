using System.Security.Principal;
using System.Diagnostics;

 // Runs within the already-authorized interactive tunnel host. No new process
 // identity, credential access, token duplication or additional network listener.
internal static class UserWorker
{
    private const int WatchdogMs=12000;
    private static readonly TimeSpan PulseInterval=TimeSpan.FromMilliseconds(1500);

    internal static async Task RunAsync(CancellationToken stop)
    {
        using var identity=WindowsIdentity.GetCurrent();
        using var process=Process.GetCurrentProcess();
        if(identity.IsSystem || process.SessionId==0 || !Environment.UserInteractive ||
            !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))return;
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(stop);
        var running=new HashSet<Task>();
        try
        {
            while(!stop.IsCancellationRequested)
            {
                foreach(var done in running.Where(x=>x.IsCompleted).ToArray())
                {
                    running.Remove(done);
                    await done;
                }
                long sent=Environment.TickCount64;
                var reply=await Request(new(WorkerAction.poll),stop);
                if(reply.Code==StatusCode.OK && reply.Worker?.Work is {} work)
                    running.Add(Task.Run(()=>RunIsolatedAsync(
                        ()=>ExecuteAsync(work,sent,Request,PowerShellRunner.RunAsync,lifetime.Token,PulseInterval),
                        e=>Report(work,"USER_JOB_FAULT",error:e))));
                else
                    await Task.Delay(500,stop);
            }
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested){}
        finally
        {
            await lifetime.CancelAsync();
            try { await Task.WhenAll(running); }
            catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        }
    }

    // Contain a single job's failure; the coordinator still owns its durable
    // outcome and will reconcile any lease for which no result was committed.
    internal static async Task RunIsolatedAsync(Func<Task> execute,Action<Exception> report)
    {
        try { await execute(); }
        catch(Exception e) when(e is not OutOfMemoryException)
        { report(e); }
    }

    private static Task<AdminResponse> Request(WorkerRequest request,CancellationToken token)=>
        AdminTools.Call(new(AdminOperation.UserWorker,Worker:request),token);

    private static void Report(UserWork work,string code,AdminResponse? reply=null,Exception? error=null)
    {
        // No command, output, exception message or credential in Event Log.
        try
        {
            EventLog.WriteEntry("WindowsAdminMCP",
                $"{code} job_id={work.JobId} step={work.Step} status={reply?.Code} correlation_id={reply?.Correlation} error_type={error?.GetType().Name} hresult={error?.HResult}",
                EventLogEntryType.Warning);
        }
        catch(Exception e) when(e is System.Security.SecurityException or System.ComponentModel.Win32Exception or
            InvalidOperationException or UnauthorizedAccessException or ArgumentException) { }
    }

    internal static bool IsTransient(StatusCode code)=>code is
        StatusCode.IPC_TIMEOUT or StatusCode.IPC_FAILED or StatusCode.STORE_UNAVAILABLE;

    internal static long RemainingAuthorityMs(long requestSent,long now)=>
        Math.Max(0,WatchdogMs-(now-requestSent));

    internal static async Task ExecuteAsync(UserWork work,long claimSent,
        Func<WorkerRequest,CancellationToken,Task<AdminResponse>> request,
        Func<string,int,string?,CancellationToken,Task<ExecutionResult>> run,
        CancellationToken stop,TimeSpan pulseInterval)
    {
        using var cancel=new CancellationTokenSource();
        using var watchdog=new CancellationTokenSource();
        int observedSignal=0;
        int transientFailures=0;
        StatusCode? lastFailure=null;
        string? failureCorrelation=null;
        long confirmedAt=claimSent;
        void Signal(WorkerStopSignal signal)
        {
            Interlocked.CompareExchange(ref observedSignal,(int)signal,0);
            cancel.Cancel();
        }
        using var watchdogRegistration=watchdog.Token.Register(()=>Signal(WorkerStopSignal.WATCHDOG_EXPIRED));
        using var stopRegistration=stop.Register(()=>Signal(WorkerStopSignal.WORKER_STOPPING));
        void Renew(long requestSent)
        {
            long remaining=RemainingAuthorityMs(requestSent,Environment.TickCount64);
            if(remaining==0)Signal(WorkerStopSignal.WATCHDOG_EXPIRED);
            else if(!cancel.IsCancellationRequested)
            {
                confirmedAt=requestSent;
                watchdog.CancelAfter(TimeSpan.FromMilliseconds(remaining));
            }
        }
        // Start the conservative local authority window at request dispatch,
        // not response arrival. A delayed acknowledgement cannot create extra authority.
        Renew(claimSent);
        string cwd=work.Cwd.Length==0?Environment.GetFolderPath(Environment.SpecialFolder.UserProfile):work.Cwd;
        var running=run(work.Command,work.TimeoutSeconds,cwd,cancel.Token);
        try
        {
            while(!running.IsCompleted && !cancel.IsCancellationRequested)
            {
                await Task.WhenAny(running,Task.Delay(pulseInterval,stop));
                if(running.IsCompleted || cancel.IsCancellationRequested)break;
                stop.ThrowIfCancellationRequested();
                long sent=Environment.TickCount64;
                if(RemainingAuthorityMs(confirmedAt,sent)==0)
                {
                    Signal(WorkerStopSignal.WATCHDOG_EXPIRED);
                    break;
                }
                var pulse=await request(new(WorkerAction.heartbeat,work.LeaseId),stop);
                // Do not revive a cancelled or expired local authority window.
                if(RemainingAuthorityMs(confirmedAt,Environment.TickCount64)==0)
                    Signal(WorkerStopSignal.WATCHDOG_EXPIRED);
                if(pulse.Code==StatusCode.OK && pulse.Worker is {Status:StatusCode.OK} confirmed)
                {
                    if(confirmed.CancelRequested)Signal(WorkerStopSignal.OPERATOR_CANCELLED);
                    else if(!cancel.IsCancellationRequested)Renew(sent);
                }
                else
                {
                    lastFailure=pulse.Code==StatusCode.OK?StatusCode.IPC_PROTOCOL_ERROR:pulse.Code;
                    failureCorrelation=pulse.Correlation;
                    if(IsTransient(lastFailure.Value))
                        transientFailures++; // bounded by the existing watchdog; no extension
                    else
                        Signal(lastFailure==StatusCode.LEASE_LOST?WorkerStopSignal.LEASE_LOST:WorkerStopSignal.IPC_REJECTED);
                }
            }
            var result=await running;
            // This is observed worker evidence, not an assertion of the external effect.
            var signal=(WorkerStopSignal)Volatile.Read(ref observedSignal);
            if(signal!=WorkerStopSignal.NONE || lastFailure is not null)
                result=result with {WorkerEvidence=new(signal,transientFailures,lastFailure,failureCorrelation)};
            if(!stop.IsCancellationRequested)
            {
                var ack=await request(new(WorkerAction.complete,work.LeaseId,result),stop);
                if(ack.Code!=StatusCode.OK || ack.Worker is not {Status:StatusCode.OK})
                    Report(work,"USER_COMPLETION_UNCONFIRMED",ack);
                // Durable completion readback/idempotent retry is a separate follow-up.
                // Never execute the script again after an unconfirmed acknowledgement.
            }
        }
        finally
        {
            cancel.Cancel();
            await running;
        }
    }
}
