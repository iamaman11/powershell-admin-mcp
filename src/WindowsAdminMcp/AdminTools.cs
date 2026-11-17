using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class AdminTools
{
    [McpServerTool(Name = "powershell_status", UseStructuredContent = true),
        Description("Read actual Windows service status and execution identity.")]
    public static async Task<AdminStatus> Status(CancellationToken cancellationToken)
    {
        string correlation = Guid.NewGuid().ToString("N");
        try
        {
            uint pid = ServiceIdentity.RunningProcessId();
            string payload = JsonSerializer.Serialize(new AdminRequest(AdminOperation.Status), PipeWire.Json);
            string reply = await PipeWire.RoundTripAsync(ServiceIdentity.Pipe, payload, pid, cancellationToken);
            var status = JsonSerializer.Deserialize<AdminStatus>(reply, PipeWire.Json);
            if (status is null || string.IsNullOrWhiteSpace(status.CorrelationId) ||
                (status.Status == StatusCode.OK && status.ServicePid != pid))
                return new(StatusCode.IPC_PROTOCOL_ERROR, correlation);
            return status;
        }
        catch (Win32Exception e)
        {
            return new(e.NativeErrorCode == 5 ? StatusCode.ACCESS_DENIED : StatusCode.SERVICE_UNAVAILABLE,
                correlation, NativeError: e.NativeErrorCode);
        }
        catch (UnauthorizedAccessException) { return new(StatusCode.ACCESS_DENIED, correlation); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(StatusCode.IPC_TIMEOUT, correlation); }
        catch (JsonException) { return new(StatusCode.IPC_PROTOCOL_ERROR, correlation); }
        catch (System.Text.DecoderFallbackException) { return new(StatusCode.IPC_PROTOCOL_ERROR, correlation); }
        catch (IOException e) { return new(StatusCode.IPC_FAILED, correlation, NativeError: e.HResult); }
    }
}
