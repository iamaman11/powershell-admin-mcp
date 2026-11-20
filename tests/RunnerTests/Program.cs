using System.Diagnostics;

static void Check(bool ok, string label)
{
    if (!ok) throw new InvalidOperationException("FAIL " + label);
    Console.WriteLine("PASS " + label);
}
static async Task<ExecutionResult> Run(string command, int timeout = 10, string? cwd = null, CancellationToken ct = default)
{
    var result = await PowerShellRunner.RunAsync(command, timeout, cwd, ct);
    Console.WriteLine($"runner status={result.Status} exit={result.ExitCode} stage={result.Stage} native_error={result.NativeError} elapsed_ms={result.ElapsedMs}");
    return result;
}
if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
    throw new InvalidOperationException("Isolated Windows CI only");
Check(File.Exists(PowerShellRunner.Executable), "r2.machine_wide_pwsh");
using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
{
    var si = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "whoami.exe"));
    si.ArgumentList.Add("/logonid"); si.UseShellExecute = false; si.RedirectStandardOutput = true;
    using var whoami = Process.Start(si)!;
    string output = await whoami.StandardOutput.ReadToEndAsync();
    await whoami.WaitForExitAsync();
    string expected = System.Text.RegularExpressions.Regex.Match(output, @"S-1-5-5-\d+-\d+").Value;
    Check(whoami.ExitCode == 0 && expected.Length > 0 && TokenLogon.Read(identity) == expected,
        "user.real_native_logon_sid_matches_whoami");
    Check(identity.Groups?.OfType<System.Security.Principal.SecurityIdentifier>().All(x => x.Value != expected) == true,
        "user.managed_groups_exclude_logon_sid_regression");
}
Check(AdminTools.ExecWaitBudget(120)==TimeSpan.FromSeconds(90) && AdminTools.ExecWaitBudget(85)==TimeSpan.FromSeconds(90) &&
    AdminTools.ExecWaitBudget(10)==TimeSpan.FromSeconds(15), "transport.exec_wait_budget");
var testWindowIdentity = new WindowIdentity(0x12345, 4242, 133700000000000000, 1);
string testWindowId = WindowCatalog.FormatId(testWindowIdentity);
Check(WindowCatalog.TryParseId(testWindowId, out var decodedWindowId) && decodedWindowId == testWindowIdentity &&
    !WindowCatalog.TryParseId("w1:bad:token", out _) &&
    !WindowCatalog.TryParseId("w1:0:1:1:1", out _), "windows.stable_ref_contract");
var invalidWindowLimit = await WindowTools.ListWindows(max_windows: 0);
Check(invalidWindowLimit.Status == WindowListCode.INVALID_ARGUMENT && invalidWindowLimit.Windows.Length == 0,
    "windows.bounded_argument_validation");
Check(!WindowCatalog.TryParseId(new string('x', 256), out _), "windows.reject_unbounded_ref");
Check(WindowCatalog.ResolveId(testWindowId, 2, out _) == WindowResolveCode.WRONG_SESSION,
    "windows.reject_cross_session_ref");
var staleWindowId = WindowCatalog.FormatId(new WindowIdentity(long.MaxValue, 4242, 133700000000000000, 1));
Check(WindowCatalog.ResolveId(staleWindowId, 1, out _) == WindowResolveCode.STALE_WINDOW,
    "windows.reject_stale_hwnd");
Check(WindowInspector.ValidArguments(testWindowId, 0, 1) &&
    WindowInspector.ValidArguments(testWindowId, 16, 100) &&
    WindowInspector.ValidArguments(testWindowId, 16, 10, includeDetails: true) &&
    WindowInspector.ValidArguments(testWindowId, 16, 10, nameContains: "status",
        roleFilter: WindowRoleFilter.BUTTON, waitMs: 1500) &&
    !WindowInspector.ValidArguments(testWindowId, 16, 10, waitMs: 50) &&
    !WindowInspector.ValidArguments(testWindowId, 16, 10, nameContains: "x", waitMs: 1501) &&
    !WindowInspector.ValidArguments(testWindowId, 16, 10, nameContains: " ") &&
    !WindowInspector.ValidArguments(testWindowId, 16, 10, nameContains: new string('x', 129)) &&
    !WindowInspector.ValidArguments(testWindowId, 16, 10,
        roleFilter: (WindowRoleFilter)int.MaxValue) &&
    !WindowInspector.ValidArguments(testWindowId, 16, 100, includeDetails: true) &&
    WindowInspector.ValidArguments(testWindowId, 16, 100, offset: 100) &&
    !WindowInspector.ValidArguments(testWindowId, 16, 1, "0.1", null) &&
    !WindowInspector.ValidArguments(testWindowId, 16, 100, offset: 1950) &&
    !WindowInspector.ValidArguments(testWindowId, 17, 50) &&
    !WindowInspector.ValidArguments(testWindowId, 3, 101) &&
    !WindowInspector.ValidArguments("invalid", 3, 60), "windows.inspect_bounded_arguments");
Check(WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.TABS) &&
    WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.TAB, tabId: "0123456789abcdef0123456789abcdef") &&
    !WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.TAB, tabId: "invalid") &&
    !WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.TABS, tabId: "0123456789abcdef0123456789abcdef") &&
    !WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.WINDOW, tabId: "0123456789abcdef0123456789abcdef") &&
    WindowInspector.ArgumentError(testWindowId, 16, 11, includeDetails: true)?.Contains(
        "max_elements", StringComparison.Ordinal) == true,
    "windows.typed_chrome_tab_scope_and_precise_invalid_argument");
Check(WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.TABS, expectedProfile: "Valer",
        expectedOrigin: "https://example.com") &&
    WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.TAB, tabId: "0123456789abcdef0123456789abcdef",
        expectedUrl: "https://example.com/form?a=1") &&
    !WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.WINDOW, expectedProfile: "Valer") &&
    !WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.TABS, expectedOrigin: "https://example.com/other") &&
    !WindowInspector.ValidArguments(testWindowId, 16, 50,
        scope: WindowInspectScope.TABS, expectedOrigin: "https://example.com",
        expectedUrl: "https://other.example/form") &&
    WindowInspector.ArgumentError(testWindowId, 16, 50, scope: WindowInspectScope.TABS,
        expectedOrigin: "not an origin")?.StartsWith("expected_origin:") == true &&
    WindowChromeCdp.MatchesContext("https://example.com/form?a=1#tab", "https://example.com",
        "https://example.com/form?a=1#tab") &&
    !WindowChromeCdp.MatchesContext("https://example.com/form?a=2", "https://example.com",
        "https://example.com/form?a=1") &&
    !WindowChromeCdp.MatchesContext("https://other.example/form", "https://example.com", null),
    "chrome.explicit_native_profile_origin_and_full_url_scope");
Check(WindowInspector.InspectCore(IntPtr.Zero, 1, 20).Status == WindowInspectCode.INVALID_ARGUMENT,
    "windows.inspect_reject_null_hwnd");
var invalidInspect = await WindowTools.InspectWindow("invalid");
Check(invalidInspect.Status == WindowInspectCode.INVALID_ARGUMENT, "windows.inspect_reject_bad_ref_before_ipc");
string freshElementId = "e1:" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x") +
    ":0123456789abcdef0123456789abcdef";
Check(WindowInspector.TryParseElementRef(freshElementId, out _) &&
    !WindowInspector.TryParseElementRef("bad", out _) &&
    !WindowInspector.TryParseElementRef("e1:1:0123456789abcdef0123456789abcdef", out _),
    "windows.action_fresh_reference_and_stale_rejection");
Check(WindowInspector.TryParsePath("0.1.0.25", out var parsedPath) &&
    parsedPath.SequenceEqual(new[] { 1, 0, 25 }) &&
    !WindowInspector.TryParsePath("0..1", out _) &&
    !WindowInspector.TryParsePath("0.01", out _) &&
    !WindowInspector.TryParsePath("1.0", out _) &&
    !WindowInspector.TryParsePath("0.2001", out _), "windows.inspect_strict_path_contract");
Check(WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.INVOKE, null)) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.CLICK, null)) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.TYPE_TEXT,
        "https://example.com/")) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.TYPE_TEXT,
        null)) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.KEY_PRESS,
        null, Key: WindowKey.ENTER)) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.KEY_PRESS,
        null)) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.KEY_PRESS,
        null, Key: (WindowKey)99)) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.CLICK,
        null, Key: WindowKey.SPACE)) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.CLICK, null,
        ExpectedName: "CI_UIA_CLICKED", ExpectedChange: WindowExpectedChange.APPEARS)) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.CLICK, null,
        ExpectedName: "CI_UIA_CLICKED")) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.CLICK, "unexpected")) &&
    WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.INVOKE, null, "0.1")) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.INVOKE, null, "0..1")) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.INVOKE, "unexpected")) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId, WindowActionKind.SET_VALUE, new string('x', 4097))),
    "windows.action_bounded_arguments");
Check(WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId,
        WindowActionKind.SET_VALUE, "next", ExpectedName: "CI_UIA_INPUT_NAME",
        ExpectedChange: WindowExpectedChange.APPEARS, ExpectedKind: WindowExpectedKind.UIA_VALUE,
        ExpectedValue: "next")) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId,
        WindowActionKind.INVOKE, null, ExpectedKind: WindowExpectedKind.UIA_VALUE,
        ExpectedValue: "next")) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId,
        WindowActionKind.INVOKE, null, ExpectedName: "field",
        ExpectedChange: WindowExpectedChange.APPEARS, ExpectedKind: WindowExpectedKind.UIA_ELEMENT,
        ExpectedValue: "next")) &&
    !WindowAction.Valid(new WindowActionRequest(testWindowId, freshElementId,
        WindowActionKind.INVOKE, null, ExpectedProfile: "Valer")),
    "windows.value_postcondition_and_browser_scope_are_typed");
Check(WindowChromeCdp.ValidTargetId("0123456789abcdef0123456789abcdef") &&
    !WindowChromeCdp.ValidTargetId("https://example.com") &&
    !WindowChromeCdp.ValidTabElementRef("c1:1:bad:1:bad:deadbeef"),
    "chrome.cdp_target_and_element_reference_boundaries");
using (var protectedNode = System.Text.Json.JsonDocument.Parse(
    "{\"properties\":[{\"name\":\"protected\",\"value\":{\"value\":true}}]}"))
    Check(WindowChromeCdp.IsProtectedAx(protectedNode.RootElement),
        "chrome.cdp_protected_fields_are_not_actionable");
using (var ax = System.Text.Json.JsonDocument.Parse("""
    {"nodes":[{"name":{"value":"test field"},"value":{"value":"updated"}},
              {"name":{"value":"hidden"},"value":{"value":"secret"},
               "properties":[{"name":"protected","value":{"value":true}}]}]}
    """))
    Check(WindowChromeCdp.HasAxValue(ax.RootElement, "test field", "updated") &&
        !WindowChromeCdp.HasAxValue(ax.RootElement, "test field", "wrong") &&
        !WindowChromeCdp.HasAxValue(ax.RootElement, "hidden", "secret") &&
        !WindowChromeCdp.HasAxValue(ax.RootElement, "unknown", ""),
        "chrome.cdp_value_postcondition_is_exact_and_ignores_protected_fields");
Check(new WindowActionResult(WindowActionCode.COMPLETED).EffectState == WindowEffectState.EFFECT_UNKNOWN &&
    new WindowActionResult(WindowActionCode.COMPLETED, ExpectedStateObserved: true).EffectState ==
        WindowEffectState.EFFECT_VERIFIED &&
    new WindowActionResult(WindowActionCode.STALE_ELEMENT).EffectState == WindowEffectState.NO_EFFECT &&
    new WindowActionResult(WindowActionCode.ACTION_FAILED_UNKNOWN_EFFECT, EffectUnknown: true).EffectState ==
        WindowEffectState.EFFECT_UNKNOWN, "windows.action_effect_state_never_assumes_success");
Check(WindowChromeCdp.TryReadEndpoint(
        "9222\n/devtools/browser/01234567-89ab-cdef-0123-456789abcdef\n", out var chromeEndpoint) &&
    chromeEndpoint?.Host == "127.0.0.1" && chromeEndpoint.Port == 9222 &&
    !WindowChromeCdp.TryReadEndpoint(
        "9222\nhttp://example.com/steal", out _) &&
    !WindowChromeCdp.TryReadEndpoint(
        "0\n/devtools/browser/01234567-89ab-cdef-0123-456789abcdef", out _) &&
    !WindowChromeCdp.TryReadEndpoint(
        "99999\n/devtools/browser/01234567-89ab-cdef-0123-456789abcdef", out _),
    "chrome.cdp_local_opt_in_endpoint_only");
Check(WindowChromeCdp.MatchesWindow("Profile Control Plane - Google Chrome (Valer)",
        "Profile Control Plane", new WindowBounds(100, 80, 1200, 800),
        new WindowBounds(80, 64, 960, 640), 1.25) &&
    !WindowChromeCdp.MatchesWindow("Unrelated - Google Chrome (Valer)",
        "Profile Control Plane", new WindowBounds(100, 80, 1200, 800),
        new WindowBounds(80, 64, 960, 640), 1.25) &&
    !WindowChromeCdp.MatchesWindow("Profile Control Plane - Google Chrome (Valer)",
        "Profile Control Plane", new WindowBounds(900, 80, 1200, 800),
        new WindowBounds(80, 64, 960, 640), 1.25),
    "chrome.cdp_exact_window_title_and_bounds");
using (var model = System.Text.Json.JsonDocument.Parse("{\"quad\":[10,20,30,20,30,40,10,40],\"viewport\":{\"clientWidth\":100,\"clientHeight\":100}}"))
{
    var root = model.RootElement;
    Check(WindowChromeCdp.TryPoint(root.GetProperty("quad"), root.GetProperty("viewport"),
            out var cx, out var cy) && cx == 20 && cy == 30 &&
        !WindowChromeCdp.TryPoint(root.GetProperty("quad"),
            System.Text.Json.JsonDocument.Parse("{\"clientWidth\":15,\"clientHeight\":100}").RootElement,
            out _, out _), "chrome.cdp_reject_off_viewport_click");
}
var providerFailed = new WindowActionResult(WindowActionCode.ACTION_FAILED_UNKNOWN_EFFECT,
    EffectUnknown: true, FailureStage: WindowFailureStage.DISPATCH_UIA,
    HResult: "80040200", ExceptionType: "COMException", ProviderResponse: "FAIL");
var reconciled = WindowAction.ReconcileObserved(providerFailed, true);
var unverified = WindowAction.ReconcileObserved(providerFailed, false);
Check(reconciled.Status == WindowActionCode.ACTION_FAILED_UNKNOWN_EFFECT &&
    reconciled.EffectState == WindowEffectState.EFFECT_VERIFIED && !reconciled.EffectUnknown &&
    reconciled.ExpectedStateObserved == true &&
    reconciled.HResult == providerFailed.HResult &&
    reconciled.FailureStage == WindowFailureStage.DISPATCH_UIA &&
    reconciled.ProviderResponse == providerFailed.ProviderResponse &&
    unverified.EffectState == WindowEffectState.EFFECT_UNKNOWN &&
    unverified.FailureStage == WindowFailureStage.DISPATCH_UIA,
    "windows.provider_error_reconciles_actual_effect_without_retry_or_losing_diagnostics");
var invalidAction = await WindowTools.InvokeElement("bad", "bad", WindowActionKind.INVOKE, confirm: true);
Check(invalidAction.Status == WindowActionCode.INVALID_ARGUMENT, "windows.action_reject_bad_ref_before_ipc");
var missingConfirmation = await WindowTools.InvokeElement(testWindowId, freshElementId,
    WindowActionKind.INVOKE, confirm: false);
Check(missingConfirmation.Status == WindowActionCode.CONFIRMATION_REQUIRED,
    "windows.action_supports_explicit_confirmation_opt_out");
// This is an owned throwaway fixture. No real user window or screen pixels are read.
var uiaDesktop = DesktopCapture.ObserveDesktop(Process.GetCurrentProcess().SessionId);
if (uiaDesktop.Code == CaptureCode.COMPLETED)
{
    var ready = new TaskCompletionSource<(IntPtr Handle, System.Windows.Forms.Form Form, System.Windows.Forms.TextBox Input)>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    int clickCount = 0;
    int deepClickCount = 0;
    int pagedClickCount = 0;
    int keyboardClickCount = 0;
    System.Windows.Forms.Form? testCover = null;
    var uiThread = new Thread(() =>
    {
        try
        {
            using var form = new System.Windows.Forms.Form { Text = "CI_UIA_OWNED_WINDOW", Width = 330, Height = 340 };
            using var button = new System.Windows.Forms.Button
                { Text = "CI_UIA_OWNED_BUTTON", AccessibleName = "CI_UIA_OWNED_BUTTON", Width = 250 };
            using var input = new System.Windows.Forms.TextBox
                { Text = "CI_UIA_SECRET_FIXTURE", AccessibleName = "CI_UIA_INPUT_NAME", Top = 40, Width = 250 };
            using var checkbox = new System.Windows.Forms.CheckBox
                { Text = "CI_UIA_OWNED_CHECKBOX", AccessibleName = "CI_UIA_OWNED_CHECKBOX", Top = 76 };
            using var radio = new System.Windows.Forms.RadioButton
                { Text = "CI_UIA_OWNED_RADIO", AccessibleName = "CI_UIA_OWNED_RADIO", Top = 108 };
            using var cover = new System.Windows.Forms.Form
                { Text = "CI_UIA_COVER", FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                  TopMost = true, ShowInTaskbar = false };
            button.Click += (_, _) =>
            {
                if (Interlocked.Increment(ref clickCount) == 2)
                {
                    button.Text = "CI_UIA_CLICKED";
                    button.AccessibleName = "CI_UIA_CLICKED";
                }
            };
            form.Controls.Add(button);
            form.Controls.Add(input);
            form.Controls.Add(checkbox);
            form.Controls.Add(radio);
            var keyboardButton = new System.Windows.Forms.Button
                { Text = "CI_UIA_KEYBOARD_BUTTON", AccessibleName = "CI_UIA_KEYBOARD_BUTTON",
                  Top = 205, Width = 250 };
            keyboardButton.Click += (_, _) =>
            {
                Interlocked.Increment(ref keyboardClickCount);
                keyboardButton.Text = "CI_UIA_KEY_ACCEPTED";
                keyboardButton.AccessibleName = "CI_UIA_KEY_ACCEPTED";
            };
            form.Controls.Add(keyboardButton);
            var delayedButton = new System.Windows.Forms.Button
                { Text = "CI_UIA_PENDING", AccessibleName = "CI_UIA_PENDING",
                  Top = 244, Width = 250 };
            form.Controls.Add(delayedButton);
            // Emulate Chromium's deeply nested ControlView hierarchy without a browser dependency.
            var nested = new System.Windows.Forms.Panel
                { Left = 10, Top = 142, Width = 275, Height = 42,
                  AccessibleRole = System.Windows.Forms.AccessibleRole.Grouping };
            form.Controls.Add(nested);
            for (int level = 0; level < 7; level++)
            {
                var inner = new System.Windows.Forms.Panel
                    { Dock = System.Windows.Forms.DockStyle.Fill,
                      AccessibleRole = System.Windows.Forms.AccessibleRole.Grouping };
                nested.Controls.Add(inner);
                nested = inner;
            }
            var deepButton = new System.Windows.Forms.Button
                { Text = "CI_UIA_DEEP_BUTTON", AccessibleName = "CI_UIA_DEEP_BUTTON",
                  Dock = System.Windows.Forms.DockStyle.Fill };
            deepButton.Click += (_, _) => Interlocked.Increment(ref deepClickCount);
            nested.Controls.Add(deepButton);
            // More than one page of UIA controls. An addressable action must
            // work even when the target lies beyond the first 100 elements.
            var many = new System.Windows.Forms.Panel
                { Left = 292, Top = 10, Width = 30, Height = 80, AutoScroll = true };
            form.Controls.Add(many);
            for (int i = 0; i < 125; i++)
            {
                var item = new System.Windows.Forms.Button
                {
                    Text = i == 124 ? "CI_UIA_PAGED_BUTTON" : $"CI_UIA_PAGE_{i}",
                    AccessibleName = i == 124 ? "CI_UIA_PAGED_BUTTON" : $"CI_UIA_PAGE_{i}",
                    Left = 0, Top = i * 25, Width = 50, Height = 24
                };
                if (i == 124) item.Click += (_, _) => Interlocked.Increment(ref pagedClickCount);
                many.Controls.Add(item);
            }
            form.Shown += (_, _) =>
            {
                testCover = cover;
                cover.BackColor = System.Drawing.Color.Magenta;
                cover.Bounds = form.Bounds;
                cover.Show();
                ready.TrySetResult((form.Handle, form, input));
            };
            System.Windows.Forms.Application.Run(form);
        }
        catch (Exception error) { ready.TrySetException(error); }
    }) { IsBackground = true };
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    System.Windows.Forms.Form? ownedForm = null;
    try
    {
        var created = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ownedForm = created.Form;
        using var current = Process.GetCurrentProcess();
        var realWindowId = WindowCatalog.FormatId(new WindowIdentity(created.Handle.ToInt64(),
            current.Id, current.StartTime.ToFileTimeUtc(), current.SessionId));
        var result = await Task.Run(() => WindowInspector.InspectCore(created.Handle, 3, 40, realWindowId))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(result.Status == WindowInspectCode.COMPLETED &&
            result.Elements.Any(e => e.Name == "CI_UIA_OWNED_BUTTON") &&
            result.Elements.Any(e => e.Role.Contains("Edit", StringComparison.Ordinal) &&
                                     e.Name == "[redacted]") &&
            result.Elements.All(e => !e.Name.Contains("CI_UIA_SECRET_FIXTURE", StringComparison.Ordinal)),
            "windows.inspect_owned_occluded_winforms_tree_and_redaction");
        Check(result.ElementRefExpiresUtc > DateTimeOffset.UtcNow &&
            result.Elements.All(e => WindowInspector.TryParseElementRef(e.ElementId, out _)),
            "windows.action_inspection_returns_fresh_selectors");
        var detailed = await Task.Run(() => WindowInspector.InspectCore(created.Handle, 3, 10,
            realWindowId, includeDetails: true)).WaitAsync(TimeSpan.FromSeconds(5));
        var detailedButton = detailed.Elements.Single(e => e.Name == "CI_UIA_OWNED_BUTTON");
        Check(detailedButton.Bounds is { Width: > 0, Height: > 0 } &&
            detailedButton.KeyboardFocusable == true && !detailedButton.Password,
            "windows.inspect_opt_in_geometry_and_focus_details");
        var matched = await Task.Run(() => WindowInspector.InspectCore(created.Handle, 3, 10,
            realWindowId, nameContains: "owned_button", roleFilter: WindowRoleFilter.BUTTON,
            includeDetails: true)).WaitAsync(TimeSpan.FromSeconds(5));
        Check(matched.Status == WindowInspectCode.COMPLETED &&
            matched.Elements.Length == 1 && matched.Elements[0].Name == "CI_UIA_OWNED_BUTTON" &&
            matched.Elements[0].Bounds is not null && matched.Elements[0].Path.StartsWith("0."),
            "windows.find_semantic_name_role_and_details_on_occluded_window");
        var missing = await Task.Run(() => WindowInspector.InspectCore(created.Handle, 3, 10,
            realWindowId, nameContains: "CI_UIA_DOES_NOT_EXIST", waitMs: 150))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(missing.Status == WindowInspectCode.STATE_NOT_OBSERVED && missing.Elements.Length == 0,
            "windows.wait_for_missing_state_returns_explicit_timeout");
        created.Form.Invoke(new Action(() =>
        {
            var target = created.Form.Controls.OfType<System.Windows.Forms.Button>()
                .Single(b => b.AccessibleName == "CI_UIA_PENDING");
            target.Text = "CI_UIA_DELAYED_READY";
            target.AccessibleName = "CI_UIA_DELAYED_READY";
        }));
        var delayed = await Task.Run(() => WindowInspector.InspectCore(created.Handle, 3, 10,
            realWindowId, nameContains: "CI_UIA_DELAYED_READY", roleFilter: WindowRoleFilter.BUTTON,
            waitMs: 1200)).WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine($"DELAYED_SEARCH status={delayed.Status} nodes={delayed.Elements.Length}");
        Check(delayed.Status == WindowInspectCode.COMPLETED &&
            delayed.Elements.Length == 1 && delayed.Elements[0].Name == "CI_UIA_DELAYED_READY",
            "windows.wait_for_changed_react_style_element_without_focus");
        var rejectedKeyboard = await Task.Run(() => WindowPhysicalClick.PressKey(
            IntPtr.Zero, System.Windows.Automation.AutomationElement.FromHandle(created.Handle),
            new WindowActionRequest(realWindowId, delayed.Elements[0].ElementId,
                WindowActionKind.KEY_PRESS, null, Key: WindowKey.SPACE), Stopwatch.StartNew()))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(rejectedKeyboard.Status == WindowActionCode.FOCUS_UNAVAILABLE &&
            Volatile.Read(ref keyboardClickCount) == 0,
            "windows.key_press_rejects_invalid_window_before_input");
        var rejectedText = await Task.Run(() => WindowPhysicalClick.TypeText(
            IntPtr.Zero, System.Windows.Automation.AutomationElement.FromHandle(created.Handle),
            new WindowActionRequest(realWindowId, delayed.Elements[0].ElementId,
                WindowActionKind.TYPE_TEXT, "test"), Stopwatch.StartNew()))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(rejectedText.Status == WindowActionCode.UNSUPPORTED_ACTION,
            "windows.type_text_rejects_unrelated_control_without_input");
        if (Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported())
        {
            var image = await WindowCapture.CaptureCoreAsync(created.Handle, realWindowId, 640)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Check(image.Result.Status == WindowCaptureCode.COMPLETED &&
                image.Png is { Length: > 100 } && image.Png.Length <= 2_000_000 &&
                image.Png[0] == 137 && image.Png[1] == 80 &&
                image.Result.Width is > 0 and <= 640,
                "windows.capture_owned_occluded_winforms_png");
            using var pngData = new MemoryStream(image.Png!);
            using var capturedBitmap = new System.Drawing.Bitmap(pngData);
            int magenta = 0;
            for (int y = 0; y < capturedBitmap.Height; y += 10)
                for (int x = 0; x < capturedBitmap.Width; x += 10)
                {
                    var pixel = capturedBitmap.GetPixel(x, y);
                    if (pixel.R > 230 && pixel.G < 40 && pixel.B > 230) magenta++;
                }
            Check(magenta == 0, "windows.capture_target_not_magenta_cover");
        }
        else Console.WriteLine("NOT_TESTED windows.capture_owned_occluded_winforms_png wgc_unsupported");
        var buttonTarget = result.Elements.Single(e => e.Name == "CI_UIA_OWNED_BUTTON");
        var click = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, buttonTarget.ElementId, WindowActionKind.INVOKE, null)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(click.Status == WindowActionCode.COMPLETED &&
            Volatile.Read(ref clickCount) == 1, "windows.action_invoke_occluded_button");
        Check(click.ForegroundChanged == false && click.TargetActivated == false,
            "windows.action_invoke_preserves_foreground");
        var inputTarget = result.Elements.Single(e => e.Role.Contains("Edit", StringComparison.Ordinal));
        var edit = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, inputTarget.ElementId, WindowActionKind.SET_VALUE,
                "CI_UIA_UPDATED_VALUE", ExpectedName: "CI_UIA_INPUT_NAME",
                ExpectedChange: WindowExpectedChange.APPEARS,
                ExpectedKind: WindowExpectedKind.UIA_VALUE,
                ExpectedValue: "CI_UIA_UPDATED_VALUE"))).WaitAsync(TimeSpan.FromSeconds(5));
        var edited = (string)created.Form.Invoke(new Func<string>(() => created.Input.Text));
        Check(edit.Status == WindowActionCode.COMPLETED && edited == "CI_UIA_UPDATED_VALUE" &&
            edit.EffectState == WindowEffectState.EFFECT_VERIFIED,
            "windows.action_set_value_occluded_edit_with_verified_property");
        Check(edit.ForegroundChanged == false && edit.TargetActivated == false,
            "windows.action_set_value_preserves_foreground");
        var repeatedEdit = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, inputTarget.ElementId, WindowActionKind.SET_VALUE,
                "CI_UIA_UPDATED_VALUE", ExpectedName: "CI_UIA_INPUT_NAME",
                ExpectedChange: WindowExpectedChange.APPEARS,
                ExpectedKind: WindowExpectedKind.UIA_VALUE,
                ExpectedValue: "CI_UIA_UPDATED_VALUE"))).WaitAsync(TimeSpan.FromSeconds(5));
        Check(repeatedEdit.Status == WindowActionCode.EXPECTED_STATE_ALREADY_TRUE &&
            (string)created.Form.Invoke(new Func<string>(() => created.Input.Text)) == "CI_UIA_UPDATED_VALUE",
            "windows.value_precondition_prevents_repeated_dispatch");
        var checkboxTarget = result.Elements.Single(e => e.Role.Contains("CheckBox", StringComparison.Ordinal));
        var toggle = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, checkboxTarget.ElementId, WindowActionKind.TOGGLE, null)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        bool checkedAfter = (bool)created.Form.Invoke(new Func<bool>(() =>
            created.Form.Controls.OfType<System.Windows.Forms.CheckBox>().Single().Checked));
        Check(toggle.Status == WindowActionCode.COMPLETED && toggle.ForegroundChanged == false &&
            toggle.TargetActivated == false &&
            checkedAfter, "windows.action_toggle_occluded_checkbox_without_focus");
        var radioTarget = result.Elements.Single(e => e.Role.Contains("RadioButton", StringComparison.Ordinal));
        var select = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, radioTarget.ElementId, WindowActionKind.SELECT, null)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        bool selectedAfter = (bool)created.Form.Invoke(new Func<bool>(() =>
            created.Form.Controls.OfType<System.Windows.Forms.RadioButton>().Single().Checked));
        Check(select.Status == WindowActionCode.COMPLETED && select.ForegroundChanged == false &&
            select.TargetActivated == false &&
            selectedAfter, "windows.action_select_occluded_radio_without_focus");
        var deepTree = await Task.Run(() => WindowInspector.InspectCore(
            created.Handle, WindowInspector.MaxDepth, 100, realWindowId))
            .WaitAsync(TimeSpan.FromSeconds(5));
        var deepTarget = deepTree.Elements.SingleOrDefault(e => e.Name == "CI_UIA_DEEP_BUTTON");
        if (deepTarget is null && deepTree.NextOffset is int nextPage)
        {
            var nextDeepTree = await Task.Run(() => WindowInspector.InspectCore(
                created.Handle, WindowInspector.MaxDepth, 100, realWindowId, offset: nextPage))
                .WaitAsync(TimeSpan.FromSeconds(5));
            deepTarget = nextDeepTree.Elements.SingleOrDefault(e => e.Name == "CI_UIA_DEEP_BUTTON");
        }
        Check(deepTree.Status == WindowInspectCode.COMPLETED && deepTarget is { Depth: > 5 },
            "windows.inspect_deep_controls_under_bounded_tree");
        var deepInvoke = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, deepTarget!.ElementId, WindowActionKind.INVOKE, null)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(deepInvoke.Status == WindowActionCode.COMPLETED &&
            deepInvoke.ForegroundChanged == false && deepInvoke.TargetActivated == false &&
            Volatile.Read(ref deepClickCount) == 1,
            "windows.action_deep_control_without_focus");
        var page1 = await Task.Run(() => WindowInspector.InspectCore(
            created.Handle, WindowInspector.MaxDepth, 100, realWindowId))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(page1.Status == WindowInspectCode.COMPLETED && page1.Truncated &&
            page1.NextOffset == 100, "windows.inspect_bounded_first_page");
        var page2 = await Task.Run(() => WindowInspector.InspectCore(
            created.Handle, WindowInspector.MaxDepth, 100, realWindowId, offset: 100))
            .WaitAsync(TimeSpan.FromSeconds(5));
        var paged = page2.Elements.SingleOrDefault(e => e.Name == "CI_UIA_PAGED_BUTTON");
        Check(page2.Status == WindowInspectCode.COMPLETED && paged is not null &&
            page1.Elements.All(e => e.Name != paged.Name), "windows.inspect_second_page_exposes_deep_sibling");
        if (paged is null) throw new InvalidOperationException("PAGED_TARGET_MISSING");
        var pathInvoke = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, paged.ElementId, WindowActionKind.INVOKE,
                null, paged.Path))).WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine($"PAGED_ACTION status={pathInvoke.Status} detail={pathInvoke.Detail} " +
            $"focus={pathInvoke.ForegroundChanged} clicks={Volatile.Read(ref pagedClickCount)} " +
            $"path={paged.Path}");
        Check(pathInvoke.Status == WindowActionCode.COMPLETED &&
            pathInvoke.ForegroundChanged == false && pathInvoke.TargetActivated == false &&
            Volatile.Read(ref pagedClickCount) == 1,
            "windows.action_paged_element_exact_path_without_focus");
        var parentPath = paged.Path[..paged.Path.LastIndexOf('.')];
        var parent = page1.Elements.Concat(page2.Elements).Single(e => e.Path == parentPath);
        var subtree = await Task.Run(() => WindowInspector.InspectCore(
            created.Handle, WindowInspector.MaxDepth, 100, realWindowId,
            parent.Path, parent.ElementId, offset: 100)).WaitAsync(TimeSpan.FromSeconds(5));
        Check(subtree.Status == WindowInspectCode.COMPLETED &&
            subtree.Elements.Any(e => e.Name == paged.Name),
            "windows.inspect_verified_subtree_page");
        var staleSubtree = await Task.Run(() => WindowInspector.InspectCore(
            created.Handle, WindowInspector.MaxDepth, 10, realWindowId,
            parent.Path, paged.ElementId)).WaitAsync(TimeSpan.FromSeconds(5));
        Check(staleSubtree.Status == WindowInspectCode.STALE_ELEMENT,
            "windows.inspect_reject_mismatched_subtree_ref");
        var stalePath = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, paged.ElementId, WindowActionKind.INVOKE,
                null, parent.Path))).WaitAsync(TimeSpan.FromSeconds(5));
        Check(stalePath.Status == WindowActionCode.STALE_ELEMENT && Volatile.Read(ref pagedClickCount) == 1,
            "windows.action_reject_mismatched_exact_path");
        var freshClickTree = await Task.Run(() => WindowInspector.InspectCore(
            created.Handle, 3, 40, realWindowId)).WaitAsync(TimeSpan.FromSeconds(5));
        var freshButton = freshClickTree.Elements.Single(e => e.Name == "CI_UIA_OWNED_BUTTON");
        var blocked = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, freshButton.ElementId, WindowActionKind.CLICK, null)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(blocked.Status is WindowActionCode.TARGET_OCCLUDED or
            WindowActionCode.CLICK_POINT_UNAVAILABLE or WindowActionCode.FOREGROUND_UNAVAILABLE &&
            Volatile.Read(ref clickCount) == 1, "windows.click_rejects_occluded_target_before_input");
        created.Form.Invoke(new Action(() => testCover!.Hide()));
        var uncoveredTree = await Task.Run(() => WindowInspector.InspectCore(
            created.Handle, 3, 40, realWindowId)).WaitAsync(TimeSpan.FromSeconds(5));
        var uncoveredButton = uncoveredTree.Elements.Single(e => e.Name == "CI_UIA_OWNED_BUTTON");
        var physical = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, uncoveredButton.ElementId, WindowActionKind.CLICK,
                null, ExpectedName: "CI_UIA_CLICKED", ExpectedChange: WindowExpectedChange.APPEARS)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        if (physical.Status == WindowActionCode.FOREGROUND_UNAVAILABLE)
            Console.WriteLine("NOT_TESTED windows.click_owned_button_foreground_denied_by_ci_desktop");
        else
            Check(physical.Status == WindowActionCode.COMPLETED &&
                physical.EffectState == WindowEffectState.EFFECT_VERIFIED &&
                physical.TargetVerified && Volatile.Read(ref clickCount) == 2,
                "windows.click_owned_button_exactly_once_with_verified_effect");
        var keyboardTree = await Task.Run(() => WindowInspector.InspectCore(created.Handle, 3, 10,
            realWindowId, nameContains: "CI_UIA_KEYBOARD_BUTTON", roleFilter: WindowRoleFilter.BUTTON))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(keyboardTree.Status == WindowInspectCode.COMPLETED &&
            keyboardTree.Elements.Length == 1, "windows.key_press_locates_exact_target");
        var keyTarget = keyboardTree.Elements[0];
        var staleKey = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, keyTarget.ElementId,
                WindowActionKind.KEY_PRESS, null, "0.999", Key: WindowKey.SPACE)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check(staleKey.Status == WindowActionCode.STALE_ELEMENT &&
            Volatile.Read(ref keyboardClickCount) == 0,
            "windows.key_press_rejects_stale_path_before_focus_or_input");
        var keyboard = await Task.Run(() => WindowAction.InvokeCore(created.Handle,
            new WindowActionRequest(realWindowId, keyTarget.ElementId,
                WindowActionKind.KEY_PRESS, null, keyTarget.Path,
                "CI_UIA_KEY_ACCEPTED", WindowExpectedChange.APPEARS, WindowKey.SPACE)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        if (keyboard.Status is WindowActionCode.FOREGROUND_UNAVAILABLE or WindowActionCode.FOCUS_UNAVAILABLE)
        {
            Check(Volatile.Read(ref keyboardClickCount) == 0,
                "windows.key_press_never_sends_when_focus_cannot_be_proven");
            Console.WriteLine($"NOT_TESTED windows.key_press_owned_button focus={keyboard.Status}");
        }
        else
            Check(keyboard.Status == WindowActionCode.COMPLETED &&
                keyboard.EffectState == WindowEffectState.EFFECT_VERIFIED &&
                keyboard.TargetVerified && Volatile.Read(ref keyboardClickCount) == 1,
                "windows.key_press_owned_button_exactly_once_with_verified_effect");
    }
    finally
    {
        if (ownedForm is { IsDisposed: false, IsHandleCreated: true })
        {
            try { ownedForm.BeginInvoke(new Action(ownedForm.Close)); }
            catch (InvalidOperationException) { }
        }
        uiThread.Join(2000);
    }
}
else Console.WriteLine($"NOT_TESTED windows.inspect_owned_occluded_winforms_tree desktop={uiaDesktop.Code}");
var simple = await Run("Write-Output 'hello'");
Check(simple.Status == ExecutionState.COMPLETED && simple.ExitCode == 0 && simple.Stdout.Trim() == "hello", "r2.stdout_exit");
string prefix = "Write-Output 'Проверка Unicode 🚀'\n#";
var unicode = await Run(prefix + new string('x', 11982 - prefix.Length));
Check(unicode.Status == ExecutionState.COMPLETED && unicode.Stdout.Trim() == "Проверка Unicode 🚀", "r2.long_unicode_stdin");
var explicitExit = await Run("exit 7");
Check(explicitExit.Status == ExecutionState.COMMAND_ERROR && explicitExit.ExitCode == 7, "r2.explicit_exit");
var thrown = await Run("throw 'EXPECTED_PROBE_FAILURE'");
Check(thrown.Status == ExecutionState.COMMAND_ERROR && thrown.Stderr.Contains("EXPECTED_PROBE_FAILURE"), "r2.exception");
var warning = await Run("Write-Warning 'expected warning'; Write-Output 'after'");
Check(warning.Status == ExecutionState.COMPLETED && warning.Warnings?.Contains("expected warning") == true &&
    warning.Stdout.Trim() == "after", "r2.warning_stream");
var nonTerminating = await Run("Write-Error 'expected error' -ErrorAction Continue; Write-Output 'after'");
Check(nonTerminating.Status == ExecutionState.COMMAND_ERROR && nonTerminating.Stderr.Contains("expected error"), "r2.nonterminating_error");
var large = await Run("[Console]::Out.Write(('a' * 100000)); [Console]::Error.Write(('b' * 100000))");
Check(large.Status == ExecutionState.COMPLETED && large.OutputTruncated && large.Stdout.Length == 16000 &&
    large.Stderr.Length == PowerShellRunner.OutputLimit, "r2.bounded_concurrent_drain");
foreach(int count in new[]{15998,15999,16000,30000})
{
    var boundary=await Run($"[Console]::Error.Write(('e'*{count})); Write-Output 'boundary'");
    Check(boundary.Status==ExecutionState.COMPLETED &&
        boundary.Stderr==new string('e',Math.Min(count,PowerShellRunner.OutputLimit)) &&
        boundary.OutputTruncated==(count>PowerShellRunner.OutputLimit),
        $"h1.stderr_exact_boundary_{count}");
}
var lf=await Run("for($i=0;$i -lt 8000;$i++){[Console]::Error.Write(\"e+`n\")}");
Check(lf.Status==ExecutionState.COMPLETED && lf.Stderr.Length==PowerShellRunner.OutputLimit &&
    lf.Stderr==string.Concat(Enumerable.Repeat("e+",5333).Select(value=>value+"\n"))+"e" &&
    lf.OutputTruncated,"h1.lf_stream_never_expands");
var unicodeBoundary=await Run("[Console]::Error.Write(('e'*15999)+'🚀')");
Check(unicodeBoundary.Status==ExecutionState.COMPLETED && unicodeBoundary.Stderr==new string('e',15999) &&
    unicodeBoundary.OutputTruncated,"h1.stderr_surrogate_safe_boundary");
var mixedWarnings=await Run("Write-Warning 'fixture'; [Console]::Error.Write(('e'*30000))");
Check(mixedWarnings.Status==ExecutionState.COMPLETED && mixedWarnings.Warnings?.Contains("fixture")==true &&
    mixedWarnings.Stderr.Length<PowerShellRunner.OutputLimit && mixedWarnings.OutputTruncated,
    "h1.warning_filter_preserves_capture_bound");
var cwd = await Run("(Get-Location).Path", cwd: Path.GetTempPath());
Check(cwd.Status == ExecutionState.COMPLETED && Path.TrimEndingDirectorySeparator(cwd.Stdout.Trim()) ==
    Path.TrimEndingDirectorySeparator(Path.GetTempPath()), "r2.working_directory");
var timeout = await Run("Start-Sleep -Seconds 30", 1);
Check(timeout.Status == ExecutionState.TIMEOUT_UNKNOWN_EFFECT && timeout.ElapsedMs < 10000, "r2.timeout");
using (var cancel = new CancellationTokenSource())
{
    cancel.Cancel();
    var before = await Run("Write-Output 'must not execute'", ct: cancel.Token);
    Check(before.Status == ExecutionState.CANCELLED_BEFORE_EXECUTION, "r2.cancel_before_launch");
}
using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
{
    var running = await Run("Start-Sleep -Seconds 30", ct: cancel.Token);
    Check(running.Status == ExecutionState.CANCELLED_UNKNOWN_EFFECT, "r2.cancel_running");
}
// No remote services or arbitrary processes: only a known inert local child.
var tree = await Run("$p=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\\ping.exe') -ArgumentList '-n','60','127.0.0.1' -PassThru; [Console]::Out.WriteLine($p.Id); Start-Sleep -Seconds 30", 4);
Check(tree.Status == ExecutionState.TIMEOUT_UNKNOWN_EFFECT && int.TryParse(tree.Stdout.Trim(), out _), "r2.child_pid_observed");
int pid = int.Parse(tree.Stdout.Trim(), System.Globalization.CultureInfo.InvariantCulture);
bool gone;
try { using var child = Process.GetProcessById(pid); gone = child.WaitForExit(5000); }
catch (ArgumentException) { gone = true; }
Check(gone, "r2.job_terminated_child");
Console.WriteLine("R2_POWERSHELL_RUNNER=PASS (actual candidate runner, direct CI; not exposed through MCP yet)");

using(var bitmap=new System.Drawing.Bitmap(2400,1800))
{
    using(var graphics=System.Drawing.Graphics.FromImage(bitmap))graphics.Clear(System.Drawing.Color.SteelBlue);
    var png=DesktopCapture.Encode(bitmap,1280);
    Check(png is not null && png.Width==1280 && png.Height==960 &&
        png.Png.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),"r5.synthetic_png_dimensions_and_signature");
}
Check(!DesktopCapture.ValidArguments(0,0,0,10,null,1280),"r5.invalid_region_pair");

Check(TunnelLifetime.UnexpectedExitCode(0)!=0 && TunnelLifetime.UnexpectedExitCode(17)==17,
    "h5.unexpected_vendor_exit_zero_is_restartable");
var fixtureCredential=new TunnelHost.Credential(1,"tunnel_"+new string('a',32),"inert-test-key");
Check(TunnelHost.Valid(fixtureCredential)&&!TunnelHost.Valid(fixtureCredential with {Version=2})&&
    !TunnelHost.Valid(fixtureCredential with {RuntimeKey="bad\nkey"}),"r6.typed_tunnel_credential");
var launch=TunnelHost.StartInfo(@"C:\Program Files\WindowsAdminMCP\client\tunnel-client-runtime.exe",
    @"C:\Program Files\WindowsAdminMCP\native\WindowsAdminMCP.exe",fixtureCredential);
Check(launch.FileName.EndsWith(@"\client\tunnel-client-runtime.exe",StringComparison.Ordinal)&&
    launch.ArgumentList.Contains("command=\"C:/Program Files/WindowsAdminMCP/native/WindowsAdminMCP.exe\" stdio,channel=main")&&
    launch.ArgumentList.Contains("env:CONTROL_PLANE_API_KEY")&&
    !launch.ArgumentList.Any(a=>a.Contains("inert-test-key",StringComparison.Ordinal))&&
    launch.Environment["CONTROL_PLANE_API_KEY"]=="inert-test-key","r6.stable_vendor_binding_no_key_in_argv");

var up=System.Net.NetworkInformation.OperationalStatus.Up;
var down=System.Net.NetworkInformation.OperationalStatus.Down;
Check(TunDependency.Classify([up],1)==TunReadiness.READY,"r6.tun_ready");
Check(TunDependency.Classify([],1)==TunReadiness.ADAPTER_MISSING,"r6.tun_missing");
Check(TunDependency.Classify([down],1)==TunReadiness.ADAPTER_DOWN,"r6.tun_down");
Check(TunDependency.Classify([up],0)==TunReadiness.DATAPLANE_NOT_RUNNING,"r6.tun_stale_adapter");
Check(TunDependency.Classify([up,up],1)==TunReadiness.ADAPTER_AMBIGUOUS,"r6.tun_duplicate_adapter");
Check(TunDependency.Classify([up],2)==TunReadiness.DATAPLANE_AMBIGUOUS,"r6.tun_duplicate_process");

using(var denied=System.Text.Json.JsonDocument.Parse("""{"schema_version":1,"component":"control-plane","details":{"http_status":403,"consecutive_failures":1}}"""))
    Check(TunnelLifetime.IsAccessDenied(denied.RootElement),"r6.authorization_denial_terminal");
using(var stale=System.Text.Json.JsonDocument.Parse("""{"schema_version":1,"component":"control-plane","details":{"http_status":403,"consecutive_failures":0}}"""))
    Check(!TunnelLifetime.IsAccessDenied(stale.RootElement),"r6.no_stale_denial");
using(var other=System.Text.Json.JsonDocument.Parse("""{"schema_version":1,"component":"control-plane","details":{"http_status":503,"consecutive_failures":2}}"""))
    Check(!TunnelLifetime.IsAccessDenied(other.RootElement),"r6.transient_error_not_auth_denial");
ProcessStartInfo InertChild()
{
    var info=new ProcessStartInfo(PowerShellRunner.Executable){UseShellExecute=false,WorkingDirectory=Environment.SystemDirectory};
    foreach(string arg in new[]{"-NoLogo","-NoProfile","-NonInteractive","-Command","Start-Sleep -Seconds 60"})info.ArgumentList.Add(arg);
    return info;
}
using(var fixture=NativeChildProcess.Start(InertChild()))
{
    fixture.Input.Close();
    int checks=0;
    var end=await TunnelLifetime.WatchAsync(fixture.Process,()=>++checks==1?TunReadiness.READY:TunReadiness.ADAPTER_DOWN,
        ()=>Task.FromResult(false),TimeSpan.FromMilliseconds(50));
    Check(end==TunnelEnd.TUN_LOST && !fixture.Process.HasExited,"r6.tun_loss_detected_on_real_child");
    fixture.StopTree();
    Check(fixture.Process.WaitForExit(5000),"r6.owned_child_terminated");
}
using(var fixture=NativeChildProcess.Start(InertChild()))
{
    fixture.Input.Close();
    var end=await TunnelLifetime.WatchAsync(fixture.Process,()=>TunReadiness.READY,
        ()=>Task.FromResult(true),TimeSpan.FromMilliseconds(50));
    Check(end==TunnelEnd.ACCESS_DENIED,"r6.real_child_auth_stop");
    fixture.StopTree();
    Check(fixture.Process.WaitForExit(5000),"r6.auth_denied_child_terminated");
}

// Real child, local fault injection only, never touch the installed tunnel.
using(var fixture=NativeChildProcess.Start(InertChild()))
{
    fixture.Input.Close();
    var workerFailure=Task.FromException(new IOException("isolated USER worker fault"));
    int checks=0;
    // The pre-H3 watch path has no worker signal: it observes only vendor/TUN.
    var priorEnd=await TunnelLifetime.WatchAsync(fixture.Process,
        ()=>++checks==2?TunReadiness.ADAPTER_DOWN:TunReadiness.READY,
        ()=>Task.FromResult(false),TimeSpan.FromMilliseconds(10));
    Check(workerFailure.IsFaulted && priorEnd==TunnelEnd.TUN_LOST&&!fixture.Process.HasExited,
        "h1.old_watch_without_worker_signal_misses_fault");
    var candidateEnd=await TunnelLifetime.WatchAsync(fixture.Process,()=>TunReadiness.READY,
        ()=>Task.FromResult(false),TimeSpan.FromMilliseconds(10),workerFailure);
    Check(candidateEnd==TunnelEnd.USER_WORKER_STOPPED && !fixture.Process.HasExited,
        "h1.worker_fault_observed_while_vendor_alive");
    _=workerFailure.Exception;
    fixture.StopTree();
    Check(fixture.Process.WaitForExit(5000),"h1.worker_fault_owned_child_terminated");
}

var startUtc=new DateTimeOffset(2026,1,1,12,0,0,TimeSpan.Zero);
var priorBoot=new BootStamp(1000,startUtc,1008,startUtc.AddMilliseconds(1),0);
// A wall clock adjustment between boots must not defeat chronological event evidence.
var nextBoot=new BootStamp(2000,startUtc.AddHours(-1),2008,startUtc.AddHours(-1),0);
Check(BootEvidence.Classify(priorBoot,priorBoot,priorBoot)==BootEvidenceCode.SAME_BOOT,"r4.boot_same_kernel_session");
Check(BootEvidence.Classify(priorBoot,priorBoot,nextBoot)==BootEvidenceCode.NEW_FULL_BOOT_OBSERVED,"r4.boot_new_full_fixture");
Check(BootEvidence.Classify(priorBoot,null,nextBoot)==BootEvidenceCode.AMBIGUOUS,"r4.boot_log_cleared_or_rotated");
Check(BootEvidence.Classify(priorBoot,priorBoot with {TypeUtc=startUtc.AddSeconds(2)},nextBoot)==BootEvidenceCode.AMBIGUOUS,"r4.boot_record_not_same");
Check(BootEvidence.Classify(priorBoot,priorBoot,nextBoot with {BootType=1})==BootEvidenceCode.AMBIGUOUS,"r4.boot_hybrid_not_accepted");
Check(BootEvidence.Classify(priorBoot,priorBoot,nextBoot with {TypeUtc=startUtc})==BootEvidenceCode.AMBIGUOUS,"r4.boot_unpaired_records_rejected");
Check(BootEvidence.Classify(priorBoot,priorBoot,nextBoot with {StartRecordId=999,TypeRecordId=1001})==BootEvidenceCode.AMBIGUOUS,"r4.boot_reused_record_ids_rejected");
var actualBoot=BootEvidence.Observe();
if(actualBoot.Status==BootEvidenceCode.FULL_BOOT_OBSERVED && actualBoot.Stamp is {} stamp)
{
    Check(BootEvidence.After(stamp).Status==BootEvidenceCode.SAME_BOOT,"r4.boot_same_session_native_event_readback");
}
else Console.WriteLine($"NOT_TESTED r4.boot_native_event_readback status={actualBoot.Status}");
Console.WriteLine("R4_BOOT_EVIDENCE_PROBE_COMPLETE (no actual OS reboot; not wired to job transitions)");

// Exact v2 schema from merged main, actual SQLite + real DPAPI ciphertext.
// No service or production directory is used.
string migrationRoot=Path.Combine(Path.GetTempPath(),"WindowsAdminMCP-migration-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(migrationRoot);
try
{
    string database=Path.Combine(migrationRoot,"jobs.db");
    string id=Guid.NewGuid().ToString("N"), key="v2-completed-fixture";
    string command="Write-Output 'v2 preserved'", v2Cwd=Environment.SystemDirectory;
    string Hash(string text)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    // Same anonymous object/property order and defaults as schema v2.
    string payload=Hash(System.Text.Json.JsonSerializer.Serialize(new {steps=new[] {new {command,timeout_seconds=30,pause_after=false}},cwd=v2Cwd}));
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+database+";Pooling=False"))
    {
        db.Open();
        using var create=db.CreateCommand();
        create.CommandText="""
                CREATE TABLE IF NOT EXISTS jobs(
                  seq INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL UNIQUE, op_key TEXT NOT NULL UNIQUE,
                  payload_hash TEXT NOT NULL, command_hash TEXT NOT NULL, cwd BLOB NOT NULL,
                  state TEXT NOT NULL CHECK(state IN ('QUEUED','RUNNING','CANCEL_REQUESTED','WAITING_FOR_RESUME','COMPLETED','FAILED','CANCELLED','NEEDS_RECONCILIATION')),
                  created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL,
                  completed_steps INTEGER NOT NULL DEFAULT 0, step_count INTEGER NOT NULL CHECK(step_count BETWEEN 1 AND 8),
                  CHECK(completed_steps BETWEEN 0 AND step_count));
                CREATE TABLE IF NOT EXISTS job_steps(
                  job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE CASCADE,
                  step_index INTEGER NOT NULL CHECK(step_index BETWEEN 1 AND 8),
                  command BLOB, timeout_seconds INTEGER NOT NULL CHECK(timeout_seconds BETWEEN 1 AND 3600),
                  pause_after INTEGER NOT NULL CHECK(pause_after IN (0,1)), result BLOB,
                  PRIMARY KEY(job_id,step_index));
                PRAGMA user_version=2;
                """;
        create.ExecuteNonQuery();
        using var row=db.CreateCommand();
        row.CommandText="""
            INSERT INTO jobs(job_id,op_key,payload_hash,command_hash,cwd,state,created_utc,updated_utc,completed_steps,step_count)
            VALUES($id,$key,$payload,$hash,$cwd,'COMPLETED',$now,$now,1,1);
            INSERT INTO job_steps(job_id,step_index,command,timeout_seconds,pause_after,result)
            VALUES($id,1,NULL,30,0,$result);
            """;
        row.Parameters.AddWithValue("$id",id);
        row.Parameters.AddWithValue("$key",key);
        row.Parameters.AddWithValue("$payload",payload);
        row.Parameters.AddWithValue("$hash",Hash(command));
        row.Parameters.AddWithValue("$cwd",JobDataProtection.Protect(v2Cwd,id,"cwd"));
        row.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        row.Parameters.AddWithValue("$result",JobDataProtection.Protect(System.Text.Json.JsonSerializer.Serialize(
            new ExecutionResult(ExecutionState.COMPLETED,Hash(command),12,0,"v2 preserved"),PipeWire.Json),id,"step/1/result"));
        row.ExecuteNonQuery();
    }
    using(var store=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
    {
        store.Initialize(database);
        var saved=store.Dispatch(new(AdminOperation.Job,JobAction.logs,JobId:id),"migration");
        Check(saved.Status==StatusCode.OK && saved.Job?.State==JobState.COMPLETED &&
            saved.Job.Execution is {Stdout:"v2 preserved",AuditPersisted:true},"r4.real_v2_migration_preserves_encrypted_result_and_audit");
        var duplicate=store.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:key,Command:command,WorkingDirectory:v2Cwd),"migration");
        Check(duplicate.Status==StatusCode.OK && duplicate.Job?.JobId==id,"r4.v2_payload_hash_unchanged");
        var counts=store.Summary();
        Check(counts.SchemaVersion==4 && counts.Total==1 && counts.Completed==1,"r4.migration_store_counts");
    }
    Check(StoreSchema.ObserveFile(database) is {Status:StoreSchemaCode.OBSERVED,Version:4},"r6.offline_store_schema_v4");
    Check(StoreSchema.ObserveFile(Path.Combine(migrationRoot,"missing.db")).Status==StoreSchemaCode.ABSENT,"r6.offline_store_absent");
    var orphan=Path.Combine(migrationRoot,"orphan.db");
    File.WriteAllText(orphan+"-wal","fixture");
    Check(StoreSchema.ObserveFile(orphan).Status==StoreSchemaCode.READBACK_FAILED,"r6.offline_store_orphan_wal_rejected");
    var backups=Directory.GetFiles(migrationRoot,"jobs.db.schema2-*.backup");
    Check(backups.Length==1,"r4.v2_backup_retained");
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+backups[0]+";Mode=ReadOnly;Pooling=False"))
    {
        db.Open();using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version";
        Check((long)cmd.ExecuteScalar()! ==2,"r4.v2_backup_schema");
    }
    using(var store=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
    {
        store.Initialize(database);
        Check(store.Summary().Completed==1 && Directory.GetFiles(migrationRoot,"jobs.db.schema2-*.backup").Length==1,
            "r4.v4_reopen_no_duplicate_migration");
    }
}
finally{Directory.Delete(migrationRoot,true);}

// Isolated real store: keyset pagination is stable, filtered and rejects invalid ranges.
string pagingRoot=Path.Combine(Path.GetTempPath(),"WindowsAdminMCP-paging-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(pagingRoot);
try
{
    using var store=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance);
    store.Initialize(Path.Combine(pagingRoot,"jobs.db"));
    JobReply Call(AdminRequest req)=>store.Dispatch(req,"paging");
    for(int i=0;i<5;i++)
        Check(Call(new(AdminOperation.Job,JobAction.submit,Key:"paging-"+i,Command:"Write-Output 1")).Status==StatusCode.OK,"paging.admission."+i);
    Check(Call(new(AdminOperation.Job,JobAction.cancel,Key:"paging-3")).Status==StatusCode.OK,"paging.cancel");
    var p1=Call(new(AdminOperation.Job,JobAction.list,StateFilter:JobState.QUEUED,PageSize:2));
    Check(p1.Status==StatusCode.OK && p1.Jobs is {Length:2} && p1.Jobs[0].OperationKey=="paging-4" &&
        p1.Jobs[1].OperationKey=="paging-2" && p1.NextBeforeSeq is >0,"paging.first");
    var p2=Call(new(AdminOperation.Job,JobAction.list,StateFilter:JobState.QUEUED,PageSize:2,BeforeSeq:p1.NextBeforeSeq));
    Check(p2.Status==StatusCode.OK && p2.Jobs is {Length:2} && p2.Jobs[0].OperationKey=="paging-1" &&
        p2.Jobs[1].OperationKey=="paging-0" && p2.NextBeforeSeq is null,"paging.second");
    Check(Call(new(AdminOperation.Job,JobAction.list,PageSize:0)).Status==StatusCode.INVALID_ARGUMENT &&
        Call(new(AdminOperation.Job,JobAction.status,Key:"paging-0",BeforeSeq:1)).Status==StatusCode.INVALID_ARGUMENT,"paging.invalid");
    var lookup=Call(new(AdminOperation.Job,JobAction.status,Key:"paging-3"));
    Check(lookup.Job?.State==JobState.CANCELLED,"paging.key_lookup");
}
finally{Directory.Delete(pagingRoot,true);}

// Native updater quiesces admission atomically, cancels queued work and
// requests active-worker cancellation without discarding unknown-effect audit.
string legacyJobWire=System.Text.Json.JsonSerializer.Serialize(
    new AdminRequest(AdminOperation.Job,JobAction.list,StateFilter:JobState.RUNNING),PipeWire.Json);
Check(!legacyJobWire.Contains("\"maintenance\"",StringComparison.Ordinal) &&
    System.Text.Json.JsonSerializer.Serialize(new AdminRequest(AdminOperation.Maintenance,Maintenance:MaintenanceAction.Drain),PipeWire.Json)
        .Contains("\"maintenance\":\"Drain\"",StringComparison.Ordinal),
    "maintenance.legacy_job_wire_compatible");
string drainRoot=Path.Combine(Path.GetTempPath(),"WindowsAdminMCP-drain-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(drainRoot);
try
{
    using var coordinator=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance);
    coordinator.Initialize(Path.Combine(drainRoot,"jobs.db"));
    var owner=new UserSession("S-1-5-21-9-8-7-1001",1,"S-1-5-5-9-8");
    coordinator.DispatchWorker(new(WorkerAction.poll),owner);
    var one=coordinator.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:"drain-active",Command:"Start-Sleep -Seconds 20",
        ExecutionContext:CommandContext.USER),"fixture",owner.Sid,owner);
    var lease=coordinator.DispatchWorker(new(WorkerAction.poll),owner).Work;
    Check(one.Status==StatusCode.OK && lease is not null,"maintenance.owned_worker_running");
    if(lease is null)throw new InvalidOperationException("Missing maintenance fixture lease");
    var queued=coordinator.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:"drain-queued",Command:"Write-Output never"),"fixture");
    Check(queued.Status==StatusCode.OK,"maintenance.queued_fixture");
    var drained=coordinator.Maintain(MaintenanceAction.Drain);
    Check(drained is {Status:StatusCode.OK,Draining:true,CancelledQueued:1,CancelRequested:1} &&
        coordinator.Summary() is {Queued:0,Running:0,CancelRequested:1},"maintenance.atomic_cancel_and_gate");
    Check(coordinator.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:"late",Command:"Write-Output late"),"fixture").Status==StatusCode.BUSY &&
        coordinator.DispatchWorker(new(WorkerAction.poll),owner).Work is null &&
        coordinator.DispatchWorker(new(WorkerAction.heartbeat,lease.LeaseId),owner).CancelRequested,
        "maintenance.no_new_admission_or_take");
    string hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(lease.Command))).ToLowerInvariant();
    Check(coordinator.DispatchWorker(new(WorkerAction.complete,lease.LeaseId,
        new(ExecutionState.CANCELLED_UNKNOWN_EFFECT,hash,100)),owner).Status==StatusCode.OK &&
        coordinator.Summary() is {Queued:0,Running:0,CancelRequested:0,NeedsReconciliation:1},
        "maintenance.unknown_effect_retained");
    Check(coordinator.Maintain(MaintenanceAction.Resume) is {Status:StatusCode.OK,Draining:false} &&
        coordinator.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:"post-drain",Command:"Write-Output ok"),"fixture").Status==StatusCode.OK,
        "maintenance.release_restores_admission");
}
finally{Directory.Delete(drainRoot,true);}


// Schema 3 fixture: migrate encrypted legacy results and retain permanent job keys.
string retentionRoot=Path.Combine(Path.GetTempPath(),"WindowsAdminMCP-retention-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(retentionRoot);
try
{
    string path=Path.Combine(retentionRoot,"jobs.db"), cmdText="Write-Output 'old-audit'";
    string Digest(string value)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    string payload=Digest(System.Text.Json.JsonSerializer.Serialize(new {steps=new[]{new JobStep(cmdText)},cwd=Environment.SystemDirectory}));
    string old=DateTimeOffset.UtcNow.AddDays(-45).ToString("O");
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+path+";Pooling=False"))
    {
        db.Open();
        using var schema=db.CreateCommand();
        schema.CommandText="""
            CREATE TABLE jobs(seq INTEGER PRIMARY KEY AUTOINCREMENT,job_id TEXT NOT NULL UNIQUE,op_key TEXT NOT NULL UNIQUE,
              payload_hash TEXT NOT NULL,command_hash TEXT NOT NULL,cwd BLOB NOT NULL,state TEXT NOT NULL,
              created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL,completed_steps INTEGER NOT NULL,step_count INTEGER NOT NULL,
              reboot_stamp TEXT,reboot_evidence TEXT);
            CREATE TABLE job_steps(job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE CASCADE,step_index INTEGER NOT NULL,
              command BLOB,timeout_seconds INTEGER NOT NULL,pause_after INTEGER NOT NULL,result BLOB,reboot_after INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(job_id,step_index));
            PRAGMA user_version=3;
            """;
        schema.ExecuteNonQuery();
        foreach(var (id,state,completed) in new[]{("old-finished","COMPLETED",1),("old-unknown","NEEDS_RECONCILIATION",0)})
        {
            using var seed=db.CreateCommand();
            seed.CommandText="""
                INSERT INTO jobs(job_id,op_key,payload_hash,command_hash,cwd,state,created_utc,updated_utc,completed_steps,step_count)
                VALUES($id,$id,$payload,$hash,$cwd,$state,$old,$old,$completed,1);
                INSERT INTO job_steps(job_id,step_index,command,timeout_seconds,pause_after,result)
                VALUES($id,1,NULL,30,0,$result);
                """;
            seed.Parameters.AddWithValue("$id",id);
            seed.Parameters.AddWithValue("$payload",payload);
            seed.Parameters.AddWithValue("$hash",Digest(cmdText));
            seed.Parameters.AddWithValue("$cwd",JobDataProtection.Protect(Environment.SystemDirectory,id,"cwd"));
            seed.Parameters.AddWithValue("$state",state);
            seed.Parameters.AddWithValue("$old",old);
            seed.Parameters.AddWithValue("$completed",completed);
            var execution=new ExecutionResult(completed==1?ExecutionState.COMPLETED:ExecutionState.TIMEOUT_UNKNOWN_EFFECT,Digest(cmdText),345,completed==1?0:null,"old-audit");
            seed.Parameters.AddWithValue("$result",JobDataProtection.Protect(System.Text.Json.JsonSerializer.Serialize(execution,PipeWire.Json),id,"step/1/result"));
            seed.ExecuteNonQuery();
        }
    }
    using(var store=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
    {
        store.Initialize(path);
        var original=store.Dispatch(new(AdminOperation.Job,JobAction.logs,JobId:"old-unknown"),"fixture");
        Check(original.Job is {State:JobState.NEEDS_RECONCILIATION,InterruptedSourceAvailable:false,Execution.Status:ExecutionState.TIMEOUT_UNKNOWN_EFFECT},"reconcile.v3_unknown_preserved");
        var prune=store.Dispatch(new(AdminOperation.Job,JobAction.prune),"fixture");
        Check(prune.Status==StatusCode.OK && prune.PrunedCount==1 && prune.Store is {Detailed:1,Pruned:1,NeedsReconciliation:1},"history.compacts_only_eligible");
        var logs=store.Dispatch(new(AdminOperation.Job,JobAction.logs,Key:"old-finished"),"fixture");
        Check(logs.Status==StatusCode.HISTORY_PRUNED && logs.Job is {HistoryPrunedUtc:not null,Execution:{Status:ExecutionState.COMPLETED,Stdout:"",ElapsedMs:345,AuditPersisted:true,PrunedOutput:{StdoutBytes:9}}},"history.retains_audit_metadata");
        var retry=store.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:"old-finished",Command:cmdText),"fixture");
        Check(retry.Status==StatusCode.OK && retry.Job is {JobId:"old-finished",HistoryPrunedUtc:not null},"history.permanent_idempotency");
        Check(store.Dispatch(new(AdminOperation.Job,JobAction.submit,Key:"old-finished",Command:"different"),"fixture").Status==StatusCode.CONFLICT,"history.pruned_key_conflict");
        var resolve=store.Dispatch(new(AdminOperation.Job,JobAction.reconcile,JobId:"old-unknown",Decision:ReconciliationDecision.UNKNOWN,Evidence:"No source retained in v3."),"fixture","S-1-5-18");
        Check(resolve.Job is {State:JobState.RECONCILED,Reconciliation.Decision:ReconciliationDecision.UNKNOWN},"reconcile.legacy_unknown_closed_without_success");
        var noPrune=store.Dispatch(new(AdminOperation.Job,JobAction.prune),"fixture");
        Check(noPrune.PrunedCount==0 && noPrune.Store is {Reconciled:1,NeedsReconciliation:0},"reconcile.fresh_evidence_not_pruned");
    }
    Check(Directory.GetFiles(retentionRoot,"jobs.db.schema3-*.backup").Length==1 &&
        StoreSchema.ObserveFile(path) is {Status:StoreSchemaCode.OBSERVED,Version:4},"migration.v3_backup_and_v4_schema");
    using(var store=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance))
    {
        store.Initialize(path);
        var resolved=store.Dispatch(new(AdminOperation.Job,JobAction.logs,JobId:"old-unknown"),"fixture");
        Check(resolved.Job is {State:JobState.RECONCILED,Reconciliation.Decision:ReconciliationDecision.UNKNOWN,Execution.Stdout:"old-audit"} &&
            Directory.GetFiles(retentionRoot,"jobs.db.schema3-*.backup").Length==1,"reconcile.v4_reopen_preserves_evidence");
    }
}
finally{Directory.Delete(retentionRoot,true);}


// A genuine SQLite/DPAPI fixture exercises session routing without impersonating users.
string routingRoot=Path.Combine(Path.GetTempPath(),"WindowsAdminMCP-routing-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(routingRoot);
try
{
    using var routing=new JobCoordinator(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobCoordinator>.Instance);
    routing.Initialize(Path.Combine(routingRoot,"jobs.db"));
    var owner=new UserSession("S-1-5-21-1-2-3-1001",1,"S-1-5-5-1-2");
    var other=new UserSession("S-1-5-21-1-2-3-1002",2,"S-1-5-5-1-3");
    var laterLogin=owner with {LogonSid="S-1-5-5-1-4"};
    var req=new AdminRequest(AdminOperation.Job,JobAction.submit,Key:"user-job",Command:"Write-Output 1",ExecutionContext:CommandContext.USER);
    Check(routing.Dispatch(req,"fixture",owner.Sid,owner).Status==StatusCode.USER_CONTEXT_UNAVAILABLE,"user.no_system_fallback");
    Check(routing.DispatchWorker(new(WorkerAction.poll),owner).Status==StatusCode.OK,"user.worker_presence");
    var admission=routing.Dispatch(req,"fixture",owner.Sid,owner);
    Check(admission.Job is {ExecutionContext:CommandContext.USER,ExecutionSessionId:1},"user.bound_admission");
    Check(routing.DispatchWorker(new(WorkerAction.poll),other).Work is null &&
        routing.DispatchWorker(new(WorkerAction.poll),laterLogin).Work is null,"user.no_other_session_claim");
    var lease=routing.DispatchWorker(new(WorkerAction.poll),owner).Work;
    Check(lease is not null && lease.JobId==admission.Job?.JobId,"user.owner_claim");
    if(lease is null)throw new InvalidOperationException("Missing fixture lease");
    var secondAdmission=routing.Dispatch(req with {Key="user-job-two",Command="Write-Output 2"},"fixture",owner.Sid,owner);
    var secondLease=routing.DispatchWorker(new(WorkerAction.poll),owner).Work;
    Check(secondLease is not null && secondLease.JobId==secondAdmission.Job?.JobId && secondLease.LeaseId!=lease.LeaseId,
        "user.two_independent_leases");
    if(secondLease is null)throw new InvalidOperationException("Missing second fixture lease.");
    Check(routing.Dispatch(new(AdminOperation.Job,JobAction.cancel,JobId:secondLease.JobId),"fixture",owner.Sid,owner).Job?.State==JobState.CANCEL_REQUESTED,
        "user.cancel_individual_lease");
    Check(routing.DispatchWorker(new(WorkerAction.heartbeat,secondLease.LeaseId),owner).CancelRequested &&
        !routing.DispatchWorker(new(WorkerAction.heartbeat,lease.LeaseId),owner).CancelRequested,"user.cancel_not_cross_job");
    string secondHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secondLease.Command))).ToLowerInvariant();
    Check(routing.DispatchWorker(new(WorkerAction.complete,secondLease.LeaseId,new(ExecutionState.CANCELLED_UNKNOWN_EFFECT,secondHash,100)),owner).Status==StatusCode.OK,
        "user.second_lease_independent_completion");
    Check(routing.DispatchWorker(new(WorkerAction.heartbeat,lease.LeaseId),other).Status==StatusCode.LEASE_LOST,"user.no_other_session_heartbeat");
    string hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(lease.Command))).ToLowerInvariant();
    var result=new ExecutionResult(ExecutionState.COMPLETED,hash,100,0,"user fixture");
    Check(routing.DispatchWorker(new(WorkerAction.complete,lease.LeaseId,result),other).Status==StatusCode.LEASE_LOST,"user.no_other_session_completion");
    Check(routing.DispatchWorker(new(WorkerAction.complete,lease.LeaseId,result),owner).Status==StatusCode.OK,"user.durable_completion");
    var readback=routing.Dispatch(new(AdminOperation.Job,JobAction.logs,Key:"user-job"),"fixture",owner.Sid,owner);
    Check(readback.Job is {State:JobState.COMPLETED,Execution:{ExecutionContext:CommandContext.USER,AuditPersisted:true,ExecutionSessionId:1}},"user.persisted_context");
    Check(routing.Dispatch(req,"fixture",owner.Sid,laterLogin).Status==StatusCode.CONFLICT,"user.no_replay_new_logon");
    // Real PowerShell result through the real coordinator and real SQLite.
    // The fixture intentionally does not impersonate another Windows logon.
    foreach(string errorScript in new[]{
        "[Console]::Error.Write(('e'*15999))",
        "[Console]::Error.Write(('e'*16000))",
        "[Console]::Out.Write(('o'*30000)); [Console]::Error.Write(('e'*30000))"})
    {
        string key="bounded-output-"+Guid.NewGuid().ToString("N");
        routing.DispatchWorker(new(WorkerAction.poll),owner);
        var accepted=routing.Dispatch(req with {Key=key,Command=errorScript},"fixture",owner.Sid,owner);
        var claim=routing.DispatchWorker(new(WorkerAction.poll),owner).Work;
        Check(accepted.Status==StatusCode.OK && claim is not null && claim.JobId==accepted.Job?.JobId,
            "h1.real_output_claim");
        if(claim is null)throw new InvalidOperationException("Missing bounded output lease");
        var actual=await Run(claim.Command);
        Check(actual.Status==ExecutionState.COMPLETED && actual.Stderr.Length<=PowerShellRunner.OutputLimit,
            "h1.real_runner_contract");
        Check(routing.DispatchWorker(new(WorkerAction.complete,claim.LeaseId,actual),owner).Status==StatusCode.OK,
            "h1.real_completion_ack");
        var saved=routing.Dispatch(new(AdminOperation.Job,JobAction.logs,Key:key),"fixture",owner.Sid,owner);
        Check(saved.Job is {State:JobState.COMPLETED,Execution:{AuditPersisted:true}} &&
            saved.Job.Execution.Stderr==actual.Stderr && saved.Job.Execution.Stdout==actual.Stdout &&
            saved.Job.Execution.OutputTruncated==actual.OutputTruncated,"h1.real_output_persisted");
    }
}
finally{Directory.Delete(routingRoot,true);}


// Receipt matching is separate from live remote acceptance.
var observed=DateTimeOffset.UtcNow;
var serviceFixture=new AdminStatus(StatusCode.OK,Guid.NewGuid().ToString("N"),123,StartedUtc:observed.AddMinutes(-5));
var receiptFixture=new AcceptanceReceipt(1,new string('a',40),"fixture",observed,123,serviceFixture.StartedUtc!.Value,
    456,observed.AddMinutes(-4),Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),
    Guid.NewGuid().ToString("N"),1,16,16,148,"image/png");
Check(AcceptanceEvidence.Validate(receiptFixture,receiptFixture.Commit,serviceFixture,456,receiptFixture.TunnelStartedUtc,1,observed)==EvidenceCode.MATCHED,
    "doctor.external_receipt_matches_generation");
Check(AcceptanceEvidence.Validate(receiptFixture with {TunnelPid=789},receiptFixture.Commit,serviceFixture,456,receiptFixture.TunnelStartedUtc,1,observed)==EvidenceCode.STALE_OR_DIFFERENT_INSTALLATION,
    "doctor.receipt_rejects_other_tunnel");
Check(AcceptanceEvidence.Validate(receiptFixture with {ScreenshotSessionId=2},receiptFixture.Commit,serviceFixture,456,receiptFixture.TunnelStartedUtc,1,observed)==EvidenceCode.INVALID,
    "doctor.receipt_rejects_other_desktop");


// Deterministic budget arithmetic: no fixed worker count, fail closed on memory pressure.
const ulong gib=1024UL*1024UL*1024UL;
Check(ExecutionBudget.Evaluate(5,32*gib,20*gib,20*gib).CanStart,"parallel.capacity_above_four");
Check(!ExecutionBudget.Evaluate(5,32*gib,1*gib,20*gib).CanStart,"parallel.physical_pressure");
Check(!ExecutionBudget.Evaluate(5,32*gib,20*gib,1*gib).CanStart,"parallel.commit_pressure");
Check(ExecutionBudget.Evaluate(40,128*gib,100*gib,100*gib).CanStart,"parallel.no_fixed_slot_count");
ulong admissionThreshold=(32*gib)/20+ExecutionBudget.Reservation;
Check(ExecutionBudget.Evaluate(0,32*gib,2*gib,20*gib) is {CanStart:true,ReserveBytes:var loweredReserve} &&
    loweredReserve==32*gib/20,"budget.lowered_five_percent_large_machine");
Check(ExecutionBudget.Evaluate(0,8*gib,2*gib,20*gib).ReserveBytes==gib,
    "budget.preserve_one_gib_minimum");
Check(ExecutionBudget.Evaluate(0,32*gib,admissionThreshold,20*gib).CanStart &&
    !ExecutionBudget.Evaluate(0,32*gib,admissionThreshold-1,20*gib).CanStart,
    "budget.physical_exact_admission_boundary");
Check(!ExecutionBudget.Evaluate(0,32*gib,20*gib,admissionThreshold-1).CanStart,
    "budget.commit_exact_admission_boundary");


// USER fault injection through the production execution loop (no live service or real commands).
Check(UserWorker.RemainingAuthorityMs(1000,13000)==0 &&
    UserWorker.RemainingAuthorityMs(1000,12000)==1000,"user.authority_from_request_dispatch");
Check(UserWorker.IsTransient(StatusCode.IPC_TIMEOUT) &&
    !UserWorker.IsTransient(StatusCode.LEASE_LOST) &&
    !UserWorker.IsTransient(StatusCode.ACCESS_DENIED),"user.authoritative_denial_not_retryable");
var workerFixture=new UserWork("lease","job",1,"fixture",30,Environment.SystemDirectory);
var finished=new TaskCompletionSource<ExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
int pulseCount=0,launches=0;
ExecutionResult? delivered=null;
WorkerObservation? workerObservation=null;
Task<ExecutionResult> FakeRunner(string cmd,int timeout,string? cwd,CancellationToken token)
{
    launches++;
    token.Register(()=>finished.TrySetResult(new(ExecutionState.CANCELLED_UNKNOWN_EFFECT,"fixture-hash",1)));
    return finished.Task;
}
Task<AdminResponse> TransientThenSuccess(WorkerRequest request,CancellationToken token)
{
    if(request.Action==WorkerAction.complete)
    {
        delivered=request.Result;
        return Task.FromResult(new AdminResponse(StatusCode.OK,"complete",Worker:new(StatusCode.OK)));
    }
    pulseCount++;
    if(pulseCount==1)return Task.FromResult(new AdminResponse(StatusCode.IPC_TIMEOUT,"injected-timeout"));
    finished.TrySetResult(new(ExecutionState.COMPLETED,"fixture-hash",10,0));
    return Task.FromResult(new AdminResponse(StatusCode.OK,"pulse",Worker:new(StatusCode.OK)));
}
await UserWorker.ExecuteAsync(workerFixture,Environment.TickCount64,TransientThenSuccess,FakeRunner,
    CancellationToken.None,TimeSpan.FromMilliseconds(1),observe:value=>workerObservation=value).WaitAsync(TimeSpan.FromSeconds(5));
Check(launches==1 && pulseCount==2 &&
    delivered is {Status:ExecutionState.COMPLETED,Stage:null} &&
    workerObservation is {Signal:WorkerStopSignal.NONE,TransientFailures:1,LastFailure:StatusCode.IPC_TIMEOUT,Correlation:"injected-timeout"},
    "user.transient_ipc_does_not_cancel_or_replay");

finished=new(TaskCreationOptions.RunContinuationsAsynchronously);
delivered=null;
launches=0;
Task<AdminResponse> LeaseLost(WorkerRequest request,CancellationToken token)
{
    if(request.Action==WorkerAction.complete)delivered=request.Result;
    return Task.FromResult(new AdminResponse(StatusCode.LEASE_LOST,"lease-lost",Worker:new(StatusCode.LEASE_LOST)));
}
await UserWorker.ExecuteAsync(workerFixture,Environment.TickCount64,LeaseLost,FakeRunner,
    CancellationToken.None,TimeSpan.FromMilliseconds(1)).WaitAsync(TimeSpan.FromSeconds(5));
Check(launches==1 && delivered is {Status:ExecutionState.CANCELLED_UNKNOWN_EFFECT,
    Stage:"USER_LEASE_LOST"},"user.authoritative_lease_loss_stops");

finished=new(TaskCreationOptions.RunContinuationsAsynchronously);
delivered=null;
launches=0;
Task<AdminResponse> ExplicitCancel(WorkerRequest request,CancellationToken token)
{
    if(request.Action==WorkerAction.complete)delivered=request.Result;
    return Task.FromResult(new AdminResponse(StatusCode.OK,"cancel",Worker:new(StatusCode.OK,CancelRequested:true)));
}
await UserWorker.ExecuteAsync(workerFixture,Environment.TickCount64,ExplicitCancel,FakeRunner,
    CancellationToken.None,TimeSpan.FromMilliseconds(1)).WaitAsync(TimeSpan.FromSeconds(5));
Check(launches==1 && delivered is {Status:ExecutionState.CANCELLED_UNKNOWN_EFFECT,
    Stage:"USER_OPERATOR_CANCELLED"},"user.explicit_cancel_distinguished");

using(var siblingLifetime=new CancellationTokenSource())
{
    var sibling=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var siblingTask=UserWorker.RunIsolatedAsync(()=>sibling.Task,_=>siblingLifetime.Cancel());
    Exception? observedFault=null;
    await UserWorker.RunIsolatedAsync(()=>Task.FromException(new IOException("injected")),e=>observedFault=e);
    Check(observedFault is IOException && !siblingTask.IsCompleted && !siblingLifetime.IsCancellationRequested,
        "user.faulted_job_does_not_cancel_sibling");
    sibling.SetResult();
    await siblingTask;
}

// Completion delivery: exact durable readback after lost ACK, bounded identical
// retry when no commit is observed, and no false success for a different result.
var receiptPayload=new ExecutionResult(ExecutionState.COMPLETED,"receipt-hash",33,0,"one");
var persistedReceipt=receiptPayload with {AuditPersisted=true,ExecutionContext=CommandContext.USER,
    ExecutionSid="fixture",ExecutionSessionId=1,QueueWaitMs=2,StepStartedUtc="start",StepFinishedUtc="finish"};
int completionCalls=0,readbackCalls=0;
Task<AdminResponse> LostAcknowledgement(WorkerRequest request,CancellationToken token)
{
    completionCalls++;
    Check(request.Action==WorkerAction.complete && request.Result==receiptPayload,"user.completion_retry_same_payload");
    return Task.FromResult(new AdminResponse(StatusCode.IPC_TIMEOUT,"lost-ack"));
}
Task<ExecutionResult?> CommittedReadback(UserWork work,CancellationToken token)
{
    readbackCalls++;
    return Task.FromResult<ExecutionResult?>(persistedReceipt);
}
var unconfirmed=await UserWorker.ConfirmCompletionAsync(workerFixture,receiptPayload,LostAcknowledgement,
    CommittedReadback,CancellationToken.None);
Check(unconfirmed is null && completionCalls==1 && readbackCalls==1,"user.lost_ack_exact_durable_readback");
completionCalls=0;
readbackCalls=0;
Task<ExecutionResult?> DifferentReadback(UserWork work,CancellationToken token)
{
    readbackCalls++;
    return Task.FromResult<ExecutionResult?>(persistedReceipt with {Stdout="different"});
}
unconfirmed=await UserWorker.ConfirmCompletionAsync(workerFixture,receiptPayload,LostAcknowledgement,
    DifferentReadback,CancellationToken.None);
Check(unconfirmed?.Code==StatusCode.IPC_TIMEOUT && completionCalls==2 && readbackCalls==2,
    "user.completion_mismatch_never_confirmed");
Check(!UserWorker.CompletionMatches(receiptPayload,persistedReceipt with {AuditPersisted=false}),
    "user.completion_not_durable_never_confirmed");

// Frozen public/durable schema-4 field set from fed40dc0. The old strict JSON
// reader rejects unknown members. Keep new causes in the existing stage string.
var rollbackShape=System.Text.Json.JsonSerializer.Serialize(delivered,PipeWire.Json);
using(var rollbackJson=System.Text.Json.JsonDocument.Parse(rollbackShape))
{
    string[] legacyFields=["status","command_sha256","elapsed_ms","exit_code","stdout","stderr",
        "warnings","output_truncated","stage","native_error","job_id","operation_key","correlation_id",
        "audit_persisted","execution_context","execution_sid","execution_session_id","queue_wait_ms",
        "step_started_utc","step_finished_utc","timing","pruned_output"];
    Check(rollbackJson.RootElement.EnumerateObject().Select(p=>p.Name).ToHashSet(StringComparer.Ordinal)
        .SetEquals(legacyFields) && rollbackJson.RootElement.GetProperty("stage").GetString()=="USER_OPERATOR_CANCELLED",
        "h1.schema4_durable_result_exact_legacy_shape");
}

// Actual elapsed watchdog budget: a late successful heartbeat may not revive
// an execution after the last confirmed authority has expired.
var expiredClaim=new UserWork("lease-expiry","job-expiry",1,"fixture",60,Environment.SystemDirectory);
var stopped=new TaskCompletionSource<ExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
ExecutionResult? lateResult=null;
int lateHeartbeat=0;
Task<ExecutionResult> AuthorityRunner(string command,int timeout,string? cwd,CancellationToken cancel)
{
    cancel.Register(()=>stopped.TrySetResult(new(ExecutionState.CANCELLED_UNKNOWN_EFFECT,"fixture-hash",1,Stage:"PROCESS_INTERRUPTED")));
    return stopped.Task;
}
async Task<AdminResponse> LateHeartbeat(WorkerRequest request,CancellationToken cancel)
{
    if(request.Action==WorkerAction.complete)
    {
        lateResult=request.Result;
        return new(StatusCode.OK,"complete",Worker:new(StatusCode.OK));
    }
    lateHeartbeat++;
    await Task.Delay(3000,cancel);
    return new(StatusCode.OK,"late-success",Worker:new(StatusCode.OK));
}
await UserWorker.ExecuteAsync(expiredClaim,Environment.TickCount64-10000,LateHeartbeat,AuthorityRunner,
    CancellationToken.None,TimeSpan.FromMilliseconds(10)).WaitAsync(TimeSpan.FromSeconds(10));
Check(lateHeartbeat==1 && lateResult is {Status:ExecutionState.CANCELLED_UNKNOWN_EFFECT,Stage:"USER_WATCHDOG_EXPIRED"},
    "h1.late_heartbeat_cannot_revive_expired_authority");
