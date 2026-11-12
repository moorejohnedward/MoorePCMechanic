using System.Globalization;
using LibreHardwareMonitor.Hardware;

namespace MoorePCMechanic.Services;

public sealed class SensorReading
{
    public string Key { get; init; } = "";
    /// <summary>Display group, e.g. "AMD Ryzen 7 7700" or "Nuvoton NCT6687D".</summary>
    public string Hardware { get; init; } = "";
    /// <summary>Type of the top-level device this sensor belongs to.</summary>
    public HardwareType RootType { get; init; }
    public string Name { get; init; } = "";
    public SensorType Type { get; init; }
    public float? Value { get; init; }
    public float? Min { get; init; }
    public float? Max { get; init; }
}

/// <summary>Live temperatures, clocks, loads, fans and power via LibreHardwareMonitor. Not thread-safe: call from one thread.</summary>
public sealed class SensorService : IDisposable
{
    private readonly Computer _computer;
    private bool _open;

    public string? StartupError { get; private set; }

    public SensorService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
        };
        try
        {
            _computer.Open();
            _open = true;
        }
        catch (Exception ex)
        {
            StartupError = ex.Message;
        }
    }

    public List<SensorReading> Read()
    {
        var list = new List<SensorReading>();
        if (!_open) return list;
        foreach (var hw in _computer.Hardware.ToList())
            Collect(hw, hw.HardwareType, list);
        return list;
    }

    private static void Collect(IHardware hw, HardwareType root, List<SensorReading> list)
    {
        try { hw.Update(); } catch { /* a device can fail one update; keep going */ }
        foreach (var s in hw.Sensors)
        {
            list.Add(new SensorReading
            {
                Key = s.Identifier.ToString(),
                Hardware = hw.Name,
                RootType = root,
                Name = s.Name,
                Type = s.SensorType,
                // A temperature of 0 °C (or below) means "no reading": the sensor exists but the
                // library can't read it, usually because the PawnIO driver isn't installed.
                Value = Real(s.SensorType, s.Value),
                Min = Real(s.SensorType, s.Min),
                Max = Real(s.SensorType, s.Max),
            });
        }
        foreach (var sub in hw.SubHardware)
            Collect(sub, root, list);
    }

    private static float? Real(SensorType t, float? v) =>
        t == SensorType.Temperature && v is float x && (x <= 0.5f || float.IsNaN(x)) ? null : v;

    public void ResetMinMax()
    {
        if (!_open) return;
        foreach (var hw in _computer.Hardware)
        {
            foreach (var s in hw.Sensors) s.ResetMin();
            foreach (var s in hw.Sensors) s.ResetMax();
            foreach (var sub in hw.SubHardware)
            {
                foreach (var s in sub.Sensors) s.ResetMin();
                foreach (var s in sub.Sensors) s.ResetMax();
            }
        }
    }

    public void Dispose()
    {
        if (_open)
        {
            try { _computer.Close(); } catch { }
            _open = false;
        }
    }

    // ---------- Formatting ----------

    public static string Unit(SensorType t) => t switch
    {
        SensorType.Temperature => "°C",
        SensorType.Load or SensorType.Control or SensorType.Level => "%",
        SensorType.Clock => "MHz",
        SensorType.Power => "W",
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Fan => "RPM",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.Frequency => "Hz",
        _ => "",
    };

    public static string Format(SensorType t, float? v)
    {
        if (v is not float x || float.IsNaN(x)) return "—";
        return t switch
        {
            SensorType.Temperature => Temp.Full(x),
            SensorType.Load or SensorType.Control or SensorType.Level => $"{x:0.0} %",
            SensorType.Clock => $"{x:0} MHz",
            SensorType.Power => $"{x:0.0} W",
            SensorType.Voltage => $"{x:0.000} V",
            SensorType.Current => $"{x:0.00} A",
            SensorType.Fan => $"{x:0} RPM",
            SensorType.Data => $"{x:0.00} GB",
            SensorType.SmallData => $"{x:0} MB",
            SensorType.Throughput => AppEnv.Bytes(x) + "/s",
            SensorType.Frequency => $"{x:0} Hz",
            _ => x.ToString("0.##", CultureInfo.CurrentCulture),
        };
    }

    public static string TypeName(SensorType t) => t switch
    {
        SensorType.SmallData => "Data (small)",
        _ => t.ToString(),
    };
}
