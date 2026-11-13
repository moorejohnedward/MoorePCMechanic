// Moore PC Mechanic - tune-up
// One scan finds what's slowing the PC down; one click fixes the ticked items.
// Every change is recorded so "Undo last tune-up" can put things back, and a System Restore
// point is made first. Nothing here deletes personal files: cleanup only touches temp/cache
// folders Windows itself treats as disposable (and the Recycle Bin only if you tick it).

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MoorePCMechanic.ViewModels;

namespace MoorePCMechanic.Services;

public sealed class TuneItem : Observable
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Why { get; init; } = "";

    private string _state = "";
    /// <summary>What it is now, e.g. "Balanced" or "4.2 GB of junk".</summary>
    public string State { get => _state; set => Set(ref _state, value); }

    private bool _needsFix;
    public bool NeedsFix { get => _needsFix; set { Set(ref _needsFix, value); Raise(nameof(StatusText)); } }

    private bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    /// <summary>False for advice-only items (things software can't fix, like a hot SSD).</summary>
    public bool CanFix { get; init; } = true;

    public bool RestartNeeded { get; init; }

    private string _result = "";
    public string Result { get => _result; set { Set(ref _result, value); Raise(nameof(StatusText)); } }

    public string StatusText => !string.IsNullOrEmpty(Result) ? Result : NeedsFix ? (CanFix ? "Can improve" : "Advice") : "Good";
    public int Level => !string.IsNullOrEmpty(Result) ? 0 : NeedsFix ? (CanFix ? 1 : 2) : 0;
}

public sealed class StartupApp : Observable
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Where { get; init; } = "";
    internal string ApprovedKeyPath { get; init; } = "";   // StartupApproved\Run, Run32 or StartupFolder
    internal RegistryHive Hive { get; init; }
    public bool WasEnabled { get; init; }

    private bool _enabled;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool IsThisApp => Command.Contains("MoorePCMechanic", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the last tune-up changed, so it can be undone.</summary>
public sealed class TuneUndo
{
    public DateTime When { get; set; }
    public string? PowerSchemeBefore { get; set; }
    public bool GameModeChanged { get; set; }
    public int? GameModeBefore { get; set; }
    public bool HagsChanged { get; set; }
    public int? HagsBefore { get; set; }
    public List<string> StartupDisabled { get; set; } = new();  // "hive|keyPath|name"
}

public static class TuneUpService
{
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string UltimatePerformance = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    private static string UndoFile => Path.Combine(AppEnv.DataFolder, "tuneup-undo.json");

    // =====================================================================
    //  Scan
    // =====================================================================

    /// <param name="hottestDriveC">Hottest drive temperature from the sensors, °C (null if unknown).</param>
    public static List<TuneItem> Scan(float? hottestDriveC, string? hottestDriveName)
    {
        var items = new List<TuneItem>();

        // ---- power plan
        var (guid, name) = ActivePowerScheme();
        bool fast = guid is HighPerformance or UltimatePerformance;
        items.Add(new TuneItem
        {
            Id = "power", Title = "Power plan: full speed",
            Why = "On a desktop plugged into power (and your UPS), \"High performance\" stops the processor dropping into slow power-saving states between bursts, so apps respond faster. Costs a few watts at idle.",
            State = name ?? "Unknown", NeedsFix = !fast, Selected = !fast,
        });

        // ---- game mode (also helps video calls: Windows prioritizes the app in front)
        int? gm = ReadDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
        bool gmOn = gm is null or 1; // Windows' default is on
        items.Add(new TuneItem
        {
            Id = "gamemode", Title = "Game Mode on",
            Why = "Keeps Windows Update and background maintenance from grabbing the PC while a full-screen app or game is running.",
            State = gmOn ? "On" : "Off", NeedsFix = !gmOn, Selected = !gmOn,
        });

        // ---- hardware-accelerated GPU scheduling
        int? hags = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
        bool hagsOn = hags == 2;
        items.Add(new TuneItem
        {
            Id = "hags", Title = "Graphics card schedules its own work",
            Why = "Hardware-accelerated GPU scheduling lets the RTX card manage its own queue, which lowers lag in games, video and AI apps. Takes effect after a restart.",
            State = hagsOn ? "On" : "Off", NeedsFix = !hagsOn, Selected = !hagsOn, RestartNeeded = true,
        });

        // ---- junk files
        long junk = JunkTargets().Sum(t => FolderSize(t.Path, t.MinAge));
        items.Add(new TuneItem
        {
            Id = "junk", Title = "Clear temporary files and crash dumps",
            Why = "Temp folders, old crash reports and Windows Update's delivery cache. Windows recreates anything it still needs; your files aren't touched.",
            State = AppEnv.Bytes(junk) + " can be cleared", NeedsFix = junk > 200L * 1024 * 1024, Selected = junk > 200L * 1024 * 1024,
        });

        // ---- recycle bin (not ticked by default: it's the user's to decide)
        long bin = RecycleBinSize();
        items.Add(new TuneItem
        {
            Id = "recycle", Title = "Empty the Recycle Bin",
            Why = "Only if you're sure you don't need anything in it.",
            State = AppEnv.Bytes(bin) + " in the Recycle Bin", NeedsFix = bin > 500L * 1024 * 1024, Selected = false,
        });

        // ---- SSD TRIM
        bool trimOn = TrimEnabled();
        items.Add(new TuneItem
        {
            Id = "trim", Title = "Re-TRIM the SSDs",
            Why = "Tells each SSD which blocks are free so it keeps writing at full speed. Safe and takes under a minute." + (trimOn ? "" : " TRIM is turned OFF on this PC; this turns it back on."),
            State = trimOn ? "TRIM on" : "TRIM is off", NeedsFix = true, Selected = true,
        });

        // ---- startup apps
        var startup = StartupApps();
        int enabled = startup.Count(s => s.Enabled);
        items.Add(new TuneItem
        {
            Id = "startup", Title = "Apps that start with Windows", CanFix = false,
            Why = "Each one slows startup and runs in the background. Untick the ones you don't need in the list below; changes apply with the tune-up and can be undone.",
            State = $"{enabled} turned on", NeedsFix = enabled > 8,
        });

        // ---- free space on C:
        try
        {
            var c = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            double freePct = 100.0 * c.AvailableFreeSpace / c.TotalSize;
            items.Add(new TuneItem
            {
                Id = "space", Title = "Free space on " + c.Name.TrimEnd('\\'), CanFix = false,
                Why = "Windows and SSDs slow down when the system drive is nearly full. Keep at least 15% free.",
                State = $"{AppEnv.Bytes(c.AvailableFreeSpace)} free ({freePct:0}%)", NeedsFix = freePct < 15,
            });
        }
        catch { }

        // ---- drive heat (advice only)
        if (hottestDriveC is float t)
        {
            items.Add(new TuneItem
            {
                Id = "ssdheat", Title = "SSD temperature", CanFix = false,
                Why = t >= 70
                    ? "Above about 158 °F (70 °C) an NVMe SSD slows itself down to cool off. A $10-15 M.2 heatsink (or the motherboard's M.2 shield, if it has one) and a front intake fan usually drop it 20-30 °F."
                    : "Fine.",
                State = $"{hottestDriveName}: {Temp.Full(t, "0")}", NeedsFix = t >= 70,
            });
        }

        // ---- restart pending
        bool reboot = RebootPending();
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        items.Add(new TuneItem
        {
            Id = "restart", Title = "Restart", CanFix = false,
            Why = "Updates waiting on a restart, or a long uptime, leave old drivers and memory leaks in place. A restart clears them.",
            State = (reboot ? "Windows is waiting for a restart. " : "") + $"Up {(int)up.TotalDays} days {up.Hours} h",
            NeedsFix = reboot || up.TotalDays >= 7,
        });

        return items;
    }

    // =====================================================================
    //  Apply
    // =====================================================================

    /// <summary>Applies the ticked items and startup changes. Returns true if a restart is needed. Needs admin.</summary>
    public static bool Apply(IList<TuneItem> items, IList<StartupApp> startup, IProgress<string> progress)
    {
        var undo = new TuneUndo { When = DateTime.Now };
        bool restart = false;
        foreach (var it in items.Where(i => i.Selected && i.CanFix))
        {
            progress.Report(it.Title + "…");
            try
            {
                switch (it.Id)
                {
                    case "power":
                        undo.PowerSchemeBefore = ActivePowerScheme().Guid;
                        EnsureHighPerformance();
                        it.Result = "Set to " + (ActivePowerScheme().Name ?? "High performance");
                        break;
                    case "gamemode":
                        undo.GameModeChanged = true;
                        undo.GameModeBefore = ReadDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
                        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\GameBar"))
                            k.SetValue("AutoGameModeEnabled", 1, RegistryValueKind.DWord);
                        it.Result = "Turned on";
                        break;
                    case "hags":
                        undo.HagsChanged = true;
                        undo.HagsBefore = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
                        using (var k = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers"))
                            k.SetValue("HwSchMode", 2, RegistryValueKind.DWord);
                        it.Result = "Turned on (after restart)";
                        restart = true;
                        break;
                    case "junk":
                        it.Result = "Cleared " + AppEnv.Bytes(CleanJunk());
                        break;
                    case "recycle":
                        long before = RecycleBinSize();
                        SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); // no confirmation, no progress, no sound
                        it.Result = "Emptied " + AppEnv.Bytes(before);
                        break;
                    case "trim":
                        if (!TrimEnabled()) Run("fsutil.exe", "behavior set DisableDeleteNotify 0", 30);
                        var (code, _) = Run("defrag.exe", "/C /L /U", 600); // /L = re-TRIM every SSD volume (HDDs are skipped)
                        it.Result = code == 0 ? "Done" : "Finished (some volumes skipped)";
                        break;
                }
            }
            catch (Exception ex)
            {
                it.Result = "Couldn't: " + ex.Message;
            }
        }

        // startup apps: only the ones the user changed
        foreach (var s in startup.Where(s => s.Enabled != s.WasEnabled))
        {
            try
            {
                SetStartupEnabled(s, s.Enabled);
                if (!s.Enabled) undo.StartupDisabled.Add($"{s.Hive}|{s.ApprovedKeyPath}|{ValueName(s)}");
            }
            catch { }
        }

        try { File.WriteAllText(UndoFile, JsonSerializer.Serialize(undo, new JsonSerializerOptions { WriteIndented = true })); } catch { }
        return restart;
    }

    public static TuneUndo? LastUndo()
    {
        try { return File.Exists(UndoFile) ? JsonSerializer.Deserialize<TuneUndo>(File.ReadAllText(UndoFile)) : null; }
        catch { return null; }
    }

    /// <summary>Puts back the power plan, Game Mode, GPU scheduling and startup apps from the last tune-up.</summary>
    public static string Undo()
    {
        var u = LastUndo();
        if (u == null) return "There's no tune-up to undo.";
        var done = new List<string>();
        if (u.PowerSchemeBefore != null) { Run("powercfg.exe", "/setactive " + u.PowerSchemeBefore, 30); done.Add("power plan"); }
        if (u.GameModeChanged)
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\GameBar");
            if (u.GameModeBefore is int gm) k.SetValue("AutoGameModeEnabled", gm, RegistryValueKind.DWord);
            else k.DeleteValue("AutoGameModeEnabled", false);
            done.Add("Game Mode");
        }
        if (u.HagsChanged)
        {
            using var k = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            if (u.HagsBefore is int hv) k.SetValue("HwSchMode", hv, RegistryValueKind.DWord);
            else k.DeleteValue("HwSchMode", false);
            done.Add("GPU scheduling (after restart)");
        }
        foreach (var entry in u.StartupDisabled)
        {
            var p = entry.Split('|', 3);
            if (p.Length != 3 || !Enum.TryParse<RegistryHive>(p[0], out var hive)) continue;
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var k = root.CreateSubKey(p[1]);
                k.SetValue(p[2], ApprovedBytes(true), RegistryValueKind.Binary);
            }
            catch { }
        }
        if (u.StartupDisabled.Count > 0) done.Add($"{u.StartupDisabled.Count} startup app(s)");
        try { File.Delete(UndoFile); } catch { }
        return done.Count == 0 ? "Nothing to put back." : "Put back: " + string.Join(", ", done) + ".";
    }

    // =====================================================================
    //  Automatic tune-up (Windows Task Scheduler)
    // =====================================================================

    public const string TaskName = @"Moore PC Mechanic\Automatic tune-up";
    private static string HistoryFile => Path.Combine(AppEnv.DataFolder, "tuneup-history.txt");

    /// <summary>
    /// What the scheduled run does, with no window: the safe, no-restart fixes only (power plan, Game Mode,
    /// junk cleanup, SSD re-TRIM). It never empties the Recycle Bin, changes startup apps or GPU scheduling,
    /// or restarts the PC. Results go to tuneup-history.txt and show in the app next time it opens.
    /// </summary>
    public static void RunAutomatic()
    {
        var log = new List<string>();
        try
        {
            if (!AppEnv.IsAdmin()) { WriteHistory("Skipped: the scheduled task isn't running as administrator. Turn automatic tune-up off and on again in the app."); return; }
            var (rpOk, _) = DriverService.CreateRestorePoint("Moore PC Mechanic: automatic tune-up");
            var items = Scan(null, null);
            foreach (var i in items)
                i.Selected = i.Id == "trim" || (i.NeedsFix && i.Id is "power" or "gamemode" or "junk");
            Apply(items, new List<StartupApp>(), new Progress<string>(_ => { }));
            foreach (var i in items.Where(i => i.Selected)) log.Add(i.Title + ": " + i.Result);
            var advice = items.Where(i => i.NeedsFix && (!i.CanFix || i.Id is "hags" or "recycle")).Select(i => i.Title).ToList();
            if (advice.Count > 0) log.Add("Needs you (open the Tune-up tab): " + string.Join(", ", advice));
            if (!rpOk) log.Add("(No new restore point: Windows makes at most one a day.)");
            WriteHistory(string.Join(" | ", log));
        }
        catch (Exception ex)
        {
            WriteHistory("Stopped with an error: " + ex.Message);
        }
    }

    private static void WriteHistory(string line)
    {
        try { File.AppendAllText(HistoryFile, $"{DateTime.Now:yyyy-MM-dd HHmm}  {line}{Environment.NewLine}"); } catch { }
    }

    /// <summary>Most recent automatic tune-up line, or null.</summary>
    public static string? LastAutomaticRun()
    {
        try { return File.Exists(HistoryFile) ? File.ReadLines(HistoryFile).LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) : null; }
        catch { return null; }
    }

    /// <summary>(on, day, hour) of the scheduled task, read back from Windows.</summary>
    public static (bool On, DayOfWeek Day, int Hour) GetSchedule()
    {
        var (code, xml) = Run("schtasks.exe", $"/Query /TN \"{TaskName}\" /XML", 30);
        if (code != 0) return (false, DayOfWeek.Sunday, 3);
        var day = DayOfWeek.Sunday;
        foreach (DayOfWeek d in Enum.GetValues<DayOfWeek>())
            if (xml.Contains("<" + d + " />") || xml.Contains("<" + d + "/>")) { day = d; break; }
        var m = Regex.Match(xml, @"<StartBoundary>\d{4}-\d\d-\d\dT(\d\d):");
        int hour = m.Success ? int.Parse(m.Groups[1].Value) : 3;
        return (true, day, hour);
    }

    /// <summary>Creates (or removes) the weekly task. Runs as you, with admin rights, only while you're signed in;
    /// if the PC was off or asleep at that time, it runs soon after it's back on. Needs admin.</summary>
    public static string SetSchedule(bool on, DayOfWeek day, int hour)
    {
        if (!on)
        {
            Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F", 30);
            return "Automatic tune-up turned off.";
        }
        var exe = UpdateService.CurrentExePath;
        var start = DateTime.Today.AddHours(hour);
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.4"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Weekly tune-up by Moore PC Mechanic: power plan, Game Mode, junk cleanup, SSD re-TRIM. Results: Documents\Moore PC Mechanic\tuneup-history.txt</Description></RegistrationInfo>
  <Triggers>
    <CalendarTrigger>
      <StartBoundary>{start:yyyy-MM-ddTHH:mm:ss}</StartBoundary>
      <Enabled>true</Enabled>
      <ScheduleByWeek><DaysOfWeek><{day} /></DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek>
    </CalendarTrigger>
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
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec><Command>{System.Security.SecurityElement.Escape(exe)}</Command><Arguments>--tuneup</Arguments></Exec>
  </Actions>
</Task>";
        var file = Path.Combine(Path.GetTempPath(), "MoorePCMechanic-tuneup-task.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        var (code, output) = Run("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{file}\" /F", 30);
        try { File.Delete(file); } catch { }
        if (code != 0) throw new InvalidOperationException(output.Trim());
        return $"Automatic tune-up is on: every {day} at {hour:00}00 (or soon after, if the PC was off or asleep).";
    }

    // =====================================================================
    //  Deep repair: DISM + SFC (Windows' own system file repair)
    // =====================================================================

    public static string DeepRepair(IProgress<string> progress)
    {
        progress.Report("Step 1 of 2: DISM is checking Windows' component store against Microsoft's copy (5-15 minutes)…");
        var (dismCode, dismOut) = Run("dism.exe", "/Online /Cleanup-Image /RestoreHealth", 3600);
        progress.Report("Step 2 of 2: System File Checker is repairing Windows files (5-15 minutes)…");
        var (sfcCode, sfcOut) = Run("sfc.exe", "/scannow", 3600);
        string sfc = sfcOut.Replace("\0", "");
        string verdict =
            Regex.IsMatch(sfc, "did not find any integrity violations", RegexOptions.IgnoreCase) ? "Windows system files are healthy." :
            Regex.IsMatch(sfc, "successfully repaired", RegexOptions.IgnoreCase) ? "Damaged Windows files were found and repaired. Restart the PC." :
            Regex.IsMatch(sfc, "unable to fix", RegexOptions.IgnoreCase) ? "Some damaged files couldn't be repaired. Details are in C:\\Windows\\Logs\\CBS\\CBS.log." :
            $"Finished (DISM code {dismCode}, SFC code {sfcCode}).";
        return verdict;
    }

    // =====================================================================
    //  Helpers: power plan
    // =====================================================================

    public static (string? Guid, string? Name) ActivePowerScheme()
    {
        var (_, output) = Run("powercfg.exe", "/getactivescheme", 30);
        var m = Regex.Match(output, @"([0-9a-fA-F-]{36})\s*\((.+)\)");
        return m.Success ? (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.Trim()) : (null, null);
    }

    private static void EnsureHighPerformance()
    {
        var (_, list) = Run("powercfg.exe", "/list", 30);
        if (!list.Contains(HighPerformance, StringComparison.OrdinalIgnoreCase))
            Run("powercfg.exe", "/duplicatescheme " + HighPerformance + " " + HighPerformance, 30); // restore the hidden built-in plan
        Run("powercfg.exe", "/setactive " + HighPerformance, 30);
    }

    // =====================================================================
    //  Helpers: junk
    // =====================================================================

    private static IEnumerable<(string Path, TimeSpan MinAge)> JunkTargets()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var day = TimeSpan.FromDays(1);
        yield return (Path.GetTempPath(), day);                                    // your temp folder (files older than a day)
        yield return (Path.Combine(win, "Temp"), day);                             // Windows temp
        yield return (Path.Combine(local, "CrashDumps"), TimeSpan.Zero);           // app crash dumps
        yield return (Path.Combine(win, "Minidump"), TimeSpan.Zero);               // blue-screen dumps
        yield return (Path.Combine(progData, @"Microsoft\Windows\WER\ReportArchive"), TimeSpan.Zero); // old error reports
        yield return (Path.Combine(progData, @"Microsoft\Windows\WER\ReportQueue"), TimeSpan.Zero);
        yield return (Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"), TimeSpan.Zero);
        yield return (Path.Combine(win, @"SoftwareDistribution\DeliveryOptimization"), TimeSpan.Zero);
    }

    private static long FolderSize(string dir, TimeSpan minAge)
    {
        long total = 0;
        var cutoff = DateTime.Now - minAge;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts))
            {
                try { if (f.LastWriteTime <= cutoff) total += f.Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    /// <summary>Deletes what it can; files in use are skipped. Returns bytes freed.</summary>
    public static long CleanJunk()
    {
        long freed = 0;
        foreach (var (dir, minAge) in JunkTargets())
        {
            if (!Directory.Exists(dir)) continue;
            var cutoff = DateTime.Now - minAge;
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts).ToList())
            {
                try
                {
                    if (f.LastWriteTime > cutoff) continue;
                    long len = f.Length;
                    if (f.IsReadOnly) f.IsReadOnly = false;
                    f.Delete();
                    freed += len;
                }
                catch { /* in use: leave it */ }
            }
            // remove empty folders left behind (deepest first), but never the folder itself
            try
            {
                foreach (var d in new DirectoryInfo(dir).EnumerateDirectories("*", opts).OrderByDescending(d => d.FullName.Length).ToList())
                    try { if (!d.EnumerateFileSystemInfos().Any()) d.Delete(); } catch { }
            }
            catch { }
        }
        return freed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint flags);

    private static long RecycleBinSize()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
    }

    // =====================================================================
    //  Helpers: TRIM, restart, registry, commands
    // =====================================================================

    private static bool TrimEnabled()
    {
        var (_, o) = Run("fsutil.exe", "behavior query DisableDeleteNotify", 30);
        var m = Regex.Match(o, @"NTFS DisableDeleteNotify\s*=\s*(\d)");
        return !m.Success || m.Groups[1].Value == "0";
    }

    private static bool RebootPending()
    {
        try
        {
            using var a = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (a != null) return true;
            using var b = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            return b != null;
        }
        catch { return false; }
    }

    private static int? ReadDword(RegistryKey root, string path, string name)
    {
        try
        {
            using var k = root.OpenSubKey(path);
            return k?.GetValue(name) is int v ? v : null;
        }
        catch { return null; }
    }

    internal static (int Code, string Output) Run(string exe, string args, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutSeconds * 1000)) { try { p.Kill(true); } catch { } return (-1, "timed out"); }
        return (p.ExitCode, outTask.Result + errTask.Result);
    }

    // =====================================================================
    //  Startup apps (the same on/off switches Task Manager's Startup tab uses)
    // =====================================================================

    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupApp> StartupApps()
    {
        var list = new List<StartupApp>();
        void FromRun(RegistryHive hive, RegistryView view, string runPath, string approvedName, string where)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var run = root.OpenSubKey(runPath);
                if (run == null) return;
                foreach (var name in run.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    bool enabled = IsApproved(hive, approvedName, name);
                    list.Add(new StartupApp
                    {
                        Name = name, Command = run.GetValue(name)?.ToString() ?? "", Where = where,
                        Hive = hive, ApprovedKeyPath = ApprovedRoot + approvedName,
                        WasEnabled = enabled, Enabled = enabled,
                    });
                }
            }
            catch { }
        }
        FromRun(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "You");
        FromRun(RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "All users");
        FromRun(RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run32", "All users (32-bit)");

        void FromFolder(string dir, RegistryHive hive, string where)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var file = Path.GetFileName(f);
                    if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    bool enabled = IsApproved(hive, "StartupFolder", file);
                    list.Add(new StartupApp
                    {
                        Name = Path.GetFileNameWithoutExtension(f), Command = f, Where = where,
                        Hive = hive, ApprovedKeyPath = ApprovedRoot + "StartupFolder",
                        WasEnabled = enabled, Enabled = enabled,
                    });
                }
            }
            catch { }
        }
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, "Your Startup folder");
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, "All users' Startup folder");

        return list.OrderBy(s => s.IsThisApp).ThenBy(s => s.Name).ToList();
    }

    /// <summary>StartupApproved values: first byte even (02/06) = on, odd (03/07) = off. No value = on.</summary>
    private static bool IsApproved(RegistryHive hive, string approvedName, string valueName)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var k = root.OpenSubKey(ApprovedRoot + approvedName);
            if (k?.GetValue(valueName) is byte[] b && b.Length > 0) return (b[0] & 1) == 0;
        }
        catch { }
        return true;
    }

    private static byte[] ApprovedBytes(bool enabled)
    {
        var b = new byte[12];
        b[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(b, 4); // when it was turned off, like Task Manager writes
        return b;
    }

    /// <summary>Run-key entries are keyed by their name; Startup-folder entries by their file name.</summary>
    private static string ValueName(StartupApp s) =>
        s.ApprovedKeyPath.EndsWith("StartupFolder") ? Path.GetFileName(s.Command) : s.Name;

    private static void SetStartupEnabled(StartupApp s, bool enabled)
    {
        using var root = RegistryKey.OpenBaseKey(s.Hive, RegistryView.Registry64);
        using var k = root.CreateSubKey(s.ApprovedKeyPath);
        k.SetValue(ValueName(s), ApprovedBytes(enabled), RegistryValueKind.Binary);
    }
}
