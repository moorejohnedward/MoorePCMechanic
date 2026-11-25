// Moore PC Mechanic - default browser
// Windows 10 and 11 don't let programs switch the default browser by themselves (a protected, hashed
// registry entry stops malware from hijacking it). The supported way is Windows' own Default apps page,
// so the app reads which browser is the default, opens that page straight to Google Chrome (where
// "Set default" is one click), and then watches for the change to confirm it worked.

using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MoorePCMechanic.Services;

public static class DefaultBrowserService
{
    private const string UrlAssoc = @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\";

    /// <summary>The ProgId Windows opens web links with (e.g. "ChromeHTML", "MSEdgeHTM"), or null.</summary>
    public static string? CurrentProgId()
    {
        foreach (var proto in new[] { "https", "http" })
            foreach (var sub in new[] { "UserChoiceLatest", "UserChoice" }) // newer Windows 11 builds use UserChoiceLatest
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(UrlAssoc + proto + @"\" + sub);
                    if (k?.GetValue("ProgId") is string id && id.Length > 0) return id;
                }
                catch { }
            }
        return null;
    }

    public static bool ChromeIsDefault() => CurrentProgId()?.StartsWith("ChromeHTML", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>A friendly name for the current default browser.</summary>
    public static string CurrentName()
    {
        var id = CurrentProgId();
        if (id == null) return "not set";
        if (id.StartsWith("ChromeHTML", StringComparison.OrdinalIgnoreCase)) return "Google Chrome";
        if (id.StartsWith("MSEdge", StringComparison.OrdinalIgnoreCase)) return "Microsoft Edge";
        if (id.StartsWith("Firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (id.StartsWith("Brave", StringComparison.OrdinalIgnoreCase)) return "Brave";
        if (id.StartsWith("Opera", StringComparison.OrdinalIgnoreCase)) return "Opera";
        try
        {
            using var k = Registry.ClassesRoot.OpenSubKey(id + @"\Application");
            if (k?.GetValue("ApplicationName") is string n && n.Length > 0 && !n.StartsWith("@")) return n;
        }
        catch { }
        return id;
    }

    /// <summary>Path to chrome.exe, or null if Chrome isn't installed.</summary>
    public static string? ChromePath()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var k = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
                if (k?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
            }
            catch { }
        }
        foreach (var dir in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData })
        {
            var p = Path.Combine(Environment.GetFolderPath(dir), @"Google\Chrome\Application\chrome.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Chrome's name in RegisteredApplications ("Google Chrome", or "Google Chrome.XYZ" for a per-user install) and whether it's per-user.</summary>
    private static (string? Name, bool PerUser) ChromeRegistration()
    {
        foreach (var (root, perUser) in new[] { (Registry.LocalMachine, false), (Registry.CurrentUser, true) })
        {
            try
            {
                using var k = root.OpenSubKey(@"SOFTWARE\RegisteredApplications");
                var name = k?.GetValueNames().FirstOrDefault(n => n.StartsWith("Google Chrome", StringComparison.OrdinalIgnoreCase));
                if (name != null) return (name, perUser);
            }
            catch { }
        }
        return (null, false);
    }

    /// <summary>Opens Windows' Default apps page, straight to Chrome's page on Windows 11 (its "Set default" button does it in one click).</summary>
    public static void OpenSetDefault()
    {
        var (name, perUser) = ChromeRegistration();
        string uri = Environment.OSVersion.Version.Build >= 22000 && name != null
            ? $"ms-settings:defaultapps?{(perUser ? "registeredAppUser" : "registeredAppMachine")}={Uri.EscapeDataString(name)}"
            : "ms-settings:defaultapps";
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    public static void OpenChromeDownload() =>
        Process.Start(new ProcessStartInfo("https://www.google.com/chrome/") { UseShellExecute = true });
}
