using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MoorePCMechanic.Services;

/// <summary>Folders, admin status and small Windows helpers.</summary>
public static class AppEnv
{
    public static string DataFolder
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Moore PC Mechanic");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SubFolder(string name)
    {
        var dir = Path.Combine(DataFolder, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Starts a new elevated copy of the app. Returns false if the user cancelled UAC.</summary>
    public static bool RestartAsAdmin()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { }
    }

    // ---- Physical memory (more reliable than sensor names) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    /// <summary>(totalBytes, usedBytes, loadPercent)</summary>
    public static (ulong Total, ulong Used, uint Load) GetMemory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
        return (m.ullTotalPhys, m.ullTotalPhys - m.ullAvailPhys, m.dwMemoryLoad);
    }

    public static string Bytes(double bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (bytes >= 1024 && i < u.Length - 1) { bytes /= 1024; i++; }
        return i <= 1 ? $"{bytes:0} {u[i]}" : $"{bytes:0.#} {u[i]}";
    }
}
