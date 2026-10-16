using System.Windows;
using WidgetWindowDemo.Services;

namespace WidgetWindowDemo
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex;
        private static bool _ownsMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(initiallyOwned: true, "WidgetWindowDemoUniqueMutex", out _ownsMutex);

            if (!_ownsMutex)
            {
                // Another instance is already running.
                _mutex.Dispose();
                _mutex = null;
                Shutdown();
                return;
            }

            AppLog.Start();

            // Errors raised inside WPF events (e.g. a damaged GIF decoded when an Image loads) must not
            // close the app and take every desktop widget with it.
            DispatcherUnhandledException += (_, args) =>
            {
                args.Handled = true;
                ErrorReporter.Report("Error inesperado",
                    "Se produjo un error y la operación no se completó. La aplicación sigue funcionando.\n\n" +
                    args.Exception.Message, args.Exception);
            };

            // Errors on other threads can't be recovered from, but they are recorded before the process ends.
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                AppLog.Error("Error no controlado (la aplicación se cerrará).", args.ExceptionObject as Exception);

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                AppLog.Error("Error en una tarea en segundo plano.", args.Exception);
                args.SetObserved();
            };

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_ownsMutex) AppLog.Info($"Cierre (código {e.ApplicationExitCode}).");

            if (_ownsMutex && _mutex != null)
            {
                try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
                _mutex.Dispose();
            }

            base.OnExit(e);
        }
    }
}