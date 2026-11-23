// Security tab: a read-only security sweep, one-click fixes for what it finds, and a
// "security pause" that briefly turns off Memory integrity and the Microsoft Vulnerable Driver
// Blocklist so an old vendor tool (like HP's AMI BIOS recovery maker) can run, then puts both back.
using System.IO;
using System.Management;
using System.Text.Json;
using Microsoft.Win32;

namespace MoorePCMechanic.Services;

/// <summary>One line in the security sweep. FixId is null when there's nothing to click.</summary>
public sealed class SecurityItem
{
    public FindingLevel Level { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string? FixId { get; init; }
    public string FixLabel { get; init; } = "Fix";
    public bool HasFix => FixId != null;
    public string LevelText => Level switch { FindingLevel.Good => "GOOD", FindingLevel.Tip => "TIP", _ => "CHECK" };
}

/// <summary>What the security pause changed, so it can be put back exactly.</summary>
public sealed class PauseState
{
    public DateTime Started { get; set; }
    public DateTime Expires { get; set; }
    public int HvciWas { get; set; }
    public int BlocklistWas { get; set; }
    public string Reason { get; set; } = "";
}

public static class SecurityService
{
    private const string HvciKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
    private const string CiConfigKey = @"SYSTEM\CurrentControlSet\Control\CI\Config";
    private const string CiPolicyKey = @"SYSTEM\CurrentControlSet\Control\CI\Policy";
    public const string ResumeTaskName = "Moore PC Mechanic - Resume security";

    private static string StateFile
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MoorePCMechanic");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "security-pause.json");
        }
    }

    // Drivers attackers are known to abuse ("bring your own vulnerable driver"), plus old ones that commonly
    // keep Memory integrity from turning on. Lower-case file names.
    private static readonly Dictionary<string, string> RiskyDrivers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msio64.sys"] = "MSI Center / Dragon Center I/O driver",
        ["msio32.sys"] = "MSI I/O driver",
        ["amifldrv64.sys"] = "AMI BIOS flash driver (HP/AMI BIOS tools)",
        ["rtcore64.sys"] = "MSI Afterburner (old RivaTuner driver)",
        ["gdrv.sys"] = "Gigabyte tools (old)",
        ["asrdrv101.sys"] = "ASRock tools (old)",
        ["asrdrv102.sys"] = "ASRock tools (old)",
        ["asio.sys"] = "ASUS tools (old)",
        ["asio64.sys"] = "ASUS tools (old)",
        ["winring0x64.sys"] = "WinRing0 (old fan/RGB/monitoring tools)",
        ["winring0.sys"] = "WinRing0 (old fan/RGB/monitoring tools)",
        ["dbutil_2_3.sys"] = "Dell BIOS utility (old)",
        ["iqvw64e.sys"] = "Intel network diagnostics (old)",
        ["gmer64.sys"] = "GMER",
        ["kprocesshacker.sys"] = "Process Hacker (old)",
        ["aswarpot.sys"] = "Avast anti-rootkit (old)",
        ["lvrs64.sys"] = "Logitech webcam driver from 2012",
        ["clvirtualdrive.sys"] = "CyberLink virtual drive (old)",
    };

    // =====================================================================
    //  Sweep (read only)
    // =====================================================================

    public static List<SecurityItem> Sweep()
    {
        var list = new List<SecurityItem>();
        void Add(FindingLevel l, string t, string d, string? fix = null, string label = "Fix") =>
            list.Add(new SecurityItem { Level = l, Title = t, Detail = d, FixId = fix, FixLabel = label });

        // Security pause still on?
        var pause = GetPause();
        if (pause != null)
            Add(FindingLevel.Warning, "Security pause is ON",
                $"Memory integrity and the vulnerable driver blocklist were turned off at {pause.Started:HHmm} for: {pause.Reason}. " +
                $"They come back on automatically after {pause.Expires:MMM d HHmm}, or click Resume now.", "resume", "Resume now");

        // Memory integrity
        var (vbsRunning, hvciRunning) = DeviceGuard();
        int hvciReg = RegInt(Registry.LocalMachine, HvciKey, "Enabled") ?? 0;
        if (hvciRunning)
            Add(FindingLevel.Good, "Memory integrity is on", "Windows checks every kernel driver before it can run.");
        else if (hvciReg == 1)
            Add(FindingLevel.Tip, "Memory integrity is switched on but not running yet", "Restart the PC to finish turning it on.");
        else
            Add(FindingLevel.Warning, "Memory integrity is off",
                "It stops malware from loading into the core of Windows. If Windows says drivers are incompatible, " +
                "the risky/old drivers below are the usual reason. Open Core isolation, click \"Scan again\", then switch it on.",
                "coreisolation", "Open Core isolation");

        // Vulnerable driver blocklist
        int? bl = RegInt(Registry.LocalMachine, CiConfigKey, "VulnerableDriverBlocklistEnable");
        bool blOn = bl == 1 || (bl == null && Environment.OSVersion.Version.Build >= 22621);
        if (blOn) Add(FindingLevel.Good, "Vulnerable driver blocklist is on", "Windows refuses drivers that attackers are known to abuse.");
        else Add(FindingLevel.Warning, "Vulnerable driver blocklist is off",
            "Windows will load drivers that attackers are known to abuse. Turn it back on unless a security pause is running.",
            "blocklist-on", "Turn on");

        // Risky / old kernel drivers that are running
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, PathName, State FROM Win32_SystemDriver WHERE State='Running'");
            foreach (ManagementBaseObject d in s.Get())
            {
                var path = NormalizeDriverPath(d["PathName"]?.ToString());
                if (path == null) continue;
                var file = Path.GetFileName(path);
                if (RiskyDrivers.TryGetValue(file, out var what))
                {
                    Add(FindingLevel.Warning, $"Risky driver running: {file}",
                        $"{what}. It can keep Memory integrity off or be misused by malware. Uninstall the program that installed it " +
                        $"(Settings > Apps), or stop the driver here. Service: {d["Name"]}.", "stopdriver:" + d["Name"], "Remove driver");
                    continue;
                }
                try
                {
                    var fi = new FileInfo(path);
                    var co = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).CompanyName ?? "";
                    if (fi.Exists && fi.LastWriteTime < new DateTime(2016, 1, 1) && !co.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                        Add(FindingLevel.Tip, $"Old driver running: {file} ({fi.LastWriteTime:yyyy})",
                            $"{co}. Drivers this old often block Memory integrity. If you don't use its device or program anymore, uninstall it.");
                }
                catch { }
            }
        }
        catch { }

        // Drivers Windows refused to load lately (Code Integrity log)
        foreach (var blocked in RecentlyBlockedDrivers(14))
            Add(FindingLevel.Tip, $"Windows blocked a driver: {blocked.File}",
                $"Blocked {blocked.Count}x in the last 2 weeks (last {blocked.Last:MMM d HHmm}). That's the protection working. " +
                "If an app you need relies on it (like HP's BIOS recovery maker), use a security pause below.");

        // Defender
        try
        {
            using var st = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender", "SELECT * FROM MSFT_MpComputerStatus");
            foreach (ManagementBaseObject m in st.Get())
            {
                bool av = m["AntivirusEnabled"] as bool? ?? false;
                bool rt = m["RealTimeProtectionEnabled"] as bool? ?? false;
                bool tamper = m["IsTamperProtected"] as bool? ?? false;
                uint sigAge = Convert.ToUInt32(m["AntivirusSignatureAge"] ?? 0u);
                uint fullAge = Convert.ToUInt32(m["FullScanAge"] ?? uint.MaxValue);
                if (av && rt) Add(FindingLevel.Good, "Microsoft Defender is on", $"Real-time protection on, tamper protection {(tamper ? "on" : "OFF")}, definitions {sigAge} day(s) old.");
                else Add(FindingLevel.Warning, "Microsoft Defender real-time protection is off", "Unless another antivirus is installed, turn it on in Windows Security.", "defender", "Open Windows Security");
                if (sigAge > 3) Add(FindingLevel.Warning, $"Defender definitions are {sigAge} days old", "Update them so new threats are recognized.", "sigupdate", "Update now");
                if (fullAge > 60) Add(FindingLevel.Tip, fullAge > 10000 ? "No full Defender scan has ever run" : $"Last full scan was {fullAge} days ago",
                    "A full scan checks every file (1-3 hours, the PC stays usable).", "fullscan", "Start full scan");
            }
            using var pr = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender", "SELECT PUAProtection, EnableNetworkProtection FROM MSFT_MpPreference");
            foreach (ManagementBaseObject p in pr.Get())
            {
                int pua = Convert.ToInt32(p["PUAProtection"] ?? 0);
                int net = Convert.ToInt32(p["EnableNetworkProtection"] ?? 0);
                if (pua != 1 || net != 1)
                    Add(FindingLevel.Tip, "Extra Defender blocking is off",
                        $"Unwanted-app blocking: {(pua == 1 ? "on" : pua == 2 ? "audit only" : "off")}. Network protection: {(net == 1 ? "on" : net == 2 ? "audit only" : "off")}. " +
                        "Both stop adware/bundleware and scam sites.", "defender-extras", "Turn both on");
                else Add(FindingLevel.Good, "Extra Defender blocking is on", "Unwanted-app blocking and network protection are both on.");
            }
        }
        catch { Add(FindingLevel.Tip, "Couldn't read Microsoft Defender", "Another antivirus may be in charge, or Windows Security isn't answering."); }

        // Firmware and sign-in protections
        int? sb = RegInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        Add(sb == 1 ? FindingLevel.Good : FindingLevel.Warning, sb == 1 ? "Secure Boot is on" : "Secure Boot is off",
            sb == 1 ? "Only signed boot loaders can start the PC." : "Turn it on in the PC's BIOS setup (Boot or Security section).");
        try
        {
            using var t = new ManagementObjectSearcher(@"root\cimv2\Security\MicrosoftTpm", "SELECT IsEnabled_InitialValue, IsActivated_InitialValue FROM Win32_Tpm");
            bool tpm = false;
            foreach (ManagementBaseObject o in t.Get()) tpm = (o["IsEnabled_InitialValue"] as bool? ?? false) && (o["IsActivated_InitialValue"] as bool? ?? false);
            Add(tpm ? FindingLevel.Good : FindingLevel.Tip, tpm ? "TPM security chip is ready" : "TPM not found or not ready", tpm ? "Used for Windows Hello and encryption." : "Run as admin to check, or turn on fTPM/PTT in the BIOS.");
        }
        catch { }
        int? lsa = RegInt(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa", "RunAsPPL");
        Add(lsa is 1 or 2 ? FindingLevel.Good : FindingLevel.Tip, lsa is 1 or 2 ? "Sign-in protection (LSA) is on" : "Sign-in protection (LSA) is off",
            lsa is 1 or 2 ? "Protects saved sign-ins from password-stealing tools." : "Turn on \"Local Security Authority protection\" in Core isolation.",
            lsa is 1 or 2 ? null : "coreisolation", "Open Core isolation");
        bool fwAll = new[] { "DomainProfile", "StandardProfile", "PublicProfile" }
            .All(p => RegInt(Registry.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{p}", "EnableFirewall") == 1);
        Add(fwAll ? FindingLevel.Good : FindingLevel.Warning, fwAll ? "Firewall is on" : "Firewall is off on at least one network type",
            fwAll ? "On for domain, private and public networks." : "Turn it back on in Windows Security > Firewall.", fwAll ? null : "firewall", "Open Firewall");
        int? uac = RegInt(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA");
        if (uac == 0) Add(FindingLevel.Warning, "User Account Control is off", "Programs can change Windows without asking. Turn UAC back on in Control Panel.");
        int? sac = RegInt(Registry.LocalMachine, CiPolicyKey, "VerifiedAndReputablePolicyState");
        if (sac is 1 or 2)
            Add(FindingLevel.Good, sac == 1 ? "Smart App Control is on" : "Smart App Control is in evaluation",
                "It blocks untrusted apps. Note: while it's on, a security pause can't let blocked drivers load, and turning it off can't be undone without resetting Windows.");

        // HP AIO tie-in
        if (KnownIssues.HpAioBiosLock)
            Add(KnownIssues.WindowsUpdateDriversBlocked() ? FindingLevel.Good : FindingLevel.Warning,
                KnownIssues.WindowsUpdateDriversBlocked() ? "HP AIO: Windows Update can't push BIOS/drivers" : "HP AIO: Windows Update can still push a BIOS",
                KnownIssues.WindowsUpdateDriversBlocked() ? "Protected against the bad BIOS push that caused the update loop." : KnownIssues.HpAioDetail,
                KnownIssues.WindowsUpdateDriversBlocked() ? null : "hp-wu-block", "Block it");

        return list.OrderBy(i => i.Level switch { FindingLevel.Warning => 0, FindingLevel.Tip => 1, _ => 2 }).ToList();
    }

    /// <summary>Runs one of the sweep's fixes. Returns a plain-English result. Most need admin.</summary>
    public static string Fix(string id)
    {
        switch (id)
        {
            case "coreisolation": Open("windowsdefender://coreisolation"); return "Opened Core isolation. Click \"Scan again\", then switch Memory integrity on and restart.";
            case "defender": Open("windowsdefender://threat"); return "Opened Windows Security.";
            case "firewall": Open("windowsdefender://network"); return "Opened Firewall & network protection.";
            case "blocklist-on":
                using (var k = Registry.LocalMachine.CreateSubKey(CiConfigKey, true)) k.SetValue("VulnerableDriverBlocklistEnable", 1, RegistryValueKind.DWord);
                return "Vulnerable driver blocklist turned on. Restart the PC to finish.";
            case "sigupdate": return Ps("Update-MpSignature") ? "Defender definitions updated." : "Couldn't update definitions right now; Windows Update will do it.";
            case "fullscan":
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command Start-MpScan -ScanType FullScan")
                    { UseShellExecute = false, CreateNoWindow = true });
                return "Full scan started in the background (1-3 hours). Results show in Windows Security > Virus & threat protection.";
            case "defender-extras":
                return Ps("Set-MpPreference -PUAProtection Enabled; Set-MpPreference -EnableNetworkProtection Enabled")
                    ? "Unwanted-app blocking and network protection are on." : "Couldn't change Defender settings (tamper protection or another antivirus may be in charge).";
            case "hp-wu-block": KnownIssues.BlockWindowsUpdateDrivers(); return "Windows Update will no longer install drivers or BIOS updates on this PC.";
            case "resume": return Resume(false);
        }
        if (id.StartsWith("stopdriver:"))
        {
            var svc = id["stopdriver:".Length..];
            TuneUpService.Run("sc.exe", $"stop \"{svc}\"", 30);
            TuneUpService.Run("sc.exe", $"config \"{svc}\" start= disabled", 30);
            var (code, _) = TuneUpService.Run("sc.exe", $"delete \"{svc}\"", 30);
            return code == 0 ? $"Driver service {svc} removed. Restart, then check Memory integrity again. (Uninstall the program that installed it too, or it may put it back.)"
                             : $"Couldn't remove {svc}. Uninstall the program that installed it from Settings > Apps.";
        }
        return "Nothing to do.";
    }

    // =====================================================================
    //  Security pause
    // =====================================================================

    public static PauseState? GetPause()
    {
        try { return File.Exists(StateFile) ? JsonSerializer.Deserialize<PauseState>(File.ReadAllText(StateFile)) : null; }
        catch { return null; }
    }

    /// <summary>True when Smart App Control is on or evaluating: then the blocklist is enforced no matter what, and a pause can't help.</summary>
    public static bool SmartAppControlBlocksPause() => RegInt(Registry.LocalMachine, CiPolicyKey, "VerifiedAndReputablePolicyState") is 1 or 2;

    /// <summary>Turns Memory integrity and the vulnerable driver blocklist off for a while and schedules them to come back.
    /// Needs admin. A restart is needed before the old tool can load its driver.</summary>
    public static string Pause(TimeSpan length, string reason)
    {
        if (GetPause() != null) return "A security pause is already running.";
        var state = new PauseState
        {
            Started = DateTime.Now,
            Expires = DateTime.Now.Add(length),
            HvciWas = RegInt(Registry.LocalMachine, HvciKey, "Enabled") ?? 0,
            BlocklistWas = RegInt(Registry.LocalMachine, CiConfigKey, "VulnerableDriverBlocklistEnable") ?? 1,
            Reason = string.IsNullOrWhiteSpace(reason) ? "running an old vendor tool" : reason.Trim(),
        };
        var (rpOk, rpMsg) = DriverService.CreateRestorePoint("Moore PC Mechanic: before security pause");
        CreateResumeTask(state.Expires); // first: if this fails, nothing has been changed yet
        File.WriteAllText(StateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        using (var k = Registry.LocalMachine.CreateSubKey(HvciKey, true)) k.SetValue("Enabled", 0, RegistryValueKind.DWord);
        using (var k = Registry.LocalMachine.CreateSubKey(CiConfigKey, true)) k.SetValue("VulnerableDriverBlocklistEnable", 0, RegistryValueKind.DWord);
        Log($"PAUSE started for {length.TotalHours:0.#}h ({state.Reason}). Was: HVCI={state.HvciWas}, blocklist={state.BlocklistWas}. {(rpOk ? "Restore point made." : rpMsg)}");
        return $"Security pause is set until {state.Expires:HHmm}. RESTART the PC, run your tool, then click Resume (or it resumes by itself).";
    }

    /// <summary>Puts Memory integrity and the blocklist back the way they were and removes the resume task. Needs admin.</summary>
    public static string Resume(bool automatic)
    {
        var state = GetPause();
        using (var k = Registry.LocalMachine.CreateSubKey(HvciKey, true)) k.SetValue("Enabled", state?.HvciWas ?? 1, RegistryValueKind.DWord);
        using (var k = Registry.LocalMachine.CreateSubKey(CiConfigKey, true)) k.SetValue("VulnerableDriverBlocklistEnable", state?.BlocklistWas ?? 1, RegistryValueKind.DWord);
        TuneUpService.Run("schtasks.exe", $"/Delete /TN \"{ResumeTaskName}\" /F", 30);
        try { File.Delete(StateFile); } catch { }
        Log($"PAUSE ended ({(automatic ? "automatically" : "by you")}). HVCI={state?.HvciWas ?? 1}, blocklist={state?.BlocklistWas ?? 1}.");
        return "Protections are back the way they were. Restart the PC to finish.";
    }

    /// <summary>Run by the scheduled task ("--resume-security"): resumes only when the pause has run out.</summary>
    public static bool ResumeIfDue()
    {
        var s = GetPause();
        if (s == null) { TuneUpService.Run("schtasks.exe", $"/Delete /TN \"{ResumeTaskName}\" /F", 30); return false; }
        if (DateTime.Now < s.Expires) return false;
        Resume(true);
        return true;
    }

    private static void CreateResumeTask(DateTime expires)
    {
        var exe = UpdateService.CurrentExePath;
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.4"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Turns Memory integrity and the vulnerable driver blocklist back on after a Moore PC Mechanic security pause.</Description></RegistrationInfo>
  <Triggers>
    <TimeTrigger><StartBoundary>{expires:yyyy-MM-ddTHH:mm:ss}</StartBoundary><Enabled>true</Enabled>
      <Repetition><Interval>PT30M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition></TimeTrigger>
    <LogonTrigger><Enabled>true</Enabled><Delay>PT2M</Delay></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{System.Security.SecurityElement.Escape(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <Enabled>true</Enabled>
    <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>
  </Settings>
  <Actions Context=""Author"">
    <Exec><Command>{System.Security.SecurityElement.Escape(exe)}</Command><Arguments>--resume-security</Arguments></Exec>
  </Actions>
</Task>";
        var file = Path.Combine(Path.GetTempPath(), "MoorePCMechanic-resume-task.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        var (code, output) = TuneUpService.Run("schtasks.exe", $"/Create /TN \"{ResumeTaskName}\" /XML \"{file}\" /F", 30);
        try { File.Delete(file); } catch { }
        if (code != 0) throw new InvalidOperationException("Couldn't schedule the automatic resume, so nothing was paused: " + output.Trim());
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static (bool Vbs, bool Hvci) DeviceGuard()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\DeviceGuard", "SELECT * FROM Win32_DeviceGuard");
            foreach (ManagementBaseObject o in s.Get())
            {
                bool vbs = Convert.ToInt32(o["VirtualizationBasedSecurityStatus"] ?? 0) == 2;
                bool hvci = o["SecurityServicesRunning"] is Array a && a.Cast<object>().Any(x => Convert.ToInt32(x) == 2);
                return (vbs, hvci);
            }
        }
        catch { }
        return (false, false);
    }

    private sealed record BlockedDriver(string File, int Count, DateTime Last);

    private static List<BlockedDriver> RecentlyBlockedDrivers(int days)
    {
        var found = new Dictionary<string, (int Count, DateTime Last)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("Microsoft-Windows-CodeIntegrity/Operational",
                System.Diagnostics.Eventing.Reader.PathType.LogName,
                $"*[System[(EventID=3023 or EventID=3077 or EventID=3033) and TimeCreated[timediff(@SystemTime) <= {days * 86400000L}]]]");
            using var r = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
            for (var e = r.ReadEvent(); e != null; e = r.ReadEvent())
            {
                using (e)
                {
                    string msg; try { msg = e.FormatDescription() ?? ""; } catch { msg = ""; }
                    var m = System.Text.RegularExpressions.Regex.Match(msg, @"([\w\-\.]+\.sys)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    var when = e.TimeCreated ?? DateTime.MinValue;
                    found[m.Value] = found.TryGetValue(m.Value, out var v) ? (v.Count + 1, when > v.Last ? when : v.Last) : (1, when);
                }
            }
        }
        catch { }
        return found.Select(kv => new BlockedDriver(kv.Key, kv.Value.Count, kv.Value.Last)).OrderByDescending(b => b.Last).Take(8).ToList();
    }

    private static string? NormalizeDriverPath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        p = p.Trim('"');
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (p.StartsWith(@"\??\")) p = p[4..];
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(win, p[12..]);
        else if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(win, p);
        return p;
    }

    private static int? RegInt(RegistryKey root, string key, string name)
    {
        try
        {
            using var k = root.OpenSubKey(key);
            var v = k?.GetValue(name);
            return v == null ? null : Convert.ToInt32(v);
        }
        catch { return null; }
    }

    private static bool Ps(string command)
    {
        var (code, _) = TuneUpService.Run("powershell.exe", $"-NoProfile -NonInteractive -Command \"{command}\"", 300);
        return code == 0;
    }

    private static void Open(string uri)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    private static void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(AppEnv.DataFolder, "security-history.txt"), $"{DateTime.Now:yyyy-MM-dd HHmm}  {line}{Environment.NewLine}"); } catch { }
    }
}
