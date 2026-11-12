using System.Management;
using System.Windows;
using System.Windows.Controls;
using LibreHardwareMonitor.Hardware;
using MoorePCMechanic.Services;

namespace MoorePCMechanic;

/// <summary>Benchmarks tab: run tests, show results against an earlier run, keep a history.</summary>
public partial class MainWindow
{
    private sealed record DriveChoice(string Root, string Text);

    private List<BenchmarkRun> _bmHistory = new();
    private CancellationTokenSource? _bmCts;
    private bool _bmInit;

    /// <summary>Called once from Window_Loaded.</summary>
    private void InitBenchmarks()
    {
        if (_bmInit) return;
        _bmInit = true;
        var drives = BenchmarkService.TestableDrives().Select(d => new DriveChoice(d.Root, d.Text)).ToList();
        BmDrive.ItemsSource = drives;
        if (drives.Count > 0) BmDrive.SelectedIndex = 0;
        else { BmDisk.IsChecked = false; BmDisk.IsEnabled = false; }
        _bmHistory = BenchmarkService.LoadHistory();
        ShowBmHistory();
    }

    private void ShowBmHistory()
    {
        BmHistory.ItemsSource = _bmHistory.OrderByDescending(r => r.Date).ToList();
    }

    private async void BtnBmRun_Click(object sender, RoutedEventArgs e)
    {
        var root = (BmDrive.SelectedItem as DriveChoice)?.Root;
        var opts = new BenchmarkOptions
        {
            Cpu = BmCpu.IsChecked == true,
            Memory = BmMem.IsChecked == true,
            Gpu = BmGpu.IsChecked == true,
            Disk = BmDisk.IsChecked == true && root != null,
            DiskRoot = root,
            Label = string.IsNullOrWhiteSpace(BmLabel.Text) ? "" : BmLabel.Text.Trim(),
        };
        if (!opts.Cpu && !opts.Memory && !opts.Gpu && !opts.Disk) { BmStatus.Text = "Tick at least one test."; return; }

        _bmCts = new CancellationTokenSource();
        BtnBmRun.IsEnabled = false;
        BtnBmStop.IsEnabled = true;
        BmProgress.Visibility = Visibility.Visible;
        BmProgress.Value = 0;
        var progress = new Progress<(double Fraction, string Text)>(p =>
        {
            if (p.Fraction >= 0) BmProgress.Value = p.Fraction;
            BmStatus.Text = p.Text;
        });

        try
        {
            var token = _bmCts.Token;
            var run = await Task.Run(() =>
            {
                var r = BenchmarkService.Run(opts, SampleCpu, progress, token);
                Snapshot(r);
                return r;
            });
            if (string.IsNullOrEmpty(run.Label)) run.Label = "Run " + run.Date.ToString("MMM d");
            var previous = _bmHistory.Where(r => r.IsThisPc).OrderByDescending(r => r.Date).FirstOrDefault(); // compare with this PC's last run
            _bmHistory.Add(run);
            BenchmarkService.SaveHistory(_bmHistory);
            ShowBmHistory();
            ShowBmResults(run, previous);
            BmStatus.Text = "Finished and saved. Run it again after an upgrade (or a driver update) to see the difference.";
        }
        catch (OperationCanceledException)
        {
            BmStatus.Text = "Stopped. Nothing was saved.";
        }
        catch (Exception ex)
        {
            BmStatus.Text = "The benchmark stopped: " + ex.Message;
        }
        finally
        {
            BtnBmRun.IsEnabled = true;
            BtnBmStop.IsEnabled = false;
            BmProgress.Visibility = Visibility.Collapsed;
            _bmCts?.Dispose();
            _bmCts = null;
        }
    }

    private void BtnBmStop_Click(object sender, RoutedEventArgs e)
    {
        _bmCts?.Cancel();
        BmStatus.Text = "Stopping…";
    }

    private void ShowBmResults(BenchmarkRun now, BenchmarkRun? before)
    {
        BmResultsPanel.Visibility = Visibility.Visible;
        BmResultsTitle.Text = before == null
            ? $"{now.Label}  ({Where(now)}{now.DateText})"
            : $"{now.Label}  ({Where(now)}{now.DateText})   compared with   {before.Label}  ({Where(before)}{before.DateText})";
        BmLines.ItemsSource = BenchmarkService.Compare(now, before);
        var notes = new List<string>();
        if (now.MaxCpuTemp is double t) notes.Add($"Hottest processor temperature during the all-core test: {Temp.Full(t, "0")}.");
        else if (now.CpuMulti != null) notes.Add("Processor temperature wasn't available (run as admin to include the heat check).");
        if (!string.IsNullOrWhiteSpace(now.Notes)) notes.Add(now.Notes);
        if (!string.IsNullOrEmpty(now.Gpu)) notes.Add($"Graphics card tested: {now.Gpu}" + (string.IsNullOrEmpty(now.GpuDriver) ? "." : $" (driver {now.GpuDriver})."));
        BmNotes.Text = string.Join("  ", notes);
    }

    private static string Where(BenchmarkRun r) => string.IsNullOrEmpty(r.Machine) ? "" : r.Machine + ", ";

    private void BmHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var picked = BmHistory.SelectedItems.Cast<BenchmarkRun>().OrderByDescending(r => r.Date).ToList();
        if (picked.Count == 0) return;
        var now = picked[0];
        var before = picked.Count >= 2 ? picked[1]
            : _bmHistory.Where(r => r.Date < now.Date && string.Equals(r.Machine, now.Machine, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(r => r.Date).FirstOrDefault();
        ShowBmResults(now, before);
    }

    private void BtnBmDelete_Click(object sender, RoutedEventArgs e)
    {
        var picked = BmHistory.SelectedItems.Cast<BenchmarkRun>().ToList();
        if (picked.Count == 0) { BmStatus.Text = "Select the run(s) to delete in the list first."; return; }
        if (MessageBox.Show(this, $"Delete {picked.Count} saved run(s)?", "Delete runs", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        foreach (var r in picked) _bmHistory.Remove(r);
        BenchmarkService.SaveHistory(_bmHistory);
        ShowBmHistory();
        BmResultsPanel.Visibility = Visibility.Collapsed;
    }

    private void BtnBmFolder_Click(object sender, RoutedEventArgs e) => AppEnv.OpenFolder(BenchmarkService.HistoryFolder);

    /// <summary>Current CPU temperature and average core clock from the live sensor loop (null when not available).</summary>
    private (float? Temp, float? Clock) SampleCpu()
    {
        var rs = _lastReadings; // replaced (never changed in place) by the sensor loop, so safe to read here
        var cpu = rs.Where(r => r.RootType == HardwareType.Cpu).ToList();
        if (cpu.Count == 0) return (null, null);
        var temp = Pick(cpu, SensorType.Temperature, "Tctl", "Package", "Tdie", "Core Max", "Core Average") ?? MaxOf(cpu, SensorType.Temperature);
        var clocks = cpu.Where(r => r.Type == SensorType.Clock && r.Name.StartsWith("Core", StringComparison.OrdinalIgnoreCase) && r.Value > 0)
                        .Select(r => r.Value!.Value).ToList();
        return (temp, clocks.Count > 0 ? clocks.Average() : null);
    }

    /// <summary>Records what the PC was at the time (so later comparisons make sense).</summary>
    private static void Snapshot(BenchmarkRun r)
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (ManagementObject o in s.Get()) using (o) { r.Cpu = (o["Name"] as string ?? "").Trim(); break; }
        }
        catch { }
        try
        {
            ulong total = 0; uint speed = 0; int sticks = 0;
            using var s = new ManagementObjectSearcher("SELECT Capacity, ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    sticks++;
                    total += Convert.ToUInt64(o["Capacity"] ?? 0UL);
                    var sp = Convert.ToUInt32(o["ConfiguredClockSpeed"] ?? o["Speed"] ?? 0u);
                    if (sp > speed) speed = sp;
                }
            r.Memory = $"{AppEnv.Bytes(total)} in {sticks} stick(s)" + (speed > 0 ? $" at {speed} MT/s" : "");
        }
        catch { }
        try
        {
            var nv = DriverService.FindNvidiaGpu();
            if (nv != null)
            {
                if (string.IsNullOrEmpty(r.Gpu)) r.Gpu = nv.Value.Name;
                r.GpuDriver = DriverService.NvidiaVersionFromWindows(nv.Value.WinVersion);
            }
        }
        catch { }
    }
}
