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
    builder.Services.AddWindowsService(options => options.ServiceName = ServiceIdentity.Name);
    builder.Services.AddHostedService<AdminService>();
    await builder.Build().RunAsync();
}
else if (args is ["stdio"])
{
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
    Console.Error.WriteLine("Usage: WindowsAdminMCP.exe service|stdio");
    Environment.ExitCode = 2;
}
