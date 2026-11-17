using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// R0 only: no execution, network listener, credentials, or Windows administration.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ProbeTools>();
await builder.Build().RunAsync();

public sealed record ProbeResponse(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("status")] string Status);

[McpServerToolType]
public sealed class ProbeTools
{
    private static int _started;
    private static int _cancelled;

    [McpServerTool(Name = "probe_echo", UseStructuredContent = true), Description("Echo inert text.")]
    public static ProbeResponse Echo(string value) => new(value, "ok");

    [McpServerTool(Name = "probe_png"), Description("Return a fixed one-pixel PNG fixture, not the desktop.")]
    public static ImageContentBlock Png() => new()
    {
        MimeType = "image/png",
        Data = Encoding.ASCII.GetBytes("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jJ2sAAAAASUVORK5CYII=")
    };

    [McpServerTool(Name = "probe_wait"), Description("Wait for cancellation; does not change the operating system.")]
    public static async Task<string> Wait(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _started);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
            return "finished";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _cancelled);
            throw;
        }
    }

    [McpServerTool(Name = "probe_counters", UseStructuredContent = true), Description("Read local cancellation counters.")]
    public static Counters ReadCounters() => new(
        Volatile.Read(ref _started), Volatile.Read(ref _cancelled));
}

public sealed record Counters(
    [property: JsonPropertyName("started")] int Started,
    [property: JsonPropertyName("cancelled")] int Cancelled);
