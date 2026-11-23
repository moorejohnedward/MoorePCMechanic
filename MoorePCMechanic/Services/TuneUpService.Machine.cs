// Moore PC Mechanic - tune-up items that depend on what kind of PC this is (see MachineProfile).
//   powermode    "Best performance" power mode on laptops, ARM and Modern Standby PCs (plugged in only)
//   memcompress  Windows memory compression on (and the SysMain service it needs)
//   visuals      lighter visual effects on light PCs (keeps smooth fonts and thumbnails)
//   edge         Edge sleeping tabs, efficiency mode, no startup boost / background running, on light PCs
//   arm          advice on ARM PCs: which startup apps run through x64 emulation
// Everything here is recorded in the tune-up's undo file and put back by "Undo last tune-up".

using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MoorePCMechanic.Services;

public static partial class TuneUpService
{
    public static readonly Guid BestPerformanceMode = new("ded574b5-45a0-4f42-8737-46345c09c238");
    private static readonly Guid BetterPerformanceMode = new("3af9b8d9-7c97-431d-ad78-34a8bfea439f");
    private static readonly Guid BestEfficiencyMode = new("961cc777-2547-4f9d-8174-7d86181b8a7a");

    private const string EdgePolicy = @"SOFTWARE\Policies\Microsoft\Edge\Recommended"; // "recommended": you can still change them in Edge
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // =====================================================================
    //  Scan
    // =====================================================================

    private static TuneItem PowerModeItem(MachineProfile pc)
    {
        var cur = ActivePowerMode();
        bool best = cur == BestPerformanceMode;
        bool onBattery = pc.HasBattery && !pc.OnAcPower;
        return new TuneItem
        {
            Id = "powermode", Title = "Power mode: Best performance",
            Why = "On laptops, ARM PCs and Modern Standby PCs, Windows hides the old High performance plan; the power mode " +
                  "(Settings > System > Power) is what sets the speed. This sets Best performance while plugged in; your battery setting is left alone." +
                  (onBattery ? " It's on battery right now: plug in and run the tune-up again." : ""),
            State = cur == null ? "Unknown" : PowerModeName(cur.Value) + (onBattery ? " (on battery)" : ""),
            NeedsFix = cur != null && !best && !onBattery, Selected = cur != null && !best && !onBattery,
        };
    }

    private static void AddMachineItems(List<TuneItem> items, MachineProfile pc, List<StartupApp> startup)
    {
        // ---- memory compression (Windows' default; some "tweak" tools turn it off)
        bool? mc = MemoryCompressionOn();
        bool sysMainOff = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\SysMain", "Start") == 4;
        bool mcNeeds = mc == false || sysMainOff || (mc == null && pc.IsLight);
        items.Add(new TuneItem
        {
            Id = "memcompress", Title = "Memory compression on",
            Why = "Windows squeezes memory that isn't being used right now instead of swapping it out to the drive, so " +
                  (pc.IsLight ? "8 GB goes a lot further." : "the PC stays quick when lots of apps are open.") +
                  " Needs the SysMain service. Takes effect after a restart.",
            State = (mc == null ? "Unknown" : mc.Value ? "On" : "Off") + (sysMainOff ? " (SysMain service is disabled)" : ""),
            NeedsFix = mcNeeds, Selected = mcNeeds, RestartNeeded = true,
        });

        // ---- lighter visual effects (light PCs)
        bool visualsDone = ReadDword(Registry.CurrentUser, PersonalizeKey, "EnableTransparency") == 0 &&
                           ReadDword(Registry.CurrentUser, AdvancedKey, "TaskbarAnimations") == 0 &&
                           GetSpi(0x1042) == 0;
        items.Add(new TuneItem
        {
            Id = "visuals", Title = "Lighter visual effects",
            Why = "Turns off animations, fades, shadows and see-through panels, which a small built-in graphics chip draws with the same memory your apps use. " +
                  "Keeps smooth fonts and picture thumbnails so things don't look rough. Some changes show after you sign out." +
                  (pc.IsLight ? "" : " Not needed on this PC; tick it if you want it anyway."),
            State = visualsDone ? "Light" : "Full effects",
            NeedsFix = pc.IsLight && !visualsDone, Selected = pc.IsLight && !visualsDone,
        });

        // ---- Edge (light PCs)
        if (EdgeInstalled())
        {
            bool edgeDone = ReadDword(Registry.LocalMachine, EdgePolicy, "SleepingTabsEnabled") == 1 &&
                            ReadDword(Registry.LocalMachine, EdgePolicy, "EfficiencyModeEnabled") == 1 &&
                            ReadDword(Registry.LocalMachine, EdgePolicy, "StartupBoostEnabled") == 0;
            items.Add(new TuneItem
            {
                Id = "edge", Title = "Edge: sleeping tabs and efficiency mode",
                Why = "The browser is what fills 8 GB. This puts tabs you haven't looked at in 5 minutes to sleep, turns on efficiency mode, " +
                      "and stops Edge preloading at startup and running after you close it. Set as recommendations, so you can still change them in Edge " +
                      "(Edge will say it's \"managed by your organization\"; that's this)." +
                      (pc.IsLight ? "" : " Not needed on this PC; tick it if you want it anyway."),
                State = edgeDone ? "Set" : "Edge defaults",
                NeedsFix = pc.IsLight && !edgeDone, Selected = pc.IsLight && !edgeDone,
            });
        }

        // ---- ARM: what runs through emulation
        if (pc.IsArm64)
        {
            var emulated = startup.Where(s => s.Enabled && !s.IsThisApp)
                                  .Select(s => (s.Name, Path: MachineProfile.ExePathFromCommand(s.Command)))
                                  .Where(s => s.Path != null && MachineProfile.ExeArchitecture(s.Path) is MachineProfile.ExeArch.X64 or MachineProfile.ExeArch.X86)
                                  .Select(s => s.Name).ToList();
            items.Add(new TuneItem
            {
                Id = "arm", Title = "ARM: programs running through emulation", CanFix = false,
                Why = "On an ARM processor, programs built for Intel/AMD (x64) still run, but through emulation, which is slower and uses more battery. " +
                      "Get the ARM64 version where there is one (Chrome, Edge, Firefox, Office, Zoom, Teams, VS Code, 7-Zip and Tailscale all have one)." +
                      (pc.AppEmulated ? " This copy of Moore PC Mechanic is the x64 one; the next update installs the native ARM64 build by itself." : "") +
                      (emulated.Count > 0 ? " Emulated at startup: " + string.Join(", ", emulated) + "." : ""),
                State = (pc.AppEmulated ? "This app: emulated. " : "This app: native ARM64. ") + $"{emulated.Count} emulated startup app(s)",
                NeedsFix = pc.AppEmulated || emulated.Count > 0,
            });
        }
    }

    // =====================================================================
    //  Startup trim on light PCs
    // =====================================================================

    private static readonly string[] KeepAtStartup =
    {
        "SecurityHealth", "Windows Defender", "Windows Security", "Tailscale", "OneDrive", "GoogleDrive", "Google Drive", "Dropbox",
        "Realtek", "RtkAud", "RTHDVCPL", "Audio", "Dolby", "Waves", "MaxxAudio", "Synaptics", "ETDCtrl", "Elan", "Touchpad", "Wacom", "Pen",
        "igfx", "Intel", "AMD", "Radeon", "NVIDIA", "Qualcomm", "Snapdragon", "Bluetooth", "Logi", "ctfmon",
        "KonaSnap", "Moore", "PDF and Moore", "PdfAndMoore",
    };

    /// <summary>On a light PC, unticks startup apps that aren't security, sync, drivers or Moore apps. Returns how many.
    /// Nothing changes until the tune-up is applied, and it can be undone.</summary>
    public static int SuggestStartupTrim(IList<StartupApp> apps, MachineProfile pc)
    {
        if (!pc.IsLight) return 0;
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        int n = 0;
        foreach (var a in apps.Where(a => a.Enabled && !a.IsThisApp))
        {
            if (a.Command.Contains(win, StringComparison.OrdinalIgnoreCase) || a.Command.Contains("%windir%", StringComparison.OrdinalIgnoreCase) ||
                a.Command.Contains("%SystemRoot%", StringComparison.OrdinalIgnoreCase)) continue; // part of Windows
            if (KeepAtStartup.Any(k => a.Name.Contains(k, StringComparison.OrdinalIgnoreCase) || a.Command.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
            a.Enabled = false;
            n++;
        }
        return n;
    }

    // =====================================================================
    //  Apply / undo
    // =====================================================================

    /// <summary>Applies one per-machine item. Returns true if a restart is needed.</summary>
    private static bool ApplyMachineItem(TuneItem it, TuneUndo undo)
    {
        switch (it.Id)
        {
            case "powermode":
                var before = ActivePowerMode();
                if (before == null) { it.Result = "Couldn't read the power mode"; return false; }
                if (PowerSetActiveOverlayScheme(BestPerformanceMode) != 0) { it.Result = "Windows didn't accept the change"; return false; }
                undo.PowerModeBefore = before.Value.ToString();
                it.Result = "Set to Best performance";
                return false;

            case "memcompress":
                if (ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\SysMain", "Start") == 4)
                {
                    Run("sc.exe", "config SysMain start= auto", 30);
                    Run("sc.exe", "start SysMain", 30);
                    undo.SysMainWasDisabled = true;
                }
                if (MemoryCompressionOn() != true)
                {
                    var (code, output) = Run("powershell.exe", "-NoProfile -NonInteractive -Command \"Enable-MMAgent -MemoryCompression\"", 120);
                    if (code != 0) { it.Result = "Couldn't: " + output.Trim(); return false; }
                    undo.MemoryCompressionTurnedOn = true;
                }
                it.Result = "On (after restart)";
                return true;

            case "visuals":
                foreach (var (_, get, set) in VisualEffectsOff)
                {
                    var was = GetSpi(get);
                    if (was == null) continue;
                    undo.VisualBefore.TryAdd(set.ToString(), was.Value);
                    SetSpi(set, 0);
                }
                if (GetMinimizeAnimation() is int minAnim)
                {
                    undo.VisualBefore.TryAdd("minanim", minAnim);
                    SetMinimizeAnimation(0);
                }
                SetSpiUi(0x004B, 1); // keep smooth screen fonts on
                SetDword(undo, RegistryHive.CurrentUser, PersonalizeKey, "EnableTransparency", 0);
                SetDword(undo, RegistryHive.CurrentUser, AdvancedKey, "TaskbarAnimations", 0);
                SetDword(undo, RegistryHive.CurrentUser, AdvancedKey, "ListviewAlphaSelect", 0);
                SetDword(undo, RegistryHive.CurrentUser, AdvancedKey, "ListviewShadow", 0);
                SetDword(undo, RegistryHive.CurrentUser, AdvancedKey, "IconsOnly", 0); // keep thumbnails
                SetDword(undo, RegistryHive.CurrentUser, @"Software\Microsoft\Windows\DWM", "EnableAeroPeek", 0);
                SetDword(undo, RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 3); // "Custom"
                it.Result = "Lighter (some after sign-out)";
                return false;

            case "edge":
                SetDword(undo, RegistryHive.LocalMachine, EdgePolicy, "SleepingTabsEnabled", 1);
                SetDword(undo, RegistryHive.LocalMachine, EdgePolicy, "SleepingTabsTimeout", 300);
                SetDword(undo, RegistryHive.LocalMachine, EdgePolicy, "EfficiencyModeEnabled", 1);
                SetDword(undo, RegistryHive.LocalMachine, EdgePolicy, "StartupBoostEnabled", 0);
                SetDword(undo, RegistryHive.LocalMachine, EdgePolicy, "BackgroundModeEnabled", 0);
                it.Result = "Set (restart Edge)";
                return false;
        }
        return false;
    }

    private static void UndoMachineItems(TuneUndo u, List<string> done)
    {
        if (u.PowerModeBefore != null && Guid.TryParse(u.PowerModeBefore, out var mode))
        {
            try { PowerSetActiveOverlayScheme(mode); done.Add("power mode"); } catch { }
        }
        if (u.MemoryCompressionTurnedOn)
        {
            Run("powershell.exe", "-NoProfile -NonInteractive -Command \"Disable-MMAgent -MemoryCompression\"", 120);
            done.Add("memory compression (after restart)");
        }
        if (u.SysMainWasDisabled)
        {
            Run("sc.exe", "stop SysMain", 30);
            Run("sc.exe", "config SysMain start= disabled", 30);
            done.Add("SysMain service");
        }
        if (u.VisualBefore.Count > 0)
        {
            foreach (var (key, value) in u.VisualBefore)
            {
                if (key == "minanim") SetMinimizeAnimation(value);
                else if (uint.TryParse(key, out var set)) SetSpi(set, value);
            }
            done.Add("visual effects");
        }
        if (u.RegistryBefore.Count > 0)
        {
            foreach (var r in u.RegistryBefore)
            {
                try
                {
                    if (!Enum.TryParse<RegistryHive>(r.Hive, out var hive)) continue;
                    using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                    using var k = root.CreateSubKey(r.Path);
                    if (r.Existed) k.SetValue(r.Name, r.Value, RegistryValueKind.DWord);
                    else k.DeleteValue(r.Name, false);
                }
                catch { }
            }
            if (u.RegistryBefore.Any(r => r.Path == EdgePolicy)) done.Add("Edge settings");
        }
    }

    private static void SetDword(TuneUndo undo, RegistryHive hive, string path, string name, int value)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var k = root.CreateSubKey(path);
        var old = k.GetValue(name);
        if (!undo.RegistryBefore.Any(r => r.Hive == hive.ToString() && r.Path == path && r.Name == name))
            undo.RegistryBefore.Add(new RegBefore { Hive = hive.ToString(), Path = path, Name = name, Existed = old is int, Value = old is int v ? v : 0 });
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    /// <summary>Windows' current power mode (overlay), or null if this Windows doesn't have one.</summary>
    public static Guid? ActivePowerMode()
    {
        try { return PowerGetEffectiveOverlayScheme(out var g) == 0 ? g : null; }
        catch { return null; }
    }

    private static string PowerModeName(Guid g) =>
        g == BestPerformanceMode ? "Best performance" :
        g == BetterPerformanceMode ? "Better performance" :
        g == BestEfficiencyMode ? "Best power efficiency" : "Balanced";

    private static bool? MemoryCompressionOn()
    {
        try
        {
            var (code, output) = Run("powershell.exe", "-NoProfile -NonInteractive -Command \"(Get-MMAgent).MemoryCompression\"", 60);
            if (code != 0) return null;
            if (output.Contains("True", StringComparison.OrdinalIgnoreCase)) return true;
            if (output.Contains("False", StringComparison.OrdinalIgnoreCase)) return false;
        }
        catch { }
        return null;
    }

    private static bool EdgeInstalled()
    {
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            if (!string.IsNullOrEmpty(pf) && File.Exists(Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"))) return true;
        return false;
    }

    // SystemParametersInfo pairs (get, set) for the effects "Adjust for best performance" turns off
    private static readonly (string Name, uint Get, uint Set)[] VisualEffectsOff =
    {
        ("menu animation", 0x1002, 0x1003),
        ("combo box slide", 0x1004, 0x1005),
        ("list box smooth scroll", 0x1006, 0x1007),
        ("selection fade", 0x1014, 0x1015),
        ("tooltip animation", 0x1016, 0x1017),
        ("cursor shadow", 0x101A, 0x101B),
        ("window shadows", 0x1024, 0x1025),
        ("control animations", 0x1042, 0x1043),
    };

    private const uint SPIF_UPDATE_AND_SEND = 0x1 | 0x2;

    private static int? GetSpi(uint action)
    {
        try { return SystemParametersInfoGet(action, 0, out int v, 0) ? v : null; }
        catch { return null; }
    }

    private static void SetSpi(uint action, int value)
    {
        try { SystemParametersInfoSet(action, 0, (IntPtr)value, SPIF_UPDATE_AND_SEND); } catch { }
    }

    private static void SetSpiUi(uint action, uint uiParam)
    {
        try { SystemParametersInfoSet(action, uiParam, IntPtr.Zero, SPIF_UPDATE_AND_SEND); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ANIMATIONINFO { public uint cbSize; public int iMinAnimate; }

    private static int? GetMinimizeAnimation()
    {
        try
        {
            var ai = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>() };
            return SystemParametersInfoAnim(0x0048, ai.cbSize, ref ai, 0) ? ai.iMinAnimate : null;
        }
        catch { return null; }
    }

    private static void SetMinimizeAnimation(int on)
    {
        try
        {
            var ai = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>(), iMinAnimate = on };
            SystemParametersInfoAnim(0x0049, ai.cbSize, ref ai, SPIF_UPDATE_AND_SEND);
        }
        catch { }
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfoGet(uint action, uint uiParam, out int pvParam, uint winIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfoSet(uint action, uint uiParam, IntPtr pvParam, uint winIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfoAnim(uint action, uint uiParam, ref ANIMATIONINFO pvParam, uint winIni);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlay);
}
