using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WidgetWindowDemo.Services
{
    /// <summary>
    /// Checks that a file can really be displayed before it is imported, so damaged files are never added.
    /// Images and GIFs are fully decoded (every frame); videos are opened with the same engine that plays them.
    /// The file is only read, never modified.
    /// </summary>
    public static class MediaValidator
    {
        private static readonly TimeSpan VideoOpenTimeout = TimeSpan.FromSeconds(15);

        /// <summary>Null when the file is valid; otherwise a short reason for the user.</summary>
        public static async Task<string?> ValidateAsync(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return "El archivo no existe.";
                if (info.Length == 0) return "El archivo está vacío.";

                if (MediaFormats.IsGif(path))
                {
                    var gif = GifInspector.Inspect(path);
                    if (!gif.IsValid) return gif.Error;
                    if (gif.Truncated) return "El GIF está incompleto o truncado (le faltan datos al final).";
                }

                if (MediaFormats.IsImage(path))
                    return await Task.Run(() => ImageStructure.Check(path) ?? DecodeAllFrames(path));

                if (MediaFormats.IsVideo(path))
                    return await TryOpenVideoAsync(path);

                return "El formato no es compatible.";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static string? DecodeAllFrames(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0) return "La imagen no contiene ningún fotograma.";

                foreach (var frame in decoder.Frames)
                {
                    // Force a full decode: damaged pixel data throws here, not just when reading the header.
                    var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                    int stride = converted.PixelWidth * 4;
                    converted.CopyPixels(new byte[stride * converted.PixelHeight], stride, 0);
                }

                return null;
            }
            catch (Exception ex)
            {
                return "La imagen está dañada o no se puede decodificar (" + ex.Message.TrimEnd('.') + ").";
            }
        }

        /// <summary>Must run on the UI thread (MediaPlayer raises its events there).</summary>
        private static async Task<string?> TryOpenVideoAsync(string path)
        {
            var player = new MediaPlayer { IsMuted = true };
            var result = new TaskCompletionSource<string?>();

            player.MediaOpened += (_, _) => result.TrySetResult(
                player.HasVideo || player.HasAudio ? null : "El archivo no contiene vídeo reproducible.");
            player.MediaFailed += (_, e) => result.TrySetResult(
                "El vídeo está dañado o falta el códec para reproducirlo (" + e.ErrorException.Message.TrimEnd('.') + ").");

            try
            {
                player.Open(new Uri(path, UriKind.Absolute));
                var finished = await Task.WhenAny(result.Task, Task.Delay(VideoOpenTimeout));
                return finished == result.Task
                    ? await result.Task
                    : "No se pudo abrir el vídeo (tiempo de espera agotado). Puede estar dañado.";
            }
            finally
            {
                player.Close(); // releases the file
            }
        }
    }
}
