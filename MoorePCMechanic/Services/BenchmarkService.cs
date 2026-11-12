// Moore PC Mechanic - benchmarks
// Repeatable tests for the parts you upgrade: processor (one core and all cores), memory
// (bandwidth and latency), a drive (sequential and random), and the graphics card
// (compute throughput and video-memory bandwidth, see GpuBenchmark.cs).
// Every run is saved with a date and a label so you can compare before and after an upgrade.

using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MoorePCMechanic.Services;

public sealed class BenchmarkRun
{
    public DateTime Date { get; set; } = DateTime.Now;
    public string Label { get; set; } = "";
    public string AppVersion { get; set; } = "";

    // what the PC was when it ran
    public string Cpu { get; set; } = "";
    public string Memory { get; set; } = "";
    public string Gpu { get; set; } = "";
    public string GpuDriver { get; set; } = "";

    // results (null = not run)
    public double? CpuSingle { get; set; }
    public double? CpuMulti { get; set; }
    public int CpuThreads { get; set; }
    public double? MemReadGBs { get; set; }
    public double? MemCopyGBs { get; set; }
    public double? MemLatencyNs { get; set; }
    public string DiskDrive { get; set; } = "";
    public double? DiskSeqReadMBs { get; set; }
    public double? DiskSeqWriteMBs { get; set; }
    public double? Disk4kReadMBs { get; set; }
    public double? Disk4kIops { get; set; }
    public double? Disk4kMultiIops { get; set; }
    public double? GpuTflops { get; set; }
    public double? GpuMemGBs { get; set; }

    // heat check during the all-core test
    public double? MaxCpuTemp { get; set; }
    public double? ClockStartMHz { get; set; }
    public double? ClockEndMHz { get; set; }
    public string Notes { get; set; } = "";

    public string DateText => Date.ToString("yyyy-MM-dd HH:mm");
    public string CpuSingleText => Fmt(CpuSingle, "0");
    public string CpuMultiText => Fmt(CpuMulti, "0");
    public string MemReadText => Fmt(MemReadGBs, "0.0", " GB/s");
    public string DiskReadText => Fmt(DiskSeqReadMBs, "0", " MB/s");
    public string GpuText => Fmt(GpuTflops, "0.0", " TFLOPS");

    private static string Fmt(double? v, string f, string unit = "") => v is double d ? d.ToString(f) + unit : "—";
}

/// <summary>One line of the results table: a test, this run's number, the comparison run's number and the change.</summary>
public sealed class BenchmarkLine
{
    public string Test { get; init; } = "";
    public string Now { get; init; } = "";
    public string Before { get; init; } = "";
    public string Change { get; init; } = "";
    public int Better { get; init; } // 1 better, -1 worse, 0 same/unknown
    public string Hint { get; init; } = "";
}

public sealed class BenchmarkOptions
{
    public bool Cpu { get; init; } = true;
    public bool Memory { get; init; } = true;
    public bool Disk { get; init; } = true;
    public string? DiskRoot { get; init; }
    public bool Gpu { get; init; } = true;
    public string Label { get; init; } = "";
}

public static class BenchmarkService
{
    private static string HistoryFile => Path.Combine(AppEnv.DataFolder, "benchmarks.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // =====================================================================
    //  History
    // =====================================================================

    public static List<BenchmarkRun> LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryFile)) return new();
            return JsonSerializer.Deserialize<List<BenchmarkRun>>(File.ReadAllText(HistoryFile)) ?? new();
        }
        catch { return new(); }
    }

    public static void SaveHistory(List<BenchmarkRun> runs)
    {
        try { File.WriteAllText(HistoryFile, JsonSerializer.Serialize(runs.OrderBy(r => r.Date).ToList(), JsonOpts)); }
        catch { }
    }

    public static string HistoryFolder => AppEnv.DataFolder;

    /// <summary>Lines for the results table, comparing <paramref name="now"/> with <paramref name="before"/> (may be null).</summary>
    public static List<BenchmarkLine> Compare(BenchmarkRun now, BenchmarkRun? before)
    {
        var lines = new List<BenchmarkLine>();
        void Add(string test, double? a, double? b, string fmt, string unit, bool higherIsBetter, string hint)
        {
            if (a is null) return;
            string change = "";
            int better = 0;
            if (b is double bv && bv > 0)
            {
                double pct = (a.Value - bv) / bv * 100;
                if (Math.Abs(pct) < 3) change = "about the same";
                else
                {
                    better = (pct > 0) == higherIsBetter ? 1 : -1;
                    change = (pct > 0 ? "+" : "") + pct.ToString("0") + "%";
                }
            }
            lines.Add(new BenchmarkLine
            {
                Test = test,
                Now = a.Value.ToString(fmt) + unit,
                Before = b is double x ? x.ToString(fmt) + unit : "—",
                Change = change,
                Better = better,
                Hint = hint,
            });
        }
        Add("Processor: one core", now.CpuSingle, before?.CpuSingle, "0", " pts", true, "How fast a single task runs: games, web pages, most apps.");
        Add($"Processor: all {now.CpuThreads} threads", now.CpuMulti, before?.CpuMulti, "0", " pts", true, "Heavy work spread across cores: video export, AI, compiling.");
        Add("Memory: read speed", now.MemReadGBs, before?.MemReadGBs, "0.0", " GB/s", true, "Dual-channel memory roughly doubles this.");
        Add("Memory: copy speed", now.MemCopyGBs, before?.MemCopyGBs, "0.0", " GB/s", true, "Read and write together.");
        Add("Memory: latency", now.MemLatencyNs, before?.MemLatencyNs, "0.0", " ns", false, "Lower is better. Tighter timings and higher speed lower it.");
        Add($"Drive {now.DiskDrive}: big file read", now.DiskSeqReadMBs, before?.DiskSeqReadMBs, "0", " MB/s", true, "Loading big files and games.");
        Add($"Drive {now.DiskDrive}: big file write", now.DiskSeqWriteMBs, before?.DiskSeqWriteMBs, "0", " MB/s", true, "Saving and copying big files.");
        Add($"Drive {now.DiskDrive}: small random reads", now.Disk4kIops, before?.Disk4kIops, "0", " IOPS", true, "How snappy Windows and apps feel (one request at a time).");
        Add($"Drive {now.DiskDrive}: small reads, many at once", now.Disk4kMultiIops, before?.Disk4kMultiIops, "0", " IOPS", true, "Busy multitasking and game loading.");
        Add("Graphics: compute", now.GpuTflops, before?.GpuTflops, "0.0", " TFLOPS", true, "Raw shader math: games, AI, rendering.");
        Add("Graphics: video memory speed", now.GpuMemGBs, before?.GpuMemGBs, "0", " GB/s", true, "Higher resolutions and textures lean on this.");
        return lines;
    }

    // =====================================================================
    //  Running
    // =====================================================================

    /// <summary>Runs the chosen tests. <paramref name="sampleCpu"/> returns the current CPU (temperature °C, clock MHz) when sensors have them.</summary>
    public static BenchmarkRun Run(BenchmarkOptions o, Func<(float? Temp, float? Clock)> sampleCpu,
                                   IProgress<(double Fraction, string Text)> progress, CancellationToken ct)
    {
        var run = new BenchmarkRun { Label = o.Label, AppVersion = UpdateService.CurrentVersion.ToString(), CpuThreads = Environment.ProcessorCount };
        var steps = new List<(string Name, double Weight, Action Act)>();

        if (o.Cpu)
        {
            steps.Add(("Processor: one core", 6, () => run.CpuSingle = CpuScore(1, 5, ct)));
            steps.Add(("Processor: all threads (also checks for overheating)", 12, () => CpuMultiWithHeat(run, sampleCpu, ct)));
        }
        if (o.Memory)
        {
            steps.Add(("Memory: bandwidth", 5, () => MemoryBandwidth(run, ct)));
            steps.Add(("Memory: latency", 4, () => run.MemLatencyNs = MemoryLatency(ct)));
        }
        if (o.Disk && !string.IsNullOrEmpty(o.DiskRoot))
            steps.Add(("Drive " + o.DiskRoot, 14, () => DiskTest(run, o.DiskRoot!, p => progress.Report((-1, p)), ct)));
        if (o.Gpu)
            steps.Add(("Graphics card", 8, () =>
            {
                var g = GpuBenchmark.Run(ct);
                run.Gpu = g.Adapter;
                run.GpuTflops = g.Tflops;
                run.GpuMemGBs = g.MemGBs;
                if (g.Error != null) run.Notes = (run.Notes + " Graphics test: " + g.Error).Trim();
            }));

        double total = steps.Sum(s => s.Weight), done = 0;
        foreach (var (name, weight, act) in steps)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report((done / total, name + "…"));
            act();
            done += weight;
        }
        progress.Report((1, "Done"));
        return run;
    }

    // ---------------------------------------------------------------------
    //  Processor
    // ---------------------------------------------------------------------

    /// <summary>Work units per second × 10 on <paramref name="threads"/> threads (warm-up first). Higher is better.</summary>
    private static double CpuScore(int threads, double seconds, CancellationToken ct, Action<double>? perSecond = null)
    {
        RunCpuFor(threads, 0.8, ct, null); // warm up: let the CPU reach full clock
        return RunCpuFor(threads, seconds, ct, perSecond);
    }

    private static double RunCpuFor(int threads, double seconds, CancellationToken ct, Action<double>? perSecond)
    {
        long units = 0;
        var stop = false;
        var workers = new Thread[threads];
        var sw = Stopwatch.StartNew();
        for (int t = 0; t < threads; t++)
        {
            int seed = t + 1;
            workers[t] = new Thread(() =>
            {
                var data = new uint[16 * 1024]; // 64 KB: stays in the CPU's own cache, so this measures the core, not memory
                for (int i = 0; i < data.Length; i++) data[i] = (uint)(i * 2654435761u + (uint)seed);
                long mine = 0;
                double sink = 0;
                while (!Volatile.Read(ref stop))
                {
                    sink += WorkUnit(data, seed);
                    mine++;
                }
                Interlocked.Add(ref units, mine);
                if (sink == 42.4242) Console.Write(""); // keeps the compiler from skipping the work
            }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            workers[t].Start();
        }
        int lastSecond = 0;
        while (sw.Elapsed.TotalSeconds < seconds && !ct.IsCancellationRequested)
        {
            Thread.Sleep(100);
            int s = (int)sw.Elapsed.TotalSeconds;
            if (s != lastSecond) { lastSecond = s; perSecond?.Invoke(sw.Elapsed.TotalSeconds); }
        }
        Volatile.Write(ref stop, true);
        foreach (var w in workers) w.Join();
        ct.ThrowIfCancellationRequested();
        return units / sw.Elapsed.TotalSeconds * 10;
    }

    /// <summary>One unit of mixed work (about a millisecond): integer hashing, branching and floating-point math, like real apps.</summary>
    private static double WorkUnit(uint[] data, int seed)
    {
        // integer: hash and shuffle the table
        uint x = (uint)seed * 0x9E3779B9u;
        int mask = data.Length - 1;
        for (int i = 0; i < 4096; i++)
        {
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            int j = (int)(x & (uint)mask);
            uint v = data[j];
            data[j] = (v ^ x) * 0x85EBCA6Bu + (uint)i;
            if ((v & 3) == 0) x += v; else x -= v >> 3;
        }
        // floating point: a small Mandelbrot tile
        double acc = 0;
        for (int py = 0; py < 16; py++)
        {
            for (int px = 0; px < 16; px++)
            {
                double cr = -2.0 + px * 0.15, ci = -1.2 + py * 0.15, zr = 0, zi = 0;
                int it = 0;
                while (it < 64 && zr * zr + zi * zi < 4.0)
                {
                    double t = zr * zr - zi * zi + cr;
                    zi = 2 * zr * zi + ci;
                    zr = t;
                    it++;
                }
                acc += it;
            }
        }
        return acc + x;
    }

    private static void CpuMultiWithHeat(BenchmarkRun run, Func<(float? Temp, float? Clock)> sample, CancellationToken ct)
    {
        int threads = Environment.ProcessorCount;
        var clocks = new List<float>();
        float maxTemp = float.MinValue;
        run.CpuMulti = CpuScore(threads, 12, ct, _ =>
        {
            var (t, c) = sample();
            if (t is float tv && tv > maxTemp) maxTemp = tv;
            if (c is float cv && cv > 0) clocks.Add(cv);
        });
        if (maxTemp > float.MinValue) run.MaxCpuTemp = maxTemp;
        if (clocks.Count >= 6)
        {
            run.ClockStartMHz = clocks.Take(3).Average();
            run.ClockEndMHz = clocks.Skip(clocks.Count - 3).Average();
            if (run.ClockEndMHz < run.ClockStartMHz * 0.88)
                run.Notes = (run.Notes + $" The processor slowed from {run.ClockStartMHz:0} to {run.ClockEndMHz:0} MHz under full load, a sign of heat or power limits (throttling).").Trim();
        }
        if (run.MaxCpuTemp >= 95)
            run.Notes = (run.Notes + $" The processor reached {run.MaxCpuTemp:0} °C. Check the cooler, fans and case airflow.").Trim();
    }

    // ---------------------------------------------------------------------
    //  Memory
    // ---------------------------------------------------------------------

    private const int MemBytes = 512 * 1024 * 1024; // far bigger than any CPU cache

    private static unsafe void MemoryBandwidth(BenchmarkRun run, CancellationToken ct)
    {
        int threads = Math.Max(2, Math.Min(Environment.ProcessorCount, 16));
        byte* src = (byte*)NativeMemory.AlignedAlloc(MemBytes, 4096);
        byte* dst = (byte*)NativeMemory.AlignedAlloc(MemBytes, 4096);
        nint sP = (nint)src, dP = (nint)dst; // lambdas below use these addresses
        try
        {
            // touch every page so Windows really hands over the memory before timing
            Parallel.For(0, threads, t =>
            {
                long chunk = MemBytes / threads;
                new Span<byte>((byte*)sP + t * chunk, (int)chunk).Fill((byte)t);
                new Span<byte>((byte*)dP + t * chunk, (int)chunk).Clear();
            });

            double bestRead = 0, bestCopy = 0;
            for (int pass = 0; pass < 4; pass++)
            {
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                long sink = 0;
                Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
                {
                    long chunk = MemBytes / threads;
                    var span = MemoryMarshal.Cast<byte, Vector<long>>(new Span<byte>((byte*)sP + t * chunk, (int)chunk));
                    var acc = Vector<long>.Zero;
                    for (int i = 0; i < span.Length; i++) acc += span[i];
                    Interlocked.Add(ref sink, Vector.Sum(acc));
                });
                double read = MemBytes / sw.Elapsed.TotalSeconds / 1e9;
                if (sink == 1) Console.Write("");
                bestRead = Math.Max(bestRead, read);

                sw.Restart();
                Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
                {
                    long chunk = MemBytes / threads;
                    Buffer.MemoryCopy((byte*)sP + t * chunk, (byte*)dP + t * chunk, chunk, chunk);
                });
                double copy = 2.0 * MemBytes / sw.Elapsed.TotalSeconds / 1e9; // every byte read once and written once
                bestCopy = Math.Max(bestCopy, copy);
            }
            run.MemReadGBs = bestRead;
            run.MemCopyGBs = bestCopy;
        }
        finally
        {
            NativeMemory.AlignedFree(src);
            NativeMemory.AlignedFree(dst);
        }
    }

    /// <summary>Average time for one random trip to memory (each step depends on the last, so the CPU can't guess ahead).</summary>
    private static double MemoryLatency(CancellationToken ct)
    {
        const int count = 64 * 1024 * 1024 / 8; // 64 MB of "next" links, one per 64-byte cache line is plenty random
        var next = new int[count];
        for (int i = 0; i < count; i++) next[i] = i;
        var rng = new Random(12345);
        for (int i = count - 1; i > 0; i--) // Sattolo: one big random loop through every slot
        {
            int j = rng.Next(i);
            (next[i], next[j]) = (next[j], next[i]);
        }
        ct.ThrowIfCancellationRequested();
        int p = 0;
        for (int i = 0; i < 1_000_000; i++) p = next[p]; // warm up
        const int steps = 20_000_000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < steps; i++) p = next[p];
        sw.Stop();
        if (p == -1) Console.Write("");
        return sw.Elapsed.TotalMilliseconds * 1e6 / steps;
    }

    // ---------------------------------------------------------------------
    //  Drive
    // ---------------------------------------------------------------------

    private const long DiskFileSize = 2L * 1024 * 1024 * 1024; // 2 GB: bigger than most drive caches
    private const int SeqBlock = 1024 * 1024;
    private const int RandBlock = 4096;

    /// <summary>Drives you can test (fixed drives with at least 3 GB free).</summary>
    public static List<(string Root, string Text)> TestableDrives()
    {
        var list = new List<(string, string)>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                if (d.AvailableFreeSpace < 3L * 1024 * 1024 * 1024) continue;
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : " " + d.VolumeLabel;
                list.Add((d.RootDirectory.FullName, $"{d.Name.TrimEnd('\\')}{label} ({AppEnv.Bytes(d.AvailableFreeSpace)} free)"));
            }
            catch { }
        }
        return list;
    }

    private static string TestFolder(string root)
    {
        // Windows doesn't let normal programs write files in the top of C:, so use the temp folder on the system drive.
        var sys = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        if (string.Equals(Path.GetPathRoot(root), sys, StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Path.GetTempPath(), "MoorePCMechanic");
        return Path.Combine(root, "MoorePCMechanic-benchmark");
    }

    private static unsafe void DiskTest(BenchmarkRun run, string root, Action<string> status, CancellationToken ct)
    {
        run.DiskDrive = root.TrimEnd('\\');
        var dir = TestFolder(root);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "speedtest.tmp");
        const FileOptions NoBuffering = (FileOptions)0x20000000; // skip Windows' file cache so we measure the drive itself
        int threads = 8;
        byte* buf = (byte*)NativeMemory.AlignedAlloc((nuint)(SeqBlock * threads), 4096);
        nint bP = (nint)buf;
        try
        {
            Random.Shared.NextBytes(new Span<byte>(buf, SeqBlock * threads)); // random data: drives can't cheat by compressing it

            using (var h = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                                           FileOptions.WriteThrough | NoBuffering, DiskFileSize))
            {
                // big sequential write: 8 workers each writing their own slice
                status("Drive: writing a 2 GB test file…");
                long slice = DiskFileSize / threads;
                var sw = Stopwatch.StartNew();
                Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
                {
                    var span = new ReadOnlySpan<byte>((byte*)bP + t * SeqBlock, SeqBlock);
                    for (long off = t * slice; off < (t + 1) * slice; off += SeqBlock)
                    {
                        if (ct.IsCancellationRequested) return;
                        RandomAccess.Write(h, span, off);
                    }
                });
                ct.ThrowIfCancellationRequested();
                run.DiskSeqWriteMBs = DiskFileSize / sw.Elapsed.TotalSeconds / (1024 * 1024);

                status("Drive: reading it back…");
                sw.Restart();
                Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
                {
                    var span = new Span<byte>((byte*)bP + t * SeqBlock, SeqBlock);
                    for (long off = t * slice; off < (t + 1) * slice; off += SeqBlock)
                    {
                        if (ct.IsCancellationRequested) return;
                        RandomAccess.Read(h, span, off);
                    }
                });
                ct.ThrowIfCancellationRequested();
                run.DiskSeqReadMBs = DiskFileSize / sw.Elapsed.TotalSeconds / (1024 * 1024);

                status("Drive: small random reads…");
                long ops = RandomReads(h, bP, 1, 4, ct);
                run.Disk4kIops = ops / 4.0;
                run.Disk4kReadMBs = run.Disk4kIops * RandBlock / (1024 * 1024);

                status("Drive: small random reads, many at once…");
                run.Disk4kMultiIops = RandomReads(h, bP, 16, 4, ct) / 4.0;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buf);
            try { File.Delete(path); } catch { }
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }
    }

    private static unsafe long RandomReads(SafeFileHandle h, nint buf, int threads, double seconds, CancellationToken ct)
    {
        long total = 0;
        long blocks = DiskFileSize / RandBlock;
        var deadline = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            var rng = new Random(1000 + t);
            var span = new Span<byte>((byte*)buf + (t % 8) * SeqBlock, RandBlock);
            long mine = 0;
            while (Stopwatch.GetTimestamp() < deadline && !ct.IsCancellationRequested)
            {
                RandomAccess.Read(h, span, rng.NextInt64(blocks) * RandBlock);
                mine++;
            }
            Interlocked.Add(ref total, mine);
        });
        ct.ThrowIfCancellationRequested();
        return total;
    }
}
