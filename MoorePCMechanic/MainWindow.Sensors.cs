using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using MoorePCMechanic.Services;

namespace MoorePCMechanic;

/// <summary>One-click install of PawnIO, the signed driver LibreHardwareMonitor uses to read CPU temperatures.</summary>
public partial class MainWindow
{
    private const string PawnIOUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

    private async void BtnPawnIO_Click(object sender, RoutedEventArgs e)
    {
        BtnPawnIO.IsEnabled = false;
        try
        {
            StatusText.Text = "Downloading the PawnIO sensor driver from its official site…";
            var dir = Path.Combine(Path.GetTempPath(), "MoorePCMechanic");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "PawnIO_setup.exe");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("MoorePCMechanic");
                var bytes = await http.GetByteArrayAsync(PawnIOUrl);
                await File.WriteAllBytesAsync(file, bytes);
            }

            var signer = await Task.Run(() => DriverService.VerifySignature(file));
            if (signer == null)
            {
                try { File.Delete(file); } catch { }
                StatusText.Text = "The PawnIO download isn't validly signed, so it wasn't run. You can install it yourself from pawnio.eu.";
                return;
            }

            var who = signer.Split(',').FirstOrDefault(p => p.Trim().StartsWith("CN=", StringComparison.OrdinalIgnoreCase))?.Trim()[3..] ?? signer;
            var ok = MessageBox.Show(this,
                "PawnIO is a small, free driver that hardware monitors (LibreHardwareMonitor, FanControl and others) use to read the processor's temperature sensors.\n\n" +
                $"The installer is digitally signed by: {who}\n\n" +
                "Its setup window opens next; click Install. Moore PC Mechanic restarts afterwards so it can use the driver.",
                "Turn on CPU temperature", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (ok != MessageBoxResult.OK) { StatusText.Text = "PawnIO install cancelled."; return; }

            StatusText.Text = "Waiting for the PawnIO installer to finish…";
            var psi = new ProcessStartInfo(file) { UseShellExecute = true };
            if (!AppEnv.IsAdmin()) psi.Verb = "runas";
            using (var p = Process.Start(psi))
                if (p != null) await p.WaitForExitAsync();

            var restart = MessageBox.Show(this, "Restart Moore PC Mechanic now to start reading the CPU temperature?",
                                          "PawnIO", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (restart == MessageBoxResult.Yes)
            {
                if (AppEnv.RestartAsAdmin()) Close();
            }
            else StatusText.Text = "PawnIO installed. Restart the app to see the CPU temperature.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "PawnIO install cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't install PawnIO: " + ex.Message + " You can install it yourself from pawnio.eu.";
        }
        finally
        {
            BtnPawnIO.IsEnabled = true;
        }
    }
}
