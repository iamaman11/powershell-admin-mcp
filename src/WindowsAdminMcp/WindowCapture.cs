using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;

[JsonConverter(typeof(JsonStringEnumConverter<WindowCaptureCode>))]
public enum WindowCaptureCode
{
    COMPLETED, INVALID_ARGUMENT, SERVICE_UNAVAILABLE, USER_CONTEXT_UNAVAILABLE,
    DESKTOP_UNAVAILABLE, STALE_WINDOW, ACCESS_DENIED, WINDOW_MINIMIZED,
    CAPTURE_UNSUPPORTED, FRAME_UNAVAILABLE, IMAGE_TOO_LARGE,
    CAPTURE_FAILED, WORKER_TIMEOUT, WORKER_FAILED
}

public sealed record WindowCaptureResult(
    [property: JsonPropertyName("status")] WindowCaptureCode Status,
    [property: JsonPropertyName("window_id")] string? WindowId = null,
    [property: JsonPropertyName("width")] int? Width = null,
    [property: JsonPropertyName("height")] int? Height = null,
    [property: JsonPropertyName("mimeType")] string? MimeType = null,
    [property: JsonPropertyName("bytes")] int? Bytes = null,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("elapsed_ms")] long? ElapsedMs = null);

internal sealed record WindowCaptureWire(
    [property: JsonPropertyName("result")] WindowCaptureResult Result,
    [property: JsonPropertyName("png")] byte[]? Png = null);

// Windows Graphics Capture captures the HWND composition surface, not pixels of
// the foreground desktop. Works when covered; minimized/protected windows may fail.
internal static class WindowCapture
{
    private const int MaxBytes = 2_000_000;
    private const int MaxPixels = 16_000_000;
    private static readonly Guid CaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceIid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    internal static bool Valid(string id, int maxWidth) =>
        WindowCatalog.TryParseId(id, out _) && maxWidth is >= 256 and <= 1920;

    internal static async Task<WindowCaptureWire> CaptureInWorkerAsync(string id, int maxWidth)
    {
        if (!Valid(id, maxWidth)) return new(new(WindowCaptureCode.INVALID_ARGUMENT));
        using var self = Process.GetCurrentProcess();
        var desktop = DesktopCapture.ObserveDesktop(self.SessionId);
        if (desktop.Code != CaptureCode.COMPLETED)
            return new(new(WindowCaptureCode.DESKTOP_UNAVAILABLE, Detail: desktop.Code.ToString()));
        var resolved = WindowCatalog.ResolveId(id, self.SessionId, out var hwnd);
        if (resolved != WindowResolveCode.OK)
            return new(new(MapResolve(resolved), Detail: resolved.ToString()));
        return await CaptureCoreAsync(hwnd, id, maxWidth);
    }

    internal static async Task<WindowCaptureWire> CaptureCoreAsync(IntPtr hwnd, string id, int maxWidth)
    {
        var timer = Stopwatch.StartNew();
        WindowCaptureWire Fail(WindowCaptureCode code, string? detail = null) =>
            new(new(code, id, Detail: detail, ElapsedMs: timer.ElapsedMilliseconds));
        if (hwnd == IntPtr.Zero || !Valid(id, maxWidth)) return Fail(WindowCaptureCode.INVALID_ARGUMENT);
        if (IsIconic(hwnd)) return Fail(WindowCaptureCode.WINDOW_MINIMIZED);
        // Respect a target application's declared capture exclusion. If the
        // affinity cannot be read, WGC itself still enforces OS protection.
        if (GetWindowDisplayAffinity(hwnd, out uint affinity) && affinity != 0)
            return Fail(WindowCaptureCode.ACCESS_DENIED, "CAPTURE_PROTECTED");
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) return Fail(WindowCaptureCode.CAPTURE_UNSUPPORTED);
            var item = CreateItem(hwnd);
            var size = item.Size;
            if (size.Width < 1 || size.Height < 1 || (long)size.Width * size.Height > MaxPixels)
                return Fail(WindowCaptureCode.IMAGE_TOO_LARGE);
            using var device = CreateDevice();
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 2,
                new SizeInt32 { Width = size.Width, Height = size.Height });
            using var session = pool.CreateCaptureSession(item);
            var ready = new TaskCompletionSource<Direct3D11CaptureFrame>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            int first = 0;
            pool.FrameArrived += (sender, _) =>
            {
                if (Interlocked.Exchange(ref first, 1) != 0) return;
                try
                {
                    var frame = sender.TryGetNextFrame();
                    if (frame is null) ready.TrySetException(new InvalidOperationException("EMPTY_FRAME"));
                    else if (!ready.TrySetResult(frame)) frame.Dispose();
                }
                catch (Exception e) { ready.TrySetException(e); }
            };
            session.StartCapture();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var frame = await ready.Task.WaitAsync(deadline.Token);
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
            int w = Math.Min(bitmap.PixelWidth, maxWidth);
            int h = Math.Max(1, (int)Math.Round((double)bitmap.PixelHeight * w / bitmap.PixelWidth));
            if (h > 1080)
            {
                w = Math.Max(1, (int)Math.Floor((double)w * 1080 / h));
                h = 1080;
            }
            for (int attempt = 0; attempt < 5; attempt++)
            {
                using var stream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetSoftwareBitmap(bitmap);
                encoder.BitmapTransform.ScaledWidth = (uint)w;
                encoder.BitmapTransform.ScaledHeight = (uint)h;
                await encoder.FlushAsync();
                if (stream.Size <= MaxBytes)
                {
                    var length = checked((uint)stream.Size);
                    using var reader = new DataReader(stream.GetInputStreamAt(0));
                    if (await reader.LoadAsync(length) != length)
                        return Fail(WindowCaptureCode.FRAME_UNAVAILABLE);
                    var png = new byte[length];
                    reader.ReadBytes(png);
                    return new(new(WindowCaptureCode.COMPLETED, id, w, h, "image/png", png.Length,
                        ElapsedMs: timer.ElapsedMilliseconds), png);
                }
                w = Math.Max(1, (int)Math.Floor(w * 0.7));
                h = Math.Max(1, (int)Math.Floor(h * 0.7));
            }
            return Fail(WindowCaptureCode.IMAGE_TOO_LARGE);
        }
        catch (OperationCanceledException) { return Fail(WindowCaptureCode.FRAME_UNAVAILABLE, "FRAME_TIMEOUT"); }
        catch (Exception e) when (e is COMException or InvalidOperationException or
                                  NotSupportedException or DllNotFoundException or TypeLoadException or
                                  ArgumentException or UnauthorizedAccessException)
        { return Fail(WindowCaptureCode.CAPTURE_FAILED, e.HResult.ToString("X8")); }
    }

    internal static async Task<CallToolResult> RunIsolatedAsync(string id, int maxWidth,
        CancellationToken cancellationToken)
    {
        if (!Valid(id, maxWidth)) return Response(new(WindowCaptureCode.INVALID_ARGUMENT));
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return Response(new(WindowCaptureCode.WORKER_FAILED));
        using var child = new Process();
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = false
        };
        start.ArgumentList.Add("window-capture-worker");
        start.ArgumentList.Add(id);
        start.ArgumentList.Add(maxWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.Environment.Remove("CONTROL_PLANE_API_KEY");
        start.Environment.Remove("OPENAI_API_KEY");
        child.StartInfo = start;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (!child.Start()) return Response(new(WindowCaptureCode.WORKER_FAILED));
            // The child is limited to a 2 MB image. Cap wire length too.
            var output = child.StandardOutput;
            var chars = new char[3_000_000];
            int used = 0;
            while (used < chars.Length)
            {
                int count = await output.ReadAsync(chars.AsMemory(used), deadline.Token);
                if (count == 0) break;
                used += count;
            }
            if (used == chars.Length) throw new InvalidDataException("WIRE_LIMIT");
            await child.WaitForExitAsync(deadline.Token);
            if (child.ExitCode != 0) return Response(new(WindowCaptureCode.WORKER_FAILED));
            var wire = JsonSerializer.Deserialize<WindowCaptureWire>(new string(chars, 0, used), PipeWire.Json);
            if (wire is null || wire.Png is { Length: > MaxBytes } ||
                (wire.Result.Status == WindowCaptureCode.COMPLETED && wire.Png is null))
                return Response(new(WindowCaptureCode.WORKER_FAILED));
            return Response(wire.Result, wire.Png);
        }
        catch (OperationCanceledException) { return Response(new(WindowCaptureCode.WORKER_TIMEOUT)); }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException or JsonException)
        { return Response(new(WindowCaptureCode.WORKER_FAILED, Detail: e.GetType().Name)); }
        finally
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
        }
    }

    internal static CallToolResult Response(WindowCaptureResult result, byte[]? png = null) => new()
    {
        IsError = png is null,
        StructuredContent = JsonSerializer.SerializeToElement(result, PipeWire.Json),
        Content = png is null
            ? [new TextContentBlock { Text = JsonSerializer.Serialize(result, PipeWire.Json) }]
            : [new ImageContentBlock { MimeType = "image/png",
                Data = Encoding.ASCII.GetBytes(Convert.ToBase64String(png)) }]
    };

    internal static WindowCaptureCode MapResolve(WindowResolveCode code) => code switch
    {
        WindowResolveCode.ACCESS_DENIED => WindowCaptureCode.ACCESS_DENIED,
        WindowResolveCode.WRONG_SESSION => WindowCaptureCode.USER_CONTEXT_UNAVAILABLE,
        _ => WindowCaptureCode.STALE_WINDOW
    };

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr hwnd, in Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, in Guid iid);
    }

    private static GraphicsCaptureItem CreateItem(IntPtr hwnd)
    {
        var factory = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        IntPtr pointer = factory.CreateForWindow(hwnd, CaptureItemIid);
        try { return GraphicsCaptureItem.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    private static IDirect3DDevice CreateDevice()
    {
        const uint bgraSupport = 0x20;
        // D3D_DRIVER_TYPE_HARDWARE = 1, D3D11_SDK_VERSION = 7.
        int hr = D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, bgraSupport,
            IntPtr.Zero, 0, 7, out var d3d, out _, out var context);
        if (hr < 0)
        {
            // A VM or remote test host might have no hardware Direct3D adapter.
            // D3D_DRIVER_TYPE_WARP = 5; never capture the entire desktop as fallback.
            hr = D3D11CreateDevice(IntPtr.Zero, 5, IntPtr.Zero, bgraSupport,
                IntPtr.Zero, 0, 7, out d3d, out _, out context);
        }
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        try
        {
            if (context != IntPtr.Zero) Marshal.Release(context);
            var iid = DxgiDeviceIid;
            hr = Marshal.QueryInterface(d3d, in iid, out var dxgi);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            try
            {
                hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                try { return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally { Marshal.Release(d3d); }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
        uint flags, IntPtr featureLevels, uint levels, uint sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice,
        out IntPtr inspectableDevice);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
}
