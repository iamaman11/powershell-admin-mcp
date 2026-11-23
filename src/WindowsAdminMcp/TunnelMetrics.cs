using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

[JsonConverter(typeof(JsonStringEnumConverter<TunnelMetricsCode>))]
public enum TunnelMetricsCode { OK, UNAVAILABLE, HTTP_ERROR, TIMEOUT, TOO_LARGE, INVALID_METRICS }

// Read-only snapshot of official loopback Prometheus counters. Histograms and
// means are cumulative since this vendor generation started; they do not
// identify an individual MCP call or prove which external hop caused a delay.
public sealed record TunnelMetricsSnapshot(
    [property:JsonPropertyName("status")] TunnelMetricsCode Status,
    [property:JsonPropertyName("observed_utc")] DateTimeOffset ObservedUtc,
    [property:JsonPropertyName("port")] int Port,
    [property:JsonPropertyName("commands_enqueued")] long? CommandsEnqueued = null,
    [property:JsonPropertyName("commands_polled")] long? CommandsPolled = null,
    [property:JsonPropertyName("queue_length")] long? QueueLength = null,
    [property:JsonPropertyName("queue_capacity")] long? QueueCapacity = null,
    [property:JsonPropertyName("worker_active")] long? WorkerActive = null,
    [property:JsonPropertyName("worker_capacity")] long? WorkerCapacity = null,
    [property:JsonPropertyName("poll_samples")] long? PollSamples = null,
    [property:JsonPropertyName("poll_mean_ms")] double? PollMeanMs = null,
    [property:JsonPropertyName("queue_age_samples")] long? QueueAgeSamples = null,
    [property:JsonPropertyName("queue_age_mean_ms")] double? QueueAgeMeanMs = null,
    [property:JsonPropertyName("tools_enqueue_response_samples")] long? ToolsEnqueueResponseSamples = null,
    [property:JsonPropertyName("tools_enqueue_response_mean_ms")] double? ToolsEnqueueResponseMeanMs = null,
    [property:JsonPropertyName("tools_poll_response_samples")] long? ToolsPollResponseSamples = null,
    [property:JsonPropertyName("tools_poll_response_mean_ms")] double? ToolsPollResponseMeanMs = null);

internal static class TunnelMetrics
{
    internal const int MaxBytes = 131072;
    private static readonly Regex MetricLine = new(
        @"\A(?<name>[a-zA-Z_][a-zA-Z0-9_]*)(?:\{(?<labels>[^}]{0,2048})\})?\s+(?<value>[-+0-9.eE]+)(?:\s|\z)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));
    private static readonly Regex LatencyType = new(
        @"(?:\A|,)latency_type=""(?<value>[^""]{1,64})""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Method = new(
        @"(?:\A|,)request_method=""(?<value>[^""]{1,64})""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    internal static async Task<TunnelMetricsSnapshot> ObserveAsync(CancellationToken cancellationToken)
    {
        var observed=DateTimeOffset.UtcNow;
        // No proxy, redirect, DNS, credentials, side effects or non-loopback URL.
        using var handler=new HttpClientHandler { UseProxy=false, AllowAutoRedirect=false };
        using var http=new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(2) };
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var response=await http.GetAsync(
                $"http://127.0.0.1:{TunnelProfiles.PrimaryPort}/metrics",
                HttpCompletionOption.ResponseHeadersRead,budget.Token);
            if(!response.IsSuccessStatusCode)
                return new(TunnelMetricsCode.HTTP_ERROR,observed,TunnelProfiles.PrimaryPort);
            if(response.Content.Headers.ContentLength>MaxBytes)
                return new(TunnelMetricsCode.TOO_LARGE,observed,TunnelProfiles.PrimaryPort);
            await using var stream=await response.Content.ReadAsStreamAsync(budget.Token);
            byte[] payload=new byte[MaxBytes+1];
            int length=0;
            while(length<payload.Length)
            {
                int n=await stream.ReadAsync(payload.AsMemory(length),budget.Token);
                if(n==0)break;
                length+=n;
            }
            if(length>MaxBytes)return new(TunnelMetricsCode.TOO_LARGE,observed,TunnelProfiles.PrimaryPort);
            return Parse(new UTF8Encoding(false,true).GetString(payload,0,length),observed);
        }
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested)
        {return new(TunnelMetricsCode.TIMEOUT,observed,TunnelProfiles.PrimaryPort);}
        catch(HttpRequestException)
        {return new(TunnelMetricsCode.UNAVAILABLE,observed,TunnelProfiles.PrimaryPort);}
        catch(IOException)
        {return new(TunnelMetricsCode.UNAVAILABLE,observed,TunnelProfiles.PrimaryPort);}
        catch(DecoderFallbackException)
        {return new(TunnelMetricsCode.INVALID_METRICS,observed,TunnelProfiles.PrimaryPort);}
        catch(RegexMatchTimeoutException)
        {return new(TunnelMetricsCode.INVALID_METRICS,observed,TunnelProfiles.PrimaryPort);}
    }

    internal static TunnelMetricsSnapshot Parse(string input,DateTimeOffset observed)
    {
        TunnelMetricsSnapshot Invalid()=>new(TunnelMetricsCode.INVALID_METRICS,observed,TunnelProfiles.PrimaryPort);
        if(Encoding.UTF8.GetByteCount(input)>MaxBytes)return new(TunnelMetricsCode.TOO_LARGE,observed,TunnelProfiles.PrimaryPort);
        var values=new Dictionary<string,double>(StringComparer.Ordinal);
        foreach(string raw in input.Split('\n'))
        {
            string line=raw.TrimEnd('\r');
            if(line.Length==0 || line[0]=='#')continue;
            var match=MetricLine.Match(line);
            if(!match.Success)continue; // Ignore new vendor metrics and exemplars.
            string name=match.Groups["name"].Value;
            string key=name;
            if(name.StartsWith("command_end_to_end_latency_milliseconds_",StringComparison.Ordinal))
            {
                string labels=match.Groups["labels"].Value;
                if(Method.Match(labels).Groups["value"].Value!="tools/call")continue;
                string kind=LatencyType.Match(labels).Groups["value"].Value;
                if(kind is not ("enqueue_to_response" or "poll_to_response"))continue;
                if(!name.EndsWith("_count",StringComparison.Ordinal) &&
                    !name.EndsWith("_sum",StringComparison.Ordinal))continue;
                key=kind+"_"+(name.EndsWith("_count",StringComparison.Ordinal)?"count":"sum");
            }
            else if(name is not ("commands_enqueued_total" or "commands_polled_total" or
                "commands_queue_length" or "commands_queue_capacity" or
                "dispatcher_worker_pool_occupancy" or "dispatcher_worker_pool_capacity" or
                "commands_age_seconds_count" or "commands_age_seconds_sum" or
                "commands_poll_latency_seconds_count" or "commands_poll_latency_seconds_sum"))
                continue;
            if(!double.TryParse(match.Groups["value"].Value,NumberStyles.Float,
                CultureInfo.InvariantCulture,out double value) ||
                !double.IsFinite(value) || value<0)return Invalid();
            double next=values.GetValueOrDefault(key)+value;
            if(!double.IsFinite(next))return Invalid();
            values[key]=next;
        }
        long? Long(string key) =>
            values.TryGetValue(key,out double v) && v<=long.MaxValue && v==Math.Truncate(v)
                ?(long)v:null;
        double? Mean(string sum,string count,double scale=1) =>
            values.TryGetValue(sum,out double s) && values.TryGetValue(count,out double n) && n>0
                ?Math.Round(s/n*scale,3):null;
        if(!values.ContainsKey("commands_queue_capacity") ||
            !values.ContainsKey("dispatcher_worker_pool_capacity"))return Invalid();
        return new(TunnelMetricsCode.OK,observed,TunnelProfiles.PrimaryPort,
            Long("commands_enqueued_total"),Long("commands_polled_total"),
            Long("commands_queue_length"),Long("commands_queue_capacity"),
            Long("dispatcher_worker_pool_occupancy"),Long("dispatcher_worker_pool_capacity"),
            Long("commands_poll_latency_seconds_count"),
            Mean("commands_poll_latency_seconds_sum","commands_poll_latency_seconds_count",1000),
            Long("commands_age_seconds_count"),
            Mean("commands_age_seconds_sum","commands_age_seconds_count",1000),
            Long("enqueue_to_response_count"),Mean("enqueue_to_response_sum","enqueue_to_response_count"),
            Long("poll_to_response_count"),Mean("poll_to_response_sum","poll_to_response_count"));
    }
}
