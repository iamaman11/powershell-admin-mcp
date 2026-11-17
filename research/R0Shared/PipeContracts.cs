using System.Text.Json.Serialization;

internal sealed record PipeRequest(string Action, string? Key = null, int DelayMs = 0);
public sealed record PipeReply(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("id")] long? Id = null,
    [property: JsonPropertyName("state")] string? State = null,
    [property: JsonPropertyName("result")] string? Result = null);

