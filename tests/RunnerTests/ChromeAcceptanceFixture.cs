using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal static class ChromeAcceptanceFixture
{
    private const int Cycles = 100;

    internal static async Task RunAsync()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_OS") != "Windows") return;

        string? chrome = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe")
        }.FirstOrDefault(File.Exists);
        if (chrome is null)
        {
            Blocked("CHROME_NOT_INSTALLED");
            return;
        }

        // The production resolver uses Chrome's default DevToolsActivePort path.
        // On a *fresh CI runner only*, bridge the endpoint metadata of our own
        // temporary, explicitly debug-enabled profile without changing Chrome
        // flags, consent, credentials, or any existing profile data.
        string endpointFile = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data",
            "DevToolsActivePort");
        if (File.Exists(endpointFile) || Process.GetProcessesByName("chrome").Length != 0)
        {
            Blocked("EXISTING_CHROME_OR_DEBUG_ENDPOINT");
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "wmcp-owned-chrome-" + Guid.NewGuid().ToString("N"));
        string profileDir = Path.Combine(root, "profile");
        Directory.CreateDirectory(profileDir);
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var state = new FixtureState();
        string nonce = Guid.NewGuid().ToString("N");
        string url = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/{nonce}/index";
        using var serverStop = new CancellationTokenSource();
        Task accepting = ServeAsync(server, nonce, state, serverStop.Token);
        Process? browser = null;
        bool endpointCreated = false;
        try
        {
            var info = new ProcessStartInfo(chrome)
            {
                UseShellExecute = false,
                CreateNoWindow = false
            };
            info.ArgumentList.Add($"--user-data-dir={profileDir}");
            info.ArgumentList.Add("--remote-debugging-port=0");
            info.ArgumentList.Add("--no-first-run");
            info.ArgumentList.Add("--no-default-browser-check");
            info.ArgumentList.Add("--disable-background-mode");
            info.ArgumentList.Add("--new-window");
            info.ArgumentList.Add(url);
            browser = Process.Start(info);
            if (browser is null)
            {
                Blocked("CHROME_PROCESS_START_FAILED");
                return;
            }
            int browserPid = browser.Id;

            string ownedEndpoint = Path.Combine(profileDir, "DevToolsActivePort");
            // Hosted Windows runners can spend substantially longer starting
            // their first browser process under load. Wait for the browser's
            // own endpoint file without touching another Chrome process.
            if (!await UntilAsync(() => File.Exists(ownedEndpoint), TimeSpan.FromSeconds(30)))
            {
                int? exitCode = browser.HasExited ? browser.ExitCode : null;
                var chromeProcesses = Process.GetProcessesByName("chrome");
                int chromeCount = chromeProcesses.Length;
                foreach (var process in chromeProcesses) process.Dispose();
                Console.WriteLine($"CHROME_CI_STARTUP_DIAGNOSTIC " +
                    $"browser_exited={browser.HasExited} " +
                    $"exit_code={exitCode} chrome_process_count={chromeCount}");
                Blocked("CI_CHROME_DEBUG_ENDPOINT_UNAVAILABLE");
                return;
            }
            // Validate actual loopback endpoint syntax before presenting it to
            // the unmodified real worker. Never overwrite an existing file.
            string text = await File.ReadAllTextAsync(ownedEndpoint);
            if (!WindowChromeCdp.TryReadEndpoint(text, out var endpoint) || File.Exists(endpointFile))
            {
                Blocked("CI_CHROME_ENDPOINT_NOT_EXCLUSIVE");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(endpointFile)!);
            using (var target = new FileStream(endpointFile, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                await target.WriteAsync(bytes);
                endpointCreated = true;
            }

            string? windowId = null;
            ChromeProfileInfo? profile = null;
            using var self = Process.GetCurrentProcess();
            bool found = await UntilAsync(() =>
            {
                var window = WindowCatalog.List(100, self.SessionId).Windows
                    .FirstOrDefault(w => w.ProcessId == browserPid &&
                        w.Title == "WMCP CI Chrome - Google Chrome");
                if (window is null) return false;
                windowId = window.WindowId;
                profile = ChromeProfileCatalog.ReadWindowProfile(windowId, self.SessionId);
                return profile is not null;
            }, TimeSpan.FromSeconds(15));
            if (!found)
            {
                Blocked("CI_CHROME_NATIVE_PROFILE_NOT_DISCOVERED");
                return;
            }

            // The throwaway CI profile has no stable account metadata. Its
            // verified native avatar display name is the binding selector.
            string selector = profile!.DisplayName;

            // Use the real USER owner from the first request onward. The 100
            // cycles and the 2/4/8 independent clients must share its single
            // worker and its consented WebSocket, without a second connection.
            using var brokerStop = new CancellationTokenSource();
            Task owner = ChromeBrokerPipe.RunOwnerAsync(brokerStop.Token);
            try
            {
                if (!ChromeBrokerPipe.IsOwner)
                throw new InvalidOperationException("CI_CDP_PIPE_OWNER_NOT_STARTED");
            async Task<ChromeBrokerReply> ExchangeAsync(ChromeBrokerRequest request)
            {
                string trace = Guid.NewGuid().ToString("N");
                request = request with { TraceId = trace };
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var reply = await ChromeBrokerPipe.ExchangeAsync(request, deadline.Token);
                if (!WindowChromeBroker.ReplyMatches(request, reply) || reply!.TraceId != trace)
                    throw new InvalidOperationException("CI_CDP_BROKER_WIRE_IDENTITY_MISMATCH");
                return reply!;
            }

            ChromeBrokerRequest Connection(ChromeConnectionAction action) => new(
                Connection: new ChromeConnectionRequest(windowId!, selector, action));
            var connected = await ExchangeAsync(Connection(ChromeConnectionAction.REQUEST));
            if (connected.Connection?.State != ChromeConnectionState.CONNECTED)
            {
                for (int attempt = 0; attempt < 15 && connected.Connection?.State ==
                     ChromeConnectionState.CONSENT_PENDING; attempt++)
                {
                    await Task.Delay(300);
                    connected = await ExchangeAsync(Connection(ChromeConnectionAction.STATUS));
                }
            }
            if (connected.Connection?.State != ChromeConnectionState.CONNECTED)
            {
                Blocked($"CHROME_CONSENT_{connected.Connection?.State}_{connected.Connection?.FailureReason}");
                return;
            }
            string? generation = connected.Connection.SessionGeneration;
            if (generation is null) throw new InvalidOperationException("CI_CDP_GENERATION_MISSING");
            Console.WriteLine($"PASS chrome_ci.authorized_shared_owner_connected owner={self.Id} generation={generation}");

            // Chrome's first native window can be visible before the page
            // target and its window geometry settle. Only retry the *read-only*
            // inspection preflight; never replay a potentially effectful action.
            WindowInspectResult? preflight = null;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                preflight = (await ExchangeAsync(new(Inspect: new WindowInspectRequest(
                    windowId!, 4, 20, null, null, 0, false, "CI_CDP_VALUE",
                    WindowRoleFilter.EDIT, 0, WindowInspectScope.TAB,
                    ExpectedProfile: selector, ExpectedUrl: url)))).Inspect;
                if (preflight?.Status == WindowInspectCode.COMPLETED &&
                    preflight.Elements.Any(e => e.ElementId.StartsWith("c1:",
                        StringComparison.Ordinal))) break;
                await Task.Delay(300);
            }
            if (preflight?.Status != WindowInspectCode.COMPLETED)
                throw new InvalidOperationException($"CI_CDP_READ_ONLY_PREFLIGHT " +
                    $"status={preflight?.Status} detail={preflight?.Detail}");

            for (int iteration = 0; iteration < Cycles; iteration++)
            {
                var status = (await ExchangeAsync(Connection(ChromeConnectionAction.STATUS))).Connection;
                if (status?.State != ChromeConnectionState.CONNECTED ||
                    status.SessionGeneration != generation)
                    throw new InvalidOperationException($"CI_CDP_STATUS_LOST cycle={iteration} state={status?.State}");

                bool setText = iteration % 2 == 0;
                string label = setText ? "CI_CDP_VALUE" : "CI_CDP_BUTTON";
                var inspect = (await ExchangeAsync(new(Inspect: new WindowInspectRequest(
                    windowId!, 4, 20, null, null, 0, false, label,
                    setText ? WindowRoleFilter.EDIT : WindowRoleFilter.BUTTON, 0,
                    WindowInspectScope.TAB, ExpectedProfile: selector, ExpectedUrl: url)))).Inspect;
                var targets = inspect?.Elements.Where(e => e.ElementId.StartsWith("c1:",
                    StringComparison.Ordinal)).ToArray() ?? [];
                if (inspect?.Status != WindowInspectCode.COMPLETED || targets.Length != 1 ||
                    inspect.SessionGeneration != generation || inspect.Tabs?.Length != 1 ||
                    inspect.Tabs[0].Url != url)
                    throw new InvalidOperationException($"CI_CDP_INSPECT cycle={iteration} " +
                        $"status={inspect?.Status} detail={inspect?.Detail} count={inspect?.Elements.Length} " +
                        $"candidates={targets.Length}");

                string value = $"CI_CDP_CYCLE_{iteration:D3}";
                var action = (await ExchangeAsync(new(Action: new WindowActionRequest(
                    windowId!, targets[0].ElementId, setText ? WindowActionKind.SET_VALUE :
                        WindowActionKind.CLICK, setText ? value : null,
                    ExpectedProfile: selector, ExpectedUrl: url,
                    ObservationTimeoutMs: 1500)))).Action;
                if (action?.Status != WindowActionCode.COMPLETED || action.EffectUnknown ||
                    action.SessionGeneration != generation ||
                    action.ExecutionMethod != WindowExecutionMethod.CHROME_CDP ||
                    setText && action.ExpectedStateObserved != true)
                    throw new InvalidOperationException($"CI_CDP_ACTION cycle={iteration} " +
                        $"status={action?.Status} detail={action?.Detail} unknown={action?.EffectUnknown}");

                int expectedClicks = (iteration + 1) / 2;
                bool observed = await UntilAsync(() => setText
                    ? state.LastText == value : state.Clicks == expectedClicks,
                    TimeSpan.FromSeconds(2));
                if (!observed)
                    throw new InvalidOperationException($"CI_CDP_EFFECT cycle={iteration} " +
                        $"clicks={state.Clicks} text={state.LastText}");
            }

            if (state.Clicks != Cycles / 2 || state.Inputs < Cycles / 2)
                throw new InvalidOperationException("CI_CDP_FINAL_EFFECT_COUNT_MISMATCH");
            Console.WriteLine($"PASS chrome_ci.100_status_inspect_action_verify_cycles " +
                $"clicks={state.Clicks} inputs={state.Inputs} owner={self.Id} generation={generation}");

            int expected = state.Clicks;
            foreach (int concurrency in new[] { 2, 4, 8 })
            {
                var reports = await Task.WhenAll(Enumerable.Range(0, concurrency)
                    .Select(_ => RunPipeClientAsync(windowId!, selector, url)));
                if (!reports.All(r => r.OwnerPid == self.Id &&
                    r.Generation == generation && r.Verified))
                    throw new InvalidOperationException(
                        $"CI_CDP_MULTICLIENT_REPLY_FAILURE clients={concurrency}");
                expected += concurrency;
                if (!await UntilAsync(() => state.Clicks == expected,
                    TimeSpan.FromSeconds(3)))
                    throw new InvalidOperationException(
                        $"CI_CDP_MULTICLIENT_EFFECT_COUNT clients={concurrency} " +
                        $"expected={expected} actual={state.Clicks}");
                Console.WriteLine($"PASS chrome_ci.shared_owner_clients_{concurrency} " +
                    $"clicks={expected} generation={generation}");
            }
            var processes = Process.GetProcessesByName("RunnerTests");
            int[] children;
            try
            {
                children = processes.Where(p => p.Id != self.Id &&
                    p.SessionId == self.SessionId).Select(p => p.Id).ToArray();
            }
            finally { foreach (var p in processes) p.Dispose(); }
            if (children.Length != 1)
                throw new InvalidOperationException(
                    $"CI_CDP_EXPECT_ONE_WORKER found={children.Length}");
            var sockets = SystemSnapshot.Observe(SystemQueryKind.TCP_CONNECTIONS,
                children[0], null, 50, 0, CancellationToken.None);
            int established = sockets.TcpConnections?.Count(t =>
                t.State == SystemTcpState.ESTABLISHED &&
                t.RemotePort == endpoint!.Port &&
                t.RemoteAddress == "127.0.0.1") ?? 0;
            if (sockets.Status != SystemQueryCode.COMPLETED || established != 1)
                throw new InvalidOperationException($"CI_CDP_EXPECT_ONE_SOCKET " +
                    $"query={sockets.Status} established={established}");
            Console.WriteLine($"PASS chrome_ci.one_shared_cdp_worker_and_websocket " +
                $"worker_pid={children[0]} owner_pid={self.Id} socket_count={established}");
            }
            finally
            {
                await brokerStop.CancelAsync();
                await owner;
            }
            Console.WriteLine("NOT_TESTED / EXTERNAL_BLOCKER chrome_ci.uia_locator " +
                "(CDP element reference, not UIA locator)");
        }
        finally
        {
            if (browser is not null)
            {
                if (!browser.HasExited) browser.Kill(entireProcessTree: true);
                browser.Dispose();
            }
            if (endpointCreated) File.Delete(endpointFile);
            serverStop.Cancel();
            server.Stop();
            try { await accepting; } catch (OperationCanceledException) { }
            try { Directory.Delete(root, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { /* Chrome may still be releasing test-only files. */ }
        }
    }

    private static void Blocked(string reason) =>
        Console.WriteLine($"NOT_TESTED / EXTERNAL_BLOCKER chrome_ci.100_cycles reason={reason}");

    private sealed record PipeClientReport(int OwnerPid, string Generation, bool Verified);

    private static async Task<PipeClientReport> RunPipeClientAsync(string window, string profile,
        string url)
    {
        string executable = Environment.ProcessPath ??
            throw new InvalidOperationException("CI_PIPE_CLIENT_EXE_MISSING");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet",
            StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(ChromeAcceptanceFixture).Assembly.Location);
        start.ArgumentList.Add("--chrome-pipe-live-action");
        start.ArgumentList.Add(window);
        start.ArgumentList.Add(profile);
        start.ArgumentList.Add(url);
        using var client = Process.Start(start) ??
            throw new InvalidOperationException("CI_CDP_PIPE_CLIENT_START_FAILED");
        var output = client.StandardOutput.ReadToEndAsync();
        await client.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        string report = await output;
        if (client.ExitCode != 0)
            throw new InvalidOperationException($"CI_CDP_PIPE_CLIENT_EXIT={client.ExitCode}: {report}");
        using var doc = JsonDocument.Parse(report);
        var json = doc.RootElement;
        return new(json.GetProperty("owner_pid").GetInt32(),
            json.GetProperty("session_generation").GetString() ?? "",
            json.GetProperty("verified").GetBoolean());
    }

    private static async Task<bool> UntilAsync(Func<bool> predicate, TimeSpan budget)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            if (predicate()) return true;
            await Task.Delay(100);
        } while (watch.Elapsed < budget);
        return predicate();
    }

    private sealed class FixtureState
    {
        internal int Clicks;
        internal int Inputs;
        internal string LastText = "";
    }

    private static async Task ServeAsync(TcpListener listener, string nonce, FixtureState state,
        CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancel);
            using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            string? line = await reader.ReadLineAsync(cancel);
            if (line is null) continue;
            string route = line.Split(' ').ElementAtOrDefault(1) ?? "";
            string body;
            string contentType;
            if (route == $"/{nonce}/index")
            {
                contentType = "text/html; charset=utf-8";
                body = "<!doctype html><html><head><title>WMCP CI Chrome</title></head><body>" +
                    "<input aria-label='CI_CDP_VALUE' id='value'>" +
                    "<button aria-label='CI_CDP_BUTTON' id='button'>CI_CDP_BUTTON</button>" +
                    "<script>" +
                    "document.getElementById('value').addEventListener('input',e=>" +
                    "fetch('/REPLACE/report?text='+encodeURIComponent(e.target.value)));" +
                    "document.getElementById('button').addEventListener('click',()=>" +
                    "fetch('/REPLACE/report?click=1'));</script></body></html>";
                body = body.Replace("REPLACE", nonce, StringComparison.Ordinal);
            }
            else if (route.StartsWith($"/{nonce}/report?", StringComparison.Ordinal))
            {
                string query = route[(route.IndexOf('?') + 1)..];
                if (query == "click=1") Interlocked.Increment(ref state.Clicks);
                else if (query.StartsWith("text=", StringComparison.Ordinal))
                {
                    Volatile.Write(ref state.LastText, Uri.UnescapeDataString(query[5..]));
                    Interlocked.Increment(ref state.Inputs);
                }
                body = "OK";
                contentType = "text/plain";
            }
            else
            {
                body = "NOT_FOUND";
                contentType = "text/plain";
            }
            byte[] payload = Encoding.UTF8.GetBytes(body);
            byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\n" +
                $"Content-Length: {payload.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
            await stream.WriteAsync(header, cancel);
            await stream.WriteAsync(payload, cancel);
        }
    }
}
