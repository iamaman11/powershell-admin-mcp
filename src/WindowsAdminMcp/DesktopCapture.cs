using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using System.Windows.Forms;

[JsonConverter(typeof(JsonStringEnumConverter<CaptureCode>))]
internal enum CaptureCode
{
    COMPLETED, INVALID_ARGUMENT, SERVICE_CHECK_FAILED, INTERACTIVE_DESKTOP_UNAVAILABLE,
    MONITOR_NOT_FOUND, REGION_OUTSIDE_MONITOR, REGION_PIXEL_LIMIT_EXCEEDED,
    SCREENSHOT_EXCEEDS_2MB, SCREEN_CAPTURE_FAILED
}

internal sealed record CaptureDescriptor(
    [property:JsonPropertyName("status")] CaptureCode Status,
    [property:JsonPropertyName("correlation_id")] string CorrelationId,
    [property:JsonPropertyName("monitor")] int? Monitor=null,
    [property:JsonPropertyName("width")] int? Width=null,
    [property:JsonPropertyName("height")] int? Height=null,
    [property:JsonPropertyName("mimeType")] string? MimeType=null,
    [property:JsonPropertyName("bytes")] int? Bytes=null,
    [property:JsonPropertyName("session_id")] int? SessionId=null,
    [property:JsonPropertyName("service_status")] StatusCode? ServiceStatus=null,
    [property:JsonPropertyName("native_error")] int? NativeError=null,
    [property:JsonPropertyName("timing")] ToolTiming? Timing=null,
    [property:JsonPropertyName("service_check_correlation_id")] string? ServiceCheckCorrelationId=null);

internal static class DesktopCapture
{
    internal static bool ValidArguments(int monitor,int x,int y,int? width,int? height,int maxWidth)
        => monitor>=0 && x>=0 && y>=0 && maxWidth is >=256 and <=1920 &&
           width.HasValue==height.HasValue && (width is null or >0) && (height is null or >0);

    internal static CallToolResult Error(CaptureCode code,string correlation,StatusCode? service=null,int? native=null,ToolTiming? timing=null,string? serviceCorrelation=null)
        => Result(new(code,correlation,ServiceStatus:service,NativeError:native,Timing:timing,ServiceCheckCorrelationId:serviceCorrelation));

    private static CallToolResult Result(CaptureDescriptor status,byte[]? png=null)
        => new()
        {
            IsError=png is null,
            StructuredContent=JsonSerializer.SerializeToElement(status,PipeWire.Json),
            Content=png is null
                ? [new TextContentBlock { Text=JsonSerializer.Serialize(status,PipeWire.Json) }]
                : [new ImageContentBlock { MimeType="image/png",Data=Encoding.ASCII.GetBytes(Convert.ToBase64String(png)) }]
        };

    internal static CallToolResult Capture(int monitor,int x,int y,int? width,int? height,int maxWidth,string correlation,ToolTiming? previous=null,string? serviceCorrelation=null)
    {
        var clock=Stopwatch.StartNew();
        ToolTiming Timing()=>(previous??new()) with {CaptureMs=clock.ElapsedMilliseconds,ToolMs=(previous?.ToolMs??0)+clock.ElapsedMilliseconds};
        CallToolResult Fail(CaptureCode code,int? native=null)=>Error(code,correlation,native:native,timing:Timing(),serviceCorrelation:serviceCorrelation);
        using var process=Process.GetCurrentProcess();
        int session=process.SessionId;
        if(!IsDefaultConsoleDesktop(session))return Fail(CaptureCode.INTERACTIVE_DESKTOP_UNAVAILABLE);
        IntPtr oldDpi=SetThreadDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2
        if(oldDpi==IntPtr.Zero)return Fail(CaptureCode.SCREEN_CAPTURE_FAILED,native:Marshal.GetLastWin32Error());
        try
        {
            var screens=Screen.AllScreens;
            if(monitor>=screens.Length)return Fail(CaptureCode.MONITOR_NOT_FOUND);
            var bounds=screens[monitor].Bounds;
            int w=width??(bounds.Width-x),h=height??(bounds.Height-y);
            if(w<1 || h<1 || x>bounds.Width-w || y>bounds.Height-h)
                return Fail(CaptureCode.REGION_OUTSIDE_MONITOR);
            if((long)w*h>16000000)return Fail(CaptureCode.REGION_PIXEL_LIMIT_EXCEEDED);
            using var source=new Bitmap(w,h,PixelFormat.Format24bppRgb);
            using(var drawing=Graphics.FromImage(source))
                drawing.CopyFromScreen(bounds.Left+x,bounds.Top+y,0,0,new Size(w,h));
            // Reject if the ordinary input desktop disappeared during capture.
            if(!IsDefaultConsoleDesktop(session))return Fail(CaptureCode.INTERACTIVE_DESKTOP_UNAVAILABLE);
            var encoded=Encode(source,maxWidth);
            if(encoded is null)return Error(CaptureCode.SCREENSHOT_EXCEEDS_2MB,correlation);
            return Result(new(CaptureCode.COMPLETED,correlation,monitor,encoded.Width,encoded.Height,
                "image/png",encoded.Png.Length,session,Timing:Timing(),ServiceCheckCorrelationId:serviceCorrelation),encoded.Png);
        }
        catch(Exception e) when(e is Win32Exception or ExternalException or ArgumentException or InvalidOperationException)
        {return Fail(CaptureCode.SCREEN_CAPTURE_FAILED,native:e.HResult);}
        finally{_ = SetThreadDpiAwarenessContext(oldDpi);}
    }

    internal sealed record Encoded(byte[] Png,int Width,int Height);
    internal static Encoded? Encode(Bitmap source,int maxWidth)
    {
        int w=Math.Min(source.Width,maxWidth);
        int h=Math.Max(1,(int)Math.Round((double)source.Height*w/source.Width));
        if(h>1080){w=Math.Max(1,(int)Math.Floor((double)w*1080/h));h=1080;}
        for(int i=0;i<5;i++)
        {
            using var scaled=new Bitmap(w,h,PixelFormat.Format24bppRgb);
            using(var graphics=Graphics.FromImage(scaled))
            {
                graphics.InterpolationMode=InterpolationMode.HighQualityBilinear;
                graphics.DrawImage(source,0,0,w,h);
            }
            using var memory=new MemoryStream();
            scaled.Save(memory,ImageFormat.Png);
            if(memory.Length<=2000000)return new(memory.ToArray(),w,h);
            w=Math.Max(1,(int)Math.Floor(w*0.7));h=Math.Max(1,(int)Math.Floor(h*0.7));
        }
        return null;
    }

    private static bool IsDefaultConsoleDesktop(int session)
    {
        if(session==0 || !Environment.UserInteractive || (uint)session!=WTSGetActiveConsoleSessionId())return false;
        IntPtr input=OpenInputDesktop(0,false,1); // DESKTOP_READOBJECTS, no switching rights
        if(input==IntPtr.Zero)return false;
        try
        {
            return Name(input)=="Default" &&
                Name(GetThreadDesktop(GetCurrentThreadId()))=="Default";
        }
        finally{_ = CloseDesktop(input);}
    }
    private static string? Name(IntPtr desktop)
    {
        if(desktop==IntPtr.Zero)return null;
        var name=new StringBuilder(128);
        return GetUserObjectInformationW(desktop,2,name,256,out _)?name.ToString():null;
    }
    [DllImport("kernel32.dll")]private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr OpenInputDesktop(uint flags,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint access);
    [DllImport("user32.dll")]private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(IntPtr handle,int index,StringBuilder value,uint length,out uint needed);
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
