// Moore PC Mechanic - AMD Radeon graphics
// AMD has no public "newest driver for this card" service like NVIDIA's, and it splits cards into
// separate driver branches (current RX cards get monthly drivers, older Polaris/Vega cards get occasional
// ones, and the oldest Radeons none). So instead of guessing download links, the app reads what's
// installed (the Adrenalin version AMD writes into the display driver's registry key, the Windows driver
// version and date) and hands updating to AMD's own tools: the AMD Software: Adrenalin Edition app
// (it checks AMD for the right driver for the exact card) or AMD's Auto-Detect and Install tool.

using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MoorePCMechanic.Services;

public sealed class AmdGpu
{
    public string Name { get; init; } = "";
    public string WinVersion { get; init; } = "";       // e.g. 32.0.21045.16010
    public string? Adrenalin { get; init; }              // e.g. 26.10.1 (null if only Windows' basic AMD driver is installed)
    public DateTime? DriverDate { get; init; }
    public bool Integrated { get; init; }                // Radeon graphics built into a Ryzen processor
    public string Generation { get; init; } = "";       // "current", "older" or "legacy"
    public string? AppPath { get; init; }                // RadeonSoftware.exe if AMD Software: Adrenalin Edition is installed

    public int AgeDays => DriverDate is DateTime d ? (int)(DateTime.Today - d.Date).TotalDays : -1;
}

public static class AmdGpuService
{
    public const string DriversPage = "https://www.amd.com/en/support/download/drivers.html";
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Every AMD graphics adapter in the PC (a Radeon card and/or Ryzen built-in graphics).</summary>
    public static List<AmdGpu> Find()
    {
        var list = new List<AmdGpu>();
        var app = FindApp();
        using var s = new ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate, AdapterCompatibility FROM Win32_VideoController");
        foreach (ManagementObject o in s.Get())
        {
            using (o)
            {
                var name = (o["Name"] as string ?? "").Trim();
                var maker = o["AdapterCompatibility"] as string ?? "";
                bool amd = name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || name.StartsWith("AMD", StringComparison.OrdinalIgnoreCase)
                           || maker.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) || maker.Equals("AMD", StringComparison.OrdinalIgnoreCase);
                if (!amd) continue;
                DateTime? date = null;
                try { if (o["DriverDate"] is string dd) date = ManagementDateTimeConverter.ToDateTime(dd); } catch { }
                list.Add(new AmdGpu
                {
                    Name = name,
                    WinVersion = o["DriverVersion"] as string ?? "",
                    Adrenalin = AdrenalinVersion(name),
                    DriverDate = date,
                    Integrated = IsIntegrated(name),
                    Generation = Generation(name),
                    AppPath = app,
                });
            }
        }
        // the graphics card first, built-in graphics second
        return list.OrderBy(g => g.Integrated).ToList();
    }

    /// <summary>AMD's installer writes the Adrenalin release (e.g. "26.10.1") into the display adapter's driver key.</summary>
    private static string? AdrenalinVersion(string gpuName)
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (cls == null) return null;
            string? any = null;
            foreach (var sub in cls.GetSubKeyNames().Where(n => Regex.IsMatch(n, @"^\d{4}$")))
            {
                using var k = cls.OpenSubKey(sub);
                var v = k?.GetValue("RadeonSoftwareVersion") as string;
                if (string.IsNullOrWhiteSpace(v)) continue;
                if (string.Equals(k!.GetValue("DriverDesc") as string, gpuName, StringComparison.OrdinalIgnoreCase)) return v.Trim();
                any ??= v.Trim();
            }
            return any;
        }
        catch { return null; }
    }

    private static string? FindApp()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var rel in new[] { @"AMD\CNext\CNext\RadeonSoftware.exe", @"AMD\CNext\CNext\AMDRSServ.exe" })
        {
            var p = Path.Combine(pf, rel);
            if (File.Exists(p)) return rel.EndsWith("RadeonSoftware.exe") ? p : Path.Combine(Path.GetDirectoryName(p)!, "RadeonSoftware.exe");
        }
        return null;
    }

    private static bool IsIntegrated(string n) =>
        Regex.IsMatch(n, @"Radeon\(TM\) Graphics|Radeon Graphics|Radeon\(TM\) \d{3}M|Radeon \d{3}M|Vega \d+ Graphics|Radeon\(TM\) Vega|Radeon R[2-7] Graphics", RegexOptions.IgnoreCase)
        && !Regex.IsMatch(n, @"\bRX\b", RegexOptions.IgnoreCase);

    /// <summary>"current" = RX 5000 and newer / recent Ryzen graphics (regular drivers); "older" = Polaris and Vega
    /// (RX 400/500, Vega, Radeon VII: occasional drivers on their own branch); "legacy" = HD / R5-R9 era (no new drivers).</summary>
    internal static string Generation(string n)
    {
        var u = n.ToUpperInvariant();
        if (Regex.IsMatch(u, @"\bRX\s?[45]\d{2}X?\b") || u.Contains("VEGA") || u.Contains("RADEON VII")) // RX 460-590 (Polaris), Vega
            return "older";
        if (Regex.IsMatch(u, @"\bHD\s?\d{4}") || Regex.IsMatch(u, @"\bR[579]\s?\d{3}") || u.Contains("R9 FURY") || u.Contains("R9 NANO") || Regex.IsMatch(u, @"\bR[2-7] GRAPHICS"))
            return "legacy";
        return "current";
    }

    /// <summary>One line for the System check and the Drivers tab.</summary>
    public static (FindingLevel Level, string Title, string Detail) Describe(AmdGpu g)
    {
        string ver = g.Adrenalin != null ? $"Adrenalin {g.Adrenalin}" : "Windows' basic AMD driver (no Adrenalin)";
        string date = g.DriverDate is DateTime d ? $", from {d:MMM d, yyyy}" : "";
        string what = $"{ver} (Windows driver {g.WinVersion}{date})";
        string how = g.AppPath != null
            ? "Open AMD Software and use System > Check for updates; it picks the right driver for this exact card."
            : "Install AMD Software: Adrenalin Edition with AMD's Auto-Detect and Install tool; it finds the right driver for this exact card and keeps it updated.";

        if (g.Generation == "legacy")
            return (FindingLevel.Tip, $"{g.Name}: AMD no longer makes new drivers for this card",
                $"{what}. This Radeon generation only gets the last driver AMD released for it, so there's nothing newer to wait for. " +
                "It's fine for everyday use; for games or a newer Windows, a newer graphics card is the upgrade.");
        if (g.Adrenalin == null && !g.Integrated)
            return (FindingLevel.Warning, $"{g.Name}: AMD's driver isn't installed",
                $"{what}. Without AMD's own driver the card runs slower and misses features (and AMD Software for settings, recording and updates). {how}");
        int age = g.AgeDays;
        string older = g.Generation == "older" ? " This card is on AMD's older-cards branch (RX 400/500 and Vega), which gets fewer updates, so a driver a few months old can still be the newest." : "";
        if (age > (g.Generation == "older" ? 400 : 180))
            return (FindingLevel.Tip, $"{g.Name}: driver is {age / 30} months old",
                $"{what}. {how}{older}");
        return (FindingLevel.Good, $"{g.Name}: {ver}", $"{what}.{older} {how}".Trim());
    }

    public static void OpenApp(AmdGpu g)
    {
        if (g.AppPath == null || !File.Exists(g.AppPath)) { OpenDriversPage(); return; }
        Process.Start(new ProcessStartInfo(g.AppPath) { UseShellExecute = true });
    }

    public static void OpenDriversPage() =>
        Process.Start(new ProcessStartInfo(DriversPage) { UseShellExecute = true });
}
