using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RockDeskAgent.Config;

namespace RockDeskAgent.Api;

/// <summary>Cliente HTTP para o portal ASP — compatível com o agente Python.</summary>
public class PortalClient
{
    private readonly HttpClient _http;
    private readonly AgentConfig _cfg;
    private static readonly ILogger Logger = AgentLogger.Get<PortalClient>();

    public PortalClient(AgentConfig cfg)
    {
        _cfg = cfg;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("User-Agent", $"RockDeskAgentCS/{AgentConfig.AgentVersion}");
    }

    /// <summary>POST form-encoded para a API do portal.</summary>
    public async Task<JsonNode?> PostAsync(Dictionary<string, string> fields,
                                            CancellationToken ct = default)
    {
        try
        {
            using var content = new FormUrlEncodedContent(fields);
            using var resp = await _http.PostAsync(_cfg.PortalUrl, content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return JsonNode.Parse(body);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("PortalClient.PostAsync erro: {Msg}", ex.Message);
            return null;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    public Task<JsonNode?> HeartbeatAsync(string hostname, string privateIp,
                                           string publicIp, CancellationToken ct = default)
        => PostAsync(new()
        {
            ["action"]        = "heartbeat",
            ["device_key"]    = _cfg.DeviceKey,
            ["hostname"]      = hostname,
            ["private_ip"]    = privateIp,
            ["public_ip"]     = publicIp,
            ["agent_version"] = AgentConfig.AgentVersion + "-cs",
        }, ct);

    public Task<JsonNode?> CheckCommandsAsync(CancellationToken ct = default)
        => PostAsync(new() { ["action"] = "check_commands", ["device_key"] = _cfg.DeviceKey }, ct);

    public Task<JsonNode?> CheckRemotePendingAsync(CancellationToken ct = default)
        => PostAsync(new() { ["action"] = "check_remote_pending", ["device_key"] = _cfg.DeviceKey }, ct);

    public Task<JsonNode?> GetRemoteSessionQueueAsync(CancellationToken ct = default)
        => PostAsync(new() { ["action"] = "get_remote_session_queue", ["device_key"] = _cfg.DeviceKey }, ct);

    public Task<JsonNode?> UpdateRemoteSessionStatusAsync(int queueId, string status,
                                                           string? errorLog = null,
                                                           CancellationToken ct = default)
    {
        var fields = new Dictionary<string, string>
        {
            ["action"]     = "update_remote_session_status",
            ["device_key"] = _cfg.DeviceKey,
            ["queue_id"]   = queueId.ToString(),
            ["status"]     = status,
        };
        if (!string.IsNullOrEmpty(errorLog))
            fields["error_log"] = errorLog[..Math.Min(errorLog.Length, 490)];
        return PostAsync(fields, ct);
    }

    public Task<JsonNode?> VerifyCodeAsync(string code, string hostname,
                                            CancellationToken ct = default)
        => PostAsync(new()
        {
            ["action"]   = "verify",
            ["code"]     = code,
            ["hostname"] = hostname,
        }, ct);

    public Task<JsonNode?> SendFullDataAsync(Dictionary<string, string> hwData,
                                              CancellationToken ct = default)
        => PostAsync(hwData, ct);
}
