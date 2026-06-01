using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RockDeskAgent.Remote;

/// <summary>
/// Captura de tela para serviços Windows (Session 0).
/// Usa OpenInputDesktop + SetThreadDesktop para acessar o desktop do usuário
/// (WinSta0\Default ou WinSta0\Winlogon) a partir do serviço em Session 0.
/// </summary>
public class ScreenCapture : IDisposable
{
    private static readonly ILogger Logger = AgentLogger.Get<ScreenCapture>();
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public int Width  { get; private set; }
    public int Height { get; private set; }

    public ScreenCapture()
    {
        // Garante que o processo está em WinSta0 antes de qualquer captura
        SwitchToWinSta0();
        SwitchToInputDesktop();
        Width  = GetSystemMetrics(SM_CXSCREEN);
        Height = GetSystemMetrics(SM_CYSCREEN);
        Logger.LogInformation("ScreenCapture pronto ({W}x{H})", Width, Height);
    }

    /// <summary>
    /// Captura o frame atual e retorna JPEG.
    /// Reabre o desktop de input a cada captura para suportar troca
    /// entre Default (logado) e Winlogon (tela bloqueada).
    /// </summary>
    public byte[]? CaptureJpeg(int quality = 70)
    {
        try
        {
            // Atualiza desktop e dimensões
            SwitchToInputDesktop();
            var w = GetSystemMetrics(SM_CXSCREEN);
            var h = GetSystemMetrics(SM_CYSCREEN);
            if (w > 0 && h > 0 && (w != Width || h != Height))
            {
                Width = w; Height = h;
                Logger.LogInformation("Resolução atualizada: {W}x{H}", w, h);
            }

            using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            using var g   = Graphics.FromImage(bmp);
            g.CopyFromScreen(0, 0, 0, 0, new Size(Width, Height),
                             CopyPixelOperation.SourceCopy);

            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
            using var ms = new MemoryStream();
            bmp.Save(ms, JpegCodec, ep);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Logger.LogWarning("CaptureJpeg erro: {E}", ex.Message);
            return null;
        }
    }

    // ── Desktop switching ──────────────────────────────────────────────

    /// <summary>Muda o processo para WinSta0 (window station do usuário).</summary>
    private static void SwitchToWinSta0()
    {
        try
        {
            const uint WINSTA_ALL = 0x0000037F;
            var hWS = OpenWindowStationW("WinSta0", false, WINSTA_ALL);
            if (hWS != IntPtr.Zero)
            {
                SetProcessWindowStation(hWS);
                CloseWindowStation(hWS);
            }
        }
        catch (Exception ex) { Logger.LogDebug("SwitchToWinSta0: {E}", ex.Message); }
    }

    /// <summary>
    /// Muda o thread atual para o desktop de input ativo.
    /// SYSTEM pode acessar WinSta0\Default E WinSta0\Winlogon.
    /// </summary>
    public static string SwitchToInputDesktop()
    {
        try
        {
            const uint DESKTOP_ALL = 0x01FF;
            var hDesk = OpenInputDesktop(0, false, DESKTOP_ALL);
            if (hDesk == IntPtr.Zero) return "Unknown";

            var buf = new char[256];
            uint len = 0;
            GetUserObjectInformationW(hDesk, UOI_NAME, buf, (uint)(buf.Length * 2), ref len);
            var name = new string(buf, 0, (int)(len / 2)).TrimEnd('\0');

            SetThreadDesktop(hDesk);
            CloseDesktop(hDesk);
            return name;
        }
        catch (Exception ex)
        {
            Logger.LogDebug("SwitchToInputDesktop: {E}", ex.Message);
            return "Unknown";
        }
    }

    // ── P/Invoke ──────────────────────────────────────────────────────
    const int SM_CXSCREEN = 0;
    const int SM_CYSCREEN = 1;
    const uint UOI_NAME   = 2;

    [DllImport("user32.dll")] static extern int     GetSystemMetrics(int n);
    [DllImport("user32.dll")] static extern IntPtr  OpenWindowStationW(string name, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool    SetProcessWindowStation(IntPtr h);
    [DllImport("user32.dll")] static extern bool    CloseWindowStation(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr  OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool    SetThreadDesktop(IntPtr h);
    [DllImport("user32.dll")] static extern bool    CloseDesktop(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformationW(IntPtr obj, uint infoClass,
        [Out] char[] buf, uint len, ref uint lenNeeded);

    public void Dispose() { }
}
