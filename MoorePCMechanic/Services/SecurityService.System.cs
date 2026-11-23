// Security sweep, part 2: is this copy of Windows (and what's installed on it) still getting security fixes?
// Matters most on an old PC coming back into use: Windows version support, how long since the last
// Windows update, SMB1, the Guest account, remote-control apps, and programs that no longer get fixes.

using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MoorePCMechanic.Services;

public static partial class SecurityService
{
    // =====================================================================
    //  Windows versions and the day they stop getting security updates
    //  Source: Microsoft's Windows 11 release information and lifecycle pages (checked Nov 2026).
    //  Home/Pro get 24 months per yearly version, Enterprise/Education 36.
    // =====================================================================

    private sealed record WinRelease(int Build, string Name, DateTime HomePro, DateTime EntEdu);

    private static readonly WinRelease[] Releases =
    {
        new(19045, "Windows 10 22H2", new DateTime(2025, 10, 14), new DateTime(2025, 10, 14)),
        new(19044, "Windows 10 21H2", new DateTime(2023, 6, 13), new DateTime(2024, 6, 11)),
        new(22000, "Windows 11 21H2", new DateTime(2023, 10, 10), new DateTime(2024, 10, 8)),
        new(22621, "Windows 11 22H2", new DateTime(2024, 10, 8), new DateTime(2025, 10, 14)),
        new(22631, "Windows 11 23H2", new DateTime(2025, 11, 11), new DateTime(2026, 11, 10)),
        new(26100, "Windows 11 24H2", new DateTime(2026, 10, 13), new DateTime(2027, 10, 12)),
        new(26200, "Windows 11 25H2", new DateTime(2027, 10, 12), new DateTime(2028, 10, 10)),
        new(26300, "Windows 11 26H2", new DateTime(2028, 10, 10), new DateTime(2029, 10, 9)),
        new(28000, "Windows 11 26H1", new DateTime(2028, 3, 14), new DateTime(2029, 3, 13)),
    };

    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    internal static void MoreChecks(Action<FindingLevel, string, string, string?, string> add)
    {
        void Try(Action a) { try { a(); } catch { } }
        Try(() => WindowsSupport(add));
        Try(() => WindowsUpdateRecency(add));
        Try(() => Smb1(add));
        Try(() => GuestAccount(add));
        Try(() => RemoteTools(add));
        Try(() => OutOfSupportPrograms(add));
    }

    /// <summary>Fixes added by this part of the sweep. Null if the id isn't one of these.</summary>
    private static string? MoreFix(string id)
    {
        switch (id)
        {
            case "windowsupdate": Open("ms-settings:windowsupdate"); return "Opened Windows Update. Click \"Check for updates\" and install everything, restarting as often as it asks.";
            case "apps": Open("ms-settings:appsfeatures"); return "Opened Installed apps. Uninstall the programs the check listed.";
            case "wu-enable":
                TuneUpService.Run("sc.exe", "config wuauserv start= demand", 30);
                TuneUpService.Run("net.exe", "start wuauserv", 60);
                return "Windows Update service is turned back on. Open Windows Update and check for updates.";
            case "smb1-off":
                var (code, output) = TuneUpService.Run("dism.exe", "/Online /Disable-Feature /FeatureName:SMB1Protocol /NoRestart", 600);
                return code is 0 or 3010 ? "SMB1 is turned off. Restart the PC to finish." : "Couldn't turn SMB1 off: " + LastLine(output);
        }
        if (id.StartsWith("guest-off:"))
        {
            var name = id["guest-off:".Length..];
            var (code, output) = TuneUpService.Run("net.exe", $"user \"{name}\" /active:no", 30);
            return code == 0 ? $"The {name} account is turned off." : "Couldn't turn it off: " + LastLine(output);
        }
        return null;
    }

    private static void WindowsSupport(Action<FindingLevel, string, string, string?, string> add)
    {
        int build = Environment.OSVersion.Version.Build;
        int ubr = RegInt(Registry.LocalMachine, CurrentVersionKey, "UBR") ?? 0;
        string edition = RegStr(CurrentVersionKey, "EditionID") ?? "";
        string disp = RegStr(CurrentVersionKey, "DisplayVersion") ?? "";
        bool ltsc = edition.Contains("EnterpriseS", StringComparison.OrdinalIgnoreCase) || edition.Contains("IoTEnterpriseS", StringComparison.OrdinalIgnoreCase);
        bool ent = edition.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase) || edition.StartsWith("Education", StringComparison.OrdinalIgnoreCase) || edition.StartsWith("IoTEnterprise", StringComparison.OrdinalIgnoreCase);
        string what = $"{(build >= 22000 ? "Windows 11" : "Windows 10")} {disp} {EditionName(edition)} (build {build}.{ubr})".Replace("  ", " ");

        if (edition.StartsWith("Server", StringComparison.OrdinalIgnoreCase))
        {
            add(FindingLevel.Tip, $"Windows Server (build {build}.{ubr})", "Windows Server is supported on its own schedule; check Microsoft's lifecycle page for this version.", null, "Fix");
            return;
        }
        if (ltsc)
        {
            add(FindingLevel.Tip, $"{what}: long-term (LTSC) edition", "LTSC editions are supported on their own longer schedule; check Microsoft's lifecycle page for this one.", null, "Fix");
            return;
        }
        var rel = Releases.FirstOrDefault(r => r.Build == build);
        if (rel == null)
        {
            if (build > 26300)
                add(FindingLevel.Good, $"{what}: preview (Insider) build", "Newer than any public release; it gets updates through the Windows Insider program.", null, "Fix");
            else
                add(FindingLevel.Warning, $"{what}: old version of Windows", "This version no longer gets security updates. Install the latest version through Windows Update.", "windowsupdate", "Open Windows Update");
            return;
        }
        var end = ent ? rel.EntEdu : rel.HomePro;
        var today = DateTime.Today;
        if (today > end)
        {
            string how = build >= 26100
                ? "Windows Update offers 25H2 or 26H2 as a quick install (a small download and one restart)."
                : build >= 22000
                    ? "Install the newest version of Windows 11 through Windows Update."
                    : ent ? "Only safe with paid Extended Security Updates. Upgrade to Windows 11 if the PC qualifies."
                          : "Windows 10 no longer gets security fixes at all (the extra year of updates ended Oct 13, 2026). Upgrade to Windows 11 if this PC qualifies (see \"Windows 11\" under Hardware); otherwise keep it off the internet for anything personal.";
            add(FindingLevel.Warning, $"{rel.Name} no longer gets security updates", $"{what}. Support ended {end:MMM d, yyyy}. {how}", "windowsupdate", "Open Windows Update");
        }
        else if ((end - today).TotalDays <= 90)
            add(FindingLevel.Tip, $"{rel.Name} support ends {end:MMM d, yyyy}", $"{what}. Install the newer version through Windows Update before then.", "windowsupdate", "Open Windows Update");
        else
            add(FindingLevel.Good, $"{rel.Name} gets security updates", $"{what}. Supported until {end:MMM d, yyyy}.", null, "Fix");
    }

    private static string EditionName(string id) => id.ToLowerInvariant() switch
    {
        "core" or "coresinglelanguage" or "corecountryspecific" => "Home",
        "professional" => "Pro",
        "professionalworkstation" => "Pro for Workstations",
        "professionaleducation" => "Pro Education",
        "enterprise" => "Enterprise",
        "education" => "Education",
        _ => id,
    };

    private static void WindowsUpdateRecency(Action<FindingLevel, string, string, string?, string> add)
    {
        // Service turned off entirely?
        if (RegInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\wuauserv", "Start") == 4)
        {
            add(FindingLevel.Warning, "Windows Update is switched off", "The Windows Update service is disabled, so no security fixes arrive. Turn it back on.", "wu-enable", "Turn it back on");
            return;
        }

        DateTime? lastCu = null, lastAny = null;
        string? cuTitle = null;
        var t = Task.Run(() =>
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new InvalidOperationException("Windows Update isn't available.");
            dynamic session = Activator.CreateInstance(type)!;
            dynamic searcher = session.CreateUpdateSearcher();
            int total = searcher.GetTotalHistoryCount();
            if (total <= 0) return;
            dynamic hist = searcher.QueryHistory(0, Math.Min(total, 300));
            int n = hist.Count;
            for (int i = 0; i < n; i++)
            {
                dynamic h = hist.Item(i);
                int op = h.Operation, rc = h.ResultCode;
                if (op != 1 || (rc != 2 && rc != 3)) continue; // installed, succeeded
                DateTime when = ((DateTime)h.Date).ToLocalTime();
                string title = (string)(h.Title ?? "");
                if (lastAny == null || when > lastAny) lastAny = when;
                bool windowsCu = title.Contains("Cumulative Update", StringComparison.OrdinalIgnoreCase) && title.Contains("Windows", StringComparison.OrdinalIgnoreCase)
                                 && !title.Contains(".NET", StringComparison.OrdinalIgnoreCase);
                if (windowsCu && (lastCu == null || when > lastCu)) { lastCu = when; cuTitle = title; }
            }
        });
        bool answered;
        try { answered = t.Wait(TimeSpan.FromSeconds(60)); }
        catch { answered = false; }
        if (!answered) { add(FindingLevel.Tip, "Couldn't read the Windows Update history", "Open Windows Update to see when it last installed updates.", "windowsupdate", "Open Windows Update"); return; }

        string paused = "";
        var pause = RegStr(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "PauseUpdatesExpiryTime", Registry.LocalMachine);
        if (DateTime.TryParse(pause, null, System.Globalization.DateTimeStyles.RoundtripKind, out var until) && until.ToLocalTime() > DateTime.Now)
            paused = $" Updates are PAUSED until {until.ToLocalTime():MMM d}.";

        if (lastCu is DateTime cu)
        {
            int days = (int)(DateTime.Now - cu).TotalDays;
            var kb = Regex.Match(cuTitle ?? "", @"KB\d+").Value;
            string text = $"Last Windows security update installed {cu:MMM d, yyyy} ({days} day(s) ago){(kb != "" ? ", " + kb : "")}.{paused}";
            if (days > 75) add(FindingLevel.Warning, $"No Windows security update in {days} days", text + " Microsoft releases them every month. Open Windows Update and install everything; an old PC may need several rounds and restarts.", "windowsupdate", "Open Windows Update");
            else if (days > 40 || paused != "") add(FindingLevel.Tip, "Windows updates are behind", text + " A new security update comes out the second Tuesday of every month.", "windowsupdate", "Open Windows Update");
            else add(FindingLevel.Good, "Windows updates are current", text, null, "Fix");
        }
        else
            add(FindingLevel.Tip, "No Windows security update found in the history",
                (lastAny is DateTime a ? $"The last update of any kind was {a:MMM d, yyyy}. " : "") + "Open Windows Update and install everything." + paused, "windowsupdate", "Open Windows Update");
    }

    private static void Smb1(Action<FindingLevel, string, string, string?, string> add)
    {
        // The SMB1 client driver only exists (and isn't disabled) while the SMB1 feature is installed.
        int? start = RegInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\mrxsmb10", "Start");
        int? server = RegInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "SMB1");
        bool on = (start != null && start != 4) || server == 1;
        if (on)
            add(FindingLevel.Warning, "Old SMB1 file sharing is on",
                "SMB1 is the 1990s file-sharing protocol the WannaCry ransomware spread through. Turn it off unless a very old NAS or network scanner needs it. (Needs a restart.)",
                "smb1-off", "Turn off");
        else
            add(FindingLevel.Good, "Old SMB1 file sharing is off", "The protocol WannaCry used isn't installed.", null, "Fix");
    }

    private static void GuestAccount(Action<FindingLevel, string, string, string?, string> add)
    {
        using var s = new System.Management.ManagementObjectSearcher("SELECT Name, SID, Disabled FROM Win32_UserAccount WHERE LocalAccount = TRUE");
        foreach (System.Management.ManagementBaseObject u in s.Get())
        {
            var sid = u["SID"]?.ToString() ?? "";
            var name = u["Name"]?.ToString() ?? "";
            bool disabled = u["Disabled"] as bool? ?? true;
            if (sid.EndsWith("-501") && !disabled)
                add(FindingLevel.Warning, $"The {name} account is turned on", "Anyone at the keyboard or on the network can use it without a password. Turn it off.", "guest-off:" + name, "Turn off");
            else if (sid.EndsWith("-500") && !disabled)
                add(FindingLevel.Tip, $"The built-in {name} account is turned on",
                    "Windows normally keeps it off. If you don't sign in with it on purpose, turn it off in Computer Management > Local Users, or make sure it has a strong password.", null, "Fix");
        }
    }

    private static readonly string[] RemoteApps =
    {
        "TeamViewer", "AnyDesk", "RustDesk", "Chrome Remote Desktop", "LogMeIn", "Splashtop", "ScreenConnect", "ConnectWise Control",
        "UltraVNC", "TightVNC", "RealVNC", "VNC Server", "Ammyy", "Supremo", "RemotePC", "Zoho Assist", "GoTo Resolve", "Remote Utilities", "DWAgent", "Parsec",
    };

    private static void RemoteTools(Action<FindingLevel, string, string, string?, string> add)
    {
        var found = InstalledPrograms()
            .Where(p => RemoteApps.Any(r => p.Name.Contains(r, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool rdp = RegInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections") == 0;
        if (found.Count == 0 && !rdp) return;
        var parts = new List<string>();
        if (found.Count > 0) parts.Add("Installed: " + string.Join(", ", found) + ".");
        if (rdp) parts.Add("Remote Desktop is turned on.");
        add(FindingLevel.Tip, "Remote-control access is set up",
            string.Join(" ", parts) + " Keep the ones you use. Uninstall the rest: scammers use these apps to take over PCs, and old copies on a PC that sat unused may have been set up by someone else.",
            found.Count > 0 ? "apps" : null, "Open Installed apps");
    }

    private sealed record InstalledApp(string Name, string Version);

    private static List<InstalledApp> InstalledPrograms()
    {
        var list = new List<InstalledApp>();
        void Read(RegistryHive hive, RegistryView view)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var k = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (k == null) return;
                foreach (var sub in k.GetSubKeyNames())
                {
                    using var e = k.OpenSubKey(sub);
                    if (e?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                    if (e.GetValue("SystemComponent") is int sc && sc == 1) continue;   // hidden parts of a bigger product
                    if (e.GetValue("ParentKeyName") != null) continue;                 // updates/patches
                    list.Add(new InstalledApp(name.Trim(), (e.GetValue("DisplayVersion") as string ?? "").Trim()));
                }
            }
            catch { }
        }
        Read(RegistryHive.LocalMachine, RegistryView.Registry64);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32);
        Read(RegistryHive.CurrentUser, RegistryView.Default);
        return list;
    }

    private static void OutOfSupportPrograms(Action<FindingLevel, string, string, string?, string> add)
    {
        var found = new List<string>();
        var updates = new List<string>();
        foreach (var p in InstalledPrograms())
        {
            var n = p.Name;
            if (n.StartsWith("Adobe Flash Player", StringComparison.OrdinalIgnoreCase)) found.Add("Adobe Flash Player (ended Dec 2020)");
            else if (n.Contains("Shockwave", StringComparison.OrdinalIgnoreCase)) found.Add("Adobe Shockwave (ended 2019)");
            else if (n.StartsWith("Microsoft Silverlight", StringComparison.OrdinalIgnoreCase)) found.Add("Microsoft Silverlight (ended Oct 2021)");
            else if (n.StartsWith("QuickTime", StringComparison.OrdinalIgnoreCase)) found.Add("Apple QuickTime for Windows (ended 2016)");
            else if (Regex.IsMatch(n, @"^Java(\(TM\))?\s+(SE\s+)?(Runtime\s+Environment\s+)?[67]\b|^Java\s+[67]\s+Update", RegexOptions.IgnoreCase)) found.Add($"{n} (Java 6/7 ended years ago)");
            else if (Regex.IsMatch(n, @"^Adobe\s+(Reader|Acrobat)\s+(X|XI|8|9|2015|2017)\b", RegexOptions.IgnoreCase)) found.Add($"{n} (no longer updated)");
            else if (Regex.Match(n, @"^Microsoft Office\b.*\b(2007|2010|2013|2016|2019|2021)\b", RegexOptions.IgnoreCase) is { Success: true } m)
            {
                var ended = m.Groups[1].Value switch
                {
                    "2007" => "Oct 2017", "2010" => "Oct 2020", "2013" => "Apr 2023", "2016" or "2019" => "Oct 14, 2025", _ => "Oct 13, 2026",
                };
                found.Add($"Microsoft Office {m.Groups[1].Value} (ended {ended})");
            }
            else if (n.StartsWith("WinRAR", StringComparison.OrdinalIgnoreCase) && OlderThan(p, 7, 13))
                updates.Add($"WinRAR {VersionText(p)} (update to 7.13 or newer: attackers used a flaw fixed in 2025)");
            else if (n.StartsWith("7-Zip", StringComparison.OrdinalIgnoreCase) && OlderThan(p, 25, 0))
                updates.Add($"7-Zip {VersionText(p)} (update to 25.00 or newer: older versions have flaws attackers have used)");
        }
        found = found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        updates = updates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (found.Count > 0)
            add(FindingLevel.Warning, $"{found.Count} program(s) no longer get security fixes",
                string.Join("; ", found) + ". Uninstall them, or replace them with a current version." + (updates.Count > 0 ? " Also update: " + string.Join("; ", updates) + "." : ""),
                "apps", "Open Installed apps");
        else if (updates.Count > 0)
            add(FindingLevel.Warning, "Update these programs", string.Join("; ", updates) + ".", "apps", "Open Installed apps");
        else
            add(FindingLevel.Good, "No out-of-support programs found", "Checked for Flash, Java 6/7, Silverlight, QuickTime, old Office and Adobe Reader, and old WinRAR and 7-Zip.", null, "Fix");
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static Version? ProgramVersion(InstalledApp p)
    {
        var m = Regex.Match(p.Version, @"^\d+(\.\d+){0,3}");
        if (!m.Success) m = Regex.Match(p.Name, @"\b\d+\.\d+(\.\d+)?\b");
        return m.Success && Version.TryParse(m.Value.Contains('.') ? m.Value : m.Value + ".0", out var v) ? v : null;
    }

    private static bool OlderThan(InstalledApp p, int major, int minor) =>
        ProgramVersion(p) is Version v && (v.Major < major || (v.Major == major && Math.Max(v.Minor, 0) < minor));

    private static string VersionText(InstalledApp p) => ProgramVersion(p)?.ToString() ?? p.Version;

    private static string? RegStr(string key, string name, RegistryKey? root = null)
    {
        try
        {
            using var k = (root ?? Registry.LocalMachine).OpenSubKey(key);
            return k?.GetValue(name)?.ToString();
        }
        catch { return null; }
    }

    private static string LastLine(string output) =>
        output.Replace("\0", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "unknown error";
}
