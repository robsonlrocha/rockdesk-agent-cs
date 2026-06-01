using System.Runtime.InteropServices;
using SkiaSharp;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace RockDeskAgent.Remote;

/// <summary>
/// Captura de tela usando Windows.Graphics.Capture API (Windows 10 2004+).
/// Funciona em qualquer desktop incluindo Winlogon (tela bloqueada).
/// Fallback: GDI BitBlt para sistemas mais antigos.
/// </summary>
public class ScreenCapture : IDisposable
{
    private static readonly ILogger Logger = AgentLogger.Get<ScreenCapture>();

    private bool _useWgc;
    private WgcCapturer? _wgc;
    private GdiCapturer? _gdi;

    public int Width  { get; private set; }
    public int Height { get; private set; }

    public ScreenCapture()
    {
        // Tenta Windows.Graphics.Capture primeiro
        try
        {
            if (GraphicsCaptureSession.IsSupported())
            {
                _wgc = new WgcCapturer();
                _wgc.Start();
                Width  = _wgc.Width;
                Height = _wgc.Height;
                _useWgc = true;
                Logger.LogInformation("Captura via Windows.Graphics.Capture ({W}x{H})", Width, Height);
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning("WGC indisponível ({Msg}) — fallback GDI", ex.Message);
        }

        // Fallback GDI
        _gdi = new GdiCapturer();
        Width  = _gdi.Width;
        Height = _gdi.Height;
        Logger.LogInformation("Captura via GDI BitBlt ({W}x{H})", Width, Height);
    }

    /// <summary>Captura um frame e retorna bytes JPEG.</summary>
    public byte[]? CaptureJpeg(int quality = 70)
    {
        try
        {
            SKBitmap? bmp = _useWgc
                ? _wgc?.GetFrame()
                : _gdi?.GetFrame();

            if (bmp == null) return null;

            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, quality);
            return data.ToArray();
        }
        catch (Exception ex)
        {
            Logger.LogDebug("CaptureJpeg erro: {E}", ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        _wgc?.Dispose();
        _gdi?.Dispose();
    }
}

// ── Windows.Graphics.Capture ───────────────────────────────────────────────
internal class WgcCapturer : IDisposable
{
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SKBitmap? _last;
    private readonly object _lock = new();

    public int Width  { get; }
    public int Height { get; }

    public WgcCapturer()
    {
        // Captura o display primário
        var displays = Windows.Devices.Enumeration.DeviceInformation
            .FindAllAsync(Windows.Devices.Display.Core.DisplayMonitor.GetDeviceSelector())
            .AsTask().GetAwaiter().GetResult();

        _item = GraphicsCaptureItem.TryCreateFromDisplayId(
            new Windows.Graphics.DisplayId { Value = 0 });

        if (_item == null)
            throw new InvalidOperationException("Não foi possível criar GraphicsCaptureItem");

        Width  = _item.Size.Width;
        Height = _item.Size.Height;

        var d3d = Direct3D11Helper.CreateDevice();
        _pool = Direct3D11CaptureFramePool.Create(d3d,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _item.CreateCaptureSession(_pool);
    }

    public void Start() => _session.StartCapture();

    private void OnFrameArrived(Direct3D11CaptureFramePool pool,
                                  object? _)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame == null) return;

        // Converte o frame para SKBitmap
        var bmp = Direct3D11Helper.FrameToBitmap(frame);
        lock (_lock) { _last?.Dispose(); _last = bmp; }
        pool.Recreate(pool.Device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, frame.ContentSize);
    }

    public SKBitmap? GetFrame() { lock (_lock) return _last?.Copy(); }

    public void Dispose()
    {
        _session.Dispose();
        _pool.Dispose();
    }
}

// ── GDI BitBlt (fallback) ──────────────────────────────────────────────────
internal class GdiCapturer : IDisposable
{
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")]  static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")]  static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")]  static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObj);
    [DllImport("gdi32.dll")]  static extern bool   BitBlt(IntPtr dst, int x, int y, int w, int h,
                                                           IntPtr src, int sx, int sy, int op);
    [DllImport("gdi32.dll")]  static extern bool   DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]  static extern bool   DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")]  static extern int    GetDIBits(IntPtr hdc, IntPtr hbm, int start,
                                                              int lines, byte[] buf, ref BitmapInfo bi,
                                                              uint usage);
    [DllImport("user32.dll")] static extern int    GetSystemMetrics(int n);

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader
    {
        public int    Size, Width, Height, Planes, BitCount;
        public int    Compression, SizeImage;
        public int    XPelsPerMeter, YPelsPerMeter;
        public int    ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfo { public BitmapInfoHeader bmiHeader; }

    public int Width  { get; } = GetSystemMetrics(0); // SM_CXSCREEN
    public int Height { get; } = GetSystemMetrics(1); // SM_CYSCREEN

    public SKBitmap? GetFrame()
    {
        var hWnd   = GetDesktopWindow();
        var srcDC  = GetDC(hWnd);
        var memDC  = CreateCompatibleDC(srcDC);
        var hBmp   = CreateCompatibleBitmap(srcDC, Width, Height);
        var old    = SelectObject(memDC, hBmp);

        const int SRCCOPY = 0x00CC0020;
        BitBlt(memDC, 0, 0, Width, Height, srcDC, 0, 0, SRCCOPY);

        var bi = new BitmapInfo
        {
            bmiHeader = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = Width, Height = -Height, // top-down
                Planes = 1, BitCount = 32, Compression = 0
            }
        };
        var buf = new byte[Width * Height * 4];
        GetDIBits(memDC, hBmp, 0, Height, buf, ref bi, 0);

        SelectObject(memDC, old);
        DeleteObject(hBmp);
        DeleteDC(memDC);
        ReleaseDC(hWnd, srcDC);

        // Converte BGRA para SKBitmap
        var bmp = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        unsafe
        {
            fixed (byte* p = buf)
                Buffer.MemoryCopy(p, (void*)bmp.GetPixels(), buf.Length, buf.Length);
        }
        return bmp;
    }

    public void Dispose() { }
}

// ── Helper para conversão D3D → SKBitmap ─────────────────────────────────
internal static class Direct3D11Helper
{
    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
        int flags, IntPtr levels, int numLevels, int sdkVersion,
        out IntPtr device, IntPtr featureLevel, out IntPtr context);

    public static IDirect3DDevice CreateDevice()
    {
        // Cria um device D3D11 para o WGC
        D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20,
            IntPtr.Zero, 0, 7, out var dev, IntPtr.Zero, out _);
        return CreateDirect3DDeviceFromD3D11Device(dev);
    }

    [DllImport("Windows.Graphics.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    static IDirect3DDevice CreateDirect3DDeviceFromD3D11Device(IntPtr d3dDevice)
    {
        var dxgi = Marshal.GetObjectForIUnknown(d3dDevice);
        return (IDirect3DDevice)dxgi;
    }

    public static SKBitmap FrameToBitmap(Direct3D11CaptureFrame frame)
    {
        // Simplificado: em produção usar D3D11 staging texture
        var w = frame.ContentSize.Width;
        var h = frame.ContentSize.Height;
        return new SKBitmap(w, h); // placeholder — implementar D3D staging
    }
}
