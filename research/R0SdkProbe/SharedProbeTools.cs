using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class SharedProbeTools
{
    [McpServerTool(Name = "probe_job", UseStructuredContent = true),
     Description("Submit, inspect or cancel an inert synthetic job owned by the shared service.")]
    public static async Task<PipeReply> Job(string action, string key, int delayMs = 0,
        CancellationToken cancellationToken = default)
    {
        string name = Environment.GetEnvironmentVariable("R0_PIPE") ?? throw new InvalidOperationException("No fixture pipe");
        string request = JsonSerializer.Serialize(new PipeRequest(action, key, delayMs), PipeWire.Json);
        string reply = await PipeWire.RoundTripAsync(name, request, cancellationToken);
        return JsonSerializer.Deserialize<PipeReply>(reply, PipeWire.Json) ?? throw new JsonException("No reply");
    }
}
