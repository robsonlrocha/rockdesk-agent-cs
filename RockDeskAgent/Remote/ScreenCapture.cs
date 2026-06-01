using System.Drawing.Imaging;

namespace RockDeskAgent.Remote;

/// <summary>
/// Captura de tela via System.Drawing.
/// Este código roda no subprocess na sessão do usuário (Session N),
/// onde Graphics.CopyFromScreen acessa o display real diretamente.
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
        Refresh();
        Logger.LogInformation("ScreenCapture pronto ({W}x{H})", Width, Height);
    }

    public void Refresh()
    {
        Width  = Screen.PrimaryScreen?.Bounds.Width  ?? 1920;
        Height = Screen.PrimaryScreen?.Bounds.Height ?? 1080;
    }

    public byte[]? CaptureJpeg(int quality = 70)
    {
        try
        {
            var w = Screen.PrimaryScreen?.Bounds.Width  ?? Width;
            var h = Screen.PrimaryScreen?.Bounds.Height ?? Height;
            if (w != Width || h != Height) { Width = w; Height = h; }

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
            Logger.LogWarning("CaptureJpeg erro: {E}", ex.Message);
            return null;
        }
    }

    public void Dispose() { }
}
