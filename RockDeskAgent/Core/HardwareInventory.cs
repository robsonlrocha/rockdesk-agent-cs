using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RockDeskAgent.Config;

namespace RockDeskAgent.Core;

/// <summary>Coleta informações de hardware via WMI — compatível com a API do portal Python.</summary>
public static class HardwareInventory
{
    private static readonly ILogger Logger = AgentLogger.Get<HardwareInventory>();

    public static Dictionary<string, string> Collect(AgentConfig cfg)
    {
        var d = new Dictionary<string, string> { ["action"] = "update", ["device_key"] = cfg.DeviceKey };

        try { CollectComputer(d); }    catch (Exception e) { Logger.LogWarning("Coleta computer: {E}", e.Message); }
        try { CollectCpu(d); }         catch (Exception e) { Logger.LogWarning("Coleta cpu: {E}", e.Message); }
        try { CollectOs(d); }          catch (Exception e) { Logger.LogWarning("Coleta os: {E}", e.Message); }
        try { CollectMemory(d); }      catch (Exception e) { Logger.LogWarning("Coleta memory: {E}", e.Message); }
        try { CollectNetwork(d); }     catch (Exception e) { Logger.LogWarning("Coleta network: {E}", e.Message); }
        try { CollectIps(d); }         catch (Exception e) { Logger.LogWarning("Coleta IPs: {E}", e.Message); }

        d["agent_version"] = AgentConfig.AgentVersion + "-cs";
        return d;
    }

    private static string Wmi(string cls, string prop, string? cond = null)
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                $"SELECT {prop} FROM {cls}" + (cond != null ? $" WHERE {cond}" : ""));
            foreach (ManagementObject o in s.Get())
                return o[prop]?.ToString()?.Trim() ?? "";
        }
        catch { }
        return "";
    }

    private static void CollectComputer(Dictionary<string, string> d)
    {
        d["comp_manufacturer"] = Wmi("Win32_ComputerSystem", "Manufacturer");
        d["comp_brand"]        = Wmi("Win32_ComputerSystem", "Manufacturer");
        d["comp_model"]        = Wmi("Win32_ComputerSystem", "Model");
        d["comp_serial"]       = Wmi("Win32_BIOS", "SerialNumber");
        d["comp_domain"]       = Wmi("Win32_ComputerSystem", "Domain");
        d["hostname"]          = Environment.MachineName;
        var partOfDomain = Wmi("Win32_ComputerSystem", "PartOfDomain");
        d["comp_part_of_domain"] = partOfDomain.ToLower() == "true" ? "1" : "0";
    }

    private static void CollectCpu(Dictionary<string, string> d)
    {
        d["cpu_model"]  = Wmi("Win32_Processor", "Name");
        d["cpu_brand"]  = Wmi("Win32_Processor", "Manufacturer");
        d["cpu_count"]  = Wmi("Win32_ComputerSystem", "NumberOfLogicalProcessors");
        d["cpu_serial"] = Wmi("Win32_Processor", "ProcessorId");
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
        try
        {
            long totalKb = 0;
            int slots = 0;
            using var s = new ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMemory");
            foreach (ManagementObject o in s.Get())
            {
                slots++;
                totalKb += Convert.ToInt64(o["Capacity"] ?? 0);
            }
            d["total_memory_gb"]    = (totalKb / 1_073_741_824.0).ToString("F2");
            d["total_memory_slots"] = slots.ToString();
        }
        catch { }
    }

    private static void CollectNetwork(Dictionary<string, string> d)
    {
        // Pega o primeiro adaptador com IP válido
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ua.Address))
                {
                    d["private_ip"] = ua.Address.ToString();
                    d["mac_address"] = ni.GetPhysicalAddress().ToString();
                    break;
                }
            }
            if (d.ContainsKey("private_ip")) break;
        }
    }

    private static void CollectIps(Dictionary<string, string> d)
    {
        if (!d.ContainsKey("private_ip"))
            d["private_ip"] = "";
        // IP público via serviço externo (assíncrono não disponível aqui, usa sync)
        try
        {
            using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            d["public_ip"] = hc.GetStringAsync("https://api.ipify.org").GetAwaiter().GetResult().Trim();
        }
        catch { d["public_ip"] = ""; }
    }

    private static string FormatWmiDate(string raw)
    {
        // WMI dates: "20240515120000.000000+000"
        if (raw.Length >= 14 && DateTime.TryParseExact(raw[..14], "yyyyMMddHHmmss",
            null, System.Globalization.DateTimeStyles.None, out var dt))
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        return "";
    }
}
