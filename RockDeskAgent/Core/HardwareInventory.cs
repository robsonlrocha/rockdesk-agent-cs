using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using RockDeskAgent.Config;

namespace RockDeskAgent.Core;

/// <summary>
/// Coleta hardware/software via WMI.
/// Formato de campos idêntico ao agente Python para compatibilidade total com o portal.
/// Campos compostos usam delimitadores :: (campos) e ||| (registros).
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
        Safe("discos",      () => CollectDisks(d));
        Safe("rede",        () => CollectNetwork(d));
        Safe("IP público",  () => CollectPublicIp(d));
        Safe("software",    () => CollectSoftware(d));

        Logger.LogInformation("Hardware coletado: {N} campos, {M} módulos RAM, {Dk} discos, {Sw} software.",
            d.Count,
            d.GetValueOrDefault("memory_modules", "").Split(new[]{"|||"}, StringSplitOptions.RemoveEmptyEntries).Length,
            d.GetValueOrDefault("disks", "").Split(new[]{"|||"}, StringSplitOptions.RemoveEmptyEntries).Length,
            d.GetValueOrDefault("software", "").Split(new[]{"|||"}, StringSplitOptions.RemoveEmptyEntries).Length);
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
        d["comp_part_of_domain"] = Wmi("Win32_ComputerSystem", "PartOfDomain").ToLower() == "true" ? "1" : "0";
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
        // Total RAM para campo simples do portal
        long totalBytes = 0;
        int slots = 0;
        var modules = new List<string>();

        using var s = new ManagementObjectSearcher(
            "SELECT Slot,Manufacturer,PartNumber,Speed,MemoryType,Capacity,SerialNumber FROM Win32_PhysicalMemory");
        foreach (ManagementObject o in s.Get())
        {
            slots++;
            var cap = long.TryParse(o["Capacity"]?.ToString(), out var c) ? c : 0;
            totalBytes += cap;
            var capGb = (cap / 1_073_741_824.0).ToString("F1");

            // Formato: slot::brand::model::freq_mhz::mem_type::cap_gb::serial
            var slot  = o["Slot"]?.ToString()          ?? slots.ToString();
            var brand = o["Manufacturer"]?.ToString()  ?? "";
            var model = o["PartNumber"]?.ToString()    ?? "";
            var freq  = o["Speed"]?.ToString()         ?? "0";
            var type  = o["MemoryType"]?.ToString()    ?? "";
            var serial= o["SerialNumber"]?.ToString()  ?? "";

            modules.Add($"{slot}::{brand}::{model}::{freq}::{type}::{capGb}::{serial}");
        }

        d["total_ram"]           = (totalBytes / 1_073_741_824.0).ToString("F1");
        d["total_memory_slots"]  = slots.ToString();
        d["memory_modules"]      = string.Join("|||", modules);
    }

    static void CollectDisks(Dictionary<string, string> d)
    {
        long totalBytes = 0;
        var disks = new List<string>();

        using var s = new ManagementObjectSearcher(
            "SELECT MediaType,Manufacturer,Model,SerialNumber,Size FROM Win32_DiskDrive");
        foreach (ManagementObject o in s.Get())
        {
            var sz = long.TryParse(o["Size"]?.ToString(), out var b) ? b : 0;
            totalBytes += sz;
            var capGb = (sz / 1_073_741_824.0).ToString("F0");

            // Tipo: detecta SSD/HDD/NVMe pela MediaType
            var mediaType = o["MediaType"]?.ToString() ?? "";
            var diskType  = mediaType.Contains("SSD") || mediaType.Contains("Solid") ? "SSD"
                          : mediaType.Contains("NVMe")                               ? "NVMe"
                          : "HDD";

            var brand  = o["Manufacturer"]?.ToString()  ?? "";
            var model  = o["Model"]?.ToString()         ?? "";
            var serial = o["SerialNumber"]?.ToString()  ?? "";

            // Formato: disk_type::brand::model::serial::cap_gb
            disks.Add($"{diskType}::{brand}::{model}::{serial}::{capGb}");
        }

        d["total_disk"] = (totalBytes / 1_073_741_824.0).ToString("F0");
        d["disks"]      = string.Join("|||", disks);
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

    static void CollectSoftware(Dictionary<string, string> d)
    {
        var swList = new List<string>();
        // Lê do registro em vez de Win32_Product (muito lento, pode causar side effects)
        foreach (var regPath in new[] {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        })
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(regPath);
                if (key == null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sk   = key.OpenSubKey(sub);
                        var name  = sk?.GetValue("DisplayName")?.ToString()    ?? "";
                        var ver   = sk?.GetValue("DisplayVersion")?.ToString() ?? "";
                        var date  = sk?.GetValue("InstallDate")?.ToString()    ?? "";
                        var uninst= sk?.GetValue("UninstallString")?.ToString()?? "";
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        // Formato: name::version::install_date::uninstall_string
                        swList.Add($"{name}::{ver}::{date}::{uninst}");
                    }
                    catch { }
                }
            }
            catch { }
        }
        d["software"] = string.Join("|||", swList);
        Logger.LogDebug("Software: {N} itens.", swList.Count);
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
