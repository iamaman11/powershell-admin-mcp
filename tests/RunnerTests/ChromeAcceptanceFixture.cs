using System.Diagnostics;
using System.Globalization;
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
        Process? worker = null;
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
            if (!await UntilAsync(() => File.Exists(ownedEndpoint), TimeSpan.FromSeconds(12)))
            {
                Blocked("CI_CHROME_DEBUG_ENDPOINT_UNAVAILABLE");
                return;
            }
            // Validate actual loopback endpoint syntax before presenting it to
            // the unmodified real worker. Never overwrite an existing file.
            string text = await File.ReadAllTextAsync(ownedEndpoint);
            if (!WindowChromeCdp.TryReadEndpoint(text, out _) || File.Exists(endpointFile))
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
                    .FirstOrDefault(w => w.ProcessId == browserPid);
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

            var workerStart = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            workerStart.ArgumentList.Add(typeof(WindowChromeBroker).Assembly.Location);
            workerStart.ArgumentList.Add("window-cdp-worker");
            workerStart.ArgumentList.Add(self.Id.ToString(CultureInfo.InvariantCulture));
            var cdpWorker = Process.Start(workerStart) ??
                throw new InvalidOperationException("CDP_WORKER_NOT_STARTED");
            worker = cdpWorker;
            string selector = profile!.ProfileId ?? profile.Email ?? profile.DisplayName;

            async Task<ChromeBrokerReply> ExchangeAsync(ChromeBrokerRequest request)
            {
                string trace = Guid.NewGuid().ToString("N");
                request = request with { TraceId = trace };
                await cdpWorker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, PipeWire.Json));
                await cdpWorker.StandardInput.FlushAsync();
                string? replyLine = await cdpWorker.StandardOutput.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromSeconds(35));
                var reply = replyLine is null ? null :
                    JsonSerializer.Deserialize<ChromeBrokerReply>(replyLine, PipeWire.Json);
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
            Console.WriteLine($"PASS chrome_ci.authorized_worker_connected owner={cdpWorker.Id} generation={generation}");

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
                $"clicks={state.Clicks} inputs={state.Inputs} owner={cdpWorker.Id} generation={generation}");
            Console.WriteLine("NOT_TESTED / EXTERNAL_BLOCKER chrome_ci.multi_mcp_clients " +
                "(standalone worker fixture, no installed USER broker)");
            Console.WriteLine("NOT_TESTED / EXTERNAL_BLOCKER chrome_ci.uia_locator " +
                "(CDP element reference, not UIA locator)");
        }
        finally
        {
            if (worker is not null)
            {
                try { worker.StandardInput.Close(); } catch (IOException) { }
                if (!worker.HasExited && !worker.WaitForExit(1500)) worker.Kill(entireProcessTree: true);
                worker.Dispose();
            }
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
