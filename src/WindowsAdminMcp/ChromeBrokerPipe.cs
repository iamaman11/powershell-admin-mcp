using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

// The existing interactive TunnelHost owns this USER-session endpoint. Independent
// MCP stdio frontends are clients, never owners of a Chrome consented socket.
// CurrentUserOnly and the Windows session-specific pipe name prevent cross-user
// and cross-desktop dispatch without introducing a service or network listener.
internal static class ChromeBrokerPipe
{
    private const int MaxClients = 8;
    private const int MaxRequestBytes = 32_768;
    private const int MaxReplyBytes = 262_144;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;
    private static int _owner;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access,
        out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token, int informationClass,
        out int elevation, int length, out int actualLength);

    internal static bool IsOwner => Volatile.Read(ref _owner) == 1;

    private static string Name(int sessionId) => $"WindowsAdminMCP.ChromeBroker.s{sessionId}";

    internal static async Task RunOwnerAsync(CancellationToken stop)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        // Unlike a Mutex, a kernel Semaphore may be released after an await
        // on a different thread. A failed nonblocking acquisition is fail-closed.
        using var singleton = new Semaphore(1, 1,
            $@"Local\WindowsAdminMCP.ChromeBroker.s{process.SessionId}");
        if (!singleton.WaitOne(0))
            throw new InvalidOperationException("CHROME_BROKER_ALREADY_OWNED");
        var clients = new HashSet<Task>();
        Volatile.Write(ref _owner, 1);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var completed in clients.Where(task => task.IsCompleted).ToArray())
                {
                    clients.Remove(completed);
                    await completed;
                }
                if (clients.Count == MaxClients)
                {
                    await Task.WhenAny(clients).WaitAsync(stop);
                    continue;
                }
                var pipe = new NamedPipeServerStream(Name(process.SessionId), PipeDirection.InOut,
                    MaxClients, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(stop); }
                catch
                {
                    await pipe.DisposeAsync();
                    throw;
                }
                clients.Add(HandleAsync(pipe, stop));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            Volatile.Write(ref _owner, 0);
            try { await Task.WhenAll(clients); }
            finally
            {
                WindowChromeBroker.StopOwnedWorker();
                singleton.Release();
            }
        }
    }

    private static async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        await using (pipe)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                // Same SID alone is insufficient: a non-elevated application
                // of the same user must not borrow the admin frontend's rights.
                if (!AuthorizedClient(pipe)) return;
                byte[] input = await ReadFrameAsync(pipe, MaxRequestBytes, timeout.Token);
                var request = JsonSerializer.Deserialize<ChromeBrokerRequest>(input, PipeWire.Json);
                if (request is null || !request.Valid) return;
                var reply = await WindowChromeBroker.ExchangeOwnedAsync(request, timeout.Token);
                if (reply is not null)
                    await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(
                        reply with { OwnerPid = Environment.ProcessId }, PipeWire.Json),
                        MaxReplyBytes, timeout.Token);
            }
            catch (Exception e) when (e is IOException or EndOfStreamException or JsonException or
                OperationCanceledException or InvalidOperationException or ArgumentException)
            { /* Client abort or invalid frame; no dispatch replay. */ }
        }
    }

    private static bool AuthorizedClient(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid == 0 ||
            pid > int.MaxValue) return false;
        try
        {
            using var self = Process.GetCurrentProcess();
            using var client = Process.GetProcessById((int)pid);
            if (client.SessionId != self.SessionId ||
                !string.Equals(client.MainModule?.FileName, self.MainModule?.FileName,
                    StringComparison.OrdinalIgnoreCase)) return false;
            if (!OpenProcessToken(client.Handle, TokenQuery, out var token)) return false;
            using (token)
                return GetTokenInformation(token, TokenElevationClass, out int elevated,
                    sizeof(int), out int size) && size == sizeof(int) && elevated != 0;
        }
        catch (Exception e) when (e is Win32Exception or ArgumentException or
            InvalidOperationException or UnauthorizedAccessException or
            System.Security.SecurityException) { return false; }
    }

    internal static async Task<ChromeBrokerReply?> ExchangeAsync(ChromeBrokerRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Valid) return null;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        await using var pipe = new NamedPipeClientStream(".", Name(process.SessionId),
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(8000, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(request, PipeWire.Json),
                MaxRequestBytes, timeout.Token);
            byte[] reply = await ReadFrameAsync(pipe, MaxReplyBytes, timeout.Token);
            var decoded = JsonSerializer.Deserialize<ChromeBrokerReply>(reply, PipeWire.Json);
            return WindowChromeBroker.ReplyMatches(request, decoded) ? decoded : null;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or JsonException or
            OperationCanceledException or TimeoutException or InvalidOperationException or
            UnauthorizedAccessException)
        { return null; }
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, int limit, CancellationToken stop)
    {
        byte[] length = new byte[4];
        await stream.ReadExactlyAsync(length, stop);
        int bytes = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (bytes is < 2 || bytes > limit) throw new InvalidDataException("CDP_FRAME_LENGTH");
        byte[] payload = new byte[bytes];
        await stream.ReadExactlyAsync(payload, stop);
        return payload;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, int limit,
        CancellationToken stop)
    {
        if (payload.Length is < 2 || payload.Length > limit)
            throw new InvalidDataException("CDP_FRAME_LENGTH");
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, stop);
        await stream.WriteAsync(payload, stop);
        await stream.FlushAsync(stop);
    }
}
