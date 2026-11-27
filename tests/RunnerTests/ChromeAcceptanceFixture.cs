using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

internal static class ChromeAcceptanceFixture
{
    private const int Cycles = 100;

    internal static async Task RunAsync()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_OS") != "Windows") return;

        await VerifyReceivePumpAsync();
        await VerifyFrameTreeFallbackAsync();

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

        // The managed resolver must use its exact dedicated user-data-dir.
        // CI must never synthesize an endpoint in Chrome's normal user profile.
        string endpointFile = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data",
            "DevToolsActivePort");
        if (File.Exists(endpointFile) || Process.GetProcessesByName("chrome").Length != 0)
        {
            Blocked("EXISTING_CHROME_OR_DEBUG_ENDPOINT");
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "wmcp-owned-chrome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string uploadFile = Path.Combine(root, "ci-file.txt");
        File.WriteAllText(uploadFile, "WMCP_CI_NATIVE_FILE_SELECTION", Encoding.UTF8);
        string managedProfileId = "ci_" + Guid.NewGuid().ToString("N");
        string profileDir = ChromeManagedProfile.DataDirectory(managedProfileId)!;
        Directory.CreateDirectory(profileDir);
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var state = new FixtureState();
        string nonce = Guid.NewGuid().ToString("N");
        string url = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/{nonce}/index";
        using var serverStop = new CancellationTokenSource();
        Task accepting = ServeAsync(server, nonce, state, serverStop.Token);
        Process? browser = null;
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
            // Chrome creates this file before its final write is necessarily
            // visible or its writer handle is released. The existence check
            // above proves only that startup began, not that it finished.
            // Retry *only* this read of our own disposable CI profile; a
            // partial/locked endpoint must never be passed to the broker.
            string? text = null;
            Uri? endpoint = null;
            bool endpointReady = await UntilAsync(() =>
            {
                try
                {
                    using var stream = new FileStream(ownedEndpoint, FileMode.Open,
                        FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length is < 1 or > 256) return false;
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string candidate = reader.ReadToEnd();
                    if (!WindowChromeCdp.TryReadEndpoint(candidate, out var parsed))
                        return false;
                    text = candidate;
                    endpoint = parsed;
                    return true;
                }
                catch (IOException) { return false; } // Chrome still owns the file.
            }, TimeSpan.FromSeconds(10));
            if (!endpointReady)
                throw new InvalidOperationException("CI_CHROME_OWNED_ENDPOINT_READ_TIMEOUT");
            if (File.Exists(endpointFile))
                throw new InvalidOperationException("CI_DEFAULT_CHROME_PROFILE_WAS_MODIFIED");

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
            // This test owns the entire new profile. The native avatar label
            // is safe to report here when a profile check fails; production
            // diagnostics never reveal unrelated Chrome profile labels.
            string ProfileEvidence()
            {
                var observed = ChromeProfileCatalog.ReadWindowProfile(windowId!, self.SessionId,
                    out var probe);
                return $"native_probe={probe} initial_label={selector} " +
                    $"current_label={observed?.DisplayName} " +
                    $"initial_id={profile.ProfileId} current_id={observed?.ProfileId}";
            }
            void RefreshOwnedProfile()
            {
                // In an unsigned, new test profile Chrome may change its
                // native avatar tooltip from a sign-in hint to "You". The
                // test owns this exact HWND/PID and user-data-dir, so obtain
                // the current selector from the real UIA avatar before each
                // *new* operation. Production never substitutes a selector.
                var observed = ChromeProfileCatalog.ReadWindowProfileStable(windowId!,
                    self.SessionId, out var probe);
                if (observed is null || observed.WindowId != windowId ||
                    observed.ProcessId != browserPid)
                    throw new InvalidOperationException(
                        $"CI_CDP_OWNED_PROFILE_LOST probe={probe} {ProfileEvidence()}");
                if (!string.Equals(selector, observed.DisplayName, StringComparison.Ordinal))
                {
                    Console.WriteLine("CHROME_CI_OWNED_AVATAR_LABEL_UPDATED " +
                        "(same native window and process)");
                    selector = observed.DisplayName;
                }
            }

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
                Connection: new ChromeConnectionRequest(windowId!, selector, action,
                    Mode: ChromeConnectionMode.MANAGED_BROWSER,
                    ManagedProfileId: managedProfileId));
            var connected = await ExchangeAsync(Connection(ChromeConnectionAction.REQUEST));
            if (connected.Connection?.State != ChromeConnectionState.CONNECTED)
            {
                for (int attempt = 0; attempt < 15 && connected.Connection?.State is
                     ChromeConnectionState.CONSENT_PENDING or ChromeConnectionState.CONNECTING; attempt++)
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
                RefreshOwnedProfile();
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
            var verifiedTab = preflight.Tabs?.SingleOrDefault();
            if (verifiedTab is null || verifiedTab.BrowserWindowId is not > 0 ||
                !WindowChromeCdp.ValidTargetId(verifiedTab.TabId))
                throw new InvalidOperationException("CI_CDP_NATIVE_WINDOW_BINDING_MISSING");

            int rejectedBeforeDispatch = 0;
            for (int iteration = 0; iteration < Cycles; iteration++)
            {
                for (int planningAttempt = 0; ; planningAttempt++)
                {
                RefreshOwnedProfile();
                var status = (await ExchangeAsync(Connection(ChromeConnectionAction.STATUS))).Connection;
                if (status?.State != ChromeConnectionState.CONNECTED ||
                    status.SessionGeneration != generation)
                {
                    // A native toolbar label can change between our last
                    // read and STATUS. This rejected operation had no effect.
                    if (planningAttempt < 3 && status?.Status == ChromeConnectionCode.PROFILE_MISMATCH &&
                        status.Detail?.StartsWith("PROFILE_", StringComparison.Ordinal) == true)
                    {
                        rejectedBeforeDispatch++;
                        continue;
                    }
                    throw new InvalidOperationException($"CI_CDP_STATUS_LOST cycle={iteration} " +
                        $"code={status?.Status} state={status?.State} detail={status?.Detail} " +
                        $"failure={status?.FailureReason} {ProfileEvidence()}");
                }

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
                {
                    if (planningAttempt < 3 && inspect?.Status == WindowInspectCode.CDP_CONTEXT_MISMATCH &&
                        inspect.Detail?.StartsWith("PROFILE_", StringComparison.Ordinal) == true)
                    {
                        rejectedBeforeDispatch++;
                        continue;
                    }
                    throw new InvalidOperationException($"CI_CDP_INSPECT cycle={iteration} " +
                        $"status={inspect?.Status} detail={inspect?.Detail} count={inspect?.Elements.Length} " +
                        $"candidates={targets.Length} {ProfileEvidence()}");
                }

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
                {
                    // A *proven* NOT_DISPATCHED profile rejection is the only
                    // effectful-call response that permits new planning here.
                    // Re-resolve the element; never replay an unknown effect.
                    if (planningAttempt < 3 && action?.Status == WindowActionCode.CDP_CONTEXT_MISMATCH &&
                        action.Detail?.StartsWith("PROFILE_", StringComparison.Ordinal) == true &&
                        action.EffectState == WindowEffectState.NO_EFFECT &&
                        action.ProviderOutcome == WindowProviderOutcome.NOT_DISPATCHED &&
                        action.FailureStage == WindowFailureStage.CDP_TARGET)
                    {
                        rejectedBeforeDispatch++;
                        continue;
                    }
                    throw new InvalidOperationException($"CI_CDP_ACTION cycle={iteration} " +
                        $"status={action?.Status} detail={action?.Detail} unknown={action?.EffectUnknown} " +
                        ProfileEvidence());
                }

                int expectedClicks = (iteration + 1) / 2;
                bool observed = await UntilAsync(() => setText
                    ? state.LastText == value : state.Clicks == expectedClicks,
                    TimeSpan.FromSeconds(2));
                if (!observed)
                    throw new InvalidOperationException($"CI_CDP_EFFECT cycle={iteration} " +
                        $"clicks={state.Clicks} text={state.LastText}");
                break;
                }
            }

            if (state.Clicks != Cycles / 2 || state.Inputs < Cycles / 2)
                throw new InvalidOperationException("CI_CDP_FINAL_EFFECT_COUNT_MISMATCH");
            Console.WriteLine($"PASS chrome_ci.100_status_inspect_action_verify_cycles " +
                $"clicks={state.Clicks} inputs={state.Inputs} owner={self.Id} generation={generation} " +
                $"no_effect_profile_rejections={rejectedBeforeDispatch}");

            // Two real DOM interactions in this isolated CI-owned browser.
            // Neither uses OS input or a second CDP owner.
            async Task<string> FindOwnedElement(string name)
            {
                RefreshOwnedProfile();
                var reply = await ExchangeAsync(new(Inspect: new WindowInspectRequest(
                    windowId!, 5, 30, null, null, 0, false, name,
                    null, 0, WindowInspectScope.TAB,
                    TabId: verifiedTab.TabId, ExpectedProfile: selector, ExpectedUrl: url,
                    BrowserWindowId: verifiedTab.BrowserWindowId)));
                var matches = reply.Inspect?.Elements.Where(e =>
                    e.Name == name && e.ElementId.StartsWith("c1:", StringComparison.Ordinal)).ToArray();
                if (reply.Inspect?.Status != WindowInspectCode.COMPLETED || matches?.Length != 1)
                    throw new InvalidOperationException($"CI_CDP_ELEMENT_{name}_{reply.Inspect?.Status}");
                return matches[0].ElementId;
            }
            // Exercise a complete background React-like confirmation flow:
            // the opening click is only confirmed once the dialog appears;
            // the confirm click is only confirmed once it disappears AND the
            // independent loopback server records exactly one completion.
            string modalOpen = await FindOwnedElement("CI_CDP_MODAL_OPEN");
            var openDialog = (await ExchangeAsync(new(Action: new WindowActionRequest(
                windowId!, modalOpen, WindowActionKind.CLICK, null,
                ExpectedName: "CI_CDP_MODAL_CONFIRM",
                ExpectedChange: WindowExpectedChange.APPEARS,
                ExpectedProfile: selector, ExpectedUrl: url,
                InputMode: WindowInputMode.CDP_ONLY,
                BrowserWindowId: verifiedTab.BrowserWindowId,
                ObservationTimeoutMs: 3000)))).Action;
            if (openDialog?.Status != WindowActionCode.COMPLETED ||
                openDialog.EffectEvidence != WindowEffectEvidence.POSTCONDITION_VERIFIED ||
                openDialog.FocusBehavior != WindowFocusBehavior.CDP_NO_NATIVE_ACTIVATION ||
                state.Confirmations != 0)
                throw new InvalidOperationException($"CI_CDP_MODAL_OPEN_{openDialog?.Status}_" +
                    $"{openDialog?.EffectEvidence}");
            string modalConfirm = await FindOwnedElement("CI_CDP_MODAL_CONFIRM");
            var confirmed = (await ExchangeAsync(new(Action: new WindowActionRequest(
                windowId!, modalConfirm, WindowActionKind.CLICK, null,
                ExpectedName: "CI_CDP_MODAL_CONFIRM",
                ExpectedChange: WindowExpectedChange.DISAPPEARS,
                ExpectedProfile: selector, ExpectedUrl: url,
                InputMode: WindowInputMode.CDP_ONLY,
                BrowserWindowId: verifiedTab.BrowserWindowId,
                ObservationTimeoutMs: 3000)))).Action;
            if (confirmed?.Status != WindowActionCode.COMPLETED ||
                confirmed.EffectEvidence != WindowEffectEvidence.POSTCONDITION_VERIFIED ||
                confirmed.FocusBehavior != WindowFocusBehavior.CDP_NO_NATIVE_ACTIVATION ||
                !await UntilAsync(() => state.Confirmations == 1, TimeSpan.FromSeconds(3)))
                throw new InvalidOperationException($"CI_CDP_MODAL_CONFIRM_{confirmed?.Status}_" +
                    $"{confirmed?.EffectEvidence}_confirmations={state.Confirmations}");
            Console.WriteLine("PASS chrome_ci.background_dialog_confirm_and_server_effect_verified");

            string fileInput = await FindOwnedElement("CI_CDP_FILE_INPUT");
            var fileReply = await ExchangeAsync(new(Action: new WindowActionRequest(
                windowId!, fileInput, WindowActionKind.SET_FILES, null,
                ExpectedProfile: selector, ExpectedUrl: url,
                InputMode: WindowInputMode.CDP_ONLY, FilePaths: [uploadFile])));
            var fileResult = fileReply.Action;
            if (fileResult?.Status != WindowActionCode.COMPLETED ||
                fileResult.EffectState != WindowEffectState.EFFECT_VERIFIED ||
                fileResult.SelectedFileCount != 1 ||
                fileResult.FocusBehavior != WindowFocusBehavior.CDP_NO_NATIVE_ACTIVATION)
                throw new InvalidOperationException($"CI_CDP_SET_FILES_{fileResult?.Status}_" +
                    $"{fileResult?.EffectEvidence}");
            Console.WriteLine("PASS chrome_ci.typed_file_input_selected_and_read_back");

            string dragSource = await FindOwnedElement("CI_CDP_DRAG_SOURCE");
            string dragTarget = await FindOwnedElement("CI_CDP_DROP_TARGET");
            var dragReply = await ExchangeAsync(new(Action: new WindowActionRequest(
                windowId!, dragSource, WindowActionKind.DRAG, null,
                ExpectedName: "CI_CDP_DROP_COMPLETE",
                ExpectedChange: WindowExpectedChange.APPEARS,
                ExpectedProfile: selector, ExpectedUrl: url,
                InputMode: WindowInputMode.CDP_ONLY, TargetElementId: dragTarget)));
            var dragResult = dragReply.Action;
            if (dragResult?.Status != WindowActionCode.COMPLETED ||
                dragResult.EffectState != WindowEffectState.EFFECT_VERIFIED ||
                dragResult.FocusBehavior != WindowFocusBehavior.CDP_NO_NATIVE_ACTIVATION ||
                !await UntilAsync(() => state.Drops == 1, TimeSpan.FromSeconds(4)))
                throw new InvalidOperationException($"CI_CDP_DRAG_{dragResult?.Status}_" +
                    $"{dragResult?.EffectEvidence}_drops={state.Drops}");
            Console.WriteLine("PASS chrome_ci.addressed_dom_drag_drop_verified");

            int expected = state.Clicks;
            foreach (int concurrency in new[] { 2, 4, 8 })
            {
                RefreshOwnedProfile();
                var reports = await Task.WhenAll(Enumerable.Range(0, concurrency)
                    .Select(_ => RunPipeClientAsync(windowId!, selector, url, managedProfileId)));
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

            // The new typed tab lifecycle shares the exact same owner and
            // consented socket; each effect is independently read back.
            RefreshOwnedProfile();
            async Task<ChromeTabResult> Tab(ChromeTabAction action, string? tab = null,
                string? navigateUrl = null)
            {
                var reply = await ExchangeAsync(new(TabManage: new ChromeTabRequest(
                    windowId!, selector, action, tab, navigateUrl)));
                return reply.TabManage ?? throw new InvalidOperationException("CI_TAB_REPLY_MISSING");
            }
            var tabList = await Tab(ChromeTabAction.LIST);
            if (tabList.Status != ChromeTabCode.COMPLETED ||
                tabList.Tabs?.All(t => t.Url != url) != false)
                throw new InvalidOperationException($"CI_TAB_LIST_{tabList.Status}");
            var browserListReply = await ExchangeAsync(new(TabManage: new ChromeTabRequest(
                windowId!, selector, ChromeTabAction.LIST, Scope: ChromeTabScope.BROWSER)));
            var browserList = browserListReply.TabManage;
            int? explicitWindow = browserList?.Tabs?.FirstOrDefault(t => t.Url == url)?.BrowserWindowId;
            if (browserList?.Status != ChromeTabCode.COMPLETED || explicitWindow is not > 0)
                throw new InvalidOperationException($"CI_BROWSER_SCOPED_LIST_{browserList?.Status}");
            var selectedWindowReply = await ExchangeAsync(new(TabManage: new ChromeTabRequest(
                windowId!, selector, ChromeTabAction.LIST, Scope: ChromeTabScope.BROWSER,
                BrowserWindowId: explicitWindow)));
            if (selectedWindowReply.TabManage is not { Status: ChromeTabCode.COMPLETED } selectedWindow ||
                selectedWindow.Tabs?.Any(t => t.BrowserWindowId != explicitWindow) == true ||
                selectedWindow.Tabs?.All(t => t.Url != url) != false)
                throw new InvalidOperationException("CI_BROWSER_SCOPED_WINDOW_FILTER_FAILED");
            var openedReply = await ExchangeAsync(new(TabManage: new ChromeTabRequest(
                windowId!, selector, ChromeTabAction.OPEN, Url: url,
                Scope: ChromeTabScope.BROWSER)));
            var openedTab = openedReply.TabManage ??
                throw new InvalidOperationException("CI_BROWSER_SCOPED_OPEN_REPLY_MISSING");
            if (openedTab.Status != ChromeTabCode.COMPLETED || openedTab.TabId is null ||
                openedTab.EffectState != WindowEffectState.EFFECT_VERIFIED ||
                openedTab.Tab?.BrowserWindowId is not > 0)
                throw new InvalidOperationException($"CI_TAB_OPEN_{openedTab.Status}_{openedTab.Detail}");
            string newTab = openedTab.TabId;
            int openedWindowId = openedTab.Tab!.BrowserWindowId!.Value;
            string secondUrl = url + "?history=1";
            foreach (var (action, destination) in new[]
            {
                (ChromeTabAction.NAVIGATE, secondUrl),
                (ChromeTabAction.BACK, url),
                (ChromeTabAction.FORWARD, secondUrl),
                (ChromeTabAction.RELOAD, (string?)null)
            })
            {
                var changedReply = await ExchangeAsync(new(TabManage: new ChromeTabRequest(
                    windowId!, selector, action, newTab,
                    action == ChromeTabAction.NAVIGATE ? destination : null,
                    Scope: ChromeTabScope.BROWSER, BrowserWindowId: openedWindowId)));
                var changed = changedReply.TabManage ??
                    throw new InvalidOperationException("CI_BROWSER_SCOPED_CHANGE_REPLY_MISSING");
                if (changed.Status != ChromeTabCode.COMPLETED ||
                    changed.EffectState != WindowEffectState.EFFECT_VERIFIED)
                    throw new InvalidOperationException($"CI_TAB_{action}_{changed.Status}_{changed.Detail}");
            }
            var closedReply = await ExchangeAsync(new(TabManage: new ChromeTabRequest(
                windowId!, selector, ChromeTabAction.CLOSE, newTab,
                Scope: ChromeTabScope.BROWSER,
                BrowserWindowId: openedWindowId)));
            var closedTab = closedReply.TabManage ??
                throw new InvalidOperationException("CI_BROWSER_SCOPED_CLOSE_REPLY_MISSING");
            if (closedTab.Status != ChromeTabCode.COMPLETED ||
                closedTab.EffectState != WindowEffectState.EFFECT_VERIFIED)
                throw new InvalidOperationException($"CI_TAB_CLOSE_{closedTab.Status}");
            Console.WriteLine("PASS chrome_ci.native_typed_tab_lifecycle " +
                "list_open_navigate_back_forward_reload_close " +
                "browser_scoped_exact_id_same_broker_same_socket");
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
            serverStop.Cancel();
            server.Stop();
            try { await accepting; } catch (OperationCanceledException) { }
            try { Directory.Delete(root, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { /* Chrome may still be releasing test-only files. */ }
            try { Directory.Delete(profileDir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { /* Chrome may still be releasing test-only files. */ }
        }
    }

    private static void Blocked(string reason) =>
        Console.WriteLine($"NOT_TESTED / EXTERNAL_BLOCKER chrome_ci.100_cycles reason={reason}");

    private static async Task VerifyReceivePumpAsync()
    {
        using var wire = new ScriptedWebSocket();
        using var session = new WindowChromeCdp.Session(wire);
        using var testDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var slowDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        session.SetDeadline(slowDeadline.Token);
        Task<JsonElement> slow = session.Call("Test.slow", new { });
        var sentSlow = await wire.NextCommandAsync(testDeadline.Token);
        int slowId = sentSlow.GetProperty("id").GetInt32();
        if (sentSlow.GetProperty("method").GetString() != "Test.slow")
            throw new InvalidOperationException("CI_CDP_SLOW_COMMAND_MISMATCH");
        try
        {
            _ = await slow.WaitAsync(testDeadline.Token);
            throw new InvalidOperationException("CI_CDP_TIMEOUT_NOT_REPORTED");
        }
        catch (OperationCanceledException e) when (e.Data["cdp_method"] is "Test.slow") { }
        if (wire.Aborted)
            throw new InvalidOperationException("CI_CDP_COMMAND_TIMEOUT_CLOSED_SOCKET");

        session.SetDeadline(testDeadline.Token);
        Task<JsonElement> fast = session.Call("Test.fast", new { });
        var sentFast = await wire.NextCommandAsync(testDeadline.Token);
        int fastId = sentFast.GetProperty("id").GetInt32();
        if (fastId == slowId) throw new InvalidOperationException("CI_CDP_ID_REUSED");
        wire.Reply(JsonSerializer.Serialize(new { id = slowId, result = new { name = "late" } }));
        wire.Reply("""{"method":"Page.frameNavigated","params":{}}""");
        wire.Reply(JsonSerializer.Serialize(new { id = fastId, result = new { name = "fast" } }));
        if ((await fast.WaitAsync(testDeadline.Token)).GetProperty("name").GetString() != "fast")
            throw new InvalidOperationException("CI_CDP_LATE_RESPONSE_CROSSED_ID");

        Task<JsonElement> first = session.Call("Test.first", new { });
        Task<JsonElement> second = session.Call("Test.second", new { });
        var commandOne = await wire.NextCommandAsync(testDeadline.Token);
        var commandTwo = await wire.NextCommandAsync(testDeadline.Token);
        int idOne = commandOne.GetProperty("id").GetInt32();
        int idTwo = commandTwo.GetProperty("id").GetInt32();
        wire.Reply(JsonSerializer.Serialize(new { id = idTwo, result = new { name = "two" } }));
        wire.Reply(JsonSerializer.Serialize(new { id = idOne, result = new { name = "one" } }));
        if ((await first.WaitAsync(testDeadline.Token)).GetProperty("name").GetString() != "one" ||
            (await second.WaitAsync(testDeadline.Token)).GetProperty("name").GetString() != "two")
            throw new InvalidOperationException("CI_CDP_OUT_OF_ORDER_RESPONSE_MISMATCH");

        Task<JsonElement> rejected = session.Call("Test.reject", new { });
        int rejectId = (await wire.NextCommandAsync(testDeadline.Token)).GetProperty("id").GetInt32();
        wire.Reply(JsonSerializer.Serialize(new
            { id = rejectId, error = new { code = -32601, message = "test" } }));
        try
        {
            _ = await rejected.WaitAsync(testDeadline.Token);
            throw new InvalidOperationException("CI_CDP_REJECTION_NOT_REPORTED");
        }
        catch (InvalidOperationException e) when (e.Message == "CDP_COMMAND_REJECTED") { }

        Task<JsonElement> detached = session.Call("Test.detached", new { }, "s-test");
        _ = await wire.NextCommandAsync(testDeadline.Token);
        wire.Reply("""{"method":"Inspector.detached","sessionId":"s-test","params":{}}""");
        try
        {
            _ = await detached.WaitAsync(testDeadline.Token);
            throw new InvalidOperationException("CI_CDP_DETACH_NOT_REPORTED");
        }
        catch (InvalidOperationException e) when (e.Message == "CDP_TARGET_DETACHED") { }
        try
        {
            _ = await session.Call("Test.detachedAgain", new { }, "s-test");
            throw new InvalidOperationException("CI_CDP_DETACHED_SESSION_REUSED");
        }
        catch (InvalidOperationException e) when (e.Message == "CDP_TARGET_DETACHED") { }

        Task<JsonElement> fragmented = session.Call("Test.fragment", new { });
        int fragmentId = (await wire.NextCommandAsync(testDeadline.Token)).GetProperty("id").GetInt32();
        wire.ReplyFragmented(JsonSerializer.Serialize(new
            { id = fragmentId, result = new { name = "fragmented" } }));
        if ((await fragmented.WaitAsync(testDeadline.Token)).GetProperty("name").GetString() !=
            "fragmented" || wire.Aborted || wire.MaxConcurrentReads != 1 || wire.SentCount != 7)
            throw new InvalidOperationException("CI_CDP_RECEIVE_PUMP_SINGLE_OWNER_FAILURE");
        Console.WriteLine("PASS chrome_ci.timeout_late_response_id_dispatch_detach_fragmentation " +
            "single_socket_no_replay");

        using var brokenWire = new ScriptedWebSocket();
        using var brokenSession = new WindowChromeCdp.Session(brokenWire);
        brokenSession.SetDeadline(testDeadline.Token);
        Task<JsonElement> broken = brokenSession.Call("Test.protocol", new { });
        _ = await brokenWire.NextCommandAsync(testDeadline.Token);
        brokenWire.Reply("""{"id":"wrong","result":{}}""");
        try
        {
            _ = await broken.WaitAsync(testDeadline.Token);
            throw new InvalidOperationException("CI_CDP_PROTOCOL_ERROR_NOT_REPORTED");
        }
        catch (JsonException) { }
        if (!brokenWire.Aborted)
            throw new InvalidOperationException("CI_CDP_PROTOCOL_FAILURE_NOT_CLOSED");
        Console.WriteLine("PASS chrome_ci.malformed_protocol_fails_closed");
    }

    private static async Task VerifyFrameTreeFallbackAsync()
    {
        using var wire = new ScriptedWebSocket();
        using var session = new WindowChromeCdp.Session(wire);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        const string sessionId = "ci-background-tab";

        // Simulate the real failure: a single page's FrameTree is silent,
        // while the same browser socket and tab session can answer another
        // read-only Page command containing a main-frame loader identity.
        Task<string> loader = WindowChromeCdp.ReadFrameLoaderAsync(session, sessionId,
            deadline.Token, frameTreeProbeMs: 250);
        var initial = await wire.NextCommandAsync(deadline.Token);
        if (initial.GetProperty("method").GetString() != "Page.getFrameTree" ||
            initial.GetProperty("sessionId").GetString() != sessionId)
            throw new InvalidOperationException("CI_CDP_LOADER_FIRST_METHOD_NOT_FRAME_TREE");
        int oldId = initial.GetProperty("id").GetInt32();
        var fallback = await wire.NextCommandAsync(deadline.Token);
        if (fallback.GetProperty("method").GetString() != "Page.getResourceTree" ||
            fallback.GetProperty("sessionId").GetString() != sessionId)
            throw new InvalidOperationException("CI_CDP_LOADER_FALLBACK_METHOD_INVALID");
        int newId = fallback.GetProperty("id").GetInt32();
        if (oldId == newId) throw new InvalidOperationException("CI_CDP_LOADER_ID_REUSED");

        // The response for the abandoned first command must be discarded,
        // not promoted to a fingerprint for the possibly navigated tab.
        wire.Reply(JsonSerializer.Serialize(new { id = oldId, sessionId,
            result = new { frameTree = new { frame = new { loaderId = "stale-loader" } } } }));
        wire.Reply(JsonSerializer.Serialize(new { id = newId, sessionId,
            result = new { frameTree = new { frame = new { loaderId = "fresh-loader" } } } }));
        if (await loader.WaitAsync(deadline.Token) != "fresh-loader" || wire.Aborted)
            throw new InvalidOperationException("CI_CDP_LOADER_FALLBACK_REUSED_STALE_RESPONSE");

        // A normal root call immediately after this tab-specific fallback
        // continues through the exact same owned socket and one receive pump.
        session.SetDeadline(deadline.Token);
        Task<JsonElement> healthy = session.Call("Browser.getVersion", new { });
        var next = await wire.NextCommandAsync(deadline.Token);
        int healthyId = next.GetProperty("id").GetInt32();
        wire.Reply(JsonSerializer.Serialize(new { id = healthyId,
            result = new { product = "Chrome/fixture" } }));
        if ((await healthy.WaitAsync(deadline.Token)).GetProperty("product").GetString() !=
            "Chrome/fixture" || wire.MaxConcurrentReads != 1 || wire.SentCount != 3)
            throw new InvalidOperationException("CI_CDP_LOADER_FALLBACK_BROKE_SHARED_SOCKET");
        Console.WriteLine("PASS chrome_ci.unresponsive_frame_tree_resource_loader_fallback " +
            "late_reply_ignored_single_socket");

        // A per-tab CDP rejection, or a successful reply missing loaderId,
        // must also reach the read-only alternative. Neither invalid reply
        // means the browser socket is gone or authorizes an effectful replay.
        async Task VerifyNonTimeoutFallbackAsync(bool rejected)
        {
            using var alternateWire = new ScriptedWebSocket();
            using var alternateSession = new WindowChromeCdp.Session(alternateWire);
            using var alternateDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task<string> read = WindowChromeCdp.ReadFrameLoaderAsync(alternateSession,
                sessionId, alternateDeadline.Token, frameTreeProbeMs: 2000);
            var firstCommand = await alternateWire.NextCommandAsync(alternateDeadline.Token);
            if (firstCommand.GetProperty("method").GetString() != "Page.getFrameTree")
                throw new InvalidOperationException("CI_CDP_LOADER_WRONG_PRIMARY_METHOD");
            int primaryId = firstCommand.GetProperty("id").GetInt32();
            if (rejected)
                alternateWire.Reply(JsonSerializer.Serialize(new { id = primaryId, sessionId,
                    error = new { code = -32000, message = "renderer unavailable" } }));
            else
                alternateWire.Reply(JsonSerializer.Serialize(new { id = primaryId, sessionId,
                    result = new { frameTree = new { frame = new { id = "main" } } } }));
            var alternative = await alternateWire.NextCommandAsync(alternateDeadline.Token);
            if (alternative.GetProperty("method").GetString() != "Page.getResourceTree" ||
                alternative.GetProperty("id").GetInt32() == primaryId)
                throw new InvalidOperationException("CI_CDP_LOADER_NO_ALTERNATIVE_ON_BAD_REPLY");
            alternateWire.Reply(JsonSerializer.Serialize(new
            {
                id = alternative.GetProperty("id").GetInt32(), sessionId,
                result = new { frameTree = new { frame = new { loaderId = "alternate-loader" } } }
            }));
            if (await read.WaitAsync(alternateDeadline.Token) != "alternate-loader" ||
                alternateWire.Aborted || alternateWire.SentCount != 2)
                throw new InvalidOperationException("CI_CDP_LOADER_ALTERNATIVE_NOT_VERIFIED");
        }
        await VerifyNonTimeoutFallbackAsync(rejected: true);
        await VerifyNonTimeoutFallbackAsync(rejected: false);
        Console.WriteLine("PASS chrome_ci.rejected_or_missing_loader_falls_back_read_only");

        using (var rejectedWire = new ScriptedWebSocket())
        using (var rejectedSession = new WindowChromeCdp.Session(rejectedWire))
        using (var rejectedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            Task<string> failed = WindowChromeCdp.ReadFrameLoaderAsync(rejectedSession,
                sessionId, rejectedDeadline.Token, frameTreeProbeMs: 2000);
            foreach (string method in new[] { "Page.getFrameTree", "Page.getResourceTree" })
            {
                var command = await rejectedWire.NextCommandAsync(rejectedDeadline.Token);
                if (command.GetProperty("method").GetString() != method)
                    throw new InvalidOperationException("CI_CDP_LOADER_UNEXPECTED_REJECTED_METHOD");
                rejectedWire.Reply(JsonSerializer.Serialize(new
                {
                    id = command.GetProperty("id").GetInt32(), sessionId,
                    error = new { code = -32000, message = "renderer unavailable" }
                }));
            }
            try
            {
                _ = await failed.WaitAsync(rejectedDeadline.Token);
                throw new InvalidOperationException("CI_CDP_LOADER_BOTH_REJECTED_NOT_REPORTED");
            }
            catch (InvalidOperationException e) when (e.Message == "CDP_COMMAND_REJECTED") { }
            if (rejectedWire.Aborted || rejectedWire.SentCount != 2)
                throw new InvalidOperationException("CI_CDP_LOADER_REJECTION_BROKE_SHARED_SOCKET");
        }
        Console.WriteLine("PASS chrome_ci.both_loader_methods_rejected_preserves_socket");

        using var stalledWire = new ScriptedWebSocket();
        using var stalledSession = new WindowChromeCdp.Session(stalledWire);
        using var outer = new CancellationTokenSource(TimeSpan.FromMilliseconds(950));
        Task<string> bothSlow = WindowChromeCdp.ReadFrameLoaderAsync(stalledSession,
            sessionId, outer.Token, frameTreeProbeMs: 150);
        _ = await stalledWire.NextCommandAsync(deadline.Token);
        var resource = await stalledWire.NextCommandAsync(deadline.Token);
        if (resource.GetProperty("method").GetString() != "Page.getResourceTree")
            throw new InvalidOperationException("CI_CDP_LOADER_BOTH_SLOW_NO_RESOURCE_FALLBACK");
        try
        {
            _ = await bothSlow.WaitAsync(deadline.Token);
            throw new InvalidOperationException("CI_CDP_LOADER_BOTH_SLOW_MUST_FAIL_CLOSED");
        }
        catch (OperationCanceledException e) when (
            e.Data["cdp_method"] is "Page.getResourceTree") { }
        if (stalledWire.Aborted)
            throw new InvalidOperationException("CI_CDP_LOADER_BOTH_SLOW_CLOSED_BROWSER_SOCKET");
        Console.WriteLine("PASS chrome_ci.both_loader_methods_timeout_fails_closed " +
            "without_connection_loss");
    }

    private sealed class ScriptedWebSocket : WebSocket
    {
        private sealed record Frame(byte[] Payload, bool Last);
        private readonly Channel<Frame> _inbox = Channel.CreateUnbounded<Frame>();
        private readonly Channel<string> _sent = Channel.CreateUnbounded<string>();
        private int _activeReads;
        private int _maxReads;
        private int _sentCount;
        private int _aborted;

        internal bool Aborted => Volatile.Read(ref _aborted) != 0;
        internal int MaxConcurrentReads => Volatile.Read(ref _maxReads);
        internal int SentCount => Volatile.Read(ref _sentCount);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Aborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => null;

        internal async Task<JsonElement> NextCommandAsync(CancellationToken cancel)
        {
            string json = await _sent.Reader.ReadAsync(cancel);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        internal void Reply(string json) =>
            _inbox.Writer.TryWrite(new Frame(Encoding.UTF8.GetBytes(json), true));

        internal void ReplyFragmented(string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            int midpoint = bytes.Length / 2;
            _inbox.Writer.TryWrite(new Frame(bytes[..midpoint], false));
            _inbox.Writer.TryWrite(new Frame(bytes[midpoint..], true));
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _activeReads);
            InterlockedExtensionsMax(ref _maxReads, active);
            try
            {
                var frame = await _inbox.Reader.ReadAsync(cancellationToken);
                if (frame.Payload.Length > buffer.Count)
                    throw new InvalidOperationException("CI_CDP_FAKE_FRAME_TOO_LONG");
                frame.Payload.CopyTo(buffer.AsSpan());
                return new WebSocketReceiveResult(frame.Payload.Length,
                    WebSocketMessageType.Text, frame.Last);
            }
            finally { Interlocked.Decrement(ref _activeReads); }
        }

        private static void InterlockedExtensionsMax(ref int target, int value)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref target);
                if (observed >= value) return;
            } while (Interlocked.CompareExchange(ref target, value, observed) != observed);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
        {
            if (messageType != WebSocketMessageType.Text || !endOfMessage || Aborted)
                throw new InvalidOperationException("CI_CDP_FAKE_SEND_INVALID");
            Interlocked.Increment(ref _sentCount);
            _sent.Writer.TryWrite(Encoding.UTF8.GetString(buffer));
            return Task.CompletedTask;
        }

        public override void Abort()
        {
            Interlocked.Exchange(ref _aborted, 1);
            _inbox.Writer.TryComplete(new IOException("CDP_SOCKET_CLOSED"));
            _sent.Writer.TryComplete();
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus,
            string? statusDescription, CancellationToken cancellationToken)
        { Abort(); return Task.CompletedTask; }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus,
            string? statusDescription, CancellationToken cancellationToken)
        { Abort(); return Task.CompletedTask; }

        public override void Dispose() => Abort();
    }

    private sealed record PipeClientReport(int OwnerPid, string Generation, bool Verified);

    private static async Task<PipeClientReport> RunPipeClientAsync(string window, string profile,
        string url, string managedProfileId)
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
        start.ArgumentList.Add(managedProfileId);
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
        internal int Drops;
        internal int Confirmations;
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
                    "<input type='file' aria-label='CI_CDP_FILE_INPUT' id='file'>" +
                    "<button aria-label='CI_CDP_BUTTON' id='button'>CI_CDP_BUTTON</button>" +
                    "<button aria-label='CI_CDP_MODAL_OPEN' id='modal-open'>Open dialog</button>" +
                    "<div id='modal-host'></div>" +
                    "<div role='button' tabindex='0' draggable='true' aria-label='CI_CDP_DRAG_SOURCE' " +
                    "id='drag' style='width:140px;height:50px;background:#ddd;margin:15px'>Drag</div>" +
                    "<div role='button' tabindex='0' aria-label='CI_CDP_DROP_TARGET' " +
                    "id='drop' style='width:140px;height:50px;background:#aaa;margin:15px'>Drop</div>" +
                    "<script>" +
                    "document.getElementById('value').addEventListener('input',e=>" +
                    "fetch('/REPLACE/report?text='+encodeURIComponent(e.target.value)));" +
                    "document.getElementById('button').addEventListener('click',()=>" +
                    "fetch('/REPLACE/report?click=1'));" +
                    "document.getElementById('modal-open').addEventListener('click',()=>{" +
                    "const modal=document.createElement('div');modal.setAttribute('role','dialog');" +
                    "const confirm=document.createElement('button');" +
                    "confirm.setAttribute('aria-label','CI_CDP_MODAL_CONFIRM');" +
                    "confirm.textContent='Confirm';modal.append(confirm);" +
                    "document.getElementById('modal-host').append(modal);" +
                    "confirm.addEventListener('click',()=>{modal.remove();" +
                    "fetch('/REPLACE/report?confirm=1')})});" +
                    "document.getElementById('drag').addEventListener('dragstart',e=>" +
                    "e.dataTransfer.setData('text/plain','WMCP'));" +
                    "document.getElementById('drop').addEventListener('dragover',e=>e.preventDefault());" +
                    "document.getElementById('drop').addEventListener('drop',e=>{e.preventDefault();" +
                    "if(e.dataTransfer.getData('text/plain')==='WMCP'){" +
                    "e.target.setAttribute('aria-label','CI_CDP_DROP_COMPLETE');" +
                    "e.target.textContent='CI_CDP_DROP_COMPLETE';" +
                    "fetch('/REPLACE/report?drag=1')}});</script></body></html>";
                body = body.Replace("REPLACE", nonce, StringComparison.Ordinal);
            }
            else if (route.StartsWith($"/{nonce}/report?", StringComparison.Ordinal))
            {
                string query = route[(route.IndexOf('?') + 1)..];
                if (query == "click=1") Interlocked.Increment(ref state.Clicks);
                else if (query == "drag=1") Interlocked.Increment(ref state.Drops);
                else if (query == "confirm=1") Interlocked.Increment(ref state.Confirmations);
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
