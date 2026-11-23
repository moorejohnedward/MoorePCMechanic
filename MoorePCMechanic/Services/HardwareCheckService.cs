// Moore PC Mechanic - hardware diagnostic (System check tab)
// Read-only checks for a full health check, written with an old PC coming back to life in mind:
// drives (health, SMART bad sectors, hours, wear), hardware errors Windows logged (WHEA), crashes and
// sudden power losses, drive errors, memory, temperatures and fans at rest, power-supply rails and the
// CMOS battery, the clock, problem devices, the network link and (on Windows 10) Windows 11 readiness.
// Nothing here changes the PC; fixes are separate buttons (Fix below).

using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace MoorePCMechanic.Services;

public static class HardwareCheckService
{
    private const long Day = 86_400_000L; // milliseconds, for event log queries

    // =====================================================================
    //  Full check
    // =====================================================================

    /// <param name="readings">Latest live sensor readings (temperatures, fans, voltages). Can be empty.</param>
    public static List<SecurityItem> Check(IReadOnlyList<SensorReading> readings, IProgress<string>? progress = null)
    {
        var list = new List<SecurityItem>();
        bool admin = AppEnv.IsAdmin();

        void Step(string name, Action act)
        {
            progress?.Report($"Checking {name.ToLowerInvariant()}…");
            try { act(); }
            catch (Exception ex) { list.Add(Item(FindingLevel.Tip, $"{name}: couldn't check", ex.Message)); }
        }

        Step("Drives", () => Drives(list, admin));
        Step("Drive errors", () => DriveErrors(list, admin));
        Step("Hardware errors", () => HardwareErrors(list));
        Step("Crashes and power loss", () => Crashes(list));
        Step("Memory", () => Memory(list));
        Step("Temperatures", () => Temperatures(list, readings, admin));
        Step("Fans", () => Fans(list, readings, admin));
        Step("Power supply", () => PowerRails(list, readings, admin));
        Step("Clock", () => Clock(list, readings));
        Step("Devices", () => Devices(list));
        Step("Network", () => Network(list));
        if (Environment.OSVersion.Version.Build < 22000)
            Step("Windows 11 readiness", () => Win11Ready(list, admin));

        if (!admin)
            list.Add(Item(FindingLevel.Tip, "Limited mode: some hardware checks are hidden",
                "Drive bad-sector counts, power-on hours, voltages, the TPM and the disk check need admin. Click \"Restart as admin\" (top right), then run the check again."));

        return list.OrderBy(i => i.Level switch { FindingLevel.Warning => 0, FindingLevel.Tip => 1, _ => 2 }).ToList();
    }

    /// <summary>Runs a hardware fix. Returns null if the id isn't a hardware fix.</summary>
    public static string? Fix(string id)
    {
        switch (id)
        {
            case "mdsched":
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mdsched.exe") { UseShellExecute = true });
                return "Windows Memory Diagnostic opened. Pick \"Restart now and check for problems\". The result shows here after the next full check.";
            case "clock-sync":
                TuneUpService.Run("sc.exe", "config w32time start= demand", 30);
                TuneUpService.Run("net.exe", "start w32time", 30);
                var (code, output) = TuneUpService.Run("w32tm.exe", "/resync /force", 60);
                if (code != 0)
                {
                    TuneUpService.Run("w32tm.exe", "/config /manualpeerlist:time.windows.com,0x9 /syncfromflags:manual /update", 30);
                    (code, output) = TuneUpService.Run("w32tm.exe", "/resync /force", 60);
                }
                return code == 0 ? "Clock set from Microsoft's time server." : "Couldn't set the clock: " + output.Trim();
            case "pchealth":
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://aka.ms/GetPCHealthCheckApp") { UseShellExecute = true });
                return "Opened the download for Microsoft's PC Health Check (the official Windows 11 check).";
        }
        return null;
    }

    // =====================================================================
    //  Drives
    // =====================================================================

    private sealed class SmartInfo
    {
        public string Instance = "";
        public string Product = "";
        public bool PredictFailure;
        public long? Realloc, Pending, Offline, Uncorrectable, PowerOnHours;
        public int? TempC;
        public bool Used;
    }

    private static void Drives(List<SecurityItem> list, bool admin)
    {
        var smart = admin ? ReadSmart() : new List<SmartInfo>();
        string? bootDisk = BootDiskNumber();
        int found = 0;

        using (var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_PhysicalDisk"))
        {
            foreach (ManagementObject d in s.Get())
            {
                using (d)
                {
                    int bus = ToInt(d["BusType"]);
                    if (bus == 7) continue; // USB sticks and external drives: not part of this PC's health
                    found++;
                    string id = d["DeviceId"]?.ToString() ?? "";
                    string name = (d["FriendlyName"]?.ToString() ?? "Drive " + id).Trim();
                    int media = ToInt(d["MediaType"]);
                    int health = ToInt(d["HealthStatus"], 5);
                    ulong size = ToULong(d["Size"]);
                    bool isSsd = media == 4 || bus == 17;
                    bool isHdd = media == 3;
                    bool boot = bootDisk != null && bootDisk == id;
                    string kind = isSsd ? "SSD" : isHdd ? "hard drive" : "drive";
                    string busName = bus switch { 17 => "NVMe", 11 => "SATA", 8 => "RAID", 10 => "SAS", 3 => "ATA", _ => "" };
                    var opStatus = d["OperationalStatus"] is Array a ? a.Cast<object>().Select(x => ToInt(x)).ToList() : new List<int>();

                    ManagementBaseObject? rc = null;
                    if (admin)
                    {
                        try { foreach (ManagementBaseObject r in d.GetRelated("MSFT_StorageReliabilityCounter")) { rc = r; break; } } catch { }
                    }
                    long? hours = ToLongN(rc?["PowerOnHours"]);
                    int? temp = ToIntN(rc?["Temperature"]);
                    int? wear = ToIntN(rc?["Wear"]);
                    long uncorrected = (ToLongN(rc?["ReadErrorsUncorrected"]) ?? 0) + (ToLongN(rc?["WriteErrorsUncorrected"]) ?? 0);

                    var sm = MatchSmart(smart, name);
                    hours ??= sm?.PowerOnHours;
                    if (temp is null or 0) temp = sm?.TempC;

                    var problems = new List<string>();
                    var tips = new List<string>();
                    var facts = new List<string> { $"{Cap(kind)}{(busName != "" ? ", " + busName : "")}, {DecimalSize(size)}" };

                    if (health == 2) problems.Add("Windows reports this drive as UNHEALTHY");
                    else if (health == 1 || opStatus.Contains(5)) problems.Add("Windows reports a warning for this drive (predicted failure or degraded)");
                    else if (health == 0) facts.Add("Windows says it's healthy");
                    if (sm?.PredictFailure == true) problems.Add("Its own self-test (SMART) predicts it will fail soon");
                    if (sm?.Pending > 0) problems.Add($"{sm.Pending} sector(s) are unreadable and waiting to be replaced");
                    if (sm?.Offline > 0) problems.Add($"{sm.Offline} sector(s) can't be read at all");
                    if (sm?.Uncorrectable > 0) problems.Add($"{sm.Uncorrectable} read error(s) it couldn't correct");
                    if (sm?.Realloc > 0)
                    {
                        if (sm.Realloc > 10) problems.Add($"{sm.Realloc} bad sectors already swapped out (the surface is wearing)");
                        else tips.Add($"{sm.Realloc} bad sector(s) swapped out. A few is OK; run this check again in a month and see if it grows");
                    }
                    else if (sm != null) facts.Add("0 bad sectors");
                    if (uncorrected > 0) problems.Add($"{uncorrected} read/write error(s) it couldn't correct");

                    if (hours is long h && h > 0)
                    {
                        facts.Add($"powered on {h:N0} hours ({h / 8766.0:0.#} years of running time)");
                        if (isHdd && h > 43_830) tips.Add("Over 5 years of running time: keep backups current, hard drives this old fail without much warning");
                    }
                    if (temp is int t && t > 0)
                    {
                        facts.Add(Temp.Full(t, "0"));
                        if (isHdd ? t >= 50 : t >= 70) tips.Add("Running hot. Check the case fans and give it some airflow");
                    }
                    if (isSsd && wear is int w && w > 0)
                    {
                        facts.Add($"{w}% of rated life used");
                        if (w >= 90) problems.Add($"{w}% of its rated life is used up");
                        else if (w >= 70) tips.Add("Past 70% of its rated life: plan a replacement");
                    }
                    if (boot && isHdd)
                        tips.Add("Windows runs from this spinning hard drive. Moving Windows to an SSD is the biggest speed-up an old PC can get");
                    if (!admin) facts.Add("bad sectors, hours and temperature need admin mode");

                    if (problems.Count > 0) problems.Add("Copy anything important off this drive now, then replace it");
                    var level = problems.Count > 0 ? FindingLevel.Warning : tips.Count > 0 ? FindingLevel.Tip : FindingLevel.Good;
                    string title = name + (boot ? " (Windows drive)" : "") + (level == FindingLevel.Good ? ": healthy" : "");
                    list.Add(Item(level, title, Sentences(problems) + Sentences(facts) + Sentences(tips)));
                }
            }
        }

        // SMART reports that didn't match a drive above (shouldn't happen often): still show real problems
        foreach (var sm in smart.Where(x => !x.Used && (x.PredictFailure || x.Pending > 0 || x.Offline > 0 || x.Realloc > 10)))
            list.Add(Item(FindingLevel.Warning, $"{(sm.Product == "" ? "A drive" : sm.Product)}: SMART warning",
                $"Self-test predicts failure: {(sm.PredictFailure ? "yes" : "no")}. Bad sectors swapped out: {sm.Realloc ?? 0}. Unreadable sectors: {(sm.Pending ?? 0) + (sm.Offline ?? 0)}. Back it up and replace it."));

        if (found == 0)
        {
            // Older Windows storage API missing: fall back to the basic status
            using var s = new ManagementObjectSearcher("SELECT Model, Status, Size, InterfaceType FROM Win32_DiskDrive");
            foreach (ManagementBaseObject d in s.Get())
            {
                if (d["InterfaceType"]?.ToString() == "USB") continue;
                var status = d["Status"]?.ToString() ?? "";
                bool ok = status.Equals("OK", StringComparison.OrdinalIgnoreCase);
                list.Add(Item(ok ? FindingLevel.Good : FindingLevel.Warning, $"{d["Model"]}: {(ok ? "healthy" : status)}",
                    ok ? "Windows reports no problems." : "Windows reports a problem with this drive. Back it up and replace it."));
            }
        }

        // Windows flagged the file system as needing a disk check (admin)
        if (admin)
        {
            var sys = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
            var (_, dirty) = TuneUpService.Run("fsutil.exe", $"dirty query {sys}", 20);
            if (Regex.IsMatch(dirty, @"\bis\s+dirty\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(dirty, @"not\s+dirty", RegexOptions.IgnoreCase))
                list.Add(Item(FindingLevel.Warning, $"Drive {sys} is marked for a disk check",
                    "Windows found file-system damage and will run a disk check (chkdsk) at the next restart. Restart soon and let it finish; don't power off during it."));
        }
    }

    private static SmartInfo? MatchSmart(List<SmartInfo> smart, string friendlyName)
    {
        var a = Norm(friendlyName);
        foreach (var x in smart.Where(x => !x.Used))
        {
            var b = Norm(x.Product);
            if (a.Length < 5 || b.Length < 5) continue;
            if (a.Contains(b) || b.Contains(a) || (b.Length >= 10 && a.Contains(b[..10])) || (a.Length >= 10 && b.Contains(a[^10..])))
            {
                x.Used = true;
                return x;
            }
        }
        return null;
    }

    /// <summary>SMART from the ATA/SATA storage driver (admin). NVMe drives use the reliability counters instead.</summary>
    private static List<SmartInfo> ReadSmart()
    {
        var map = new Dictionary<string, SmartInfo>(StringComparer.OrdinalIgnoreCase);
        SmartInfo Get(string inst)
        {
            if (!map.TryGetValue(inst, out var v)) map[inst] = v = new SmartInfo { Instance = inst, Product = ProductFromInstance(inst) };
            return v;
        }
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus");
            foreach (ManagementBaseObject o in s.Get())
                Get(o["InstanceName"]?.ToString() ?? "").PredictFailure = o["PredictFailure"] as bool? ?? false;
        }
        catch { }
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData");
            foreach (ManagementBaseObject o in s.Get())
            {
                if (o["VendorSpecific"] is not byte[] b) continue;
                var x = Get(o["InstanceName"]?.ToString() ?? "");
                // 30 attribute slots of 12 bytes after a 2-byte header: id, flags(2), value, worst, raw(6), reserved
                for (int i = 2; i + 12 <= b.Length && i < 2 + 30 * 12; i += 12)
                {
                    int attr = b[i];
                    if (attr == 0) continue;
                    long raw = (long)b[i + 5] | (long)b[i + 6] << 8 | (long)b[i + 7] << 16 | (long)b[i + 8] << 24 | (long)b[i + 9] << 32 | (long)b[i + 10] << 40;
                    switch (attr)
                    {
                        case 5: x.Realloc = raw & 0xFFFFFFFF; break;
                        case 9: x.PowerOnHours = raw & 0xFFFFFFFF; break;
                        case 187: x.Uncorrectable = raw & 0xFFFFFFFF; break;
                        case 194: if (b[i + 5] is > 0 and < 100) x.TempC = b[i + 5]; break;
                        case 197: x.Pending = raw & 0xFFFFFFFF; break;
                        case 198: x.Offline = raw & 0xFFFFFFFF; break;
                    }
                }
            }
        }
        catch { }
        return map.Values.Where(v => v.Instance.Length > 0).ToList();
    }

    private static string ProductFromInstance(string inst)
    {
        // e.g. SCSI\Disk&Ven_WDC&Prod_WD10EZEX-08WN4A0\4&2b6b...  or  IDE\DiskWDC_WD10EZEX-08WN4A0______01.01A01\5&...
        var m = Regex.Match(inst, @"Prod_([^\\&]+)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.Replace('_', ' ').Trim();
        m = Regex.Match(inst, @"\\Disk([^\\]+)", RegexOptions.IgnoreCase);
        return m.Success ? Regex.Replace(m.Groups[1].Value, "_{2,}.*$", "").Replace('_', ' ').Trim() : "";
    }

    private static string? BootDiskNumber()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT DiskNumber FROM MSFT_Partition WHERE IsBoot = TRUE");
            foreach (ManagementBaseObject o in s.Get()) return o["DiskNumber"]?.ToString();
        }
        catch { }
        return null;
    }

    // =====================================================================
    //  Event log: drive errors, hardware errors, crashes
    // =====================================================================

    private sealed record Ev(string Provider, int Id, int Level, DateTime When, string Text);

    private static List<Ev> Events(string xpath, int max = 400, bool withText = false)
    {
        var list = new List<Ev>();
        try
        {
            var q = new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true }; // newest first
            using var r = new EventLogReader(q);
            for (var e = r.ReadEvent(); e != null && list.Count < max; e = r.ReadEvent())
            {
                using (e)
                {
                    string text = "";
                    if (withText) { try { text = e.FormatDescription() ?? ""; } catch { } }
                    list.Add(new Ev(e.ProviderName ?? "", e.Id, e.Level ?? 0, e.TimeCreated ?? DateTime.MinValue, text));
                }
            }
        }
        catch { }
        return list;
    }

    private static string Since(int days) => $"TimeCreated[timediff(@SystemTime) <= {days * Day}]";

    private static readonly Dictionary<string, string> DiskEventText = new(StringComparer.OrdinalIgnoreCase)
    {
        ["disk/7"] = "bad block", ["disk/11"] = "controller error", ["disk/51"] = "error while paging",
        ["disk/153"] = "read/write had to be retried", ["disk/154"] = "hardware error", ["disk/157"] = "drive disappeared",
        ["Ntfs/55"] = "file system damaged", ["Ntfs/57"] = "write failed", ["Ntfs/137"] = "file system transaction failed",
        ["storahci/129"] = "SATA controller reset", ["stornvme/129"] = "NVMe reset", ["stornvme/11"] = "NVMe controller error",
    };

    private static void DriveErrors(List<SecurityItem> list, bool admin)
    {
        var ev = Events("*[System[(Provider[@Name='disk'] or Provider[@Name='Ntfs'] or Provider[@Name='storahci'] or Provider[@Name='stornvme'] " +
                        "or Provider[@Name='iaStorA'] or Provider[@Name='iaStorAC'] or Provider[@Name='iaStorAVC']) and (Level=1 or Level=2 or Level=3) and " + Since(30) + "]]");
        if (ev.Count == 0)
        {
            list.Add(Item(FindingLevel.Good, "No drive errors in the last 30 days", "Windows logged no bad blocks, read/write failures or controller resets."));
            return;
        }
        var groups = ev.GroupBy(e => $"{e.Provider}/{e.Id}")
                       .Select(g => $"{(DiskEventText.TryGetValue(g.Key, out var t) ? t : g.Key)} ×{g.Count()} (last {g.Max(x => x.When):MMM d HHmm})")
                       .ToList();
        bool serious = ev.Any(e => e.Level <= 2 || e.Id is 7 or 154 || (e.Provider == "Ntfs" && e.Id == 55));
        list.Add(Item(serious ? FindingLevel.Warning : FindingLevel.Tip, $"{ev.Count} drive error(s) logged in the last 30 days",
            string.Join("; ", groups) + ". " +
            (serious ? "Bad blocks and read failures usually mean a drive is failing, or a loose/old SATA cable. Reseat or replace the cable, and back up now."
                     : "Retries and resets are often a loose SATA cable or a sleepy USB drive. Watch whether they keep happening.")));
    }

    private static void HardwareErrors(List<SecurityItem> list)
    {
        var ev = Events("*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and " + Since(90) + "]]");
        if (ev.Count == 0)
        {
            list.Add(Item(FindingLevel.Good, "No hardware errors in the last 90 days", "The processor, memory and expansion slots haven't reported any errors to Windows (WHEA)."));
            return;
        }
        string What(int id) => id switch
        {
            1 => "fatal hardware error", 17 => "corrected PCIe (slot/link) error", 18 => "fatal processor/memory error",
            19 => "corrected processor/memory error", 20 => "fatal PCIe error", 47 => "corrected memory error", _ => $"hardware event {id}",
        };
        int fatal = ev.Count(e => e.Level is 1 or 2);
        var parts = ev.GroupBy(e => e.Id).Select(g => $"{What(g.Key)} ×{g.Count()} (last {g.Max(x => x.When):MMM d HHmm})");
        string advice = ev.Any(e => e.Id is 17 or 20)
            ? "PCIe errors point at a card or drive slot: reseat the graphics card and NVMe drive, and update the chipset driver and BIOS."
            : "Processor/memory errors point at memory, cooling, an overclock (including XMP/EXPO) or a weak power supply. Run the memory test and the power & cooling test.";
        list.Add(Item(fatal > 0 || ev.Count >= 20 ? FindingLevel.Warning : FindingLevel.Tip,
            fatal > 0 ? $"{fatal} fatal hardware error(s) in the last 90 days" : $"{ev.Count} corrected hardware error(s) in the last 90 days",
            string.Join("; ", parts) + ". " + advice));
    }

    private static readonly Dictionary<string, (string Name, string Hint)> StopCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0x0000001a"] = ("MEMORY_MANAGEMENT", "often bad memory"),
        ["0x00000050"] = ("PAGE_FAULT_IN_NONPAGED_AREA", "memory or a driver"),
        ["0x0000000a"] = ("IRQL_NOT_LESS_OR_EQUAL", "usually a driver, sometimes memory"),
        ["0x000000d1"] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "a driver"),
        ["0x0000003b"] = ("SYSTEM_SERVICE_EXCEPTION", "a driver"),
        ["0x0000007e"] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "a driver"),
        ["0x0000009f"] = ("DRIVER_POWER_STATE_FAILURE", "a driver during sleep or wake"),
        ["0x00000124"] = ("WHEA_UNCORRECTABLE_ERROR", "hardware: cooling, power supply or overclock"),
        ["0x00000101"] = ("CLOCK_WATCHDOG_TIMEOUT", "processor: cooling, power or overclock"),
        ["0x00000133"] = ("DPC_WATCHDOG_VIOLATION", "a storage or chipset driver"),
        ["0x00000116"] = ("VIDEO_TDR_FAILURE", "the graphics card or its driver"),
        ["0x00000117"] = ("VIDEO_TDR_TIMEOUT_DETECTED", "the graphics card or its driver"),
        ["0x0000007a"] = ("KERNEL_DATA_INPAGE_ERROR", "the drive or its cable"),
        ["0x00000077"] = ("KERNEL_STACK_INPAGE_ERROR", "the drive or its cable"),
        ["0x000000f4"] = ("CRITICAL_OBJECT_TERMINATION", "the drive or its cable"),
        ["0x000000ef"] = ("CRITICAL_PROCESS_DIED", "damaged Windows files (Tune-up > Deep repair) or the drive"),
        ["0x00000139"] = ("KERNEL_SECURITY_CHECK_FAILURE", "a driver or memory"),
    };

    private static void Crashes(List<SecurityItem> list)
    {
        var bsod = Events("*[System[(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] or Provider[@Name='BugCheck']) and EventID=1001 and " + Since(90) + "]]", 50, withText: true);
        var power = Events("*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and " + Since(90) + "]]", 200);
        int dumps = 0;
        try
        {
            var md = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
            if (Directory.Exists(md)) dumps = Directory.GetFiles(md, "*.dmp").Length;
        }
        catch { }

        if (bsod.Count == 0 && power.Count == 0)
        {
            list.Add(Item(FindingLevel.Good, "No crashes or sudden shutdowns in the last 90 days", "No blue screens, and Windows always shut down properly."));
            return;
        }

        var parts = new List<string>();
        var level = FindingLevel.Tip;
        if (bsod.Count > 0)
        {
            var codes = bsod.Select(e => Regex.Match(e.Text, @"0x[0-9a-fA-F]{8}").Value).Where(c => c != "")
                            .GroupBy(c => c.ToLowerInvariant())
                            .Select(g => StopCodes.TryGetValue(g.Key, out var sc) ? $"{sc.Name} ×{g.Count()} ({sc.Hint})" : $"{g.Key} ×{g.Count()}");
            parts.Add($"{bsod.Count} blue screen(s), last {bsod[0].When:MMM d HHmm}: {string.Join("; ", codes)}.");
            if (bsod.Count >= 2 || (DateTime.Now - bsod[0].When).TotalDays <= 30) level = FindingLevel.Warning;
        }
        if (power.Count > 0)
        {
            parts.Add($"{power.Count} time(s) the PC lost power or was switched off without shutting down (last {power[0].When:MMM d HHmm}). " +
                      "If that happened while it was running normally, suspect the power supply, a loose power cable or overheating. Holding the power button and power cuts count too.");
            if (power.Count >= 3) level = FindingLevel.Warning;
        }
        if (dumps > 0) parts.Add($"{dumps} crash dump file(s) are in C:\\Windows\\Minidump if a technician wants them.");
        list.Add(Item(level, bsod.Count > 0 ? "Blue screens in the last 90 days" : "Sudden shutdowns in the last 90 days", string.Join(" ", parts)));
    }

    // =====================================================================
    //  Memory
    // =====================================================================

    private static void Memory(List<SecurityItem> list)
    {
        var sticks = new List<(ulong Size, string Part, uint Speed, uint Configured, string Slot, int Type)>();
        using (var s = new ManagementObjectSearcher("SELECT Capacity, PartNumber, Speed, ConfiguredClockSpeed, DeviceLocator, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
            foreach (ManagementBaseObject m in s.Get())
                sticks.Add((ToULong(m["Capacity"]), (m["PartNumber"]?.ToString() ?? "").Trim(), (uint)ToInt(m["Speed"]), (uint)ToInt(m["ConfiguredClockSpeed"]),
                            m["DeviceLocator"]?.ToString() ?? "", ToInt(m["SMBIOSMemoryType"])));
        int slots = 0;
        using (var s = new ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray"))
            foreach (ManagementBaseObject a in s.Get()) slots += ToInt(a["MemoryDevices"]);

        ulong installed = (ulong)sticks.Sum(x => (long)x.Size);
        var usable = AppEnv.GetMemory().Total;
        string type = sticks.Select(x => x.Type).FirstOrDefault() switch { 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 35 => "DDR5", _ => "" };
        uint runs = sticks.Select(x => x.Configured).DefaultIfEmpty(0u).Min();
        var facts = $"{sticks.Count} stick(s), {installed / 1073741824.0:0.#} GB {type}".Trim() +
                    (slots > 0 ? $" in {sticks.Count} of {slots} slots" : "") +
                    (runs > 0 ? $", running at {runs} MT/s" : "") + $". Windows can use {usable / 1073741824.0:0.#} GB.";

        var issues = new List<string>();
        var level = FindingLevel.Good;
        if (!Environment.Is64BitOperatingSystem && installed > 4UL << 30)
        {
            issues.Add("This is 32-bit Windows, which can only use about 3.5 GB. Installing 64-bit Windows unlocks the rest");
            level = FindingLevel.Warning;
        }
        else if (installed > 0 && (double)installed - usable > Math.Max(2.0 * (1 << 30), installed * 0.10))
        {
            issues.Add("Windows can use noticeably less than what's installed. Common causes: built-in graphics reserving memory, or a \"memory remap\" setting off in the BIOS");
            level = FindingLevel.Tip;
        }
        bool mixed = sticks.Select(x => x.Part).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
                     || sticks.Select(x => x.Size).Distinct().Count() > 1 || sticks.Select(x => x.Speed).Distinct().Count() > 1;
        if (sticks.Count > 1 && mixed)
        {
            issues.Add("The sticks aren't a matched set (" + string.Join(", ", sticks.Select(x => $"{x.Slot}: {x.Size >> 30} GB {x.Part}".Trim())) +
                       "). Mixed sticks are a common cause of random crashes on older PCs. If the memory test finds errors, test one stick at a time");
            if (level == FindingLevel.Good) level = FindingLevel.Tip;
        }
        list.Add(Item(level, level == FindingLevel.Good ? "Memory looks right" : "Memory: worth a look", facts + " " + Sentences(issues)));

        // Last Windows Memory Diagnostic result
        var md = Events("*[System[Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results']]]", 1, withText: true);
        if (md.Count == 0)
        {
            list.Add(Item(FindingLevel.Tip, "Full memory test hasn't been run",
                "Windows Memory Diagnostic tests every byte before Windows starts, which the quick test can't. It can take an hour or more with a lot of memory. Best done once on a PC you're bringing back.",
                "mdsched", "Full test at restart"));
        }
        else
        {
            var e = md[0];
            bool clean = Regex.IsMatch(e.Text, @"no errors", RegexOptions.IgnoreCase);
            bool bad = Regex.IsMatch(e.Text, @"hardware errors|detected errors", RegexOptions.IgnoreCase);
            if (bad)
                list.Add(Item(FindingLevel.Warning, $"Full memory test found errors ({e.When:MMM d})",
                    "A memory stick is faulty. Test one stick at a time (or swap slots) to find which one, and replace it. Turning off XMP/EXPO in the BIOS can help if the sticks are only unstable at their rated speed.",
                    "mdsched", "Test again at restart"));
            else
                list.Add(Item(clean ? FindingLevel.Good : FindingLevel.Tip, clean ? $"Full memory test passed ({e.When:MMM d, yyyy})" : $"Full memory test ran {e.When:MMM d, yyyy}",
                    clean ? "Windows Memory Diagnostic found no errors." : "Windows logged a result; open Event Viewer > System for the details.",
                    clean ? null : "mdsched", "Test again at restart"));
        }
    }

    // =====================================================================
    //  Sensors: temperatures, fans, power-supply rails, CMOS battery
    // =====================================================================

    internal static float? Pick(IEnumerable<SensorReading> rs, SensorType type, params string[] names)
    {
        var ofType = rs.Where(r => r.Type == type && r.Value is float f && !float.IsNaN(f)).ToList();
        foreach (var n in names)
        {
            var hit = ofType.FirstOrDefault(r => r.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit.Value;
        }
        return null;
    }

    internal static float? CpuTemp(IReadOnlyList<SensorReading> rs)
    {
        var cpu = rs.Where(r => r.RootType == HardwareType.Cpu).ToList();
        return Pick(cpu, SensorType.Temperature, "Tctl", "Package", "Tdie", "Core Max", "Core Average")
               ?? cpu.Where(r => r.Type == SensorType.Temperature && r.Value is float).Select(r => r.Value).Max();
    }

    internal static float? CpuLoad(IReadOnlyList<SensorReading> rs) =>
        Pick(rs.Where(r => r.RootType == HardwareType.Cpu), SensorType.Load, "CPU Total");

    internal static float? CpuClock(IReadOnlyList<SensorReading> rs)
    {
        var c = rs.Where(r => r.RootType == HardwareType.Cpu && r.Type == SensorType.Clock && r.Name.StartsWith("Core", StringComparison.OrdinalIgnoreCase) && r.Value > 0)
                  .Select(r => r.Value!.Value).ToList();
        return c.Count == 0 ? null : c.Average();
    }

    internal static float? CpuPower(IReadOnlyList<SensorReading> rs) =>
        Pick(rs.Where(r => r.RootType == HardwareType.Cpu), SensorType.Power, "Package");

    private static bool IsGpu(HardwareType t) => t is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    internal static (string Name, float? Temp, float? Power) Gpu(IReadOnlyList<SensorReading> rs)
    {
        var root = rs.FirstOrDefault(r => r.RootType is HardwareType.GpuNvidia or HardwareType.GpuAmd) ?? rs.FirstOrDefault(r => r.RootType == HardwareType.GpuIntel);
        if (root == null) return ("", null, null);
        var g = rs.Where(r => r.Hardware == root.Hardware).ToList();
        return (root.Hardware,
                Pick(g, SensorType.Temperature, "GPU Core", "GPU Hot Spot") ?? g.Where(r => r.Type == SensorType.Temperature && r.Value is float).Select(r => r.Value).Max(),
                Pick(g, SensorType.Power, "GPU Package", "GPU Power", "GPU Board", "GPU Core"));
    }

    public enum Rail { V12, V5, V33, Battery }

    internal static (double Nominal, double Low, double High, string Label) RailSpec(Rail r) => r switch
    {
        Rail.V12 => (12.0, 11.40, 12.60, "+12 V"),
        Rail.V5 => (5.0, 4.75, 5.25, "+5 V"),
        Rail.V33 => (3.3, 3.135, 3.465, "+3.3 V"),
        _ => (3.0, 2.85, 3.40, "CMOS battery"),
    };

    /// <summary>A motherboard voltage reading for a power-supply rail, or null if the board doesn't report it
    /// (or reports a number that can't be a real rail, which means the board's scaling isn't known).</summary>
    internal static float? RailVolts(IReadOnlyList<SensorReading> rs, Rail rail)
    {
        foreach (var r in rs.Where(r => r.RootType == HardwareType.Motherboard && r.Type == SensorType.Voltage && r.Value is float))
        {
            var n = r.Name.Replace(" ", "");
            bool standby = n.Contains("SB", StringComparison.OrdinalIgnoreCase) || n.Contains("Standby", StringComparison.OrdinalIgnoreCase) || n.Contains("Stby", StringComparison.OrdinalIgnoreCase);
            bool match = rail switch
            {
                Rail.V12 => n.Contains("12V", StringComparison.OrdinalIgnoreCase),
                Rail.V5 => !standby && Regex.IsMatch(n, @"(^|\+|[^\d\.])5V", RegexOptions.IgnoreCase),
                Rail.V33 => !standby && (n.Contains("3.3V", StringComparison.OrdinalIgnoreCase) || n.Equals("3VCC", StringComparison.OrdinalIgnoreCase) || n.Equals("+3V3", StringComparison.OrdinalIgnoreCase)),
                _ => n.Contains("Bat", StringComparison.OrdinalIgnoreCase) || n.Contains("CMOS", StringComparison.OrdinalIgnoreCase),
            };
            if (!match) continue;
            var v = r.Value!.Value;
            var spec = RailSpec(rail);
            // Anything far outside the rail's range is a scaling problem on this board, not a real voltage.
            // (A coin cell can really run down to ~2 V, so the battery gets a wider window.)
            double lo = rail == Rail.Battery ? 1.5 : spec.Nominal * 0.80, hi = rail == Rail.Battery ? 3.8 : spec.Nominal * 1.20;
            if (v > lo && v < hi) return v;
        }
        return null;
    }

    private static void Temperatures(List<SecurityItem> list, IReadOnlyList<SensorReading> rs, bool admin)
    {
        if (rs.Count == 0) { list.Add(Item(FindingLevel.Tip, "Temperatures: sensors still starting", "Run the check again in a few seconds.")); return; }
        var cpu = CpuTemp(rs);
        var load = CpuLoad(rs) ?? 0;
        var (gpuName, gpu, _) = Gpu(rs);
        var parts = new List<string>();
        var level = FindingLevel.Good;
        string? title = null;

        if (cpu is float c)
        {
            parts.Add($"Processor {Temp.Full(c, "0")}" + (load > 30 ? $" at {load:0}% load" : " at rest"));
            if (load <= 30 && c >= 80) { level = FindingLevel.Warning; title = "Processor is very hot at rest"; parts.Add("Check that the CPU fan spins and the cooler is tight. Old thermal paste dries out: clean it off and apply fresh paste"); }
            else if (load <= 30 && c >= 65) { level = FindingLevel.Tip; title = "Processor is warm for an idle PC"; parts.Add("Usually dust in the cooler or dried-out thermal paste. Blow out the dust and re-paste if it stays warm"); }
        }
        else parts.Add(admin ? "Processor temperature isn't readable yet (see \"Turn on CPU temperature\" at the top)" : "Processor temperature needs admin mode");

        if (gpu is float g)
        {
            parts.Add($"{gpuName} {Temp.Full(g, "0")}");
            if (g >= 85) { level = FindingLevel.Warning; title ??= "Graphics card is very hot at rest"; parts.Add("Check its fans spin under load and clean the dust out of its cooler"); }
            else if (g >= 75 && level == FindingLevel.Good) { level = FindingLevel.Tip; title ??= "Graphics card is warm at rest"; parts.Add("Dust in its cooler is the usual reason"); }
        }

        var boardTemps = rs.Where(r => r.RootType == HardwareType.Motherboard && r.Type == SensorType.Temperature && r.Value is float v && v > 5 && v < 110).ToList();
        if (boardTemps.Count > 0)
        {
            var hot = boardTemps.MaxBy(r => r.Value)!;
            parts.Add($"hottest board sensor ({hot.Name}) {Temp.Full(hot.Value!.Value, "0")}");
        }
        list.Add(Item(level, title ?? "Temperatures at rest are normal", Sentences(parts) + "Run the power & cooling test to see them under full load."));
    }

    private static void Fans(List<SecurityItem> list, IReadOnlyList<SensorReading> rs, bool admin)
    {
        var fans = rs.Where(r => r.Type == SensorType.Fan && !IsGpu(r.RootType)).ToList();
        if (fans.Count == 0)
        {
            list.Add(Item(FindingLevel.Tip, "Fan speeds aren't readable",
                admin ? "This board doesn't report fan speeds to Windows. Look inside: the CPU fan must spin." : "Fan speeds need admin mode."));
            return;
        }
        var cpuFan = fans.FirstOrDefault(f => f.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase));
        var text = string.Join(", ", fans.Select(f => $"{f.Name} {(f.Value is float v && v > 0 ? $"{v:N0} RPM" : "0 RPM")}"));
        if (cpuFan != null && cpuFan.Value is not > 0)
            list.Add(Item(FindingLevel.Warning, "CPU fan reads 0 RPM", text + ". Unless the cooler is a pump or the fan is plugged into a different header, the CPU fan isn't spinning: check its plug and that nothing is blocking the blades."));
        else if (fans.All(f => f.Value is not > 0))
            list.Add(Item(FindingLevel.Tip, "No fan reports a speed", text + ". Fans may be plugged into the power supply instead of the board (they still spin, the board just can't see them). Look inside and confirm the CPU fan spins."));
        else
            list.Add(Item(FindingLevel.Good, "Fans are spinning", text + ". Headers reading 0 RPM usually just have nothing plugged in."));
    }

    private static void PowerRails(List<SecurityItem> list, IReadOnlyList<SensorReading> rs, bool admin)
    {
        var parts = new List<string>();
        bool any = false, bad = false;
        foreach (var rail in new[] { Rail.V12, Rail.V5, Rail.V33 })
        {
            var v = RailVolts(rs, rail);
            if (v is not float x) continue;
            any = true;
            var spec = RailSpec(rail);
            bool ok = x >= spec.Low && x <= spec.High;
            bad |= !ok;
            parts.Add($"{spec.Label} {x:0.00} V{(ok ? "" : $" (outside {spec.Low:0.00}-{spec.High:0.00})")}");
        }
        if (any)
            list.Add(Item(bad ? FindingLevel.Warning : FindingLevel.Good, bad ? "Power supply voltage out of range" : "Power supply voltages are in range at rest",
                string.Join(", ", parts) + ". " + (bad
                    ? "A rail more than 5% off can mean a failing power supply. Board sensors can read a little off, so confirm with the power & cooling test or a multimeter before replacing it."
                    : "All within the ±5% the ATX standard allows. The power & cooling test checks them again under full load.")));
        else
            list.Add(Item(FindingLevel.Tip, "Power supply voltages aren't readable",
                admin ? "This board doesn't report usable 12 V / 5 V / 3.3 V readings. The power & cooling test still shows whether the power supply holds up under full load."
                      : "Voltages need admin mode."));

        if (RailVolts(rs, Rail.Battery) is float bat)
            list.Add(bat < 2.85f
                ? Item(FindingLevel.Warning, $"CMOS battery is weak ({bat:0.00} V)",
                    "That coin cell (CR2032) keeps the BIOS settings and clock while the PC is unplugged. Replace it: it's cheap, and a dead one makes the clock and BIOS settings reset.")
                : Item(FindingLevel.Good, $"CMOS battery is fine ({bat:0.00} V)", "The coin cell that keeps the BIOS settings and clock is healthy."));
    }

    // =====================================================================
    //  Clock (a dead CMOS battery makes an old PC wake up with the wrong date)
    // =====================================================================

    private static void Clock(List<SecurityItem> list, IReadOnlyList<SensorReading> rs)
    {
        DateTimeOffset? net = null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var req = new HttpRequestMessage(HttpMethod.Get, "http://www.msftconnecttest.com/connecttest.txt"); // plain HTTP on purpose: a wrong clock breaks HTTPS
            using var resp = http.Send(req);
            net = resp.Headers.Date;
        }
        catch { }
        string zone = TimeZoneInfo.Local.StandardName;
        if (net is not DateTimeOffset n)
        {
            list.Add(Item(FindingLevel.Tip, "Clock: couldn't check (no internet)",
                $"Time zone is {zone}. Connect to the internet and run the check again: a wrong clock breaks secure websites and Windows Update."));
            return;
        }
        var diff = DateTimeOffset.UtcNow - n;
        double sec = Math.Abs(diff.TotalSeconds);
        if (sec <= 120)
        {
            list.Add(Item(FindingLevel.Good, "Clock is right", $"Within {sec:0} seconds of internet time. Time zone: {zone}."));
            return;
        }
        string off = sec >= 86400 ? $"{sec / 86400:0.#} days" : sec >= 3600 ? $"{sec / 3600:0.#} hours" : $"{sec / 60:0} minutes";
        bool weakBattery = RailVolts(rs, Rail.Battery) is float b && b < 2.85f;
        list.Add(Item(FindingLevel.Warning, $"Clock is {off} {(diff.TotalSeconds > 0 ? "fast" : "slow")}",
            "A wrong clock breaks secure websites, sign-ins and Windows Update. " +
            (sec >= 86400 || weakBattery
                ? "On a PC that sat unplugged, this almost always means the CMOS coin-cell battery (CR2032) is dead; replace it or the clock resets every time the power is cut. "
                : "") + $"Time zone: {zone}.", "clock-sync", "Set clock now"));
    }

    // =====================================================================
    //  Devices and network
    // =====================================================================

    private static void Devices(List<SecurityItem> list)
    {
        var p = DriverService.ScanProblems();
        if (p.Count == 0)
            list.Add(Item(FindingLevel.Good, "Every device has a working driver", "Nothing in Device Manager has a yellow warning."));
        else
            list.Add(Item(FindingLevel.Warning, $"{p.Count} device(s) aren't working",
                string.Join("; ", p.Take(6).Select(d => $"{d.Name}: {d.Meaning}")) + (p.Count > 6 ? $"; and {p.Count - 6} more" : "") +
                ". The Drivers tab can search Windows Update for the missing drivers.", "goto-drivers", "Open Drivers tab"));
    }

    private static void Network(List<SecurityItem> list)
    {
        var links = new List<(string Name, ulong Speed, bool Wired)>();
        using (var s = new ManagementObjectSearcher(@"root\StandardCimv2",
                   "SELECT Name, InterfaceDescription, Speed, MediaConnectState, PhysicalMediaType FROM MSFT_NetAdapter WHERE ConnectorPresent = TRUE"))
            foreach (ManagementBaseObject a in s.Get())
            {
                if (ToInt(a["MediaConnectState"]) != 1) continue;
                int pm = ToInt(a["PhysicalMediaType"]);
                links.Add(($"{a["InterfaceDescription"] ?? a["Name"]}", ToULong(a["Speed"]), pm == 14));
            }
        if (links.Count == 0)
        {
            list.Add(Item(FindingLevel.Tip, "Not connected to a network", "Windows Update, Defender updates and the clock check need the internet."));
            return;
        }
        static string Mbps(ulong bps) => bps >= 1_000_000_000 ? $"{bps / 1e9:0.#} Gbps" : $"{bps / 1e6:0} Mbps";
        var slowWired = links.Where(l => l.Wired && l.Speed > 0 && l.Speed < 1_000_000_000).ToList();
        string text = string.Join("; ", links.Select(l => $"{l.Name}: {(l.Wired ? "wired" : "wireless")}, {Mbps(l.Speed)}"));
        if (slowWired.Count > 0)
            list.Add(Item(FindingLevel.Tip, $"Wired network is only running at {Mbps(slowWired[0].Speed)}",
                text + ". A gigabit port stuck at 100 Mbps almost always means a damaged or old cable (it needs all 8 wires: Cat5e or newer) or a 100 Mbps port on the other end."));
        else
            list.Add(Item(FindingLevel.Good, "Network link is good", text + "."));
    }

    // =====================================================================
    //  Windows 11 readiness (only checked on Windows 10)
    // =====================================================================

    [DllImport("kernel32.dll")]
    private static extern bool GetFirmwareType(out uint type); // 1 = legacy BIOS, 2 = UEFI

    private static void Win11Ready(List<SecurityItem> list, bool admin)
    {
        var no = new List<string>();
        var unsure = new List<string>();
        var yes = new List<string>();

        string cpuName = "";
        using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
            foreach (ManagementBaseObject p in s.Get()) cpuName = p["Name"]?.ToString()?.Trim() ?? "";
        var (cpuOk, cpuWhy) = Win11Cpu(cpuName);
        (cpuOk == true ? yes : cpuOk == false ? no : unsure).Add($"processor: {cpuWhy}");

        bool uefi = GetFirmwareType(out var fw) && fw == 2;
        if (uefi) yes.Add("starts in UEFI mode (Secure Boot capable)");
        else no.Add("Windows starts in old Legacy BIOS mode. Microsoft's MBR2GPT tool converts the drive without erasing it; then switch the BIOS to UEFI and turn on Secure Boot");

        if (admin)
        {
            string? spec = null;
            try
            {
                using var t = new ManagementObjectSearcher(@"root\cimv2\Security\MicrosoftTpm", "SELECT SpecVersion, IsEnabled_InitialValue FROM Win32_Tpm");
                foreach (ManagementBaseObject o in t.Get()) spec = (o["SpecVersion"]?.ToString() ?? "").Split(',')[0].Trim();
            }
            catch { }
            if (spec == null) no.Add("no TPM found. Most PCs from 2016 on have one built in: turn on \"fTPM\" (AMD) or \"PTT\" (Intel) in the BIOS");
            else if (spec.StartsWith("2")) yes.Add("TPM 2.0");
            else no.Add($"TPM is version {spec}; Windows 11 needs 2.0");
        }
        else unsure.Add("TPM (needs admin mode to check)");

        var ram = AppEnv.GetMemory().Total;
        if (ram >= 4UL << 30) yes.Add($"{ram / 1073741824.0:0} GB memory"); else no.Add("needs at least 4 GB of memory");
        try
        {
            var sys = new DriveInfo(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:");
            if (sys.TotalSize >= 64L * 1000 * 1000 * 1000) yes.Add($"{sys.TotalSize / 1e9:0} GB system drive"); else no.Add("system drive is smaller than 64 GB");
        }
        catch { }

        if (no.Count == 0 && unsure.Count == 0)
            list.Add(Item(FindingLevel.Good, "Ready for Windows 11", Sentences(yes) + "Windows 10 no longer gets security updates, so upgrade: Settings > Windows Update, or Microsoft's Installation Assistant.", "pchealth", "Get PC Health Check"));
        else
            list.Add(Item(no.Count > 0 ? FindingLevel.Warning : FindingLevel.Tip, no.Count > 0 ? "Not ready for Windows 11 yet" : "Windows 11 readiness: not sure yet",
                (no.Count > 0 ? "Blocking: " + Sentences(no) : "") + (unsure.Count > 0 ? "Couldn't confirm: " + Sentences(unsure) : "") + (yes.Count > 0 ? "OK: " + Sentences(yes) : "") +
                "Microsoft's PC Health Check gives the official answer.", "pchealth", "Get PC Health Check"));
    }

    /// <summary>Rough Windows 11 processor check from the name (Intel 8th gen+, AMD Ryzen 2000 Zen+ and newer). Null = can't tell.</summary>
    internal static (bool? Ok, string Why) Win11Cpu(string name)
    {
        var n = name.ToUpperInvariant();
        var intel = Regex.Match(n, @"\bI[3579]-(\d{3,5})");
        if (intel.Success)
        {
            var num = intel.Groups[1].Value;
            int gen = num.Length == 5 || (num.Length == 4 && num[0] == '1') ? int.Parse(num[..2]) : num.Length == 4 ? num[0] - '0' : 1;
            return gen >= 8 ? (true, $"Intel Core {Ord(gen)} gen ({name})") : (false, $"Intel Core {Ord(gen)} gen is too old; Windows 11 needs 8th gen or newer ({name})");
        }
        if (n.Contains("CORE ULTRA") || n.Contains("CORE(TM) ULTRA") || Regex.IsMatch(n, @"CORE\(TM\) [3579] \d{3}")) return (true, $"Intel Core Ultra / Core 100-200 series ({name})");
        var tr = Regex.Match(n, @"THREADRIPPER\s+(?:PRO\s+)?(\d)\d{3}");
        if (tr.Success) return tr.Groups[1].Value[0] >= '2' ? (true, name) : (false, $"first-generation Threadripper isn't supported ({name})");
        if (n.Contains("RYZEN AI") || n.Contains("RYZEN Z")) return (true, name);
        var ry = Regex.Match(n, @"RYZEN\s+\d\s+(?:PRO\s+)?(\d)(\d{3})([A-Z0-9]*)");
        if (ry.Success)
        {
            int series = ry.Groups[1].Value[0] - '0';
            string suffix = ry.Groups[3].Value;
            if (series >= 3) return (true, name);
            if (series == 2 && !(suffix.StartsWith("G") || suffix.StartsWith("U") || suffix.StartsWith("H"))) return (true, $"Ryzen 2000 (Zen+) ({name})");
            return (false, $"this first-generation Ryzen (Zen 1) isn't on Microsoft's list ({name})");
        }
        return (null, $"couldn't tell from the name \"{name}\"");
    }

    private static string Ord(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    // =====================================================================
    //  Helpers
    // =====================================================================

    internal static SecurityItem Item(FindingLevel level, string title, string detail, string? fix = null, string label = "Fix") =>
        new() { Level = level, Title = title, Detail = detail.Trim(), FixId = fix, FixLabel = label };

    private static string Sentences(IEnumerable<string> parts)
    {
        var s = string.Join(". ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.TrimEnd('.', ' ')));
        return s.Length == 0 ? "" : Cap(s) + ". ";
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Norm(string s) => Regex.Replace(s.ToUpperInvariant(), "[^A-Z0-9]", "");

    private static string DecimalSize(ulong bytes) => bytes >= 1_000_000_000_000 ? $"{bytes / 1e12:0.##} TB" : $"{bytes / 1e9:0} GB";

    private static int ToInt(object? v, int fallback = 0) { try { return v == null ? fallback : Convert.ToInt32(v); } catch { return fallback; } }
    private static int? ToIntN(object? v) { try { return v == null ? null : Convert.ToInt32(v); } catch { return null; } }
    private static long? ToLongN(object? v) { try { return v == null ? null : Convert.ToInt64(v); } catch { return null; } }
    private static ulong ToULong(object? v) { try { return v == null ? 0 : Convert.ToUInt64(v); } catch { return 0; } }
}
