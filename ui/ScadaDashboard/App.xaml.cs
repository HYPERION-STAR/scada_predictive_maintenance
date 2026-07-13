using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ScadaDashboard;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Dispatcher", e.Exception);
    }

    private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        Log("Domain", e.ExceptionObject as Exception);
    }

    private static void Log(string src, Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {src}: {ex}\n\n");
        }
        catch { }
    }
}
