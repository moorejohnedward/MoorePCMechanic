using System.Windows;
using System.Windows.Controls;
using LibreHardwareMonitor.Hardware;
using MoorePCMechanic.Services;

namespace MoorePCMechanic;

/// <summary>Tune-up tab: scan, one-click "Tune & optimize", startup apps, deep repair and undo.</summary>
public partial class MainWindow
{
    private List<TuneItem> _tuneItems = new();
    private List<StartupApp> _startupApps = new();
    private bool _tuneBusy;

    private void SetTuneBusy(bool busy, string? status = null)
    {
        _tuneBusy = busy;
        BtnTuneScan.IsEnabled = BtnTuneRepair.IsEnabled = BtnTuneUndo.IsEnabled = BtnTuneUp.IsEnabled = !busy;
        BtnTuneApply.IsEnabled = !busy && _tuneItems.Count > 0;
        TuneProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (status != null) TuneStatus.Text = status;
    }

    /// <summary>Header button: jump to the Tune-up tab and scan.</summary>
    private async void BtnTuneUp_Click(object sender, RoutedEventArgs e)
    {
        Tabs.SelectedItem = TuneTab;
        await TuneScanAsync();
    }

    private async void BtnTuneScan_Click(object sender, RoutedEventArgs e) => await TuneScanAsync();

    private async Task TuneScanAsync()
    {
        if (_tuneBusy) return;
        SetTuneBusy(true, "Checking power settings, junk files, SSDs and startup apps…");
        try
        {
            // hottest drive from the live sensors (°C; null if unknown)
            var hot = _lastReadings.Where(r => r.RootType == HardwareType.Storage && r.Type == SensorType.Temperature && r.Value is float f && f > 0)
                                   .OrderByDescending(r => r.Value).FirstOrDefault();
            var (items, startup) = await Task.Run(() => (TuneUpService.Scan(hot?.Value, hot?.Hardware), TuneUpService.StartupApps()));
            _tuneItems = items.OrderByDescending(i => i.NeedsFix).ThenBy(i => i.CanFix ? 0 : 1).ToList();
            _startupApps = startup;
            TuneList.ItemsSource = _tuneItems;
            StartupGrid.ItemsSource = _startupApps;
            int fixable = _tuneItems.Count(i => i.NeedsFix && i.CanFix);
            int advice = _tuneItems.Count(i => i.NeedsFix && !i.CanFix);
            SetTuneBusy(false, (fixable == 0 ? "Nothing to fix automatically." : $"{fixable} thing(s) can be improved; they're ticked.") +
                               (advice > 0 ? $" {advice} piece(s) of advice (blue) need you to act." : "") +
                               " Untick anything you'd rather leave, then click \"Tune & optimize\".");
        }
        catch (Exception ex)
        {
            SetTuneBusy(false, "Scan failed: " + ex.Message);
        }
    }

    private async void BtnTuneApply_Click(object sender, RoutedEventArgs e)
    {
        StartupGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var chosen = _tuneItems.Where(i => i.Selected && i.CanFix).ToList();
        int startupChanges = _startupApps.Count(s => s.Enabled != s.WasEnabled);
        if (chosen.Count == 0 && startupChanges == 0) { TuneStatus.Text = "Nothing is ticked."; return; }
        if (!EnsureAdminFor("change power, graphics and startup settings")) return;

        var lines = chosen.Select(i => "• " + i.Title).ToList();
        if (startupChanges > 0) lines.Add($"• {startupChanges} startup app change(s)");
        var ok = MessageBox.Show(this,
            "Apply these changes?\n\n" + string.Join("\n", lines) +
            "\n\nA System Restore point is made first, and \"Undo last tune-up\" can put the settings back.",
            "Tune & optimize", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;

        SetTuneBusy(true, "Making a restore point…");
        try
        {
            var (rpOk, rpMsg) = await Task.Run(() => DriverService.CreateRestorePoint("Moore PC Mechanic: before tune-up"));
            if (!rpOk && MessageBox.Show(this, rpMsg + "\n\nTune up anyway?", "No restore point", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                SetTuneBusy(false, "Tune-up cancelled.");
                return;
            }
            var progress = new Progress<string>(s => TuneStatus.Text = s);
            bool restart = await Task.Run(() => TuneUpService.Apply(chosen, _startupApps, progress));
            SetTuneBusy(false, "Tune-up done." + (restart ? " Restart the PC to finish (graphics scheduling)." : "") +
                               " Run a benchmark to see the difference.");
            if (restart && MessageBox.Show(this, "Restart now to finish the tune-up?\n\nSave your work in other programs first.",
                                           "Restart", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/r /t 5") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
            }
        }
        catch (Exception ex)
        {
            SetTuneBusy(false, "Tune-up stopped: " + ex.Message);
        }
    }

    private async void BtnTuneUndo_Click(object sender, RoutedEventArgs e)
    {
        var u = TuneUpService.LastUndo();
        if (u == null) { TuneStatus.Text = "There's no tune-up to undo."; return; }
        if (!EnsureAdminFor("put settings back")) return;
        if (MessageBox.Show(this, $"Put back the settings changed by the tune-up on {u.When:MMM d} at {u.When:HHmm}?\n\n(Deleted temp files and the emptied Recycle Bin can't come back.)",
                            "Undo tune-up", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        SetTuneBusy(true, "Putting settings back…");
        var msg = await Task.Run(TuneUpService.Undo);
        SetTuneBusy(false, msg);
        await TuneScanAsync();
    }

    private async void BtnTuneRepair_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminFor("repair Windows system files")) return;
        if (MessageBox.Show(this,
                "Deep repair runs Windows' own repair tools:\n\n• DISM checks Windows' component store against Microsoft's copy and fixes it.\n• System File Checker (SFC) then repairs damaged Windows files.\n\n" +
                "It takes 20-30 minutes and needs internet. You can keep using the PC, but it may feel slower while it runs.",
                "Deep repair", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        SetTuneBusy(true, "Starting deep repair…");
        var progress = new Progress<string>(s => TuneStatus.Text = s);
        try
        {
            var verdict = await Task.Run(() => TuneUpService.DeepRepair(progress));
            SetTuneBusy(false, "Deep repair finished: " + verdict);
        }
        catch (Exception ex)
        {
            SetTuneBusy(false, "Deep repair stopped: " + ex.Message);
        }
    }
}
