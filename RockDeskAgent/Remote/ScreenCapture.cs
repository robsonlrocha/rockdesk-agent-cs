using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RockDeskAgent.Remote;

/// <summary>
/// Captura de tela via System.Drawing.Graphics.CopyFromScreen.
/// Simples, confiável, sem P/Invoke complexo.
/// Funciona no desktop onde o processo está rodando (Default ou Winlogon).
/// </summary>
public class ScreenCapture : IDisposable
{
    private static readonly ILogger Logger = AgentLogger.Get<ScreenCapture>();
    private static readonly ImageCodecInfo JpegCodec;
    private static readonly EncoderParameter[] QualityParams;

    public int Width  { get; private set; }
    public int Height { get; private set; }

    static ScreenCapture()
    {
        JpegCodec = ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        // pré-aloca um array de parâmetro — será reutilizado
        QualityParams = new EncoderParameter[1];
    }

    public ScreenCapture()
    {
        RefreshDimensions();
        Logger.LogInformation("ScreenCapture pronto ({W}x{H})", Width, Height);
    }

    private void RefreshDimensions()
    {
        Width  = GetSystemMetrics(0); // SM_CXVIRTUALSCREEN ou SM_CXSCREEN
        Height = GetSystemMetrics(1);
        if (Width <= 0 || Height <= 0) { Width = 1920; Height = 1080; }
    }

    /// <summary>Captura um frame e retorna bytes JPEG.</summary>
    public byte[]? CaptureJpeg(int quality = 70)
    {
        try
        {
            // Atualiza dimensões caso a resolução mudou
            var w = GetSystemMetrics(0);
            var h = GetSystemMetrics(1);
            if (w > 0 && h > 0 && (w != Width || h != Height))
            {
                Width  = w;
                Height = h;
                Logger.LogInformation("Resolução atualizada: {W}x{H}", Width, Height);
            }

            using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            using var g   = Graphics.FromImage(bmp);
            // CopyFromScreen copia do desktop atual do processo
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
            Logger.LogDebug("CaptureJpeg erro: {E}", ex.Message);
            return null;
        }
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int n);

    public void Dispose() { }
}
