using System.Management;
using Microsoft.Win32;

namespace MoorePCMechanic.Services;

/// <summary>
/// PCs with a known, serious problem the app must work around.
/// HP All-in-One 24-dd0xxx (MonkD board, SSID 86F2/86F3; also the MonkP 86F3 boards): Windows Update
/// pushed a BIOS for the wrong board and left these PCs stuck on "The System BIOS update failed -
/// Unable to open BIOS image file". On these PCs the app never offers BIOS or firmware updates.
/// </summary>
public static class KnownIssues
{
    private static readonly Lazy<bool> _hpAio = new(DetectHpAio);

    /// <summary>True on the HP 24-dd0 AIO family: no BIOS/firmware updates, ever.</summary>
    public static bool HpAioBiosLock => _hpAio.Value;

    public const string HpAioTitle = "HP AIO: never update this PC's BIOS";

    public const string HpAioDetail =
        "This HP 24-dd0 All-in-One (MonkD board) has a known problem: Windows Update can push a BIOS meant for a " +
        "different board, and the PC gets stuck on \"The System BIOS update failed - Unable to open BIOS image file\". " +
        "Moore PC Mechanic hides BIOS and firmware updates on this PC and won't suggest them.\n" +
        "Safer options: keep the BIOS it has now. Stop Windows Update from installing drivers (the Drivers tab offers this). " +
        "Don't let HP Support Assistant update the BIOS. If it ever loops again, boot with the MOORE-BOOT stick (HP F.41, " +
        "sp141980) using Windows + B at power-on. For more speed, add RAM or a faster SSD instead of a BIOS update.";

    private static bool DetectHpAio()
    {
        try
        {
            string maker = "", model = "", board = "";
            using (var s = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                foreach (ManagementBaseObject o in s.Get())
                {
                    maker = o["Manufacturer"]?.ToString() ?? "";
                    model = o["Model"]?.ToString() ?? "";
                }
            using (var s = new ManagementObjectSearcher("SELECT Product FROM Win32_BaseBoard"))
                foreach (ManagementBaseObject o in s.Get())
                    board = o["Product"]?.ToString() ?? "";

            bool hp = maker.Contains("HP", StringComparison.OrdinalIgnoreCase) || maker.Contains("Hewlett", StringComparison.OrdinalIgnoreCase);
            if (!hp) return false;
            if (model.Contains("24-dd0", StringComparison.OrdinalIgnoreCase)) return true;
            var b = board.Trim().ToUpperInvariant();
            return b == "86F2" || b == "86F3";
        }
        catch { return false; }
    }

    /// <summary>A Windows Update item that would change the BIOS or other system firmware.</summary>
    public static bool IsBiosOrFirmware(DriverUpdate u) =>
        u.ClassName.Equals("Firmware", StringComparison.OrdinalIgnoreCase)
        || u.Title.Contains("Firmware", StringComparison.OrdinalIgnoreCase)
        || u.Title.Contains("BIOS", StringComparison.OrdinalIgnoreCase);

    private const string WuPolicy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";

    /// <summary>True when the "Do not include drivers with Windows Updates" policy is on.</summary>
    public static bool WindowsUpdateDriversBlocked()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(WuPolicy);
            return Convert.ToInt32(k?.GetValue("ExcludeWUDriversInQualityUpdate") ?? 0) == 1;
        }
        catch { return false; }
    }

    /// <summary>Turns on "Do not include drivers with Windows Updates". Needs admin.</summary>
    public static void BlockWindowsUpdateDrivers()
    {
        using var k = Registry.LocalMachine.CreateSubKey(WuPolicy, true);
        k.SetValue("ExcludeWUDriversInQualityUpdate", 1, RegistryValueKind.DWord);
    }
}
