// Local operator path, deliberately not an MCP tool. The service retains the
// durable job ledger and owns all cancellations; no direct SQLite mutations.
internal static class MaintenanceDrain
{
    internal static async Task<MaintenanceReply> ExecuteAsync(MaintenanceAction action)
    {
        var reply=await AdminTools.Call(new(AdminOperation.Maintenance,Maintenance:action),CancellationToken.None);
        if(reply.Maintenance is {} result)return result;
        // Older services reject the unknown enum during deserialization and
        // close the pipe (IPC_FAILED), or return IPC_PROTOCOL_ERROR.
        if(reply.Code is not (StatusCode.IPC_PROTOCOL_ERROR or StatusCode.IPC_FAILED))return new(reply.Code,false);

        // A verified candidate may be running against an already installed
        // service from before the Maintenance operation existed. The old pipe
        // supports typed list/cancel, so drain it through its audited API.
        // The updater closes ingress *before* taking this compatibility path.
        if(action==MaintenanceAction.Resume)return new(StatusCode.OK,false);
        int queued=0,running=0;
        foreach(var state in new[]{JobState.QUEUED,JobState.RUNNING})
        {
            long? before=null;
            for(int page=0;page<100;page++)
            {
                var listed=await AdminTools.Call(new(AdminOperation.Job,JobAction.list,
                    StateFilter:state,PageSize:100,BeforeSeq:before),CancellationToken.None);
                if(listed.Job is not {Status:StatusCode.OK} jobs)return new(listed.Code,false,queued,running);
                foreach(var job in jobs.Jobs??[])
                {
                    var cancelled=await AdminTools.Call(new(AdminOperation.Job,JobAction.cancel,
                        JobId:job.JobId),CancellationToken.None);
                    if(cancelled.Job?.Status!=StatusCode.OK)return new(cancelled.Code,false,queued,running);
                    if(state==JobState.QUEUED)queued++;else running++;
                }
                if(jobs.NextBeforeSeq is not {} next)break;
                before=next;
                if(page==99)return new(StatusCode.BUSY,false,queued,running);
            }
        }
        return new(StatusCode.OK,false,queued,running);
    }
}
