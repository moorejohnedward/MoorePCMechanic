using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MoorePCMechanic.Services;
using MoorePCMechanic.ViewModels;

namespace MoorePCMechanic;

// "Audio & calls" tab: Audio Guard, device list, call-ready check and fixes.
public partial class MainWindow
{
    private AudioService? _audio;
    private readonly ObservableCollection<AudioDeviceRow> _audioRows = new();
    private readonly ObservableCollection<AudioEvent> _audioEvents = new();
    private readonly ObservableCollection<Finding> _callChecks = new();
    private readonly DispatcherTimer _levelTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _loadingPicks;
    private bool _micTestRunning;
    private float _micTestPeak;

    private void InitAudio()
    {
        AudioGrid.ItemsSource = _audioRows;
        AudioEvents.ItemsSource = _audioEvents;
        CallChecks.ItemsSource = _callChecks;

        try
        {
            _audio = new AudioService();
        }
        catch (Exception ex)
        {
            GuardStatus.Text = "Audio features couldn't start: " + ex.Message;
            return;
        }

        _audio.EventLogged += e =>
        {
            _audioEvents.Insert(0, e);
            while (_audioEvents.Count > 300) _audioEvents.RemoveAt(_audioEvents.Count - 1);
            UpdateAudioSummary();
        };
        _audio.DevicesChanged += RefreshAudioDevices;

        GuardCheck.IsChecked = _audio.Settings.GuardEnabled;
        StartupCheck.IsChecked = AudioService.StartsWithWindows;
        RefreshAudioDevices();
        RefreshFixStatus();

        _audio.LogInfo($"Watching audio. Headset: {_audio.Settings.SpeakerName ?? "not picked yet"}.");
        if (_audio.Settings.GuardEnabled) _audio.RestoreDefaults("app started");

        _levelTimer.Tick += (_, _) => UpdateLevels();
        _levelTimer.Start();
    }

    private void DisposeAudio()
    {
        _levelTimer.Stop();
        _audio?.Dispose();
        _audio = null;
    }

    // ---------------------------------------------------------------------
    //  Device list and pickers
    // ---------------------------------------------------------------------

    private void RefreshAudioDevices()
    {
        if (_audio is null) return;
        var devices = _audio.GetDevices();
        var s = _audio.Settings;

        _audioRows.Clear();
        foreach (var d in devices)
            _audioRows.Add(new AudioDeviceRow(d, d.Id == (d.IsMic ? s.MicId : s.SpeakerId)));

        _loadingPicks = true;
        var speakers = devices.Where(d => !d.IsMic && d.IsActive).ToList();
        var mics = devices.Where(d => d.IsMic && d.IsActive).ToList();
        SpeakerPick.ItemsSource = speakers;
        MicPick.ItemsSource = mics;
        SpeakerPick.SelectedItem = speakers.FirstOrDefault(d => d.Id == s.SpeakerId);
        MicPick.SelectedItem = mics.FirstOrDefault(d => d.Id == s.MicId);
        _loadingPicks = false;

        UpdateGuardStatus();
    }

    private void UpdateGuardStatus()
    {
        if (_audio is null) return;
        var s = _audio.Settings;
        bool spkOk = _audio.IsActive(s.SpeakerId), micOk = _audio.IsActive(s.MicId);
        if (!s.GuardEnabled)
        {
            GuardStatus.Text = "Off";
            GuardStatus.Foreground = (Brush)FindResource("MutedBrush");
        }
        else if (!spkOk || !micOk)
        {
            GuardStatus.Text = "● Waiting for headset";
            GuardStatus.Foreground = (Brush)FindResource("WarnBrush");
        }
        else
        {
            GuardStatus.Text = "● On guard";
            GuardStatus.Foreground = (Brush)FindResource("GoodBrush");
        }
    }

    private void UpdateAudioSummary()
    {
        if (_audio is null) return;
        var drops = _audio.DropsToday == 0
            ? "no headset dropouts"
            : $"{_audio.DropsToday} headset dropout{(_audio.DropsToday == 1 ? "" : "s")} (longest {AudioService.Duration(_audio.LongestDropToday)})";
        var switches = _audio.SwitchesToday == 0 ? "no device switches" : $"{_audio.SwitchesToday} switch{(_audio.SwitchesToday == 1 ? "" : "es")} undone";
        AudioSummary.Text = $"Since the app opened: {drops}, {switches}.";
        UpdateGuardStatus();
    }

    private void DevicePick_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPicks || _audio is null) return;
        _audio.SetPreferred(SpeakerPick.SelectedItem as AudioDeviceInfo, MicPick.SelectedItem as AudioDeviceInfo);
        RefreshAudioDevices();
    }

    private void GuardCheck_Click(object sender, RoutedEventArgs e)
    {
        _audio?.SetGuard(GuardCheck.IsChecked == true);
        UpdateGuardStatus();
    }

    private void StartupCheck_Click(object sender, RoutedEventArgs e) =>
        AudioService.StartsWithWindows = StartupCheck.IsChecked == true;

    private void BtnToggleDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_audio is null || (sender as FrameworkElement)?.Tag is not AudioDeviceRow row) return;
        _audio.SetDeviceEnabled(row.Info.Id, !row.Info.IsActive);
    }

    private void UpdateLevels()
    {
        if (_audio is null) return;
        foreach (var r in _audioRows)
            r.Level = r.Info.IsActive ? _audio.GetPeak(r.Info.Id) : 0;

        var mic = _micTestRunning ? _audio.GetPeak(_audio.Settings.MicId) : 0f;
        if (mic > _micTestPeak) _micTestPeak = mic;
        MicMeter.Value = mic;
    }

    // ---------------------------------------------------------------------
    //  Call-ready check
    // ---------------------------------------------------------------------

    private async void BtnCallCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_audio is null) return;
        BtnCallCheck.IsEnabled = BtnCallFix.IsEnabled = false;
        _callChecks.Clear();

        // Listen to the mic for 5 seconds
        _micTestPeak = 0;
        _micTestRunning = _audio.StartMicTest();
        for (int i = 5; i > 0 && _micTestRunning; i--)
        {
            MicTestLabel.Text = $"Listening… say a few words ({i})";
            await Task.Delay(1000);
        }
        _audio.StopMicTest();
        _micTestRunning = false;
        MicTestLabel.Text = "Mic level";
        MicMeter.Value = 0;

        var cameras = await Task.Run(() => AudioService.GetCameras());
        bool anyFixable = false;
        void Add(FindingLevel level, string title, string detail, bool fixable = false)
        {
            _callChecks.Add(new Finding(level, title, detail));
            if (fixable && level != FindingLevel.Good) anyFixable = true;
        }

        var s = _audio.Settings;
        bool spkOn = _audio.IsActive(s.SpeakerId), micOn = _audio.IsActive(s.MicId);

        // 1. Headset connected
        if (spkOn && micOn) Add(FindingLevel.Good, "Headset connected", $"{s.SpeakerName}");
        else Add(FindingLevel.Warning, "Headset not connected",
            "Turn the headset on and check the dongle is plugged in. If it's on, unplug the dongle for 5 seconds and plug it back in.");

        // 2. Defaults
        if (spkOn && micOn)
        {
            bool spkDef = new[] { NAudio.CoreAudioApi.Role.Multimedia, NAudio.CoreAudioApi.Role.Communications }
                .All(r => _audio.DefaultId(NAudio.CoreAudioApi.DataFlow.Render, r) == s.SpeakerId);
            bool micDef = new[] { NAudio.CoreAudioApi.Role.Multimedia, NAudio.CoreAudioApi.Role.Communications }
                .All(r => _audio.DefaultId(NAudio.CoreAudioApi.DataFlow.Capture, r) == s.MicId);
            if (spkDef && micDef) Add(FindingLevel.Good, "Headset is the default for calls", "Speaker and mic are set for normal sound and for calls.");
            else Add(FindingLevel.Warning, "Headset isn't the default everywhere",
                "Windows has another device set for " + (!spkDef ? "the speaker" : "the mic") + ". Fix everything sets it back.", fixable: true);
        }

        // 3. Volume / mute
        var mv = _audio.GetVolume(s.MicId);
        if (mv is { } m)
        {
            if (m.Muted) Add(FindingLevel.Warning, "Mic is muted in Windows", "Fix everything unmutes it. (The mute button on the headset itself is separate.)", fixable: true);
            else if (m.Volume < 0.6f) Add(FindingLevel.Tip, $"Mic volume is low ({m.Volume:P0})", "People may have trouble hearing you. Fix everything raises it to 80%.", fixable: true);
            else Add(FindingLevel.Good, $"Mic volume {m.Volume:P0}", "Not muted.");
        }
        var sv = _audio.GetVolume(s.SpeakerId);
        if (sv is { } v && (v.Muted || v.Volume < 0.15f))
            Add(FindingLevel.Warning, "Headset sound is muted or very low", "Fix everything turns it up to 50%.", fixable: true);

        // 4. Mic actually hears you
        if (micOn)
        {
            if (_micTestPeak > 0.05f) Add(FindingLevel.Good, "Mic hears you", $"Peak level {_micTestPeak:P0} while you talked.");
            else if (_micTestPeak > 0.005f) Add(FindingLevel.Tip, "Mic is very quiet", "Check the mic boom is flipped down and close to your mouth, and the headset's mic mute is off.");
            else Add(FindingLevel.Warning, "Mic didn't pick up any sound",
                "Check the headset's mic mute button/boom. In Windows Settings > Privacy > Microphone, make sure desktop apps can use the mic.");
        }

        // 5. Camera
        if (cameras.Count == 0) Add(FindingLevel.Warning, "No camera found", "Check the webcam's USB cable.");
        else if (cameras.Any(c => c.Ok)) Add(FindingLevel.Good, "Camera ready", string.Join(", ", cameras.Where(c => c.Ok).Select(c => c.Name)));
        else Add(FindingLevel.Warning, "Camera has a problem", "Windows sees the camera but reports an error. Unplug it and plug it back in.");

        // 6. Ducking and guard
        if (!AudioService.DuckingOff()) Add(FindingLevel.Tip, "Windows lowers sound during calls", "Fix everything turns this off.", fixable: true);
        if (!s.GuardEnabled) Add(FindingLevel.Tip, "Audio Guard is off", "Fix everything turns it on so Windows can't switch devices mid-call.", fixable: true);

        Add(FindingLevel.Tip, "In Zoom", "Settings > Audio: pick your headset by name for Speaker and Microphone (not \"Same as System\"). Do the same for the camera under Video.");

        BtnCallCheck.IsEnabled = true;
        BtnCallFix.IsEnabled = anyFixable;
        _audio.LogInfo(anyFixable ? "Call-ready check found things to fix." : "Call-ready check passed.");
    }

    private void BtnCallFix_Click(object sender, RoutedEventArgs e)
    {
        if (_audio is null) return;
        var s = _audio.Settings;
        if (!s.GuardEnabled) { _audio.SetGuard(true); GuardCheck.IsChecked = true; }
        _audio.RestoreDefaults("call-ready fix");
        _audio.FixVolume(s.MicId, 0.8f);
        _audio.FixVolume(s.SpeakerId, 0.5f);
        if (!AudioService.DuckingOff()) AudioService.SetDuckingOff(true);
        RefreshFixStatus();
        BtnCallCheck_Click(sender, e);
    }

    // ---------------------------------------------------------------------
    //  Fixes
    // ---------------------------------------------------------------------

    private async void RefreshFixStatus()
    {
        var ducking = AudioService.DuckingOff();
        BtnDucking.Content = ducking ? "Undo" : "Stop it";
        SetStatus(DuckingStatus, ducking, "Fixed: Windows leaves your volume alone during calls.", "Windows lowers other sound during calls.");

        var (suspendOff, portsAwake) = await Task.Run(() => (AudioService.UsbSuspendOff(), AudioService.UsbPortsAllowedToSleep()));
        BtnUsbSuspend.Content = suspendOff == true ? "Undo" : "Keep USB powered";
        if (suspendOff is null) { UsbSuspendStatus.Text = "Couldn't read the power plan."; UsbSuspendStatus.Foreground = (Brush)FindResource("MutedBrush"); }
        else SetStatus(UsbSuspendStatus, suspendOff.Value, "Fixed: USB power saving is off.", "USB power saving is on.");

        if (portsAwake is null)
        {
            UsbPortsStatus.Text = AppEnv.IsAdmin() ? "Couldn't read USB port settings." : "Needs \"Restart as admin\" to check.";
            UsbPortsStatus.Foreground = (Brush)FindResource("MutedBrush");
        }
        else SetStatus(UsbPortsStatus, portsAwake == 0, "Fixed: Windows can't switch off USB ports.", $"{portsAwake} USB ports can be switched off to save power.");
    }

    private void SetStatus(TextBlock t, bool good, string goodText, string badText)
    {
        t.Text = good ? goodText : badText;
        t.Foreground = (Brush)FindResource(good ? "GoodBrush" : "WarnBrush");
    }

    private void BtnDucking_Click(object sender, RoutedEventArgs e)
    {
        var turnOff = !AudioService.DuckingOff();
        AudioService.SetDuckingOff(turnOff);
        _audio?.LogInfo(turnOff ? "Stopped Windows lowering sound during calls." : "Restored Windows' call volume ducking.");
        RefreshFixStatus();
    }

    private async void BtnUsbSuspend_Click(object sender, RoutedEventArgs e)
    {
        BtnUsbSuspend.IsEnabled = false;
        var turnOff = AudioService.UsbSuspendOff() != true;
        var ok = await Task.Run(() => AudioService.SetUsbSuspendOff(turnOff));
        _audio?.LogInfo(ok
            ? (turnOff ? "Turned off USB power saving (selective suspend)." : "Turned USB power saving back on.")
            : "Windows didn't accept the USB power change. Try again after \"Restart as admin\".");
        BtnUsbSuspend.IsEnabled = true;
        RefreshFixStatus();
    }

    private async void BtnUsbPorts_Click(object sender, RoutedEventArgs e)
    {
        if (!AppEnv.IsAdmin())
        {
            UsbPortsStatus.Text = "Click \"Restart as admin\" at the top, then press this again.";
            UsbPortsStatus.Foreground = (Brush)FindResource("TipBrush");
            return;
        }
        BtnUsbPorts.IsEnabled = false;
        var n = await Task.Run(() => AudioService.SetUsbPortsNeverSleep());
        _audio?.LogInfo(n > 0 ? $"Stopped Windows switching off {n} USB ports to save power." : "USB ports were already set to stay awake.");
        BtnUsbPorts.IsEnabled = true;
        RefreshFixStatus();
    }

    private void BtnSoundPanel_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl") { UseShellExecute = true }); } catch { }
    }
}
