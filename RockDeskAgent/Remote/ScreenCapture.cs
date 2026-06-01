using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RockDeskAgent.Remote;

/// <summary>
/// Captura de tela.
/// O subprocess roda com token SYSTEM do winlogon (Session N).
/// SYSTEM pode trocar para qualquer desktop via SetThreadDesktop:
/// - WinSta0\Default quando logado
/// - WinSta0\Winlogon quando tela bloqueada
/// </summary>
public class ScreenCapture : IDisposable
{
    private static readonly ILogger Logger = AgentLogger.Get<ScreenCapture>();
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public int Width  { get; private set; }
    public int Height { get; private set; }

    private string _currentDesktop = "";

    public ScreenCapture()
    {
        _currentDesktop = SwitchToInputDesktop();
        Width  = GetSystemMetrics(0);
        Height = GetSystemMetrics(1);
        if (Width  <= 0) Width  = 1920;
        if (Height <= 0) Height = 1080;
        Logger.LogInformation("ScreenCapture: desktop='{D}' {W}x{H}", _currentDesktop, Width, Height);
    }

    /// <summary>Captura JPEG do desktop de input atual.</summary>
    public byte[]? CaptureJpeg(int quality = 70)
    {
        try
        {
            // Atualiza resolução se mudou
            var w = GetSystemMetrics(0);
            var h = GetSystemMetrics(1);
            if (w > 0 && h > 0 && (w != Width || h != Height))
            {
                Width = w; Height = h;
                Logger.LogInformation("Resolução: {W}x{H}", Width, Height);
            }

            using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            using var g   = Graphics.FromImage(bmp);
            g.CopyFromScreen(0, 0, 0, 0, new Size(Width, Height),
                             CopyPixelOperation.SourceCopy);

            using var ep = new EncoderParameters(1);
            ep.Param[0]  = new EncoderParameter(Encoder.Quality, (long)quality);
            using var ms = new MemoryStream();
            bmp.Save(ms, JpegCodec, ep);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Logger.LogWarning("CaptureJpeg: {E}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Verifica se o desktop mudou (bloqueio / desbloqueio).
    /// Retorna true se mudou — o caller deve recriar ScreenCapture.
    /// </summary>
    public bool DesktopChanged()
    {
        var desk = GetInputDesktopName();
        if (desk != _currentDesktop && desk != "Unknown" && desk != "")
        {
            Logger.LogInformation("Desktop mudou: '{O}' → '{N}'", _currentDesktop, desk);
            return true;
        }
        return false;
    }

    // ── Desktop switching ─────────────────────────────────────────────
    [DllImport("user32.dll")] static extern IntPtr OpenWindowStationW(string n, bool i, uint a);
    [DllImport("user32.dll")] static extern bool   SetProcessWindowStation(IntPtr h);
    [DllImport("user32.dll")] static extern bool   CloseWindowStation(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint f, bool i, uint a);
    [DllImport("user32.dll")] static extern bool   SetThreadDesktop(IntPtr h);
    [DllImport("user32.dll")] static extern bool   CloseDesktop(IntPtr h);
    [DllImport("user32.dll")] static extern int    GetSystemMetrics(int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformationW(IntPtr o, uint cls, [Out] char[] buf, uint len, ref uint needed);

    public static string SwitchToInputDesktop()
    {
        try
        {
            // 1. Garante que o processo está em WinSta0
            var hWS = OpenWindowStationW("WinSta0", false, 0x037F);
            if (hWS != IntPtr.Zero) { SetProcessWindowStation(hWS); CloseWindowStation(hWS); }

            // 2. Abre o desktop de input ativo (SYSTEM pode abrir Winlogon)
            var hDesk = OpenInputDesktop(0, false, 0x01FF);
            if (hDesk == IntPtr.Zero) return "Unknown";

            // 3. Troca o thread para este desktop
            SetThreadDesktop(hDesk);

            // 4. Lê o nome
            var buf = new char[256]; uint len = 0;
            GetUserObjectInformationW(hDesk, 2, buf, (uint)(buf.Length * 2), ref len);
            CloseDesktop(hDesk);
            var name = new string(buf, 0, (int)(len / 2)).TrimEnd('\0');
            return string.IsNullOrEmpty(name) ? "Default" : name;
        }
        catch (Exception ex)
        {
            Logger.LogDebug("SwitchToInputDesktop: {E}", ex.Message);
            return "Unknown";
        }
    }

    public static string GetInputDesktopName()
    {
        try
        {
            var hDesk = OpenInputDesktop(0, false, 0x01FF);
            if (hDesk == IntPtr.Zero) return "Unknown";
            var buf = new char[256]; uint len = 0;
            GetUserObjectInformationW(hDesk, 2, buf, (uint)(buf.Length * 2), ref len);
            CloseDesktop(hDesk);
            var name = new string(buf, 0, (int)(len / 2)).TrimEnd('\0');
            return string.IsNullOrEmpty(name) ? "Default" : name;
        }
        catch { return "Unknown"; }
    }

    public void Dispose() { }
}
