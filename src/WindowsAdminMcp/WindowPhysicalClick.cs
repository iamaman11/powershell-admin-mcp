using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

// Explicit physical click fallback. Never inject input into an occluded or
// unexpected foreground window; UIA actions remain the background-first path.
internal static class WindowPhysicalClick
{
    private const string ForegroundPolicy = "WINDOWS_FOREGROUND_POLICY_NOT_GRANTED";
    private const uint InputMouse = 0;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown=0x0008,RightUp=0x0010;
    private const uint Wheel = 0x0800, HorizontalWheel=0x1000;
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const uint KeyUnicode = 0x0004;
    private const uint RootAncestor = 2;
    private const uint PreviousZWindow = 3;

    // Only a failure before any CDP dispatch may use the native path. An
    // ambiguous browser action must never be replayed through SendInput.
    internal static bool CanUseNativeFallback(WindowActionResult? background) =>
        background is null || background.Status == WindowActionCode.CDP_UNAVAILABLE &&
        !background.EffectUnknown && background.FailureStage == WindowFailureStage.CDP_CONNECT;

    internal static bool ForegroundInputAllowed(WindowActionRequest request) =>
        request.InputMode == WindowInputMode.ALLOW_FOREGROUND_INPUT;

    private static WindowActionResult ForegroundInputDisallowed(Stopwatch clock) =>
        new(WindowActionCode.FOREGROUND_INPUT_DISALLOWED, Detail: "BACKGROUND_ONLY",
            ElapsedMs: clock.ElapsedMilliseconds);

    private static bool ActivateVerifiedWindow(IntPtr hwnd, AutomationElement target,
        bool focusElement = true)
    {
        if (GetForegroundWindow() == hwnd) return true;
        _ = SetForegroundWindow(hwnd);
        if (GetForegroundWindow() == hwnd) return true;
        // Chrome sometimes grants foreground only when the exact accessible
        // control receives native UIA focus. This does not inject user input.
        try
        {
            // Mouse input must not focus a covered target before hit testing:
            // some providers raise its window and make an overlay appear absent.
            if (focusElement && target.Current.IsKeyboardFocusable)
                target.SetFocus();
        }
        catch (Exception e) when (e is COMException or ElementNotAvailableException or
            InvalidOperationException) { return false; }
        for (int i = 0; i < 4; i++)
        {
            if (GetForegroundWindow() == hwnd) return true;
            Thread.Sleep(25);
        }
        return false;
    }

    private static bool StableFocus(IntPtr hwnd, AutomationElement target, string? expectedProfile)
    {
        // A Chromium UIA provider may acknowledge focus before its renderer
        // processes the corresponding DOM focus. Let the target consume that
        // transition, then check both HWND and exact element again before input.
        // Other native apps retain the zero-delay path.
        if (expectedProfile is not null) Thread.Sleep(160);
        if (GetForegroundWindow() != hwnd || !target.Current.HasKeyboardFocus) return false;
        var actual = AutomationElement.FocusedElement;
        return actual is not null && actual.GetRuntimeId().SequenceEqual(target.GetRuntimeId());
    }

    internal static int ChromeChunkUnits(string value, int offset)
    {
        if (offset < 0 || offset >= value.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        int units = offset < 64 ? 1 : Math.Min(16, value.Length - offset);
        if (offset + units < value.Length && char.IsSurrogatePair(value, offset + units - 1))
            units++;
        return units;
    }

    internal static (uint Down,uint Up,int Count) PointerPlan(WindowActionKind action)=> action switch
    {
        WindowActionKind.RIGHT_CLICK=>(RightDown,RightUp,1),
        WindowActionKind.DOUBLE_CLICK=>(LeftDown,LeftUp,2),
        _=>(LeftDown,LeftUp,1)
    };

    internal static WindowActionResult Click(IntPtr hwnd, AutomationElement target,
        WindowActionRequest request, Stopwatch clock, bool allowCdp = true)
    {
        bool dispatched = false;
        var foregroundAtEntry = GetForegroundWindow();
        WindowFailureStage stage = WindowFailureStage.ACTIVATE_WINDOW;
        try
        {
            var current = target.Current;
            // Chrome web content can be clicked through its explicitly opted-in
            // DevTools connection without stealing desktop focus or mouse position.
            var background = allowCdp ? WindowChromeCdp.TryDispatch(hwnd, target, request, clock) : null;
            if (!CanUseNativeFallback(background)) return background!;

            if (!ForegroundInputAllowed(request)) return ForegroundInputDisallowed(clock);

            if (current.IsOffscreen || IsIconic(hwnd) || !IsWindowVisible(hwnd))
                return new(WindowActionCode.WINDOW_NOT_VISIBLE, ElapsedMs: clock.ElapsedMilliseconds,
                    FailureStage: stage);

            if (!ActivateVerifiedWindow(hwnd, target, focusElement: false))
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
            var plan=PointerPlan(request.Action);
            var inputs=new Input[plan.Count*2];
            for(int click=0;click<plan.Count;click++)
            {
                inputs[click*2]=new Input {Type=InputMouse,Mouse=new MouseInput {Flags=plan.Down}};
                inputs[click*2+1]=new Input {Type=InputMouse,Mouse=new MouseInput {Flags=plan.Up}};
            }
            dispatched = true;
            uint inserted = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            bool changed = foregroundAtEntry != GetForegroundWindow();
            bool activated = foregroundAtEntry != hwnd && GetForegroundWindow() == hwnd;
            return inserted == inputs.Length
                ? new(WindowActionCode.COMPLETED, ElapsedMs: clock.ElapsedMilliseconds,
                    ForegroundChanged: changed, TargetActivated: activated,
                    ExecutionMethod: WindowExecutionMethod.WIN32_SENDINPUT)
                : new(WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT, EffectUnknown: true,
                    ElapsedMs: clock.ElapsedMilliseconds, ForegroundChanged: changed,
                    TargetActivated: activated, FailureStage: stage,
                    Detail: $"INPUTS_INSERTED_{inserted}:WIN32_{Marshal.GetLastPInvokeError()}",
                    ExecutionMethod: WindowExecutionMethod.WIN32_SENDINPUT);
        }
        catch (Exception e) when (e is COMException or ElementNotAvailableException or InvalidOperationException)
        {
            return new(dispatched ? WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT : WindowActionCode.UIA_UNAVAILABLE,
                EffectUnknown: dispatched, ElapsedMs: clock.ElapsedMilliseconds,
                FailureStage: stage, ExceptionType: e.GetType().Name,
                HResult: e.HResult.ToString("X8"),
                ExecutionMethod: dispatched ? WindowExecutionMethod.WIN32_SENDINPUT : null);
        }
    }

    internal static WindowActionResult Scroll(IntPtr hwnd,AutomationElement target,
        WindowActionRequest request,Stopwatch clock)
    {
        if (!ForegroundInputAllowed(request)) return ForegroundInputDisallowed(clock);
        var before=GetForegroundWindow();
        if(!IsWindowVisible(hwnd) || IsIconic(hwnd))return new(WindowActionCode.WINDOW_NOT_VISIBLE);
        if(!ActivateVerifiedWindow(hwnd,target,focusElement:false))
            return new(WindowActionCode.FOREGROUND_UNAVAILABLE,FailureStage:WindowFailureStage.ACTIVATE_WINDOW);
        if(!target.TryGetClickablePoint(out var point))return new(WindowActionCode.CLICK_POINT_UNAVAILABLE);
        var p=new Point((int)Math.Round(point.X),(int)Math.Round(point.Y));
        if(!HitsTarget(hwnd,target,p))return new(WindowActionCode.TARGET_OCCLUDED);
        if(!SetCursorPos(p.X,p.Y) || GetForegroundWindow()!=hwnd || !HitsTarget(hwnd,target,p) ||
            WindowCatalog.ResolveId(request.WindowId,Process.GetCurrentProcess().SessionId,out var actual)!=WindowResolveCode.OK || actual!=hwnd)
            return new(WindowActionCode.TARGET_OCCLUDED);
        bool vertical=request.ScrollDirection is WindowScrollDirection.UP or WindowScrollDirection.DOWN;
        int ticks=(request.ScrollDirection is WindowScrollDirection.UP or WindowScrollDirection.RIGHT ? 1:-1)*
            request.ScrollUnits*120;
        var inputs=new[] {new Input {Type=InputMouse,Mouse=new MouseInput {
            Flags=vertical?Wheel:HorizontalWheel,Data=unchecked((uint)ticks)} }};
        uint inserted=SendInput(1,inputs,Marshal.SizeOf<Input>());
        return new(inserted==1?WindowActionCode.COMPLETED:WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT,
            EffectUnknown:true,ForegroundChanged:before!=GetForegroundWindow(),
            ElapsedMs:clock.ElapsedMilliseconds,FailureStage:WindowFailureStage.DISPATCH_INPUT,
            ExecutionMethod:WindowExecutionMethod.WIN32_SENDINPUT);
    }

    // HOVER moves only the cursor. DRAG is an explicitly requested physical
    // gesture: any partial SendInput is an unknown effect and is never replayed.
    internal static WindowActionResult Pointer(IntPtr hwnd,AutomationElement target,
        WindowActionRequest request,Stopwatch clock)
    {
        if (!ForegroundInputAllowed(request)) return ForegroundInputDisallowed(clock);
        var before=GetForegroundWindow();
        if(!IsWindowVisible(hwnd) || IsIconic(hwnd))return new(WindowActionCode.WINDOW_NOT_VISIBLE);
        if(!ActivateVerifiedWindow(hwnd,target,focusElement:false))
            return new(WindowActionCode.FOREGROUND_UNAVAILABLE,FailureStage:WindowFailureStage.ACTIVATE_WINDOW);
        if(!target.TryGetClickablePoint(out var point))return new(WindowActionCode.CLICK_POINT_UNAVAILABLE);
        var source=new Point((int)Math.Round(point.X),(int)Math.Round(point.Y));
        if(!HitsTarget(hwnd,target,source))return new(WindowActionCode.TARGET_OCCLUDED);
        if(request.Action==WindowActionKind.DRAG)
        {
            var dest=new Point(request.TargetX!.Value,request.TargetY!.Value);
            if(WindowFromPoint(dest)==IntPtr.Zero || GetAncestor(WindowFromPoint(dest),RootAncestor)!=hwnd)
                return new(WindowActionCode.TARGET_OCCLUDED);
        }
        if(!SetCursorPos(source.X,source.Y) || !GetCursorPos(out var actualPoint) ||
            actualPoint.X!=source.X || actualPoint.Y!=source.Y ||
            GetForegroundWindow()!=hwnd || !HitsTarget(hwnd,target,source) ||
            WindowCatalog.ResolveId(request.WindowId,Process.GetCurrentProcess().SessionId,out var check)!=WindowResolveCode.OK || check!=hwnd)
            return new(WindowActionCode.TARGET_OCCLUDED);
        if(request.Action==WindowActionKind.HOVER)
            return new(WindowActionCode.COMPLETED,ForegroundChanged:before!=GetForegroundWindow(),
                EffectUnknown:true,Detail:"CURSOR_POSITION_VERIFIED_HOVER_EFFECT_UNKNOWN",
                ObservationState:WindowObservationState.UNKNOWN,
                ElapsedMs:clock.ElapsedMilliseconds,ExecutionMethod:WindowExecutionMethod.WIN32_SENDINPUT);
        bool pressed=false;
        try
        {
            var down=new[]{new Input {Type=InputMouse,Mouse=new MouseInput {Flags=LeftDown}}};
            uint accepted=SendInput(1,down,Marshal.SizeOf<Input>());
            if(accepted!=1)return new(WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT,EffectUnknown:true,
                FailureStage:WindowFailureStage.DISPATCH_INPUT);
            pressed=true;
            var dest=new Point(request.TargetX!.Value,request.TargetY!.Value);
            if(!SetCursorPos(dest.X,dest.Y) || GetForegroundWindow()!=hwnd)
                return new(WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT,EffectUnknown:true,
                    FailureStage:WindowFailureStage.DISPATCH_INPUT);
            var up=new[]{new Input {Type=InputMouse,Mouse=new MouseInput {Flags=LeftUp}}};
            uint finished=SendInput(1,up,Marshal.SizeOf<Input>());
            pressed=false;
            return new(finished==1?WindowActionCode.COMPLETED:WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT,
                EffectUnknown:true,ForegroundChanged:before!=GetForegroundWindow(),
                FailureStage:WindowFailureStage.DISPATCH_INPUT,ElapsedMs:clock.ElapsedMilliseconds,
                ExecutionMethod:WindowExecutionMethod.WIN32_SENDINPUT);
        }
        finally
        {
            if(pressed)
                _=SendInput(1,[new Input {Type=InputMouse,Mouse=new MouseInput {Flags=LeftUp}}],Marshal.SizeOf<Input>());
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
            if (request.Key is null && request.VirtualKey is null)
                return new(WindowActionCode.INVALID_ARGUMENT, FailureStage: stage,
                    ElapsedMs: clock.ElapsedMilliseconds);

            var background = WindowChromeCdp.TryDispatch(hwnd, target, request, clock);
            if (!CanUseNativeFallback(background)) return background!;

            if (!ForegroundInputAllowed(request)) return ForegroundInputDisallowed(clock);

            if (!target.Current.IsKeyboardFocusable || target.Current.IsOffscreen ||
                IsIconic(hwnd) || !IsWindowVisible(hwnd))
                return new(WindowActionCode.FOCUS_UNAVAILABLE, FailureStage: stage,
                    ElapsedMs: clock.ElapsedMilliseconds);

            if (!ActivateVerifiedWindow(hwnd, target))
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

            ushort virtualKey = request.VirtualKey is int vk ? checked((ushort)vk) :
                (ushort)(request.Key!.Value switch
                {
                    WindowKey.ENTER => 0x0D,
                    WindowKey.SPACE => 0x20,
                    WindowKey.ESCAPE => 0x1B,
                    _ => 0
                });
            if (virtualKey == 0) return new(WindowActionCode.INVALID_ARGUMENT);
            stage = WindowFailureStage.DISPATCH_INPUT;
            var keys = new List<ushort>(5);
            if(request.Ctrl)keys.Add(0x11);
            if(request.Alt)keys.Add(0x12);
            if(request.Shift)keys.Add(0x10);
            if(request.Win)keys.Add(0x5B);
            keys.Add(virtualKey);
            var inputs=keys.Select(key=>new KeyboardEventInput {Type=InputKeyboard,VirtualKey=key})
                .Concat(keys.AsEnumerable().Reverse().Select(key=>new KeyboardEventInput
                    {Type=InputKeyboard,VirtualKey=key,Flags=KeyUp})).ToArray();
            if (!StableFocus(hwnd, target, request.ExpectedProfile) ||
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
                    TargetActivated: activated, ElapsedMs: clock.ElapsedMilliseconds,
                    ExecutionMethod: WindowExecutionMethod.WIN32_SENDINPUT)
                : new(WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT, EffectUnknown: true,
                    ForegroundChanged: changed, TargetActivated: activated,
                    FailureStage: stage, ElapsedMs: clock.ElapsedMilliseconds,
                    Detail: $"KEY_INPUTS_INSERTED_{inserted}:WIN32_{Marshal.GetLastPInvokeError()}",
                    ExecutionMethod: WindowExecutionMethod.WIN32_SENDINPUT);
        }
        catch (Exception e) when (e is COMException or ElementNotAvailableException or InvalidOperationException)
        {
            return new(dispatched ? WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT :
                WindowActionCode.FOCUS_UNAVAILABLE, EffectUnknown: dispatched,
                ForegroundChanged: foregroundAtEntry != GetForegroundWindow(),
                FailureStage: stage, ExceptionType: e.GetType().Name,
                HResult: e.HResult.ToString("X8"), ElapsedMs: clock.ElapsedMilliseconds,
                ExecutionMethod: dispatched ? WindowExecutionMethod.WIN32_SENDINPUT : null);
        }
    }

    // TYPE_TEXT replaces the value of the specifically focused element. For
    // providers without ValuePattern.SetValue it uses Unicode SendInput, never
    // a sequence of unverified per-character clicks or global paste shortcuts.
    internal static WindowActionResult TypeText(IntPtr hwnd, AutomationElement target,
        WindowActionRequest request, Stopwatch clock, bool allowCdp = true)
    {
        bool dispatched = false;
        var foregroundAtEntry = GetForegroundWindow();
        try
        {
            if (request.Value is null)
                return new(WindowActionCode.INVALID_ARGUMENT, ElapsedMs: clock.ElapsedMilliseconds);
            if (allowCdp)
            {
                var background = WindowChromeCdp.TryDispatch(hwnd, target, request, clock);
                if (!CanUseNativeFallback(background)) return background!;
            }

            if (!ForegroundInputAllowed(request)) return ForegroundInputDisallowed(clock);

            if (!target.Current.IsKeyboardFocusable || target.Current.IsOffscreen ||
                IsIconic(hwnd) || !IsWindowVisible(hwnd))
                return new(WindowActionCode.FOCUS_UNAVAILABLE, FailureStage: WindowFailureStage.PREPARE_FOCUS);
            if (!ActivateVerifiedWindow(hwnd, target))
                return new(WindowActionCode.FOREGROUND_UNAVAILABLE,
                    FailureStage: WindowFailureStage.ACTIVATE_WINDOW, Detail: ForegroundPolicy);

            // Chromium sometimes reports UIA keyboard focus without delivering
            // DOM focus to a React input or contenteditable widget. A physical,
            // hit-tested click is required before SendInput for verified Chrome.
            // Never inject text if the field is covered by another window.
            if (request.ExpectedProfile is not null)
            {
                var click = Click(hwnd, target, request with { Action = WindowActionKind.CLICK },
                    clock, allowCdp: false);
                if (click.Status != WindowActionCode.COMPLETED) return click;
                // SendInput queues the mouse events before Chromium necessarily
                // processes their DOM focus transition. An immediate Unicode
                // batch can go to the window while the React input stays inert.
                // The verified native PowerShell probe needed an explicit
                // post-click interval. Fail closed if focus was stolen meanwhile.
                Thread.Sleep(320);
                if (GetForegroundWindow() != hwnd)
                    return new(WindowActionCode.FOCUS_UNAVAILABLE,
                        FailureStage: WindowFailureStage.PREPARE_FOCUS,
                        ElapsedMs: clock.ElapsedMilliseconds);
            }

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
            if (!StableFocus(hwnd, target, request.ExpectedProfile) ||
                WindowCatalog.ResolveId(request.WindowId, Process.GetCurrentProcess().SessionId,
                    out resolved) != WindowResolveCode.OK || resolved != hwnd)
                return new(WindowActionCode.FOCUS_UNAVAILABLE,
                    FailureStage: WindowFailureStage.PREPARE_FOCUS);
            dispatched = true;
            uint inserted;
            if (request.ExpectedProfile is not null &&
                request.Value.Length > 0)
            {
                // Chromium's controlled React inputs can discard a fast batch
                // even after the first character appears in UIA. A verified
                // native probe succeeded with 25ms between Unicode scalars.
                // Pace the initial characters, then use bounded chunks for
                // long text so the isolated worker stays within its deadline.
                var selection = inputs.GetRange(0, 4).ToArray();
                int inputSize = Marshal.SizeOf<KeyboardEventInput>();
                inserted = SendKeyboardInput((uint)selection.Length, selection, inputSize);
                if (inserted != selection.Length)
                    return new(WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT, EffectUnknown: true,
                        FailureStage: WindowFailureStage.DISPATCH_INPUT,
                        Detail: $"TEXT_SELECT_INSERTED_{inserted}", ElapsedMs: clock.ElapsedMilliseconds,
                        ExecutionMethod: WindowExecutionMethod.WIN32_SENDINPUT);
                Thread.Sleep(100);
                for (int offset = 0; offset < request.Value.Length;)
                {
                    if (GetForegroundWindow() != hwnd || !target.Current.HasKeyboardFocus)
                        return new(WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT, EffectUnknown: true,
                            FailureStage: WindowFailureStage.DISPATCH_INPUT,
                            Detail: "FOCUS_LOST_DURING_TEXT", ElapsedMs: clock.ElapsedMilliseconds,
                            ExecutionMethod: WindowExecutionMethod.WIN32_SENDINPUT);
                    // KEYEVENTF_UNICODE uses UTF-16; keep surrogate pairs in
                    // the same SendInput call so no half-character is emitted.
                    int units = ChromeChunkUnits(request.Value, offset);
                    var chunk = inputs.GetRange(4 + offset * 2, units * 2).ToArray();
                    uint accepted = SendKeyboardInput((uint)chunk.Length, chunk, inputSize);
                    inserted += accepted;
                    if (accepted != chunk.Length)
                        return new(WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT, EffectUnknown: true,
                            FailureStage: WindowFailureStage.DISPATCH_INPUT,
                            Detail: $"TEXT_CHUNK_INSERTED_{accepted}_OF_{chunk.Length}",
                            ElapsedMs: clock.ElapsedMilliseconds,
                            ExecutionMethod: WindowExecutionMethod.WIN32_SENDINPUT);
                    offset += units;
                    Thread.Sleep(25);
                }
            }
            else
                inserted = SendKeyboardInput((uint)inputs.Count, [.. inputs],
                    Marshal.SizeOf<KeyboardEventInput>());
            bool changed = foregroundAtEntry != GetForegroundWindow();
            return inserted == inputs.Count && GetForegroundWindow() == hwnd
                ? new(WindowActionCode.COMPLETED, ForegroundChanged: changed,
                    TargetActivated: foregroundAtEntry != hwnd, ElapsedMs: clock.ElapsedMilliseconds,
                    ExecutionMethod: WindowExecutionMethod.WIN32_SENDINPUT)
                : new(WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT, EffectUnknown: true,
                    ForegroundChanged: changed, FailureStage: WindowFailureStage.DISPATCH_INPUT,
                    Detail: $"TEXT_INPUTS_INSERTED_{inserted}:WIN32_{Marshal.GetLastPInvokeError()}",
                    ElapsedMs: clock.ElapsedMilliseconds,
                    ExecutionMethod: WindowExecutionMethod.WIN32_SENDINPUT);
        }
        catch (Exception e) when (e is COMException or ElementNotAvailableException or InvalidOperationException)
        {
            return new(dispatched ? WindowActionCode.INPUT_FAILED_UNKNOWN_EFFECT :
                WindowActionCode.FOCUS_UNAVAILABLE, EffectUnknown: dispatched,
                FailureStage: dispatched ? WindowFailureStage.DISPATCH_INPUT : WindowFailureStage.PREPARE_FOCUS,
                ExceptionType: e.GetType().Name, HResult: e.HResult.ToString("X8"),
                ElapsedMs: clock.ElapsedMilliseconds,
                ExecutionMethod: dispatched ? WindowExecutionMethod.WIN32_SENDINPUT : null);
        }
    }

    private static bool HitsTarget(IntPtr hwnd, AutomationElement target, Point pixel)
    {
        // UIA and WindowFromPoint may report the covered child even while a
        // separate topmost HWND physically occupies the same screen point.
        // A native pointer must never pass through such an overlay.
        IntPtr above = GetWindow(hwnd, PreviousZWindow);
        for (int i = 0; above != IntPtr.Zero && i < 256; i++)
        {
            if (IsWindowVisible(above) && !IsIconic(above) &&
                GetWindowRect(above, out var bounds) &&
                pixel.X >= bounds.Left && pixel.X < bounds.Right &&
                pixel.Y >= bounds.Top && pixel.Y < bounds.Bottom)
                return false;
            IntPtr next = GetWindow(above, PreviousZWindow);
            if (next == above) return false;
            above = next;
        }
        if (above != IntPtr.Zero) return false;
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
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
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
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rectangle);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputs, [In] Input[] input, int size);
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendKeyboardInput(uint inputs, [In] KeyboardEventInput[] input, int size);
}
