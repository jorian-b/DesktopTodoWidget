using System.Windows;
using System.Threading;

namespace DesktopTodoWidget;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(false, @"Local\DesktopTodoWidget");
        try
        {
            _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsSingleInstanceMutex = true;
        }

        if (!_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (s, ev) => LogAndShow(ev.ExceptionObject as Exception);
        this.DispatcherUnhandledException += (s, ev) => { LogAndShow(ev.Exception); ev.Handled = true; };
        
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        _ownsSingleInstanceMutex = false;
        base.OnExit(e);
    }

    private void LogAndShow(Exception? ex)
    {
        if (ex == null) return;
        string message = $"FATAL ERROR:\n{ex.Message}\n\n{ex.StackTrace}";
        
        // Try to log to temp
        try
        {
            string logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DesktopTodoWidget_CRASH.log");
            System.IO.File.AppendAllText(logPath, $"{DateTime.Now}: {message}\n\n");
        }
        catch { }

        MessageBox.Show(message, "DesktopTodoWidget Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

