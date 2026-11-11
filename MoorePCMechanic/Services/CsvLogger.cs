using System.IO;
using System.Globalization;
using System.Text;

namespace MoorePCMechanic.Services;

/// <summary>Writes one row per second of every sensor to a CSV you can open in Excel or Sheets.</summary>
public sealed class CsvLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly List<string> _keys;
    private int _rows;

    public string FilePath { get; }
    public int Rows => _rows;

    public CsvLogger(IReadOnlyList<SensorReading> firstSample)
    {
        FilePath = Path.Combine(AppEnv.SubFolder("Logs"), $"sensors-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv");
        _writer = new StreamWriter(FilePath, false, new UTF8Encoding(true));
        var unique = firstSample.GroupBy(r => r.Key).Select(g => g.First()).ToList();
        _keys = unique.Select(r => r.Key).ToList();
        var header = new StringBuilder("Time");
        foreach (var r in unique)
        {
            var unit = SensorService.Unit(r.Type);
            header.Append(',').Append(Escape($"{r.Hardware} | {r.Name}{(unit.Length > 0 ? $" ({unit})" : "")}"));
        }
        _writer.WriteLine(header.ToString());
    }

    public void Write(IReadOnlyList<SensorReading> sample)
    {
        var map = new Dictionary<string, float?>();
        foreach (var r in sample) map[r.Key] = r.Value;
        var line = new StringBuilder(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        foreach (var k in _keys)
        {
            line.Append(',');
            if (map.TryGetValue(k, out var v) && v is float f && !float.IsNaN(f))
                line.Append(f.ToString("0.###", CultureInfo.InvariantCulture));
        }
        _writer.WriteLine(line.ToString());
        _rows++;
        if (_rows % 10 == 0) _writer.Flush();
    }

    private static string Escape(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public void Dispose()
    {
        try { _writer.Flush(); _writer.Dispose(); } catch { }
    }
}
