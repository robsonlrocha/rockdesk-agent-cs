using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using RockDeskAgent.Api;
using RockDeskAgent.Config;

namespace RockDeskAgent.Remote;

/// <summary>
/// Worker de sessão remota.
/// Roda como SUBPROCESS na sessão interativa do usuário (Session N),
/// lançado pelo serviço via SessionHelper.LaunchInUserSession().
/// Nesta sessão, CopyFromScreen acessa o display real normalmente.
/// </summary>
public class RemoteWorker
{
    private static readonly ILogger Logger = AgentLogger.Get<RemoteWorker>();

    private readonly AgentConfig _cfg;
    private readonly string _token;
    private readonly string _relayUrl;
    private readonly int _queueId;

    private ScreenCapture? _capture;
    private ClientWebSocket? _ws;
    private int _screenW, _screenH;
    private int _fps = 15, _quality = 70;
    private bool _running;
    private readonly CancellationTokenSource _cts = new();

    public RemoteWorker(AgentConfig cfg, string token, string relayUrl, int queueId)
    {
        _cfg      = cfg;
        _token    = token;
        _relayUrl = relayUrl;
        _queueId  = queueId;
    }

    public async Task RunAsync(CancellationToken serviceCt = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serviceCt, _cts.Token);
        var ct = linked.Token;

        // Reage imediatamente quando o desktop muda após SendSAS
        InputInjector.DesktopChangedAfterSas += newDesk =>
        {
            Logger.LogInformation("DesktopChangedAfterSas: '{D}' — recriando captura imediatamente.", newDesk);
            ScreenCapture.SwitchToInputDesktop();
            _capture?.Dispose();
            _capture = new ScreenCapture();
            _screenW = _capture.Width; _screenH = _capture.Height;
            _ = SendJsonAsync(new { type = "screen_info", w = _screenW, h = _screenH }, _cts.Token);
        };

        // Troca para o input desktop antes de criar ScreenCapture
        ScreenCapture.SwitchToInputDesktop();
        try { _capture = new ScreenCapture(); _screenW = _capture.Width; _screenH = _capture.Height; }
        catch (Exception ex)
        {
            Logger.LogError("ScreenCapture init falhou: {E}", ex.Message);
            await MarkErrorAsync($"ScreenCapture: {ex.Message}", CancellationToken.None);
            return;
        }

        var wsUrl = _relayUrl.TrimEnd('/') + $"/agent/{_token}";
        Logger.LogInformation("RemoteWorker: conectando a {U}", wsUrl[..Math.Min(40, wsUrl.Length)]);

        try
        {
            _ws = new ClientWebSocket();
            _ws.Options.SetRequestHeader("User-Agent", $"RockDeskAgentCS/{AgentConfig.AgentVersion}");
            await _ws.ConnectAsync(new Uri(wsUrl), ct);
            Logger.LogInformation("Conectado ao relay. Tela: {W}x{H}", _screenW, _screenH);
            _running = true;

            await SendJsonAsync(new { type = "screen_info", w = _screenW, h = _screenH }, ct);

            var captureTask = Task.Run(() => CaptureLoopAsync(ct), ct);
            await ReceiveLoopAsync(ct);

            _running = false;
            await _cts.CancelAsync();
            try { await captureTask; } catch { }

            await MarkStatusAsync("ended", "", CancellationToken.None);
        }
        catch (OperationCanceledException) { Logger.LogInformation("RemoteWorker: cancelado."); }
        catch (Exception ex)
        {
            Logger.LogWarning("RemoteWorker erro: {E}", ex.Message);
            await MarkErrorAsync(ex.Message, CancellationToken.None);
        }
        finally
        {
            _capture?.Dispose();
            _ws?.Dispose();
        }
    }

    // ── Captura → relay ───────────────────────────────────────────────
    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        // Troca o thread do capture para o input desktop
        ScreenCapture.SwitchToInputDesktop();
        int sent = 0, frameCheck = 0;
        while (_running && !ct.IsCancellationRequested)
        {
            // A cada ~1s verifica se desktop mudou (bloqueio ↔ desbloqueio)
            frameCheck++;
            if (frameCheck % 15 == 0 && _capture != null && _capture.DesktopChanged())
            {
                ScreenCapture.SwitchToInputDesktop();
                _capture.Dispose();
                _capture = new ScreenCapture();
                _screenW = _capture.Width; _screenH = _capture.Height;
                await SendJsonAsync(new { type = "screen_info", w = _screenW, h = _screenH }, ct);
            }
            var t0 = DateTime.UtcNow;
            try
            {
                var jpeg = _capture?.CaptureJpeg(_quality);
                if (jpeg != null && _ws?.State == WebSocketState.Open)
                {
                    var frame = new byte[1 + jpeg.Length];
                    frame[0] = 0x01;
                    Buffer.BlockCopy(jpeg, 0, frame, 1, jpeg.Length);
                    await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
                    if (++sent == 1)
                        Logger.LogInformation("1º frame: {Kb} KB", jpeg.Length / 1024);
                }
                else if (jpeg == null)
                {
                    Logger.LogWarning("CaptureJpeg=null — tela inacessível?");
                    await Task.Delay(500, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogDebug("Capture erro: {E}", ex.Message);
                await Task.Delay(200, ct);
            }
            var elapsed = DateTime.UtcNow - t0;
            var sleep   = TimeSpan.FromSeconds(1.0 / _fps) - elapsed;
            if (sleep > TimeSpan.Zero) await Task.Delay(sleep, ct);
        }
        Logger.LogInformation("CaptureLoop encerrado. Frames: {N}", sent);
    }

    // ── Recebe mensagens do viewer ─────────────────────────────────────
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            try
            {
                var r = await _ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) { Logger.LogInformation("Relay fechou."); break; }
                if (r.MessageType == WebSocketMessageType.Text)
                    HandleMessage(JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, r.Count)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogDebug("Receive erro: {E}", ex.Message); break;
            }
        }
    }

    // ── Comandos do viewer ─────────────────────────────────────────────
    private void HandleMessage(JsonNode? msg)
    {
        var t = msg?["type"]?.GetValue<string>() ?? "";
        switch (t)
        {
            case "mouse_move":
                InputInjector.MouseMove(Get(msg, "x"), Get(msg, "y"), _screenW, _screenH); break;
            case "mouse_down":
                InputInjector.MouseDown(Get(msg, "x"), Get(msg, "y"), Get(msg, "button"), _screenW, _screenH); break;
            case "mouse_up":
                InputInjector.MouseUp(Get(msg, "x"), Get(msg, "y"), Get(msg, "button"), _screenW, _screenH); break;
            case "mouse_scroll":
                InputInjector.MouseScroll(Get(msg, "delta")); break;
            case "key_down":
                InputInjector.KeyDown(Get(msg, "vk")); break;
            case "key_up":
                InputInjector.KeyUp(Get(msg, "vk")); break;
            case "ctrl_alt_del":
                var ok = InputInjector.TrySendSAS();
                _ = SendJsonAsync(new { type = "cad_ack", ok, desk = "cs-native" }, _cts.Token);
                break;
            case "quality":
                _fps     = Math.Clamp(Get(msg, "fps"),          1, 30);
                _quality = Math.Clamp(Get(msg, "jpeg_quality"), 20, 95);
                break;
            case "viewer_disconnected":
                _running = false; _cts.Cancel(); break;
        }
    }

    private static int Get(JsonNode? n, string k) => n?[k]?.GetValue<int>() ?? 0;

    private async Task SendJsonAsync(object obj, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(obj));
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private async Task MarkStatusAsync(string status, string? err, CancellationToken ct)
    {
        try { await new PortalClient(_cfg).UpdateRemoteSessionStatusAsync(_queueId, status, err, ct); }
        catch { }
    }

    private Task MarkErrorAsync(string msg, CancellationToken ct) =>
        MarkStatusAsync("error", msg[..Math.Min(msg.Length, 490)], ct);
}
