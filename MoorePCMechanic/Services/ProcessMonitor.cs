using System.Diagnostics;

namespace MoorePCMechanic.Services;

public sealed record ProcessGroup(string Name, int Count, double CpuPercent, double MemoryMB)
{
    public string CountText => Count > 1 ? $"x{Count}" : "";
    public string CpuText => $"{CpuPercent:0.0} %";
    public string MemoryText => MemoryMB >= 1024 ? $"{MemoryMB / 1024:0.00} GB" : $"{MemoryMB:0} MB";
}

/// <summary>Samples per-process CPU and memory, grouped by program name (all the chrome.exe's together).</summary>
public sealed class ProcessMonitor
{
    private Dictionary<int, (TimeSpan Cpu, DateTime When)> _previous = new();
    private readonly int _cores = Environment.ProcessorCount;

    public List<ProcessGroup> Sample()
    {
        var now = DateTime.UtcNow;
        var next = new Dictionary<int, (TimeSpan Cpu, DateTime When)>();
        var groups = new Dictionary<string, (int Count, double Cpu, double Mem)>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == 0) continue; // System Idle Process
                var name = p.ProcessName;
                double mem = p.WorkingSet64 / 1048576.0;
                double cpuPct = 0;
                try
                {
                    var cpu = p.TotalProcessorTime;
                    next[p.Id] = (cpu, now);
                    if (_previous.TryGetValue(p.Id, out var prev))
                    {
                        var ms = (now - prev.When).TotalMilliseconds;
                        if (ms > 0) cpuPct = (cpu - prev.Cpu).TotalMilliseconds / ms / _cores * 100.0;
                    }
                }
                catch
                {
                    // Protected process (needs admin) - still count its memory.
                }

                groups.TryGetValue(name, out var g);
                groups[name] = (g.Count + 1, g.Cpu + Math.Max(0, cpuPct), g.Mem + mem);
            }
            catch
            {
                // Process exited while we looked at it.
            }
            finally
            {
                p.Dispose();
            }
        }

        _previous = next;
        return groups.Select(kv => new ProcessGroup(kv.Key, kv.Value.Count, Math.Min(100, kv.Value.Cpu), kv.Value.Mem)).ToList();
    }
}
