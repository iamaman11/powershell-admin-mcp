// One shared protocol for the service (SYSTEM) and authenticated elevated
// frontend (USER). SQLite owner is always the existing SCM JobCoordinator.
// PREPARE -> durable COMMITTING intent -> native effect -> OBSERVE. Lost ACK
// yields EFFECT_UNKNOWN, never a blind second destructive execution.
internal static class FileManageWorkflow
{
    internal static FileReceiptRequest Receipt(FileManageRequest request,FileWriteAction action,
        FileReceiptPhase phase,FileManageReply? observation=null)
    {
        string hash=NativeFileManager.SpecificationHash(request);
        FileWriteReply? write=observation is null?null:new(
            observation.Status==FileManageCode.COMPLETED?FileWriteCode.COMMITTED:
            observation.EffectState==WindowEffectState.EFFECT_UNKNOWN?FileWriteCode.EFFECT_UNKNOWN:
            FileWriteCode.STAGED,request.OperationId,request.ExecutionContext);
        return new(phase,action,request.Path,request.OperationId,request.ExecutionContext,
            action==FileWriteAction.BEGIN?0:null,hash,Observation:write);
    }

    private static FileManageReply Map(FileManageRequest request,FileReceiptReply? ledger,
        string stage)
    {
        FileManageCode code=ledger?.Code switch
        {
            FileReceiptCode.NOT_FOUND=>FileManageCode.NOT_FOUND,
            FileReceiptCode.CONFLICT=>FileManageCode.CONFLICT,
            FileReceiptCode.STORE_FULL=>FileManageCode.STORE_FULL,
            FileReceiptCode.IO_ERROR=>FileManageCode.IO_ERROR,
            FileReceiptCode.EFFECT_UNKNOWN=>FileManageCode.EFFECT_UNKNOWN,
            FileReceiptCode.READY=>ledger!.State switch
            {
                FileReceiptState.COMMITTED=>FileManageCode.COMPLETED,
                FileReceiptState.STAGED=>FileManageCode.READY,
                FileReceiptState.COMMITTING or FileReceiptState.EFFECT_UNKNOWN=>FileManageCode.EFFECT_UNKNOWN,
                _=>FileManageCode.CONFLICT
            },
            _=>FileManageCode.SERVICE_UNAVAILABLE
        };
        return new(code,request.OperationId,request.Action,request.ExecutionContext,
            code==FileManageCode.COMPLETED?WindowEffectState.EFFECT_VERIFIED:
            code==FileManageCode.EFFECT_UNKNOWN?WindowEffectState.EFFECT_UNKNOWN:
            WindowEffectState.NO_EFFECT,stage,DateTimeOffset.UtcNow);
    }

    internal static async Task<FileManageReply> RunAsync(FileManageRequest request,
        Func<FileReceiptRequest,CancellationToken,Task<FileReceiptReply?>> ledger,
        Func<CancellationToken,Task<FileManageReply>> execute,CancellationToken ct)
    {
        if(!NativeFileManager.Valid(request))
            return new(FileManageCode.INVALID_ARGUMENT,request.OperationId,request.Action,
                request.ExecutionContext,Stage:"VALIDATE",ObservedAt:DateTimeOffset.UtcNow);
        try
        {
            if(request.ReadOnly)
            {
                var status=await ledger(Receipt(request,FileWriteAction.STATUS,FileReceiptPhase.PREPARE),ct);
                return Map(request,status,"SQLITE_STATUS");
            }
            var reserved=await ledger(Receipt(request,FileWriteAction.BEGIN,FileReceiptPhase.PREPARE),ct);
            var reservation=Map(request,reserved,"SQLITE_BEGIN");
            if(reservation.Status!=FileManageCode.READY)return reservation;
            var intent=await ledger(Receipt(request,FileWriteAction.COMMIT,FileReceiptPhase.PREPARE),ct);
            if(intent is not {Code:FileReceiptCode.READY,State:FileReceiptState.COMMITTING})
            {
                var outcome=Map(request,intent,"SQLITE_INTENT");
                return outcome.Status==FileManageCode.READY?outcome with
                    {Status=FileManageCode.EFFECT_UNKNOWN,EffectState=WindowEffectState.EFFECT_UNKNOWN}:outcome;
            }
            FileManageReply result;
            try{result=await execute(ct);}
            catch(OperationCanceledException)
            {result=new(FileManageCode.EFFECT_UNKNOWN,request.OperationId,request.Action,
                request.ExecutionContext,WindowEffectState.EFFECT_UNKNOWN,"TIMEOUT",DateTimeOffset.UtcNow);}
            // No caller cancellation here: persisting a known outcome after
            // an effect is more important than the original request timeout.
            var observed=await ledger(Receipt(request,FileWriteAction.COMMIT,
                FileReceiptPhase.OBSERVE,result),CancellationToken.None);
            if(observed is {Code:FileReceiptCode.READY})return result;
            return result with {Status=FileManageCode.EFFECT_UNKNOWN,
                EffectState=WindowEffectState.EFFECT_UNKNOWN,Stage="SQLITE_PERSIST"};
        }
        catch(OperationCanceledException)
        {return new(FileManageCode.EFFECT_UNKNOWN,request.OperationId,request.Action,
            request.ExecutionContext,WindowEffectState.EFFECT_UNKNOWN,"TRANSPORT_TIMEOUT",DateTimeOffset.UtcNow);}
    }
}
