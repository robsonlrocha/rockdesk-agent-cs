using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RockDeskAgent.Config;

namespace RockDeskAgent.Core;

/// <summary>
/// Coleta hardware/software via WMI e envia para o portal.
/// Campos idênticos ao agente Python para compatibilidade total.
/// </summary>
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

        Safe("computador",  () => CollectComputer(d));
        Safe("CPU",         () => CollectCpu(d));
        Safe("OS",          () => CollectOs(d));
        Safe("RAM",         () => CollectRam(d));
        Safe("rede",        () => CollectNetwork(d));
        Safe("IP público",  () => CollectPublicIp(d));
        Safe("discos",      () => CollectDisks(d));

        Logger.LogInformation("Hardware coletado ({N} campos).", d.Count);
        return d;
    }

    // ── Seções ─────────────────────────────────────────────────────────
    static void CollectComputer(Dictionary<string, string> d)
    {
        d["comp_manufacturer"]   = Wmi("Win32_ComputerSystem", "Manufacturer");
        d["comp_brand"]          = d["comp_manufacturer"];
        d["comp_model"]          = Wmi("Win32_ComputerSystem", "Model");
        d["comp_serial"]         = Wmi("Win32_BIOS", "SerialNumber");
        d["comp_domain"]         = Wmi("Win32_ComputerSystem", "Domain");
        d["comp_part_of_domain"] = Wmi("Win32_ComputerSystem", "PartOfDomain") == "True" ? "1" : "0";
    }

    static void CollectCpu(Dictionary<string, string> d)
    {
        d["cpu_model"]      = Wmi("Win32_Processor", "Name");
        d["cpu_brand"]      = Wmi("Win32_Processor", "Manufacturer");
        d["cpu_count"]      = Wmi("Win32_ComputerSystem", "NumberOfLogicalProcessors");
        d["cpu_serial"]     = Wmi("Win32_Processor", "ProcessorId");
        d["cpu_generation"] = Wmi("Win32_Processor", "Description");
    }

    static void CollectOs(Dictionary<string, string> d)
    {
        d["os_name"]         = Wmi("Win32_OperatingSystem", "Caption");
        d["os_arch"]         = Wmi("Win32_OperatingSystem", "OSArchitecture");
        d["os_install_date"] = FormatDate(Wmi("Win32_OperatingSystem", "InstallDate"));
        d["os_last_boot"]    = FormatDate(Wmi("Win32_OperatingSystem", "LastBootUpTime"));
    }

    static void CollectRam(Dictionary<string, string> d)
    {
        long total = 0; int slots = 0;
        using var s = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory");
        foreach (ManagementObject o in s.Get())
        {
            if (long.TryParse(o["Capacity"]?.ToString(), out var c)) total += c;
            slots++;
        }
        d["total_ram"]          = (total / 1_073_741_824.0).ToString("F0");
        d["total_memory_slots"] = slots.ToString();
    }

    static void CollectNetwork(Dictionary<string, string> d)
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
                    d["private_ip"] = ua.Address.ToString();
                    return;
                }
            }
        }
        d["private_ip"] = "";
    }

    static void CollectPublicIp(Dictionary<string, string> d)
    {
        try
        {
            using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            d["public_ip"] = hc.GetStringAsync("https://api.ipify.org").GetAwaiter().GetResult().Trim();
        }
        catch { d["public_ip"] = ""; }
    }

    static void CollectDisks(Dictionary<string, string> d)
    {
        long total = 0;
        using var s = new ManagementObjectSearcher("SELECT Size FROM Win32_DiskDrive");
        foreach (ManagementObject o in s.Get())
            if (long.TryParse(o["Size"]?.ToString(), out var sz)) total += sz;
        d["total_disk"] = (total / 1_073_741_824.0).ToString("F0");
    }

    // ── Helpers ────────────────────────────────────────────────────────
    static void Safe(string sec, Action fn)
    {
        try { fn(); }
        catch (Exception ex) { Logger.LogWarning("Coleta {S}: {E}", sec, ex.Message); }
    }

    static string Wmi(string cls, string prop)
    {
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT {prop} FROM {cls}");
            foreach (ManagementObject o in s.Get())
            {
                var v = o[prop]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        catch (Exception ex) { Logger.LogDebug("WMI {C}.{P}: {E}", cls, prop, ex.Message); }
        return "";
    }

    static string FormatDate(string raw)
    {
        if (raw.Length >= 14 &&
            DateTime.TryParseExact(raw[..14], "yyyyMMddHHmmss",
                null, System.Globalization.DateTimeStyles.None, out var dt))
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        return raw;
    }
}
