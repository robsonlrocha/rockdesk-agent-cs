using System.Runtime.InteropServices;
using RockDeskAgent.Config;
using SkiaSharp;

namespace RockDeskAgent.Remote;

/// <summary>
/// Captura de tela.
/// v1: GDI BitBlt (funciona em qualquer Windows, incluindo tela bloqueada quando
///     o processo está no desktop Winlogon via lpDesktop correto no CreateProcess).
/// v2 (planejado): Windows.Graphics.Capture API para 60fps + desempenho superior.
/// </summary>
public class ScreenCapture : IDisposable
{
    private static readonly ILogger Logger = AgentLogger.Get<ScreenCapture>();
    private readonly GdiCapturer _gdi;

    public int Width  => _gdi.Width;
    public int Height => _gdi.Height;

    public ScreenCapture()
    {
        _gdi = new GdiCapturer();
        Logger.LogInformation("ScreenCapture GDI iniciado ({W}x{H})", Width, Height);
    }

    /// <summary>Captura um frame e retorna bytes JPEG.</summary>
    public byte[]? CaptureJpeg(int quality = 70)
    {
        try
        {
            using var bmp = _gdi.GetFrame();
            if (bmp == null) return null;
            using var img  = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, quality);
            return data.ToArray();
        }
        catch (Exception ex)
        {
            Logger.LogDebug("CaptureJpeg erro: {E}", ex.Message);
            return null;
        }
    }

    public void Dispose() { }
}

// ── GDI BitBlt ────────────────────────────────────────────────────────────
internal class GdiCapturer
{
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int    ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")]  static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]  static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")]  static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")]  static extern bool   BitBlt(IntPtr dst, int x, int y, int w, int h,
                                                           IntPtr src, int sx, int sy, uint op);
    [DllImport("gdi32.dll")]  static extern bool   DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]  static extern bool   DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")]  static extern int    GetDIBits(IntPtr dc, IntPtr bmp, int start,
                                                              int lines, byte[] buf,
                                                              ref BITMAPINFO bi, uint usage);
    [DllImport("user32.dll")] static extern int    GetSystemMetrics(int n);

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint   biSize;
        public int    biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint   biCompression, biSizeImage;
        public int    biXPelsPerMeter, biYPelsPerMeter;
        public uint   biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; [MarshalAs(UnmanagedType.ByValArray, SizeConst=4)] public uint[] bmiColors; }

    const uint SRCCOPY = 0x00CC0020;

    public int Width  { get; } = GetSystemMetrics(0);
    public int Height { get; } = GetSystemMetrics(1);

    public SKBitmap? GetFrame()
    {
        var hwnd  = GetDesktopWindow();
        var srcDC = GetDC(hwnd);
        var memDC = CreateCompatibleDC(srcDC);
        var hBmp  = CreateCompatibleBitmap(srcDC, Width, Height);
        var old   = SelectObject(memDC, hBmp);

        BitBlt(memDC, 0, 0, Width, Height, srcDC, 0, 0, SRCCOPY);

        var bi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize    = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth   = Width,
                biHeight  = -Height, // top-down
                biPlanes  = 1,
                biBitCount = 32,
                biCompression = 0
            },
            bmiColors = new uint[4]
        };
        var buf = new byte[Width * Height * 4];
        GetDIBits(memDC, hBmp, 0, Height, buf, ref bi, 0);

        SelectObject(memDC, old);
        DeleteObject(hBmp);
        DeleteDC(memDC);
        ReleaseDC(hwnd, srcDC);

        // BGRA → SKBitmap
        var skBmp = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        unsafe
        {
            fixed (byte* p = buf)
                Buffer.MemoryCopy(p, (void*)skBmp.GetPixels(), buf.Length, buf.Length);
        }
        return skBmp;
    }
}
