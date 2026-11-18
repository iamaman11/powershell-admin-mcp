using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;

[McpServerToolType]
public sealed class AdminTools
{
    private static async Task<AdminResponse> Call(AdminRequest request,CancellationToken ct)
    {
        string correlation=Guid.NewGuid().ToString("N");
        bool sent=false;
        AdminResponse Fail(StatusCode code,int? native=null)
        {
            if(sent && request.Action==JobAction.submit) code=StatusCode.ADMISSION_UNCONFIRMED;
            return new(code,correlation,new AdminStatus(code,correlation,NativeError:native),
                new JobReply(code,correlation,OperationKey:request.Key,NativeError:native));
        }
        try
        {
            uint pid=ServiceIdentity.RunningProcessId();
            string payload=JsonSerializer.Serialize(request,PipeWire.Json);
            sent=true;
            string raw=await PipeWire.RoundTripAsync(ServiceIdentity.Pipe,payload,pid,ct);
            var reply=JsonSerializer.Deserialize<AdminResponse>(raw,PipeWire.Json);
            if(reply is null || string.IsNullOrWhiteSpace(reply.Correlation) ||
                (reply.Info?.Status==StatusCode.OK && reply.Info.ServicePid!=pid))
                return Fail(StatusCode.IPC_PROTOCOL_ERROR);
            return reply;
        }
        catch(Win32Exception e){return Fail(e.NativeErrorCode==5?StatusCode.ACCESS_DENIED:StatusCode.SERVICE_UNAVAILABLE,e.NativeErrorCode);}
        catch(UnauthorizedAccessException){return Fail(StatusCode.ACCESS_DENIED);}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return Fail(StatusCode.IPC_TIMEOUT);}
        catch(JsonException){return Fail(StatusCode.IPC_PROTOCOL_ERROR);}
        catch(DecoderFallbackException){return Fail(StatusCode.IPC_PROTOCOL_ERROR);}
        catch(IOException e){return Fail(StatusCode.IPC_FAILED,e.HResult);}
    }

    internal static Task<AdminResponse> Diagnostics(CancellationToken ct) => Call(new(AdminOperation.Diagnostics),ct);

    [McpServerTool(Name="powershell_status",UseStructuredContent=true),
        Description("Read actual Windows service status and execution identity.")]
    public static async Task<AdminStatus> Status(CancellationToken cancellationToken)
    {
        var reply=await Call(new(AdminOperation.Status),cancellationToken);
        return reply.Info??new(reply.Code,reply.Correlation);
    }

    [McpServerTool(Name="powershell_job",UseStructuredContent=true),
        Description("Durably submit one script OR 1-8 sequential steps (each command, timeout_seconds, pause_after, reboot_after, verify_after_reboot). A reboot_after step must finish successfully before an external restart; the next step must explicitly declare verify_after_reboot and fail if the postcondition is absent. A verified new full boot releases only the next step; the tool never restarts Windows itself. Inspect status by job_id or operation_key, list jobs with state_filter, page_size (1-100, default 20) and before_seq (use next_before_seq from previous page), read a step's bounded logs (step_index is 1-based, 1..total_steps), cancel, or resume a paused checkpoint with expected_completed_steps. Submit requires stable operation_key; retry only same key and payload. Resume never replays an interrupted step. A manual pause does not verify an OS reboot.")]
    public static async Task<JobReply> Job(JobAction action,string? operation_key=null,string? job_id=null,
        string? command=null,int timeout_seconds=30,string? working_directory=null,JobStep[]? steps=null,
        [Description("One-based step number, 1..total_steps; logs only.")] int? step_index=null,int? expected_completed_steps=null,
        JobState? state_filter=null,int? page_size=null,long? before_seq=null,CancellationToken cancellationToken=default)
    {
        var reply=await Call(new(AdminOperation.Job,action,operation_key,job_id,command,timeout_seconds,working_directory,steps,step_index,expected_completed_steps,state_filter,page_size,before_seq),cancellationToken);
        return reply.Job??new(reply.Code,reply.Correlation,OperationKey:operation_key);
    }

    [McpServerTool(Name="powershell_screenshot"),
        Description("Capture the current authorized interactive physical console desktop. Returns PNG directly; never unlocks or switches sessions. Screen may contain sensitive data.")]
    public static async Task<CallToolResult> Screenshot(int monitor=0,int x=0,int y=0,int? width=null,int? height=null,
        int max_width=1280,CancellationToken cancellationToken=default)
    {
        string correlation=Guid.NewGuid().ToString("N");
        if(!DesktopCapture.ValidArguments(monitor,x,y,width,height,max_width))
            return DesktopCapture.Error(CaptureCode.INVALID_ARGUMENT,correlation);
        var status=await Status(cancellationToken);
        if(status.Status!=StatusCode.OK)
            return DesktopCapture.Error(CaptureCode.SERVICE_CHECK_FAILED,correlation,status.Status);
        cancellationToken.ThrowIfCancellationRequested();
        return DesktopCapture.Capture(monitor,x,y,width,height,max_width,correlation);
    }

    internal static TimeSpan ExecWaitBudget(int executionSeconds) => TimeSpan.FromSeconds(Math.Min(executionSeconds + 5, 90));

    [McpServerTool(Name="powershell_exec",UseStructuredContent=true),
        Description("Execute a short script using the same durable queue (timeout_seconds 1..120 is the execution deadline, not the client wait). The synchronous wait is at most 90 seconds from tool entry, leaving a 30-second transport reserve. PENDING includes the admitted job_id; inspect it without replay. audit_persisted means a step result has been durably saved, not merely admitted. Disconnect does not cancel an admitted job.")]
    public static async Task<ExecutionResult> Exec(string command,int timeout_seconds=30,
        string? working_directory=null,string? operation_key=null,CancellationToken cancellationToken=default)
    {
        var clock=Stopwatch.StartNew();
        string hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command))).ToLowerInvariant();
        operation_key??=Guid.NewGuid().ToString("N");
        if(timeout_seconds is <1 or >120)
            return new(ExecutionState.INVALID_ARGUMENT,hash,clock.ElapsedMilliseconds,OperationKey:operation_key);
        var admitted=await Job(JobAction.submit,operation_key,command:command,timeout_seconds:timeout_seconds,
            working_directory:working_directory,cancellationToken:cancellationToken);
        if(admitted.Status!=StatusCode.OK || admitted.Job is null)
        {
            var error=Enum.TryParse<ExecutionState>(admitted.Status.ToString(),out var parsed)?parsed:ExecutionState.IPC_PROTOCOL_ERROR;
            return new(error,hash,clock.ElapsedMilliseconds,Stage:"ADMISSION",NativeError:admitted.NativeError,
                OperationKey:operation_key,CorrelationId:admitted.CorrelationId);
        }
        var job=admitted.Job;
        ExecutionResult Pending(string stage,string correlation) => new(ExecutionState.PENDING,hash,clock.ElapsedMilliseconds,
            Stage:stage,JobId:job.JobId,OperationKey:operation_key,CorrelationId:correlation);
        var remaining=ExecWaitBudget(timeout_seconds)-clock.Elapsed;
        if(remaining<=TimeSpan.Zero)return Pending("SYNCHRONOUS_WAIT_LIMIT",admitted.CorrelationId);
        using var waitDeadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitDeadline.CancelAfter(remaining);
        try
        {
            while(true)
            {
                var read=await Job(JobAction.logs,job_id:job.JobId,cancellationToken:waitDeadline.Token);
                if(read.Status==StatusCode.OK && read.Job is {} current)
                {
                    if(current.Execution is {} result) return result with {JobId=job.JobId,OperationKey=operation_key,
                        CorrelationId=read.CorrelationId};
                    if(current.State is JobState.CANCELLED or JobState.NEEDS_RECONCILIATION or JobState.FAILED)
                        return new(current.State==JobState.CANCELLED?ExecutionState.CANCELLED:ExecutionState.NEEDS_RECONCILIATION,
                            hash,clock.ElapsedMilliseconds,JobId:job.JobId,OperationKey:operation_key,CorrelationId:read.CorrelationId);
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
