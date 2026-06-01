using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RockDeskAgent.Remote;

/// <summary>
/// Captura de tela para o subprocess (Session N, token SYSTEM do winlogon).
/// Usa ImpersonateLoggedOnUser antes de capturar — garante que CopyFromScreen
/// opera no contexto do usuário interativo, capturando qualquer desktop
/// (Default quando logado, Winlogon quando tela bloqueada).
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
        // Garante que estamos no desktop de input correto
        _currentDesktop = SwitchToInputDesktop();
        Width  = GetSystemMetrics(SM_CXSCREEN);
        Height = GetSystemMetrics(SM_CYSCREEN);
        if (Width  <= 0) Width  = 1920;
        if (Height <= 0) Height = 1080;
        Logger.LogInformation("ScreenCapture: desktop='{D}' {W}x{H}", _currentDesktop, Width, Height);
    }

    /// <summary>
    /// Captura JPEG. Usa ImpersonateLoggedOnUser para garantir contexto correto
    /// tanto no desktop Default (logado) quanto Winlogon (bloqueado).
    /// </summary>
    public byte[]? CaptureJpeg(int quality = 70)
    {
        // Obtém dimensões atuais
        var w = GetSystemMetrics(SM_CXSCREEN);
        var h = GetSystemMetrics(SM_CYSCREEN);
        if (w > 0 && h > 0 && (w != Width || h != Height))
        {
            Width = w; Height = h;
            Logger.LogInformation("Resolução: {W}x{H}", Width, Height);
        }

        // Tenta com impersonação do usuário (funciona no Default e Winlogon)
        var jpeg = CaptureWithImpersonation(Width, Height, quality);
        if (jpeg != null) return jpeg;

        // Fallback: sem impersonação (pode funcionar em alguns sistemas)
        return CaptureDirectly(Width, Height, quality);
    }

    private static byte[]? CaptureWithImpersonation(int w, int h, int quality)
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (!WTSQueryUserToken(sessionId, out var userTok)) return null;

        try
        {
            if (!ImpersonateLoggedOnUser(userTok)) return null;
            try
            {
                return CaptureDirectly(w, h, quality);
            }
            finally { RevertToSelf(); }
        }
        finally { CloseHandle(userTok); }
    }

    private static byte[]? CaptureDirectly(int w, int h, int quality)
    {
        try
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g   = Graphics.FromImage(bmp);
            g.CopyFromScreen(0, 0, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

            using var ep = new EncoderParameters(1);
            ep.Param[0]  = new EncoderParameter(Encoder.Quality, (long)quality);
            using var ms = new MemoryStream();
            bmp.Save(ms, JpegCodec, ep);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Logger.LogWarning("CaptureDirectly: {E}", ex.Message);
            return null;
        }
    }

    // ── Desktop switching ─────────────────────────────────────────────
    public static string SwitchToInputDesktop()
    {
        try
        {
            // Garante que o processo está em WinSta0
            var hWS = OpenWindowStationW("WinSta0", false, 0x037F);
            if (hWS != IntPtr.Zero) { SetProcessWindowStation(hWS); CloseWindowStation(hWS); }

            // Abre o desktop de input ativo (Default ou Winlogon)
            var hDesk = OpenInputDesktop(0, false, 0x01FF);
            if (hDesk == IntPtr.Zero)
            {
                Logger.LogWarning("OpenInputDesktop retornou NULL (err={E})",
                    Marshal.GetLastWin32Error());
                return "Unknown";
            }

            SetThreadDesktop(hDesk);

            var buf = new char[256]; uint len = 0;
            GetUserObjectInformationW(hDesk, UOI_NAME, buf, (uint)(buf.Length * 2), ref len);
            CloseDesktop(hDesk);
            var name = new string(buf, 0, (int)(len / 2)).TrimEnd('\0');
            if (string.IsNullOrEmpty(name)) name = "Default";
            Logger.LogDebug("SwitchToInputDesktop: '{D}'", name);
            return name;
        }
        catch (Exception ex)
        {
            Logger.LogWarning("SwitchToInputDesktop: {E}", ex.Message);
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
            GetUserObjectInformationW(hDesk, UOI_NAME, buf, (uint)(buf.Length * 2), ref len);
            CloseDesktop(hDesk);
            return new string(buf, 0, (int)(len / 2)).TrimEnd('\0');
        }
        catch { return "Unknown"; }
    }

    public bool DesktopChanged()
    {
        var desk = GetInputDesktopName();
        if (!string.IsNullOrEmpty(desk) && desk != "Unknown" && desk != _currentDesktop)
        {
            Logger.LogInformation("Desktop mudou: '{O}' → '{N}'", _currentDesktop, desk);
            return true;
        }
        return false;
    }

    // ── P/Invoke ──────────────────────────────────────────────────────
    const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    const uint UOI_NAME = 2;

    [DllImport("kernel32.dll")] static extern uint   WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll")] static extern bool   WTSQueryUserToken(uint sid, out IntPtr tok);
    [DllImport("advapi32.dll")] static extern bool   ImpersonateLoggedOnUser(IntPtr tok);
    [DllImport("advapi32.dll")] static extern bool   RevertToSelf();
    [DllImport("kernel32.dll")] static extern bool   CloseHandle(IntPtr h);
    [DllImport("user32.dll")]   static extern int    GetSystemMetrics(int n);
    [DllImport("user32.dll")]   static extern IntPtr OpenWindowStationW(string n, bool i, uint a);
    [DllImport("user32.dll")]   static extern bool   SetProcessWindowStation(IntPtr h);
    [DllImport("user32.dll")]   static extern bool   CloseWindowStation(IntPtr h);
    [DllImport("user32.dll")]   static extern IntPtr OpenInputDesktop(uint f, bool i, uint a);
    [DllImport("user32.dll")]   static extern bool   SetThreadDesktop(IntPtr h);
    [DllImport("user32.dll")]   static extern bool   CloseDesktop(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformationW(IntPtr o, uint cls, [Out] char[] buf, uint len, ref uint needed);

    public void Dispose() { }
}
