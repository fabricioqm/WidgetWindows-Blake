using System.IO;

namespace WidgetWindowDemo.Services
{
    /// <summary>
    /// Where the app keeps the user's data. It must be a per-user writable folder: once installed under
    /// C:\Program Files the program folder is read-only for normal users.
    ///
    ///   %LOCALAPPDATA%\WidgetWindowDemo\widgets.json        widget list and settings
    ///   %LOCALAPPDATA%\WidgetWindowDemo\Assets\Widgets\     imported media (copies)
    ///   %LOCALAPPDATA%\WidgetWindowDemo\logs\               log files (see <see cref="AppLog"/>)
    /// </summary>
    public static class AppPaths
    {
        public static string DataDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WidgetWindowDemo");

        public static string WidgetsFile => Path.Combine(DataDirectory, "widgets.json");

        /// <summary>Imported media, stored in widgets.json relative to <see cref="DataDirectory"/>.</summary>
        public static string MediaDirectory => Path.Combine(DataDirectory, "Assets", "Widgets");

        /// <summary>Read-only files shipped with the app (icon...).</summary>
        public static string ProgramDirectory => AppContext.BaseDirectory;

        /// <summary>
        /// Earlier versions kept widgets.json and the imported media next to the .exe. Copy them (never move:
        /// the originals stay as a backup) to the per-user folder the first time this version runs.
        /// </summary>
        public static void MigrateFromProgramFolder()
        {
            string oldJson = Path.Combine(ProgramDirectory, "widgets.json");
            if (File.Exists(WidgetsFile) || !File.Exists(oldJson)) return;

            try
            {
                Directory.CreateDirectory(MediaDirectory);

                string oldMedia = Path.Combine(ProgramDirectory, "Assets", "Widgets");
                int copied = 0;
                if (Directory.Exists(oldMedia))
                {
                    foreach (var file in Directory.GetFiles(oldMedia))
                    {
                        string target = Path.Combine(MediaDirectory, Path.GetFileName(file));
                        if (!File.Exists(target)) { File.Copy(file, target); copied++; }
                    }
                }

                File.Copy(oldJson, WidgetsFile);
                AppLog.Info($"Datos migrados desde la carpeta del programa ({copied} archivos multimedia).");
            }
            catch (Exception ex)
            {
                AppLog.Error("No se pudieron migrar los datos desde la carpeta del programa.", ex);
            }
        }
    }
}
