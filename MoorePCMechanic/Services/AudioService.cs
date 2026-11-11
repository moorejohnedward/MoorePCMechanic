// Moore PC Mechanic - Audio & calls
//
// Audio Guard: keeps the headset John picks as Windows' default speaker and mic
// for every role (normal, media, and "communications" - the one Zoom and Teams use).
// When Windows switches to something else, it switches back. It also logs every
// time the headset disappears and comes back, so wireless dropouts can be told
// apart from Windows switching devices on its own.

using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MoorePCMechanic.Services;

public enum AudioEventKind { Info, Switched, Restored, Dropped, Reconnected, Warning }

public sealed record AudioEvent(DateTime Time, AudioEventKind Kind, string Message)
{
    public string TimeText => Time.ToString("h:mm:ss tt");
    public string KindText => Kind switch
    {
        AudioEventKind.Switched => "SWITCH",
        AudioEventKind.Restored => "FIXED",
        AudioEventKind.Dropped => "DROP",
        AudioEventKind.Reconnected => "BACK",
        AudioEventKind.Warning => "CHECK",
        _ => "INFO",
    };
}

/// <summary>Saved choices for the Audio tab.</summary>
public sealed class AudioSettings
{
    public string? SpeakerId { get; set; }
    public string? SpeakerName { get; set; }
    public string? MicId { get; set; }
    public string? MicName { get; set; }
    public bool GuardEnabled { get; set; } = true;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Moore PC Mechanic", "audio-settings.json");

    public static AudioSettings Load()
    {
        try { return JsonSerializer.Deserialize<AudioSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

/// <summary>Snapshot of one audio device for the UI.</summary>
public sealed class AudioDeviceInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required bool IsMic { get; init; }
    public required DeviceState State { get; init; }
    public bool IsDefault { get; init; }
    public bool IsDefaultComms { get; init; }

    public bool IsActive => State == DeviceState.Active;
    public string KindText => IsMic ? "Microphone" : "Speaker / headphones";
    public string StateText => State switch
    {
        DeviceState.Active => "On",
        DeviceState.Disabled => "Turned off",
        DeviceState.Unplugged => "Unplugged",
        DeviceState.NotPresent => "Not connected",
        _ => State.ToString(),
    };
    public string DefaultText => (IsDefault, IsDefaultComms) switch
    {
        (true, true) => "Default (all)",
        (true, false) => "Default (not for calls)",
        (false, true) => "Calls only",
        _ => "",
    };
    public override string ToString() => Name;
}

public sealed class AudioService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly AudioNotificationClient _client;
    private readonly Dictionary<string, DateTime> _dropStarted = new();
    private readonly Queue<DateTime> _recentRestores = new();
    private DateTime _pausedUntil = DateTime.MinValue;
    private bool _restorePending;

    public AudioSettings Settings { get; } = AudioSettings.Load();

    /// <summary>Raised on the UI thread for every logged event.</summary>
    public event Action<AudioEvent>? EventLogged;
    /// <summary>Raised on the UI thread when devices or defaults change.</summary>
    public event Action? DevicesChanged;

    public int DropsToday { get; private set; }
    public TimeSpan LongestDropToday { get; private set; }
    public int SwitchesToday { get; private set; }

    public AudioService()
    {
        _client = new AudioNotificationClient(this);
        _enumerator.RegisterEndpointNotificationCallback(_client);
        AutoPickHeadsetIfNeeded();
    }

    // =====================================================================
    //  Devices
    // =====================================================================

    public List<AudioDeviceInfo> GetDevices()
    {
        var list = new List<AudioDeviceInfo>();
        string? defOut = DefaultId(DataFlow.Render, Role.Multimedia), defOutCom = DefaultId(DataFlow.Render, Role.Communications);
        string? defIn = DefaultId(DataFlow.Capture, Role.Multimedia), defInCom = DefaultId(DataFlow.Capture, Role.Communications);

        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            MMDeviceCollection devices;
            try { devices = _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active | DeviceState.Disabled | DeviceState.Unplugged); }
            catch { continue; }
            foreach (var d in devices)
            {
                try
                {
                    var isMic = flow == DataFlow.Capture;
                    list.Add(new AudioDeviceInfo
                    {
                        Id = d.ID,
                        Name = SafeName(d),
                        IsMic = isMic,
                        State = d.State,
                        IsDefault = d.ID == (isMic ? defIn : defOut),
                        IsDefaultComms = d.ID == (isMic ? defInCom : defOutCom),
                    });
                }
                catch { }
                finally { d.Dispose(); }
            }
        }
        return list.OrderBy(x => x.IsMic).ThenBy(x => x.State != DeviceState.Active).ThenBy(x => x.Name).ToList();
    }

    public string? DefaultId(DataFlow flow, Role role)
    {
        try
        {
            if (!_enumerator.HasDefaultAudioEndpoint(flow, role)) return null;
            using var d = _enumerator.GetDefaultAudioEndpoint(flow, role);
            return d.ID;
        }
        catch { return null; }
    }

    public string NameOf(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "nothing";
        try { using var d = _enumerator.GetDevice(id); return SafeName(d); }
        catch { return "an unknown device"; }
    }

    public bool IsActive(string? id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        try { using var d = _enumerator.GetDevice(id); return d.State == DeviceState.Active; }
        catch { return false; }
    }

    private static string SafeName(MMDevice d)
    {
        try { return d.FriendlyName; } catch { }
        try { return d.DeviceFriendlyName; } catch { }
        return "Unnamed device";
    }

    /// <summary>First run: pick the Barracuda (or any headset) automatically.</summary>
    private void AutoPickHeadsetIfNeeded()
    {
        if (Settings.SpeakerId is not null && Settings.MicId is not null) return;
        var devices = GetDevices().Where(d => d.IsActive).ToList();

        static int Score(AudioDeviceInfo d)
        {
            var n = d.Name.ToLowerInvariant();
            if (n.Contains("barracuda")) return 3;
            if (n.Contains("razer")) return 2;
            if (n.Contains("headset") || n.Contains("headphone")) return 1;
            return 0;
        }

        var spk = devices.Where(d => !d.IsMic).OrderByDescending(Score).ThenByDescending(d => d.IsDefaultComms).FirstOrDefault();
        var mic = devices.Where(d => d.IsMic).OrderByDescending(Score).ThenByDescending(d => d.IsDefaultComms).FirstOrDefault();
        if (Settings.SpeakerId is null && spk is not null) { Settings.SpeakerId = spk.Id; Settings.SpeakerName = spk.Name; }
        if (Settings.MicId is null && mic is not null) { Settings.MicId = mic.Id; Settings.MicName = mic.Name; }
        Settings.Save();
    }

    public void SetPreferred(AudioDeviceInfo? speaker, AudioDeviceInfo? mic)
    {
        if (speaker is not null) { Settings.SpeakerId = speaker.Id; Settings.SpeakerName = speaker.Name; }
        if (mic is not null) { Settings.MicId = mic.Id; Settings.MicName = mic.Name; }
        Settings.Save();
        if (Settings.GuardEnabled) RestoreDefaults("you picked new devices");
    }

    public void SetGuard(bool on)
    {
        Settings.GuardEnabled = on;
        Settings.Save();
        Log(AudioEventKind.Info, on ? "Audio Guard turned on." : "Audio Guard turned off.");
        if (on) RestoreDefaults("Audio Guard was turned on");
    }

    // =====================================================================
    //  Making the headset the default
    // =====================================================================

    /// <summary>Sets the chosen speaker and mic as default for every role. Returns true if anything changed.</summary>
    public bool RestoreDefaults(string reason)
    {
        bool changed = false;
        changed |= MakeDefault(Settings.SpeakerId, DataFlow.Render);
        changed |= MakeDefault(Settings.MicId, DataFlow.Capture);
        if (changed)
        {
            Log(AudioEventKind.Restored, $"Put {Short(Settings.SpeakerName)} back as speaker and mic ({reason}).");
            DevicesChanged?.Invoke();
        }
        return changed;
    }

    private bool MakeDefault(string? id, DataFlow flow)
    {
        if (string.IsNullOrEmpty(id) || !IsActive(id)) return false;
        bool changed = false;
        foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            if (DefaultId(flow, role) == id) continue;
            if (PolicyConfig.SetDefault(id, role)) changed = true;
        }
        return changed;
    }

    public bool SetDeviceEnabled(string id, bool enabled)
    {
        var ok = PolicyConfig.SetVisibility(id, enabled);
        if (ok) Log(AudioEventKind.Info, $"{(enabled ? "Turned on" : "Turned off")} {NameOf(id)}.");
        else Log(AudioEventKind.Warning, $"Windows didn't allow turning {(enabled ? "on" : "off")} {NameOf(id)}. Try again after \"Restart as admin\".");
        DevicesChanged?.Invoke();
        return ok;
    }

    // =====================================================================
    //  Windows notifications (arrive on a background thread)
    // =====================================================================

    internal void OnDefaultChanged(DataFlow flow, Role role, string? newId) => Post(() =>
    {
        DevicesChanged?.Invoke();
        if (!Settings.GuardEnabled) return;
        var preferred = flow == DataFlow.Render ? Settings.SpeakerId : Settings.MicId;
        if (string.IsNullOrEmpty(preferred) || newId == preferred) return;

        var what = flow == DataFlow.Render ? "speaker" : "mic";
        var roleText = role == Role.Communications ? " for calls" : "";
        if (!IsActive(preferred))
        {
            Log(AudioEventKind.Warning, $"Windows moved your {what}{roleText} to {NameOf(newId)} because {Short(flow == DataFlow.Render ? Settings.SpeakerName : Settings.MicName)} isn't available. It'll be put back when the headset returns.");
            return;
        }
        SwitchesToday++;
        Log(AudioEventKind.Switched, $"Windows switched your {what}{roleText} to {NameOf(newId)}.");
        ScheduleRestore("Windows switched it");
    });

    internal void OnStateChanged(string id, DeviceState state) => Post(() =>
    {
        DevicesChanged?.Invoke();
        if (id != Settings.SpeakerId && id != Settings.MicId) return;
        var label = id == Settings.SpeakerId ? "Headset speaker" : "Headset mic";

        if (state != DeviceState.Active)
        {
            if (_dropStarted.ContainsKey(id)) return;
            _dropStarted[id] = DateTime.Now;
            DropsToday++;
            Log(AudioEventKind.Dropped, $"{label} disconnected ({StateWord(state)}).");
        }
        else
        {
            if (_dropStarted.Remove(id, out var since))
            {
                var gone = DateTime.Now - since;
                if (gone > LongestDropToday) LongestDropToday = gone;
                Log(AudioEventKind.Reconnected, $"{label} reconnected after {Duration(gone)}.");
            }
            if (Settings.GuardEnabled) ScheduleRestore("the headset reconnected", delayMs: 1200);
        }
    });

    internal void OnListChanged() => Post(() => DevicesChanged?.Invoke());

    private void ScheduleRestore(string reason, int delayMs = 400)
    {
        if (_restorePending) return;
        if (DateTime.Now < _pausedUntil) return;

        // Safety: if something keeps fighting us, back off instead of flip-flopping.
        var now = DateTime.Now;
        while (_recentRestores.Count > 0 && now - _recentRestores.Peek() > TimeSpan.FromSeconds(30)) _recentRestores.Dequeue();
        if (_recentRestores.Count >= 8)
        {
            _pausedUntil = now.AddMinutes(1);
            Log(AudioEventKind.Warning, "Something keeps switching your audio. Audio Guard is pausing for 1 minute. A program like Razer Synapse, Logitech G Hub or a phone link may be fighting it.");
            return;
        }
        _recentRestores.Enqueue(now);

        _restorePending = true;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _restorePending = false;
            RestoreDefaults(reason);
        };
        timer.Start();
    }

    // =====================================================================
    //  Log
    // =====================================================================

    private void Log(AudioEventKind kind, string message)
    {
        var e = new AudioEvent(DateTime.Now, kind, message);
        EventLogged?.Invoke(e);
        try
        {
            var file = Path.Combine(AppEnv.SubFolder("Logs"), "audio-events.csv");
            var header = !File.Exists(file);
            using var w = new StreamWriter(file, append: true);
            if (header) w.WriteLine("Time,Event,Details");
            w.WriteLine($"{e.Time:yyyy-MM-dd HH:mm:ss},{e.KindText},\"{message.Replace("\"", "'")}\"");
        }
        catch { }
    }

    public void LogInfo(string message) => Log(AudioEventKind.Info, message);

    private static void Post(Action a)
    {
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(a);
    }

    private static string Short(string? name) => string.IsNullOrWhiteSpace(name) ? "your headset" : name;
    private static string StateWord(DeviceState s) => s switch
    {
        DeviceState.Unplugged => "unplugged",
        DeviceState.NotPresent => "dongle or link lost",
        DeviceState.Disabled => "turned off",
        _ => s.ToString(),
    };
    public static string Duration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0.#} s" : t.TotalMinutes < 60 ? $"{t.TotalMinutes:0.#} min" : $"{t.TotalHours:0.#} h";

    // =====================================================================
    //  Levels, volume and mic test
    // =====================================================================

    public (float Volume, bool Muted)? GetVolume(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            using var d = _enumerator.GetDevice(id);
            if (d.State != DeviceState.Active) return null;
            var v = d.AudioEndpointVolume;
            return (v.MasterVolumeLevelScalar, v.Mute);
        }
        catch { return null; }
    }

    public bool FixVolume(string? id, float minimum)
    {
        if (string.IsNullOrEmpty(id)) return false;
        try
        {
            using var d = _enumerator.GetDevice(id);
            var v = d.AudioEndpointVolume;
            if (v.Mute) v.Mute = false;
            if (v.MasterVolumeLevelScalar < minimum) v.MasterVolumeLevelScalar = minimum;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Current output level (0-1) of a speaker, or of a mic while a test is running.</summary>
    public float GetPeak(string? id)
    {
        if (string.IsNullOrEmpty(id)) return 0;
        try
        {
            using var d = _enumerator.GetDevice(id);
            return d.State == DeviceState.Active ? d.AudioMeterInformation.MasterPeakValue : 0;
        }
        catch { return 0; }
    }

    private WasapiCapture? _micTest;
    private MMDevice? _micTestDevice;

    /// <summary>Opens the mic so its level meter works (Windows only measures a mic while something is listening).</summary>
    public bool StartMicTest()
    {
        StopMicTest();
        if (string.IsNullOrEmpty(Settings.MicId)) return false;
        try
        {
            _micTestDevice = _enumerator.GetDevice(Settings.MicId);
            _micTest = new WasapiCapture(_micTestDevice);
            _micTest.DataAvailable += (_, _) => { };
            _micTest.StartRecording();
            return true;
        }
        catch
        {
            StopMicTest();
            return false;
        }
    }

    public void StopMicTest()
    {
        try { _micTest?.StopRecording(); } catch { }
        try { _micTest?.Dispose(); } catch { }
        try { _micTestDevice?.Dispose(); } catch { }
        _micTest = null;
        _micTestDevice = null;
    }

    // =====================================================================
    //  Windows settings that cause cut-outs
    // =====================================================================

    private const string DuckingKey = @"Software\Microsoft\Multimedia\Audio";

    /// <summary>True when Windows is set to "Do nothing" during calls.</summary>
    public static bool DuckingOff()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(DuckingKey);
            return k?.GetValue("UserDuckingPreference") is int v && v == 3;
        }
        catch { return false; }
    }

    public static bool SetDuckingOff(bool off)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(DuckingKey);
            k.SetValue("UserDuckingPreference", off ? 3 : 1, RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }

    private const string UsbSubgroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

    /// <summary>True when USB selective suspend (power-saving that can cut a dongle) is off in the active power plan.</summary>
    public static bool? UsbSuspendOff()
    {
        var output = RunHidden("powercfg", $"/query SCHEME_CURRENT {UsbSubgroup} {UsbSelectiveSuspend}");
        if (output is null) return null;
        var line = output.Split('\n').FirstOrDefault(l => l.Contains("Current AC Power Setting Index", StringComparison.OrdinalIgnoreCase));
        if (line is null) return null;
        return line.TrimEnd().EndsWith("0x00000000", StringComparison.OrdinalIgnoreCase);
    }

    public static bool SetUsbSuspendOff(bool off)
    {
        var v = off ? "0" : "1";
        var a = RunHidden("powercfg", $"/setacvalueindex SCHEME_CURRENT {UsbSubgroup} {UsbSelectiveSuspend} {v}");
        var b = RunHidden("powercfg", $"/setdcvalueindex SCHEME_CURRENT {UsbSubgroup} {UsbSelectiveSuspend} {v}");
        var c = RunHidden("powercfg", "/setactive SCHEME_CURRENT");
        return a is not null && b is not null && c is not null && UsbSuspendOff() == off;
    }

    /// <summary>Count of USB hubs/controllers Windows is allowed to switch off to save power. Needs admin; null if unknown.</summary>
    public static int? UsbPortsAllowedToSleep()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName, Enable FROM MSPower_DeviceEnable");
            int n = 0;
            foreach (ManagementObject o in s.Get())
                using (o)
                    if ((o["InstanceName"] as string ?? "").StartsWith("USB", StringComparison.OrdinalIgnoreCase) && o["Enable"] is bool b && b) n++;
            return n;
        }
        catch { return null; }
    }

    /// <summary>Unticks "Allow the computer to turn off this device to save power" on every USB device. Needs admin.</summary>
    public static int SetUsbPortsNeverSleep()
    {
        int changed = 0;
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM MSPower_DeviceEnable");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    if (!(o["InstanceName"] as string ?? "").StartsWith("USB", StringComparison.OrdinalIgnoreCase)) continue;
                    if (o["Enable"] is bool b && b)
                    {
                        o["Enable"] = false;
                        o.Put();
                        changed++;
                    }
                }
        }
        catch { }
        return changed;
    }

    /// <summary>Webcams Windows sees, with whether they're working.</summary>
    public static List<(string Name, bool Ok)> GetCameras()
    {
        var list = new List<(string, bool)>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, Status, PNPClass FROM Win32_PnPEntity WHERE PNPClass = 'Camera' OR PNPClass = 'Image'");
            foreach (ManagementObject o in s.Get())
                using (o)
                    list.Add((o["Name"] as string ?? "Camera", string.Equals(o["Status"] as string, "OK", StringComparison.OrdinalIgnoreCase)));
        }
        catch { }
        return list;
    }

    // ---- Start with Windows ----

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "Moore PC Mechanic";

    public static bool StartsWithWindows
    {
        get
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(RunName) is string; }
            catch { return false; }
        }
        set
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) k.SetValue(RunName, $"\"{UpdateService.CurrentExePath}\" --minimized");
                else k.DeleteValue(RunName, false);
            }
            catch { }
        }
    }

    private static string? RunHidden(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        StopMicTest();
        try { _enumerator.UnregisterEndpointNotificationCallback(_client); } catch { }
        try { _enumerator.Dispose(); } catch { }
    }
}

/// <summary>Receives Windows' audio device notifications and hands them to the AudioService.</summary>
[ComVisible(true)]
public sealed class AudioNotificationClient : IMMNotificationClient
{
    private readonly AudioService _owner;
    public AudioNotificationClient(AudioService owner) => _owner = owner;

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _owner.OnStateChanged(deviceId, newState);
    public void OnDeviceAdded(string pwstrDeviceId) => _owner.OnListChanged();
    public void OnDeviceRemoved(string deviceId) => _owner.OnListChanged();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _owner.OnDefaultChanged(flow, role, defaultDeviceId);
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
}

/// <summary>
/// Windows' own (undocumented but long-stable) interface for changing the default
/// audio device - the same one the Sound control panel uses.
/// </summary>
internal static class PolicyConfig
{
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class CPolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, IntPtr defaultPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }

    public static bool SetDefault(string id, Role role) => Call(p => p.SetDefaultEndpoint(id, (int)role));

    public static bool SetVisibility(string id, bool visible) => Call(p => p.SetEndpointVisibility(id, visible ? 1 : 0));

    private static bool Call(Func<IPolicyConfig, int> action)
    {
        object? obj = null;
        try
        {
            obj = new CPolicyConfigClient();
            return action((IPolicyConfig)obj) >= 0;
        }
        catch { return false; }
        finally
        {
            if (obj is not null) Marshal.FinalReleaseComObject(obj);
        }
    }
}
