using System.Reflection;
using System.Runtime.InteropServices;

public sealed record VersionIdentity(string McpVersion,string AssemblyVersion,string InformationalVersion,
    string DotNetRuntime,string ExpectedTunnelVersion,int StoreSchema);

internal static class BuildIdentity
{
    private static readonly Assembly Assembly=typeof(BuildIdentity).Assembly;
    internal static readonly string InformationalVersion=Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ??throw new InvalidOperationException("Assembly informational version missing.");
    internal static string McpVersion=>InformationalVersion.Split('+')[0];
    internal static VersionIdentity Observe()=>new(McpVersion,Assembly.GetName().Version?.ToString()??"",
        InformationalVersion,RuntimeInformation.FrameworkDescription,"0.0.21",StoreSchema.Current);
}
