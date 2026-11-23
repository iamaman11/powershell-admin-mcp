using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using ModelContextProtocol.Server;

// Typed native file tools; no arbitrary script wrapper or additional process.
[McpServerToolType]
public sealed class FileTools
{
    [McpServerTool(Name = "windows.file_query", UseStructuredContent = true),
     Description("Native file STAT or paged directory LIST, with exact USER/SYSTEM context and change-aware cursor.")]
    public static Task<NativeFileReply> Query(FileQueryAction action, string path,
        int page_size = 50, string? cursor = null, bool include_sha256 = false,
        CommandContext execution_context = CommandContext.USER,
        CancellationToken cancellationToken = default) =>
        Dispatch(new(action switch
            {
                FileQueryAction.STAT => NativeFileAction.STAT,
                FileQueryAction.LIST => NativeFileAction.LIST,
                _ => (NativeFileAction)(-1)
            }, path, execution_context, page_size, cursor, include_sha256), cancellationToken);

    [McpServerTool(Name = "windows.file_read_chunk", UseStructuredContent = true),
     Description("Read exact binary file bytes (base64), offset 0..EOF, length 1..262144, SHA256 and version.")]
    public static Task<NativeFileReply> ReadChunk(string path, long offset, int length = 65536,
        string? expected_version = null, CommandContext execution_context = CommandContext.USER,
        CancellationToken cancellationToken = default) =>
        Dispatch(new(NativeFileAction.READ_CHUNK, path, execution_context,
            Offset:offset, Length:length, ExpectedVersion:expected_version), cancellationToken);

    [McpServerTool(Name="windows.file_manage",UseStructuredContent=true),
     Description("Native MKDIR/COPY/MOVE/DELETE, USER or SYSTEM, with durable GUID N operation_id. " +
         "Directory COPY/DELETE requires recursive=true; MOVE is same-volume atomic. " +
         "read_only=true queries the receipt without modifying files; never retry EFFECT_UNKNOWN blindly. " +
         "Optional expected_version and expected_sha256 together protect a source file from changes.")]
    public static async Task<FileManageReply> Manage(FileManageAction action,string path,string operation_id,
        string? destination=null,bool recursive=false,string? expected_version=null,
        string? expected_sha256=null,bool read_only=false,
        CommandContext execution_context=CommandContext.USER,CancellationToken cancellationToken=default)
    {
        var request=new FileManageRequest(action,path,operation_id,execution_context,destination,
            recursive,expected_version,expected_sha256,read_only);
        if(!NativeFileManager.Valid(request))
            return new(FileManageCode.INVALID_ARGUMENT,operation_id,action,execution_context,
                Stage:"VALIDATE",ObservedAt:DateTimeOffset.UtcNow);
        if(execution_context==CommandContext.SYSTEM)
        {
            var response=await AdminTools.Call(new(AdminOperation.FileManage,FileManage:request),
                cancellationToken);
            return response.FileManage??new(FileManageCode.EFFECT_UNKNOWN,operation_id,action,
                execution_context,WindowEffectState.EFFECT_UNKNOWN,"TRANSPORT",DateTimeOffset.UtcNow);
        }
        var status=await AdminTools.Status(cancellationToken:cancellationToken);
        if(status.Status!=StatusCode.OK)
            return new(FileManageCode.SERVICE_UNAVAILABLE,operation_id,action,execution_context,
                Stage:"TRANSPORT",ObservedAt:DateTimeOffset.UtcNow);
        using var self=Process.GetCurrentProcess();
        using var identity=WindowsIdentity.GetCurrent();
        if(identity.IsSystem || status.Frontend?.Elevated!=true ||
            status.Frontend.SessionId!=self.SessionId || status.Frontend.Sid!=identity.User?.Value ||
            !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return new(FileManageCode.USER_CONTEXT_UNAVAILABLE,operation_id,action,execution_context,
                Stage:"VALIDATE",ObservedAt:DateTimeOffset.UtcNow);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        return await FileManageWorkflow.RunAsync(request,async (receipt,ct)=>
            (await AdminTools.Call(new(AdminOperation.FileReceipt,FileReceipt:receipt),ct)).FileReceipt,
            ct=>Task.Run(()=>NativeFileManager.Execute(request,ct),ct),deadline.Token);
    }

    [McpServerTool(Name="windows.file_write",UseStructuredContent=true),
     Description("WRITE_ATOMIC writes up to 16 KiB of base64 in one call; larger files use BEGIN/APPEND/COMMIT. Supply stable operation_id (GUID N format). STATUS recovers lost replies. CAS replacement requires expected_version and original_sha256.")]
    public static async Task<FileWriteReply> Write(FileWriteAction action,string path,string operation_id,
        long? expected_bytes=null,string? expected_sha256=null,
        FileOverwritePolicy overwrite_policy=FileOverwritePolicy.FAIL_IF_EXISTS,
        string? expected_version=null,string? original_sha256=null,
        long? offset=null,string? data_base64=null,string? chunk_sha256=null,
        CommandContext execution_context=CommandContext.USER,CancellationToken cancellationToken=default)
    {
        var request=new FileWriteRequest(action,path,operation_id,execution_context,expected_bytes,
            expected_sha256,overwrite_policy,expected_version,original_sha256,offset,data_base64,chunk_sha256);
        if(action==FileWriteAction.WRITE_ATOMIC)
        {
            if(!NativeFileWriter.TryInlineSteps(request,out var steps,out var error))
                return new(error,operation_id,execution_context,Stage:"VALIDATE",
                    ObservedAt:DateTimeOffset.UtcNow);
            FileWriteReply? result=null;
            foreach(var step in steps)
            {
                result=await WriteStep(step,cancellationToken);
                // A retry of the same operation_id may have committed before
                // its response was lost. Never upload or commit twice.
                if(result.Status==FileWriteCode.COMMITTED ||
                    step.Action!=FileWriteAction.COMMIT && result.Status!=FileWriteCode.STAGED)
                    return result;
            }
            return result!;
        }
        return await WriteStep(request,cancellationToken);
    }

    private static async Task<FileWriteReply> WriteStep(FileWriteRequest request,
        CancellationToken cancellationToken)
    {
        string operation_id=request.OperationId;
        var execution_context=request.ExecutionContext;
        if(!NativeFileWriter.Valid(request))return new(FileWriteCode.INVALID_ARGUMENT,operation_id,execution_context,
            Stage:"VALIDATE",ObservedAt:DateTimeOffset.UtcNow);
        if(execution_context==CommandContext.SYSTEM)
        {
            var response=await AdminTools.Call(new(AdminOperation.FileWrite,FileWrite:request),cancellationToken);
            // IPC can fail *after* SCM received COMMIT. Missing typed receipt
            // never proves NO_EFFECT, even if the transport says unavailable.
            return response.FileWrite??new(FileWriteCode.EFFECT_UNKNOWN,operation_id,execution_context,
                EffectState:WindowEffectState.EFFECT_UNKNOWN,
                Stage:"TRANSPORT",ObservedAt:DateTimeOffset.UtcNow);
        }
        var status=await AdminTools.Status(cancellationToken:cancellationToken);
        if(status.Status!=StatusCode.OK)
            return new(FileWriteCode.SERVICE_UNAVAILABLE,operation_id,execution_context,Stage:"TRANSPORT");
        using var self=Process.GetCurrentProcess();
        using var identity=WindowsIdentity.GetCurrent();
        if(identity.IsSystem || status.Frontend?.Elevated!=true || status.Frontend.SessionId!=self.SessionId ||
            status.Frontend.Sid!=identity.User?.Value ||
            !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return new(FileWriteCode.USER_CONTEXT_UNAVAILABLE,operation_id,execution_context,Stage:"VALIDATE");
        // The SCM service is the single SQLite owner even for USER filesystem
        // actions. Prepare the durable operation identity before any write;
        // register the verified native outcome afterwards. The native sidecar
        // remains the crash-recovery readback when this frontend disappears.
        var preparation=await AdminTools.Call(new(AdminOperation.FileReceipt,
            FileReceipt:FileReceiptRequest.From(request,FileReceiptPhase.PREPARE)),cancellationToken);
        if(preparation.FileReceipt is not {Code:FileReceiptCode.READY})
            return new(preparation.FileReceipt?.AsFileCode()??FileWriteCode.SERVICE_UNAVAILABLE,
                operation_id,execution_context,Stage:"SQLITE_PREPARE");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        FileWriteReply result;
        try{result=await Task.Run(()=>NativeFileWriter.Execute(request,deadline.Token),deadline.Token);}
        catch(OperationCanceledException){result=new(FileWriteCode.EFFECT_UNKNOWN,operation_id,
            execution_context,EffectState:WindowEffectState.EFFECT_UNKNOWN,Stage:"TIMEOUT");}
        var observed=await AdminTools.Call(new(AdminOperation.FileReceipt,
            FileReceipt:FileReceiptRequest.From(request,FileReceiptPhase.OBSERVE,result)),CancellationToken.None);
        return observed.FileReceipt is {Code:FileReceiptCode.READY}?result:
            result with {Status=FileWriteCode.EFFECT_UNKNOWN,EffectState=WindowEffectState.EFFECT_UNKNOWN,
                Stage="SQLITE_PERSIST"};
    }

    private static async Task<NativeFileReply> Dispatch(NativeFileRequest request, CancellationToken ct)
    {
        if (!NativeFileReader.Valid(request))
            return new(NativeFileCode.INVALID_ARGUMENT, request.ExecutionContext, DateTimeOffset.UtcNow,
                Stage:"VALIDATE");
        if (request.ExecutionContext == CommandContext.SYSTEM)
        {
            var reply = await AdminTools.Call(new(AdminOperation.File, File:request), ct);
            return reply.File ?? new(reply.Code is StatusCode.ACCESS_DENIED ? NativeFileCode.ACCESS_DENIED :
                NativeFileCode.SERVICE_UNAVAILABLE, CommandContext.SYSTEM, DateTimeOffset.UtcNow,
                Stage:"TRANSPORT");
        }
        var status = await AdminTools.Status(cancellationToken:ct);
        if (status.Status != StatusCode.OK)
            return new(NativeFileCode.SERVICE_UNAVAILABLE, CommandContext.USER,
                DateTimeOffset.UtcNow, Stage:"TRANSPORT");
        using var self = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        // Files do not need the interactive PowerShell USER job worker or a
        // desktop. The elevated frontend token is the actual file authority.
        if (identity.IsSystem || status.Frontend?.Elevated != true ||
            status.Frontend.SessionId != self.SessionId ||
            status.Frontend.Sid != identity.User?.Value ||
            !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return new(NativeFileCode.USER_CONTEXT_UNAVAILABLE, CommandContext.USER,
                DateTimeOffset.UtcNow, Stage:"VALIDATE");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        return await NativeFileReader.ExecuteAsync(request, deadline.Token);
    }
}
