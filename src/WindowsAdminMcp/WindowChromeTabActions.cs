using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Automation;

// Guard and dispatch effectful tab operations.
internal static partial class WindowChromeCdp
{
    // AX roles describe accessibility, not the browser's ability to receive
    // pointer input. In particular a clickable <summary>, custom widget or
    // editable field may be exposed as "generic" or "textbox". Route every
    // pointer-like action through the same verified DOM hit test.
    internal static bool PointerAction(WindowActionKind action) => action is
        WindowActionKind.CLICK or WindowActionKind.DOUBLE_CLICK or WindowActionKind.RIGHT_CLICK or
        WindowActionKind.INVOKE or WindowActionKind.SELECT or
        WindowActionKind.TOGGLE or WindowActionKind.EXPAND or WindowActionKind.COLLAPSE;

    internal static bool CdpPointerAction(WindowActionKind action) =>
        PointerAction(action) || action is WindowActionKind.HOVER or WindowActionKind.SCROLL or
            WindowActionKind.DRAG;

    internal static (int DeltaX, int DeltaY) WheelDelta(WindowScrollDirection direction, int units) =>
        direction switch
        {
            WindowScrollDirection.UP => (0, -120 * units),
            WindowScrollDirection.DOWN => (0, 120 * units),
            WindowScrollDirection.LEFT => (-120 * units, 0),
            WindowScrollDirection.RIGHT => (120 * units, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };

    // One CDP pointer gesture, with all target/viewport/identity checks made
    // by the caller before this first effectful command. No desktop input.
    private static async Task DispatchPointer(Session cdp, string sessionId,
        WindowActionRequest request, double x, double y)
    {
        if (request.Action == WindowActionKind.HOVER)
        {
            await cdp.Call("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y }, sessionId);
        }
        else if (request.Action == WindowActionKind.SCROLL)
        {
            var (deltaX, deltaY) = WheelDelta(request.ScrollDirection!.Value, request.ScrollUnits);
            await cdp.Call("Input.dispatchMouseEvent", new { type = "mouseWheel", x, y,
                deltaX, deltaY }, sessionId);
        }
        else
        {
            string button = request.Action == WindowActionKind.RIGHT_CLICK ? "right" : "left";
            int repeats = request.Action == WindowActionKind.DOUBLE_CLICK ? 2 : 1;
            for (int click = 1; click <= repeats; click++)
            {
                await cdp.Call("Input.dispatchMouseEvent", new { type = "mousePressed", x, y,
                    button, clickCount = click }, sessionId);
                await cdp.Call("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y,
                    button, clickCount = click }, sessionId);
            }
        }
    }

    // Both endpoints are verified DOM identities in the same page/loader, not
    // guessed screen coordinates. CDP owns these events; Win32 input is unused.
    private static async Task DispatchDomDrag(Session cdp, string sessionId,
        double fromX, double fromY, double toX, double toY)
    {
        await cdp.Call("Input.dispatchMouseEvent", new
            { type = "mouseMoved", x = fromX, y = fromY }, sessionId);
        await cdp.Call("Input.dispatchMouseEvent", new
            { type = "mousePressed", x = fromX, y = fromY, button = "left", clickCount = 1 }, sessionId);
        for (int step = 1; step <= 12; step++)
        {
            double fraction = step / 12.0;
            await cdp.Call("Input.dispatchMouseEvent", new
            {
                type = "mouseMoved", x = fromX + (toX - fromX) * fraction,
                y = fromY + (toY - fromY) * fraction, button = "left", buttons = 1
            }, sessionId);
        }
        await cdp.Call("Input.dispatchMouseEvent", new
            { type = "mouseReleased", x = toX, y = toY, button = "left", clickCount = 1 }, sessionId);
    }

    private static async Task<bool> FileInputNode(Session cdp, string sessionId,
        int backendNodeId, int fileCount)
    {
        var resolved = await cdp.Call("DOM.resolveNode", new { backendNodeId }, sessionId);
        string objectId = resolved.GetProperty("object").GetProperty("objectId").GetString()!;
        var result = await cdp.Call("Runtime.callFunctionOn", new
        {
            objectId,
            functionDeclaration = "function(count){return this instanceof HTMLInputElement && this.type==='file' && !this.disabled && !this.closest('[inert]') && (count===1 || this.multiple);}",
            arguments = new[] { new { value = fileCount } },
            returnByValue = true, silent = true
        }, sessionId);
        return result.GetProperty("result").TryGetProperty("value", out var value) &&
            value.ValueKind == JsonValueKind.True;
    }

    // Do not return any file path or local file contents through the result.
    private static async Task<bool> FilesSelected(Session cdp, string sessionId, int backendNodeId,
        string[] paths, long[] lengths)
    {
        var resolved = await cdp.Call("DOM.resolveNode", new { backendNodeId }, sessionId);
        string objectId = resolved.GetProperty("object").GetProperty("objectId").GetString()!;
        var result = await cdp.Call("Runtime.callFunctionOn", new
        {
            objectId,
            functionDeclaration = "function(names,sizes){return this instanceof HTMLInputElement && this.type==='file' && this.files.length===names.length && names.every((n,i)=>this.files[i].name===n && this.files[i].size===sizes[i]);}",
            arguments = new object[]
            {
                new { value = paths.Select(p => Path.GetFileName(p)).ToArray() },
                new { value = lengths }
            },
            returnByValue = true, silent = true
        }, sessionId);
        return result.GetProperty("result").TryGetProperty("value", out var value) &&
            value.ValueKind == JsonValueKind.True;
    }

    internal static bool? AxActionState(JsonElement node, WindowActionKind action)
    {
        string? property = action switch
        {
            WindowActionKind.EXPAND or WindowActionKind.COLLAPSE => "expanded",
            WindowActionKind.SELECT => "selected",
            WindowActionKind.TOGGLE => "checked",
            _ => null
        };
        if (property is null || !node.TryGetProperty("properties", out var properties)) return null;
        foreach (var item in properties.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var name) ||
                name.GetString() != property &&
                !(action == WindowActionKind.TOGGLE && name.GetString() == "pressed") ||
                !item.TryGetProperty("value", out var value) ||
                !value.TryGetProperty("value", out var state)) continue;
            if (state.ValueKind == JsonValueKind.True) return true;
            if (state.ValueKind == JsonValueKind.False) return false;
            if (state.ValueKind == JsonValueKind.String &&
                bool.TryParse(state.GetString(), out bool parsed)) return parsed;
        }
        return null;
    }

    private static async Task<bool> EditableDomNode(Session cdp, string sessionId, int backendNodeId)
    {
        var resolved = await cdp.Call("DOM.resolveNode", new { backendNodeId }, sessionId);
        string objectId = resolved.GetProperty("object").GetProperty("objectId").GetString()!;
        var editable = await cdp.Call("Runtime.callFunctionOn", new
        {
            objectId,
            functionDeclaration = "function(){const e=this;return e instanceof Element && !e.closest('[inert]') && (e.isContentEditable || (e instanceof HTMLInputElement && !['button','checkbox','color','file','hidden','image','radio','range','reset','submit'].includes(e.type) && !e.readOnly && !e.disabled) || (e instanceof HTMLTextAreaElement && !e.readOnly && !e.disabled));}",
            returnByValue = true, silent = true
        }, sessionId);
        return editable.GetProperty("result").TryGetProperty("value", out var value) &&
            value.ValueKind == JsonValueKind.True;
    }

    private static async Task ReplaceFocusedText(Session cdp, string sessionId, string value)
    {
        // Chrome receives the same replacement gesture for foreground and
        // background tabs. Only the focused tab target receives these events.
        await cdp.Call("Input.dispatchKeyEvent", new { type = "keyDown", key = "Control",
            code = "ControlLeft", windowsVirtualKeyCode = 17 }, sessionId);
        await cdp.Call("Input.dispatchKeyEvent", new { type = "keyDown", key = "a",
            code = "KeyA", windowsVirtualKeyCode = 65, modifiers = 2 }, sessionId);
        await cdp.Call("Input.dispatchKeyEvent", new { type = "keyUp", key = "a",
            code = "KeyA", windowsVirtualKeyCode = 65, modifiers = 2 }, sessionId);
        await cdp.Call("Input.dispatchKeyEvent", new { type = "keyUp", key = "Control",
            code = "ControlLeft", windowsVirtualKeyCode = 17 }, sessionId);
        if (value.Length > 0) await cdp.Call("Input.insertText", new { text = value }, sessionId);
        else
        {
            await cdp.Call("Input.dispatchKeyEvent", new { type = "keyDown", key = "Backspace",
                code = "Backspace", windowsVirtualKeyCode = 8 }, sessionId);
            await cdp.Call("Input.dispatchKeyEvent", new { type = "keyUp", key = "Backspace",
                code = "Backspace", windowsVirtualKeyCode = 8 }, sessionId);
        }
    }

    private static async Task<bool> ReadTextValue(Session cdp, string sessionId, int backendNodeId,
        string expectedValue)
    {
        var resolved = await cdp.Call("DOM.resolveNode", new { backendNodeId }, sessionId);
        string objectId = resolved.GetProperty("object").GetProperty("objectId").GetString()!;
        var readback = await cdp.Call("Runtime.callFunctionOn", new
        {
            objectId,
            // Only a boolean leaves the page, including for password inputs.
            functionDeclaration = "function(expected){return ('value' in this ? this.value : this.textContent) === expected;}",
            arguments = new[] { new { value = expectedValue } },
            returnByValue = true, silent = true
        }, sessionId);
        return readback.GetProperty("result").TryGetProperty("value", out var value) &&
            value.ValueKind == JsonValueKind.True;
    }

    internal static WindowActionResult InvokeTab(IntPtr hwnd, WindowActionRequest request,
        Stopwatch clock)
    {
        if (!TryParseTabElementRef(request.ElementId, out var target) || target is null)
            return new(WindowActionCode.STALE_ELEMENT);
        if (!TryEndpoint(out var endpoint, out string endpointFailure))
            return new(WindowActionCode.CDP_UNAVAILABLE, FailureStage: WindowFailureStage.CDP_CONNECT,
                Detail: endpointFailure);
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var connection = ConnectionState(endpoint!, request: false, hwnd);
                if (connection == ChromeConnectionState.CONSENT_PENDING)
                    return new(WindowActionCode.CDP_CONSENT_PENDING,
                        FailureStage: WindowFailureStage.CDP_CONNECT, Detail: "ALLOW_CHROME_PROMPT");
                if (connection != ChromeConnectionState.CONNECTED)
                    return new(WindowActionCode.CDP_UNAVAILABLE,
                        FailureStage: WindowFailureStage.CDP_CONNECT, Detail: connection.ToString());
                var result = InvokeTabAsync(endpoint!, hwnd, request, target, clock)
                    .GetAwaiter().GetResult();
                // Only a proven pre-dispatch transport failure may be retried.
                // Never replay a click after its effect could be unknown.
                if (attempt == 0 && CanRetryCdpAction(result, TabConnected)) continue;
                return result;
            }
            return new(WindowActionCode.CDP_UNAVAILABLE, FailureStage: WindowFailureStage.CDP_CONNECT);
        }
        catch (Exception e) when (e is WebSocketException or IOException or JsonException or
            OperationCanceledException or InvalidOperationException or KeyNotFoundException)
        {
            ObserveCdpFailure(e);
            return new(WindowActionCode.CDP_UNAVAILABLE, FailureStage: WindowFailureStage.CDP_CONNECT,
                ExceptionType: e.GetType().Name,
                Detail: e is WebSocketException ws ? $"CDP_WEBSOCKET_{ws.WebSocketErrorCode}" : null,
                ElapsedMs: clock.ElapsedMilliseconds);
        }
    }

    internal static bool CanRetryCdpAction(WindowActionResult result, bool connected) =>
        result.Status == WindowActionCode.CDP_UNAVAILABLE && !result.EffectUnknown &&
        result.ExecutionMethod is null && !connected;

    // Keep the final readback close to the *first* effectful CDP command.
    // This cannot make a remote navigation atomic with dispatch, but it
    // rejects a tab move, URL change, new loader, or replaced native window
    // that occurs during earlier, potentially slow DOM/AX preparation.
    private static async Task<(WindowActionCode? Code, string? Detail)> DispatchCheckpoint(Session cdp,
        string sessionId, IntPtr hwnd, WindowActionRequest request, string targetId,
        string snapshotUrl, int browserWindowId, string loaderId,
        CancellationToken cdpDeadline)
    {
        using var self = Process.GetCurrentProcess();
        if (WindowCatalog.ResolveId(request.WindowId, self.SessionId, out var current) !=
            WindowResolveCode.OK || current != hwnd)
            return (WindowActionCode.STALE_WINDOW, "STALE_WINDOW");
        if (request.ExpectedProfile is null)
            return (WindowActionCode.CDP_CONTEXT_MISMATCH, "PROFILE_REQUIRED");
        if (!VerifiedChromeProfile(hwnd, request.ExpectedProfile, out var profileFailure))
            return (WindowActionCode.CDP_CONTEXT_MISMATCH, $"PROFILE_{profileFailure}");
        var info = await cdp.Call("Target.getTargetInfo", new { targetId });
        if (!MatchesDispatchTarget(info, targetId, snapshotUrl,
            request.ExpectedOrigin, request.ExpectedUrl))
            return (WindowActionCode.CDP_CONTEXT_MISMATCH, "TAB_TARGET_CHANGED");
        var owner = await cdp.Call("Browser.getWindowForTarget", new { targetId });
        if (!owner.TryGetProperty("windowId", out var windowId) ||
            windowId.ValueKind != JsonValueKind.Number ||
            !windowId.TryGetInt32(out int actualWindowId) ||
            actualWindowId != browserWindowId)
            return (WindowActionCode.CDP_CONTEXT_MISMATCH, "TAB_WINDOW_CHANGED");
        return await ReadFrameLoaderAsync(cdp, sessionId, cdpDeadline) == loaderId
            ? (null, null) : (WindowActionCode.STALE_ELEMENT, "TAB_LOADER_CHANGED");
    }

    private static async Task<WindowActionResult> InvokeTabAsync(Uri endpoint, IntPtr hwnd,
        WindowActionRequest request, TabElementRef target, Stopwatch clock)
    {
        bool dispatched = false;
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(request.ObservationTimeoutMs + (TabConnected ? 5500 : 24000)));
        try
        {
            var cdp = await TabSessionAsync(endpoint, timeout.Token);
            var tabs = request.BrowserWindowId is { } browserWindowId
                ? await BrowserTargets(cdp, browserWindowId, target.TargetId)
                : await TargetsForWindow(cdp, hwnd, request.WindowId, timeout.Token,
                    request.ExpectedOrigin, request.ExpectedUrl, target.TargetId,
                    request.ExpectedProfile is not null);
            var selected = tabs.SingleOrDefault(t => t.Id == target.TargetId);
            if (selected is null) return request.BrowserWindowId is not null
                ? Rejected(WindowActionCode.CDP_CONTEXT_MISMATCH, "BROWSER_TARGET_NOT_FOUND")
                : Rejected(WindowActionCode.CDP_TARGET_AMBIGUOUS);
            if (!MatchesContext(selected.Url, request.ExpectedOrigin, request.ExpectedUrl))
                return Rejected(WindowActionCode.CDP_CONTEXT_MISMATCH, "TAB_URL_MISMATCH");
            if (request.ExpectedProfile is not null &&
                !VerifiedChromeProfile(hwnd, request.ExpectedProfile, out var profileFailure))
                return Rejected(WindowActionCode.CDP_CONTEXT_MISMATCH, $"PROFILE_{profileFailure}");
            string sessionId = await Attach(cdp, selected.Id);
            string loader = await ReadFrameLoaderAsync(cdp, sessionId, timeout.Token);
            if (loader != target.LoaderId) return Rejected(WindowActionCode.STALE_ELEMENT);
            var ax = await cdp.Call("Accessibility.getFullAXTree", new { }, sessionId);
            var nodeMatches = ax.GetProperty("nodes").EnumerateArray().Where(node =>
                node.TryGetProperty("backendDOMNodeId", out var backend) &&
                backend.ValueKind == JsonValueKind.Number && backend.GetInt32() == target.BackendId &&
                !(node.TryGetProperty("ignored", out var ignored) && ignored.GetBoolean())).ToArray();
            if (nodeMatches.Length != 1) return Rejected(WindowActionCode.STALE_ELEMENT);
            // Protected fields remain redacted in inspection, but an explicit
            // action on their fresh element reference may still enter text.
            if (AxDisabled(nodeMatches[0])) return Rejected(WindowActionCode.DISABLED);
            string role = AxValue(nodeMatches[0], "role"), name = AxValue(nodeMatches[0], "name");
            if (!string.Equals(ElementDigest(request.WindowId, selected.Id, target.BackendId,
                loader, selected.Url, role, name), target.Digest, StringComparison.OrdinalIgnoreCase))
                return Rejected(WindowActionCode.STALE_ELEMENT);
            if (request.ExpectedName is not null)
            {
                bool before = request.ExpectedKind is WindowExpectedKind.UIA_ELEMENT or
                    WindowExpectedKind.UIA_VALUE or WindowExpectedKind.UIA_ENABLED
                    ? ExpectedAxState(ax, request)
                    : WindowAction.ExternalStatePresent(request.ExpectedKind, request.ExpectedName);
                if (before == (request.ExpectedChange == WindowExpectedChange.APPEARS) &&
                    (request.ExpectedEnabledName is null ||
                     HasAxEnabled(ax, request.ExpectedEnabledName, request.ExpectedEnabled)))
                    return Rejected(WindowActionCode.EXPECTED_STATE_ALREADY_TRUE);
            }
            bool? stateBefore = AxActionState(nodeMatches[0], request.Action);
            bool? desiredState = request.Action switch
            {
                WindowActionKind.EXPAND => true,
                WindowActionKind.COLLAPSE => false,
                WindowActionKind.SELECT => true,
                WindowActionKind.TOGGLE when stateBefore is not null => !stateBefore.Value,
                _ => null
            };
            if (desiredState is not null && stateBefore == desiredState)
                return Rejected(WindowActionCode.EXPECTED_STATE_ALREADY_TRUE);
            string provider = "CDP_BACKGROUND";
            int? selectedFileCount = null;
            if (CdpPointerAction(request.Action))
            {
                var box = await cdp.Call("DOM.getBoxModel", new { backendNodeId = target.BackendId }, sessionId);
                var metrics = await cdp.Call("Page.getLayoutMetrics", new { }, sessionId);
                if (!TryPoint(box.GetProperty("model").GetProperty("content"),
                    metrics.GetProperty("cssVisualViewport"), out double x, out double y))
                    return Rejected(WindowActionCode.CDP_TARGET_NOT_VISIBLE);
                var resolved = await cdp.Call("DOM.resolveNode", new { backendNodeId = target.BackendId }, sessionId);
                string objectId = resolved.GetProperty("object").GetProperty("objectId").GetString()!;
                var hit = await cdp.Call("Runtime.callFunctionOn", new
                {
                    objectId,
                    functionDeclaration = "function(x,y){const h=document.elementFromPoint(x,y);return !!h && (h===this || this.contains(h)) && !this.matches(':disabled') && !this.closest('[inert]');}",
                    arguments = new[] { new { value = x }, new { value = y } },
                    returnByValue = true, silent = true
                }, sessionId);
                if (!hit.GetProperty("result").TryGetProperty("value", out var ok) ||
                    ok.ValueKind != JsonValueKind.True) return Rejected(WindowActionCode.TARGET_OCCLUDED);
                double? dropX = null, dropY = null;
                if (request.Action == WindowActionKind.DRAG)
                {
                    if (!TryParseTabElementRef(request.TargetElementId, out var destination) ||
                        destination is null || destination.TargetId != target.TargetId ||
                        destination.LoaderId != target.LoaderId)
                        return Rejected(WindowActionCode.STALE_ELEMENT, "DRAG_DESTINATION_CHANGED");
                    var dropAx = ax.GetProperty("nodes").EnumerateArray().Where(node =>
                        node.TryGetProperty("backendDOMNodeId", out var backend) &&
                        backend.ValueKind == JsonValueKind.Number &&
                        backend.GetInt32() == destination.BackendId &&
                        !(node.TryGetProperty("ignored", out var ignored) && ignored.GetBoolean())).ToArray();
                    if (dropAx.Length != 1 || AxDisabled(dropAx[0]) ||
                        !string.Equals(ElementDigest(request.WindowId, selected.Id,
                            destination.BackendId, loader, selected.Url,
                            AxValue(dropAx[0], "role"), AxValue(dropAx[0], "name")),
                            destination.Digest, StringComparison.OrdinalIgnoreCase))
                        return Rejected(WindowActionCode.STALE_ELEMENT, "DRAG_DESTINATION_CHANGED");
                    var dropBox = await cdp.Call("DOM.getBoxModel", new
                        { backendNodeId = destination.BackendId }, sessionId);
                    if (!TryPoint(dropBox.GetProperty("model").GetProperty("content"),
                        metrics.GetProperty("cssVisualViewport"), out double dx, out double dy))
                        return Rejected(WindowActionCode.CDP_TARGET_NOT_VISIBLE);
                    var dropResolved = await cdp.Call("DOM.resolveNode", new
                        { backendNodeId = destination.BackendId }, sessionId);
                    var dropHit = await cdp.Call("Runtime.callFunctionOn", new
                    {
                        objectId = dropResolved.GetProperty("object").GetProperty("objectId").GetString()!,
                        functionDeclaration = "function(x,y){const h=document.elementFromPoint(x,y);return !!h && (h===this || this.contains(h)) && !this.matches(':disabled') && !this.closest('[inert]');}",
                        arguments = new[] { new { value = dx }, new { value = dy } },
                        returnByValue = true, silent = true
                    }, sessionId);
                    if (!dropHit.GetProperty("result").TryGetProperty("value", out var dropOk) ||
                        dropOk.ValueKind != JsonValueKind.True)
                        return Rejected(WindowActionCode.TARGET_OCCLUDED, "DRAG_DESTINATION_OCCLUDED");
                    dropX = dx; dropY = dy;
                }
                var checkpoint = await DispatchCheckpoint(cdp, sessionId, hwnd, request,
                    target.TargetId, selected.Url, selected.BrowserWindowId, target.LoaderId,
                    timeout.Token);
                if (checkpoint.Code is not null) return Rejected(checkpoint.Code.Value,
                    checkpoint.Detail);
                dispatched = true;
                if (request.Action == WindowActionKind.DRAG)
                    await DispatchDomDrag(cdp, sessionId, x, y, dropX!.Value, dropY!.Value);
                else await DispatchPointer(cdp, sessionId, request, x, y);
            }
            else if (request.Action == WindowActionKind.SET_FILES)
            {
                if (!await FileInputNode(cdp, sessionId, target.BackendId,
                    request.FilePaths!.Length))
                    return Rejected(WindowActionCode.UNSUPPORTED_ACTION, "NOT_FILE_INPUT");
                var paths = request.FilePaths!;
                long[] lengths = new long[paths.Length];
                for (int i = 0; i < paths.Length; i++)
                {
                    try
                    {
                        var info = new FileInfo(paths[i]);
                        if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
                            return Rejected(WindowActionCode.ACCESS_DENIED, "FILE_NOT_READABLE");
                        using var stream = new FileStream(paths[i], FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        lengths[i] = stream.Length;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or
                                               ArgumentException or System.Security.SecurityException)
                    { return Rejected(WindowActionCode.ACCESS_DENIED, "FILE_NOT_READABLE"); }
                }
                if (await FilesSelected(cdp, sessionId, target.BackendId, paths, lengths))
                    return Rejected(WindowActionCode.EXPECTED_STATE_ALREADY_TRUE, "FILES_ALREADY_SELECTED");
                var checkpoint = await DispatchCheckpoint(cdp, sessionId, hwnd, request,
                    target.TargetId, selected.Url, selected.BrowserWindowId, target.LoaderId,
                    timeout.Token);
                if (checkpoint.Code is not null) return Rejected(checkpoint.Code.Value, checkpoint.Detail);
                dispatched = true;
                await cdp.Call("DOM.setFileInputFiles", new
                    { files = paths, backendNodeId = target.BackendId }, sessionId);
                selectedFileCount = paths.Length;
                bool observed = await FilesSelected(cdp, sessionId, target.BackendId, paths, lengths);
                return new(WindowActionCode.COMPLETED, TargetVerified: true,
                    TargetIdentity: request.ElementId, ProviderResponse: provider,
                    ExpectedStateObserved: observed,
                    ObservationState: observed ? WindowObservationState.OBSERVED :
                        WindowObservationState.NOT_OBSERVED,
                    ElapsedMs: clock.ElapsedMilliseconds,
                    ExecutionMethod: WindowExecutionMethod.CHROME_CDP,
                    SelectedFileCount: observed ? selectedFileCount : null);
            }
            else if (request.Action is WindowActionKind.KEY_PRESS or WindowActionKind.TYPE_TEXT or
                     WindowActionKind.SET_VALUE)
            {
                if ((request.Action is WindowActionKind.TYPE_TEXT or WindowActionKind.SET_VALUE) &&
                    !await EditableDomNode(cdp, sessionId, target.BackendId))
                    return Rejected(WindowActionCode.UNSUPPORTED_ACTION);
                var checkpoint = await DispatchCheckpoint(cdp, sessionId, hwnd, request,
                    target.TargetId, selected.Url, selected.BrowserWindowId, target.LoaderId,
                    timeout.Token);
                if (checkpoint.Code is not null) return Rejected(checkpoint.Code.Value,
                    checkpoint.Detail);
                // DOM.focus itself can fire web events. Once requested, any
                // transport failure has an unknown effect, even if no key or
                // text command has been issued yet; never retry it blindly.
                dispatched = true;
                await cdp.Call("DOM.focus", new { backendNodeId = target.BackendId }, sessionId);
                if (request.Action is WindowActionKind.TYPE_TEXT or WindowActionKind.SET_VALUE)
                {
                    await ReplaceFocusedText(cdp, sessionId, request.Value!);
                }
                else
                {
                    var (key, code, vk, value) = request.Key switch
                    {
                        WindowKey.ENTER => ("Enter", "Enter", 13, "\r"),
                        WindowKey.SPACE => (" ", "Space", 32, " "),
                        WindowKey.ESCAPE => ("Escape", "Escape", 27, ""),
                        _ => throw new InvalidOperationException("INVALID_KEY")
                    };
                    await cdp.Call("Input.dispatchKeyEvent", new { type = "keyDown", key, code,
                        windowsVirtualKeyCode = vk, text = value }, sessionId);
                    await cdp.Call("Input.dispatchKeyEvent", new { type = "keyUp", key, code,
                        windowsVirtualKeyCode = vk }, sessionId);
                }
            }
            else return Rejected(WindowActionCode.UNSUPPORTED_ACTION);

            bool? effect = null;
            bool? enabledObserved = null;
            if (request.ExpectedName is not null || request.ExpectedEnabledName is not null ||
                desiredState is not null ||
                (request.Action is WindowActionKind.TYPE_TEXT or WindowActionKind.SET_VALUE))
            {
                var observation = Stopwatch.StartNew();
                do
                {
                    bool needsAx = desiredState is not null || request.ExpectedEnabledName is not null ||
                        (request.ExpectedName is not null && request.ExpectedKind is
                            (WindowExpectedKind.UIA_ELEMENT or WindowExpectedKind.UIA_VALUE or
                                WindowExpectedKind.UIA_ENABLED));
                    var latestAx = needsAx
                        ? await cdp.Call("Accessibility.getFullAXTree", new { }, sessionId)
                        : default;
                    bool primary = request.ExpectedName is not null
                        ? (request.ExpectedKind is WindowExpectedKind.UIA_ELEMENT or
                            WindowExpectedKind.UIA_VALUE or WindowExpectedKind.UIA_ENABLED
                            ? ExpectedAxState(latestAx, request)
                            : WindowAction.ExternalStatePresent(request.ExpectedKind, request.ExpectedName)) ==
                            (request.ExpectedChange == WindowExpectedChange.APPEARS)
                        : (request.Action is WindowActionKind.TYPE_TEXT or WindowActionKind.SET_VALUE)
                            ? await ReadTextValue(cdp, sessionId, target.BackendId, request.Value!)
                            : true;
                    enabledObserved = request.ExpectedEnabledName is null ? null :
                        HasAxEnabled(latestAx, request.ExpectedEnabledName, request.ExpectedEnabled);
                    bool semanticObserved = desiredState is null ||
                        latestAx.GetProperty("nodes").EnumerateArray().Any(node =>
                            node.TryGetProperty("backendDOMNodeId", out var backend) &&
                            backend.ValueKind == JsonValueKind.Number &&
                            backend.GetInt32() == target.BackendId &&
                            AxActionState(node, request.Action) == desiredState);
                    effect = primary && semanticObserved && enabledObserved != false;
                    if (effect == true) break;
                    await Task.Delay(100, timeout.Token);
                } while (observation.ElapsedMilliseconds < request.ObservationTimeoutMs);
            }
            return new(WindowActionCode.COMPLETED, TargetVerified: true,
                TargetIdentity: request.ElementId, ProviderResponse: provider,
                ExpectedStateObserved: effect,
                ObservationState: effect is null ? WindowObservationState.NOT_REQUESTED :
                    effect == true ? WindowObservationState.OBSERVED : WindowObservationState.NOT_OBSERVED,
                ExpectedEnabledObserved: enabledObserved, ElapsedMs: clock.ElapsedMilliseconds,
                ExecutionMethod: WindowExecutionMethod.CHROME_CDP);
        }
        catch (CdpWindowMappingException) when (!dispatched)
        {
            // Preserve the authorized socket when only the window lookup failed.
            return Rejected(WindowActionCode.CDP_TARGET_AMBIGUOUS);
        }
        catch (Exception e) when (e is WebSocketException or IOException or JsonException or
            OperationCanceledException or InvalidOperationException or KeyNotFoundException)
        {
            ObserveCdpFailure(e);
            return new(dispatched ? WindowActionCode.CDP_FAILED_UNKNOWN_EFFECT : WindowActionCode.CDP_UNAVAILABLE,
                EffectUnknown: dispatched, FailureStage: dispatched ? WindowFailureStage.DISPATCH_INPUT :
                    WindowFailureStage.CDP_TARGET, ExceptionType: e.GetType().Name,
                Detail: e is InvalidOperationException { Message: "CDP_COMMAND_REJECTED" } or
                    InvalidOperationException { Message: "CDP_TARGET_DETACHED" } ? e.Message : null,
                ElapsedMs: clock.ElapsedMilliseconds,
                ExecutionMethod: dispatched ? WindowExecutionMethod.CHROME_CDP : null);
        }
        WindowActionResult Rejected(WindowActionCode status, string? detail = null) => new(status,
            TargetVerified: status == WindowActionCode.EXPECTED_STATE_ALREADY_TRUE,
            FailureStage: WindowFailureStage.CDP_TARGET, Detail: detail,
            ElapsedMs: clock.ElapsedMilliseconds);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hwnd, int command);
}
