// Moore PC Mechanic - what kind of PC this is, so the tune-up picks the right fixes for it.
// A gaming desktop, an 8 GB all-in-one and a Snapdragon (ARM) laptop each need different things:
//   - Desktops that sleep the old way (S3) get the High performance power plan.
//   - Laptops, ARM PCs and "Modern Standby" PCs hide that plan; Windows' power mode
//     ("Best performance" in Settings > System > Power) is the lever there instead.
//   - Light PCs (8 GB or less, or 4 or fewer threads) also get the low-RAM extras:
//     memory compression, lighter visual effects, Edge sleeping tabs and a startup trim.
//   - GPU scheduling is only offered where there's a separate graphics card.
//   - On ARM, x64 programs run through emulation, so the app points those out.

using System.IO;
using System.Management;
using System.Runtime.InteropServices;

namespace MoorePCMechanic.Services;

public sealed class MachineProfile
{
    public bool IsArm64 { get; init; }
    /// <summary>This copy of the app is the x64 build running under emulation on an ARM PC.</summary>
    public bool AppEmulated { get; init; }
    public bool HasBattery { get; init; }
    public bool OnAcPower { get; init; } = true;
    /// <summary>Modern Standby (S0 low power idle): the High performance plan is hidden and the power mode slider is used instead.</summary>
    public bool ModernStandby { get; init; }
    public double RamGB { get; init; }
    public int Threads { get; init; }
    public bool DiscreteGpu { get; init; }
    public string GpuName { get; init; } = "";

    /// <summary>8 GB of RAM or less, or 4 threads or fewer: gets the low-RAM extras.</summary>
    public bool IsLight => (RamGB > 0 && RamGB <= 8.5) || Threads <= 4;

    /// <summary>Use Windows' power mode (overlay) instead of switching power plans.</summary>
    public bool UsesPowerModes => IsArm64 || ModernStandby || HasBattery;

    public string Kind =>
        IsArm64 ? (HasBattery ? "ARM laptop" : "ARM PC") :
        HasBattery ? "laptop" :
        DiscreteGpu ? "desktop with a graphics card" : "desktop";

    public string Summary =>
        $"This PC: {Kind}, {RamGB:0} GB RAM, {Threads} threads" + (GpuName.Length > 0 ? $", {GpuName}" : "") +
        (IsLight ? ". Light PC: low-RAM extras are included." : ".");

    public static MachineProfile Detect()
    {
        bool arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        bool emulated = arm && RuntimeInformation.ProcessArchitecture != Architecture.Arm64;

        bool battery = false, ac = true;
        try
        {
            if (GetSystemPowerStatus(out var ps))
            {
                battery = ps.BatteryFlag != 128 && ps.BatteryFlag != 255; // 128 = no battery, 255 = unknown
                ac = ps.ACLineStatus != 0;
            }
        }
        catch { }

        bool modernStandby = false;
        try
        {
            var caps = new byte[256];
            if (GetPwrCapabilities(caps)) modernStandby = caps[20] != 0; // SYSTEM_POWER_CAPABILITIES.AoAc
        }
        catch { }

        double ram = 0;
        try { ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0; } catch { }

        bool discrete = false;
        string gpu = "";
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (ManagementObject mo in s.Get())
            {
                var name = mo["Name"]?.ToString() ?? "";
                if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || name.Contains("Remote", StringComparison.OrdinalIgnoreCase)) continue;
                bool d = IsDiscrete(name);
                if (gpu.Length == 0 || d) gpu = name.Trim();
                discrete |= d;
            }
        }
        catch { }

        return new MachineProfile
        {
            IsArm64 = arm, AppEmulated = emulated, HasBattery = battery, OnAcPower = ac,
            ModernStandby = modernStandby, RamGB = ram, Threads = Environment.ProcessorCount,
            DiscreteGpu = discrete && !arm, GpuName = gpu,
        };
    }

    internal static bool IsDiscrete(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var k in new[] { "Radeon RX", "Radeon Pro", "Radeon R9", "Radeon R7 3", "Arc(TM) A", "Arc A", "Arc(TM) B", "Arc B" })
            if (name.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ---- PE header: what CPU a program was built for (to spot emulated apps on ARM)

    public enum ExeArch { Unknown, X86, X64, Arm64, ArmHybrid }

    public static ExeArch ExeArchitecture(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt16() != 0x5A4D) return ExeArch.Unknown; // "MZ"
            fs.Position = 0x3C;
            fs.Position = br.ReadInt32();
            if (br.ReadUInt32() != 0x00004550) return ExeArch.Unknown; // "PE\0\0"
            return br.ReadUInt16() switch
            {
                0x014C => ExeArch.X86,
                0x8664 => ExeArch.X64,
                0xAA64 => ExeArch.Arm64,
                0xA641 => ExeArch.ArmHybrid, // ARM64EC / ARM64X: runs natively
                _ => ExeArch.Unknown,
            };
        }
        catch { return ExeArch.Unknown; }
    }

    /// <summary>The .exe a startup command or shortcut runs, or null.</summary>
    public static string? ExePathFromCommand(string command)
    {
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        string? path = null;
        if (c.StartsWith('"'))
        {
            int end = c.IndexOf('"', 1);
            if (end > 1) path = c[1..end];
        }
        else
        {
            int i = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (i > 0) path = c[..(i + 4)];
        }
        return path != null && File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetPwrCapabilities([Out] byte[] caps);
}
