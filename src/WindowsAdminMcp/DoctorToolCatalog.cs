using System.Reflection;
using System.Text.Json.Serialization;

// This is an inspection of the server assembly's declared tools, not a claim
// about the tool list cached by an external ChatGPT connector.
[JsonConverter(typeof(JsonStringEnumConverter<DoctorCatalogSource>))]
public enum DoctorCatalogSource { SERVER_ASSEMBLY_METADATA }

public sealed record DoctorToolCatalog(
    [property: JsonPropertyName("source")] DoctorCatalogSource Source,
    [property: JsonPropertyName("tool_count")] int ToolCount,
    [property: JsonPropertyName("structured_tool_count")] int StructuredToolCount,
    [property: JsonPropertyName("native_dto_result_count")] int NativeDtoResultCount,
    [property: JsonPropertyName("tool_names")] string[] ToolNames);

internal static class DoctorCatalog
{
    // Keep this list aligned with Program.cs registrations. The native stdio
    // acceptance test independently checks the actual generated tools/list.
    private static readonly Type[] RegisteredTypes =
    [
        typeof(AdminTools), typeof(WindowTools), typeof(ChromeConnectionTools),
        typeof(UpdateTools), typeof(FileTools), typeof(SystemTools),
        typeof(TcpConnectionTools), typeof(ExpertDiagnosticsTools),
        typeof(NetworkProbeTools), typeof(NativeAdminTools)
    ];

    private static readonly string[] ExpectedNames =
    [
        "powershell_exec", "powershell_job", "powershell_screenshot",
        "powershell_status", "powershell_update", "windows.admin_action",
        "windows.capture_window", "windows.chrome_connection", "windows.file_acl",
        "windows.file_manage", "windows.file_query", "windows.file_read_chunk",
        "windows.file_write", "windows.inspect_window", "windows.invoke_element",
        "windows.invoke_locator", "windows.list_windows", "windows.manage_window",
        "windows.network_probe", "windows.process_wait_chain", "windows.system_query",
        "windows.tcp_connections"
    ];

    internal static DoctorToolCatalog Observe()
    {
        var names = new List<string>();
        int structured = 0, typed = 0;
        foreach (var type in RegisteredTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var attribute = method.GetCustomAttributesData().FirstOrDefault(a =>
                    a.AttributeType.Name == "McpServerToolAttribute");
                if (attribute is null) continue;
                string? name = attribute.NamedArguments.FirstOrDefault(a => a.MemberName == "Name")
                    .TypedValue.Value as string;
                names.Add(name ?? string.Empty);
                bool isStructured = attribute.NamedArguments.Any(a =>
                    a.MemberName == "UseStructuredContent" && a.TypedValue.Value is true);
                if (!isStructured) continue;
                structured++;
                if (method.ReturnType.IsGenericType &&
                    method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>) &&
                    method.ReturnType.GenericTypeArguments[0] !=
                        typeof(ModelContextProtocol.Protocol.CallToolResult)) typed++;
            }
        }
        names.Sort(StringComparer.Ordinal);
        return new(DoctorCatalogSource.SERVER_ASSEMBLY_METADATA, names.Count, structured, typed, names.ToArray());
    }

    internal static bool IsComplete(DoctorToolCatalog observed) =>
        observed.ToolCount == ExpectedNames.Length && observed.StructuredToolCount == 21 &&
        observed.NativeDtoResultCount == 20 &&
        observed.ToolNames.SequenceEqual(ExpectedNames, StringComparer.Ordinal) &&
        RegisteredTypes.All(type => type.GetCustomAttributesData().Any(a =>
            a.AttributeType.Name == "McpServerToolTypeAttribute"));
}
