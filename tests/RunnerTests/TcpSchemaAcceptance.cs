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
            method!.ReturnType == typeof(Task<SystemQueryResult>) &&
            method.GetParameters().Select(p => p.Name).SequenceEqual(
                ["pid", "page_size", "offset", "cancellationToken"]) &&
            method.GetParameters().All(p => p.IsOptional),
            "issue72.tcp_connections_dedicated_mcp_schema_entrypoint");

        var invalid = TcpConnectionTools.Query(page_size: 0).GetAwaiter().GetResult();
        check(invalid.Kind == SystemQueryKind.TCP_CONNECTIONS &&
            invalid.Status == SystemQueryCode.INVALID_ARGUMENT &&
            invalid.TcpConnections is null,
            "issue72.tcp_connections_invalid_arguments_fail_before_native_io");

        var json = JsonSerializer.Serialize(invalid, PipeWire.Json);
        check(json.Contains("\"kind\":\"TCP_CONNECTIONS\"", StringComparison.Ordinal) &&
            json.Contains("\"status\":\"INVALID_ARGUMENT\"", StringComparison.Ordinal),
            "issue72.tcp_connections_result_wire_enums");
    }
}
