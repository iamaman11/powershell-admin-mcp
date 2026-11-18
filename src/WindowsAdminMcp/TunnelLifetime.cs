using System.Diagnostics;
using System.Text.Json;

internal enum TunnelEnd { EXITED, TUN_LOST, ACCESS_DENIED, USER_WORKER_STOPPED }

// A single foreground wait owned by the existing launcher. No background service,
// route management, external probe, or second restart policy.
internal static class TunnelLifetime
{
    internal static async Task<TunnelEnd> WatchAsync(Process child,Func<TunReadiness> observe,
        Func<Task<bool>> accessDenied,TimeSpan interval,Task? userWorker=null)
    {
        var exit=child.WaitForExitAsync();
        while(!exit.IsCompleted)
        {
            if(userWorker?.IsCompleted==true)return TunnelEnd.USER_WORKER_STOPPED;
            var delay=Task.Delay(interval);
            var next=userWorker is null?await Task.WhenAny(exit,delay):
                await Task.WhenAny(exit,delay,userWorker);
            if(userWorker?.IsCompleted==true)return TunnelEnd.USER_WORKER_STOPPED;
            if(next==exit)break;
            if(observe()!=TunReadiness.READY)return TunnelEnd.TUN_LOST;
            if(await accessDenied())return TunnelEnd.ACCESS_DENIED;
        }
        await exit;
        return userWorker?.IsCompleted==true?TunnelEnd.USER_WORKER_STOPPED:TunnelEnd.EXITED;
    }

    // Official local component health only. Refuse to continue after explicit
    // 401/403; never change credentials, network settings or endpoint to retry.
    internal static bool IsAccessDenied(JsonElement root)
        => root.ValueKind==JsonValueKind.Object &&
           root.TryGetProperty("schema_version",out var schema) &&
           schema.ValueKind==JsonValueKind.Number && schema.TryGetInt32(out int version) && version==1 &&
           root.TryGetProperty("component",out var component) && component.ValueKind==JsonValueKind.String &&
           component.GetString()=="control-plane" &&
           root.TryGetProperty("details",out var detail) && detail.ValueKind==JsonValueKind.Object &&
           detail.TryGetProperty("http_status",out var code) && code.ValueKind==JsonValueKind.Number &&
           code.TryGetInt32(out int status) && status is 401 or 403 &&
           detail.TryGetProperty("consecutive_failures",out var failures) &&
           failures.ValueKind==JsonValueKind.Number && failures.TryGetInt64(out long count) && count>0;

    internal static async Task<bool> AccessDeniedAsync()
    {
        try
        {
            using var handler=new HttpClientHandler { UseProxy=false,AllowAutoRedirect=false };
            using var http=new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(2),MaxResponseContentBufferSize=65536 };
            using var response=await http.GetAsync("http://127.0.0.1:8080/health/control-plane");
            if(!response.IsSuccessStatusCode)return false;
            using var doc=JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            return IsAccessDenied(doc.RootElement);
        }
        catch(Exception e) when(e is HttpRequestException or OperationCanceledException or JsonException)
        {return false;}
    }
}
