using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MoorePCMechanic.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

/// <summary>One row in the Live Monitor sensor grid.</summary>
public sealed class SensorRow : Observable
{
    public string Key { get; init; } = "";
    public string Hardware { get; init; } = "";
    public string Name { get; init; } = "";
    public string TypeName { get; init; } = "";

    private string _value = "—";
    public string Value { get => _value; set => Set(ref _value, value); }

    private string _min = "—";
    public string Min { get => _min; set => Set(ref _min, value); }

    private string _max = "—";
    public string Max { get => _max; set => Set(ref _max, value); }
}

/// <summary>A big dashboard tile (CPU, GPU, RAM...).</summary>
public sealed class Card : Observable
{
    public Card(string title, string glyph)
    {
        Title = title;
        Glyph = glyph;
    }

    public string Title { get; }
    public string Glyph { get; }

    private string _value = "—";
    public string Value { get => _value; set => Set(ref _value, value); }

    private string _detail = "";
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    private double _percent;
    public double Percent { get => _percent; set => Set(ref _percent, value); }

    /// <summary>0 = normal, 1 = warm, 2 = hot. Drives the value color.</summary>
    private int _heat;
    public int Heat { get => _heat; set => Set(ref _heat, value); }
}
