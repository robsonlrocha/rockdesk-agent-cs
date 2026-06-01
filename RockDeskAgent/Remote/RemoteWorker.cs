using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using RockDeskAgent.Config;

namespace RockDeskAgent.Remote;

/// <summary>
/// Worker de sessão remota: conecta ao relay, captura tela e processa input.
/// Equivalente ao _remote_worker_main() do agente Python, mas em C#.
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
    private int _fps = 15;
    private int _quality = 70;
    private bool _running;
    private CancellationTokenSource _cts = new();

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

        // Inicializa captura de tela
        try
        {
            _capture = new ScreenCapture();
            _screenW = _capture.Width;
            _screenH = _capture.Height;
        }
        catch (Exception ex)
        {
            Logger.LogError("Falha ao inicializar ScreenCapture: {E}", ex.Message);
            return;
        }

        var wsUrl = _relayUrl.TrimEnd('/') + $"/agent/{_token}";
        Logger.LogInformation("RemoteWorker: conectando a {Url}", wsUrl[..Math.Min(40, wsUrl.Length)]);

        try
        {
            _ws = new ClientWebSocket();
            _ws.Options.SetRequestHeader("User-Agent", $"RockDeskAgentCS/{AgentConfig.AgentVersion}");
            await _ws.ConnectAsync(new Uri(wsUrl), ct);

            Logger.LogInformation("RemoteWorker: conectado ao relay (C# WebSocket nativo)");
            _running = true;

            // Troca o thread de receive para o desktop de input (para SendSAS e input)
            var desk = ScreenCapture.SwitchToInputDesktop();
            Logger.LogInformation("RemoteWorker: receive thread → desktop='{D}'", desk);

            // Envia screen_info inicial (dimensões reais virão do capture loop)
            await SendJsonAsync(new { type = "screen_info", w = _screenW, h = _screenH }, ct);

            // Inicia loop de captura em background
            var captureTask = Task.Run(() => CaptureLoopAsync(ct), ct);

            // Loop de recebimento de mensagens do viewer
            await ReceiveLoopAsync(ct);

            _running = false;
            _cts.Cancel();
            await captureTask;
        }
        catch (OperationCanceledException) { Logger.LogInformation("RemoteWorker: cancelado."); }
        catch (Exception ex) { Logger.LogWarning("RemoteWorker erro: {E}", ex.Message); }
        finally
        {
            _capture?.Dispose();
            _ws?.Dispose();
        }
    }

    // ── Captura de tela → relay ────────────────────────────────────────
    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        // Garante que este thread está no desktop correto antes de capturar
        var deskName = ScreenCapture.SwitchToInputDesktop();
        Logger.LogInformation("CaptureLoop: desktop='{D}'", deskName);

        var interval   = TimeSpan.FromSeconds(1.0 / _fps);
        int framesSent = 0;
        int frameCheck = 0;

        while (_running && !ct.IsCancellationRequested)
        {
            var t0 = DateTime.UtcNow;
            try
            {
                // A cada 60 frames (~4s a 15fps), verifica se desktop mudou
                frameCheck++;
                if (frameCheck % 60 == 0)
                {
                    var newDesk = ScreenCapture.SwitchToInputDesktop();
                    if (newDesk != deskName)
                    {
                        Logger.LogInformation("Desktop mudou: '{Old}' → '{New}'", deskName, newDesk);
                        deskName = newDesk;
                        // Reinicia captura com nova resolução
                        _capture?.Dispose();
                        _capture = new ScreenCapture();
                        _screenW = _capture.Width;
                        _screenH = _capture.Height;
                        await SendJsonAsync(new { type = "screen_info", w = _screenW, h = _screenH }, ct);
                    }
                }

                var jpeg = _capture?.CaptureJpeg(_quality);
                if (jpeg != null && _ws?.State == WebSocketState.Open)
                {
                    var frame = new byte[1 + jpeg.Length];
                    frame[0] = 0x01;
                    Buffer.BlockCopy(jpeg, 0, frame, 1, jpeg.Length);
                    await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
                    framesSent++;
                    if (framesSent == 1)
                        Logger.LogInformation("1º frame enviado ({Kb} KB)", jpeg.Length / 1024);
                }
                else if (jpeg == null)
                {
                    Logger.LogDebug("CaptureJpeg retornou null — aguardando...");
                    await Task.Delay(500, ct); // Backoff em caso de falha
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning("CaptureLoop erro: {E}", ex.Message);
                await Task.Delay(1000, ct);
            }
            var elapsed = DateTime.UtcNow - t0;
            var sleep   = interval - elapsed;
            if (sleep > TimeSpan.Zero) await Task.Delay(sleep, ct);
        }
        Logger.LogInformation("CaptureLoop encerrado. Frames enviados: {N}", framesSent);
    }

    // ── Recebe mensagens do viewer via relay ───────────────────────────
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            try
            {
                var result = await _ws.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Logger.LogInformation("RemoteWorker: relay fechou a conexão.");
                    break;
                }
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buf, 0, result.Count);
                    HandleMessage(JsonNode.Parse(json));
                }
                // Mensagens binárias do viewer (upload de arquivo)
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    HandleBinary(buf[..result.Count]);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogDebug("ReceiveLoop erro: {E}", ex.Message);
                break;
            }
        }
    }

    // ── Processa comandos do viewer ────────────────────────────────────
    private void HandleMessage(JsonNode? msg)
    {
        if (msg == null) return;
        var type = msg["type"]?.GetValue<string>() ?? "";

        switch (type)
        {
            case "mouse_move":
                InputInjector.MouseMove(
                    msg["x"]?.GetValue<int>() ?? 0,
                    msg["y"]?.GetValue<int>() ?? 0,
                    _screenW, _screenH);
                break;

            case "mouse_down":
                InputInjector.MouseDown(
                    msg["x"]?.GetValue<int>() ?? 0,
                    msg["y"]?.GetValue<int>() ?? 0,
                    msg["button"]?.GetValue<int>() ?? 1,
                    _screenW, _screenH);
                break;

            case "mouse_up":
                InputInjector.MouseUp(
                    msg["x"]?.GetValue<int>() ?? 0,
                    msg["y"]?.GetValue<int>() ?? 0,
                    msg["button"]?.GetValue<int>() ?? 1,
                    _screenW, _screenH);
                break;

            case "mouse_scroll":
                InputInjector.MouseScroll(msg["delta"]?.GetValue<int>() ?? 0);
                break;

            case "key_down":
                InputInjector.KeyDown(msg["vk"]?.GetValue<int>() ?? 0);
                break;

            case "key_up":
                InputInjector.KeyUp(msg["vk"]?.GetValue<int>() ?? 0);
                break;

            case "ctrl_alt_del":
                // ✅ Em C# SCM Service, SendSAS(TRUE) funciona nativamente
                var ok = InputInjector.TrySendSAS();
                // Envia ack ao viewer
                _ = SendJsonAsync(new { type = "cad_ack", ok, desk = "native" }, _cts.Token);
                break;

            case "quality":
                _fps     = Math.Clamp(msg["fps"]?.GetValue<int>() ?? 15, 1, 30);
                _quality = Math.Clamp(msg["jpeg_quality"]?.GetValue<int>() ?? 70, 20, 95);
                Logger.LogInformation("Qualidade: fps={Fps} q={Q}", _fps, _quality);
                break;

            case "viewer_connected":
                Logger.LogInformation("Viewer conectado.");
                break;

            case "viewer_disconnected":
                Logger.LogInformation("Viewer desconectou.");
                _running = false;
                _cts.Cancel();
                break;
        }
    }

    private void HandleBinary(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1) return;
        // Protocolo upload: [0x03][36B tid][chunk]
        if (data[0] == 0x03) { /* TODO: file upload */ }
    }

    private async Task SendJsonAsync(object obj, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var json = System.Text.Json.JsonSerializer.Serialize(obj);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }
}
