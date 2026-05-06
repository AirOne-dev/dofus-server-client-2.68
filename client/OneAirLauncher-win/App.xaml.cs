using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace OneAirLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain", args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("Dispatcher", args.Exception);
            try
            {
                MessageBox.Show(
                    args.Exception.ToString(),
                    "OneAir Launcher — erreur fatale",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            LogCrash("Task", args.Exception);

        // L'accélération hardware WPF (D3D9 + d3dcompiler_47) crash sur Wine/CrossOver
        // et sur certains Windows mal configurés (RDP, VMs, drivers GPU vieux).
        // Tier 0 = "pas d'accélération dispo", sur ces machines on force le software.
        // Override possible via env var pour debug.
        try
        {
            var force = Environment.GetEnvironmentVariable("ONEAIR_FORCE_SOFTWARE_RENDER");
            if (force == "1" || RenderCapability.Tier >> 16 == 0)
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }
        catch (Exception ex) { LogCrash("RenderInit", ex); }

        base.OnStartup(e);
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OneAir", "Logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "launcher.log"),
                $"[{DateTime.Now:O}] FATAL ({source}): {ex}{Environment.NewLine}");
        }
        catch { }
    }
}
