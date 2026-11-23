using System.Windows;
using System.Windows.Controls;
using MoorePCMechanic.Services;

namespace MoorePCMechanic;

/// <summary>Security tab: sweep, one-click fixes, and the security pause.</summary>
public partial class MainWindow
{
    private bool _secBusy;

    private void SetSecBusy(bool busy, string? status = null)
    {
        _secBusy = busy;
        BtnSecScan.IsEnabled = BtnSecPause.IsEnabled = BtnSecResume.IsEnabled = !busy;
        SecProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (status != null) SecStatus.Text = status;
    }

    private async void BtnSecScan_Click(object sender, RoutedEventArgs e) => await SecScanAsync();

    private async Task SecScanAsync()
    {
        if (_secBusy) return;
        SetSecBusy(true, "Checking… (about 10-20 seconds)");
        try
        {
            var items = await Task.Run(SecurityService.Sweep);
            SecList.ItemsSource = items;
            int warn = items.Count(i => i.Level == FindingLevel.Warning), tip = items.Count(i => i.Level == FindingLevel.Tip);
            SetSecBusy(false, warn == 0 && tip == 0 ? "All clear." :
                $"{warn} to fix, {tip} tip(s)." + (AppEnv.IsAdmin() ? "" : " Some details and fixes need admin (the app will offer to restart as admin)."));
        }
        catch (Exception ex)
        {
            SetSecBusy(false, "Sweep failed: " + ex.Message);
        }
        ShowPauseState();
    }

    private async void SecFix_Click(object sender, RoutedEventArgs e)
    {
        if (_secBusy || sender is not Button { Tag: string id }) return;
        var msg = await RunSecurityFixAsync(id);
        if (msg == null) return;
        if (!NoRescanFixes.Contains(id)) await SecScanAsync();
        SecStatus.Text = msg;
    }

    /// <summary>Fixes that only open a Windows page or start something long; a rescan right after shows nothing new.</summary>
    private static readonly HashSet<string> NoRescanFixes = new() { "coreisolation", "defender", "firewall", "fullscan", "windowsupdate", "apps" };

    /// <summary>Runs one security fix (asking for admin and confirmation where needed). Returns the result, or null if it didn't run.
    /// Shared by the Security tab and the System check tab.</summary>
    private async Task<string?> RunSecurityFixAsync(string id)
    {
        bool needsAdmin = id is not ("coreisolation" or "defender" or "firewall" or "windowsupdate" or "apps");
        if (needsAdmin && !EnsureAdminFor("change security settings")) return null;
        if (id.StartsWith("stopdriver:"))
        {
            var ok = MessageBox.Show(this, $"Stop and remove the driver service \"{id["stopdriver:".Length..]}\"?\n\nThe device or program that uses it stops working until it's reinstalled. A restore point is made first.",
                                     "Remove driver", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.OK) return null;
            SetSecBusy(true, "Making a restore point…");
            await Task.Run(() => DriverService.CreateRestorePoint("Moore PC Mechanic: before removing a driver"));
        }
        if (id == "smb1-off" && MessageBox.Show(this,
                "Turn off SMB1 file sharing?\n\nOnly very old network drives (NAS) and scanners still need it. If one stops working afterwards, it can be turned back on in \"Turn Windows features on or off\".",
                "Turn off SMB1", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return null;
        SetSecBusy(true, "Working…");
        string msg;
        try { msg = await Task.Run(() => SecurityService.Fix(id)); }
        catch (Exception ex) { msg = "Couldn't do that: " + ex.Message; }
        SetSecBusy(false, msg);
        return msg;
    }

    private async void BtnSecPause_Click(object sender, RoutedEventArgs e)
    {
        if (SecurityService.SmartAppControlBlocksPause())
        {
            MessageBox.Show(this, "Smart App Control is on (or evaluating) on this PC. While it is, Windows enforces the driver blocklist no matter what, so a pause can't help.\n\n" +
                "Turning Smart App Control off can't be undone without resetting Windows, so the safer route is to run the tool on another PC.",
                "Security pause", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!EnsureAdminFor("pause security protections")) return;
        var hours = SecPauseLen.SelectedIndex switch { 0 => 1, 2 => 4, _ => 2 };
        var ok = MessageBox.Show(this,
            $"Turn OFF Memory integrity and the vulnerable driver blocklist for {hours} hour(s)?\n\n" +
            "While paused, any program could load a driver that attackers abuse. Only run the tool you need, and don't browse or install anything else.\n\n" +
            "1. A restore point is made.\n2. Restart the PC.\n3. Run your tool.\n4. Click \"Resume protections now\" (or it resumes by itself) and restart again.",
            "Security pause", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;
        SetSecBusy(true, "Making a restore point and setting up the pause…");
        string msg;
        try { msg = await Task.Run(() => SecurityService.Pause(TimeSpan.FromHours(hours), SecPauseReason.Text)); }
        catch (Exception ex) { msg = "Pause not started: " + ex.Message; }
        SetSecBusy(false, msg);
        ShowPauseState();
        if (SecurityService.GetPause() != null) AskRestart("The pause takes effect after a restart.");
    }

    private async void BtnSecResume_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdminFor("turn security protections back on")) return;
        SetSecBusy(true, "Turning protections back on…");
        string msg;
        try { msg = await Task.Run(() => SecurityService.Resume(false)); }
        catch (Exception ex) { msg = "Couldn't resume: " + ex.Message; }
        SetSecBusy(false, msg);
        ShowPauseState();
        AskRestart("Memory integrity and the blocklist come back on after a restart.");
    }

    /// <summary>Shows the red pause banner on the Security tab and in the status bar while a pause is running.</summary>
    private void ShowPauseState()
    {
        var p = SecurityService.GetPause();
        bool on = p != null;
        SecPauseState.Visibility = BtnSecResume.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        BtnSecPause.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (p != null)
        {
            SecPauseState.Text = $"PAUSED since {p.Started:HHmm} for {p.Reason}. Protections come back on by themselves after {p.Expires:MMM d HHmm}.";
            StatusText.Text = "Security pause is ON - Memory integrity and the driver blocklist are off. See the Security tab.";
        }
    }

    private void AskRestart(string why)
    {
        var r = MessageBox.Show(this, why + "\n\nRestart now? Save your work in other programs first.", "Restart needed", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes)
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/r /t 5") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
    }
}
