using System;
using System.Threading.Tasks;
using System.Windows;
using MoorePCMechanic.Services;

namespace MoorePCMechanic;

public partial class UpdateWindow : Window
{
    private UpdateInfo? _info;

    /// <param name="info">A release that's already known to be newer, or null to check now.</param>
    public UpdateWindow(UpdateInfo? info = null)
    {
        InitializeComponent();
        AutoCheckBox.IsChecked = UpdateService.CheckOnStartup;
        AutoCheckBox.Checked += (_, _) => UpdateService.CheckOnStartup = true;
        AutoCheckBox.Unchecked += (_, _) => UpdateService.CheckOnStartup = false;

        if (info is not null) ShowRelease(info);
        else Loaded += async (_, _) => await CheckNowAsync();
    }

    private async Task CheckNowAsync()
    {
        HeadlineText.Text = "Checking for updates…";
        VersionText.Text = $"You have version {UpdateService.CurrentVersion}";
        SetButtons(update: false, skip: false);
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info is null)
            {
                HeadlineText.Text = "You're up to date";
                NotesText.Text = "This is the newest version of Moore PC Mechanic.";
                LaterButton.Content = "Close";
            }
            else ShowRelease(info);
        }
        catch (Exception ex)
        {
            HeadlineText.Text = "Couldn't check for updates";
            NotesText.Text = "Make sure you're online and try again.\n\n" + ex.Message;
            LaterButton.Content = "Close";
        }
    }

    private void ShowRelease(UpdateInfo info)
    {
        _info = info;
        HeadlineText.Text = "A new version is ready";
        VersionText.Text = $"You have {UpdateService.CurrentVersion}   →   New: {info.Version}";
        NotesText.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes)
            ? "No release notes were posted for this version."
            : CleanMarkdown(info.ReleaseNotes);
        SetButtons(update: true, skip: true);
    }

    private void SetButtons(bool update, bool skip)
    {
        UpdateButton.Visibility = update ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = skip ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_info is null) return;
        UpdateButton.IsEnabled = SkipButton.IsEnabled = LaterButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Downloading…";

        var progress = new Progress<double>(p =>
        {
            DownloadProgress.Value = p;
            StatusText.Text = $"Downloading… {p:P0}";
        });

        try
        {
            await UpdateService.DownloadAndInstallAsync(_info, progress);
            StatusText.Text = "Installed. Restarting…";
        }
        catch (UnauthorizedAccessException)
        {
            StatusText.Text = "Windows blocked the update in this folder. Click \"Restart as admin\" in the app, then try again.";
            UpdateButton.IsEnabled = SkipButton.IsEnabled = LaterButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Update didn't finish: " + ex.Message + "  Your current version is untouched.";
            UpdateButton.IsEnabled = SkipButton.IsEnabled = LaterButton.IsEnabled = true;
        }
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        if (_info is not null) UpdateService.Skip(_info);
        Close();
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e) => Close();

    // Release notes come from CHANGELOG.md; strip the markdown symbols for plain display.
    private static string CleanMarkdown(string md) =>
        md.Replace("\r", "")
          .Replace("**", "")
          .Replace("### ", "")
          .Replace("## ", "")
          .Replace("\n- ", "\n•  ")
          .Replace("\n* ", "\n•  ")
          .Trim();
}
