using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Ultron;

public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog("UnhandledException: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            CrashLog("UnobservedTask: " + e.Exception);
        try
        {
            if (args.Length > 0 && args[0].Equals("--watchdog", StringComparison.OrdinalIgnoreCase))
            {
                Services.Watchdog.RunGuardian();
                return 0;
            }

            WinRT.ComWrappersSupport.InitializeComWrappers();
            XamlCheckProcessRequirements();
            Application.Start(_p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
            return 0;
        }
        catch (Exception ex)
        {
            CrashLog("MainFatal: " + ex);
            return 1;
        }
    }

    private static void CrashLog(string msg)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ultron");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
        }
        catch
        {
        }
    }
}