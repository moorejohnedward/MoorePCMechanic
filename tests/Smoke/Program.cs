using System.Diagnostics;
using MoorePCMechanic.Services;

int failures = 0;
void Step(string name, Action act)
{
    var sw = Stopwatch.StartNew();
    try { act(); Console.WriteLine($"PASS  {name}  ({sw.Elapsed.TotalSeconds:0.0}s)"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL  {name}: {ex}"); }
}

Step("Installed drivers", () =>
{
    var d = DriverService.ScanInstalled();
    Console.WriteLine($"      {d.Count} drivers; first non-Microsoft: " +
        string.Join(" | ", d.Where(x => !x.IsMicrosoft).Take(3).Select(x => $"{x.Device} [{x.ClassName}] {x.Version} {x.DateText} {x.AgeText}")));
    if (d.Count == 0) throw new Exception("no drivers listed");
});
Step("Problem devices", () => Console.WriteLine($"      {DriverService.ScanProblems().Count} problem device(s)"));
Step("NVIDIA version mapping", () =>
{
    var v = DriverService.NvidiaVersionFromWindows("32.0.15.6614");
    if (v != "566.14") throw new Exception("got " + v);
});
Step("NVIDIA lookup (GeForce RTX 4070)", () =>
{
    var st = DriverService.CheckNvidiaForAsync("NVIDIA GeForce RTX 4070", "32.0.15.6614").GetAwaiter().GetResult();
    Console.WriteLine($"      latest={st.LatestVersion} name={st.LatestName} date={st.LatestDate:yyyy-MM-dd} size={st.DownloadSize} url={st.DownloadUrl} error={st.Error}");
    if (st.Error != null) throw new Exception(st.Error);
});
Step("NVIDIA lookup (GeForce RTX 5070 Ti)", () =>
{
    var st = DriverService.CheckNvidiaForAsync("NVIDIA GeForce RTX 5070 Ti", "32.0.15.6614").GetAwaiter().GetResult();
    Console.WriteLine($"      latest={st.LatestVersion} error={st.Error}");
});
Step("Signature check on a Microsoft file", () =>
{
    var signer = DriverService.VerifySignature(Path.Combine(Environment.SystemDirectory, "ntoskrnl.exe"))
                 ?? DriverService.VerifySignature(Path.Combine(Environment.SystemDirectory, "notepad.exe"));
    Console.WriteLine("      signer: " + signer);
});
Step("Windows Update driver search", () =>
{
    var t = Task.Run(() => DriverService.SearchWindowsUpdate());
    if (!t.Wait(TimeSpan.FromMinutes(4))) { Console.WriteLine("      (still searching after 4 min, skipped)"); return; }
    Console.WriteLine($"      {t.Result.Count} update(s): " + string.Join(" | ", t.Result.Take(3).Select(u => u.Title)));
});

var progress = new Progress<(double, string)>(p => Console.WriteLine($"      .. {p.Item2}"));
var drive = BenchmarkService.TestableDrives().FirstOrDefault();
Console.WriteLine("Drives: " + string.Join(", ", BenchmarkService.TestableDrives().Select(d => d.Text)));
Step("Benchmarks (all)", () =>
{
    var r = BenchmarkService.Run(new BenchmarkOptions { Cpu = true, Memory = true, Gpu = true, Disk = drive.Root != null, DiskRoot = drive.Root, Label = "CI" },
                                 () => (null, null), progress, CancellationToken.None);
    foreach (var l in BenchmarkService.Compare(r, null)) Console.WriteLine($"      {l.Test,-48} {l.Now}");
    Console.WriteLine($"      GPU: '{r.Gpu}' notes: {r.Notes}");
    if (r.CpuSingle is not > 0 || r.CpuMulti is not > 0 || r.MemReadGBs is not > 0 || r.MemLatencyNs is not > 0) throw new Exception("missing results");
});

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
