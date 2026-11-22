using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

public sealed record VersionIdentity(
    [property:JsonPropertyName("mcp_version")] string McpVersion,
    [property:JsonPropertyName("assembly_version")] string AssemblyVersion,
    [property:JsonPropertyName("informational_version")] string InformationalVersion,
    [property:JsonPropertyName("dotnet_runtime")] string DotNetRuntime,
    [property:JsonPropertyName("expected_tunnel_version")] string ExpectedTunnelVersion,
    [property:JsonPropertyName("store_schema")] int StoreSchema);

internal static class BuildIdentity
{
    private static readonly Assembly Assembly=typeof(BuildIdentity).Assembly;
    internal static readonly string InformationalVersion=Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ??throw new InvalidOperationException("Assembly informational version missing.");
    internal static string McpVersion=>InformationalVersion.Split('+')[0];
    internal static VersionIdentity Observe()=>new(McpVersion,Assembly.GetName().Version?.ToString()??"",
        InformationalVersion,RuntimeInformation.FrameworkDescription,VendorLock.Version,StoreSchema.Current);
}
