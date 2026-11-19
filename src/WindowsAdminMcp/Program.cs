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
else if (args is ["maintenance", "store-schema"])
{
    var schema=StoreSchema.ObserveOffline();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(schema,PipeWire.Json));
    Environment.ExitCode=schema.Status==StoreSchemaCode.READBACK_FAILED?1:0;
}
else if (args is ["doctor"] || args is ["doctor","--evidence",_])
{
    var report=await Doctor.ObserveAsync(args.Length==3?args[2]:null);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report,PipeWire.Json));
    Environment.ExitCode=report.Status==DoctorState.OK?0:1;
}
else if (args is ["maintenance", "recovery"] || args is ["maintenance", "configure-recovery"])
{
    var recovery=args[1]=="configure-recovery"?ServiceRecovery.Configure():ServiceRecovery.Observe();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(recovery,PipeWire.Json));
}
else if (args is ["doctor", "boot"])
{
    // The first read-only component of the unified native doctor.
    var boot = BootEvidence.Observe();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(boot, PipeWire.Json));
    Environment.ExitCode = boot.Status == BootEvidenceCode.FULL_BOOT_OBSERVED ? 0 : 1;
}
else if (args is ["window-inspect-worker", var windowId, var depthText, var maxText,
    var pathText, var refText, var offsetText])
{
    // Short-lived on-demand UIA reader; independently validates USER desktop.
    WindowInspectResult result = int.TryParse(depthText, out int depth) &&
        int.TryParse(maxText, out int maxElements) && int.TryParse(offsetText, out int offset)
        ? WindowInspector.InspectInWorker(windowId, depth, maxElements,
            pathText == "-" ? null : pathText, refText == "-" ? null : refText, offset)
        : new(WindowInspectCode.INVALID_ARGUMENT, []);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, PipeWire.Json));
}
else if (args is ["window-capture-worker", var captureId, var widthText])
{
    // Runs only on the authorized USER desktop; WGC never calls desktop CopyFromScreen.
    var wire = int.TryParse(widthText, out int maxWidth)
        ? await WindowCapture.CaptureInWorkerAsync(captureId, maxWidth)
        : new WindowCaptureWire(new(WindowCaptureCode.INVALID_ARGUMENT));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(wire, PipeWire.Json));
}
else if (args is ["window-action-worker"])
{
    // No action values in process arguments. Parent sends bounded JSON via stdin.
    WindowActionResult result;
    try
    {
        char[] chars = new char[16001];
        int length = 0;
        while (length < chars.Length)
        {
            int read = await Console.In.ReadAsync(chars.AsMemory(length));
            if (read == 0) break;
            length += read;
        }
        var request = length <= 16000
            ? System.Text.Json.JsonSerializer.Deserialize<WindowActionRequest>(
                new string(chars, 0, length), PipeWire.Json)
            : null;
        result = request is null ? new(WindowActionCode.INVALID_ARGUMENT) :
            WindowAction.InvokeInWorker(request);
    }
    catch (System.Text.Json.JsonException)
    {
        result = new(WindowActionCode.INVALID_ARGUMENT);
    }
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, PipeWire.Json));
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
        options.ServerInfo = new() { Name = "windows-admin-mcp", Version = BuildIdentity.McpVersion };
    }).WithStdioServerTransport().WithTools<AdminTools>().WithTools<WindowTools>();
    await builder.Build().RunAsync();
}
else
{
    Console.Error.WriteLine("Usage: WindowsAdminMCP.exe service|stdio|tunnel|doctor [boot|--evidence path]|maintenance store-schema|recovery|configure-recovery");
    Environment.ExitCode = 2;
}
