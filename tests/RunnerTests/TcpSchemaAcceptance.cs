using System.Reflection;
using System.Text.Json;

// Regression against dropping the dedicated TCP entry point from the MCP
// tool catalog. WindowsAdminMCP still has the original system_query variant.
internal static class TcpSchemaAcceptance
{
    internal static void Run(Action<bool, string> check)
    {
        var type = typeof(TcpConnectionTools);
        var method = type.GetMethod(nameof(TcpConnectionTools.Query));
        var catalogedType = type.GetCustomAttributesData().Any(attr =>
            attr.AttributeType.Name == "McpServerToolTypeAttribute");
        var catalogedMethod = method?.GetCustomAttributesData().Any(attr =>
            attr.AttributeType.Name == "McpServerToolAttribute" &&
            attr.NamedArguments.Any(named => named.MemberName == "Name" &&
                string.Equals(named.TypedValue.Value as string,
                    "windows.tcp_connections", StringComparison.Ordinal))) == true;
        check(catalogedType && catalogedMethod &&
            method!.ReturnType == typeof(Task<TcpConnectionsResult>) &&
            method.GetParameters().Select(p => p.Name).SequenceEqual(
                ["pid", "page_size", "offset", "cancellationToken"]) &&
            method.GetParameters().All(p => p.IsOptional),
            "issue72.tcp_connections_dedicated_mcp_schema_entrypoint");

        var invalid = TcpConnectionTools.Query(page_size: 0).GetAwaiter().GetResult();
        check(invalid.Kind == TcpConnectionKind.TCP_CONNECTIONS &&
            invalid.Status == SystemQueryCode.INVALID_ARGUMENT &&
            invalid.TcpConnections is null,
            "issue72.tcp_connections_invalid_arguments_fail_before_native_io");

        var observed = new SystemQueryResult(SystemQueryCode.COMPLETED,
            SystemQueryKind.TCP_CONNECTIONS, DateTimeOffset.UtcNow,
            TcpConnections: [new(123, SystemTcpFamily.IPV4, SystemTcpState.LISTEN,
                "127.0.0.1", 8080, null, null)], NextOffset: 4, NativeError: null,
            ElapsedMs: 12);
        var dedicated = TcpConnectionsResult.FromSnapshot(observed);
        check(dedicated is { Status:SystemQueryCode.COMPLETED,
                Kind:TcpConnectionKind.TCP_CONNECTIONS, NextOffset:4, ElapsedMs:12 } &&
            dedicated.TcpConnections is [{ Pid:123, LocalPort:8080 }] &&
            dedicated.ObservedUtc == observed.ObservedUtc,
            "native_schema.tcp_adapter_preserves_snapshot_and_pagination");

        var properties = typeof(TcpConnectionsResult).GetProperties()
            .Select(p => p.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name)
            .ToHashSet(StringComparer.Ordinal);
        check(properties.SetEquals(["status", "kind", "observed_utc", "tcp_connections",
            "next_offset", "native_error", "elapsed_ms"]),
            "native_schema.tcp_result_exact_fields_without_unrelated_domains");

        var json = JsonSerializer.Serialize(invalid, PipeWire.Json);
        check(json.Contains("\"kind\":\"TCP_CONNECTIONS\"", StringComparison.Ordinal) &&
            json.Contains("\"status\":\"INVALID_ARGUMENT\"", StringComparison.Ordinal) &&
            !json.Contains("\"processes\"", StringComparison.Ordinal) &&
            !json.Contains("\"services\"", StringComparison.Ordinal),
            "issue72.tcp_connections_result_wire_enums");
    }
}
