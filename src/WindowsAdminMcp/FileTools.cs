using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using ModelContextProtocol.Server;

// Two explicit read-only tools; no arbitrary script wrapper and no additional
// process or writer. A future staged write API will use the SQLite job owner.
[McpServerToolType]
public sealed class FileTools
{
    [McpServerTool(Name = "windows.file_query", UseStructuredContent = true),
     Description("Native file STAT or paged directory LIST, with exact USER/SYSTEM context and change-aware cursor.")]
    public static Task<NativeFileReply> Query(FileQueryAction action, string path,
        int page_size = 50, string? cursor = null, bool include_sha256 = false,
        CommandContext execution_context = CommandContext.USER,
        CancellationToken cancellationToken = default) =>
        Dispatch(new(action switch
            {
                FileQueryAction.STAT => NativeFileAction.STAT,
                FileQueryAction.LIST => NativeFileAction.LIST,
                _ => (NativeFileAction)(-1)
            }, path, execution_context, page_size, cursor, include_sha256), cancellationToken);

    [McpServerTool(Name = "windows.file_read_chunk", UseStructuredContent = true),
     Description("Read exact binary file bytes (base64), offset 0..EOF, length 1..262144, SHA256 and version.")]
    public static Task<NativeFileReply> ReadChunk(string path, long offset, int length = 65536,
        string? expected_version = null, CommandContext execution_context = CommandContext.USER,
        CancellationToken cancellationToken = default) =>
        Dispatch(new(NativeFileAction.READ_CHUNK, path, execution_context,
            Offset:offset, Length:length, ExpectedVersion:expected_version), cancellationToken);

    private static async Task<NativeFileReply> Dispatch(NativeFileRequest request, CancellationToken ct)
    {
        if (!NativeFileReader.Valid(request))
            return new(NativeFileCode.INVALID_ARGUMENT, request.ExecutionContext, DateTimeOffset.UtcNow,
                Stage:"VALIDATE");
        if (request.ExecutionContext == CommandContext.SYSTEM)
        {
            var reply = await AdminTools.Call(new(AdminOperation.File, File:request), ct);
            return reply.File ?? new(reply.Code is StatusCode.ACCESS_DENIED ? NativeFileCode.ACCESS_DENIED :
                NativeFileCode.SERVICE_UNAVAILABLE, CommandContext.SYSTEM, DateTimeOffset.UtcNow,
                Stage:"TRANSPORT");
        }
        var status = await AdminTools.Status(cancellationToken:ct);
        if (status.Status != StatusCode.OK)
            return new(NativeFileCode.SERVICE_UNAVAILABLE, CommandContext.USER,
                DateTimeOffset.UtcNow, Stage:"TRANSPORT");
        using var self = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        // Files do not need the interactive PowerShell USER job worker or a
        // desktop. The elevated frontend token is the actual file authority.
        if (identity.IsSystem || status.Frontend?.Elevated != true ||
            status.Frontend.SessionId != self.SessionId ||
            status.Frontend.Sid != identity.User?.Value ||
            !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return new(NativeFileCode.USER_CONTEXT_UNAVAILABLE, CommandContext.USER,
                DateTimeOffset.UtcNow, Stage:"VALIDATE");
        return await NativeFileReader.ExecuteAsync(request, ct);
    }
}
