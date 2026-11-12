// Moore PC Mechanic - drivers
// 1) Lists every installed driver (version, date, maker) and any device Windows reports a problem with.
// 2) Asks Windows Update for driver updates, then downloads and installs the ones you pick
//    (after making a System Restore point so the change can be undone).
// 3) Checks NVIDIA for the newest graphics driver for your exact card, downloads it, checks
//    NVIDIA's digital signature, and runs NVIDIA's installer silently.

using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml.Linq;

namespace MoorePCMechanic.Services;

public sealed class DriverInfo
{
    public string Device { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? Date { get; init; }
    public string Inf { get; init; } = "";
    public string DateText => Date?.ToString("yyyy-MM-dd") ?? "—";
    public bool IsMicrosoft => Provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
    public string AgeText
    {
        get
        {
            if (Date is not DateTime d) return "";
            var years = (DateTime.Now - d).TotalDays / 365.25;
            if (years >= 3) return $"{years:0} years old";
            if (years >= 1) return $"{years:0.#} years old";
            return "This year";
        }
    }
}

public sealed class ProblemDevice
{
    public string Name { get; init; } = "";
    public int Code { get; init; }
    public string Meaning { get; init; } = "";
    public string DeviceId { get; init; } = "";
}

public sealed class DriverUpdate
{
    internal dynamic? Com { get; init; }
    public string Title { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string Maker { get; init; } = "";
    public string Model { get; init; } = "";
    public DateTime? Date { get; init; }
    public long Size { get; init; }
    public string DateText => Date?.ToString("yyyy-MM-dd") ?? "—";
    public string SizeText => Size > 0 ? AppEnv.Bytes(Size) : "";
    public bool Selected { get; set; } = true;
    public string Result { get; set; } = "";
}

public sealed class NvidiaStatus
{
    public string GpuName { get; init; } = "";
    public string InstalledVersion { get; init; } = "";   // e.g. 566.14
    public string? LatestVersion { get; init; }
    public string? LatestName { get; init; }
    public DateTime? LatestDate { get; init; }
    public string? DownloadUrl { get; init; }
    public long DownloadSize { get; init; }
    public string? Error { get; init; }
    public bool UpdateAvailable =>
        LatestVersion != null && TryVer(LatestVersion, out var l) && TryVer(InstalledVersion, out var i) && l > i;

    internal static bool TryVer(string s, out Version v) => Version.TryParse(s, out v!);
}

public static class DriverService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MoorePCMechanic");
        return c;
    }

    // =====================================================================
    //  Installed drivers and problem devices
    // =====================================================================

    public static List<DriverInfo> ScanInstalled()
    {
        var list = new List<DriverInfo>();
        using var s = new ManagementObjectSearcher(
            "SELECT DeviceName, DeviceClass, DriverProviderName, DriverVersion, DriverDate, InfName FROM Win32_PnPSignedDriver");
        foreach (ManagementObject o in s.Get())
        {
            using (o)
            {
                var name = o["DeviceName"] as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                DateTime? date = null;
                if (o["DriverDate"] is string ds && ds.Length >= 8)
                {
                    try { date = ManagementDateTimeConverter.ToDateTime(ds); } catch { }
                }
                list.Add(new DriverInfo
                {
                    Device = name.Trim(),
                    ClassName = Friendly(o["DeviceClass"] as string),
                    Provider = (o["DriverProviderName"] as string ?? "").Trim(),
                    Version = (o["DriverVersion"] as string ?? "").Trim(),
                    Date = date,
                    Inf = o["InfName"] as string ?? "",
                });
            }
        }
        // Hardware makers' drivers first (they're the ones that matter), newest problems first within a class.
        return list
            .GroupBy(d => d.Device + "|" + d.Version)
            .Select(g => g.First())
            .OrderBy(d => d.IsMicrosoft)
            .ThenBy(d => ClassRank(d.ClassName))
            .ThenBy(d => d.Device)
            .ToList();
    }

    public static List<ProblemDevice> ScanProblems()
    {
        var list = new List<ProblemDevice>();
        using var s = new ManagementObjectSearcher(
            "SELECT Name, DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
        foreach (ManagementObject o in s.Get())
        {
            using (o)
            {
                int code = Convert.ToInt32(o["ConfigManagerErrorCode"] ?? 0);
                if (code == 45) continue; // "not connected right now": normal for unplugged USB devices
                list.Add(new ProblemDevice
                {
                    Name = (o["Name"] as string) ?? (o["DeviceID"] as string) ?? "Unknown device",
                    DeviceId = o["DeviceID"] as string ?? "",
                    Code = code,
                    Meaning = ProblemText(code),
                });
            }
        }
        return list;
    }

    private static string ProblemText(int code) => code switch
    {
        1 => "Not set up correctly (code 1). Reinstalling the driver usually fixes it.",
        3 => "The driver may be damaged or the PC is low on memory (code 3).",
        10 => "Can't start (code 10). Usually a missing or wrong driver.",
        12 => "Can't find enough free resources (code 12).",
        14 => "Needs a restart to work (code 14).",
        18 => "The driver needs to be reinstalled (code 18).",
        19 => "Windows' registry entry for it is damaged (code 19).",
        21 => "Windows is removing this device (code 21).",
        22 => "Turned off in Device Manager (code 22).",
        24 => "Not present, not working, or missing drivers (code 24).",
        28 => "No driver installed (code 28). A Windows Update scan often finds one.",
        29 => "Turned off in the BIOS/firmware (code 29).",
        31 => "Windows can't load the driver (code 31).",
        32 => "Its driver service is turned off (code 32).",
        37 => "The driver failed to start (code 37).",
        38 => "An older copy of the driver is still loaded (code 38). Restart the PC.",
        39 => "The driver is damaged or missing (code 39).",
        40 => "Windows can't read its registry settings (code 40).",
        41 => "The driver loaded but can't find the device (code 41).",
        43 => "Windows stopped it because it reported a problem (code 43).",
        48 => "Blocked because it's known to cause problems (code 48).",
        52 => "The driver's digital signature can't be verified (code 52).",
        _ => $"Windows reports a problem (code {code}).",
    };

    private static string Friendly(string? cls) => (cls ?? "").ToUpperInvariant() switch
    {
        "DISPLAY" => "Graphics",
        "MEDIA" => "Sound",
        "AUDIOENDPOINT" => "Audio endpoint",
        "NET" => "Network",
        "BLUETOOTH" => "Bluetooth",
        "SYSTEM" => "System / chipset",
        "HDC" => "Storage controller",
        "SCSIADAPTER" => "Storage controller",
        "DISKDRIVE" => "Drive",
        "USB" => "USB",
        "HIDCLASS" => "Input (HID)",
        "KEYBOARD" => "Keyboard",
        "MOUSE" => "Mouse",
        "MONITOR" => "Monitor",
        "CAMERA" => "Camera",
        "IMAGE" => "Camera",
        "PRINTER" => "Printer",
        "SOFTWAREDEVICE" => "Software device",
        "SOFTWARECOMPONENT" => "Software component",
        "FIRMWARE" => "Firmware",
        "PROCESSOR" => "Processor",
        "" => "Other",
        var x => char.ToUpper(x[0]) + x[1..].ToLowerInvariant(),
    };

    private static int ClassRank(string c) => c switch
    {
        "Graphics" => 0, "Sound" => 1, "Network" => 2, "Bluetooth" => 3, "System / chipset" => 4,
        "Storage controller" => 5, "Camera" => 6, "USB" => 7, "Firmware" => 8, _ => 20,
    };

    // =====================================================================
    //  Windows Update: driver updates
    // =====================================================================

    private static dynamic NewSession()
    {
        var t = Type.GetTypeFromProgID("Microsoft.Update.Session")
                ?? throw new InvalidOperationException("Windows Update isn't available on this PC.");
        dynamic session = Activator.CreateInstance(t)!;
        try { session.ClientApplicationID = "Moore PC Mechanic"; } catch { }
        return session;
    }

    /// <summary>Asks Windows Update (online) for driver updates that aren't installed yet. Can take a few minutes.</summary>
    public static List<DriverUpdate> SearchWindowsUpdate()
    {
        dynamic session = NewSession();
        dynamic searcher = session.CreateUpdateSearcher();
        try { searcher.ServerSelection = 2; } catch { } // 2 = Windows Update itself, even on PCs set up for a company server
        searcher.Online = true;
        dynamic result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
        var list = new List<DriverUpdate>();
        int count = result.Updates.Count;
        for (int i = 0; i < count; i++)
        {
            dynamic u = result.Updates.Item(i);
            DateTime? date = null;
            try { date = (DateTime)u.DriverVerDate; } catch { }
            long size = 0;
            try { size = Convert.ToInt64(u.MaxDownloadSize); } catch { }
            string cls = "", maker = "", model = "";
            try { cls = Friendly((string)u.DriverClass); } catch { }
            try { maker = (string)u.DriverManufacturer; } catch { }
            try { model = (string)u.DriverModel; } catch { }
            list.Add(new DriverUpdate
            {
                Com = u,
                Title = (string)u.Title,
                ClassName = cls,
                Maker = maker,
                Model = model,
                Date = date,
                Size = size,
            });
        }
        return list;
    }

    /// <summary>Downloads and installs the chosen updates one at a time. Needs admin. Returns true if a restart is needed.</summary>
    public static bool InstallWindowsUpdates(IList<DriverUpdate> updates, IProgress<string> progress)
    {
        dynamic session = NewSession();
        bool reboot = false;
        int n = 0;
        foreach (var up in updates)
        {
            n++;
            var label = $"({n} of {updates.Count}) {up.Title}";
            try
            {
                dynamic u = up.Com!;
                try { if (!(bool)u.EulaAccepted) u.AcceptEula(); } catch { }
                var collType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!;
                dynamic coll = Activator.CreateInstance(collType)!;
                coll.Add(u);

                progress.Report("Downloading " + label);
                dynamic dl = session.CreateUpdateDownloader();
                dl.Updates = coll;
                dynamic dres = dl.Download();
                int dcode = (int)dres.ResultCode;
                if (dcode != 2 && dcode != 3) { up.Result = "Download failed"; continue; }

                progress.Report("Installing " + label);
                dynamic inst = session.CreateUpdateInstaller();
                inst.Updates = coll;
                dynamic ires = inst.Install();
                int icode = (int)ires.ResultCode;
                up.Result = icode switch { 2 => "Installed", 3 => "Installed (with warnings)", 4 => "Failed", 5 => "Cancelled", _ => "Unknown result" };
                try { if ((bool)ires.RebootRequired) { reboot = true; up.Result += ", restart needed"; } } catch { }
            }
            catch (COMException ex)
            {
                up.Result = "Failed: " + WuError(ex.HResult);
            }
            catch (Exception ex)
            {
                up.Result = "Failed: " + ex.Message;
            }
        }
        return reboot;
    }

    private static string WuError(int hr) => (uint)hr switch
    {
        0x80240044 => "needs administrator rights",
        0x8024402C or 0x80072EE7 or 0x80072EFD => "couldn't reach Windows Update (internet?)",
        0x80240016 => "Windows Update is busy installing something else; try again in a few minutes",
        0x8024001E => "the update service was stopped",
        _ => $"error 0x{(uint)hr:X8}",
    };

    // =====================================================================
    //  System Restore point (the "undo" before driver changes)
    // =====================================================================

    /// <summary>Makes a restore point. Returns a plain-English result. Needs admin.</summary>
    public static (bool Ok, string Message) CreateRestorePoint(string description)
    {
        try
        {
            DateTime before = NewestRestorePoint() ?? DateTime.MinValue;
            using var mc = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            object ret = mc.InvokeMethod("CreateRestorePoint", new object[] { description, 10, 100 }); // DEVICE_DRIVER_INSTALL, BEGIN_SYSTEM_CHANGE
            uint code = Convert.ToUInt32(ret ?? 1u);
            if (code == 1058) return (false, "System Protection is turned off for drive C:, so Windows can't make restore points.");
            if (code != 0) return (false, $"Windows couldn't make a restore point (code {code}).");
            DateTime? after = NewestRestorePoint();
            if (after is DateTime a && a > before) return (true, "Restore point created: \"" + description + "\".");
            return (true, "Windows already made a restore point in the last 24 hours, so it didn't make another (that one still works as an undo).");
        }
        catch (Exception ex)
        {
            return (false, "Couldn't make a restore point: " + ex.Message);
        }
    }

    private static DateTime? NewestRestorePoint()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\default", "SELECT CreationTime FROM SystemRestore");
            DateTime? newest = null;
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    if (o["CreationTime"] is string t)
                    {
                        var d = ManagementDateTimeConverter.ToDateTime(t);
                        if (newest == null || d > newest) newest = d;
                    }
                }
            }
            return newest;
        }
        catch { return null; }
    }

    // =====================================================================
    //  NVIDIA graphics driver
    // =====================================================================

    /// <summary>(GPU name, Windows driver version) of the first NVIDIA card, or null.</summary>
    public static (string Name, string WinVersion)? FindNvidiaGpu()
    {
        using var s = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController");
        foreach (ManagementObject o in s.Get())
        {
            using (o)
            {
                var name = o["Name"] as string ?? "";
                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                    return (name.Trim(), o["DriverVersion"] as string ?? "");
            }
        }
        return null;
    }

    /// <summary>Windows shows NVIDIA drivers as e.g. 32.0.15.6614; NVIDIA calls that 566.14 (the last 5 digits).</summary>
    public static string NvidiaVersionFromWindows(string winVersion)
    {
        var parts = winVersion.Split('.');
        if (parts.Length < 4) return winVersion;
        var digits = (parts[2] + parts[3].PadLeft(4, '0'));
        if (digits.Length < 5) return winVersion;
        var last5 = digits[^5..];
        return $"{int.Parse(last5[..3])}.{last5[3..]}";
    }

    public static async Task<NvidiaStatus?> CheckNvidiaAsync(CancellationToken ct = default)
    {
        var gpu = FindNvidiaGpu();
        if (gpu is null) return null;
        return await CheckNvidiaForAsync(gpu.Value.Name, gpu.Value.WinVersion, ct);
    }

    /// <summary>Looks up NVIDIA's newest driver for a named card (split out so it can be tested without that card).</summary>
    public static async Task<NvidiaStatus> CheckNvidiaForAsync(string name, string winVer, CancellationToken ct = default)
    {
        var installed = NvidiaVersionFromWindows(winVer);
        try
        {
            var (psid, pfid) = await LookupNvidiaIdsAsync(name, ct);
            if (psid == null || pfid == null)
                return new NvidiaStatus { GpuName = name, InstalledVersion = installed, Error = "NVIDIA's site didn't list this card by name." };

            bool win11 = Environment.OSVersion.Version.Build >= 22000;
            string os = win11 ? "135" : "57";
            var url = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
                      $"?func=DriverManualLookup&psid={psid}&pfid={pfid}&osID={os}&languageCode=1033&isWHQL=1&dch=1&sort1=0&numberOfResults=1";
            var json = await Http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var ids = doc.RootElement.GetProperty("IDS");
            if (ids.GetArrayLength() == 0)
                return new NvidiaStatus { GpuName = name, InstalledVersion = installed, Error = "NVIDIA didn't return a driver for this card." };
            var info = ids[0].GetProperty("downloadInfo");
            string Get(string p) => info.TryGetProperty(p, out var v) ? Uri.UnescapeDataString(v.GetString() ?? "") : "";
            DateTime? date = DateTime.TryParse(Get("ReleaseDateTime"), out var d) ? d : null;
            long size = 0;
            var sizeText = Get("DownloadURLFileSize");
            if (sizeText.EndsWith("MB", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(sizeText[..^2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mb))
                size = (long)(mb * 1024 * 1024);
            return new NvidiaStatus
            {
                GpuName = name,
                InstalledVersion = installed,
                LatestVersion = Get("Version"),
                LatestName = Get("Name"),
                LatestDate = date,
                DownloadUrl = Get("DownloadURL"),
                DownloadSize = size,
            };
        }
        catch (Exception ex)
        {
            return new NvidiaStatus { GpuName = name, InstalledVersion = installed, Error = "Couldn't reach NVIDIA: " + ex.Message };
        }
    }

    /// <summary>NVIDIA's product list maps a card name (e.g. "GeForce RTX 4070") to its series id (psid) and product id (pfid).</summary>
    private static async Task<(string? Psid, string? Pfid)> LookupNvidiaIdsAsync(string gpuName, CancellationToken ct)
    {
        var xml = await Http.GetStringAsync("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3", ct);
        var doc = XDocument.Parse(xml);
        string target = Normalize(gpuName);
        foreach (var lv in doc.Descendants("LookupValue"))
        {
            var n = (string?)lv.Element("Name");
            if (n != null && Normalize(n) == target)
                return ((string?)lv.Attribute("ParentID"), (string?)lv.Element("Value"));
        }
        return (null, null);

        static string Normalize(string s) =>
            s.Replace("NVIDIA", "", StringComparison.OrdinalIgnoreCase).Replace("(TM)", "").Replace("(R)", "")
             .Replace("  ", " ").Trim().ToLowerInvariant();
    }

    /// <summary>Downloads the NVIDIA driver, checks it's genuinely signed by NVIDIA, and returns the file path.</summary>
    public static async Task<string> DownloadNvidiaAsync(NvidiaStatus st, IProgress<double> progress, CancellationToken ct = default)
    {
        if (st.DownloadUrl is null) throw new InvalidOperationException("No download link.");
        var uri = new Uri(st.DownloadUrl);
        if (!uri.Host.EndsWith(".nvidia.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The download link isn't on nvidia.com, so it wasn't used.");

        var dir = Path.Combine(Path.GetTempPath(), "MoorePCMechanic", "nvidia");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Path.GetFileName(uri.LocalPath));

        using (var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? st.DownloadSize;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
            var buf = new byte[1 << 20];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress.Report((double)read / total);
            }
        }

        var signer = VerifySignature(file);
        if (signer == null || !signer.Contains("NVIDIA Corporation", StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(file); } catch { }
            throw new InvalidDataException("The download isn't validly signed by NVIDIA, so it was deleted and not run.");
        }
        return file;
    }

    /// <summary>Runs NVIDIA's installer silently (graphics driver + its audio, no restart). Screens flicker for a moment.</summary>
    public static async Task<int> RunNvidiaInstallerAsync(string file, bool silent)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = true,
            Arguments = silent ? "-s -noreboot -noeula" : "",
        };
        if (!AppEnv.IsAdmin()) psi.Verb = "runas";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("The installer didn't start.");
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    // =====================================================================
    //  Authenticode check (is this file really signed, and by whom?)
    // =====================================================================

    /// <summary>Returns the signer's subject (e.g. "CN=NVIDIA Corporation, ...") if the file's signature is valid, else null.</summary>
    public static string? VerifySignature(string file)
    {
        if (WinVerifyTrustFile(file) != 0) return null;
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            return cert.Subject;
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static int WinVerifyTrustFile(string path)
    {
        var fi = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fi, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 1,       // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x1000,    // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            int result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
            data.dwStateAction = 2;      // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
            return result;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }
}
