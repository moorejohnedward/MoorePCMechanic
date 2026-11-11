// Moore PC Mechanic - in-app updater
// Checks GitHub Releases for a newer version, downloads the new .exe,
// verifies it, swaps it in place of the running .exe, and relaunches.
//
// How the swap works: Windows won't let you overwrite a running .exe,
// but it DOES let you rename it. So we rename the running file to
// "MoorePCMechanic.exe.old", move the new download into its place,
// start the new copy, and close. The next launch deletes the .old file.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MoorePCMechanic.Services;

public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string ReleaseNotes,
    string DownloadUrl,
    long DownloadSize,
    string? Sha256Url,
    string PageUrl);

public static class UpdateService
{
    // ---- Settings -------------------------------------------------------
    // GitHub account and repo the app updates from. (github.com/moorejohnedward/moorepcmechanic)
    public const string GitHubOwner = "moorejohnedward";
    public const string GitHubRepo = "moorepcmechanic";
    private const string ExeAssetName = "MoorePCMechanic.exe";
    private const string ShaAssetName = "MoorePCMechanic.exe.sha256";
    // ---------------------------------------------------------------------

    private static readonly HttpClient Http = CreateClient();

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Moore PC Mechanic");
    private static readonly string SettingsFile = Path.Combine(SettingsDir, "update-settings.json");

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    public static string CurrentExePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ExeAssetName);

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub's API requires a User-Agent header.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MoorePCMechanic-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Call once at startup: removes the old .exe left behind by the last update.</summary>
    public static void CleanupAfterUpdate()
    {
        var old = CurrentExePath + ".old";
        for (int i = 0; i < 10 && File.Exists(old); i++)
        {
            try { File.Delete(old); }
            catch { Thread.Sleep(300); } // previous copy may still be closing
        }
        try { File.Delete(CurrentExePath + ".new"); } catch { }
    }

    /// <summary>Returns the newest release if it's newer than this copy, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null; // offline, no releases yet, or rate-limited

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var latest) || latest <= CurrentVersion) return null;

        string? exeUrl = null, shaUrl = null;
        long size = 0;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString();
            if (string.Equals(name, ExeAssetName, StringComparison.OrdinalIgnoreCase))
            {
                exeUrl = a.GetProperty("browser_download_url").GetString();
                size = a.GetProperty("size").GetInt64();
            }
            else if (string.Equals(name, ShaAssetName, StringComparison.OrdinalIgnoreCase))
            {
                shaUrl = a.GetProperty("browser_download_url").GetString();
            }
        }
        if (exeUrl is null) return null;

        return new UpdateInfo(
            latest, tag,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            exeUrl, size, shaUrl,
            root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "");
    }

    /// <summary>Downloads, verifies and installs the update, then relaunches the app.</summary>
    public static async Task DownloadAndInstallAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        var exe = CurrentExePath;
        var newFile = exe + ".new";
        var oldFile = exe + ".old";

        // 1) Download next to the current .exe
        using (var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? info.DownloadSize;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(newFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }

        // 2) Verify: right size, and matches the published SHA-256 fingerprint
        var fi = new FileInfo(newFile);
        if (info.DownloadSize > 0 && fi.Length != info.DownloadSize)
        {
            File.Delete(newFile);
            throw new InvalidDataException("The download was incomplete. Please try again.");
        }
        if (info.Sha256Url is not null)
        {
            var expected = (await Http.GetStringAsync(info.Sha256Url, ct).ConfigureAwait(false))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().ToLowerInvariant();
            string actual;
            await using (var fs = File.OpenRead(newFile))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (!string.IsNullOrEmpty(expected) && expected != actual)
            {
                File.Delete(newFile);
                throw new InvalidDataException("The download didn't pass its safety check, so it wasn't installed.");
            }
        }

        // 3) Swap: rename running exe out of the way, move new one in
        if (File.Exists(oldFile)) File.Delete(oldFile);
        File.Move(exe, oldFile);
        try
        {
            File.Move(newFile, exe);
        }
        catch
        {
            File.Move(oldFile, exe); // put the original back if anything went wrong
            throw;
        }

        // 4) Relaunch the new version and close this one
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = "--updated" });
        System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
    }

    // ---- "Skip this version" and auto-check preferences ------------------

    private sealed class Settings
    {
        public string? SkippedVersion { get; set; }
        public bool CheckOnStartup { get; set; } = true;
    }

    private static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile)) ?? new(); }
        catch { return new(); }
    }

    private static void Save(Settings s)
    {
        try { Directory.CreateDirectory(SettingsDir); File.WriteAllText(SettingsFile, JsonSerializer.Serialize(s)); }
        catch { }
    }

    public static bool CheckOnStartup
    {
        get => Load().CheckOnStartup;
        set { var s = Load(); s.CheckOnStartup = value; Save(s); }
    }

    public static bool IsSkipped(UpdateInfo info) => Load().SkippedVersion == info.Version.ToString();

    public static void Skip(UpdateInfo info) { var s = Load(); s.SkippedVersion = info.Version.ToString(); Save(s); }

    private static bool TryParseVersion(string tag, out Version v)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        var dash = t.IndexOf('-');
        if (dash >= 0) t = t[..dash];
        if (Version.TryParse(t, out var parsed))
        {
            v = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
            return true;
        }
        v = new Version(0, 0, 0);
        return false;
    }
}
