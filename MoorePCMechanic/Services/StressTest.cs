// Moore PC Mechanic - power & cooling test
// Runs every processor thread (and the graphics card) flat out for a few minutes while the live sensors
// are sampled once a second: temperatures, clock speeds (throttling), power-supply rails and fans.
// A weak or failing power supply often shows up only under load, as sagging voltages or a PC that
// simply turns off. A marker file is written before the load starts and removed when it ends; if the
// app finds it on the next start (and the PC has restarted since), the PC went down during the test.

using System.IO;
using System.Numerics;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using static MoorePCMechanic.Services.HardwareCheckService;

namespace MoorePCMechanic.Services;

public static class StressTest
{
    private sealed class MarkerData
    {
        public DateTime Started { get; set; }
        public int Minutes { get; set; }
        public bool Gpu { get; set; }
    }

    private static string MarkerFile => Path.Combine(AppEnv.DataFolder, "power-test-running.json");

    public static void ClearMarker()
    {
        try { File.Delete(MarkerFile); } catch { }
    }

    /// <summary>Called at startup: if the last test never finished and the PC has restarted since it began,
    /// the PC shut off or crashed under load. Returns that finding (once) or null.</summary>
    public static SecurityItem? CheckInterrupted()
    {
        try
        {
            if (!File.Exists(MarkerFile)) return null;
            var m = JsonSerializer.Deserialize<MarkerData>(File.ReadAllText(MarkerFile));
            ClearMarker();
            if (m == null) return null;
            var boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
            if (boot < m.Started) return null; // the app was closed mid-test, but the PC kept running
            return Item(FindingLevel.Warning, $"The PC went down during the power & cooling test ({m.Started:MMM d HHmm})",
                "It restarted or turned off while the processor" + (m.Gpu ? " and graphics card were" : " was") + " under full load. " +
                "That points at the power supply (it can't deliver full power), overheating, or unstable memory/overclock settings. " +
                "Check the Crashes line in the System check (a blue screen code means Windows crashed; no code usually means the power cut out). " +
                "If it was a power cut-out, try a known-good power supply before anything else.");
        }
        catch { return null; }
    }

    private sealed record Sample(float? CpuTemp, float? CpuClock, float? CpuPower, float? GpuTemp, float? GpuPower,
                                 float? V12, float? V5, float? V33, float? CpuFan);

    private static Sample Take(IReadOnlyList<SensorReading> rs)
    {
        var (_, gt, gp) = Gpu(rs);
        var cpuFan = rs.FirstOrDefault(r => r.Type == SensorType.Fan && r.RootType == HardwareType.Motherboard && r.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase));
        return new Sample(CpuTemp(rs), CpuClock(rs), CpuPower(rs), gt, gp,
                          RailVolts(rs, Rail.V12), RailVolts(rs, Rail.V5), RailVolts(rs, Rail.V33), cpuFan?.Value);
    }

    /// <param name="sample">Returns the latest live sensor readings (the window's sensor loop refreshes them every second).</param>
    public static List<SecurityItem> Run(TimeSpan length, bool useGpu, Func<IReadOnlyList<SensorReading>> sample,
                                         IProgress<string> progress, CancellationToken ct)
    {
        var items = new List<SecurityItem>();
        try
        {
            File.WriteAllText(MarkerFile, JsonSerializer.Serialize(new MarkerData { Started = DateTime.Now, Minutes = (int)length.TotalMinutes, Gpu = useGpu }));
        }
        catch { }

        var idle = new List<Sample>();
        var load = new List<(double T, Sample S)>();
        string? gpuError = null;
        bool cancelled = false;
        var started = DateTime.Now;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = new List<Thread>();
        try
        {
            // Resting readings first (3 seconds)
            for (int i = 0; i < 3; i++)
            {
                progress.Report("Reading resting temperatures and voltages…");
                idle.Add(Take(sample()));
                if (ct.WaitHandle.WaitOne(1000)) throw new OperationCanceledException();
            }

            // Load: every thread on the processor, plus the graphics card if asked
            int threads = Environment.ProcessorCount;
            for (int i = 0; i < threads; i++)
            {
                var t = new Thread(() => Burn(stop.Token)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "power-test cpu" };
                t.Start();
                workers.Add(t);
            }
            if (useGpu)
            {
                var g = new Thread(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        GpuResult r;
                        try { r = GpuBenchmark.Run(stop.Token); }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex) { gpuError = ex.Message; break; }
                        if (r.Error != null && !stop.IsCancellationRequested) { gpuError = r.Error; break; }
                    }
                }) { IsBackground = true, Name = "power-test gpu" };
                g.Start();
                workers.Add(g);
            }

            var t0 = DateTime.Now;
            while (true)
            {
                if (ct.WaitHandle.WaitOne(1000)) throw new OperationCanceledException();
                double secs = (DateTime.Now - t0).TotalSeconds;
                var s = Take(sample());
                load.Add((secs, s));
                progress.Report($"Power & cooling test: {Clock(secs)} of {Clock(length.TotalSeconds)}" +
                                (s.CpuTemp is float c ? $" · CPU {Temp.Full(c, "0")}" : "") +
                                (s.GpuTemp is float gt ? $" · GPU {Temp.Full(gt, "0")}" : "") +
                                (s.V12 is float v ? $" · 12 V rail {v:0.00} V" : ""));
                if (secs >= length.TotalSeconds) break;
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally
        {
            stop.Cancel();
            foreach (var w in workers) w.Join(5000);
            ClearMarker();
        }

        // ---------- Results ----------
        double ran = load.Count == 0 ? 0 : load[^1].T;
        if (cancelled)
            items.Add(Item(FindingLevel.Tip, $"Power & cooling test stopped after {Clock(ran)}", "The results below only cover the part that ran."));
        else
            items.Add(Item(FindingLevel.Good, $"PC stayed up under full load for {Clock(ran)} ({DateTime.Now:HHmm})",
                $"The processor ({Environment.ProcessorCount} threads)" + (useGpu && gpuError == null ? " and graphics card" : "") +
                " ran flat out without the PC shutting off, freezing or crashing. A failing power supply usually can't do that."));
        if (load.Count == 0) return items;

        var L = load.Select(x => x.S).ToList();
        var idleS = idle.Count > 0 ? idle[^1] : L[0];

        // CPU temperature and throttling
        var cpuMax = L.Max(x => x.CpuTemp);
        var cpuPow = L.Max(x => x.CpuPower);
        var fanMax = L.Max(x => x.CpuFan);
        if (cpuMax is float cm)
        {
            var peakClock = load.Where(x => x.T <= 20).Select(x => x.S.CpuClock).Max();
            var lateClock = load.Where(x => x.T >= ran - 30).Select(x => x.S.CpuClock).Where(c => c != null).Select(c => c!.Value).DefaultIfEmpty().Average();
            double drop = peakClock is float pc && pc > 0 && lateClock > 0 ? 1 - lateClock / pc : 0;
            string detail = $"Processor went from {(idleS.CpuTemp is float it ? Temp.Full(it, "0") : "?")} at rest to {Temp.Full(cm, "0")} under load" +
                            (cpuPow is float p ? $", drawing up to {p:0} W" : "") +
                            (peakClock is float pk && lateClock > 0 ? $"; clock {pk / 1000:0.00} GHz at the start, {lateClock / 1000:0.00} GHz at the end" : "") +
                            (fanMax is float f ? $"; CPU fan up to {f:N0} RPM" : "") + ".";
            if (fanMax is float f0 && f0 <= 0)
                items.Add(Item(FindingLevel.Warning, "CPU fan never spun up under load", detail + " Check that the CPU fan is plugged into the CPU_FAN header and nothing blocks it."));
            else if (cm >= 95 || (drop > 0.15 && cm >= 90))
                items.Add(Item(FindingLevel.Warning, drop > 0.15 ? $"Processor overheats and slows down ({drop:P0} slower)" : "Processor runs too hot under load",
                    detail + " The cooler isn't keeping up: blow the dust out of the heatsink and fans, check the cooler is tight, and replace dried thermal paste."));
            else if (cm >= 88)
                items.Add(Item(FindingLevel.Tip, "Processor runs warm under full load", detail + " Within limits, but cleaning the dust out and fresh thermal paste would give it headroom."));
            else if (drop > 0.25)
                items.Add(Item(FindingLevel.Tip, $"Processor slowed down {drop:P0} without being hot", detail + " That's power limits, not heat: check the BIOS power settings and that the 4/8-pin CPU power plug is in firmly."));
            else
                items.Add(Item(FindingLevel.Good, "Processor cooling is good", detail));
        }
        else
            items.Add(Item(FindingLevel.Tip, "No processor temperature during the test", AppEnv.IsAdmin()
                ? "Turn on CPU temperature (button at the top) and run the test again for the full picture."
                : "Run as admin for temperatures, clocks and voltages, then run the test again."));

        // Graphics card
        if (useGpu)
        {
            var gMax = L.Max(x => x.GpuTemp);
            var gPow = L.Max(x => x.GpuPower);
            if (gpuError != null)
                items.Add(Item(FindingLevel.Tip, "Graphics card wasn't loaded", "Couldn't run work on it: " + gpuError));
            else if (gMax is float g)
            {
                string d = $"Graphics card reached {Temp.Full(g, "0")}" + (gPow is float gp ? $", drawing up to {gp:0} W" : "") + ".";
                items.Add(g >= 88 ? Item(FindingLevel.Warning, "Graphics card runs too hot under load", d + " Clean the dust out of its cooler and check its fans spin.")
                        : g >= 83 ? Item(FindingLevel.Tip, "Graphics card runs warm under load", d + " Fine, but cleaning its cooler would help.")
                        : Item(FindingLevel.Good, "Graphics card cooling is good", d));
            }
        }

        // Power-supply rails under load
        var railParts = new List<string>();
        var level = FindingLevel.Good;
        foreach (var rail in new[] { Rail.V12, Rail.V5, Rail.V33 })
        {
            var vals = L.Select(x => RailOf(x, rail)).Where(v => v != null).Select(v => v!.Value).ToList();
            if (vals.Count == 0) continue;
            var spec = RailSpec(rail);
            float lo = vals.Min(), hi = vals.Max();
            float? rest = RailOf(idleS, rail);
            railParts.Add($"{spec.Label}: {(rest is float r ? $"{r:0.00} V at rest, " : "")}{lo:0.00}-{hi:0.00} V under load");
            if (lo < spec.Low || hi > spec.High) level = FindingLevel.Warning;
            else if (rest is float r2 && r2 - lo > spec.Nominal * 0.04 && level == FindingLevel.Good) level = FindingLevel.Tip;
        }
        if (railParts.Count > 0)
            items.Add(Item(level,
                level == FindingLevel.Warning ? "Power supply voltage dropped out of range under load" : level == FindingLevel.Tip ? "Power supply voltage sags under load" : "Power supply held steady under load",
                string.Join("; ", railParts) + ". " + (level switch
                {
                    FindingLevel.Warning => "The ATX standard allows ±5%. A rail outside that under load points at a tired or undersized power supply. Board sensors can be a little off, so confirm with a multimeter or a known-good power supply.",
                    FindingLevel.Tip => "Still inside the ±5% the ATX standard allows, but the dip is bigger than a healthy supply usually shows. Keep an eye on it.",
                    _ => "Inside the ±5% the ATX standard allows the whole time.",
                })));
        else
            items.Add(Item(FindingLevel.Tip, "Power supply voltages weren't readable during the test",
                "This board doesn't report usable rail voltages, so the power supply is judged by whether the PC stayed up under full load."));

        return items;
    }

    private static float? RailOf(Sample s, Rail rail) => rail switch { Rail.V12 => s.V12, Rail.V5 => s.V5, _ => s.V33 };

    private static string Clock(double secs) => $"{(int)secs / 60}:{(int)secs % 60:00}";

    /// <summary>Keeps one processor thread busy with vector floating-point and integer work until stopped.</summary>
    private static void Burn(CancellationToken ct)
    {
        var m = new Vector<double>(0.9999);
        var k = new Vector<double>(0.0001);
        Vector<double> a = Vector<double>.One, b = Vector<double>.One * 2, c = Vector<double>.One * 3, d = Vector<double>.One * 4;
        ulong x = 88172645463325252UL;
        while (!ct.IsCancellationRequested)
        {
            for (int i = 0; i < 1_000_000; i++)
            {
                a = a * m + k; b = b * m + k; c = c * m + k; d = d * m + k;
                x ^= x << 13; x ^= x >> 7; x ^= x << 17;
            }
            if (a[0] + b[0] + c[0] + d[0] + x == 0) Thread.Yield(); // never true; stops the work being optimized away
        }
    }
}
