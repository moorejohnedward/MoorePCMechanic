using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MoorePCMechanic.ViewModels;

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

/// <summary>One row in the Audio & calls device list.</summary>
public sealed class AudioDeviceRow : Observable
{
    public AudioDeviceRow(MoorePCMechanic.Services.AudioDeviceInfo info, bool isPreferred)
    {
        Info = info;
        IsPreferred = isPreferred;
    }

    public MoorePCMechanic.Services.AudioDeviceInfo Info { get; }
    public string Name => Info.Name;
    public string KindText => Info.KindText;
    public string StateText => Info.StateText;
    public string DefaultText => Info.DefaultText;
    public bool IsPreferred { get; }
    public string RoleText => IsPreferred ? "Your headset" : "";
    public string ToggleText => Info.IsActive ? "Turn off" : "Turn on";
    public bool CanToggle => !IsPreferred && Info.State != NAudio.CoreAudioApi.DeviceState.Unplugged;

    private double _level;
    public double Level { get => _level; set => Set(ref _level, value); }
}
