using System.IO;

namespace WidgetWindowDemo
{
    /// <summary>Single source of truth for the media formats the app can import and display.</summary>
    internal static class MediaFormats
    {
        /// <summary>Decoded by WPF (WIC). GIFs are animated with WpfAnimatedGif.</summary>
        public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico" };

        /// <summary>Played by MediaElement (Media Foundation); availability depends on installed codecs.</summary>
        public static readonly string[] VideoExtensions = { ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".mkv", ".webm" };

        public static string OpenFileFilter
        {
            get
            {
                string all = string.Join(";", ImageExtensions.Concat(VideoExtensions).Select(e => "*" + e));
                string images = string.Join(";", ImageExtensions.Select(e => "*" + e));
                string videos = string.Join(";", VideoExtensions.Select(e => "*" + e));
                return $"Imágenes y vídeos|{all}|Imágenes|{images}|Vídeos|{videos}|Todos los archivos|*.*";
            }
        }

        private static string Ext(string path) => Path.GetExtension(path).ToLowerInvariant();

        public static bool IsImage(string path) => ImageExtensions.Contains(Ext(path));
        public static bool IsVideo(string path) => VideoExtensions.Contains(Ext(path));
        public static bool IsGif(string path) => Ext(path) == ".gif";
        public static bool IsSupported(string path) => IsImage(path) || IsVideo(path);
    }
}
