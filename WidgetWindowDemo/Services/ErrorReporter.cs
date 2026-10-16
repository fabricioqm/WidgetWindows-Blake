using WidgetWindowDemo.Views;

namespace WidgetWindowDemo.Services
{
    public sealed record ErrorReport(string Title, string Message, Exception? Exception);

    /// <summary>
    /// Single entry point for user-facing errors: every error is written to the log (<see cref="AppLog"/>),
    /// shown with the app's own alert dialog (<see cref="AppDialog"/>) and raised through <see cref="Reported"/>.
    /// </summary>
    public static class ErrorReporter
    {
        /// <summary>Raised for every error (logging, tests, a future in-app notification area).</summary>
        public static event EventHandler<ErrorReport>? Reported;

        /// <summary>Set to false to collect errors through <see cref="Reported"/> without showing dialogs.</summary>
        public static bool ShowDialogs { get; set; } = true;

        public static void Report(string title, string message, Exception? exception = null,
            IEnumerable<string>? details = null)
        {
            AppLog.Error($"{title}: {message.Replace(Environment.NewLine, " ").Replace("\n", " ")}", exception);
            Reported?.Invoke(null, new ErrorReport(title, message, exception));

            if (ShowDialogs)
                AppDialog.Alert(DialogKind.Error, title, message, details);
        }
    }
}
