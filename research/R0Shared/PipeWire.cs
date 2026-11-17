using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed record PipeRequest(string Action, string? Key = null, int DelayMs = 0);
public sealed record PipeReply(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("id")] long? Id = null,
    [property: JsonPropertyName("state")] string? State = null,
    [property: JsonPropertyName("result")] string? Result = null);

internal static class PipeWire
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    // Explicit small length-prefixed frames; no unbounded ReadLine or new protocol server.
    internal static async Task<string> ReadFrameAsync(Stream s, CancellationToken token)
    {
        byte[] header = new byte[4];
        await s.ReadExactlyAsync(header, token);
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 4096) throw new IOException("Invalid fixture frame length");
        byte[] body = new byte[length];
        await s.ReadExactlyAsync(body, token);
        return new UTF8Encoding(false, true).GetString(body);
    }
    internal static async Task WriteFrameAsync(Stream s, string value, CancellationToken token)
    {
        byte[] body = Encoding.UTF8.GetBytes(value);
        if (body.Length is < 1 or > 4096) throw new IOException("Invalid fixture frame length");
        byte[] header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await s.WriteAsync(header, token);
        await s.WriteAsync(body, token);
        await s.FlushAsync(token);
    }


    internal static async Task<string> RoundTripAsync(string name, string payload, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(deadline.Token);
        await WriteFrameAsync(pipe, payload, deadline.Token);
        return await ReadFrameAsync(pipe, deadline.Token);
    }
}
