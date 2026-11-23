// Moore PC Mechanic - quick memory test
// Borrows most of the free memory, writes known patterns into it and reads them back. Any bit that
// comes back different is a memory error. Patterns: all zeros, all ones, two checkerboards, each
// word's own address, and pseudo-random data. It can only test memory Windows isn't using, so the
// "Full test at restart" (Windows Memory Diagnostic) is still the deepest check.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MoorePCMechanic.Services;

public sealed record MemTestResult(long BytesTested, long Errors, int Passes, TimeSpan Elapsed, bool Cancelled, string? Note);

public static class MemoryTest
{
    private const long Block = 256L << 20; // 256 MB pieces, tested in parallel
    private static readonly string[] PatternNames = { "zeros", "ones", "checkerboard", "inverse checkerboard", "address", "random" };

    /// <param name="maxBytes">Upper limit (the smoke test uses a small one). 0 = as much as is safely free.</param>
    public static unsafe MemTestResult Run(int passes, IProgress<string> progress, CancellationToken ct, long maxBytes = 0)
    {
        var sw = Stopwatch.StartNew();
        var (total, used, _) = AppEnv.GetMemory();
        long free = (long)(total - used);
        // Leave Windows and other programs room: 75% of what's free, minus 1 GB, at most 48 GB.
        long target = (long)Math.Min(free * 0.75 - (1L << 30), 48L << 30);
        if (maxBytes > 0) target = Math.Min(target, maxBytes);
        if (target < Block) return new MemTestResult(0, 0, 0, sw.Elapsed, false, "Not enough free memory to test. Close some programs and try again.");

        var blocks = new List<nint>();
        try
        {
            progress.Report($"Borrowing {AppEnv.Bytes(target)} of free memory…");
            while ((long)blocks.Count * Block < target)
            {
                try { blocks.Add((nint)NativeMemory.AlignedAlloc((nuint)Block, 4096)); }
                catch (OutOfMemoryException) { break; }
            }
            if (blocks.Count == 0) return new MemTestResult(0, 0, 0, sw.Elapsed, false, "Windows wouldn't lend any memory for the test.");

            long words = Block / 8;
            long errors = 0;
            int done = 0, steps = passes * PatternNames.Length;
            var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct };
            for (int pass = 0; pass < passes; pass++)
            {
                for (int kind = 0; kind < PatternNames.Length; kind++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress.Report($"Pass {pass + 1} of {passes}: {PatternNames[kind]} pattern ({++done} of {steps}) · {AppEnv.Bytes(blocks.Count * Block)} · errors so far: {errors}");
                    ulong seed = 0x9E3779B97F4A7C15UL * (ulong)(pass * 16 + kind + 1);
                    Parallel.For(0, blocks.Count, opts, b => Fill((ulong*)blocks[b], words, kind, seed + (ulong)b));
                    Parallel.For(0, blocks.Count, opts, b =>
                    {
                        long bad = Verify((ulong*)blocks[b], words, kind, seed + (ulong)b);
                        if (bad > 0) Interlocked.Add(ref errors, bad);
                    });
                    if (errors > 1_000_000) break; // clearly broken; no need to keep counting
                }
            }
            return new MemTestResult(blocks.Count * Block, errors, passes, sw.Elapsed, false, null);
        }
        catch (OperationCanceledException)
        {
            return new MemTestResult(blocks.Count * Block, 0, 0, sw.Elapsed, true, null);
        }
        finally
        {
            foreach (var p in blocks) NativeMemory.AlignedFree((void*)p);
        }
    }

    private static unsafe void Fill(ulong* p, long n, int kind, ulong seed)
    {
        switch (kind)
        {
            case 0: case 1:
            {
                ulong v = kind == 0 ? 0UL : ~0UL;
                for (long i = 0; i < n; i++) p[i] = v;
                break;
            }
            case 2: case 3:
            {
                ulong a = kind == 2 ? 0x5555555555555555UL : 0xAAAAAAAAAAAAAAAAUL, b = ~a;
                for (long i = 0; i < n; i++) p[i] = (i & 1) == 0 ? a : b;
                break;
            }
            case 4:
                for (long i = 0; i < n; i++) p[i] = (ulong)(p + i) ^ seed;
                break;
            default:
            {
                ulong x = seed | 1;
                for (long i = 0; i < n; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; p[i] = x; }
                break;
            }
        }
    }

    private static unsafe long Verify(ulong* p, long n, int kind, ulong seed)
    {
        long bad = 0;
        switch (kind)
        {
            case 0: case 1:
            {
                ulong v = kind == 0 ? 0UL : ~0UL;
                for (long i = 0; i < n; i++) if (p[i] != v) bad++;
                break;
            }
            case 2: case 3:
            {
                ulong a = kind == 2 ? 0x5555555555555555UL : 0xAAAAAAAAAAAAAAAAUL, b = ~a;
                for (long i = 0; i < n; i++) if (p[i] != ((i & 1) == 0 ? a : b)) bad++;
                break;
            }
            case 4:
                for (long i = 0; i < n; i++) if (p[i] != ((ulong)(p + i) ^ seed)) bad++;
                break;
            default:
            {
                ulong x = seed | 1;
                for (long i = 0; i < n; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; if (p[i] != x) bad++; }
                break;
            }
        }
        return bad;
    }

    /// <summary>Turns a result into a line for the System check list.</summary>
    public static SecurityItem ToItem(MemTestResult r)
    {
        var installed = AppEnv.GetMemory().Total;
        string size = AppEnv.Bytes(r.BytesTested);
        string time = r.Elapsed.TotalMinutes >= 1 ? $"{(int)r.Elapsed.TotalMinutes} min {r.Elapsed.Seconds} s" : $"{r.Elapsed.TotalSeconds:0} s";
        if (r.Note != null) return HardwareCheckService.Item(FindingLevel.Tip, "Quick memory test didn't run", r.Note);
        if (r.Cancelled) return HardwareCheckService.Item(FindingLevel.Tip, "Quick memory test was stopped", "Run it again and let it finish.");
        if (r.Errors > 0)
            return HardwareCheckService.Item(FindingLevel.Warning, $"Memory test FOUND {r.Errors:N0} ERROR(S)",
                $"Tested {size} in {time}. Memory that changes what's stored in it causes crashes and corrupted files. " +
                "1) If XMP/EXPO is on in the BIOS, turn it off and test again. 2) Reseat the sticks. 3) Test one stick at a time to find the bad one, and replace it.",
                "mdsched", "Full test at restart");
        return HardwareCheckService.Item(FindingLevel.Good, $"Quick memory test passed ({DateTime.Now:HHmm})",
            $"No errors in {size} of {AppEnv.Bytes(installed)} ({r.Passes} pass{(r.Passes == 1 ? "" : "es")}, 6 patterns each, {time}). The rest was in use by Windows and programs; the full test at restart covers every byte.");
    }
}
