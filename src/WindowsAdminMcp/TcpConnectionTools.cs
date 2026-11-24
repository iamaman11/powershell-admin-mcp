using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

// Keep the old windows.system_query variant for existing callers. This small
// dedicated contract lets newer clients query TCP without depending on a
// cached seven-way system_query discriminator or its unrelated result fields.
[JsonConverter(typeof(JsonStringEnumConverter<TcpConnectionKind>))]
public enum TcpConnectionKind { TCP_CONNECTIONS }

public sealed record TcpConnectionsResult(
    [property:JsonPropertyName("status")] SystemQueryCode Status,
    [property:JsonPropertyName("kind")] TcpConnectionKind Kind,
    [property:JsonPropertyName("observed_utc")] DateTimeOffset ObservedUtc,
    [property:JsonPropertyName("tcp_connections"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
        SystemTcpConnectionInfo[]? TcpConnections,
    [property:JsonPropertyName("next_offset"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
        int? NextOffset,
    [property:JsonPropertyName("native_error"),JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
        int? NativeError,
    [property:JsonPropertyName("elapsed_ms")] long ElapsedMs)
{
    internal static TcpConnectionsResult FromSnapshot(SystemQueryResult snapshot)
    {
        if (snapshot.Kind != SystemQueryKind.TCP_CONNECTIONS)
            throw new ArgumentException("NOT_A_TCP_SNAPSHOT", nameof(snapshot));
        return new(snapshot.Status, TcpConnectionKind.TCP_CONNECTIONS, snapshot.ObservedUtc,
            snapshot.TcpConnections, snapshot.NextOffset, snapshot.NativeError, snapshot.ElapsedMs);
    }
}

[McpServerToolType]
public sealed class TcpConnectionTools
{
    [McpServerTool(Name="windows.tcp_connections", UseStructuredContent=true),
     Description("Native bounded read-only Windows TCP IPv4/IPv6 connection snapshot including owning process PID and state. Optional exact PID and paged results; no PowerShell, netstat, outbound connection, or secondary service.")]
    public static Task<TcpConnectionsResult> Query(int? pid=null,int page_size=50,int offset=0,
        CancellationToken cancellationToken=default) =>
        Task.FromResult(TcpConnectionsResult.FromSnapshot(SystemSnapshot.Observe(
            SystemQueryKind.TCP_CONNECTIONS,pid,null,page_size,offset,cancellationToken)));
}
