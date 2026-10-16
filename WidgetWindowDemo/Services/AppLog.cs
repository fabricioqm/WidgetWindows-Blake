using System.Diagnostics;
using System.IO;
using System.Text;

namespace WidgetWindowDemo.Services
{
    public enum LogLevel { Info, Warning, Error }

    /// <summary>
    /// Writes the app's log to %LOCALAPPDATA%\WidgetWindowDemo\logs\app-YYYY-MM-DD.log (one file per day).
    /// A per-user folder is used because the install folder may not be writable. Files older than
    /// <see cref="RetentionDays"/> days are deleted at startup, and a day's file stops growing at
    /// <see cref="MaxFileBytes"/>. Logging never throws: a failure to write is ignored.
    /// </summary>
    public static class AppLog
    {
        private const int RetentionDays = 14;
        private const long MaxFileBytes = 5 * 1024 * 1024;

        private static readonly object Gate = new();

        public static string Directory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WidgetWindowDemo", "logs");

        public static string CurrentFile => Path.Combine(Directory, $"app-{DateTime.Now:yyyy-MM-dd}.log");

        public static void Info(string message) => Write(LogLevel.Info, message, null);
        public static void Warning(string message, Exception? exception = null) => Write(LogLevel.Warning, message, exception);
        public static void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

        /// <summary>Call once at startup: header with app/OS details and cleanup of old files.</summary>
        public static void Start()
        {
            DeleteOldFiles();
            var version = typeof(AppLog).Assembly.GetName().Version;
            Info($"Inicio · versión {version} · {Environment.OSVersion} · .NET {Environment.Version} · " +
                 $"proceso {Environment.ProcessId}");
        }

        public static void Write(LogLevel level, string message, Exception? exception)
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ")
                .Append(message);
            if (exception != null)
                line.AppendLine().Append("    ").Append(exception.ToString().Replace(Environment.NewLine, Environment.NewLine + "    "));
            line.AppendLine();

            Debug.Write(line.ToString());

            try
            {
                lock (Gate)
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    var file = new FileInfo(CurrentFile);
                    if (file.Exists && file.Length > MaxFileBytes) return;
                    File.AppendAllText(file.FullName, line.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never break the app (disk full, folder not writable, ...).
            }
        }

        /// <summary>Opens the logs folder in Explorer.</summary>
        public static void OpenFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Directory}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Error("No se pudo abrir la carpeta de registros.", ex);
            }
        }

        private static void DeleteOldFiles()
        {
            try
            {
                if (!System.IO.Directory.Exists(Directory)) return;
                foreach (var file in new DirectoryInfo(Directory).GetFiles("app-*.log"))
                    if (file.LastWriteTime < DateTime.Now.AddDays(-RetentionDays))
                        file.Delete();
            }
            catch
            {
                // Best effort.
            }
        }
    }
}
