using System.Windows;
using System.Windows.Controls;
using MoorePCMechanic.Services;

namespace MoorePCMechanic;

/// <summary>Drivers tab: installed drivers, problem devices, Windows Update driver updates and the NVIDIA driver.</summary>
public partial class MainWindow
{
    private List<DriverInfo> _drivers = new();
    private List<DriverUpdate> _wuUpdates = new();
    private NvidiaStatus? _nvidia;
    private bool _drvBusy;

    private void SetDrvBusy(bool busy, string? status = null)
    {
        _drvBusy = busy;
        BtnDrvScan.IsEnabled = !busy;
        BtnDrvInstall.IsEnabled = !busy && _wuUpdates.Any(u => u.Selected && string.IsNullOrEmpty(u.Result));
        BtnNvInstall.IsEnabled = !busy;
        DrvProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (status != null) DrvStatus.Text = status;
    }

    private async void BtnDrvScan_Click(object sender, RoutedEventArgs e)
    {
        if (_drvBusy) return;
        SetDrvBusy(true, "Listing installed drivers…");
        try
        {
            // 1) what's installed and what's broken (a few seconds)
            var (drivers, problems) = await Task.Run(() => (DriverService.ScanInstalled(), DriverService.ScanProblems()));
            _drivers = drivers;
            ApplyDriverFilter();
            DrvProblems.ItemsSource = problems.Select(p => new Finding(
                p.Code == 22 ? FindingLevel.Tip : FindingLevel.Warning, p.Name, p.Meaning)).ToList();

            // 2) NVIDIA and Windows Update at the same time
            SetDrvBusy(true, "Asking NVIDIA and Windows Update for newer drivers… (Windows Update can take a minute or two)");
            var nvTask = DriverService.CheckNvidiaAsync();
            var wuTask = Task.Run(() =>
            {
                try { return (DriverService.SearchWindowsUpdate(), (string?)null); }
                catch (Exception ex) { return (new List<DriverUpdate>(), (string?)ex.Message); }
            });

            _nvidia = await nvTask;
            ShowNvidia();

            var (updates, wuError) = await wuTask;
            _wuUpdates = updates;
            WuGrid.ItemsSource = _wuUpdates;
            WuPanel.Visibility = _wuUpdates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            var parts = new List<string>();
            parts.Add(problems.Count == 0 ? "No problem devices." : $"{problems.Count} device(s) need attention (listed above).");
            if (wuError != null) parts.Add("Windows Update didn't answer: " + wuError);
            else parts.Add(_wuUpdates.Count == 0 ? "Windows Update has no driver updates for this PC." : $"Windows Update has {_wuUpdates.Count} driver update(s).");
            if (_nvidia?.UpdateAvailable == true) parts.Add($"NVIDIA {_nvidia.LatestVersion} is available.");
            SetDrvBusy(false, string.Join("  ", parts));
            BtnDrvInstallText.Text = _wuUpdates.Count > 0 ? $"Download & install ({_wuUpdates.Count(u => u.Selected)})" : "Download & install";
        }
        catch (Exception ex)
        {
            SetDrvBusy(false, "Driver scan failed: " + ex.Message);
        }
    }

    private void ApplyDriverFilter()
    {
        if (DrvGrid == null) return;
        IEnumerable<DriverInfo> q = _drivers;
        if (DrvHideMs.IsChecked == true) q = q.Where(d => !d.IsMicrosoft);
        var text = DrvSearch.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
            q = q.Where(d => d.Device.Contains(text, StringComparison.OrdinalIgnoreCase)
                          || d.ClassName.Contains(text, StringComparison.OrdinalIgnoreCase)
                          || d.Provider.Contains(text, StringComparison.OrdinalIgnoreCase));
        var list = q.ToList();
        DrvGrid.ItemsSource = list;
        DrvCount.Text = _drivers.Count == 0 ? "" : $"{list.Count} shown of {_drivers.Count}";
    }

    private void DrvFilter_Changed(object sender, RoutedEventArgs e) => ApplyDriverFilter();

    // ---------------------------------------------------------------------
    //  Windows Update install
    // ---------------------------------------------------------------------

    private async void BtnDrvInstall_Click(object sender, RoutedEventArgs e)
    {
        WuGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var chosen = _wuUpdates.Where(u => u.Selected && string.IsNullOrEmpty(u.Result)).ToList();
        if (chosen.Count == 0) { DrvStatus.Text = "Tick at least one update."; return; }
        if (!EnsureAdminFor("install drivers")) return;

        var ask = MessageBox.Show(this,
            $"Install {chosen.Count} driver update(s) from Windows Update?\n\n" +
            "A System Restore point is made first, so you can undo this from \"Create a restore point\" > System Restore if anything goes wrong.\n\n" +
            "Devices may flicker or disconnect for a moment while their drivers change.",
            "Install driver updates", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK) return;

        SetDrvBusy(true, "Making a restore point…");
        if (!await MakeRestorePointAsync("Moore PC Mechanic: before driver updates")) { SetDrvBusy(false); return; }

        var progress = new Progress<string>(s => DrvStatus.Text = s);
        bool reboot = await Task.Run(() => DriverService.InstallWindowsUpdates(chosen, progress));
        WuGrid.Items.Refresh();
        int ok = chosen.Count(u => u.Result.StartsWith("Installed"));
        SetDrvBusy(false, $"Installed {ok} of {chosen.Count}." + (reboot ? " Restart the PC to finish." : ""));
        if (reboot)
        {
            var r = MessageBox.Show(this, "Some drivers need a restart to finish installing. Restart now?\n\nSave your work in other programs first.",
                                    "Restart needed", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/r /t 5") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
        }
    }

    /// <summary>Restore point first; if Windows can't make one, ask whether to go ahead anyway.</summary>
    private async Task<bool> MakeRestorePointAsync(string description)
    {
        var (okRp, msg) = await Task.Run(() => DriverService.CreateRestorePoint(description));
        DrvStatus.Text = msg;
        if (okRp) return true;
        var go = MessageBox.Show(this, msg + "\n\nInstall anyway, without an undo point?", "No restore point",
                                 MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return go == MessageBoxResult.Yes;
    }

    private bool EnsureAdminFor(string what)
    {
        if (AppEnv.IsAdmin()) return true;
        var r = MessageBox.Show(this, $"Windows only lets programs {what} when they run as administrator.\n\nRestart Moore PC Mechanic as administrator now?",
                                "Administrator needed", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (r == MessageBoxResult.Yes)
        {
            if (AppEnv.RestartAsAdmin()) Close();
            else DrvStatus.Text = "Admin restart was cancelled.";
        }
        return false;
    }

    // ---------------------------------------------------------------------
    //  NVIDIA
    // ---------------------------------------------------------------------

    private void ShowNvidia()
    {
        if (_nvidia == null) { NvPanel.Visibility = Visibility.Collapsed; return; }
        NvPanel.Visibility = Visibility.Visible;
        var nv = _nvidia;
        if (nv.Error != null)
        {
            NvTitle.Text = $"{nv.GpuName}: driver {nv.InstalledVersion}";
            NvDetail.Text = nv.Error + " Windows Update may still offer an NVIDIA driver in the list below.";
            BtnNvInstall.Visibility = Visibility.Collapsed;
            return;
        }
        if (nv.UpdateAvailable)
        {
            NvTitle.Text = $"{nv.GpuName}: NVIDIA {nv.LatestVersion} is available (you have {nv.InstalledVersion})";
            NvDetail.Text = $"{nv.LatestName}" + (nv.LatestDate is DateTime d ? $", released {d:MMM d, yyyy}" : "") +
                            (nv.DownloadSize > 0 ? $". Download about {AppEnv.Bytes(nv.DownloadSize)}." : ".") +
                            " Installs silently and keeps your NVIDIA settings. Screens go black for a few seconds during the install.";
            BtnNvInstall.Visibility = Visibility.Visible;
            BtnNvInstallText.Text = $"Download & install {nv.LatestVersion}";
        }
        else
        {
            NvTitle.Text = $"{nv.GpuName}: driver {nv.InstalledVersion} is the newest ✓";
            NvDetail.Text = (nv.LatestName ?? "Game Ready Driver") + (nv.LatestDate is DateTime d ? $", released {d:MMM d, yyyy}." : ".");
            BtnNvInstall.Visibility = Visibility.Collapsed;
        }
    }

    private async void BtnNvInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_nvidia?.DownloadUrl == null || _drvBusy) return;
        if (!EnsureAdminFor("install a graphics driver")) return;
        var ask = MessageBox.Show(this,
            $"Download and install NVIDIA {_nvidia.LatestVersion}?\n\n" +
            "• A restore point is made first.\n• The download is checked for NVIDIA's digital signature before it runs.\n" +
            "• Your screens will go black and flicker for a few seconds. Close games and video calls first.",
            "Update graphics driver", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK) return;

        SetDrvBusy(true, "Making a restore point…");
        try
        {
            if (!await MakeRestorePointAsync($"Moore PC Mechanic: before NVIDIA {_nvidia.LatestVersion}")) { SetDrvBusy(false); return; }

            NvProgress.Visibility = Visibility.Visible;
            NvProgress.Value = 0;
            DrvStatus.Text = $"Downloading NVIDIA {_nvidia.LatestVersion}…";
            var prog = new Progress<double>(p => { NvProgress.Value = p; DrvStatus.Text = $"Downloading NVIDIA {_nvidia.LatestVersion}… {p:P0}"; });
            var file = await DriverService.DownloadNvidiaAsync(_nvidia, prog);
            NvProgress.Visibility = Visibility.Collapsed;

            DrvStatus.Text = "Signature checked (NVIDIA Corporation). Installing… this takes a few minutes and the screens will flicker.";
            int code = await DriverService.RunNvidiaInstallerAsync(file, silent: true);
            if (code == 0)
            {
                DrvStatus.Text = $"NVIDIA {_nvidia.LatestVersion} installed. A restart is recommended.";
                _nvidia = await DriverService.CheckNvidiaAsync();
                ShowNvidia();
            }
            else
            {
                var open = MessageBox.Show(this,
                    $"NVIDIA's installer finished with code {code}, which can mean it needs a restart or that something went wrong.\n\n" +
                    "Open NVIDIA's installer normally so you can see what it says?", "NVIDIA installer",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (open == MessageBoxResult.Yes) await DriverService.RunNvidiaInstallerAsync(file, silent: false);
                DrvStatus.Text = "Done. Click \"Check for driver updates\" to see the installed version.";
            }
        }
        catch (Exception ex)
        {
            NvProgress.Visibility = Visibility.Collapsed;
            DrvStatus.Text = "NVIDIA update stopped: " + ex.Message;
        }
        finally
        {
            SetDrvBusy(false);
        }
    }
}
