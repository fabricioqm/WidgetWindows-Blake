using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace WidgetWindowDemo
{
    /// <summary>
    /// Converts a file path into a small, cached BitmapImage for the Widget Manager list.
    /// GIFs keep all their frames (so WpfAnimatedGif can animate them); other images are
    /// decoded at a small size to save memory. Videos return null (the UI shows a placeholder).
    /// </summary>
    public sealed class ThumbnailConverter : IValueConverter
    {
        private static readonly Dictionary<string, BitmapImage?> Cache = new(StringComparer.OrdinalIgnoreCase);

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string path || !File.Exists(path)) return null;

            if (!MediaFormats.IsImage(path)) return null;

            if (Cache.TryGetValue(path, out var cached)) return cached;

            // A damaged GIF would make WpfAnimatedGif throw while the card loads; show no thumbnail instead.
            if (MediaFormats.IsGif(path) && !Services.GifInspector.Inspect(path).IsValid)
            {
                Cache[path] = null;
                return null;
            }

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.CacheOption = BitmapCacheOption.OnLoad; // doesn't lock the file
                if (!MediaFormats.IsGif(path)) image.DecodePixelWidth = 320;
                image.EndInit();
                image.Freeze();

                Cache[path] = image;
                return image;
            }
            catch
            {
                Cache[path] = null;
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();

        /// <summary>Call when a widget file is deleted.</summary>
        public static void Evict(string path) => Cache.Remove(path);
    }
}