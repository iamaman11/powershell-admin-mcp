using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

[JsonConverter(typeof(JsonStringEnumConverter<EvidenceCode>))]
internal enum EvidenceCode { NOT_SUPPLIED, INVALID, STALE_OR_DIFFERENT_INSTALLATION, LOCAL_JOB_MISMATCH, MATCHED }
internal sealed record EvidenceObservation(EvidenceCode Status,string? RunId=null,
    bool RemoteReceiptMatched=false,bool DesktopReceiptMatched=false,string? ReceiptSha256=null,
    string Scope="OPERATOR_ATTESTED_NOT_A_LIVE_REMOTE_PROBE");
internal sealed record AcceptanceReceipt(int Schema,string Commit,string RunId,DateTimeOffset ObservedUtc,
    int ServicePid,DateTimeOffset ServiceStartedUtc,int TunnelPid,DateTimeOffset TunnelStartedUtc,
    string StatusCorrelationId,string ShortJobId,string LongJobId,string CancelledJobId,
    string? ScreenshotCorrelationId=null,int? ScreenshotSessionId=null,int? ScreenshotWidth=null,
    int? ScreenshotHeight=null,int? ScreenshotPngBytes=null,string? ScreenshotMime=null);

internal static class AcceptanceEvidence
{
    private static bool Id(string? value)=>Guid.TryParseExact(value,"N",out _);
    internal static EvidenceCode Validate(AcceptanceReceipt? r,string? commit,AdminStatus? service,
        int? tunnelPid,DateTimeOffset? tunnelStarted,int? desktopSession,DateTimeOffset now)
    {
        if(r is null || r.Schema!=1 || r.Commit is null || !Regex.IsMatch(r.Commit,@"\A[a-f0-9]{40}\z") ||
            r.RunId is null || !Regex.IsMatch(r.RunId,@"\A[A-Za-z0-9-]{1,70}\z") ||
            !Id(r.StatusCorrelationId) || !Id(r.ShortJobId) || !Id(r.LongJobId) || !Id(r.CancelledJobId) ||
            new[]{r.ShortJobId,r.LongJobId,r.CancelledJobId}.Distinct(StringComparer.Ordinal).Count()!=3)
            return EvidenceCode.INVALID;
        if(commit!=r.Commit || service is not {Status:StatusCode.OK,ServicePid:int,StartedUtc:not null} ||
            service.ServicePid!=r.ServicePid || service.StartedUtc!=r.ServiceStartedUtc ||
            tunnelPid!=r.TunnelPid || tunnelStarted is null ||
            Math.Abs((tunnelStarted.Value-r.TunnelStartedUtc).TotalSeconds)>1 ||
            r.ObservedUtc<r.ServiceStartedUtc || r.ObservedUtc<r.TunnelStartedUtc ||
            r.ObservedUtc>now.AddMinutes(1) || now-r.ObservedUtc>TimeSpan.FromHours(24))
            return EvidenceCode.STALE_OR_DIFFERENT_INSTALLATION;
        if(r.ScreenshotCorrelationId is not null && (!Id(r.ScreenshotCorrelationId) ||
            r.ScreenshotSessionId is null or <=0 || r.ScreenshotSessionId!=desktopSession ||
            r.ScreenshotWidth!=16 || r.ScreenshotHeight!=16 ||
            r.ScreenshotPngBytes is null or <67 or >2000000 || r.ScreenshotMime!="image/png"))
            return EvidenceCode.INVALID;
        return EvidenceCode.MATCHED;
    }

    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static bool MatchesJob(JobReply reply,string id,string key,string command,string marker,DateTimeOffset since,long minMs)
    {
        return reply.Status==StatusCode.OK && reply.Job is {State:JobState.COMPLETED,CompletedSteps:1,TotalSteps:1,
            Execution:{Status:ExecutionState.COMPLETED,ExitCode:0,AuditPersisted:true} result} job &&
            job.JobId==id && job.OperationKey==key && job.CommandHash==Hash(command) && result.CommandSha256==job.CommandHash &&
            !result.OutputTruncated && result.Stdout.TrimEnd('\r','\n')==marker && result.ElapsedMs>=minMs &&
            DateTimeOffset.TryParse(job.CreatedUtc,out var created) && created>=since;
    }

    internal static async Task<EvidenceObservation> ObserveAsync(string? path,string? commit,AdminStatus? service,
        int? tunnelPid,DateTimeOffset? tunnelStarted,int? desktopSession)
    {
        if(path is null)return new(EvidenceCode.NOT_SUPPLIED);
        try
        {
            using var stream=File.OpenRead(path);
            if(stream.Length is <=0 or >65536)return new(EvidenceCode.INVALID);
            byte[] bytes=new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            string hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var r=JsonSerializer.Deserialize<AcceptanceReceipt>(bytes,PipeWire.Json);
            var validation=Validate(r,commit,service,tunnelPid,tunnelStarted,desktopSession,DateTimeOffset.UtcNow);
            if(validation!=EvidenceCode.MATCHED || r is null)return new(validation,ReceiptSha256:hash);
            string shortMarker="WADMIN-ACCEPT-"+r.RunId+"-SHORT",longMarker="WADMIN-ACCEPT-"+r.RunId+"-LONG";
            var s=await AdminTools.Job(JobAction.logs,job_id:r.ShortJobId,step_index:1);
            var l=await AdminTools.Job(JobAction.logs,job_id:r.LongJobId,step_index:1);
            var q=await AdminTools.Job(JobAction.status,job_id:r.CancelledJobId);
            var since=r.ServiceStartedUtc>r.TunnelStartedUtc?r.ServiceStartedUtc:r.TunnelStartedUtc;
            if(!MatchesJob(s,r.ShortJobId,r.RunId+"-exec","Write-Output '"+shortMarker+"'",shortMarker,since,0) ||
                !MatchesJob(l,r.LongJobId,r.RunId+"-long","Start-Sleep -Seconds 130; Write-Output '"+longMarker+"'",longMarker,since,130000) ||
                q.Status!=StatusCode.OK || q.Job is not {State:JobState.CANCELLED,CompletedSteps:0,TotalSteps:1} queued ||
                queued.JobId!=r.CancelledJobId || queued.OperationKey!=r.RunId+"-queue" ||
                !DateTimeOffset.TryParse(queued.CreatedUtc,out var created) || created<since)
                return new(EvidenceCode.LOCAL_JOB_MISMATCH,r.RunId,ReceiptSha256:hash);
            return new(EvidenceCode.MATCHED,r.RunId,true,r.ScreenshotCorrelationId is not null,hash);
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or System.Security.SecurityException)
        {return new(EvidenceCode.INVALID);}
    }
}
