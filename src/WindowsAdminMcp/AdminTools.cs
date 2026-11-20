using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;

[McpServerToolType]
public sealed class AdminTools
{
    internal static async Task<AdminResponse> Call(AdminRequest request,CancellationToken ct)
    {
        string correlation=Guid.NewGuid().ToString("N");
        var started=DateTimeOffset.UtcNow;
        var clock=Stopwatch.StartNew();
        long? scm=null,ipc=null;
        long ipcStarted=0;
        bool sent=false;
        ToolTiming Timing(AdminResponse? response=null) => (response?.Job?.Timing??new ToolTiming()) with
        {
            StartedUtc=started,ToolMs=clock.ElapsedMilliseconds,ScmMs=scm,
            IpcMs=ipc??(sent?clock.ElapsedMilliseconds-ipcStarted:null),
            ServiceMs=response?.ServiceElapsedMs,IpcCalls=sent?1:0
        };
        AdminResponse Fail(StatusCode code,int? native=null)
        {
            if(sent && request.Action==JobAction.submit) code=StatusCode.ADMISSION_UNCONFIRMED;
            var timing=Timing();
            return new(code,correlation,new AdminStatus(code,correlation,NativeError:native,Timing:timing),
                new JobReply(code,correlation,OperationKey:request.Key,NativeError:native,Timing:timing));
        }
        try
        {
            uint pid=ServiceIdentity.RunningProcessId();
            scm=clock.ElapsedMilliseconds;
            string payload=JsonSerializer.Serialize(request with {TraceId=correlation},PipeWire.Json);
            ipcStarted=clock.ElapsedMilliseconds;sent=true;
            string raw=await PipeWire.RoundTripAsync(ServiceIdentity.Pipe,payload,pid,ct);
            ipc=clock.ElapsedMilliseconds-ipcStarted;
            var reply=JsonSerializer.Deserialize<AdminResponse>(raw,PipeWire.Json);
            if(reply is null || reply.Correlation!=correlation ||
                (reply.Info?.Status==StatusCode.OK && reply.Info.ServicePid!=pid))
                return Fail(StatusCode.IPC_PROTOCOL_ERROR);
            var timing=Timing(reply);
            return reply with {Info=reply.Info is {} info?info with {Timing=timing}:null,
                Job=reply.Job is {} job?job with {Timing=timing}:null};
        }
        catch(Win32Exception e){return Fail(e.NativeErrorCode==5?StatusCode.ACCESS_DENIED:StatusCode.SERVICE_UNAVAILABLE,e.NativeErrorCode);}
        catch(UnauthorizedAccessException){return Fail(StatusCode.ACCESS_DENIED);}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return Fail(StatusCode.IPC_TIMEOUT);}
        catch(JsonException){return Fail(StatusCode.IPC_PROTOCOL_ERROR);}
        catch(DecoderFallbackException){return Fail(StatusCode.IPC_PROTOCOL_ERROR);}
        catch(IOException e){return Fail(StatusCode.IPC_FAILED,e.HResult);}
    }

    private static CommandContext DefaultContext()
    {
        using var identity=WindowsIdentity.GetCurrent();
        return identity.IsSystem?CommandContext.SYSTEM:CommandContext.USER;
    }

    internal static Task<AdminResponse> Diagnostics(CancellationToken ct) => Call(new(AdminOperation.Diagnostics),ct);

    [McpServerTool(Name="powershell_status",UseStructuredContent=true),
        Description("Service identity.")]
    public static async Task<AdminStatus> Status(CancellationToken cancellationToken)
    {
        var reply=await Call(new(AdminOperation.Status),cancellationToken);
        var status=reply.Info??new(reply.Code,reply.Correlation);
        using var identity=WindowsIdentity.GetCurrent();
        using var process=Process.GetCurrentProcess();
        var frontend=new FrontendContext(process.Id,identity.Name,identity.User?.Value,process.SessionId,
            new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        // The service owns queue/identity checks. A user frontend is not proof
        // that a matching user-side runner is currently available.
        return status with {Frontend=frontend,ExecutionContext=status.Status!=StatusCode.OK?CommandContext.UNCONFIRMED:
            identity.IsSystem?CommandContext.SYSTEM:
            status.UserContextAvailable==true?CommandContext.USER:CommandContext.UNCONFIRMED};
    }

    [McpServerTool(Name="powershell_job",UseStructuredContent=true),
        Description("Durable PS jobs; no replay.")]
    public static async Task<JobReply> Job(JobAction action,string? operation_key=null,string? job_id=null,
        string? command=null,int timeout_seconds=30,string? working_directory=null,JobStep[]? steps=null,
        [Description("Logs: 1-based step.")] int? step_index=null,int? expected_completed_steps=null,
        JobState? state_filter=null,int? page_size=null,long? before_seq=null,
        ReconciliationDecision? decision=null,string? evidence=null,int? retain_days=null,
        [Description("USER default; SYSTEM explicit.")] CommandContext? execution_context=null,
        [Description("Logs require authorization.")] bool include_interrupted_command=false,
        [Description("Serialize jobs; * exclusive.")] string? resource_key=null,
        CancellationToken cancellationToken=default)
    {
        var reply=await Call(new(AdminOperation.Job,action,operation_key,job_id,command,timeout_seconds,working_directory,steps,step_index,expected_completed_steps,state_filter,page_size,before_seq,decision,evidence,retain_days,
            execution_context??(action==JobAction.submit?DefaultContext():null),IncludeInterruptedCommand:include_interrupted_command,ResourceKey:resource_key),cancellationToken);
        return reply.Job??new(reply.Code,reply.Correlation,OperationKey:operation_key);
    }

    [McpServerTool(Name="powershell_screenshot"),
        Description("Desktop PNG capture.")]
    public static async Task<CallToolResult> Screenshot(int monitor=0,int x=0,int y=0,int? width=null,int? height=null,
        int max_width=1280,CancellationToken cancellationToken=default)
    {
        string correlation=Guid.NewGuid().ToString("N");
        var clock=Stopwatch.StartNew();
        var started=DateTimeOffset.UtcNow;
        if(!DesktopCapture.ValidArguments(monitor,x,y,width,height,max_width))
            return DesktopCapture.Error(CaptureCode.INVALID_ARGUMENT,correlation,timing:new(StartedUtc:started,ToolMs:clock.ElapsedMilliseconds));
        var status=await Status(cancellationToken);
        if(status.Status!=StatusCode.OK)
            return DesktopCapture.Error(CaptureCode.SERVICE_CHECK_FAILED,correlation,status.Status,
                timing:(status.Timing??new()) with {StartedUtc=started,ToolMs=clock.ElapsedMilliseconds},serviceCorrelation:status.CorrelationId);
        cancellationToken.ThrowIfCancellationRequested();
        return DesktopCapture.Capture(monitor,x,y,width,height,max_width,correlation,
            (status.Timing??new()) with {StartedUtc=started,ToolMs=clock.ElapsedMilliseconds},status.CorrelationId);
    }

    internal static TimeSpan ExecWaitBudget(int executionSeconds) => TimeSpan.FromSeconds(Math.Min(executionSeconds + 5, 90));

    [McpServerTool(Name="powershell_exec",UseStructuredContent=true),
        Description("PS step; USER default; poll PENDING.")]
    public static async Task<ExecutionResult> Exec(string command,int timeout_seconds=30,
        string? working_directory=null,string? operation_key=null,CommandContext? execution_context=null,string? resource_key=null,CancellationToken cancellationToken=default)
    {
        var clock=Stopwatch.StartNew();
        var started=DateTimeOffset.UtcNow;
        long scmMs=0,ipcMs=0,serviceMs=0,storeMs=0,storeWaitMs=0;
        int ipcCalls=0;
        void Observe(ToolTiming? timing)
        {
            if(timing is null)return;
            scmMs+=timing.ScmMs??0;ipcMs+=timing.IpcMs??0;serviceMs+=timing.ServiceMs??0;
            storeMs+=timing.StoreMs??0;storeWaitMs+=timing.StoreWaitMs??0;ipcCalls+=timing.IpcCalls??0;
        }
        ExecutionResult Finish(ExecutionResult result)=>result with
        {Timing=new(started,clock.ElapsedMilliseconds,scmMs,ipcMs,serviceMs,storeWaitMs,storeMs,IpcCalls:ipcCalls)};
        string hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command))).ToLowerInvariant();
        operation_key??=Guid.NewGuid().ToString("N");
        if(timeout_seconds is <1 or >120)
            return Finish(new(ExecutionState.INVALID_ARGUMENT,hash,clock.ElapsedMilliseconds,OperationKey:operation_key));
        var admitted=await Job(JobAction.submit,operation_key,command:command,timeout_seconds:timeout_seconds,
            working_directory:working_directory,execution_context:execution_context,resource_key:resource_key,cancellationToken:cancellationToken);
        Observe(admitted.Timing);
        if(admitted.Status!=StatusCode.OK || admitted.Job is null)
        {
            var error=Enum.TryParse<ExecutionState>(admitted.Status.ToString(),out var parsed)?parsed:ExecutionState.IPC_PROTOCOL_ERROR;
            return Finish(new(error,hash,clock.ElapsedMilliseconds,Stage:"ADMISSION",NativeError:admitted.NativeError,
                OperationKey:operation_key,CorrelationId:admitted.CorrelationId));
        }
        var job=admitted.Job;
        ExecutionResult Pending(string stage,string correlation) => Finish(new(ExecutionState.PENDING,hash,clock.ElapsedMilliseconds,
            Stage:stage,JobId:job.JobId,OperationKey:operation_key,CorrelationId:correlation));
        var remaining=ExecWaitBudget(timeout_seconds)-clock.Elapsed;
        if(remaining<=TimeSpan.Zero)return Pending("SYNCHRONOUS_WAIT_LIMIT",admitted.CorrelationId);
        using var waitDeadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitDeadline.CancelAfter(remaining);
        try
        {
            while(true)
            {
                var read=await Job(JobAction.logs,job_id:job.JobId,cancellationToken:waitDeadline.Token);
                Observe(read.Timing);
                if(read.Status==StatusCode.HISTORY_PRUNED)
                    return Finish(new(ExecutionState.HISTORY_PRUNED,hash,clock.ElapsedMilliseconds,Stage:"HISTORY_PRUNED",
                        JobId:job.JobId,OperationKey:operation_key,CorrelationId:read.CorrelationId));
                if(read.Status==StatusCode.OK && read.Job is {} current)
                {
                    if(current.Execution is {} result) return Finish(result with {JobId=job.JobId,OperationKey=operation_key,
                        CorrelationId=read.CorrelationId});
                    if(current.State is JobState.CANCELLED or JobState.NEEDS_RECONCILIATION or JobState.RECONCILED or JobState.FAILED)
                        return Finish(new(current.State==JobState.CANCELLED?ExecutionState.CANCELLED:
                            current.State==JobState.RECONCILED?ExecutionState.RECONCILED:ExecutionState.NEEDS_RECONCILIATION,
                            hash,clock.ElapsedMilliseconds,JobId:job.JobId,OperationKey:operation_key,CorrelationId:read.CorrelationId));
                }
                else return Pending(read.Status.ToString(),read.CorrelationId);
                await Task.Delay(500,waitDeadline.Token);
            }
        }
        catch(OperationCanceledException) when(waitDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Pending("SYNCHRONOUS_WAIT_LIMIT",admitted.CorrelationId);
        }
    }
}
