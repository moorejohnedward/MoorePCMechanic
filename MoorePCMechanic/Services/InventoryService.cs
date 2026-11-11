using System.Globalization;
using System.Management;
using System.Text;
using Microsoft.Win32;

namespace MoorePCMechanic.Services;

public record InfoItem(string Label, string Value);

public class InfoSection
{
    public InfoSection(string title, string glyph)
    {
        Title = title;
        Glyph = glyph;
    }

    public string Title { get; }
    /// <summary>Segoe MDL2 / Fluent icon character.</summary>
    public string Glyph { get; }
    public List<InfoItem> Items { get; } = new();

    public void Add(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) Items.Add(new InfoItem(label, value.Trim()));
    }
}

public enum FindingLevel { Good, Tip, Warning }

public record Finding(FindingLevel Level, string Title, string Detail)
{
    public string LevelText => Level switch
    {
        FindingLevel.Good => "GOOD",
        FindingLevel.Tip => "TIP",
        _ => "CHECK",
    };
}

public class InventoryResult
{
    public string MachineName { get; set; } = Environment.MachineName;
    public DateTime ScannedAt { get; set; } = DateTime.Now;
    public List<InfoSection> Sections { get; } = new();
    public List<Finding> Findings { get; } = new();

    public string ToReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("MOORE PC MECHANIC - SYSTEM REPORT");
        sb.AppendLine($"Computer: {MachineName}");
        sb.AppendLine($"Scanned:  {ScannedAt:yyyy-MM-dd h:mm tt}");
        sb.AppendLine(new string('=', 60));
        foreach (var s in Sections)
        {
            sb.AppendLine();
            sb.AppendLine($"[{s.Title.ToUpperInvariant()}]");
            int w = s.Items.Count == 0 ? 0 : s.Items.Max(i => i.Label.Length);
            foreach (var i in s.Items)
                sb.AppendLine($"  {i.Label.PadRight(w)} : {i.Value.Replace("\n", "\n" + new string(' ', w + 5))}");
        }
        if (Findings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[MECHANIC'S NOTES]");
            foreach (var f in Findings)
                sb.AppendLine($"  ({f.LevelText}) {f.Title} - {f.Detail}");
        }
        return sb.ToString();
    }
}

/// <summary>Reads the machine's hardware details from Windows (WMI + registry).</summary>
public static class InventoryService
{
    // ---------- WMI helpers ----------

    private static List<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            foreach (ManagementBaseObject mo in searcher.Get()) list.Add(mo);
        }
        catch
        {
            // Class not available on this machine / access denied: skip quietly.
        }
        return list;
    }

    private static string? S(ManagementBaseObject mo, string prop)
    {
        try { return mo[prop]?.ToString()?.Trim(); } catch { return null; }
    }

    private static ulong U(ManagementBaseObject mo, string prop)
    {
        try
        {
            var v = mo[prop];
            return v == null ? 0 : Convert.ToUInt64(v, CultureInfo.InvariantCulture);
        }
        catch { return 0; }
    }

    private static DateTime? D(ManagementBaseObject mo, string prop)
    {
        var s = S(mo, prop);
        if (string.IsNullOrEmpty(s)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); } catch { return null; }
    }

    private static string GB(ulong bytes) => $"{bytes / 1024.0 / 1024 / 1024:0.#} GB";
    private static string DecimalSize(ulong bytes) =>
        bytes >= 1_000_000_000_000 ? $"{bytes / 1e12:0.##} TB" : $"{bytes / 1e9:0} GB";

    // ---------- Scan ----------

    public static InventoryResult Scan()
    {
        var r = new InventoryResult();
        ScanSystem(r);
        ScanCpu(r);
        ScanBoard(r);
        ScanMemory(r);
        ScanGpu(r);
        ScanStorage(r);
        ScanUsb(r);
        ScanNetwork(r);
        if (r.Findings.Count == 0)
            r.Findings.Add(new Finding(FindingLevel.Good, "No issues spotted", "Nothing in the basic checks needs attention."));
        return r;
    }

    private static void ScanSystem(InventoryResult r)
    {
        var sec = new InfoSection("System", "");
        foreach (var cs in Query("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
            sec.Add("Maker / model", $"{S(cs, "Manufacturer")} {S(cs, "Model")}");

        foreach (var os in Query("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem"))
        {
            sec.Add("Windows", $"{S(os, "Caption")} ({S(os, "OSArchitecture")})");
            var displayVersion = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null) as string;
            sec.Add("Version", $"{displayVersion} build {S(os, "BuildNumber")}".Trim());
            var installed = D(os, "InstallDate");
            if (installed != null) sec.Add("Installed", installed.Value.ToString("MMM d, yyyy"));
            var boot = D(os, "LastBootUpTime");
            if (boot != null)
            {
                var up = DateTime.Now - boot.Value;
                sec.Add("Up time", $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m (since {boot.Value:MMM d, h:mm tt})");
                if (up.TotalDays >= 7)
                    r.Findings.Add(new Finding(FindingLevel.Tip, $"Up for {(int)up.TotalDays} days",
                        "A restart clears memory leaks and finishes pending Windows updates. Use Restart, not Shut down (Fast Startup doesn't fully reset)."));
            }
        }
        sec.Add("Computer name", Environment.MachineName);
        sec.Add("Admin mode", AppEnv.IsAdmin() ? "Yes" : "No (some sensors hidden)");
        r.Sections.Add(sec);
    }

    private static void ScanCpu(InventoryResult r)
    {
        var sec = new InfoSection("Processor", "");
        foreach (var p in Query("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation FROM Win32_Processor"))
        {
            sec.Add("CPU", S(p, "Name"));
            sec.Add("Cores / threads", $"{U(p, "NumberOfCores")} cores / {U(p, "NumberOfLogicalProcessors")} threads");
            sec.Add("Base clock", $"{U(p, "MaxClockSpeed") / 1000.0:0.00} GHz");
            var l2 = U(p, "L2CacheSize"); var l3 = U(p, "L3CacheSize");
            if (l2 + l3 > 0) sec.Add("Cache", $"L2 {l2 / 1024.0:0.#} MB, L3 {l3 / 1024.0:0.#} MB");
            sec.Add("Socket", S(p, "SocketDesignation"));
        }
        r.Sections.Add(sec);
    }

    private static void ScanBoard(InventoryResult r)
    {
        var sec = new InfoSection("Motherboard & BIOS", "");
        foreach (var b in Query("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard"))
        {
            sec.Add("Board", $"{S(b, "Manufacturer")} {S(b, "Product")}");
            var ver = S(b, "Version");
            if (!string.IsNullOrEmpty(ver) && !ver.Contains("default", StringComparison.OrdinalIgnoreCase)) sec.Add("Board revision", ver);
        }
        foreach (var bios in Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
        {
            sec.Add("BIOS", $"{S(bios, "Manufacturer")} {S(bios, "SMBIOSBIOSVersion")}");
            var rel = D(bios, "ReleaseDate");
            if (rel != null)
            {
                sec.Add("BIOS date", rel.Value.ToString("MMM d, yyyy"));
                if ((DateTime.Now - rel.Value).TotalDays > 540)
                    r.Findings.Add(new Finding(FindingLevel.Tip, "BIOS is over 18 months old",
                        "Newer BIOS versions often improve memory compatibility and stability. Check your board maker's support page before upgrading RAM."));
            }
        }
        r.Sections.Add(sec);
    }

    private static string MemType(ulong smbiosType) => smbiosType switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => "",
    };

    private static void ScanMemory(InventoryResult r)
    {
        var sec = new InfoSection("Memory (RAM)", "");

        int slots = 0;
        ulong maxKb = 0;
        foreach (var a in Query("SELECT MemoryDevices, MaxCapacityEx, MaxCapacity FROM Win32_PhysicalMemoryArray"))
        {
            slots += (int)U(a, "MemoryDevices");
            var mx = U(a, "MaxCapacityEx");
            if (mx == 0) mx = U(a, "MaxCapacity");
            maxKb += mx;
        }

        var sticks = Query("SELECT BankLabel, DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SMBIOSMemoryType FROM Win32_PhysicalMemory");
        ulong total = 0;
        foreach (var m in sticks) total += U(m, "Capacity");

        sec.Add("Installed", $"{GB(total)} in {sticks.Count} stick{(sticks.Count == 1 ? "" : "s")}");
        if (slots > 0) sec.Add("Slots", $"{sticks.Count} of {slots} used");
        if (maxKb > 0) sec.Add("Board maximum", $"{maxKb / 1024.0 / 1024:0} GB (as reported by the board)");

        var partNumbers = new List<string>();
        uint rated = 0, running = 0;
        string type = "";
        foreach (var m in sticks)
        {
            var slot = S(m, "DeviceLocator") ?? "Slot";
            var part = S(m, "PartNumber") ?? "";
            var maker = S(m, "Manufacturer") ?? "";
            type = MemType(U(m, "SMBIOSMemoryType"));
            var speed = (uint)U(m, "Speed");
            var conf = (uint)U(m, "ConfiguredClockSpeed");
            rated = Math.Max(rated, speed);
            running = running == 0 ? conf : Math.Min(running, conf);
            if (!string.IsNullOrWhiteSpace(part)) partNumbers.Add(part);
            var speedText = conf > 0 && speed > 0 && conf != speed ? $"rated {speed} MT/s, running {conf} MT/s" : $"{Math.Max(conf, speed)} MT/s";
            sec.Add(slot, $"{GB(U(m, "Capacity"))} {type} - {maker} {part}\n{speedText}".Replace("  ", " "));
        }
        r.Sections.Add(sec);

        // ---- Findings ----
        if (sticks.Count == 1 && slots >= 2)
        {
            var part = partNumbers.FirstOrDefault();
            r.Findings.Add(new Finding(FindingLevel.Tip, "Running single-channel memory",
                $"Only 1 of {slots} slots is used. Adding a second matching stick enables dual-channel, which roughly doubles memory bandwidth" +
                (string.IsNullOrEmpty(part) ? "." : $". Match this part number: {part}.") +
                (slots == 4 ? " On 4-slot boards a pair usually goes in A2 and B2 (2nd and 4th from the CPU) - check your manual." : "")));
        }
        else if (sticks.Count >= 2 && sticks.Count % 2 == 0)
        {
            var distinct = partNumbers.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            r.Findings.Add(distinct <= 1
                ? new Finding(FindingLevel.Good, "Dual-channel memory", $"{sticks.Count} matching sticks installed.")
                : new Finding(FindingLevel.Tip, "Mixed memory sticks", "Sticks have different part numbers. They'll usually work, but at the slowest stick's speed and timings."));
        }

        if (rated > 0 && running > 0 && running < rated)
        {
            r.Findings.Add(new Finding(FindingLevel.Tip, "RAM running below its rated speed",
                $"Rated {rated} MT/s but running {running} MT/s. Turning on XMP (Intel) or EXPO (AMD) in the BIOS is usually a free speed boost."));
        }
        else if ((type == "DDR5" && running > 0 && running <= 4800) || (type == "DDR4" && running > 0 && running <= 2666))
        {
            r.Findings.Add(new Finding(FindingLevel.Tip, $"{type} at {running} MT/s - possibly default speed",
                "That's the standard default. If the label on your stick lists a higher speed, enable XMP/EXPO in the BIOS."));
        }
    }

    // GPU VRAM from the display-adapter registry keys (WMI's AdapterRAM caps at 4 GB).
    private static Dictionary<string, ulong> ReadVram()
    {
        var map = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return map;
            foreach (var name in cls.GetSubKeyNames())
            {
                if (name.Length != 4 || !name.All(char.IsDigit)) continue;
                using var k = cls.OpenSubKey(name);
                var desc = k?.GetValue("DriverDesc") as string;
                var raw = k?.GetValue("HardwareInformation.qwMemorySize");
                ulong size = raw switch
                {
                    long l => (ulong)l,
                    byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b, 0),
                    byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                    int i => (uint)i,
                    _ => 0,
                };
                if (desc != null && size > 0) map[desc] = size;
            }
        }
        catch { }
        return map;
    }

    // NVIDIA Windows driver "32.0.15.8157" -> NVIDIA version "581.57"
    private static string? NvidiaVersion(string? winVer)
    {
        if (string.IsNullOrEmpty(winVer)) return null;
        var parts = winVer.Split('.');
        if (parts.Length < 4) return null;
        var digits = parts[2] + parts[3];
        if (digits.Length < 5) return null;
        var last5 = digits[^5..];
        return $"{last5[..3]}.{last5[3..]}";
    }

    private static void ScanGpu(InventoryResult r)
    {
        var sec = new InfoSection("Graphics", "");
        var vram = ReadVram();
        foreach (var g in Query("SELECT Name, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, AdapterCompatibility FROM Win32_VideoController"))
        {
            var name = S(g, "Name") ?? "Display adapter";
            if (name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase) || name.Contains("Remote", StringComparison.OrdinalIgnoreCase)) continue;
            var lines = new List<string>();
            if (vram.TryGetValue(name, out var v)) lines.Add($"{v / 1024.0 / 1024 / 1024:0} GB video memory");
            var drv = S(g, "DriverVersion");
            var date = D(g, "DriverDate");
            var isNvidia = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
            var friendly = isNvidia ? NvidiaVersion(drv) : null;
            lines.Add($"Driver {(friendly != null ? friendly + $" ({drv})" : drv)}" + (date != null ? $", dated {date.Value:MMM d, yyyy}" : ""));
            var w = U(g, "CurrentHorizontalResolution");
            if (w > 0) lines.Add($"Display: {w} x {U(g, "CurrentVerticalResolution")} @ {U(g, "CurrentRefreshRate")} Hz");
            sec.Add(name, string.Join("\n", lines));

            if (date != null && (DateTime.Now - date.Value).TotalDays > 120 && (isNvidia || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)))
                r.Findings.Add(new Finding(FindingLevel.Tip, "Graphics driver is a few months old",
                    $"{name} driver is from {date.Value:MMM yyyy}. Update through the {(isNvidia ? "NVIDIA app" : "AMD Adrenalin app")} - the official source."));
        }
        r.Sections.Add(sec);
    }

    private static string Bus(ulong bus) => bus switch
    {
        17 => "NVMe",
        11 => "SATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        _ => "",
    };

    private static void ScanStorage(InventoryResult r)
    {
        var sec = new InfoSection("Storage", "");
        var disks = Query("SELECT FriendlyName, MediaType, BusType, Size, HealthStatus, FirmwareVersion FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage");
        if (disks.Count > 0)
        {
            foreach (var d in disks)
            {
                var name = S(d, "FriendlyName") ?? "Disk";
                var media = U(d, "MediaType") switch { 3 => "Hard drive", 4 => "SSD", _ => "Drive" };
                var bus = Bus(U(d, "BusType"));
                var health = U(d, "HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };
                sec.Add(name, $"{DecimalSize(U(d, "Size"))} {bus} {media} - health: {health}\nFirmware {S(d, "FirmwareVersion")}".Replace("  ", " "));
                if (health is "Warning" or "Unhealthy")
                    r.Findings.Add(new Finding(FindingLevel.Warning, $"{name} reports {health.ToLower()}",
                        "Back up anything important on this drive now, then check its SMART details on the Live Monitor tab."));
            }
        }
        else
        {
            foreach (var d in Query("SELECT Model, Size, InterfaceType, Status FROM Win32_DiskDrive"))
                sec.Add(S(d, "Model") ?? "Disk", $"{DecimalSize(U(d, "Size"))} {S(d, "InterfaceType")} - status: {S(d, "Status")}");
        }

        foreach (var v in Query("SELECT DeviceID, VolumeName, Size, FreeSpace, FileSystem FROM Win32_LogicalDisk WHERE DriveType = 3"))
        {
            var size = U(v, "Size");
            var free = U(v, "FreeSpace");
            if (size == 0) continue;
            var pctFree = free * 100.0 / size;
            var label = S(v, "VolumeName");
            sec.Add($"Drive {S(v, "DeviceID")}", $"{(string.IsNullOrEmpty(label) ? "" : label + " - ")}{GB(free)} free of {GB(size)} ({pctFree:0}% free)");
            if (pctFree < 15)
                r.Findings.Add(new Finding(FindingLevel.Warning, $"Drive {S(v, "DeviceID")} is getting full",
                    $"Only {pctFree:0}% free. SSDs slow down and Windows updates can fail when space runs low. Aim for 15-20% free."));
        }
        r.Sections.Add(sec);
    }

    private static string UsbVendor(string? pnp)
    {
        if (pnp == null) return "";
        var i = pnp.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        var ven = pnp.Substring(i + 4, Math.Min(4, pnp.Length - i - 4)).ToUpperInvariant();
        return ven switch
        {
            "8086" => "Intel (chipset)",
            "1022" => "AMD (chipset/CPU)",
            "1B21" => "ASMedia (add-in card or extra board controller)",
            "1912" => "Renesas/NEC (add-in card or extra board controller)",
            "1B73" => "Fresco Logic (add-in card)",
            "1106" => "VIA (add-in card or extra board controller)",
            "1B6F" => "Etron (add-in card or extra board controller)",
            "10DE" => "NVIDIA (graphics card USB-C)",
            _ => $"vendor {ven}",
        };
    }

    private static void ScanUsb(InventoryResult r)
    {
        var sec = new InfoSection("USB controllers", "");
        int n = 1;
        foreach (var u in Query("SELECT Name, PNPDeviceID FROM Win32_USBController"))
        {
            var vendor = UsbVendor(S(u, "PNPDeviceID"));
            sec.Add($"Controller {n++}", $"{S(u, "Name")}{(vendor.Length > 0 ? "\n" + vendor : "")}");
        }
        r.Sections.Add(sec);
    }

    private static void ScanNetwork(InventoryResult r)
    {
        var sec = new InfoSection("Network", "");
        foreach (var a in Query("SELECT Name, Speed, NetEnabled FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE"))
        {
            var enabled = S(a, "NetEnabled");
            if (!string.Equals(enabled, "True", StringComparison.OrdinalIgnoreCase)) continue;
            var bps = U(a, "Speed");
            sec.Add(S(a, "Name") ?? "Adapter", bps > 0 && bps < ulong.MaxValue / 2 ? (bps >= 1_000_000_000 ? $"Connected at {bps / 1e9:0.#} Gbps" : $"Connected at {bps / 1e6:0} Mbps") : "Connected");
        }
        r.Sections.Add(sec);
    }
}
