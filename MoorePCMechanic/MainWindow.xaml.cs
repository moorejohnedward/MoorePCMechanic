using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;
using MoorePCMechanic.Services;
using MoorePCMechanic.ViewModels;

namespace MoorePCMechanic;

public partial class MainWindow : Window
{
    // ---- UI collections ----
    private readonly ObservableCollection<Card> _cards = new();
    private readonly ObservableCollection<SensorRow> _sensorRows = new();
    private readonly Dictionary<string, SensorRow> _rowMap = new();
    private readonly ObservableCollection<ProcessGroup> _procRows = new();
    private readonly ObservableCollection<Finding> _findings = new();
    private ICollectionView? _sensorView;

    private readonly Card _cpuCard = new("Processor", "");
    private readonly Card _gpuCard = new("Graphics", "");
    private readonly Card _vramCard = new("Video memory", "");
    private readonly Card _ramCard = new("System memory", "");
    private readonly Card _diskCard = new("Hottest drive", "");

    // ---- Background work ----
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private volatile bool _resetRequested;
    private volatile bool _closing;
    private readonly object _logLock = new();
    private CsvLogger? _logger;
    private List<SensorReading> _lastReadings = new();
    private List<ProcessGroup> _lastProcs = new();
    private InventoryResult? _inventory;
    private bool _sensorNotesAdded;

    public MainWindow()
    {
        InitializeComponent();

        foreach (var c in new[] { _cpuCard, _gpuCard, _vramCard, _ramCard, _diskCard }) _cards.Add(c);
        CardsList.ItemsSource = _cards;
        FindingsList.ItemsSource = _findings;
        ProcessGrid.ItemsSource = _procRows;

        _sensorView = CollectionViewSource.GetDefaultView(_sensorRows);
        _sensorView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorRow.Hardware)));
        _sensorView.Filter = FilterSensor;
        SensorGrid.ItemsSource = _sensorView;

        var admin = AppEnv.IsAdmin();
        AdminText.Text = admin ? "● Admin mode - all sensors" : "● Limited mode";
        AdminText.Foreground = (Brush)FindResource(admin ? "GoodBrush" : "TipBrush");
        BtnElevate.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        SubtitleText.Text = $"{Environment.MachineName}  ·  v{UpdateService.CurrentVersion}";

        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--updated"))
            StatusText.Text = $"Updated to version {UpdateService.CurrentVersion}.";
        if (args.Contains("--minimized"))
            WindowState = WindowState.Minimized; // started with Windows: run quietly on the taskbar
    }

    // =====================================================================
    //  Startup / shutdown
    // =====================================================================

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _loop = Task.Run(() => SensorLoop(_cts.Token));
        InitAudio();
        _ = CheckForUpdateQuietlyAsync();
        await RunInventoryAsync();
    }

    // =====================================================================
    //  Updates
    // =====================================================================

    private UpdateInfo? _pendingUpdate;

    /// <summary>Startup check: shows the banner if a newer version is out. Silent if offline.</summary>
    private async Task CheckForUpdateQuietlyAsync()
    {
        if (!UpdateService.CheckOnStartup) return;
        try
        {
            await Task.Delay(3000); // let the dashboard load first
            var info = await UpdateService.CheckAsync();
            if (info is null || UpdateService.IsSkipped(info)) return;
            _pendingUpdate = info;
            UpdateBannerText.Text = $"Version {info.Version} is ready (you have {UpdateService.CurrentVersion}). Updating takes about a minute and restarts the app.";
            UpdateBanner.Visibility = Visibility.Visible;
        }
        catch { /* no internet or GitHub unreachable: try again next launch */ }
    }

    private void BtnUpdates_Click(object sender, RoutedEventArgs e) =>
        new UpdateWindow(null) { Owner = this }.ShowDialog();

    private void BtnShowUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
        new UpdateWindow(_pendingUpdate) { Owner = this }.ShowDialog();
    }

    private void BtnDismissUpdate_Click(object sender, RoutedEventArgs e) =>
        UpdateBanner.Visibility = Visibility.Collapsed;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        DisposeAudio();
        _cts.Cancel();
        try { _loop?.Wait(2500); } catch { }
        lock (_logLock)
        {
            _logger?.Dispose();
            _logger = null;
        }
    }

    // =====================================================================
    //  Inventory
    // =====================================================================

    private async Task RunInventoryAsync()
    {
        BtnRescan.IsEnabled = false;
        InvStatus.Text = "Scanning hardware…";
        try
        {
            var inv = await Task.Run(() => InventoryService.Scan());
            _inventory = inv;
            SectionsList.ItemsSource = inv.Sections;
            _findings.Clear();
            foreach (var f in inv.Findings.OrderByDescending(f => f.Level)) _findings.Add(f);
            _sensorNotesAdded = false; // re-add the live-sensor notes after a rescan
            InvStatus.Text = $"Scanned at {inv.ScannedAt:h:mm tt}";
        }
        catch (Exception ex)
        {
            InvStatus.Text = "Scan failed: " + ex.Message;
        }
        finally
        {
            BtnRescan.IsEnabled = true;
        }
    }

    private async void BtnRescan_Click(object sender, RoutedEventArgs e) => await RunInventoryAsync();

    private string BuildReport()
    {
        var sb = new StringBuilder(_inventory?.ToReport() ?? "Hardware scan not finished yet.\r\n");
        sb.AppendLine();
        sb.AppendLine("[LIVE SNAPSHOT]");
        foreach (var c in _cards) sb.AppendLine($"  {c.Title}: {c.Value}  {c.Detail}");
        var notes = _findings.Where(f => _inventory == null || !_inventory.Findings.Contains(f)).ToList();
        foreach (var f in notes) sb.AppendLine($"  ({f.LevelText}) {f.Title} - {f.Detail}");
        return sb.ToString();
    }

    private void BtnCopyReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BuildReport());
            InvStatus.Text = "Report copied - paste it anywhere (or to Claude for upgrade advice).";
        }
        catch (Exception ex)
        {
            InvStatus.Text = "Couldn't copy: " + ex.Message;
        }
    }

    private void BtnSaveReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(AppEnv.SubFolder("Reports"), $"system-report-{DateTime.Now:yyyy-MM-dd_HH-mm}.txt");
            File.WriteAllText(path, BuildReport());
            InvStatus.Text = "Saved: " + path;
        }
        catch (Exception ex)
        {
            InvStatus.Text = "Couldn't save: " + ex.Message;
        }
    }

    private void BtnOpenData_Click(object sender, RoutedEventArgs e) => AppEnv.OpenFolder(AppEnv.DataFolder);

    private void BtnElevate_Click(object sender, RoutedEventArgs e)
    {
        if (AppEnv.RestartAsAdmin()) Close();
        else StatusText.Text = "Admin restart was cancelled.";
    }

    // =====================================================================
    //  Sensor loop (background thread)
    // =====================================================================

    private async Task SensorLoop(CancellationToken ct)
    {
        using var sensors = new SensorService();
        if (sensors.StartupError != null)
        {
            var msg = sensors.StartupError;
            _ = Dispatcher.InvokeAsync(() => StatusText.Text = "Sensor library couldn't start: " + msg);
        }

        var procMon = new ProcessMonitor();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        int tick = 0;
        try
        {
            do
            {
                if (_resetRequested)
                {
                    _resetRequested = false;
                    try { sensors.ResetMinMax(); } catch { }
                }

                List<SensorReading> readings;
                try { readings = sensors.Read(); }
                catch { readings = new List<SensorReading>(); }

                List<ProcessGroup>? procs = null;
                if (tick % 2 == 0)
                {
                    try { procs = procMon.Sample(); } catch { }
                }

                int logRows = -1;
                lock (_logLock)
                {
                    if (_logger != null)
                    {
                        try { _logger.Write(readings); logRows = _logger.Rows; } catch { }
                    }
                }

                var mem = AppEnv.GetMemory();
                if (_closing) break;
                _ = Dispatcher.InvokeAsync(() => ApplyUpdate(readings, procs, mem, logRows));
                tick++;
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // closing
        }
    }

    // =====================================================================
    //  UI updates (UI thread)
    // =====================================================================

    private void ApplyUpdate(List<SensorReading> readings, List<ProcessGroup>? procs, (ulong Total, ulong Used, uint Load) mem, int logRows)
    {
        if (_closing) return;
        _lastReadings = readings;

        foreach (var r in readings)
        {
            if (!_rowMap.TryGetValue(r.Key, out var row))
            {
                row = new SensorRow { Key = r.Key, Hardware = r.Hardware, Name = r.Name, TypeName = SensorService.TypeName(r.Type) };
                _rowMap[r.Key] = row;
                _sensorRows.Add(row);
            }
            row.Value = SensorService.Format(r.Type, r.Value);
            row.Min = SensorService.Format(r.Type, r.Min);
            row.Max = SensorService.Format(r.Type, r.Max);
        }

        UpdateCards(readings, mem);

        if (procs != null)
        {
            _lastProcs = procs;
            ShowProcesses();
        }

        if (logRows >= 0)
            BtnLogText.Text = $"Stop logging ({logRows} rows)";

        if (!_sensorNotesAdded && readings.Count > 0 && _inventory != null)
        {
            _sensorNotesAdded = true;
            AddSensorNotes(readings);
        }
    }

    private static float? Pick(IEnumerable<SensorReading> rs, SensorType type, params string[] names)
    {
        var ofType = rs.Where(r => r.Type == type && r.Value is float f && !float.IsNaN(f)).ToList();
        foreach (var n in names)
        {
            var hit = ofType.FirstOrDefault(r => r.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit.Value;
        }
        return null;
    }

    private static float? MaxOf(IEnumerable<SensorReading> rs, SensorType type)
    {
        var vals = rs.Where(r => r.Type == type && r.Value is float f && !float.IsNaN(f)).Select(r => r.Value!.Value).ToList();
        return vals.Count == 0 ? null : vals.Max();
    }

    private static int HeatLevel(float? t, float warm, float hot) =>
        t is not float x ? 0 : x >= hot ? 2 : x >= warm ? 1 : 0;

    private void UpdateCards(List<SensorReading> rs, (ulong Total, ulong Used, uint Load) mem)
    {
        // ---- CPU ----
        var cpu = rs.Where(r => r.RootType == HardwareType.Cpu).ToList();
        if (cpu.Count > 0)
        {
            var load = Pick(cpu, SensorType.Load, "CPU Total");
            var temp = Pick(cpu, SensorType.Temperature, "Tctl", "Package", "Tdie", "Core Max", "Core Average") ?? MaxOf(cpu, SensorType.Temperature);
            var clocks = cpu.Where(r => r.Type == SensorType.Clock && r.Name.StartsWith("Core", StringComparison.OrdinalIgnoreCase) && r.Value > 0)
                            .Select(r => r.Value!.Value).ToList();
            var power = Pick(cpu, SensorType.Power, "Package");
            _cpuCard.Value = (load is float l ? $"{l:0}%" : "—") + (temp is float t ? $"  ·  {t:0}°C" : "");
            _cpuCard.Detail = string.Join("  ·  ", new[]
            {
                clocks.Count > 0 ? $"{clocks.Average() / 1000:0.00} GHz avg" : null,
                power is float p ? $"{p:0} W" : null,
                temp == null ? "temp hidden (see notes)" : null,
            }.Where(s => s != null));
            _cpuCard.Percent = load ?? 0;
            _cpuCard.Heat = HeatLevel(temp, 80, 90);
        }

        // ---- GPU (prefer the discrete card) ----
        var gpuRoot = rs.FirstOrDefault(r => r.RootType is HardwareType.GpuNvidia or HardwareType.GpuAmd)
                      ?? rs.FirstOrDefault(r => r.RootType == HardwareType.GpuIntel);
        if (gpuRoot != null)
        {
            var gpu = rs.Where(r => r.Hardware == gpuRoot.Hardware).ToList();
            var load = Pick(gpu, SensorType.Load, "GPU Core", "D3D 3D");
            var temp = Pick(gpu, SensorType.Temperature, "GPU Core", "GPU Hot Spot") ?? MaxOf(gpu, SensorType.Temperature);
            var power = Pick(gpu, SensorType.Power, "GPU Package", "GPU Power", "GPU Board", "GPU Core");
            var clock = Pick(gpu, SensorType.Clock, "GPU Core");
            _gpuCard.Value = (load is float l ? $"{l:0}%" : "—") + (temp is float t ? $"  ·  {t:0}°C" : "");
            _gpuCard.Detail = string.Join("  ·  ", new[]
            {
                gpuRoot.Hardware.Replace("NVIDIA GeForce ", "").Replace("AMD Radeon ", "Radeon "),
                clock is float c ? $"{c:0} MHz" : null,
                power is float p ? $"{p:0} W" : null,
            }.Where(s => s != null));
            _gpuCard.Percent = load ?? 0;
            _gpuCard.Heat = HeatLevel(temp, 78, 87);

            var used = Pick(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
            var total = Pick(gpu, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
            if (used is float u && total is float tot && tot > 0)
            {
                _vramCard.Value = $"{u / 1024:0.0} / {tot / 1024:0} GB";
                _vramCard.Percent = u * 100 / tot;
                _vramCard.Detail = $"{u * 100 / tot:0}% in use";
                _vramCard.Heat = u / tot >= 0.95 ? 2 : u / tot >= 0.85 ? 1 : 0;
            }
            else if (used is float u2)
            {
                _vramCard.Value = $"{u2 / 1024:0.0} GB used";
                _vramCard.Detail = "total not reported";
            }
        }
        else
        {
            _gpuCard.Detail = "No GPU sensors found";
        }

        // ---- RAM ----
        if (mem.Total > 0)
        {
            _ramCard.Value = $"{mem.Used / 1073741824.0:0.0} / {mem.Total / 1073741824.0:0} GB";
            _ramCard.Detail = $"{mem.Load}% in use";
            _ramCard.Percent = mem.Load;
            _ramCard.Heat = mem.Load >= 90 ? 2 : mem.Load >= 80 ? 1 : 0;
        }

        // ---- Hottest drive ----
        var driveTemps = rs.Where(r => r.RootType == HardwareType.Storage && r.Type == SensorType.Temperature && r.Value is float f && !float.IsNaN(f) && f > 0)
                           .OrderByDescending(r => r.Value).ToList();
        if (driveTemps.Count > 0)
        {
            var hot = driveTemps[0];
            var usedSpace = rs.FirstOrDefault(r => r.Hardware == hot.Hardware && r.Type == SensorType.Load && r.Name.Contains("Used Space", StringComparison.OrdinalIgnoreCase));
            _diskCard.Value = $"{hot.Value:0}°C";
            _diskCard.Detail = hot.Hardware + (usedSpace?.Value is float us ? $"  ·  {us:0}% full" : "");
            _diskCard.Percent = usedSpace?.Value ?? 0;
            _diskCard.Heat = HeatLevel(hot.Value, 60, 70);
        }
        else
        {
            _diskCard.Detail = AppEnv.IsAdmin() ? "No drive temperature sensors" : "Needs admin mode";
        }
    }

    private void AddSensorNotes(List<SensorReading> rs)
    {
        bool admin = AppEnv.IsAdmin();
        bool cpuTemp = rs.Any(r => r.RootType == HardwareType.Cpu && r.Type == SensorType.Temperature && r.Value is float f && f > 0);
        if (!admin)
        {
            _findings.Insert(0, new Finding(FindingLevel.Tip, "Limited mode",
                "Click \"Restart as admin\" (top right) to see CPU temperature, fan speeds, voltages and drive health."));
        }
        else if (!cpuTemp)
        {
            _findings.Insert(0, new Finding(FindingLevel.Tip, "CPU temperature not available",
                "The sensor library needs a small signed driver for CPU temps. If it stays blank, install PawnIO from pawnio.eu, then restart this app."));
        }

        // Drive wear (SMART "Percentage Used" / life remaining) where the drive reports it
        foreach (var g in rs.Where(r => r.RootType == HardwareType.Storage).GroupBy(r => r.Hardware))
        {
            var life = g.FirstOrDefault(r => r.Type == SensorType.Level &&
                        (r.Name.Contains("Percentage Used", StringComparison.OrdinalIgnoreCase) || r.Name.Contains("Remaining Life", StringComparison.OrdinalIgnoreCase)));
            if (life?.Value is not float v) continue;
            bool isUsed = life.Name.Contains("Used", StringComparison.OrdinalIgnoreCase);
            float wearUsed = isUsed ? v : 100 - v;
            _findings.Add(wearUsed >= 80
                ? new Finding(FindingLevel.Warning, $"{g.Key}: {wearUsed:0}% of rated life used", "Plan to replace this SSD and keep backups current.")
                : new Finding(FindingLevel.Good, $"{g.Key}: {wearUsed:0}% of rated life used", "Plenty of life left."));
        }
    }

    // =====================================================================
    //  Live sensors tab
    // =====================================================================

    private bool FilterSensor(object o)
    {
        var q = SensorSearch?.Text?.Trim();
        if (string.IsNullOrEmpty(q) || o is not SensorRow r) return true;
        if (q.Equals("temp", StringComparison.OrdinalIgnoreCase)) q = "Temperature";
        return r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Hardware.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.TypeName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void SensorSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => _sensorView?.Refresh();

    private void BtnResetMinMax_Click(object sender, RoutedEventArgs e) => _resetRequested = true;

    private void BtnLog_Click(object sender, RoutedEventArgs e)
    {
        lock (_logLock)
        {
            if (_logger == null)
            {
                if (_lastReadings.Count == 0)
                {
                    StatusText.Text = "Sensors are still starting - try again in a second.";
                    return;
                }
                try
                {
                    _logger = new CsvLogger(_lastReadings);
                    BtnLogText.Text = "Stop logging";
                    StatusText.Text = "Logging to " + _logger.FilePath;
                }
                catch (Exception ex)
                {
                    StatusText.Text = "Couldn't start log: " + ex.Message;
                }
            }
            else
            {
                var path = _logger.FilePath;
                var rows = _logger.Rows;
                _logger.Dispose();
                _logger = null;
                BtnLogText.Text = "Start logging";
                StatusText.Text = $"Saved {rows} rows to {path}";
            }
        }
    }

    private void BtnOpenLogs_Click(object sender, RoutedEventArgs e) => AppEnv.OpenFolder(AppEnv.SubFolder("Logs"));

    // =====================================================================
    //  Processes tab
    // =====================================================================

    private void Sort_Checked(object sender, RoutedEventArgs e)
    {
        if (_procRows == null) return; // fires once during InitializeComponent
        ShowProcesses();
    }

    private void ShowProcesses()
    {
        bool byMem = SortMem?.IsChecked == true;
        var top = (byMem
                ? _lastProcs.OrderByDescending(p => p.MemoryMB)
                : _lastProcs.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.MemoryMB))
            .Take(30).ToList();
        _procRows.Clear();
        foreach (var p in top) _procRows.Add(p);
    }
}
