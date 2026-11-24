using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

// One authorized CDP socket, one receive loop, bounded per-command waiters.
// A tab command timeout must not destroy the shared Chrome session.
internal static partial class WindowChromeCdp
{
    private const int MaxMessageBytes = 1_048_576;

    // The authorized browser socket has exactly one reader. A command timeout
    // cancels only that command's waiter; a late reply is discarded by its ID.
    // Never cancel ReceiveAsync with an MCP request's short-lived deadline.
    internal sealed class Session : IDisposable
    {
        private const int MaxPendingCalls = 32;
        private sealed record Pending(string? SessionId,
            TaskCompletionSource<JsonElement> Completion);

        private readonly WebSocket _socket;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly ConcurrentDictionary<int, Pending> _pending = new();
        private readonly ConcurrentDictionary<string, byte> _detached =
            new(StringComparer.Ordinal);
        private int _nextId;
        private int _closed;
        private Exception? _failure;
        private CancellationToken _deadline;

        internal Session(WebSocket socket)
        {
            _socket = socket;
            _ = ReceiveLoopAsync();
        }

        internal void SetDeadline(CancellationToken deadline) => _deadline = deadline;

        internal async Task<JsonElement> Call(string method, object parameters, string? sessionId = null)
        {
            try
            {
                return await CallCore(method, parameters, sessionId);
            }
            catch (OperationCanceledException e)
            {
                // The command name is an in-process literal, never page data.
                // Preserve it through the typed error path for causal diagnosis.
                e.Data["cdp_method"] = method;
                throw;
            }
        }

        private async Task<JsonElement> CallCore(string method, object parameters, string? sessionId)
        {
            CancellationToken deadline = _deadline;
            deadline.ThrowIfCancellationRequested();
            ThrowIfUnavailable(sessionId);
            int id = Interlocked.Increment(ref _nextId);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
                { id, method, @params = parameters, sessionId },
                new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
            if (_pending.Count >= MaxPendingCalls)
                throw new InvalidOperationException("CDP_PENDING_LIMIT");
            var completion = new TaskCompletionSource<JsonElement>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            // A canceled waiter may race a fatal transport error. Observe its
            // eventual exception even after the caller has returned.
            _ = completion.Task.ContinueWith(t => { _ = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted);
            if (!_pending.TryAdd(id, new Pending(sessionId, completion)))
                throw new InvalidOperationException("CDP_PENDING_DUPLICATE_ID");

            try
            {
                await _sendGate.WaitAsync(deadline);
                try
                {
                    deadline.ThrowIfCancellationRequested();
                    ThrowIfUnavailable(sessionId);
                    // Do not let a request deadline cancel an in-flight socket
                    // write. If the write itself stalls, abort: its dispatch
                    // status is indeterminate and no later write is safe.
                    Task send = _socket.SendAsync(payload.AsMemory(),
                        WebSocketMessageType.Text, true, _stop.Token).AsTask();
                    try { await send.WaitAsync(deadline); }
                    catch (OperationCanceledException) when (!send.IsCompleted)
                    {
                        _ = send.ContinueWith(t => { _ = t.Exception; },
                            TaskContinuationOptions.OnlyOnFaulted);
                        Fail(new IOException("CDP_SEND_INDETERMINATE"));
                        throw new IOException("CDP_SEND_INDETERMINATE");
                    }
                }
                finally { _sendGate.Release(); }
                return await completion.Task.WaitAsync(deadline);
            }
            finally { _pending.TryRemove(id, out _); }
        }

        private void ThrowIfUnavailable(string? sessionId)
        {
            if (Volatile.Read(ref _failure) is { } failure) throw failure;
            if (sessionId is not null && _detached.ContainsKey(sessionId))
                throw new InvalidOperationException("CDP_TARGET_DETACHED");
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[16384];
            using var stream = new MemoryStream();
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    stream.SetLength(0);
                    WebSocketReceiveResult part;
                    do
                    {
                        part = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _stop.Token);
                        if (part.MessageType == WebSocketMessageType.Close)
                            throw new IOException("CDP_SOCKET_CLOSED");
                        if (part.MessageType != WebSocketMessageType.Text ||
                            stream.Length + part.Count > MaxMessageBytes)
                            throw new JsonException("CDP_MESSAGE_INVALID");
                        stream.Write(buffer, 0, part.Count);
                    } while (!part.EndOfMessage);

                    using var response = JsonDocument.Parse(stream.ToArray());
                    var message = response.RootElement;
                    if (!message.TryGetProperty("id", out var responseId))
                    {
                        if (message.TryGetProperty("method", out var eventMethod) &&
                            eventMethod.ValueKind == JsonValueKind.String &&
                            eventMethod.GetString() == "Inspector.detached" &&
                            message.TryGetProperty("sessionId", out var eventSession) &&
                            eventSession.ValueKind == JsonValueKind.String &&
                            eventSession.GetString() is { } detachedSession)
                        {
                            if (_detached.Count >= 4096)
                                throw new JsonException("CDP_DETACHED_SESSION_LIMIT");
                            _detached.TryAdd(detachedSession, 0);
                            foreach (var pending in _pending.Values)
                                if (pending.SessionId == detachedSession)
                                    pending.Completion.TrySetException(
                                        new InvalidOperationException("CDP_TARGET_DETACHED"));
                        }
                        continue;
                    }
                    if (responseId.ValueKind != JsonValueKind.Number ||
                        !responseId.TryGetInt32(out int id) || id <= 0)
                        throw new JsonException("CDP_RESPONSE_ID_INVALID");
                    if (!_pending.TryGetValue(id, out var waiter)) continue;
                    string? responseSession = null;
                    if (message.TryGetProperty("sessionId", out var targetSession))
                    {
                        if (targetSession.ValueKind is not
                            (JsonValueKind.String or JsonValueKind.Null))
                            throw new JsonException("CDP_RESPONSE_SESSION_INVALID");
                        responseSession = targetSession.GetString();
                    }
                    if (responseSession != waiter.SessionId)
                        throw new JsonException("CDP_RESPONSE_SESSION_MISMATCH");
                    if (message.TryGetProperty("error", out _))
                        waiter.Completion.TrySetException(
                            new InvalidOperationException("CDP_COMMAND_REJECTED"));
                    else if (message.TryGetProperty("result", out var result))
                        waiter.Completion.TrySetResult(result.Clone());
                    else throw new JsonException("CDP_RESPONSE_RESULT_MISSING");
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException e)
            { Fail(new IOException("CDP_SOCKET_CLOSED", e)); }
            catch (Exception e) when (e is IOException or WebSocketException or JsonException or
                OperationCanceledException or InvalidOperationException)
            { Fail(e); }
        }

        private void Fail(Exception error)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            Volatile.Write(ref _failure, error);
            _stop.Cancel();
            _socket.Abort();
            foreach (var pending in _pending.Values)
                pending.Completion.TrySetException(error);
        }

        public void Dispose() => Fail(new IOException("CDP_SOCKET_CLOSED"));
    }
}
