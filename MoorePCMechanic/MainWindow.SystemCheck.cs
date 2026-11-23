using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using MoorePCMechanic.Services;

namespace MoorePCMechanic;

/// <summary>System check tab: full hardware + security check, quick memory test, power & cooling test, report.</summary>
public partial class MainWindow
{
    private List<SecurityItem> _hwItems = new();
    private List<SecurityItem> _chkSecItems = new();
    private readonly List<SecurityItem> _testItems = new();   // memory and power tests, newest first
    private bool _chkBusy;
    private CancellationTokenSource? _testCts;
    private DateTime? _chkWhen;

    /// <summary>Called from Window_Loaded: reports a power & cooling test the PC didn't survive.</summary>
    private void InitSystemCheck()
    {
        ChkLength.SelectedIndex = 0;
        var down = StressTest.CheckInterrupted();
        if (down == null) return;
        _testItems.Insert(0, down);
        ShowCheckLists();
        ChkStatus.Text = down.Title;
        StatusText.Text = "The PC went down during the last power & cooling test. See the System check tab.";
    }

    private void SetChkBusy(bool busy, string? status = null, bool testRunning = false)
    {
        _chkBusy = busy;
        BtnChkRun.IsEnabled = BtnChkMem.IsEnabled = BtnChkStress.IsEnabled = BtnSysCheck.IsEnabled = !busy;
        BtnChkStop.Visibility = testRunning ? Visibility.Visible : Visibility.Collapsed;
        ChkProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (status != null) ChkStatus.Text = status;
    }

    /// <summary>Header button: jump to the System check tab and run it.</summary>
    private async void BtnSysCheck_Click(object sender, RoutedEventArgs e)
    {
        Tabs.SelectedItem = CheckTab;
        await RunSystemCheckAsync();
    }

    private async void BtnChkRun_Click(object sender, RoutedEventArgs e) => await RunSystemCheckAsync();

    private async Task RunSystemCheckAsync()
    {
        if (_chkBusy) return;
        SetChkBusy(true, "Starting the full check (about 30-60 seconds)…");
        try
        {
            // the live sensors need a second or two after the app starts
            for (int i = 0; i < 6 && _lastReadings.Count == 0; i++) await Task.Delay(500);
            var readings = _lastReadings;
            var progress = new Progress<string>(s => ChkStatus.Text = s);
            var hwTask = Task.Run(() => HardwareCheckService.Check(readings, progress));
            var secTask = Task.Run(SecurityService.Sweep);
            await Task.WhenAll(hwTask, secTask);
            _hwItems = hwTask.Result;
            _chkSecItems = secTask.Result;
            SecList.ItemsSource = _chkSecItems; // the Security tab shows the same sweep
            _chkWhen = DateTime.Now;
            ShowCheckLists();
            int probs = _hwItems.Concat(_chkSecItems).Count(i => i.Level == FindingLevel.Warning);
            SetChkBusy(false, $"Checked at {_chkWhen:HHmm}. " + (probs == 0 ? "Nothing needs fixing." : $"{probs} thing(s) to look at (red, at the top of each list).") +
                              (_testItems.Count == 0 ? " Next: the quick memory test, then the power & cooling test." : ""));
        }
        catch (Exception ex)
        {
            SetChkBusy(false, "Check failed: " + ex.Message);
        }
    }

    private void ShowCheckLists()
    {
        ChkHwList.ItemsSource = _testItems.Concat(_hwItems).ToList();
        ChkSecList.ItemsSource = _chkSecItems.ToList();
        string Count(IEnumerable<SecurityItem> items)
        {
            var l = items.ToList();
            if (l.Count == 0) return "not checked yet";
            int w = l.Count(i => i.Level == FindingLevel.Warning), t = l.Count(i => i.Level == FindingLevel.Tip), g = l.Count(i => i.Level == FindingLevel.Good);
            return $"{w} to fix · {t} tip(s) · {g} good";
        }
        ChkHwSummary.Text = Count(_testItems.Concat(_hwItems));
        ChkSecSummary.Text = Count(_chkSecItems);
    }

    // =====================================================================
    //  Tests
    // =====================================================================

    private async void BtnChkMem_Click(object sender, RoutedEventArgs e)
    {
        if (_chkBusy) return;
        if (MessageBox.Show(this,
                "The quick memory test borrows most of the free memory and checks it with 6 patterns, twice (about 1-3 minutes).\n\n" +
                "Close other programs first so more memory gets tested. The PC may feel slow while it runs.",
                "Quick memory test", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        _testCts = new CancellationTokenSource();
        SetChkBusy(true, "Starting the memory test…", testRunning: true);
        var progress = new Progress<string>(s => ChkStatus.Text = s);
        var ct = _testCts.Token;
        try
        {
            var r = await Task.Run(() => MemoryTest.Run(2, progress, ct), ct);
            var item = MemoryTest.ToItem(r);
            _testItems.Insert(0, item);
            ShowCheckLists();
            SetChkBusy(false, item.Title);
        }
        catch (OperationCanceledException) { SetChkBusy(false, "Memory test stopped."); }
        catch (Exception ex) { SetChkBusy(false, "Memory test failed: " + ex.Message); }
    }

    private async void BtnChkStress_Click(object sender, RoutedEventArgs e)
    {
        if (_chkBusy) return;
        int minutes = ChkLength.SelectedIndex switch { 1 => 10, 2 => 30, _ => 3 };
        bool gpu = ChkGpu.IsChecked == true;
        if (!AppEnv.IsAdmin())
        {
            var a = MessageBox.Show(this,
                "Without admin mode the test can't read temperatures, clock speeds or power-supply voltages, so it can only tell whether the PC stays up.\n\nRestart as admin first? (Recommended)",
                "Power & cooling test", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (a == MessageBoxResult.Cancel) return;
            if (a == MessageBoxResult.Yes)
            {
                if (AppEnv.RestartAsAdmin()) Close();
                return;
            }
        }
        if (MessageBox.Show(this,
                $"Run the processor{(gpu ? " and graphics card" : "")} flat out for {minutes} minutes while watching temperatures, clock speeds and power-supply voltages?\n\n" +
                "• Save your work and close other programs first.\n• The fans will get loud; that's normal.\n" +
                "• If the PC shuts off or restarts, that IS the result: open Moore PC Mechanic again and it will say so.\n• \"Stop test\" ends it any time.",
                "Power & cooling test", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        _testCts = new CancellationTokenSource();
        SetChkBusy(true, "Starting the power & cooling test…", testRunning: true);
        var progress = new Progress<string>(s => ChkStatus.Text = s);
        var ct = _testCts.Token;
        try
        {
            var items = await Task.Run(() => StressTest.Run(TimeSpan.FromMinutes(minutes), gpu, () => _lastReadings, progress, ct));
            _testItems.InsertRange(0, items.OrderBy(i => i.Level switch { FindingLevel.Warning => 0, FindingLevel.Tip => 1, _ => 2 }));
            ShowCheckLists();
            int probs = items.Count(i => i.Level == FindingLevel.Warning);
            SetChkBusy(false, probs == 0 ? "Power & cooling test finished: no problems found." : $"Power & cooling test finished: {probs} problem(s). See the top of the Hardware list.");
        }
        catch (Exception ex) { SetChkBusy(false, "Power & cooling test failed: " + ex.Message); }
    }

    private void BtnChkStop_Click(object sender, RoutedEventArgs e)
    {
        _testCts?.Cancel();
        ChkStatus.Text = "Stopping…";
    }

    // =====================================================================
    //  Fix buttons
    // =====================================================================

    private async void CheckFix_Click(object sender, RoutedEventArgs e)
    {
        if (_chkBusy || sender is not Button { Tag: string id }) return;
        if (id == "goto-drivers") { Tabs.SelectedItem = DriversTab; return; }

        string? msg;
        if (id is "mdsched" or "clock-sync" or "pchealth")
        {
            if (id == "clock-sync" && !EnsureAdminFor("set the clock")) return;
            if (id == "mdsched" && MessageBox.Show(this,
                    "Windows Memory Diagnostic tests all of the memory before Windows starts. It needs a restart and can take an hour or more with a lot of memory.\n\n" +
                    "Save your work, then pick \"Restart now and check for problems\" in the next window. The result appears here after the next full check.",
                    "Full memory test", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            try { msg = await Task.Run(() => HardwareCheckService.Fix(id)); }
            catch (Exception ex) { msg = "Couldn't do that: " + ex.Message; }
        }
        else msg = await RunSecurityFixAsync(id);

        if (msg == null) return;
        if (id is "clock-sync" || (!NoRescanFixes.Contains(id) && id is not ("mdsched" or "pchealth"))) await RunSystemCheckAsync();
        ChkStatus.Text = msg;
    }

    // =====================================================================
    //  Report
    // =====================================================================

    private string BuildCheckReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("MOORE PC MECHANIC - FULL SYSTEM CHECK");
        sb.AppendLine($"Computer:   {Environment.MachineName}");
        sb.AppendLine($"Checked:    {(_chkWhen ?? DateTime.Now):yyyy-MM-dd HHmm}");
        sb.AppendLine($"Admin mode: {(AppEnv.IsAdmin() ? "yes" : "no (some checks hidden)")}");
        sb.AppendLine(new string('=', 60));
        void Section(string name, IEnumerable<SecurityItem> items)
        {
            var l = items.ToList();
            if (l.Count == 0) return;
            sb.AppendLine();
            sb.AppendLine($"[{name}]");
            foreach (var i in l) sb.AppendLine($"  ({i.LevelText}) {i.Title} - {i.Detail}");
        }
        Section("TESTS", _testItems);
        Section("HARDWARE", _hwItems);
        Section("SECURITY", _chkSecItems);
        sb.AppendLine();
        sb.AppendLine(new string('=', 60));
        sb.Append(BuildReport()); // the Overview hardware inventory
        return sb.ToString();
    }

    private void BtnChkCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BuildCheckReport());
            ChkStatus.Text = "Report copied - paste it anywhere (or to Claude for a second opinion).";
        }
        catch (Exception ex) { ChkStatus.Text = "Couldn't copy: " + ex.Message; }
    }

    private void BtnChkSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(AppEnv.SubFolder("Reports"), $"system-check-{Environment.MachineName}-{DateTime.Now:yyyy-MM-dd_HHmm}.txt");
            File.WriteAllText(path, BuildCheckReport());
            ChkStatus.Text = "Saved: " + path;
        }
        catch (Exception ex) { ChkStatus.Text = "Couldn't save: " + ex.Message; }
    }
}
