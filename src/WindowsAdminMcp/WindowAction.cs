using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Automation;

[JsonConverter(typeof(JsonStringEnumConverter<WindowActionKind>))]
public enum WindowActionKind { INVOKE, SET_VALUE, SELECT, TOGGLE, EXPAND, COLLAPSE, CLICK, KEY_PRESS, TYPE_TEXT, SCROLL, HOVER, DRAG, DOUBLE_CLICK, RIGHT_CLICK, SET_FILES }

[JsonConverter(typeof(JsonStringEnumConverter<WindowScrollDirection>))]
public enum WindowScrollDirection { UP, DOWN, LEFT, RIGHT }

[JsonConverter(typeof(JsonStringEnumConverter<WindowKey>))]
public enum WindowKey { ENTER, SPACE, ESCAPE }

[JsonConverter(typeof(JsonStringEnumConverter<WindowExpectedChange>))]
public enum WindowExpectedChange { APPEARS, DISAPPEARS }

[JsonConverter(typeof(JsonStringEnumConverter<WindowExpectedKind>))]
public enum WindowExpectedKind { UIA_ELEMENT, UIA_VALUE, UIA_ENABLED, WINDOW_TITLE, PROCESS_NAME, WINDOW_CREATED, PROCESS_STARTED }

[JsonConverter(typeof(JsonStringEnumConverter<WindowObservationState>))]
public enum WindowObservationState { NOT_REQUESTED, OBSERVED, NOT_OBSERVED, UNKNOWN }

[JsonConverter(typeof(JsonStringEnumConverter<WindowEffectState>))]
public enum WindowEffectState { NO_EFFECT, EFFECT_VERIFIED, EFFECT_UNKNOWN }

[JsonConverter(typeof(JsonStringEnumConverter<WindowEffectEvidence>))]
public enum WindowEffectEvidence
{
    NOT_DISPATCHED, PROVIDER_ACK_ONLY, POSTCONDITION_NOT_OBSERVED,
    POSTCONDITION_VERIFIED, DISPATCH_OUTCOME_UNKNOWN
}

[JsonConverter(typeof(JsonStringEnumConverter<WindowNextStep>))]
public enum WindowNextStep
{
    NONE, INSPECT_AND_SUPPLY_POSTCONDITION, INSPECT_STATE_DO_NOT_RETRY,
    REFRESH_ELEMENT, CHECK_CHROME_CONNECTION, CHANGE_INPUT_MODE,
    FIX_ARGUMENTS
}

[JsonConverter(typeof(JsonStringEnumConverter<WindowFocusBehavior>))]
public enum WindowFocusBehavior
{
    NOT_APPLICABLE, CDP_NO_NATIVE_ACTIVATION, UIA_PROVIDER_MAY_ACTIVATE,
    EXPLICIT_FOREGROUND_INPUT, UNKNOWN_METHOD_MAY_ACTIVATE
}

[JsonConverter(typeof(JsonStringEnumConverter<WindowExecutionMethod>))]
public enum WindowExecutionMethod { UIA_PATTERN, WIN32_SENDINPUT, CHROME_CDP }

// BACKGROUND_ONLY forbids our own foreground activation and OS input, but an
// arbitrary UIA provider can still activate its window. CDP_ONLY forbids UIA
// action dispatch too; it is available only for consented Chrome web targets.
[JsonConverter(typeof(JsonStringEnumConverter<WindowInputMode>))]
public enum WindowInputMode { BACKGROUND_ONLY, ALLOW_FOREGROUND_INPUT, CDP_ONLY }

[JsonConverter(typeof(JsonStringEnumConverter<WindowProviderOutcome>))]
public enum WindowProviderOutcome { NOT_DISPATCHED, ACKNOWLEDGED, REJECTED, UNKNOWN }

[JsonConverter(typeof(JsonStringEnumConverter<WindowFailureStage>))]
public enum WindowFailureStage { RESOLVE_ELEMENT, PREPARE_UIA, DISPATCH_UIA, ACTIVATE_WINDOW, PREPARE_FOCUS, HIT_TEST, DISPATCH_INPUT, VERIFY_EFFECT, WORKER, CDP_CONNECT, CDP_TARGET }

[JsonConverter(typeof(JsonStringEnumConverter<WindowActionCode>))]
public enum WindowActionCode
{
    COMPLETED, INVALID_ARGUMENT, CONFIRMATION_REQUIRED, SERVICE_UNAVAILABLE,
    USER_CONTEXT_UNAVAILABLE, DESKTOP_UNAVAILABLE, STALE_WINDOW, STALE_ELEMENT,
    ACCESS_DENIED, DISABLED, PASSWORD_DENIED, UNSUPPORTED_ACTION,
    UIA_UNAVAILABLE, AUDIT_UNAVAILABLE, AUDIT_RESULT_FAILED,
    TIMEOUT_UNKNOWN_EFFECT, ACTION_FAILED_UNKNOWN_EFFECT, WORKER_FAILED_UNKNOWN_EFFECT,
    UIA_ELEMENTNOTENABLED_UNKNOWN_EFFECT,
    EXPECTED_STATE_ALREADY_TRUE, WINDOW_NOT_VISIBLE, FOREGROUND_UNAVAILABLE,
    FOREGROUND_INPUT_DISALLOWED,
    CDP_ONLY_UNAVAILABLE,
    CLICK_POINT_UNAVAILABLE, TARGET_OCCLUDED, INPUT_FAILED_UNKNOWN_EFFECT,
    FOCUS_UNAVAILABLE, CDP_UNAVAILABLE, CDP_TARGET_AMBIGUOUS, CDP_CONTEXT_MISMATCH,
    CDP_TARGET_NOT_VISIBLE, CDP_FAILED_UNKNOWN_EFFECT, CDP_CONSENT_PENDING,
    LOCATOR_NOT_FOUND, LOCATOR_AMBIGUOUS, LOCATOR_RESOLVED,
    LOCATOR_TIMEOUT, LOCATOR_WORKER_FAILED,
    EFFECT_VERIFIED_PROVIDER_REJECTED
}

internal sealed record WindowActionRequest(
    [property: JsonPropertyName("window_id")] string WindowId,
    [property: JsonPropertyName("element_id")] string ElementId,
    [property: JsonPropertyName("action")] WindowActionKind Action,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("element_path")] string? ElementPath = null,
    [property: JsonPropertyName("expected_name")] string? ExpectedName = null,
    [property: JsonPropertyName("expected_change")] WindowExpectedChange? ExpectedChange = null,
    [property: JsonPropertyName("key")] WindowKey? Key = null,
    [property: JsonPropertyName("expected_kind")] WindowExpectedKind ExpectedKind = WindowExpectedKind.UIA_ELEMENT,
    [property: JsonPropertyName("expected_value")] string? ExpectedValue = null,
    [property: JsonPropertyName("expected_profile")] string? ExpectedProfile = null,
    [property: JsonPropertyName("expected_origin")] string? ExpectedOrigin = null,
    [property: JsonPropertyName("expected_url")] string? ExpectedUrl = null,
    [property: JsonPropertyName("observation_timeout_ms")] int ObservationTimeoutMs = 8000,
    [property: JsonPropertyName("expected_enabled_name")] string? ExpectedEnabledName = null,
    [property: JsonPropertyName("expected_enabled")] bool ExpectedEnabled = true,
    [property: JsonPropertyName("virtual_key")] int? VirtualKey = null,
    [property: JsonPropertyName("ctrl")] bool Ctrl = false,
    [property: JsonPropertyName("alt")] bool Alt = false,
    [property: JsonPropertyName("shift")] bool Shift = false,
    [property: JsonPropertyName("win")] bool Win = false,
    [property: JsonPropertyName("scroll_direction")] WindowScrollDirection? ScrollDirection = null,
    [property: JsonPropertyName("scroll_units")] int ScrollUnits = 1,
    [property: JsonPropertyName("target_x")] int? TargetX = null,
    [property: JsonPropertyName("target_y")] int? TargetY = null,
    [property: JsonPropertyName("locator_automation_id")] string? LocatorAutomationId = null,
    [property: JsonPropertyName("locator_name")] string? LocatorName = null,
    [property: JsonPropertyName("locator_role")] WindowRoleFilter? LocatorRole = null,
    [property: JsonPropertyName("resolve_only")] bool ResolveOnly = false,
    [property: JsonPropertyName("input_mode")] WindowInputMode InputMode = WindowInputMode.BACKGROUND_ONLY,
    [property: JsonPropertyName("target_element_id")] string? TargetElementId = null,
    [property: JsonPropertyName("file_paths")] string[]? FilePaths = null,
    [property: JsonPropertyName("browser_window_id")] int? BrowserWindowId = null);

public sealed record WindowActionResult(
    [property: JsonPropertyName("status")] WindowActionCode Status,
    [property: JsonPropertyName("audit_id")] string? AuditId = null,
    [property: JsonPropertyName("effect_unknown")] bool EffectUnknown = false,
    [property: JsonPropertyName("foreground_changed")] bool? ForegroundChanged = null,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("elapsed_ms")] long? ElapsedMs = null,
    [property: JsonPropertyName("target_activated")] bool? TargetActivated = null,
    [property: JsonPropertyName("target_verified")] bool TargetVerified = false,
    [property: JsonPropertyName("target_identity")] string? TargetIdentity = null,
    [property: JsonPropertyName("expected_state_observed")] bool? ExpectedStateObserved = null,
    [property: JsonPropertyName("failure_stage")] WindowFailureStage? FailureStage = null,
    [property: JsonPropertyName("exception_type")] string? ExceptionType = null,
    [property: JsonPropertyName("hresult")] string? HResult = null,
    [property: JsonPropertyName("provider_response")] string? ProviderResponse = null,
    [property: JsonPropertyName("observation_state")] WindowObservationState ObservationState = WindowObservationState.NOT_REQUESTED,
    [property: JsonPropertyName("expected_enabled_observed")] bool? ExpectedEnabledObserved = null,
    [property: JsonPropertyName("execution_method"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
        WindowExecutionMethod? ExecutionMethod = null,
    [property: JsonPropertyName("enabled_before"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
        bool? EnabledBefore = null,
    [property: JsonPropertyName("broker_owner_pid"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
        int? BrokerOwnerPid = null,
    [property: JsonPropertyName("trace_id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
        string? TraceId = null,
    [property: JsonPropertyName("session_generation"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
        string? SessionGeneration = null,
    [property: JsonPropertyName("resolved_element_id"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
        string? ResolvedElementId = null,
    [property: JsonPropertyName("resolved_element_path"), JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
        string? ResolvedElementPath = null,
    [property: JsonPropertyName("selected_file_count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? SelectedFileCount = null)
{
    [JsonPropertyName("safe_to_retry")]
    public bool SafeToRetry => EffectState==WindowEffectState.NO_EFFECT &&
        Status is WindowActionCode.INVALID_ARGUMENT or WindowActionCode.CONFIRMATION_REQUIRED or
            WindowActionCode.STALE_WINDOW or WindowActionCode.STALE_ELEMENT or
            WindowActionCode.LOCATOR_NOT_FOUND or WindowActionCode.LOCATOR_AMBIGUOUS or
            WindowActionCode.LOCATOR_TIMEOUT or WindowActionCode.LOCATOR_WORKER_FAILED or
            WindowActionCode.AUDIT_UNAVAILABLE or WindowActionCode.CDP_ONLY_UNAVAILABLE or
            WindowActionCode.FOREGROUND_INPUT_DISALLOWED;

    // A successful provider call is dispatch acknowledgement, NOT proof that the UI changed.
    [JsonPropertyName("effect_state")]
    public WindowEffectState EffectState => ExpectedStateObserved == true
        ? WindowEffectState.EFFECT_VERIFIED
        : EffectUnknown || Status == WindowActionCode.COMPLETED
            ? WindowEffectState.EFFECT_UNKNOWN : WindowEffectState.NO_EFFECT;

    [JsonPropertyName("provider_outcome")]
    public WindowProviderOutcome ProviderOutcome =>
        ExecutionMethod is null ? WindowProviderOutcome.NOT_DISPATCHED :
        FailureStage == WindowFailureStage.DISPATCH_UIA && HResult is not null
            ? WindowProviderOutcome.REJECTED :
        Status == WindowActionCode.COMPLETED ? WindowProviderOutcome.ACKNOWLEDGED :
        WindowProviderOutcome.UNKNOWN;

    // A provider acknowledgement and the absence of an observed foreground
    // change are both narrower than a proven UI transition / focus guarantee.
    [JsonPropertyName("effect_evidence")]
    public WindowEffectEvidence EffectEvidence => ExpectedStateObserved switch
    {
        true => WindowEffectEvidence.POSTCONDITION_VERIFIED,
        false => WindowEffectEvidence.POSTCONDITION_NOT_OBSERVED,
        _ when Status == WindowActionCode.COMPLETED => WindowEffectEvidence.PROVIDER_ACK_ONLY,
        _ when EffectState == WindowEffectState.EFFECT_UNKNOWN => WindowEffectEvidence.DISPATCH_OUTCOME_UNKNOWN,
        _ => WindowEffectEvidence.NOT_DISPATCHED
    };

    [JsonPropertyName("next_step")]
    public WindowNextStep NextStep => EffectState == WindowEffectState.EFFECT_UNKNOWN
        ? EffectEvidence == WindowEffectEvidence.PROVIDER_ACK_ONLY
            ? WindowNextStep.INSPECT_AND_SUPPLY_POSTCONDITION
            : WindowNextStep.INSPECT_STATE_DO_NOT_RETRY
        : Status switch
        {
            WindowActionCode.STALE_ELEMENT or WindowActionCode.STALE_WINDOW or
                WindowActionCode.CDP_TARGET_NOT_VISIBLE or WindowActionCode.TARGET_OCCLUDED or
                WindowActionCode.LOCATOR_NOT_FOUND or WindowActionCode.LOCATOR_AMBIGUOUS =>
                WindowNextStep.REFRESH_ELEMENT,
            WindowActionCode.CDP_UNAVAILABLE or WindowActionCode.CDP_CONSENT_PENDING =>
                WindowNextStep.CHECK_CHROME_CONNECTION,
            WindowActionCode.CDP_ONLY_UNAVAILABLE or WindowActionCode.FOREGROUND_INPUT_DISALLOWED =>
                WindowNextStep.CHANGE_INPUT_MODE,
            WindowActionCode.INVALID_ARGUMENT => WindowNextStep.FIX_ARGUMENTS,
            _ => WindowNextStep.NONE
        };

    [JsonPropertyName("focus_behavior")]
    public WindowFocusBehavior FocusBehavior => ExecutionMethod switch
    {
        WindowExecutionMethod.UIA_PATTERN => WindowFocusBehavior.UIA_PROVIDER_MAY_ACTIVATE,
        WindowExecutionMethod.CHROME_CDP => WindowFocusBehavior.CDP_NO_NATIVE_ACTIVATION,
        WindowExecutionMethod.WIN32_SENDINPUT => WindowFocusBehavior.EXPLICIT_FOREGROUND_INPUT,
        _ when EffectState == WindowEffectState.EFFECT_UNKNOWN =>
            WindowFocusBehavior.UNKNOWN_METHOD_MAY_ACTIVATE,
        _ => WindowFocusBehavior.NOT_APPLICABLE
    };
}

// The disposable child uses native UIA2 with AutoSetFocus disabled. An untrusted
// provider can still change focus on its own; report the observed result.
internal static class WindowAction
{
    internal static bool Valid(WindowActionRequest request) => ValidCore(request, requireFresh: true);

    internal static WindowActionCode InvalidCode(WindowActionRequest request) =>
        ValidCore(request, requireFresh: false) ? WindowActionCode.STALE_ELEMENT :
            WindowActionCode.INVALID_ARGUMENT;

    private static bool ValidCore(WindowActionRequest request, bool requireFresh)
    {
        bool uiaRef = requireFresh ? WindowInspector.TryParseElementRef(request.ElementId, out _) :
            WindowInspector.WellFormedElementRef(request.ElementId, out _);
        bool tabRef = requireFresh ? WindowChromeCdp.ValidTabElementRef(request.ElementId) :
            WindowChromeCdp.WellFormedTabElementRef(request.ElementId);
        bool locator=request.LocatorAutomationId is not null || request.LocatorName is not null ||
            request.LocatorRole is not null;
        return
        WindowCatalog.TryParseId(request.WindowId, out _) &&
        WindowChromeCdp.ContextArgumentError(request.ExpectedProfile, request.ExpectedOrigin,
            request.ExpectedUrl) is null &&
        (!tabRef || request.ExpectedProfile is not null) &&
        (tabRef ||
            request.ExpectedOrigin is null && request.ExpectedUrl is null) &&
        (uiaRef || tabRef || locator) &&
        (request.BrowserWindowId is null || request.BrowserWindowId > 0 && tabRef &&
            request.InputMode == WindowInputMode.CDP_ONLY &&
            request.ExpectedProfile is not null &&
            (request.ExpectedOrigin is not null || request.ExpectedUrl is not null)) &&
        (!request.ResolveOnly || locator) &&
        (locator ? request.ElementId=="locator" &&
            request.LocatorAutomationId is null or {Length:>=1 and <=128} &&
            request.LocatorName is null or {Length:>=1 and <=128} &&
            (request.LocatorAutomationId is null || !string.IsNullOrWhiteSpace(request.LocatorAutomationId) &&
                !request.LocatorAutomationId.Contains('\0')) &&
            (request.LocatorName is null || !string.IsNullOrWhiteSpace(request.LocatorName) &&
                !request.LocatorName.Contains('\0')) &&
            (request.LocatorRole is null || Enum.IsDefined(request.LocatorRole.Value)) &&
            request.ExpectedOrigin is null && request.ExpectedUrl is null :
            request.LocatorAutomationId is null && request.LocatorName is null && request.LocatorRole is null) &&
        (request.ElementPath is null || uiaRef &&
         WindowInspector.TryParsePath(request.ElementPath, out _)) &&
        Enum.IsDefined(request.Action) &&
        Enum.IsDefined(request.InputMode) &&
        (request.Action==WindowActionKind.SCROLL ? request.ScrollDirection is not null &&
            Enum.IsDefined(request.ScrollDirection.Value) && request.ScrollUnits is >=1 and <=10 :
            request.ScrollDirection is null && request.ScrollUnits==1) &&
        (request.Action == WindowActionKind.DRAG ?
            (tabRef && request.InputMode == WindowInputMode.CDP_ONLY &&
                request.TargetX is null && request.TargetY is null &&
                (requireFresh ? WindowChromeCdp.ValidTabElementRef(request.TargetElementId) :
                    WindowChromeCdp.WellFormedTabElementRef(request.TargetElementId)) ||
             request.TargetElementId is null && request.TargetX.HasValue && request.TargetY.HasValue &&
                request.InputMode != WindowInputMode.CDP_ONLY) :
            request.TargetX is null && request.TargetY is null && request.TargetElementId is null) &&
        (request.Action == WindowActionKind.SET_FILES ?
            tabRef && request.InputMode == WindowInputMode.CDP_ONLY &&
            request.FilePaths is { Length: >= 1 and <= 16 } &&
            request.FilePaths.All(p => p is { Length: >= 3 and <= 1024 } &&
                !p.Contains('\0') && Path.IsPathFullyQualified(p)) :
            request.FilePaths is null) &&
        Enum.IsDefined(request.ExpectedKind) &&
        request.ObservationTimeoutMs is >= 100 and <= 15000 &&
        (request.ExpectedKind is not (WindowExpectedKind.WINDOW_CREATED or WindowExpectedKind.PROCESS_STARTED) ||
            request.ExpectedName is not null && request.ExpectedChange == WindowExpectedChange.APPEARS) &&
        (request.ExpectedKind == WindowExpectedKind.UIA_VALUE ?
            request.ExpectedValue is { Length: <= 4096 } &&
            !request.ExpectedValue.Contains('\0') && request.ExpectedName is not null :
            request.ExpectedValue is null) &&
        (request.ExpectedKind == WindowExpectedKind.UIA_ELEMENT || request.ExpectedName is not null) &&
        (request.ExpectedName is null && request.ExpectedChange is null ||
         request.ExpectedName is { Length: >= 1 and <= 128 } &&
         !string.IsNullOrWhiteSpace(request.ExpectedName) &&
         !request.ExpectedName.Contains('\0') &&
         request.ExpectedChange is not null && Enum.IsDefined(request.ExpectedChange.Value)) &&
        (request.ExpectedEnabledName is null ? request.ExpectedEnabled :
            request.ExpectedEnabledName.Length is >= 1 and <= 128 &&
            !string.IsNullOrWhiteSpace(request.ExpectedEnabledName) &&
            !request.ExpectedEnabledName.Contains('\0')) &&
        (request.Action == WindowActionKind.KEY_PRESS ?
            (request.Key is not null && Enum.IsDefined(request.Key.Value) && request.VirtualKey is null ||
             request.Key is null && request.VirtualKey is >= 1 and <= 254) :
            request.Key is null && request.VirtualKey is null && !request.Ctrl && !request.Alt && !request.Shift && !request.Win) &&
        (request.Action is WindowActionKind.SET_VALUE or WindowActionKind.TYPE_TEXT ?
            request.Value is { Length: <= 4096 } && request.Value?.Contains('\0') == false :
            request.Value is null);
    }

    internal static WindowActionResult InvokeInWorker(WindowActionRequest request)
    {
        if (!Valid(request)) return new(InvalidCode(request));
        using var self = Process.GetCurrentProcess();
        var desktop = DesktopCapture.ObserveDesktop(self.SessionId);
        if (desktop.Code != CaptureCode.COMPLETED)
            return new(WindowActionCode.DESKTOP_UNAVAILABLE, Detail: desktop.Code.ToString());
        var resolved = WindowCatalog.ResolveId(request.WindowId, self.SessionId, out var hwnd);
        if (resolved != WindowResolveCode.OK)
            return new(resolved == WindowResolveCode.ACCESS_DENIED ? WindowActionCode.ACCESS_DENIED :
                resolved == WindowResolveCode.WRONG_SESSION ? WindowActionCode.USER_CONTEXT_UNAVAILABLE :
                WindowActionCode.STALE_WINDOW, Detail: resolved.ToString());
        return request.ElementId=="locator"?InvokeLocator(hwnd,request):InvokeCore(hwnd, request);
    }

    // Resolve exactly one semantic UIA locator *inside* the disposable action
    // worker. No old snapshot, ambiguous best-effort click or retry is used.
    private static WindowActionResult InvokeLocator(IntPtr hwnd,WindowActionRequest request)
    {
        try
        {
            if(request.ExpectedProfile is not null &&
                !WindowChromeCdp.VerifiedChromeProfile(hwnd,request.ExpectedProfile,
                    out var profileFailure))
                return new(WindowActionCode.CDP_CONTEXT_MISMATCH,
                    FailureStage:WindowFailureStage.CDP_TARGET,
                    Detail:$"PROFILE_{profileFailure}");
            var root=AutomationElement.FromHandle(hwnd);
            if(root is null)return new(WindowActionCode.UIA_UNAVAILABLE);
            var walker=TreeWalker.ControlViewWalker;
            var pending=new Stack<(AutomationElement Element,int Depth,int[] Path)>();
            pending.Push((root,0,[]));
            int traversed=0;
            WindowActionRequest? match=null;
            while(pending.Count>0 && traversed<WindowInspector.MaxTraversal)
            {
                var (element,depth,path)=pending.Pop();
                traversed++;
                AutomationElement.AutomationElementInformation current;
                try{current=element.Current;}catch(ElementNotAvailableException){continue;}
                bool role=request.LocatorRole is null ||
                    WindowInspector.MatchesRole(current.ControlType,request.LocatorRole.Value);
                if(role &&
                    (request.LocatorAutomationId is null || current.AutomationId==request.LocatorAutomationId) &&
                    (request.LocatorName is null || current.Name==request.LocatorName))
                {
                    if(match is not null)return new(WindowActionCode.LOCATOR_AMBIGUOUS,
                        Detail:"MULTIPLE_MATCHES");
                    long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    match=request with {ElementId=WindowInspector.ElementRef(element,request.WindowId,now),
                        ElementPath="0"+string.Concat(path.Select(i=>"."+i)),
                        LocatorAutomationId=null,LocatorName=null,LocatorRole=null};
                }
                if(depth>=WindowInspector.MaxDepth)continue;
                var children=new List<AutomationElement>();
                try
                {
                    var child=walker.GetFirstChild(element);
                    while(child is not null && children.Count<WindowInspector.MaxTraversal)
                    {children.Add(child);child=walker.GetNextSibling(child);}
                }
                catch(ElementNotAvailableException){continue;}
                for(int i=children.Count-1;i>=0;i--)
                    pending.Push((children[i],depth+1,[..path,i]));
            }
            if(pending.Count>0)return new(WindowActionCode.LOCATOR_AMBIGUOUS,Detail:"TRAVERSAL_LIMIT");
            if(match is null)return new(WindowActionCode.LOCATOR_NOT_FOUND);
            return request.ResolveOnly
                ? new(WindowActionCode.LOCATOR_RESOLVED,
                    ResolvedElementId: match.ElementId, ResolvedElementPath: match.ElementPath)
                : InvokeCore(hwnd,match);
        }
        catch(ElementNotAvailableException){return new(WindowActionCode.STALE_ELEMENT);}
        catch(COMException e){return new(WindowActionCode.UIA_UNAVAILABLE,
            ExceptionType:e.GetType().Name,HResult:e.HResult.ToString("X8"));}
    }

    internal static WindowActionResult InvokeCore(IntPtr hwnd, WindowActionRequest request)
    {
        var clock = Stopwatch.StartNew();
        if (hwnd == IntPtr.Zero || !Valid(request))
            return new(hwnd == IntPtr.Zero ? WindowActionCode.INVALID_ARGUMENT : InvalidCode(request),
                ElapsedMs: clock.ElapsedMilliseconds);
        if (WindowChromeCdp.WellFormedTabElementRef(request.ElementId))
        {
            if (!WindowChromeCdp.VerifiedChromeProfile(hwnd, request.ExpectedProfile!,
                out var profileFailure))
                return new(WindowActionCode.CDP_CONTEXT_MISMATCH,
                    Detail: $"PROFILE_{profileFailure}",
                    FailureStage: WindowFailureStage.CDP_TARGET,
                    ElapsedMs: clock.ElapsedMilliseconds);
            return WindowChromeCdp.InvokeTab(hwnd, request, clock);
        }
        if (request.ExpectedProfile is not null &&
            !WindowChromeCdp.VerifiedChromeProfile(hwnd, request.ExpectedProfile,
                out var nonTabProfileFailure))
            return new(WindowActionCode.CDP_CONTEXT_MISMATCH,
                FailureStage: WindowFailureStage.CDP_TARGET,
                Detail: $"PROFILE_{nonTabProfileFailure}");
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return new(WindowActionCode.UIA_UNAVAILABLE);
            var walker = TreeWalker.ControlViewWalker;
            WindowActionResult ActOnMatch(AutomationElement element, int[] path)
            {
                var current = element.Current;
                if (!current.IsEnabled)
                    return new(WindowActionCode.DISABLED, EnabledBefore: false,
                        TargetVerified: true, TargetIdentity: request.ElementId,
                        FailureStage: WindowFailureStage.PREPARE_UIA,
                        ElapsedMs: clock.ElapsedMilliseconds);
                // Creation checks compare identities, not a boolean "process exists":
                // another Camoufox instance may already be running.
                WindowEffectBaseline baseline;
                try { baseline = WindowEffectObserver.Capture(request); }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or
                                          UnauthorizedAccessException or System.Security.SecurityException)
                {
                    return new(WindowActionCode.UIA_UNAVAILABLE,
                        FailureStage: WindowFailureStage.VERIFY_EFFECT, ExceptionType: e.GetType().Name);
                }
                if (request.ExpectedName is not null &&
                    request.ExpectedKind is not (WindowExpectedKind.PROCESS_STARTED or WindowExpectedKind.WINDOW_CREATED))
                {
                    bool before = ExpectedStatePresent(root, request);
                    bool desired = request.ExpectedChange == WindowExpectedChange.APPEARS;
                    if (before == desired && (request.ExpectedEnabledName is null ||
                        WindowEffectObserver.EnabledState(request.WindowId,
                            request.ExpectedEnabledName, request.ExpectedEnabled)))
                        return new(WindowActionCode.EXPECTED_STATE_ALREADY_TRUE,
                            ElapsedMs: clock.ElapsedMilliseconds);
                }
                var semanticClick = request.Action == WindowActionKind.CLICK
                    ? SemanticClickAction(element, current.ControlType) : null;
                var effectiveAction = semanticClick ?? request.Action;
                bool expansionObservable = (effectiveAction is WindowActionKind.EXPAND or WindowActionKind.COLLAPSE) &&
                    element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _);
                if (expansionObservable && TargetExpanded(hwnd, path, element.GetRuntimeId(), effectiveAction))
                    return new(WindowActionCode.EXPECTED_STATE_ALREADY_TRUE,
                        ElapsedMs: clock.ElapsedMilliseconds);
                bool radioInvoke = request.Action == WindowActionKind.SELECT &&
                    current.ControlType == ControlType.RadioButton &&
                    element.TryGetCurrentPattern(InvokePattern.Pattern, out _);
                bool selectionObservable = effectiveAction == WindowActionKind.SELECT &&
                    element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _);
                bool valueObservable = (request.Action is WindowActionKind.SET_VALUE or WindowActionKind.TYPE_TEXT) &&
                    element.TryGetCurrentPattern(ValuePattern.Pattern, out _);
                WindowActionResult ClickWithNativeFallback()
                {
                    // Browser document buttons may advertise InvokePattern but
                    // reject Invoke with UIA_E_ELEMENTNOTENABLED even while they
                    // are visibly enabled. Prefer the existing CDP/verified real
                    // pointer path for web Invoke controls. If no input was sent,
                    // native UIA remains a background-capable fallback.
                    if (semanticClick == WindowActionKind.INVOKE && PreferPhysicalWebClick(semanticClick,
                        WindowChromeCdp.IsInsideDocument(element)))
                    {
                        if (request.InputMode == WindowInputMode.BACKGROUND_ONLY)
                        {
                            var background = WindowChromeCdp.TryDispatch(hwnd, element, request, clock);
                            if (!WindowPhysicalClick.CanUseNativeFallback(background)) return background!;
                            return WindowActionNoFocus.Invoke(hwnd, path, element.GetRuntimeId(),
                                request with { Action = WindowActionKind.INVOKE }, clock);
                        }
                        var physical = WindowPhysicalClick.Click(hwnd, element, request, clock);
                        if (!CanFallbackToSemanticAfterPhysical(physical)) return physical;
                        return WindowActionNoFocus.Invoke(hwnd, path, element.GetRuntimeId(),
                            request with { Action = WindowActionKind.INVOKE }, clock);
                    }
                    // Other controls use the native control pattern first. No
                    // alternate input method replays an uncertain provider result.
                    if (semanticClick is not null)
                    {
                        var native = WindowActionNoFocus.Invoke(hwnd, path, element.GetRuntimeId(),
                            request with { Action = semanticClick.Value }, clock);
                        // Never replay an action after it may have been delivered:
                        // even an HRESULT failure can follow a real browser click.
                        if (!CanFallbackAfterNativeClick(native)) return native;
                    }
                    return WindowPhysicalClick.Click(hwnd, element, request, clock);
                }
                WindowActionResult SetValueWithNativeFallback()
                {
                    var browser = WindowChromeCdp.TryDispatch(hwnd, element, request, clock);
                    if (!WindowPhysicalClick.CanUseNativeFallback(browser)) return browser!;
                    // A verified Chrome profile uses real keyboard events so
                    // React receives input/change, unlike provider-only ValuePattern.
                    if (request.ExpectedProfile is not null &&
                        request.InputMode == WindowInputMode.ALLOW_FOREGROUND_INPUT)
                    {
                        var typed = WindowPhysicalClick.TypeText(hwnd, element, request, clock,
                            allowCdp: false);
                        if (typed.Status is not (WindowActionCode.FOCUS_UNAVAILABLE or
                            WindowActionCode.FOREGROUND_UNAVAILABLE) || typed.EffectUnknown)
                            return typed;
                    }
                    return Pattern(element, current.ControlType, request.Action) is null
                        ? new(WindowActionCode.UNSUPPORTED_ACTION)
                        : WindowActionNoFocus.Invoke(hwnd, path, element.GetRuntimeId(), request,
                            clock, radioInvoke);
                }
                WindowActionResult CdpPointerWithNativeFallback()
                {
                    var background = WindowChromeCdp.TryDispatch(hwnd, element, request, clock);
                    if (!WindowPhysicalClick.CanUseNativeFallback(background)) return background!;
                    return request.Action == WindowActionKind.SCROLL
                        ? ScrollTarget(hwnd, element, request, clock)
                        : WindowPhysicalClick.Pointer(hwnd, element, request, clock);
                }
                // CDP_ONLY never dispatches a native UIA action or SendInput.
                // If no consented web target can be addressed, fail before
                // effect rather than silently falling back to an UIA provider.
                var result = request.InputMode == WindowInputMode.CDP_ONLY
                    ? CdpOnlyResult(WindowChromeCdp.TryDispatch(hwnd, element, request, clock), clock)
                    : request.Action == WindowActionKind.CLICK
                    ? ClickWithNativeFallback()
                    : request.Action is WindowActionKind.DOUBLE_CLICK or WindowActionKind.RIGHT_CLICK
                    ? WindowPhysicalClick.Click(hwnd,element,request,clock)
                    : (request.Action is WindowActionKind.HOVER or WindowActionKind.SCROLL) &&
                      WindowChromeCdp.IsInsideDocument(element)
                    ? CdpPointerWithNativeFallback()
                    : request.Action == WindowActionKind.SCROLL
                    ? ScrollTarget(hwnd,element,request,clock)
                    : request.Action is WindowActionKind.HOVER or WindowActionKind.DRAG
                    ? WindowPhysicalClick.Pointer(hwnd,element,request,clock)
                    : request.Action == WindowActionKind.KEY_PRESS
                    ? WindowPhysicalClick.PressKey(hwnd, element, request, clock)
                    : request.Action == WindowActionKind.TYPE_TEXT
                    ? WindowPhysicalClick.TypeText(hwnd, element, request, clock)
                    : request.Action == WindowActionKind.SET_VALUE
                    ? SetValueWithNativeFallback()
                    : Pattern(element, current.ControlType, request.Action) is null &&
                      WindowChromeCdp.PointerAction(request.Action)
                    ? WindowPhysicalClick.Click(hwnd, element,
                        request with { Action = WindowActionKind.CLICK }, clock)
                    : WindowActionNoFocus.Invoke(hwnd, path, element.GetRuntimeId(), request,
                        clock, radioInvoke);
                result = result with { TargetVerified = true, TargetIdentity = request.ElementId,
                    EnabledBefore = true };
                if ((request.Action is WindowActionKind.SET_VALUE or WindowActionKind.TYPE_TEXT) &&
                    (result.Status == WindowActionCode.COMPLETED || result.EffectUnknown))
                {
                    try
                    {
                        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) &&
                            value is ValuePattern pattern &&
                            string.Equals(pattern.Current.Value, request.Value, StringComparison.Ordinal))
                            result = ReconcileObserved(result, true);
                    }
                    catch (Exception e) when (e is COMException or ElementNotAvailableException or InvalidOperationException)
                    { result = result with { FailureStage = result.FailureStage ?? WindowFailureStage.VERIFY_EFFECT }; }
                }
                // Providers may report a failed Invoke *after* the UI changed.
                // Reconcile by observation without re-dispatching the action.
                if ((result.Status == WindowActionCode.COMPLETED || result.EffectUnknown) &&
                    (request.ExpectedName is not null || request.ExpectedEnabledName is not null ||
                     selectionObservable || expansionObservable || valueObservable))
                {
                    try
                    {
                        bool observed = false;
                        bool enabledObserved = false;
                        var verification = Stopwatch.StartNew();
                        do
                        {
                            bool primary = request.ExpectedName is not null
                                ? WindowEffectObserver.Check(request, baseline) ==
                                    (request.ExpectedChange == WindowExpectedChange.APPEARS)
                                : selectionObservable ? TargetSelected(hwnd, path, element.GetRuntimeId())
                                : expansionObservable ? TargetExpanded(hwnd, path, element.GetRuntimeId(), effectiveAction)
                                : valueObservable ? TargetValue(hwnd, path, element.GetRuntimeId(), request.Value!)
                                : true;
                            enabledObserved = request.ExpectedEnabledName is null ||
                                WindowEffectObserver.EnabledState(request.WindowId,
                                    request.ExpectedEnabledName, request.ExpectedEnabled);
                            observed = primary && enabledObserved;
                            if (observed) break;
                            Thread.Sleep(100);
                        } while (verification.ElapsedMilliseconds < request.ObservationTimeoutMs);
                        result = ReconcileObserved(result, observed) with
                        { ObservationState = observed ? WindowObservationState.OBSERVED : WindowObservationState.NOT_OBSERVED,
                          ExpectedEnabledObserved = request.ExpectedEnabledName is null ? null : enabledObserved };
                    }
                    catch (Exception e) when (e is COMException or ElementNotAvailableException or
                                              InvalidOperationException or Win32Exception or
                                              UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        result = result with { ExpectedStateObserved = null,
                            ObservationState = WindowObservationState.UNKNOWN,
                            FailureStage = result.FailureStage ?? WindowFailureStage.VERIFY_EFFECT,
                            ExceptionType = result.ExceptionType ?? e.GetType().Name,
                            HResult = result.HResult ?? e.HResult.ToString("X8") };
                    }
                }
                return RequireVerifiedActionEffect(request,
                    RequireVerifiedTextEffect(request, result, valueObservable));
            }
            WindowInspector.TryParseElementRef(request.ElementId, out long snapshotMs);
            if (request.ElementPath is not null)
            {
                WindowInspector.TryParsePath(request.ElementPath, out var path);
                var selected = WindowInspector.ResolvePath(root, walker, path);
                if (selected is null || !string.Equals(
                    WindowInspector.ElementRef(selected, request.WindowId, snapshotMs),
                    request.ElementId, StringComparison.Ordinal))
                    return new(WindowActionCode.STALE_ELEMENT,
                        FailureStage: WindowFailureStage.RESOLVE_ELEMENT,
                        ElapsedMs: clock.ElapsedMilliseconds);
                return ActOnMatch(selected, path);
            }
            var stack = new Stack<(AutomationElement Element, int Depth, int[] Path)>();
            stack.Push((root, 0, []));
            int inspected = 0;
            while (stack.Count > 0 && inspected < 100)
            {
                var (element, depth, path) = stack.Pop();
                inspected++;
                AutomationElement.AutomationElementInformation current;
                try { current = element.Current; }
                catch (ElementNotAvailableException) { continue; }
                string candidate;
                try { candidate = WindowInspector.ElementRef(element, request.WindowId, snapshotMs); }
                catch (ElementNotAvailableException) { continue; }
                if (string.Equals(candidate, request.ElementId, StringComparison.Ordinal))
                    return ActOnMatch(element, path);
                if (depth >= WindowInspector.MaxDepth) continue;
                var children = new List<AutomationElement>();
                try
                {
                    var child = walker.GetFirstChild(element);
                    // Match the first inspection page exactly. A caller can
                    // supply element_path for later pages instead of scanning
                    // an unbounded tree during an action.
                    while (child is not null && children.Count < 100)
                    {
                        children.Add(child);
                        child = walker.GetNextSibling(child);
                    }
                }
                catch (ElementNotAvailableException) { continue; }
                for (int i = children.Count - 1; i >= 0; i--)
                    stack.Push((children[i], depth + 1, [.. path, i]));
            }
            return new(WindowActionCode.STALE_ELEMENT,
                FailureStage: WindowFailureStage.RESOLVE_ELEMENT,
                ElapsedMs: clock.ElapsedMilliseconds);
        }
        catch (ElementNotAvailableException)
        {
            return new(WindowActionCode.STALE_ELEMENT,
                FailureStage: WindowFailureStage.RESOLVE_ELEMENT);
        }
        catch (COMException e) { return new(WindowActionCode.UIA_UNAVAILABLE, Detail: e.HResult.ToString("X8"),
            FailureStage: WindowFailureStage.RESOLVE_ELEMENT, ExceptionType: e.GetType().Name,
            HResult: e.HResult.ToString("X8")); }
        catch (InvalidOperationException e) { return new(WindowActionCode.UIA_UNAVAILABLE,
            FailureStage: WindowFailureStage.RESOLVE_ELEMENT, ExceptionType: e.GetType().Name,
            HResult: e.HResult.ToString("X8")); }
    }

    private static WindowActionResult ScrollTarget(IntPtr hwnd,AutomationElement element,
        WindowActionRequest request,Stopwatch clock)
    {
        try
        {
            if(element.TryGetCurrentPattern(ScrollPattern.Pattern,out var observed) && observed is ScrollPattern scroll)
            {
                bool vertical=request.ScrollDirection is WindowScrollDirection.UP or WindowScrollDirection.DOWN;
                if(vertical?!scroll.Current.VerticallyScrollable:!scroll.Current.HorizontallyScrollable)
                    return new(WindowActionCode.UNSUPPORTED_ACTION,ElapsedMs:clock.ElapsedMilliseconds);
                double before=vertical?scroll.Current.VerticalScrollPercent:scroll.Current.HorizontalScrollPercent;
                var amount=request.ScrollDirection is WindowScrollDirection.UP or WindowScrollDirection.LEFT
                    ? ScrollAmount.SmallDecrement:ScrollAmount.SmallIncrement;
                for(int i=0;i<request.ScrollUnits;i++)
                    scroll.Scroll(vertical?ScrollAmount.NoAmount:amount,vertical?amount:ScrollAmount.NoAmount);
                double after=vertical?scroll.Current.VerticalScrollPercent:scroll.Current.HorizontalScrollPercent;
                bool changed=before!=after;
                return new(WindowActionCode.COMPLETED,EffectUnknown:!changed,
                    ElapsedMs:clock.ElapsedMilliseconds,ExpectedStateObserved:changed,
                    ObservationState:changed?WindowObservationState.OBSERVED:WindowObservationState.NOT_OBSERVED,
                    ExecutionMethod:WindowExecutionMethod.UIA_PATTERN);
            }
            return WindowPhysicalClick.Scroll(hwnd,element,request,clock);
        }
        catch(Exception e) when(e is COMException or ElementNotAvailableException or InvalidOperationException)
        {
            return new(WindowActionCode.ACTION_FAILED_UNKNOWN_EFFECT,EffectUnknown:true,
                ElapsedMs:clock.ElapsedMilliseconds,FailureStage:WindowFailureStage.DISPATCH_UIA,
                ExceptionType:e.GetType().Name,HResult:e.HResult.ToString("X8"));
        }
    }

    private static bool ExpectedNamePresent(AutomationElement root, string name)
    {
        var condition = new PropertyCondition(AutomationElement.NameProperty, name);
        var found = root.FindFirst(TreeScope.Descendants, condition);
        return found is not null && !found.Current.IsOffscreen;
    }

    private static bool ExpectedValuePresent(AutomationElement root, string name, string value)
    {
        var controls = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, name));
        if (controls.Count != 1) return false;
        var element = controls[0];
        return !element.Current.IsPassword &&
            element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
            pattern is ValuePattern valuePattern &&
            string.Equals(valuePattern.Current.Value, value, StringComparison.Ordinal);
    }

    internal static bool ExpectedEnabledPresent(AutomationElement root, string name, bool enabled)
    {
        var controls = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, name));
        if (controls.Count != 1) return false;
        var control = controls[0].Current;
        return !control.IsOffscreen && !control.IsPassword && control.IsEnabled == enabled;
    }

    internal static bool ExpectedStatePresent(AutomationElement root, WindowActionRequest request)
    {
        string name = request.ExpectedName!;
        return request.ExpectedKind switch
        {
            WindowExpectedKind.UIA_ELEMENT => ExpectedNamePresent(root, name),
            WindowExpectedKind.UIA_VALUE => ExpectedValuePresent(root, name, request.ExpectedValue!),
            WindowExpectedKind.UIA_ENABLED => ExpectedEnabledPresent(root, name, true),
            WindowExpectedKind.WINDOW_TITLE => WindowTitlePresent(request.WindowId, name),
            WindowExpectedKind.PROCESS_NAME => ExternalStatePresent(request.ExpectedKind, name),
            _ => false
        };
    }

    private static bool TargetSelected(IntPtr hwnd, int[] path, int[] runtimeId)
    {
        var root = AutomationElement.FromHandle(hwnd);
        if (root is null) return false;
        var element = WindowInspector.ResolvePath(root, TreeWalker.ControlViewWalker, path);
        return element is not null && element.GetRuntimeId().SequenceEqual(runtimeId) &&
            element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selected) &&
            selected is SelectionItemPattern item && item.Current.IsSelected;
    }

    private static bool TargetExpanded(IntPtr hwnd, int[] path, int[] runtimeId, WindowActionKind action)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var element = root is null ? null : WindowInspector.ResolvePath(root, TreeWalker.ControlViewWalker, path);
        if (element is null || !element.GetRuntimeId().SequenceEqual(runtimeId) ||
            !element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expanded) ||
            expanded is not ExpandCollapsePattern pattern) return false;
        return pattern.Current.ExpandCollapseState == (action == WindowActionKind.EXPAND
            ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
    }

    private static bool TargetValue(IntPtr hwnd, int[] path, int[] runtimeId, string value)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var element = root is null ? null : WindowInspector.ResolvePath(root, TreeWalker.ControlViewWalker, path);
        return element is not null && element.GetRuntimeId().SequenceEqual(runtimeId) &&
            !element.Current.IsPassword &&
            element.TryGetCurrentPattern(ValuePattern.Pattern, out var current) &&
            current is ValuePattern pattern &&
            string.Equals(pattern.Current.Value, value, StringComparison.Ordinal);
    }

    internal static bool WindowTitleMatches(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.Ordinal) ||
        // Native browser titles append a separator and browser name after the
        // page title. Match the whole prefix, not an arbitrary substring.
        actual.StartsWith(expected + " — ", StringComparison.Ordinal) ||
        actual.StartsWith(expected + " - ", StringComparison.Ordinal);

    internal static bool WindowTitlePresent(string windowId, string expected)
    {
        using var current = Process.GetCurrentProcess();
        return WindowCatalog.TryReadTitle(windowId, current.SessionId, out var title) &&
            WindowTitleMatches(title, expected);
    }

    internal static bool ExternalStatePresent(WindowExpectedKind kind, string name)
    {
        if (kind != WindowExpectedKind.PROCESS_NAME) return false;
        using var current = Process.GetCurrentProcess();
        var matches = Process.GetProcessesByName(
            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);
        try { return matches.Any(p => p.SessionId == current.SessionId); }
        finally { foreach (var p in matches) p.Dispose(); }
    }

    // Preserve the original provider code and diagnostics even when the
    // requested transition is observed after a reported dispatch failure.
    internal static WindowActionResult ReconcileObserved(WindowActionResult result, bool observed) =>
        result with { Status = observed && result.Status is
                    (WindowActionCode.ACTION_FAILED_UNKNOWN_EFFECT or
                     WindowActionCode.UIA_ELEMENTNOTENABLED_UNKNOWN_EFFECT) &&
                result.FailureStage == WindowFailureStage.DISPATCH_UIA && result.HResult is not null
                ? WindowActionCode.EFFECT_VERIFIED_PROVIDER_REJECTED : result.Status,
            ExpectedStateObserved = observed,
            ObservationState = observed ? WindowObservationState.OBSERVED : WindowObservationState.NOT_OBSERVED,
            EffectUnknown = result.EffectUnknown && !observed,
            FailureStage = observed ? result.FailureStage :
                result.FailureStage ?? WindowFailureStage.VERIFY_EFFECT };

    // A successful native dispatch is not a verified UI action. If the caller
    // supplied a postcondition and it was not observed, preserve the unknown
    // effect and do not encourage an unsafe second click.
    internal static WindowActionResult RequireVerifiedActionEffect(WindowActionRequest request,
        WindowActionResult result) =>
        result.Status == WindowActionCode.COMPLETED && result.ExpectedStateObserved != true &&
        (request.ExpectedName is not null || request.ExpectedEnabledName is not null)
            ? result with { Status = WindowActionCode.ACTION_FAILED_UNKNOWN_EFFECT,
                EffectUnknown = true, FailureStage = result.FailureStage ?? WindowFailureStage.VERIFY_EFFECT,
                Detail = result.Detail ?? "EXPECTED_EFFECT_NOT_OBSERVED" }
            : result;

    // Only a failure known to have happened before UIA dispatch may fall
    // through to CDP/physical input. An uncertain provider result is final.
    internal static bool CanFallbackAfterNativeClick(WindowActionResult result) =>
        !result.EffectUnknown && result.FailureStage != WindowFailureStage.DISPATCH_UIA &&
        (result.Status is WindowActionCode.UIA_UNAVAILABLE or WindowActionCode.UNSUPPORTED_ACTION);

    internal static bool CanFallbackToSemanticAfterPhysical(WindowActionResult result) =>
        !result.EffectUnknown &&
        (result.Status is WindowActionCode.FOREGROUND_UNAVAILABLE or
            WindowActionCode.TARGET_OCCLUDED or WindowActionCode.CLICK_POINT_UNAVAILABLE or
            WindowActionCode.WINDOW_NOT_VISIBLE);

    internal static WindowActionResult CdpOnlyResult(WindowActionResult? dispatched, Stopwatch clock) =>
        dispatched ?? new(WindowActionCode.CDP_ONLY_UNAVAILABLE,
            Detail:"CDP_ONLY_REQUIRES_CONSENTED_CHROME_WEB_TARGET",
            FailureStage:WindowFailureStage.CDP_TARGET,
            ElapsedMs:clock.ElapsedMilliseconds);

    internal static bool PreferPhysicalWebClick(WindowActionKind? semantic, bool insideDocument) =>
        insideDocument && semantic == WindowActionKind.INVOKE;

    // SendInput returning a full insertion count means the OS queued keys;
    // it is not proof that Chrome/React changed an input. When the value or
    // requested postcondition was observable but never appeared, report an
    // unknown effect (no automatic retry) instead of a successful operation.
    internal static WindowActionResult RequireVerifiedTextEffect(WindowActionRequest request,
        WindowActionResult result, bool valueObservable) =>
        (request.Action is WindowActionKind.SET_VALUE or WindowActionKind.TYPE_TEXT) &&
        result.Status == WindowActionCode.COMPLETED && result.ExpectedStateObserved != true &&
        (valueObservable || request.ExpectedEnabledName is not null || request.ExpectedName is not null)
            ? result with { Status = WindowActionCode.ACTION_FAILED_UNKNOWN_EFFECT,
                EffectUnknown = true, FailureStage = result.FailureStage ?? WindowFailureStage.VERIFY_EFFECT,
                Detail = result.Detail ?? "TEXT_EFFECT_NOT_OBSERVED" }
            : result;

    private static WindowActionKind? SemanticClickAction(AutomationElement element, ControlType role)
    {
        if ((role == ControlType.Button || role == ControlType.Hyperlink ||
            role == ControlType.MenuItem) && Pattern(element, role, WindowActionKind.INVOKE) is not null)
            return WindowActionKind.INVOKE;
        if ((role == ControlType.Button || role == ControlType.CheckBox) &&
            Pattern(element, role, WindowActionKind.TOGGLE) is not null)
            return WindowActionKind.TOGGLE;
        if ((role == ControlType.RadioButton || role == ControlType.ListItem ||
            role == ControlType.TabItem) && Pattern(element, role, WindowActionKind.SELECT) is not null)
            return WindowActionKind.SELECT;
        if (Pattern(element, role, WindowActionKind.EXPAND) is ExpandCollapsePattern expand)
            return expand.Current.ExpandCollapseState switch
            {
                ExpandCollapseState.Collapsed => WindowActionKind.EXPAND,
                ExpandCollapseState.Expanded or ExpandCollapseState.PartiallyExpanded => WindowActionKind.COLLAPSE,
                _ => null
            };
        if (Pattern(element, role, WindowActionKind.INVOKE) is not null)
            return WindowActionKind.INVOKE;
        if (Pattern(element, role, WindowActionKind.TOGGLE) is not null)
            return WindowActionKind.TOGGLE;
        if (Pattern(element, role, WindowActionKind.SELECT) is not null)
            return WindowActionKind.SELECT;
        return null;
    }

    private static object? Pattern(AutomationElement element, ControlType role, WindowActionKind kind)
    {
        AutomationPattern? id = kind switch
        {
            // Actual provider patterns, not UIA role names, define native abilities.
            WindowActionKind.INVOKE => InvokePattern.Pattern,
            WindowActionKind.SET_VALUE => ValuePattern.Pattern,
            WindowActionKind.SELECT => SelectionItemPattern.Pattern,
            WindowActionKind.TOGGLE => TogglePattern.Pattern,
            WindowActionKind.EXPAND or WindowActionKind.COLLAPSE => ExpandCollapsePattern.Pattern,
            _ => null
        };
        return id is not null && element.TryGetCurrentPattern(id, out var value) ? value : null;
    }

    internal static async Task<WindowActionResult> RunIsolatedAsync(WindowActionRequest request,
        CancellationToken cancellationToken)
    {
        if (!Valid(request)) return new(InvalidCode(request));
        var auditId = Guid.NewGuid().ToString("N");
        if (!WriteAudit(auditId, "INTENT", request, null))
            return new(WindowActionCode.AUDIT_UNAVAILABLE, AuditId: auditId);
        bool chrome = request.ExpectedProfile is not null ||
            WindowChromeCdp.WellFormedTabElementRef(request.ElementId) ||
            IsChromeWindow(request.WindowId);
        if (chrome && request.ElementId == "locator")
        {
            // Resolve UIA only in the disposable process. It never performs the
            // effect or owns CDP. The broker rechecks the fresh fingerprint.
            var resolved = await RunDisposableWorkerAsync(request with { ResolveOnly = true },
                cancellationToken);
            if (resolved.Status == WindowActionCode.LOCATOR_RESOLVED &&
                resolved.ResolvedElementId is not null)
                request = request with { ElementId = resolved.ResolvedElementId,
                    ElementPath = resolved.ResolvedElementPath,
                    ResolveOnly = false, LocatorAutomationId = null,
                    LocatorName = null, LocatorRole = null };
            else return FinalizeAudit(auditId, request, resolved);
        }
        WindowActionResult result = chrome
            ? await WindowChromeBroker.InvokeAsync(request, cancellationToken, auditId)
            : await RunDisposableWorkerAsync(request, cancellationToken);
        return FinalizeAudit(auditId, request, result);
    }

    private static WindowActionResult FinalizeAudit(string auditId, WindowActionRequest request,
        WindowActionResult result)
    {
        if (!WriteAudit(auditId, "RESULT", request, result))
            return new(WindowActionCode.AUDIT_RESULT_FAILED, AuditId: auditId,
                TraceId: auditId, EffectUnknown: true);
        return result with { AuditId = auditId, TraceId = result.TraceId ?? auditId };
    }

    internal static bool IsChromeWindow(string windowId)
    {
        if (!WindowCatalog.TryParseId(windowId, out var identity)) return false;
        using var self = Process.GetCurrentProcess();
        if (WindowCatalog.ResolveId(windowId, self.SessionId, out _) != WindowResolveCode.OK)
            return false;
        try
        {
            using var owner = Process.GetProcessById(identity.ProcessId);
            return owner.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or Win32Exception or InvalidOperationException or
            System.Security.SecurityException) { return false; }
    }

    private static async Task<WindowActionResult> RunDisposableWorkerAsync(WindowActionRequest request,
        CancellationToken cancellationToken)
    {
        WindowActionResult result;
        bool launched = false;
        using var process = new Process();
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            result = new(request.ResolveOnly ? WindowActionCode.LOCATOR_WORKER_FAILED :
                WindowActionCode.WORKER_FAILED_UNKNOWN_EFFECT, EffectUnknown: !request.ResolveOnly,
                FailureStage: WindowFailureStage.WORKER);
        else
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardInput = true, RedirectStandardError = false, CreateNoWindow = true
            };
            start.ArgumentList.Add("window-action-worker");
            start.Environment.Remove("CONTROL_PLANE_API_KEY");
            start.Environment.Remove("OPENAI_API_KEY");
            process.StartInfo = start;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(request.ObservationTimeoutMs + 6000));
            try
            {
                if (!process.Start()) throw new Win32Exception("Worker launch failed");
                launched = true;
                string payload = JsonSerializer.Serialize(request, PipeWire.Json);
                await process.StandardInput.WriteAsync(payload.AsMemory(), deadline.Token);
                await process.StandardInput.FlushAsync(deadline.Token);
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                string json = await output;
                if (json.Length > 2048) throw new JsonException("OUTPUT_LIMIT");
                result = JsonSerializer.Deserialize<WindowActionResult>(json, PipeWire.Json) ??
                    new(request.ResolveOnly ? WindowActionCode.LOCATOR_WORKER_FAILED :
                        WindowActionCode.WORKER_FAILED_UNKNOWN_EFFECT, EffectUnknown: !request.ResolveOnly,
                        FailureStage: WindowFailureStage.WORKER);
            }
            catch (OperationCanceledException)
            {
                Stop(process, launched);
                result = new(request.ResolveOnly ? WindowActionCode.LOCATOR_TIMEOUT :
                    WindowActionCode.TIMEOUT_UNKNOWN_EFFECT, EffectUnknown: !request.ResolveOnly,
                    FailureStage: WindowFailureStage.WORKER);
            }
            catch (Exception e) when (e is Win32Exception or IOException or JsonException or InvalidOperationException)
            {
                Stop(process, launched);
                result = new(request.ResolveOnly ? WindowActionCode.LOCATOR_WORKER_FAILED :
                    WindowActionCode.WORKER_FAILED_UNKNOWN_EFFECT,
                    EffectUnknown: launched && !request.ResolveOnly,
                    Detail: e.GetType().Name, FailureStage: WindowFailureStage.WORKER,
                    ExceptionType: e.GetType().Name, HResult: e.HResult.ToString("X8"));
            }
        }
        return result;
    }

    private static void Stop(Process process, bool launched)
    {
        if (!launched) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
    }

    // Two Windows Application event records, never the supplied field value.
    // Audit failure before dispatch blocks action. No new log service or DB.
    private static bool WriteAudit(string id, string phase, WindowActionRequest req, WindowActionResult? result)
    {
        try
        {
            string windowHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(req.WindowId)).AsSpan(0, 12));
            string record = JsonSerializer.Serialize(new
            {
                component = "WindowsAdminMCP.window_action", audit_id = id, phase,
                window_hash = windowHash, element_id = req.ElementId,
                action = req.Action.ToString(), input_mode = req.InputMode.ToString(),
                result = result?.Status.ToString(),
                trace_id = result?.TraceId ?? id,
                broker_owner_pid = result?.BrokerOwnerPid,
                session_generation = result?.SessionGeneration,
                provider_outcome = result?.ProviderOutcome.ToString(),
                effect_evidence = result?.EffectEvidence.ToString(),
                next_step = result?.NextStep.ToString(),
                focus_behavior = result?.FocusBehavior.ToString(),
                enabled_before = result?.EnabledBefore,
                safe_to_retry = result?.SafeToRetry,
                effect_state = result?.EffectState.ToString(),
                observation_state = result?.ObservationState.ToString(),
                failure_stage = result?.FailureStage?.ToString(), hresult = result?.HResult,
                utc = DateTimeOffset.UtcNow
            }, PipeWire.Json);
            EventLog.WriteEntry("WindowsAdminMCP", record, EventLogEntryType.Information, 8101);
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or
                                 System.Security.SecurityException or ArgumentException)
        { return false; }
    }

}
