using System.IO;
using System.Windows;
using System.Windows.Threading;
using MoorePCMechanic.Services;

namespace MoorePCMechanic;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;

        // Scheduled automatic tune-up (Windows Task Scheduler runs "MoorePCMechanic.exe --tuneup"): no window.
        if (e.Args.Contains("--tuneup"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            Task.Run(TuneUpService.RunAutomatic).ContinueWith(_ => Dispatcher.Invoke(() => Shutdown()));
            return;
        }

        if (e.Args.Contains("--updated")) UpdateService.CloseOtherCopies(); // older copies still running would keep the old .exe locked
        UpdateService.CleanupAfterUpdate(); // remove the previous version left behind by an update
        base.OnStartup(e);
        var main = new MoorePCMechanic.MainWindow();
        MainWindow = main;
        main.Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppEnv.DataFolder, "error-log.txt"), $"{DateTime.Now}: {e.Exception}\r\n\r\n");
        }
        catch { }
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were saved to error-log.txt in Documents\\Moore PC Mechanic.",
            "Moore PC Mechanic", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
