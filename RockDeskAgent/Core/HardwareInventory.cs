using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RockDeskAgent.Config;

namespace RockDeskAgent.Core;

/// <summary>Coleta informações de hardware via WMI — compatível com a API do portal.</summary>
public static class HardwareInventory
{
    private static readonly ILogger Logger = AgentLogger.GetNamed(nameof(HardwareInventory));

    public static Dictionary<string, string> Collect(AgentConfig cfg)
    {
        var d = new Dictionary<string, string>
        {
            ["action"]        = "update",
            ["device_key"]    = cfg.DeviceKey,
            ["hostname"]      = Environment.MachineName,
            ["agent_version"] = AgentConfig.AgentVersion + "-cs",
        };

        Logger.LogInformation("Coletando hardware...");

        Safe("computador",  () => CollectComputer(d));
        Safe("CPU",         () => CollectCpu(d));
        Safe("OS",          () => CollectOs(d));
        Safe("memória",     () => CollectMemory(d));
        Safe("rede",        () => CollectNetwork(d));
        Safe("IP público",  () => CollectPublicIp(d));
        Safe("discos",      () => CollectDisks(d));
        Safe("software",    () => CollectSoftware(d));

        Logger.LogInformation("Hardware coletado: {Count} campos.", d.Count);
        return d;
    }

    private static void Safe(string section, Action fn)
    {
        try { fn(); }
        catch (Exception ex) { Logger.LogWarning("Coleta {S}: {E}", section, ex.Message); }
    }

    // ── Helpers WMI ────────────────────────────────────────────────────
    private static string Wmi(string cls, string prop, string? where = null)
    {
        try
        {
            var query = $"SELECT {prop} FROM {cls}" + (where != null ? $" WHERE {where}" : "");
            using var s = new ManagementObjectSearcher(query);
            foreach (ManagementObject o in s.Get())
            {
                var v = o[prop]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        catch (Exception ex) { Logger.LogDebug("WMI {C}.{P}: {E}", cls, prop, ex.Message); }
        return "";
    }

    private static List<Dictionary<string, string>> WmiAll(string cls, params string[] props)
    {
        var result = new List<Dictionary<string, string>>();
        try
        {
            var query = $"SELECT {string.Join(",", props)} FROM {cls}";
            using var s = new ManagementObjectSearcher(query);
            foreach (ManagementObject o in s.Get())
            {
                var row = new Dictionary<string, string>();
                foreach (var p in props)
                    row[p] = o[p]?.ToString()?.Trim() ?? "";
                result.Add(row);
            }
        }
        catch (Exception ex) { Logger.LogDebug("WmiAll {C}: {E}", cls, ex.Message); }
        return result;
    }

    // ── Seções ─────────────────────────────────────────────────────────
    private static void CollectComputer(Dictionary<string, string> d)
    {
        d["comp_manufacturer"]  = Wmi("Win32_ComputerSystem", "Manufacturer");
        d["comp_brand"]         = d["comp_manufacturer"];
        d["comp_model"]         = Wmi("Win32_ComputerSystem", "Model");
        d["comp_serial"]        = Wmi("Win32_BIOS", "SerialNumber");
        d["comp_domain"]        = Wmi("Win32_ComputerSystem", "Domain");
        d["comp_part_of_domain"]= Wmi("Win32_ComputerSystem", "PartOfDomain") == "True" ? "1" : "0";
    }

    private static void CollectCpu(Dictionary<string, string> d)
    {
        d["cpu_model"]  = Wmi("Win32_Processor", "Name");
        d["cpu_brand"]  = Wmi("Win32_Processor", "Manufacturer");
        d["cpu_count"]  = Wmi("Win32_ComputerSystem", "NumberOfLogicalProcessors");
        d["cpu_serial"] = Wmi("Win32_Processor", "ProcessorId");
        // Geração Intel (ex: "10th Gen")
        var gen = Wmi("Win32_Processor", "Description");
        d["cpu_generation"] = gen;
    }

    private static void CollectOs(Dictionary<string, string> d)
    {
        d["os_name"]         = Wmi("Win32_OperatingSystem", "Caption");
        d["os_arch"]         = Wmi("Win32_OperatingSystem", "OSArchitecture");
        d["os_install_date"] = FormatWmiDate(Wmi("Win32_OperatingSystem", "InstallDate"));
        d["os_last_boot"]    = FormatWmiDate(Wmi("Win32_OperatingSystem", "LastBootUpTime"));
    }

    private static void CollectMemory(Dictionary<string, string> d)
    {
        var sticks = WmiAll("Win32_PhysicalMemory",
            "Capacity", "Manufacturer", "Speed", "MemoryType", "PartNumber");
        long total = 0;
        var rows = new List<string>();
        foreach (var s in sticks)
        {
            var cap = long.TryParse(s.GetValueOrDefault("Capacity", "0"), out var c) ? c : 0;
            total += cap;
            rows.Add($"{s.GetValueOrDefault("Manufacturer")}|{cap / 1_073_741_824}GB|{s.GetValueOrDefault("Speed")}MHz");
        }
        d["total_memory_gb"]    = (total / 1_073_741_824.0).ToString("F1");
        d["total_memory_slots"] = sticks.Count.ToString();
        d["memory_sticks_json"] = System.Text.Json.JsonSerializer.Serialize(rows);
    }

    private static void CollectNetwork(Dictionary<string, string> d)
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ua.Address))
                {
                    d["private_ip"]  = ua.Address.ToString();
                    d["mac_address"] = BitConverter.ToString(ni.GetPhysicalAddress().GetAddressBytes())
                                       .Replace("-", ":");
                    d["adapter_name"] = ni.Name;
                    return;
                }
            }
        }
        d["private_ip"]  = "";
        d["mac_address"] = "";
    }

    private static void CollectPublicIp(Dictionary<string, string> d)
    {
        try
        {
            using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            d["public_ip"] = hc.GetStringAsync("https://api.ipify.org")
                               .GetAwaiter().GetResult().Trim();
        }
        catch { d["public_ip"] = ""; }
    }

    private static void CollectDisks(Dictionary<string, string> d)
    {
        var disks = WmiAll("Win32_DiskDrive",
            "Model", "SerialNumber", "Size", "MediaType");
        d["disk_count"] = disks.Count.ToString();
        long totalBytes = 0;
        foreach (var dk in disks)
            if (long.TryParse(dk.GetValueOrDefault("Size", "0"), out var sz)) totalBytes += sz;
        d["total_disk_gb"] = (totalBytes / 1_073_741_824.0).ToString("F0");
    }

    private static void CollectSoftware(Dictionary<string, string> d)
    {
        // Envia contagem apenas — lista completa pelo portal via WMI é muito grande
        var sw = WmiAll("Win32_Product", "Name");
        d["software_count"] = sw.Count.ToString();
    }

    private static string FormatWmiDate(string raw)
    {
        if (raw.Length >= 14 &&
            DateTime.TryParseExact(raw[..14], "yyyyMMddHHmmss",
                null, System.Globalization.DateTimeStyles.None, out var dt))
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        return raw;
    }
}
