using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

// These deliberately active diagnostics make outbound connections only to a
// caller-supplied, validated endpoint. Unlike a passive system snapshot, they
// must never inherit a browser proxy, Windows credentials or cookies.
[JsonConverter(typeof(JsonStringEnumConverter<NetworkProbeKind>))]
public enum NetworkProbeKind
{
    DNS, TCP_CONNECT, TLS_HANDSHAKE, HTTP_HEAD, HTTPS_HEAD, HTTP_GET, HTTPS_GET
}

[JsonConverter(typeof(JsonStringEnumConverter<NetworkProbeCode>))]
public enum NetworkProbeCode
{
    COMPLETED, INVALID_ARGUMENT, UNSUPPORTED_PLATFORM, NAME_NOT_RESOLVED,
    CONNECTION_REFUSED, NETWORK_UNREACHABLE, TLS_VALIDATION_FAILED,
    CONNECTION_FAILED, HTTP_FAILED, TIMEOUT, CANCELLED, RESOURCE_LIMIT
}

public sealed record NetworkProbeResult(
    [property: JsonPropertyName("status")] NetworkProbeCode Status,
    [property: JsonPropertyName("kind")] NetworkProbeKind Kind,
    [property: JsonPropertyName("observed_utc")] DateTimeOffset ObservedUtc,
    [property: JsonPropertyName("elapsed_ms")] long ElapsedMs,
    [property: JsonPropertyName("outbound_connection_attempted")] bool OutboundConnectionAttempted,
    [property: JsonPropertyName("resolved_addresses"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string[]? ResolvedAddresses = null,
    [property: JsonPropertyName("addresses_truncated")] bool AddressesTruncated = false,
    [property: JsonPropertyName("connected_address"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ConnectedAddress = null,
    [property: JsonPropertyName("http_status_code"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? HttpStatusCode = null,
    [property: JsonPropertyName("http_version"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? HttpVersion = null,
    [property: JsonPropertyName("sampled_body_bytes")] int SampledBodyBytes = 0,
    [property: JsonPropertyName("body_truncated")] bool BodyTruncated = false,
    [property: JsonPropertyName("tls_protocol"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? TlsProtocol = null,
    [property: JsonPropertyName("cipher_suite"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? CipherSuite = null,
    [property: JsonPropertyName("peer_certificate_sha256"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? PeerCertificateSha256 = null,
    [property: JsonPropertyName("socket_error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? SocketError = null);

internal static class NativeNetworkProbe
{
    private const int AddressLimit = 16;
    private const int ConnectionAttemptLimit = 4;

    internal static bool TryValidate(NetworkProbeKind kind, string? host, int? port,
        string? pathAndQuery, int timeoutMs, bool allowHttpGet, int maxResponseBytes,
        out string normalizedHost, out int effectivePort)
    {
        normalizedHost = "";
        effectivePort = 0;
        if (!Enum.IsDefined(kind) || host is null || host.Length is < 1 or > 253 ||
            timeoutMs is < 250 or > 30000 || maxResponseBytes is < 0 or > 4096 ||
            pathAndQuery is null || pathAndQuery.Length is < 1 or > 2048)
            return false;
        if (!TryNormalizeHost(host, out normalizedHost)) return false;

        bool http = kind is NetworkProbeKind.HTTP_HEAD or NetworkProbeKind.HTTPS_HEAD or
            NetworkProbeKind.HTTP_GET or NetworkProbeKind.HTTPS_GET;
        bool get = kind is NetworkProbeKind.HTTP_GET or NetworkProbeKind.HTTPS_GET;
        if (allowHttpGet != get) return false;
        if (!http && (pathAndQuery != "/" || maxResponseBytes != 4096)) return false;
        if (http && (!pathAndQuery.StartsWith("/", StringComparison.Ordinal) ||
            pathAndQuery.StartsWith("//", StringComparison.Ordinal) ||
            pathAndQuery.Any(c => char.IsControl(c) || c is '\\' or '#' or '@'))) return false;
        if (kind == NetworkProbeKind.DNS)
            return port is null;

        effectivePort = port ?? (kind switch
        {
            NetworkProbeKind.HTTP_HEAD or NetworkProbeKind.HTTP_GET => 80,
            NetworkProbeKind.HTTPS_HEAD or NetworkProbeKind.HTTPS_GET => 443,
            _ => 0
        });
        return effectivePort is >= 1 and <= 65535;
    }

    private static bool TryNormalizeHost(string value, out string host)
    {
        host = "";
        // Reject URI syntax, userinfo, zone-specific IPs, shell metacharacters
        // and leading/trailing whitespace. IP literals are accepted explicitly.
        if (value != value.Trim() || (value.Any(c => char.IsControl(c) ||
            c is '/' or '\\' or '@' or '?' or '#' or '%' or '[' or ']' or ':') &&
            !IPAddress.TryParse(value, out _))) return false;
        if (IPAddress.TryParse(value, out var ip))
        {
            if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0)
                return false;
            host = ip.ToString();
            return true;
        }
        try
        {
            host = new IdnMapping().GetAscii(value.TrimEnd('.')).ToLowerInvariant();
        }
        catch (ArgumentException) { return false; }
        if (host.Length is < 1 or > 253 || host.StartsWith(".", StringComparison.Ordinal) ||
            host.Contains("..", StringComparison.Ordinal)) return false;
        foreach (var label in host.Split('.'))
        {
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.Any(c => !((c is >= 'a' and <= 'z') ||
                                 (c is >= '0' and <= '9') || c == '-')))
                return false;
        }
        return true;
    }

    internal static async Task<NetworkProbeResult> RunAsync(NetworkProbeKind kind, string? host,
        int? port, string? pathAndQuery, int timeoutMs, bool allowHttpGet,
        int maxResponseBytes, CancellationToken cancellationToken)
    {
        var at = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        bool attempted = false;
        bool truncated = false;
        string[]? resolved = null;
        string? connected = null;
        int? httpStatus = null;
        string? httpVersion = null;
        int sampledBytes = 0;
        bool bodyTruncated = false;
        string? tlsProtocol = null;
        string? cipherSuite = null;
        string? certificateSha256 = null;

        NetworkProbeResult Result(NetworkProbeCode code, string? socketError = null) =>
            new(code, kind, at, timer.ElapsedMilliseconds, attempted, resolved, truncated,
                connected, httpStatus, httpVersion, sampledBytes, bodyTruncated,
                tlsProtocol, cipherSuite, certificateSha256, socketError);

        if (!TryValidate(kind, host, port, pathAndQuery, timeoutMs, allowHttpGet,
            maxResponseBytes, out var normalizedHost, out var effectivePort))
            return Result(NetworkProbeCode.INVALID_ARGUMENT);
        if (!OperatingSystem.IsWindows()) return Result(NetworkProbeCode.UNSUPPORTED_PLATFORM);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        try
        {
            IPAddress[] addresses = IPAddress.TryParse(normalizedHost, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(normalizedHost, deadline.Token);
            // Ignore non-TCP address families; do not expose unbounded DNS data.
            var available = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork ||
                (a.AddressFamily == AddressFamily.InterNetworkV6 && a.ScopeId == 0))
                .Distinct().ToArray();
            resolved = available.Take(AddressLimit).Select(a => a.ToString()).ToArray();
            truncated = available.Length > AddressLimit;
            if (available.Length == 0) return Result(NetworkProbeCode.NAME_NOT_RESOLVED);
            if (kind == NetworkProbeKind.DNS) return Result(NetworkProbeCode.COMPLETED);
            // Never accidentally exhaust the budget on just AAAA records when
            // an A record also exists (or conversely). Still cap total attempts.
            var candidates = available.Take(2)
                .Concat(available.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(2))
                .Concat(available.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).Take(2))
                .Distinct().Take(ConnectionAttemptLimit).ToArray();

            if (kind is NetworkProbeKind.TCP_CONNECT or NetworkProbeKind.TLS_HANDSHAKE)
            {
                Socket? socket = null;
                SocketException? lastSocketError = null;
                foreach (var address in candidates)
                {
                    var attempt = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        attempted = true;
                        await attempt.ConnectAsync(new IPEndPoint(address, effectivePort), deadline.Token);
                        socket = attempt;
                        connected = address.ToString();
                        break;
                    }
                    catch (SocketException ex)
                    {
                        lastSocketError = ex;
                        attempt.Dispose();
                    }
                    catch { attempt.Dispose(); throw; }
                }
                if (socket is null)
                    return Result(lastSocketError is null ? NetworkProbeCode.CONNECTION_FAILED :
                        MapSocketError(lastSocketError.SocketErrorCode),
                        lastSocketError?.SocketErrorCode.ToString());
                using (socket)
                {
                    if (kind == NetworkProbeKind.TCP_CONNECT) return Result(NetworkProbeCode.COMPLETED);
                    using var stream = new NetworkStream(socket, ownsSocket: false);
                    using var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = normalizedHost,
                        CertificateRevocationCheckMode = X509RevocationMode.Online
                    }, deadline.Token);
                    tlsProtocol = ssl.SslProtocol.ToString();
                    cipherSuite = ssl.NegotiatedCipherSuite.ToString();
                    if (ssl.RemoteCertificate is not null)
                    {
                        using var cert = new X509Certificate2(ssl.RemoteCertificate);
                        certificateSha256 = cert.GetCertHashString(HashAlgorithmName.SHA256);
                    }
                    return Result(NetworkProbeCode.COMPLETED);
                }
            }

            var secure = kind is NetworkProbeKind.HTTPS_HEAD or NetworkProbeKind.HTTPS_GET;
            var useGet = kind is NetworkProbeKind.HTTP_GET or NetworkProbeKind.HTTPS_GET;
            var queryAt = pathAndQuery!.IndexOf('?');
            var builder = new UriBuilder(secure ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
                normalizedHost, effectivePort)
            {
                Path = queryAt < 0 ? pathAndQuery : pathAndQuery[..queryAt],
                Query = queryAt < 0 ? "" : pathAndQuery[(queryAt + 1)..]
            };
            var target = builder.Uri;
            // Resolve exactly once and pin the connection. HttpClient must not
            // resolve an unrelated address after an attacker changes the DNS answer.
            var pinned = candidates;
            int connectionAttempts = 0;
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.None,
                MaxConnectionsPerServer = 1,
                MaxResponseHeadersLength = 16,
                ConnectTimeout = TimeSpan.FromMilliseconds(timeoutMs),
                ConnectCallback = async (_, ct) =>
                {
                    SocketException? last = null;
                    foreach (var address in pinned)
                    {
                        if (Interlocked.Increment(ref connectionAttempts) > ConnectionAttemptLimit)
                            throw new SocketException((int)System.Net.Sockets.SocketError.TooManyOpenSockets);
                        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            attempted = true;
                            await socket.ConnectAsync(new IPEndPoint(address, effectivePort), ct);
                            connected = address.ToString();
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch (SocketException ex) { last = ex; socket.Dispose(); }
                        catch { socket.Dispose(); throw; }
                    }
                    throw last ?? new SocketException((int)System.Net.Sockets.SocketError.HostUnreachable);
                }
            };
            handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.Online;
            using var client = new HttpClient(handler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            using var request = new HttpRequestMessage(useGet ? HttpMethod.Get : HttpMethod.Head, target);
            using var response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            httpStatus = (int)response.StatusCode;
            httpVersion = response.Version.ToString();
            if (useGet)
            {
                using var responseStream = await response.Content.ReadAsStreamAsync(deadline.Token);
                var buffer = new byte[maxResponseBytes + 1];
                while (sampledBytes < buffer.Length)
                {
                    var read = await responseStream.ReadAsync(buffer.AsMemory(sampledBytes), deadline.Token);
                    if (read == 0) break;
                    sampledBytes += read;
                }
                bodyTruncated = sampledBytes > maxResponseBytes;
                sampledBytes = Math.Min(sampledBytes, maxResponseBytes);
                // Deliberately do not return the response body, cookies or headers.
            }
            return Result(NetworkProbeCode.COMPLETED);
        }
        catch (OperationCanceledException)
        {
            return Result(cancellationToken.IsCancellationRequested ?
                NetworkProbeCode.CANCELLED : NetworkProbeCode.TIMEOUT);
        }
        catch (AuthenticationException) { return Result(NetworkProbeCode.TLS_VALIDATION_FAILED); }
        catch (CryptographicException) { return Result(NetworkProbeCode.TLS_VALIDATION_FAILED); }
        catch (SocketException ex) { return Result(MapSocketError(ex.SocketErrorCode), ex.SocketErrorCode.ToString()); }
        catch (HttpRequestException ex) when (HasAuthenticationFailure(ex))
        {
            return Result(NetworkProbeCode.TLS_VALIDATION_FAILED);
        }
        catch (HttpRequestException ex) when (FindSocketError(ex) is { } error)
        {
            return Result(MapSocketError(error), error.ToString());
        }
        catch (HttpRequestException) { return Result(NetworkProbeCode.HTTP_FAILED); }
        catch (IOException) { return Result(NetworkProbeCode.CONNECTION_FAILED); }
        catch (ArgumentException) { return Result(NetworkProbeCode.INVALID_ARGUMENT); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return Result(NetworkProbeCode.CONNECTION_FAILED);
        }
    }

    private static bool HasAuthenticationFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is AuthenticationException) return true;
        return false;
    }

    private static SocketError? FindSocketError(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is SocketException socket) return socket.SocketErrorCode;
        return null;
    }

    private static NetworkProbeCode MapSocketError(SocketError code) => code switch
    {
        SocketError.ConnectionRefused => NetworkProbeCode.CONNECTION_REFUSED,
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
            NetworkProbeCode.NAME_NOT_RESOLVED,
        SocketError.NetworkDown or SocketError.NetworkUnreachable or
            SocketError.HostUnreachable or SocketError.AddressNotAvailable =>
            NetworkProbeCode.NETWORK_UNREACHABLE,
        SocketError.TimedOut => NetworkProbeCode.TIMEOUT,
        _ => NetworkProbeCode.CONNECTION_FAILED
    };
}

[McpServerToolType]
public sealed class NetworkProbeTools
{
    [McpServerTool(Name = "windows.network_probe", UseStructuredContent = true),
     Description("Bounded native DNS/TCP/TLS/HTTPS probe to exact host. GET requires opt-in and can cause effects. No credentials, cookies, proxy, redirects or response body. Timeout 250..30000 ms.")]
    public static Task<NetworkProbeResult> Probe(NetworkProbeKind kind, string host,
        int? port = null, string path_and_query = "/", int timeout_ms = 5000,
        bool allow_http_get = false, int max_response_bytes = 4096,
        CancellationToken cancellationToken = default) =>
        NativeNetworkProbe.RunAsync(kind, host, port, path_and_query, timeout_ms,
            allow_http_get, max_response_bytes, cancellationToken);
}
