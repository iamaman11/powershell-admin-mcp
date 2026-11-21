using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

// Native UIA2 uses the same Windows UI Automation provider as the managed
// inspector, but lets this *client* disable the default SetFocus before actions.
// Deliberately no new NuGet dependency, input injection, or persistent worker.
internal static class WindowActionNoFocus
{
    private static readonly Guid ClassId = new("e22ad333-b25f-460c-83d0-0581107395c9"); // CUIAutomation8
    private static readonly Guid InterfaceId = new("34723aff-0c9d-49d0-9896-7ab52df8cd8a"); // IUIAutomation2

    // Slots from the Windows SDK UIAutomationClient interfaces, including IUnknown.
    private const int ElementFromHandleSlot = 6;
    private const int ControlViewWalkerSlot = 14;
    private const int GetAutoFocusSlot = 58;
    private const int AutoSetFocusSlot = 59;
    private const int ElementRuntimeIdSlot = 4;
    private const int ElementCurrentPatternSlot = 16;
    private const int WalkerFirstChildSlot = 4;
    private const int WalkerNextSiblingSlot = 6;
    private const int PatternActionSlot = 3;

    private const int InvokePatternId = 10000;
    private const int ValuePatternId = 10002;
    private const int SelectionItemPatternId = 10010;
    private const int TogglePatternId = 10015;
    private const int ExpandCollapsePatternId = 10005;
    private const int UiaElementNotEnabled = unchecked((int)0x80040200);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetAutoFocus(IntPtr self, int enabled);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAutoFocus(IntPtr self, out int enabled);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ElementFromHandle(IntPtr self, IntPtr hwnd, out IntPtr element);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetControlWalker(IntPtr self, out IntPtr walker);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetRelativeElement(IntPtr self, IntPtr element, out IntPtr relative);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetRuntimeId(IntPtr self, out IntPtr safeArray);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCurrentPattern(IntPtr self, int id, out IntPtr pattern);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InvokeAction(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetValueAction(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string value);

    internal static WindowActionResult Invoke(IntPtr hwnd, int[] path, int[] expectedRuntimeId,
        WindowActionRequest request, Stopwatch clock, bool radioInvoke = false)
    {
        if (path.Length > WindowInspector.MaxDepth || expectedRuntimeId.Length is < 1 or > 128)
            return new(WindowActionCode.STALE_ELEMENT, ElapsedMs: clock.ElapsedMilliseconds);

        object? factory = null;
        IntPtr unknown = IntPtr.Zero, automation = IntPtr.Zero, walker = IntPtr.Zero;
        IntPtr element = IntPtr.Zero, pattern = IntPtr.Zero;
        bool dispatched = false;
        WindowFailureStage stage = WindowFailureStage.PREPARE_UIA;
        IntPtr foregroundAtEntry = GetForegroundWindow();
        try
        {
            factory = Activator.CreateInstance(Type.GetTypeFromCLSID(ClassId, throwOnError: true)!);
            if (factory is null)
                return new(WindowActionCode.UIA_UNAVAILABLE, ElapsedMs: clock.ElapsedMilliseconds);
            unknown = Marshal.GetIUnknownForObject(factory);
            Check(Marshal.QueryInterface(unknown, in InterfaceId, out automation));

            // Must precede ElementFromHandle and GetCurrentPattern: this switch
            // applies to the COM client instance issuing the action.
            Check(Method<SetAutoFocus>(automation, AutoSetFocusSlot)(automation, 0));
            Check(Method<GetAutoFocus>(automation, GetAutoFocusSlot)(automation, out int autoFocus));
            if (autoFocus != 0)
                return new(WindowActionCode.UIA_UNAVAILABLE, Detail: "AUTOSETFOCUS_NOT_DISABLED",
                    ElapsedMs: clock.ElapsedMilliseconds);
            Check(Method<ElementFromHandle>(automation, ElementFromHandleSlot)(
                automation, hwnd, out element));
            stage = WindowFailureStage.RESOLVE_ELEMENT;
            if (element == IntPtr.Zero)
                return new(WindowActionCode.STALE_ELEMENT, ElapsedMs: clock.ElapsedMilliseconds);

            Check(Method<GetControlWalker>(automation, ControlViewWalkerSlot)(automation, out walker));
            if (walker == IntPtr.Zero)
                return new(WindowActionCode.UIA_UNAVAILABLE, ElapsedMs: clock.ElapsedMilliseconds);

            // Follow the same bounded ControlView child indices as the managed
            // fingerprint walker; never act if the views disagree.
            foreach (int childIndex in path)
            {
                if (childIndex is < 0 or > WindowInspector.MaxTraversal)
                    return new(WindowActionCode.STALE_ELEMENT, ElapsedMs: clock.ElapsedMilliseconds);
                IntPtr child = IntPtr.Zero;
                try
                {
                    Check(Method<GetRelativeElement>(walker, WalkerFirstChildSlot)(
                        walker, element, out child));
                    for (int i = 0; i < childIndex && child != IntPtr.Zero; i++)
                    {
                        Check(Method<GetRelativeElement>(walker, WalkerNextSiblingSlot)(
                            walker, child, out var sibling));
                        Release(ref child);
                        child = sibling;
                    }
                    if (child == IntPtr.Zero)
                        return new(WindowActionCode.STALE_ELEMENT, ElapsedMs: clock.ElapsedMilliseconds);
                    Release(ref element);
                    element = child;
                    child = IntPtr.Zero;
                }
                finally { Release(ref child); }
            }

            if (!RuntimeIdMatches(element, expectedRuntimeId))
                return new(WindowActionCode.STALE_ELEMENT, ElapsedMs: clock.ElapsedMilliseconds);

            int patternId = request.Action switch
            {
                WindowActionKind.INVOKE => InvokePatternId,
                WindowActionKind.SET_VALUE => ValuePatternId,
                WindowActionKind.SELECT when radioInvoke => InvokePatternId,
                WindowActionKind.SELECT => SelectionItemPatternId,
                WindowActionKind.TOGGLE => TogglePatternId,
                WindowActionKind.EXPAND or WindowActionKind.COLLAPSE => ExpandCollapsePatternId,
                _ => 0
            };
            if (patternId == 0) return new(WindowActionCode.INVALID_ARGUMENT);
            Check(Method<GetCurrentPattern>(element, ElementCurrentPatternSlot)(
                element, patternId, out pattern));
            if (pattern == IntPtr.Zero)
                return new(WindowActionCode.UNSUPPORTED_ACTION, ElapsedMs: clock.ElapsedMilliseconds);

            IntPtr foregroundBefore = GetForegroundWindow();
            stage = WindowFailureStage.DISPATCH_UIA;
            dispatched = true;
            int result = request.Action == WindowActionKind.SET_VALUE
                ? Method<SetValueAction>(pattern, PatternActionSlot)(pattern, request.Value!)
                : Method<InvokeAction>(pattern, request.Action == WindowActionKind.COLLAPSE
                    ? PatternActionSlot + 1 : PatternActionSlot)(pattern);
            bool changedDuringPreparation = foregroundAtEntry != foregroundBefore;
            IntPtr foregroundAfter = GetForegroundWindow();
            bool changedDuringAction = foregroundBefore != foregroundAfter;
            bool changed = changedDuringPreparation || changedDuringAction;
            // This observes the top-level target HWND only, not the identity
            // or title of any unrelated foreground window. It does not claim
            // that UIA caused the transition or guarantee preservation.
            bool targetActivated = foregroundAtEntry != hwnd && foregroundAfter == hwnd;
            string? focusDetail = changedDuringPreparation && changedDuringAction ? "FOREGROUND_PREPARE_AND_ACTION" :
                changedDuringPreparation ? "FOREGROUND_PREPARE" :
                changedDuringAction ? "FOREGROUND_ACTION" : null;
            return result < 0
                ? new(WindowActionCode.ACTION_FAILED_UNKNOWN_EFFECT, EffectUnknown: true,
                    ForegroundChanged: changed, Detail: (result == UiaElementNotEnabled
                        ? "UIA_E_ELEMENTNOTENABLED:" : "") + result.ToString("X8") +
                        (focusDetail is null ? "" : ":" + focusDetail),
                    ElapsedMs: clock.ElapsedMilliseconds, TargetActivated: targetActivated,
                    FailureStage: stage, HResult: result.ToString("X8"),
                    ProviderResponse: result.ToString("X8"))
                : new(WindowActionCode.COMPLETED, ForegroundChanged: changed,
                    Detail: focusDetail,
                    ElapsedMs: clock.ElapsedMilliseconds, TargetActivated: targetActivated);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or
                                      ArgumentException or NotSupportedException)
        {
            return new(dispatched ? WindowActionCode.ACTION_FAILED_UNKNOWN_EFFECT : WindowActionCode.UIA_UNAVAILABLE,
                EffectUnknown: dispatched, Detail: error.HResult.ToString("X8"),
                ElapsedMs: clock.ElapsedMilliseconds, FailureStage: stage,
                ExceptionType: error.GetType().Name, HResult: error.HResult.ToString("X8"));
        }
        finally
        {
            Release(ref pattern);
            Release(ref element);
            Release(ref walker);
            Release(ref automation);
            Release(ref unknown);
            if (factory is not null && Marshal.IsComObject(factory))
                Marshal.FinalReleaseComObject(factory);
        }
    }

    private static bool RuntimeIdMatches(IntPtr element, int[] expected)
    {
        IntPtr safeArray = IntPtr.Zero;
        try
        {
            Check(Method<GetRuntimeId>(element, ElementRuntimeIdSlot)(element, out safeArray));
            if (safeArray == IntPtr.Zero || SafeArrayGetDim(safeArray) != 1)
                return false;
            Check(SafeArrayGetLBound(safeArray, 1, out int lower));
            Check(SafeArrayGetUBound(safeArray, 1, out int upper));
            if (upper < lower || (long)upper - lower + 1 != expected.Length)
                return false;
            for (int i = 0; i < expected.Length; i++)
            {
                int index = lower + i;
                Check(SafeArrayGetElement(safeArray, ref index, out int actual));
                if (actual != expected[i]) return false;
            }
            return true;
        }
        finally
        {
            if (safeArray != IntPtr.Zero) SafeArrayDestroy(safeArray);
        }
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    private static void Check(int hresult)
    {
        if (hresult < 0) Marshal.ThrowExceptionForHR(hresult);
    }

    private static void Release(ref IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return;
        Marshal.Release(pointer);
        pointer = IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("oleaut32.dll")]
    private static extern uint SafeArrayGetDim(IntPtr safeArray);
    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetLBound(IntPtr safeArray, uint dimension, out int lower);
    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetUBound(IntPtr safeArray, uint dimension, out int upper);
    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetElement(IntPtr safeArray, ref int index, out int value);
    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayDestroy(IntPtr safeArray);
}
