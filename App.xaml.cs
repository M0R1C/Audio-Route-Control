using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AudioRouteControl;

public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception) LogException(exception);
    }

    private static void LogException(Exception exception)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "AudioRouteControl-error.log");
            File.AppendAllText(path, $"{DateTime.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}
