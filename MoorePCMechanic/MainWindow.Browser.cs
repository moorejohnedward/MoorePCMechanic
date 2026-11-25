using System.Windows;
using System.Windows.Threading;
using MoorePCMechanic.Services;

namespace MoorePCMechanic;

/// <summary>Tune-up tab: default browser line ("Make Google Chrome the default").</summary>
public partial class MainWindow
{
    private DispatcherTimer? _browserWatch;

    private void ShowDefaultBrowser()
    {
        bool chrome = DefaultBrowserService.ChromeIsDefault();
        bool installed = DefaultBrowserService.ChromePath() != null;
        BrowserNow.Text = DefaultBrowserService.CurrentName() + (chrome ? " ✓" : "");
        BtnBrowser.Visibility = chrome ? Visibility.Collapsed : Visibility.Visible;
        BtnBrowser.Content = installed ? "Make Google Chrome the default" : "Get Google Chrome";
    }

    private void BtnBrowser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DefaultBrowserService.ChromePath() == null)
            {
                DefaultBrowserService.OpenChromeDownload();
                BrowserStatus.Text = "Opened Google's Chrome download page. Install Chrome, then click \"Make Google Chrome the default\" here.";
                BtnBrowser.Content = "Make Google Chrome the default";
                return;
            }
            DefaultBrowserService.OpenSetDefault();
            BrowserStatus.Text = Environment.OSVersion.Version.Build >= 22000
                ? "In the Settings window: click \"Set default\" at the top of Google Chrome's page. Waiting for the change…"
                : "In the Settings window: under \"Web browser\", pick Google Chrome. Waiting for the change…";
            WatchForChrome();
        }
        catch (Exception ex)
        {
            BrowserStatus.Text = "Couldn't open Default apps: " + ex.Message;
        }
    }

    /// <summary>Checks every 2 seconds for 3 minutes whether Chrome has become the default.</summary>
    private void WatchForChrome()
    {
        _browserWatch?.Stop();
        var until = DateTime.Now.AddMinutes(3);
        _browserWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _browserWatch.Tick += (_, _) =>
        {
            if (DefaultBrowserService.ChromeIsDefault())
            {
                _browserWatch.Stop();
                ShowDefaultBrowser();
                BrowserStatus.Text = "Done: Google Chrome now opens your links.";
                StatusText.Text = "Google Chrome is now the default browser.";
            }
            else if (DateTime.Now > until)
            {
                _browserWatch.Stop();
                ShowDefaultBrowser();
                BrowserStatus.Text = "No change yet. Click the button again if Settings closed before \"Set default\" was clicked.";
            }
        };
        _browserWatch.Start();
    }
}
