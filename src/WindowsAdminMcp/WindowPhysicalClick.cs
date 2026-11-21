using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

// Explicit physical click fallback. Never inject input into an occluded or
// unexpected foreground window; UIA actions remain the background-first path.
internal static class WindowPhysicalClick
{
    private const string ForegroundPolicy = "WINDOWS_FOREGROUND_POLICY_USE_UIA_OR_OPT_IN_CDP";
    private const uint InputMouse = 0;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const uint KeyUnicode = 0x0004;
    private const uint RootAncestor = 2;

    internal static WindowActionResult Click(IntPtr hwnd, AutomationElement target,
        WindowActionRequest request, Stopwatch clock)
    {
        bool dispatched = false;
        var foregroundAtEntry = GetForegroundWindow();
        WindowFailureStage stage = WindowFailureStage.ACTIVATE_WINDOW;
        try
        {
            var current = target.Current;
            if (current.ControlType != ControlType.Button &&
                current.ControlType != ControlType.Hyperlink &&
                current.ControlType != ControlType.MenuItem &&
                current.ControlType != ControlType.CheckBox &&
                current.ControlType != ControlType.RadioButton &&
                current.ControlType != ControlType.ListItem &&
                current.ControlType != ControlType.TabItem)
                return new(WindowActionCode.UNSUPPORTED_ACTION, ElapsedMs: clock.ElapsedMilliseconds);

            // Chrome web content can be clicked through its explicitly opted-in
            // DevTools connection without stealing desktop focus or mouse position.
            var background = WindowChromeCdp.TryDispatch(hwnd, target, request, clock);
            if (background is not null) return background;

            if (current.IsOffscreen || IsIconic(hwnd) || !IsWindowVisible(hwnd))
                return new(WindowActionCode.WINDOW_NOT_VISIBLE, ElapsedMs: clock.ElapsedMilliseconds,
                    FailureStage: stage);

            if (GetForegroundWindow() != hwnd && !SetForegroundWindow(hwnd))
                return new(WindowActionCode.FOREGROUND_UNAVAILABLE,
                    ElapsedMs: clock.ElapsedMilliseconds, FailureStage: stage,
                    Detail: ForegroundPolicy);
            if (GetForegroundWindow() != hwnd)
                return new(WindowActionCode.FOREGROUND_UNAVAILABLE,
                    ElapsedMs: clock.ElapsedMilliseconds, FailureStage: stage,
                    Detail: ForegroundPolicy);

            stage = WindowFailureStage.HIT_TEST;
            if (!WindowInspector.TryParseElementRef(request.ElementId, out long snapshot) ||
                WindowCatalog.ResolveId(request.WindowId, Process.GetCurrentProcess().SessionId, out var actualHwnd)
                    != WindowResolveCode.OK || actualHwnd != hwnd ||
                !string.Equals(WindowInspector.ElementRef(target, request.WindowId, snapshot),
                    request.ElementId, StringComparison.Ordinal))
                return new(WindowActionCode.STALE_ELEMENT, ElapsedMs: clock.ElapsedMilliseconds,
                    ForegroundChanged: foregroundAtEntry != GetForegroundWindow(), FailureStage: stage);

            if (!target.TryGetClickablePoint(out var point))
                return new(WindowActionCode.CLICK_POINT_UNAVAILABLE, ElapsedMs: clock.ElapsedMilliseconds,
                    ForegroundChanged: foregroundAtEntry != GetForegroundWindow(), FailureStage: stage);

            var pixel = new Point((int)Math.Round(point.X), (int)Math.Round(point.Y));
            if (!HitsTarget(hwnd, target, pixel))
                return new(WindowActionCode.TARGET_OCCLUDED, ElapsedMs: clock.ElapsedMilliseconds,
                    ForegroundChanged: foregroundAtEntry != GetForegroundWindow(), FailureStage: stage);

            // Cursor movement may update hover state or trigger user activity.
            // Recheck window ownership and UIA hit testing immediately before input.
            if (!SetCursorPos(pixel.X, pixel.Y))
                return new(WindowActionCode.CLICK_POINT_UNAVAILABLE, ElapsedMs: clock.ElapsedMilliseconds,
                    ForegroundChanged: foregroundAtEntry != GetForegroundWindow(), FailureStage: stage);
            if (!GetCursorPos(out var actualCursor) || actualCursor.X != pixel.X ||
                actualCursor.Y != pixel.Y || GetForegroundWindow() != hwnd ||
                !HitsTarget(hwnd, target, pixel) ||
                WindowCatalog.ResolveId(request.WindowId, Process.GetCurrentProcess().SessionId,
                    out actualHwnd) != WindowResolveCode.OK || actualHwnd != hwnd ||
                !WindowInspector.TryParseElementRef(request.ElementId, out _))
                return new(WindowActionCode.TARGET_OCCLUDED, ElapsedMs: clock.ElapsedMilliseconds,
                    ForegroundChanged: foregroundAtEntry != GetForegroundWindow(), FailureStage: stage);

            stage = WindowFailureStage.DISPATCH_INPUT;
            var inputs = new[]
            {
                new Input { Type = InputMouse, Mouse = new MouseInput { Flags = LeftDown } },
                new Input { Type = InputMouse, Mouse = new MouseInput { Flags = LeftUp } }
            };
            dispatched = true;
            uint inserted = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            bool changed = foregroundAtEntry != GetForegroundWindow();
            bool activated = foregroundAtEntry != hwnd && GetForegroundWindow() == hwnd;
            return inserted == inputs.Length
                ? new(WindowActionCode.COMPLETED, ElapsedMs: clock.ElapsedMilliseconds,
                    ForegroundChanged: changed, TargetActivated: activated)
                : new(WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT, EffectUnknown: true,
                    ElapsedMs: clock.ElapsedMilliseconds, ForegroundChanged: changed,
                    TargetActivated: activated, FailureStage: stage,
                    Detail: $"INPUTS_INSERTED_{inserted}:WIN32_{Marshal.GetLastPInvokeError()}");
        }
        catch (Exception e) when (e is COMException or ElementNotAvailableException or InvalidOperationException)
        {
            return new(dispatched ? WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT : WindowActionCode.UIA_UNAVAILABLE,
                EffectUnknown: dispatched, ElapsedMs: clock.ElapsedMilliseconds,
                FailureStage: stage, ExceptionType: e.GetType().Name,
                HResult: e.HResult.ToString("X8"));
        }
    }

    // A key is addressed to an exact, freshly verified UIA element. In particular,
    // never send Enter to the global foreground when Chrome denies SetFocus.
    internal static WindowActionResult PressKey(IntPtr hwnd, AutomationElement target,
        WindowActionRequest request, Stopwatch clock)
    {
        bool dispatched = false;
        var foregroundAtEntry = GetForegroundWindow();
        WindowFailureStage stage = WindowFailureStage.PREPARE_FOCUS;
        try
        {
            if (request.Key is null)
                return new(WindowActionCode.INVALID_ARGUMENT, FailureStage: stage,
                    ElapsedMs: clock.ElapsedMilliseconds);

            var background = WindowChromeCdp.TryDispatch(hwnd, target, request, clock);
            if (background is not null) return background;

            if (!target.Current.IsKeyboardFocusable || target.Current.IsOffscreen ||
                IsIconic(hwnd) || !IsWindowVisible(hwnd))
                return new(WindowActionCode.FOCUS_UNAVAILABLE, FailureStage: stage,
                    ElapsedMs: clock.ElapsedMilliseconds);

            if (GetForegroundWindow() != hwnd && !SetForegroundWindow(hwnd))
                return new(WindowActionCode.FOREGROUND_UNAVAILABLE,
                    FailureStage: WindowFailureStage.ACTIVATE_WINDOW,
                    ElapsedMs: clock.ElapsedMilliseconds, Detail: ForegroundPolicy);
            if (GetForegroundWindow() != hwnd)
                return new(WindowActionCode.FOREGROUND_UNAVAILABLE,
                    FailureStage: WindowFailureStage.ACTIVATE_WINDOW,
                    ElapsedMs: clock.ElapsedMilliseconds, Detail: ForegroundPolicy);

            if (!target.Current.HasKeyboardFocus) target.SetFocus();
            // UIA providers can update focus asynchronously. Allow one short
            // observation interval, but never inject a key on an unverified focus.
            bool focused = false;
            for (int attempt = 0; attempt < 4 && GetForegroundWindow() == hwnd; attempt++)
            {
                var actual = AutomationElement.FocusedElement;
                focused = target.Current.HasKeyboardFocus && actual is not null &&
                    actual.GetRuntimeId().SequenceEqual(target.GetRuntimeId());
                if (focused) break;
                Thread.Sleep(25);
            }
            if (!focused || GetForegroundWindow() != hwnd ||
                !WindowInspector.TryParseElementRef(request.ElementId, out long snapshot) ||
                WindowCatalog.ResolveId(request.WindowId, Process.GetCurrentProcess().SessionId,
                    out var resolved) != WindowResolveCode.OK || resolved != hwnd ||
                !string.Equals(WindowInspector.ElementRef(target, request.WindowId, snapshot),
                    request.ElementId, StringComparison.Ordinal))
                return new(WindowActionCode.FOCUS_UNAVAILABLE,
                    ForegroundChanged: foregroundAtEntry != GetForegroundWindow(),
                    FailureStage: stage, ElapsedMs: clock.ElapsedMilliseconds);

            ushort virtualKey = request.Key.Value switch
            {
                WindowKey.ENTER => 0x0D,
                WindowKey.SPACE => 0x20,
                WindowKey.ESCAPE => 0x1B,
                _ => (ushort)0
            };
            if (virtualKey == 0) return new(WindowActionCode.INVALID_ARGUMENT);
            stage = WindowFailureStage.DISPATCH_INPUT;
            var inputs = new[]
            {
                new KeyboardEventInput { Type = InputKeyboard, VirtualKey = virtualKey },
                new KeyboardEventInput { Type = InputKeyboard, VirtualKey = virtualKey, Flags = KeyUp }
            };
            var finalFocus = AutomationElement.FocusedElement;
            if (GetForegroundWindow() != hwnd || !target.Current.HasKeyboardFocus ||
                finalFocus is null ||
                !finalFocus.GetRuntimeId().SequenceEqual(target.GetRuntimeId()) ||
                WindowCatalog.ResolveId(request.WindowId, Process.GetCurrentProcess().SessionId,
                    out var finalHwnd) != WindowResolveCode.OK || finalHwnd != hwnd)
                return new(WindowActionCode.FOCUS_UNAVAILABLE, FailureStage: stage,
                    ElapsedMs: clock.ElapsedMilliseconds);
            dispatched = true;
            uint inserted = SendKeyboardInput((uint)inputs.Length, inputs, Marshal.SizeOf<KeyboardEventInput>());
            bool changed = foregroundAtEntry != GetForegroundWindow();
            bool activated = foregroundAtEntry != hwnd && GetForegroundWindow() == hwnd;
            return inserted == inputs.Length
                ? new(WindowActionCode.COMPLETED, ForegroundChanged: changed,
                    TargetActivated: activated, ElapsedMs: clock.ElapsedMilliseconds)
                : new(WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT, EffectUnknown: true,
                    ForegroundChanged: changed, TargetActivated: activated,
                    FailureStage: stage, ElapsedMs: clock.ElapsedMilliseconds,
                    Detail: $"KEY_INPUTS_INSERTED_{inserted}:WIN32_{Marshal.GetLastPInvokeError()}");
        }
        catch (Exception e) when (e is COMException or ElementNotAvailableException or InvalidOperationException)
        {
            return new(dispatched ? WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT :
                WindowActionCode.FOCUS_UNAVAILABLE, EffectUnknown: dispatched,
                ForegroundChanged: foregroundAtEntry != GetForegroundWindow(),
                FailureStage: stage, ExceptionType: e.GetType().Name,
                HResult: e.HResult.ToString("X8"), ElapsedMs: clock.ElapsedMilliseconds);
        }
    }

    // TYPE_TEXT replaces an Edit/ComboBox value. For providers without
    // ValuePattern.SetValue it uses one ordered Unicode SendInput batch, never
    // a sequence of unverified per-character clicks or global paste shortcuts.
    internal static WindowActionResult TypeText(IntPtr hwnd, AutomationElement target,
        WindowActionRequest request, Stopwatch clock)
    {
        bool dispatched = false;
        var foregroundAtEntry = GetForegroundWindow();
        try
        {
            if (request.Value is null || (target.Current.ControlType != ControlType.Edit &&
                target.Current.ControlType != ControlType.ComboBox))
                return new(WindowActionCode.UNSUPPORTED_ACTION, ElapsedMs: clock.ElapsedMilliseconds);
            var background = WindowChromeCdp.TryDispatch(hwnd, target, request, clock);
            if (background is not null) return background;

            if (!target.Current.IsKeyboardFocusable || target.Current.IsOffscreen ||
                IsIconic(hwnd) || !IsWindowVisible(hwnd))
                return new(WindowActionCode.FOCUS_UNAVAILABLE, FailureStage: WindowFailureStage.PREPARE_FOCUS);
            if (GetForegroundWindow() != hwnd && !SetForegroundWindow(hwnd))
                return new(WindowActionCode.FOREGROUND_UNAVAILABLE,
                    FailureStage: WindowFailureStage.ACTIVATE_WINDOW, Detail: ForegroundPolicy);
            if (GetForegroundWindow() != hwnd)
                return new(WindowActionCode.FOREGROUND_UNAVAILABLE,
                    FailureStage: WindowFailureStage.ACTIVATE_WINDOW, Detail: ForegroundPolicy);

            if (!target.Current.HasKeyboardFocus) target.SetFocus();
            bool focused = false;
            for (int attempt = 0; attempt < 4 && GetForegroundWindow() == hwnd; attempt++)
            {
                var actual = AutomationElement.FocusedElement;
                focused = target.Current.HasKeyboardFocus && actual is not null &&
                    actual.GetRuntimeId().SequenceEqual(target.GetRuntimeId());
                if (focused) break;
                Thread.Sleep(25);
            }
            if (!focused || GetForegroundWindow() != hwnd ||
                !WindowInspector.TryParseElementRef(request.ElementId, out long snapshot) ||
                WindowCatalog.ResolveId(request.WindowId, Process.GetCurrentProcess().SessionId,
                    out var resolved) != WindowResolveCode.OK || resolved != hwnd ||
                !string.Equals(WindowInspector.ElementRef(target, request.WindowId, snapshot),
                    request.ElementId, StringComparison.Ordinal))
                return new(WindowActionCode.FOCUS_UNAVAILABLE,
                    FailureStage: WindowFailureStage.PREPARE_FOCUS);

            var inputs = new List<KeyboardEventInput>(4 + request.Value.Length * 2);
            // Ctrl+A selects the current field contents before replacing them.
            inputs.Add(new() { Type = InputKeyboard, VirtualKey = 0x11 });
            inputs.Add(new() { Type = InputKeyboard, VirtualKey = 0x41 });
            inputs.Add(new() { Type = InputKeyboard, VirtualKey = 0x41, Flags = KeyUp });
            inputs.Add(new() { Type = InputKeyboard, VirtualKey = 0x11, Flags = KeyUp });
            if (request.Value.Length == 0)
            {
                inputs.Add(new() { Type = InputKeyboard, VirtualKey = 0x2E });
                inputs.Add(new() { Type = InputKeyboard, VirtualKey = 0x2E, Flags = KeyUp });
            }
            foreach (char ch in request.Value)
            {
                inputs.Add(new() { Type = InputKeyboard, Scan = ch, Flags = KeyUnicode });
                inputs.Add(new() { Type = InputKeyboard, Scan = ch, Flags = KeyUnicode | KeyUp });
            }
            var actualFocus = AutomationElement.FocusedElement;
            if (GetForegroundWindow() != hwnd || !target.Current.HasKeyboardFocus ||
                actualFocus is null ||
                !actualFocus.GetRuntimeId().SequenceEqual(target.GetRuntimeId()) ||
                WindowCatalog.ResolveId(request.WindowId, Process.GetCurrentProcess().SessionId,
                    out resolved) != WindowResolveCode.OK || resolved != hwnd)
                return new(WindowActionCode.FOCUS_UNAVAILABLE,
                    FailureStage: WindowFailureStage.PREPARE_FOCUS);
            dispatched = true;
            uint inserted = SendKeyboardInput((uint)inputs.Count, [.. inputs],
                Marshal.SizeOf<KeyboardEventInput>());
            bool changed = foregroundAtEntry != GetForegroundWindow();
            return inserted == inputs.Count && GetForegroundWindow() == hwnd
                ? new(WindowActionCode.COMPLETED, ForegroundChanged: changed,
                    TargetActivated: foregroundAtEntry != hwnd, ElapsedMs: clock.ElapsedMilliseconds)
                : new(WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT, EffectUnknown: true,
                    ForegroundChanged: changed, FailureStage: WindowFailureStage.DISPATCH_INPUT,
                    Detail: $"TEXT_INPUTS_INSERTED_{inserted}:WIN32_{Marshal.GetLastPInvokeError()}",
                    ElapsedMs: clock.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is COMException or ElementNotAvailableException or InvalidOperationException)
        {
            return new(dispatched ? WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT :
                WindowActionCode.FOCUS_UNAVAILABLE, EffectUnknown: dispatched,
                FailureStage: dispatched ? WindowFailureStage.DISPATCH_INPUT : WindowFailureStage.PREPARE_FOCUS,
                ExceptionType: e.GetType().Name, HResult: e.HResult.ToString("X8"),
                ElapsedMs: clock.ElapsedMilliseconds);
        }
    }

    private static bool HitsTarget(IntPtr hwnd, AutomationElement target, Point pixel)
    {
        var hitWindow = WindowFromPoint(pixel);
        if (hitWindow == IntPtr.Zero || GetAncestor(hitWindow, RootAncestor) != hwnd)
            return false;
        var hit = AutomationElement.FromPoint(new System.Windows.Point(pixel.X, pixel.Y));
        var expectedId = target.GetRuntimeId();
        var walker = TreeWalker.RawViewWalker;
        for (int depth = 0; hit is not null && depth < 24; depth++)
        {
            if (hit.GetRuntimeId().SequenceEqual(expectedId)) return true;
            hit = walker.GetParent(hit);
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Point(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    // Native INPUT is 40 bytes on our pinned win-x64 build; KEYBDINPUT sits
    // at offset 8 and is padded to the same union size as MOUSEINPUT.
    [StructLayout(LayoutKind.Sequential, Size = 40)]
    private struct KeyboardEventInput
    {
        public uint Type, Padding;
        public ushort VirtualKey, Scan;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
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
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputs, [In] Input[] input, int size);
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendKeyboardInput(uint inputs, [In] KeyboardEventInput[] input, int size);
}
