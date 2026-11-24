using System.Net;
using System.Net.Sockets;
using System.Text;

internal static class NetworkProbeAcceptance
{
    internal static void Run()
    {
        static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL " + name);
            Console.WriteLine("PASS " + name);
        }

        static bool Valid(NetworkProbeKind kind, string? host, int? port = null,
            string path = "/", int timeout = 5000, bool allowGet = false,
            int max = 4096) => NativeNetworkProbe.TryValidate(kind, host, port,
                path, timeout, allowGet, max, out _, out _);

        Check(Valid(NetworkProbeKind.DNS, "example.org") &&
            Valid(NetworkProbeKind.DNS, "localhost") &&
            Valid(NetworkProbeKind.DNS, "127.0.0.1") &&
            Valid(NetworkProbeKind.DNS, "::1"), "network_probe.host_literals");
        Check(Valid(NetworkProbeKind.TCP_CONNECT, "127.0.0.1", 443) &&
            Valid(NetworkProbeKind.TLS_HANDSHAKE, "example.org", 443),
            "network_probe.tcp_tls_explicit_ports");
        Check(Valid(NetworkProbeKind.HTTPS_HEAD, "example.org", path: "/health?mode=brief") &&
            Valid(NetworkProbeKind.HTTP_HEAD, "example.org"),
            "network_probe.head_default_ports");
        Check(Valid(NetworkProbeKind.HTTPS_GET, "example.org", allowGet: true, max: 128) &&
            Valid(NetworkProbeKind.HTTP_GET, "example.org", allowGet: true, max: 0) &&
            !Valid(NetworkProbeKind.HTTPS_GET, "example.org"),
            "network_probe.get_explicit_opt_in");
        Check(!Valid(NetworkProbeKind.DNS, "example.org", port: 53) &&
            !Valid(NetworkProbeKind.TCP_CONNECT, "example.org") &&
            !Valid(NetworkProbeKind.TLS_HANDSHAKE, "example.org", 0) &&
            !Valid(NetworkProbeKind.TCP_CONNECT, "example.org", 65536),
            "network_probe.discriminated_port_contract");
        Check(!Valid(NetworkProbeKind.DNS, "example.org", path: "/path") &&
            !Valid(NetworkProbeKind.TCP_CONNECT, "example.org", 443, max: 32) &&
            !Valid(NetworkProbeKind.HTTP_HEAD, "example.org", allowGet: true),
            "network_probe.unused_parameter_rejection");
        Check(!Valid(NetworkProbeKind.HTTP_HEAD, "example.org", path: "//redirect.example/") &&
            !Valid(NetworkProbeKind.HTTP_HEAD, "example.org", path: "/foo#fragment") &&
            !Valid(NetworkProbeKind.HTTP_HEAD, "example.org", path: "/\\evil") &&
            !Valid(NetworkProbeKind.HTTP_HEAD, "example.org", path: "/ok\r\nHost:evil"),
            "network_probe.uri_scope_validation");
        Check(!Valid(NetworkProbeKind.HTTP_HEAD, "https://example.org") &&
            !Valid(NetworkProbeKind.DNS, "a@b.example") &&
            !Valid(NetworkProbeKind.DNS, " example.org ") &&
            !Valid(NetworkProbeKind.DNS, "bad..example.org") &&
            !Valid(NetworkProbeKind.DNS, "::1%1"),
            "network_probe.host_scope_validation");
        Check(!Valid(NetworkProbeKind.DNS, "example.org", timeout: 249) &&
            !Valid(NetworkProbeKind.DNS, "example.org", timeout: 30001) &&
            !Valid(NetworkProbeKind.HTTPS_GET, "example.org", allowGet: true, max: 4097),
            "network_probe.resource_budgets");
        Check(NativeNetworkProbe.TryValidate(NetworkProbeKind.HTTPS_HEAD,
                "Example.ORG", null, "/", 5000, false, 4096,
                out string host, out int port) && host == "example.org" && port == 443 &&
            NativeNetworkProbe.TryValidate(NetworkProbeKind.HTTP_HEAD,
                "::1", null, "/", 5000, false, 4096,
                out _, out int plainPort) && plainPort == 80,
            "network_probe.normalized_destination");
    }

    internal static async Task RunLoopbackAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL " + name);
            Console.WriteLine("PASS " + name);
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(2);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        async Task ServeOnceAsync(string method, string statusLine, string body = "")
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = accepted.GetStream();
            var requestBytes = new byte[1024];
            var count = await stream.ReadAsync(requestBytes, deadline.Token);
            var request = Encoding.ASCII.GetString(requestBytes, 0, count);
            Check(request.StartsWith(method + " ", StringComparison.Ordinal),
                "network_probe.loopback_request_method_" + method.ToLowerInvariant());
            var response = Encoding.ASCII.GetBytes(statusLine + "\r\nConnection: close\r\n" +
                "Content-Length: " + Encoding.ASCII.GetByteCount(body) + "\r\n\r\n" + body);
            await stream.WriteAsync(response, deadline.Token);
        }

        var headServer = ServeOnceAsync("HEAD", "HTTP/1.1 204 No Content");
        var head = await NativeNetworkProbe.RunAsync(NetworkProbeKind.HTTP_HEAD,
            "127.0.0.1", port, "/health", 5000, false, 4096, CancellationToken.None);
        await headServer;
        Check(head.Status == NetworkProbeCode.COMPLETED && head.OutboundConnectionAttempted &&
            head.HttpStatusCode == 204 && head.ConnectedAddress == "127.0.0.1" &&
            head.SampledBodyBytes == 0,
            "network_probe.loopback_http_head_verified");

        var getServer = ServeOnceAsync("GET", "HTTP/1.1 200 OK", "private server body");
        var get = await NativeNetworkProbe.RunAsync(NetworkProbeKind.HTTP_GET,
            "127.0.0.1", port, "/health", 5000, true, 4, CancellationToken.None);
        await getServer;
        Check(get.Status == NetworkProbeCode.COMPLETED && get.HttpStatusCode == 200 &&
            get.SampledBodyBytes == 4 && get.BodyTruncated &&
            !get.ToString().Contains("private server body", StringComparison.Ordinal),
            "network_probe.loopback_http_get_bounded_no_body_leak");

        var tlsInvalid = await NativeNetworkProbe.RunAsync(NetworkProbeKind.TLS_HANDSHAKE,
            "127.0.0.1", port, "/", 500, false, 4096, CancellationToken.None);
        Check(tlsInvalid.Status == NetworkProbeCode.TIMEOUT ||
            tlsInvalid.Status == NetworkProbeCode.CONNECTION_FAILED ||
            tlsInvalid.Status == NetworkProbeCode.TLS_VALIDATION_FAILED,
            "network_probe.invalid_tls_does_not_claim_success");
    }
}
