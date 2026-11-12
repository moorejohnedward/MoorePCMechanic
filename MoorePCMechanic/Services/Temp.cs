using System.IO;
using System.Text.Json;

namespace MoorePCMechanic.Services;

public enum TempUnit { Celsius, Fahrenheit, Kelvin, All }

/// <summary>
/// Shows temperatures in Celsius, Fahrenheit, Kelvin or all three (picked at the top of the window).
/// Sensors always report Celsius; this converts only for display, so warnings and history stay in °C.
/// </summary>
public static class Temp
{
    private static readonly string File_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Moore PC Mechanic", "display-settings.json");

    private sealed class Settings { public TempUnit Unit { get; set; } = TempUnit.All; }

    private static TempUnit? _unit;

    public static TempUnit Unit
    {
        get
        {
            if (_unit is TempUnit u) return u;
            try { _unit = JsonSerializer.Deserialize<Settings>(File.ReadAllText(File_))?.Unit ?? TempUnit.All; }
            catch { _unit = TempUnit.All; }
            return _unit.Value;
        }
        set
        {
            _unit = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
                File.WriteAllText(File_, JsonSerializer.Serialize(new Settings { Unit = value }));
            }
            catch { }
        }
    }

    public static double ToF(double c) => c * 9 / 5 + 32;
    public static double ToK(double c) => c + 273.15;

    /// <summary>Short form for big dashboard numbers, e.g. "65°C", "149°F" or "338 K". "All" shows °C here (the others go in the detail line).</summary>
    public static string Short(double c) => Unit switch
    {
        TempUnit.Fahrenheit => $"{ToF(c):0}°F",
        TempUnit.Kelvin => $"{ToK(c):0} K",
        _ => $"{c:0}°C",
    };

    /// <summary>The other units for a dashboard detail line when "All" is picked, e.g. "149°F / 338 K"; otherwise null.</summary>
    public static string? Others(double c) => Unit == TempUnit.All ? $"{ToF(c):0}°F / {ToK(c):0} K" : null;

    /// <summary>Full form for tables and notes, e.g. "65.0 °C · 149.0 °F · 338.2 K".</summary>
    public static string Full(double c, string fmt = "0.0") => Unit switch
    {
        TempUnit.Fahrenheit => ToF(c).ToString(fmt) + " °F",
        TempUnit.Kelvin => ToK(c).ToString(fmt) + " K",
        TempUnit.All => c.ToString(fmt) + " °C · " + ToF(c).ToString(fmt) + " °F · " + ToK(c).ToString(fmt) + " K",
        _ => c.ToString(fmt) + " °C",
    };
}
