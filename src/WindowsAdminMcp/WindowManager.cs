using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<WindowManageAction>))]
public enum WindowManageAction { ACTIVATE, RESTORE, MINIMIZE, MAXIMIZE, MOVE, RESIZE, CLOSE }

[JsonConverter(typeof(JsonStringEnumConverter<WindowManageCode>))]
public enum WindowManageCode
{
    COMPLETED, INVALID_ARGUMENT, SERVICE_UNAVAILABLE, USER_CONTEXT_UNAVAILABLE,
    DESKTOP_UNAVAILABLE, STALE_WINDOW, ACCESS_DENIED, WINDOW_NOT_VISIBLE,
    FOREGROUND_UNAVAILABLE, NATIVE_FAILURE, EFFECT_UNKNOWN,
    AUDIT_UNAVAILABLE, AUDIT_RESULT_FAILED
}

public sealed record WindowManageResult(
    [property:JsonPropertyName("status")] WindowManageCode Status,
    [property:JsonPropertyName("window_id")] string? WindowId=null,
    [property:JsonPropertyName("action")] WindowManageAction? Action=null,
    [property:JsonPropertyName("before")] WindowGeometry? Before=null,
    [property:JsonPropertyName("after")] WindowGeometry? After=null,
    [property:JsonPropertyName("foreground_changed")] bool? ForegroundChanged=null,
    [property:JsonPropertyName("effect_state")] WindowEffectState EffectState=WindowEffectState.NO_EFFECT,
    [property:JsonPropertyName("native_error")] int? NativeError=null,
    [property:JsonPropertyName("elapsed_ms")] long? ElapsedMs=null)
{
    [JsonPropertyName("safe_to_retry")]
    public bool SafeToRetry=>EffectState==WindowEffectState.NO_EFFECT &&
        Status is WindowManageCode.INVALID_ARGUMENT or WindowManageCode.SERVICE_UNAVAILABLE or
            WindowManageCode.USER_CONTEXT_UNAVAILABLE or WindowManageCode.DESKTOP_UNAVAILABLE or
            WindowManageCode.STALE_WINDOW or WindowManageCode.ACCESS_DENIED or
            WindowManageCode.WINDOW_NOT_VISIBLE or WindowManageCode.AUDIT_UNAVAILABLE;
}

public sealed record WindowGeometry(int X,int Y,int Width,int Height,bool Minimized,bool Maximized,bool Foreground);

// Win32 top-level window operations are short, synchronous and already native.
// Never fake foreground privileges or perform unrequested activation for MOVE/RESIZE.
internal static class WindowManager
{
    private const int Restore=9,Minimize=6,Maximize=3;
    private const uint WmClose=0x0010;
    private const uint NoZOrder=0x0004,NoActivate=0x0010,NoMove=0x0002,NoSize=0x0001;

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd,int command);
    [DllImport("user32.dll",EntryPoint="PostMessageW",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr wparam,IntPtr lparam);

    internal static bool Valid(WindowManageAction action,int? x,int? y,int? width,int? height) =>
        Enum.IsDefined(action) && (action switch
        {
            WindowManageAction.MOVE => x.HasValue && y.HasValue && width is null && height is null,
            WindowManageAction.RESIZE => x is null && y is null && width is >0 and <=32768 && height is >0 and <=32768,
            _ => x is null && y is null && width is null && height is null
        });

    private static WindowGeometry? Observe(IntPtr hwnd)
    {
        if(!GetWindowRect(hwnd,out var r))return null;
        return new(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top,IsIconic(hwnd),IsZoomed(hwnd),GetForegroundWindow()==hwnd);
    }

    // Window mutation has the same fail-closed audit gate as UIA actions.
    private static bool Audit(string auditId,string phase,string id,WindowManageAction action,WindowManageResult? result)
    {
        try
        {
            string hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)).AsSpan(0,12));
            string record=JsonSerializer.Serialize(new {component="WindowsAdminMCP.window_manage",
                audit_id=auditId,phase,window_hash=hash,action=action.ToString(),
                status=result?.Status.ToString(),effect_state=result?.EffectState.ToString(),
                utc=DateTimeOffset.UtcNow},PipeWire.Json);
            EventLog.WriteEntry("WindowsAdminMCP",record,EventLogEntryType.Information,8102);
            return true;
        }
        catch(Exception e) when(e is Win32Exception or InvalidOperationException or
            System.Security.SecurityException or ArgumentException){return false;}
    }

    internal static WindowManageResult ExecuteAudited(string id,WindowManageAction action,
        int? x,int? y,int? width,int? height,int session)
    {
        string auditId=Guid.NewGuid().ToString("N");
        if(!Audit(auditId,"INTENT",id,action,null))
            return new(WindowManageCode.AUDIT_UNAVAILABLE,id,action);
        var result=Execute(id,action,x,y,width,height,session);
        return Audit(auditId,"RESULT",id,action,result)?result:
            result with {Status=WindowManageCode.AUDIT_RESULT_FAILED,
                EffectState=WindowEffectState.EFFECT_UNKNOWN};
    }

    internal static WindowManageResult Execute(string id,WindowManageAction action,int? x,int? y,int? width,int? height,int session)
    {
        var clock=Stopwatch.StartNew();
        WindowManageResult Done(WindowManageCode code,WindowGeometry? before=null,WindowGeometry? after=null,
            int? error=null,WindowEffectState effect=WindowEffectState.NO_EFFECT) =>
            new(code,id,action,before,after,before?.Foreground!=after?.Foreground,effect,error,clock.ElapsedMilliseconds);
        if(!WindowCatalog.TryParseId(id,out _) || !Valid(action,x,y,width,height))return Done(WindowManageCode.INVALID_ARGUMENT);
        var resolved=WindowCatalog.ResolveId(id,session,out var hwnd);
        if(resolved!=WindowResolveCode.OK)return Done(resolved switch
        {
            WindowResolveCode.WRONG_SESSION=>WindowManageCode.USER_CONTEXT_UNAVAILABLE,
            WindowResolveCode.ACCESS_DENIED=>WindowManageCode.ACCESS_DENIED,
            _=>WindowManageCode.STALE_WINDOW
        });
        var before=Observe(hwnd);
        if(before is null)return Done(WindowManageCode.NATIVE_FAILURE);
        if(action==WindowManageAction.ACTIVATE && !IsWindowVisible(hwnd))return Done(WindowManageCode.WINDOW_NOT_VISIBLE,before);
        if(action==WindowManageAction.CLOSE)
        {
            // Graceful WM_CLOSE; never terminate the process or dismiss an app's
            // unsaved-work prompt. Message acceptance is not effect verification.
            if(!PostMessage(hwnd,WmClose,IntPtr.Zero,IntPtr.Zero))
                return Done(WindowManageCode.NATIVE_FAILURE,before,Observe(hwnd),Marshal.GetLastPInvokeError());
            // WM_CLOSE is queued on the target UI thread. A busy but responsive
            // WinForms/WPF message pump may process it after 750 ms; allow a
            // bounded 3-second observation window before reporting uncertainty.
            // Never resend WM_CLOSE if the first effect is still unknown.
            for(int attempt=0;attempt<120;attempt++)
            {
                var state=WindowCatalog.ResolveId(id,session,out _);
                if(state==WindowResolveCode.STALE_WINDOW)
                    return Done(WindowManageCode.COMPLETED,before,effect:WindowEffectState.EFFECT_VERIFIED);
                if(state!=WindowResolveCode.OK)break;
                Thread.Sleep(25);
            }
            return Done(WindowManageCode.EFFECT_UNKNOWN,before,Observe(hwnd),
                effect:WindowEffectState.EFFECT_UNKNOWN);
        }
        bool ok=action switch
        {
            WindowManageAction.ACTIVATE=>SetForegroundWindow(hwnd),
            WindowManageAction.RESTORE=>ShowWindow(hwnd,Restore),
            WindowManageAction.MINIMIZE=>ShowWindow(hwnd,Minimize),
            WindowManageAction.MAXIMIZE=>ShowWindow(hwnd,Maximize),
            WindowManageAction.MOVE=>SetWindowPos(hwnd,IntPtr.Zero,x!.Value,y!.Value,0,0,NoZOrder|NoActivate|NoSize),
            WindowManageAction.RESIZE=>SetWindowPos(hwnd,IntPtr.Zero,0,0,width!.Value,height!.Value,NoZOrder|NoActivate|NoMove),
            _=>false
        };
        int native=Marshal.GetLastPInvokeError();
        // ShowWindow returns prior visibility, NOT an indication of failure.
        if(action is WindowManageAction.RESTORE or WindowManageAction.MINIMIZE or WindowManageAction.MAXIMIZE)ok=true;
        if(!ok)return Done(action==WindowManageAction.ACTIVATE?WindowManageCode.FOREGROUND_UNAVAILABLE:
            WindowManageCode.NATIVE_FAILURE,before,Observe(hwnd),native);
        if(WindowCatalog.ResolveId(id,session,out var afterHwnd)!=WindowResolveCode.OK || afterHwnd!=hwnd)
            return Done(WindowManageCode.EFFECT_UNKNOWN,before,null,effect:WindowEffectState.EFFECT_UNKNOWN);
        var after=Observe(hwnd);
        if(after is null)return Done(WindowManageCode.EFFECT_UNKNOWN,before,null,effect:WindowEffectState.EFFECT_UNKNOWN);
        bool observed=action switch
        {
            WindowManageAction.ACTIVATE=>after.Foreground,
            WindowManageAction.RESTORE=>!after.Minimized && !after.Maximized,
            WindowManageAction.MINIMIZE=>after.Minimized,
            WindowManageAction.MAXIMIZE=>after.Maximized,
            WindowManageAction.MOVE=>after.X==x && after.Y==y,
            WindowManageAction.RESIZE=>after.Width==width && after.Height==height,
            _=>false
        };
        return Done(observed?WindowManageCode.COMPLETED:WindowManageCode.EFFECT_UNKNOWN,
            before,after,effect:observed?WindowEffectState.EFFECT_VERIFIED:WindowEffectState.EFFECT_UNKNOWN);
    }
}
