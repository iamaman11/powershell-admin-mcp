using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

[JsonConverter(typeof(JsonStringEnumConverter<NativeAdminAction>))]
public enum NativeAdminAction
{
    SERVICE_START, SERVICE_STOP, SERVICE_PAUSE, SERVICE_CONTINUE,
    TASK_ENABLE, TASK_DISABLE
}

[JsonConverter(typeof(JsonStringEnumConverter<NativeAdminState>))]
public enum NativeAdminState
{
    STOPPED, RUNNING, PAUSED, ENABLED, DISABLED
}

[JsonConverter(typeof(JsonStringEnumConverter<NativeAdminCode>))]
public enum NativeAdminCode
{
    COMMITTED, INVALID_ARGUMENT, NOT_FOUND, ACCESS_DENIED, USER_CONTEXT_UNAVAILABLE,
    SERVICE_UNAVAILABLE, PRECONDITION_FAILED, UNSAFE_TARGET, ACTION_FAILED,
    EFFECT_UNKNOWN, CONFLICT, STORE_FULL, UNSUPPORTED_PLATFORM
}

public sealed record NativeAdminResult(
    [property:JsonPropertyName("status")] NativeAdminCode Status,
    [property:JsonPropertyName("operation_id")] string OperationId,
    [property:JsonPropertyName("action")] NativeAdminAction Action,
    [property:JsonPropertyName("target")] string Target,
    [property:JsonPropertyName("effect_state")] WindowEffectState EffectState = WindowEffectState.NO_EFFECT,
    [property:JsonPropertyName("stage")] string Stage = "VALIDATE",
    [property:JsonPropertyName("observed_utc")] DateTimeOffset? ObservedUtc = null,
    [property:JsonPropertyName("state_before"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] NativeAdminState? StateBefore = null,
    [property:JsonPropertyName("state_after"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] NativeAdminState? StateAfter = null,
    [property:JsonPropertyName("native_error"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int? NativeError = null);

internal sealed record NativeAdminIntent(string OperationId,NativeAdminAction Action,string Target,
    NativeAdminState ExpectedState,bool AllowDisruptive,string ActorSid,DateTimeOffset CreatedUtc);

// A native, per-operation one-shot intent, NOT a second SQLite writer. Its
// CreateNew + WriteThrough + Flush(true) happens before a potentially mutating
// Win32/Task Scheduler call. A lost acknowledgement can never authorize replay:
// an intent with no independently persisted outcome is EFFECT_UNKNOWN.
// Results are immutable CreateNew companions; neither file is ever overwritten.
internal static class NativeAdminControl
{
    private static readonly HashSet<string> CriticalServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsAdminMCP", "RpcSs", "RpcEptMapper", "DcomLaunch", "EventLog",
        "Schedule", "PlugPlay", "Power", "ProfSvc", "SamSs", "LSM", "Winmgmt",
        "BFE", "MpsSvc", "WinDefend", "WdNisSvc", "TermService", "LanmanServer",
        "LanmanWorkstation", "Dhcp", "Dnscache", "NlaSvc", "CryptSvc", "W32Time",
        "WinRM", "WlanSvc", "Netlogon", "GPSvc", "SENS", "TrustedInstaller"
    };

    internal static bool Valid(NativeAdminAction action,string target,string operationId,
        NativeAdminState expected,bool allowDisruptive,bool readOnly,bool confirm) =>
        Enum.IsDefined(action) && Enum.IsDefined(expected) &&
        Guid.TryParseExact(operationId,"N",out _) &&
        (readOnly || confirm) &&
        (action switch
        {
            NativeAdminAction.SERVICE_START => expected==NativeAdminState.STOPPED,
            NativeAdminAction.SERVICE_STOP => expected==NativeAdminState.RUNNING && allowDisruptive,
            NativeAdminAction.SERVICE_PAUSE => expected==NativeAdminState.RUNNING,
            NativeAdminAction.SERVICE_CONTINUE => expected==NativeAdminState.PAUSED,
            NativeAdminAction.TASK_ENABLE => expected==NativeAdminState.DISABLED && !allowDisruptive,
            NativeAdminAction.TASK_DISABLE => expected==NativeAdminState.ENABLED && !allowDisruptive,
            _=>false
        }) &&
        (IsServiceAction(action)
            ? ValidServiceName(target) : ValidTaskPath(target));

    internal static bool IsServiceAction(NativeAdminAction action) => action is
        NativeAdminAction.SERVICE_START or NativeAdminAction.SERVICE_STOP or
        NativeAdminAction.SERVICE_PAUSE or NativeAdminAction.SERVICE_CONTINUE;

    internal static bool ValidServiceName(string? name) =>
        name is {Length:>=1 and <=128} &&
        name.All(c=>char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    internal static bool ValidTaskPath(string? path)
    {
        if(path is not {Length:>=2 and <=512} || path[0]!='\\' || path[^1]=='\\' ||
            path.Contains("\\\\",StringComparison.Ordinal) || path.Contains('/') || path.Contains('\0'))
            return false;
        foreach(var part in path[1..].Split('\\'))
            if(part is "" or "." or ".." || part.Length>128 ||
                part.Any(c=>char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|'))
                return false;
        return true;
    }

    private static string ReceiptDirectory()
    {
        string parent=Path.GetDirectoryName(StateDirectory.DatabasePath())!;
        string path=Path.Combine(parent,"admin-action-intents");
        Directory.CreateDirectory(path);
        var info=new DirectoryInfo(path);
        if((info.Attributes&FileAttributes.ReparsePoint)!=0)
            throw new UnauthorizedAccessException("Unsafe admin action directory.");
        var acl=info.GetAccessControl();
        var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
        var admins=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
        if(acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !owner.Equals(system) && !owner.Equals(admins))
            throw new UnauthorizedAccessException("Unsafe admin action directory owner.");
        foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType==AccessControlType.Allow &&
                !rule.IdentityReference.Equals(system) && !rule.IdentityReference.Equals(admins))
                throw new UnauthorizedAccessException("Unsafe admin action directory ACL.");
        return path;
    }

    private static void WriteOnce<T>(string path,T value)
    {
        byte[] json=JsonSerializer.SerializeToUtf8Bytes(value,PipeWire.Json);
        if(json.Length>8192)throw new IOException("Receipt exceeds bound.");
        using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,
            4096,FileOptions.WriteThrough);
        file.Write(json);
        file.Flush(flushToDisk:true);
    }

    private static T? ReadBounded<T>(string path)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,
            4096,FileOptions.SequentialScan);
        if(file.Length is <=0 or >8192)return default;
        return JsonSerializer.Deserialize<T>(file,PipeWire.Json);
    }

    private static NativeAdminResult Result(NativeAdminCode code,NativeAdminIntent intent,
        string stage,NativeAdminState? before=null,NativeAdminState? after=null,int? error=null)=>
        new(code,intent.OperationId,intent.Action,intent.Target,
            code==NativeAdminCode.COMMITTED?WindowEffectState.EFFECT_VERIFIED:
            code==NativeAdminCode.EFFECT_UNKNOWN?WindowEffectState.EFFECT_UNKNOWN:
            WindowEffectState.NO_EFFECT,stage,DateTimeOffset.UtcNow,before,after,error);

    internal static NativeAdminResult Run(NativeAdminAction action,string target,string operationId,
        NativeAdminState expected,bool allowDisruptive,bool readOnly,bool confirm,
        string actorSid,CancellationToken ct,string? isolatedReceiptDirectory=null,
        Func<NativeAdminIntent,CancellationToken,NativeAdminResult>? isolatedExecutor=null)
    {
        var requested=new NativeAdminIntent(operationId,action,target,expected,allowDisruptive,actorSid,
            DateTimeOffset.UtcNow);
        if(!Valid(action,target,operationId,expected,allowDisruptive,readOnly,confirm) ||
            actorSid is not {Length:>=4 and <=256})
            return Result(NativeAdminCode.INVALID_ARGUMENT,requested,"VALIDATE");
        if(!OperatingSystem.IsWindows())return Result(NativeAdminCode.UNSUPPORTED_PLATFORM,requested,"VALIDATE");
        string directory;
        try{directory=isolatedReceiptDirectory??ReceiptDirectory();}
        catch(Exception e) when(e is UnauthorizedAccessException or SecurityException)
        {return Result(NativeAdminCode.ACCESS_DENIED,requested,"RECEIPT_DIRECTORY");}
        catch(Exception e) when(e is IOException or ArgumentException)
        {return Result(NativeAdminCode.ACTION_FAILED,requested,"RECEIPT_DIRECTORY");}
        string basePath=Path.Combine(directory,operationId);
        string intentPath=basePath+".intent";
        string resultPath=basePath+".result";
        try
        {
            if(readOnly)return Existing();
            if(File.Exists(intentPath))return Existing();
            if(Directory.EnumerateFiles(directory,"*.intent",SearchOption.TopDirectoryOnly)
                    .Take(4096).Count()>=4096)
                return Result(NativeAdminCode.STORE_FULL,requested,"RECEIPT_CAPACITY");
            ct.ThrowIfCancellationRequested();
            try{WriteOnce(intentPath,requested);}
            catch(IOException) when(File.Exists(intentPath)) {return Existing();}
            catch(UnauthorizedAccessException) {return Result(NativeAdminCode.ACCESS_DENIED,requested,"PREPARE");}
            catch(IOException) {return Result(NativeAdminCode.EFFECT_UNKNOWN,requested,"PREPARE_UNCERTAIN");}
            // The intent is already durable before the first possible effect.
            NativeAdminResult outcome;
            try
            {
                ct.ThrowIfCancellationRequested();
                outcome=isolatedExecutor is not null ? isolatedExecutor(requested,ct) :
                    IsServiceAction(action) ? Service(requested,ct) : TaskScheduler(requested,ct);
            }
            catch(OperationCanceledException)
            {outcome=Result(NativeAdminCode.EFFECT_UNKNOWN,requested,"CANCELLED_AFTER_INTENT");}
            catch(Exception e) when(e is Win32Exception or COMException or InvalidOperationException or
                UnauthorizedAccessException or SecurityException or System.TimeoutException)
            {
                int? error=e is Win32Exception win?win.NativeErrorCode:e is COMException com?com.HResult:null;
                outcome=Result(NativeAdminCode.EFFECT_UNKNOWN,requested,"NATIVE_CALL_UNCERTAIN",error:error);
            }
            try{WriteOnce(resultPath,outcome);return outcome;}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException)
            {return Result(NativeAdminCode.EFFECT_UNKNOWN,requested,"RESULT_PERSIST_FAILED");}
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException)
        {return Result(NativeAdminCode.EFFECT_UNKNOWN,requested,"RECEIPT_READ_UNCERTAIN");}

        NativeAdminResult Existing()
        {
            if(!File.Exists(intentPath))return Result(NativeAdminCode.NOT_FOUND,requested,"STATUS");
            NativeAdminIntent? stored=ReadBounded<NativeAdminIntent>(intentPath);
            if(stored is null)return Result(NativeAdminCode.EFFECT_UNKNOWN,requested,"RECEIPT_CORRUPT");
            if(stored.OperationId!=requested.OperationId || stored.Action!=requested.Action ||
                stored.Target!=requested.Target || stored.ExpectedState!=requested.ExpectedState ||
                stored.AllowDisruptive!=requested.AllowDisruptive || stored.ActorSid!=requested.ActorSid)
                return Result(NativeAdminCode.CONFLICT,requested,"RECEIPT_IDENTITY");
            if(!File.Exists(resultPath))return Result(NativeAdminCode.EFFECT_UNKNOWN,requested,"INTENT_NO_RESULT");
            NativeAdminResult? saved=ReadBounded<NativeAdminResult>(resultPath);
            return saved is not null && saved.OperationId==operationId && saved.Action==action &&
                saved.Target==target ? saved : Result(NativeAdminCode.EFFECT_UNKNOWN,requested,"RESULT_CORRUPT");
        }
    }

    private static NativeAdminResult Service(NativeAdminIntent intent,CancellationToken ct)
    {
        if(CriticalServices.Contains(intent.Target))
            return Result(NativeAdminCode.UNSAFE_TARGET,intent,"TARGET_GUARD");
        using var service=new ServiceController(intent.Target);
        ServiceControllerStatus before;
        try{before=service.Status;}
        catch(InvalidOperationException e) when(e.InnerException is Win32Exception w && w.NativeErrorCode==1060)
        {return Result(NativeAdminCode.NOT_FOUND,intent,"SERVICE_OPEN");}
        catch(InvalidOperationException e) when(e.InnerException is Win32Exception w && w.NativeErrorCode==5)
        {return Result(NativeAdminCode.ACCESS_DENIED,intent,"SERVICE_OPEN");}
        if(service.ServiceType!=ServiceType.Win32OwnProcess)
            return Result(NativeAdminCode.UNSAFE_TARGET,intent,"SHARED_OR_DRIVER_SERVICE");
        NativeAdminState desired=intent.Action switch
        {
            NativeAdminAction.SERVICE_START=>NativeAdminState.RUNNING,
            NativeAdminAction.SERVICE_STOP=>NativeAdminState.STOPPED,
            NativeAdminAction.SERVICE_PAUSE=>NativeAdminState.PAUSED,
            NativeAdminAction.SERVICE_CONTINUE=>NativeAdminState.RUNNING,
            _=>throw new ArgumentOutOfRangeException()
        };
        NativeAdminState? observed=before switch
        {
            ServiceControllerStatus.Stopped=>NativeAdminState.STOPPED,
            ServiceControllerStatus.Running=>NativeAdminState.RUNNING,
            ServiceControllerStatus.Paused=>NativeAdminState.PAUSED,
            _=>null
        };
        if(observed!=intent.ExpectedState)
            return Result(NativeAdminCode.PRECONDITION_FAILED,intent,"PRECONDITION",observed);
        if(intent.Action==NativeAdminAction.SERVICE_STOP)
        {
            if(!service.CanStop)return Result(NativeAdminCode.UNSAFE_TARGET,intent,"NOT_STOPPABLE",observed);
            var dependent=service.DependentServices;
            try
            {
                if(dependent.Any(x=>x.Status!=ServiceControllerStatus.Stopped))
                    return Result(NativeAdminCode.UNSAFE_TARGET,intent,"ACTIVE_DEPENDENTS",observed);
            }
            finally {foreach(var item in dependent)item.Dispose();}
        }
        if(intent.Action is NativeAdminAction.SERVICE_PAUSE or NativeAdminAction.SERVICE_CONTINUE &&
            !service.CanPauseAndContinue)
            return Result(NativeAdminCode.UNSAFE_TARGET,intent,"NOT_PAUSABLE",observed);
        ct.ThrowIfCancellationRequested();
        switch(intent.Action)
        {
            case NativeAdminAction.SERVICE_START:service.Start();break;
            case NativeAdminAction.SERVICE_STOP:service.Stop();break;
            case NativeAdminAction.SERVICE_PAUSE:service.Pause();break;
            case NativeAdminAction.SERVICE_CONTINUE:service.Continue();break;
        }
        var deadline=Stopwatch.StartNew();
        while(deadline.Elapsed<TimeSpan.FromSeconds(10))
        {
            // No cancellation of the Win32 operation itself after issue. A
            // cancelled waiter returns UNKNOWN, never NO_EFFECT.
            ct.ThrowIfCancellationRequested();
            service.Refresh();
            var current=service.Status;
            if(current.ToString().Equals(desired.ToString(),StringComparison.OrdinalIgnoreCase))
                return Result(NativeAdminCode.COMMITTED,intent,"SCM_VERIFIED",observed,desired);
            Thread.Sleep(150);
        }
        return Result(NativeAdminCode.EFFECT_UNKNOWN,intent,"SCM_VERIFY_TIMEOUT",observed);
    }

    private static NativeAdminResult TaskScheduler(NativeAdminIntent intent,CancellationToken ct)
    {
        string full=intent.Target;
        if(full.StartsWith("\\Microsoft\\",StringComparison.OrdinalIgnoreCase) ||
            full.Contains("WindowsAdminMCP",StringComparison.OrdinalIgnoreCase))
            return Result(NativeAdminCode.UNSAFE_TARGET,intent,"SYSTEM_TASK_GUARD");
        int split=full.LastIndexOf('\\');
        string folder=split==0?"\\":full[..split];
        string name=full[(split+1)..];
        Type? serviceType=Type.GetTypeFromProgID("Schedule.Service");
        if(serviceType is null)return Result(NativeAdminCode.ACTION_FAILED,intent,"TASK_COM_UNAVAILABLE");
        object? scheduler=null,folderObject=null,taskObject=null;
        bool issued=false;
        try
        {
            scheduler=Activator.CreateInstance(serviceType);
            if(scheduler is null)return Result(NativeAdminCode.ACTION_FAILED,intent,"TASK_COM_UNAVAILABLE");
            dynamic service=scheduler;
            service.Connect();
            folderObject=service.GetFolder(folder);
            dynamic taskFolder=folderObject!;
            taskObject=taskFolder.GetTask(name);
            dynamic task=taskObject!;
            bool before=(bool)task.Enabled;
            bool expected=intent.ExpectedState==NativeAdminState.ENABLED;
            NativeAdminState beforeState=before?NativeAdminState.ENABLED:NativeAdminState.DISABLED;
            if(before!=expected)
                return Result(NativeAdminCode.PRECONDITION_FAILED,intent,"PRECONDITION",beforeState);
            ct.ThrowIfCancellationRequested();
            bool next=intent.Action==NativeAdminAction.TASK_ENABLE;
            issued=true;
            task.Enabled=next;
            // Independent re-query after mutation, with an exact path.
            if(Marshal.IsComObject(taskObject))Marshal.FinalReleaseComObject(taskObject);
            taskObject=null;
            taskObject=taskFolder.GetTask(name);
            dynamic verified=taskObject!;
            if((bool)verified.Enabled==next)
                return Result(NativeAdminCode.COMMITTED,intent,"TASK_VERIFIED",beforeState,
                    next?NativeAdminState.ENABLED:NativeAdminState.DISABLED);
            return Result(NativeAdminCode.EFFECT_UNKNOWN,intent,"TASK_VERIFY_MISMATCH",beforeState);
        }
        catch(COMException e) when(!issued &&
            (e.HResult==unchecked((int)0x80070002) ||
             e.HResult==unchecked((int)0x8004130F)))
        {return Result(NativeAdminCode.NOT_FOUND,intent,"TASK_NOT_FOUND",error:e.HResult);}
        catch(COMException e) when(!issued && e.HResult==unchecked((int)0x80070005))
        {return Result(NativeAdminCode.ACCESS_DENIED,intent,"TASK_ACCESS",error:e.HResult);}
        finally
        {
            foreach(object? com in new[]{taskObject,folderObject,scheduler})
                if(com is not null && Marshal.IsComObject(com))
                    try{Marshal.FinalReleaseComObject(com);}catch(InvalidComObjectException){}
        }
    }
}

[McpServerToolType]
public sealed class NativeAdminTools
{
    [McpServerTool(Name="windows.admin_action",UseStructuredContent=true),
     Description("Guarded native SCM service START/STOP/PAUSE/CONTINUE and Task Scheduler ENABLE/DISABLE. Requires exact target, expected pre-state, GUID N operation_id and confirm=true. SERVICE_STOP requires allow_disruptive=true; critical/shared services blocked. Durable one-shot intent prevents any blind effect replay after timeout: read_only=true checks an existing exact receipt without executing anything. UNKNOWN effect must be reconciled independently. Runs only under elevated authenticated USER session; never executes a script or arbitrary task command.")]
    public static async Task<NativeAdminResult> Action(NativeAdminAction action,string target,
        string operation_id,NativeAdminState expected_state,bool confirm=false,
        bool allow_disruptive=false,bool read_only=false,
        CancellationToken cancellationToken=default)
    {
        NativeAdminResult Denied(NativeAdminCode code,string stage)=>
            new(code,operation_id,action,target,Stage:stage,ObservedUtc:DateTimeOffset.UtcNow);
        if(!NativeAdminControl.Valid(action,target,operation_id,expected_state,
            allow_disruptive,read_only,confirm))return Denied(NativeAdminCode.INVALID_ARGUMENT,"VALIDATE");
        if(!OperatingSystem.IsWindows())return Denied(NativeAdminCode.UNSUPPORTED_PLATFORM,"VALIDATE");
        var status=await AdminTools.Status(cancellationToken:cancellationToken);
        if(status.Status!=StatusCode.OK)return Denied(NativeAdminCode.SERVICE_UNAVAILABLE,"STATUS");
        using var self=Process.GetCurrentProcess();
        using var identity=WindowsIdentity.GetCurrent();
        if(identity.IsSystem || status.ExecutionContext!=CommandContext.USER ||
            status.Frontend?.Elevated!=true || status.Frontend.SessionId!=self.SessionId ||
            status.Frontend.Sid!=identity.User?.Value ||
            !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return Denied(NativeAdminCode.USER_CONTEXT_UNAVAILABLE,"IDENTITY");
        return await Task.Run(()=>NativeAdminControl.Run(action,target,operation_id,
            expected_state,allow_disruptive,read_only,confirm,identity.User!.Value,
            cancellationToken),CancellationToken.None);
    }
}
