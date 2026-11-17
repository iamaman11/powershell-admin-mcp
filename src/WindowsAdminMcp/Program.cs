using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

if (args is ["service"])
{
    if (!WindowsServiceHelpers.IsWindowsService())
        throw new InvalidOperationException("Service mode requires SCM.");
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.AddEventLog(settings =>
    {
        settings.SourceName = "WindowsAdminMCP";
        settings.Filter = (_, level) => level >= LogLevel.Information;
    });
    builder.Services.AddWindowsService(options => options.ServiceName = ServiceIdentity.Name);
    builder.Services.AddSingleton<JobCoordinator>();
    builder.Services.AddHostedService<AdminService>();
    await builder.Build().RunAsync();
}
else if (args is ["tunnel"])
{
    Environment.ExitCode = await TunnelHost.RunAsync();
}
else if (args is ["doctor"])
{
    var report=await Doctor.ObserveAsync();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report,PipeWire.Json));
    Environment.ExitCode=report.Status==DoctorState.DEGRADED?1:0;
}
else if (args is ["doctor", "boot"])
{
    // The first read-only component of the unified native doctor.
    var boot = BootEvidence.Observe();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(boot, PipeWire.Json));
    Environment.ExitCode = boot.Status == BootEvidenceCode.FULL_BOOT_OBSERVED ? 0 : 1;
}
else if (args is ["stdio"])
{
    // The SDK frontend is not a runtime credential owner.
    Environment.SetEnvironmentVariable("CONTROL_PLANE_API_KEY", null);
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "windows-admin-mcp", Version = "0.8.0-dev" };
    }).WithStdioServerTransport().WithTools<AdminTools>();
    await builder.Build().RunAsync();
}
else
{
    Console.Error.WriteLine("Usage: WindowsAdminMCP.exe service|stdio|tunnel|doctor [boot]");
    Environment.ExitCode = 2;
}
