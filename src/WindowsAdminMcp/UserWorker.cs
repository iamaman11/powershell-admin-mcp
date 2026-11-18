using System.Security.Principal;
using System.Diagnostics;

 // Runs within the already-authorized interactive tunnel host. No new process
 // identity, credential access, token duplication or additional network listener.
internal static class UserWorker
{
    internal static async Task RunAsync(CancellationToken stop)
    {
        using var identity=WindowsIdentity.GetCurrent();
        using var process=Process.GetCurrentProcess();
        if(identity.IsSystem || process.SessionId==0 || !Environment.UserInteractive ||
            !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))return;
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var reply=await Request(new(WorkerAction.poll),stop);
                if(reply.Code==StatusCode.OK && reply.Worker?.Work is {} work)
                    await Execute(work,stop);
                else
                    await Task.Delay(1000,stop);
            }
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested){}
    }

    private static Task<AdminResponse> Request(WorkerRequest request,CancellationToken token)=>
        AdminTools.Call(new(AdminOperation.UserWorker,Worker:request),token);

    private static async Task Execute(UserWork work,CancellationToken stop)
    {
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(stop);
        // A wedged or disconnected frontend cannot keep executing past its local lease watchdog.
        cancel.CancelAfter(TimeSpan.FromSeconds(12));
        string cwd=work.Cwd.Length==0?Environment.GetFolderPath(Environment.SpecialFolder.UserProfile):work.Cwd;
        var running=PowerShellRunner.RunAsync(work.Command,work.TimeoutSeconds,cwd,cancel.Token);
        try
        {
            while(!running.IsCompleted)
            {
                await Task.WhenAny(running,Task.Delay(1500,stop));
                if(running.IsCompleted)break;
                stop.ThrowIfCancellationRequested();
                var pulse=await Request(new(WorkerAction.heartbeat,work.LeaseId),stop);
                if(pulse.Code!=StatusCode.OK || pulse.Worker is null || pulse.Worker.CancelRequested)
                    cancel.Cancel();
                else if(!cancel.IsCancellationRequested)cancel.CancelAfter(TimeSpan.FromSeconds(12));
            }
            var result=await running;
            // An unconfirmed response must never execute this work again. The
            // coordinator either saved the result or will reconcile lost work.
            if(!stop.IsCancellationRequested)
                await Request(new(WorkerAction.complete,work.LeaseId,result),stop);
        }
        finally
        {
            cancel.Cancel();
            await running;
        }
    }
}
