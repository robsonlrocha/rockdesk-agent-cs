using System.Text.Json;
using System.Text.Json.Serialization;

namespace RockDeskAgent.Config;

public class AgentConfig
{
    [JsonPropertyName("device_key")]
    public string DeviceKey { get; set; } = "";

    [JsonPropertyName("device_id")]
    public int DeviceId { get; set; }

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = "";

    [JsonPropertyName("portal_url")]
    public string PortalUrl { get; set; } = "https://rockdesk.mnti.com.br/modules/inventory/api.asp";

    [JsonPropertyName("relay_url")]
    public string RelayUrl { get; set; } = "wss://remote.mnti.com.br";

    // ─── Constantes ────────────────────────────────────────────────────
    public static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     "RockDeskAgent");

    public static readonly string ConfigFile = Path.Combine(ConfigDir, "config.json");
    public static readonly string LogFile    = Path.Combine(ConfigDir, "agent_cs.log");
    public static readonly string SvcName    = "RockDeskAgent";
    public static readonly string SvcDisplay = "RockDesk Agent";
    public static readonly string AgentVersion = "1.0.9";
    public static readonly string PortalDownloadUrl =
        "https://rockdesk.mnti.com.br/downloads/RockDeskAgent.exe";
    public static readonly string PortalDownloadUrlCS =
        "https://rockdesk.mnti.com.br/downloads/RockDeskAgentCS.exe";

    // ─── Carrega / salva ───────────────────────────────────────────────
    public static AgentConfig Load()
    {
        if (!File.Exists(ConfigFile)) return new AgentConfig();
        try
        {
            var json = File.ReadAllText(ConfigFile);
            return JsonSerializer.Deserialize<AgentConfig>(json) ?? new AgentConfig();
        }
        catch { return new AgentConfig(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigFile, json);
    }

    public bool IsRegistered => !string.IsNullOrEmpty(DeviceKey);
}
