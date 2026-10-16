using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace WidgetWindowDemo.Models
{
    public enum WidgetType { Image, Video }

    /// <summary>Where a widget lives in the window z-order.</summary>
    public enum WidgetLayer
    {
        /// <summary>"Siempre visible": in the topmost band, above normal application windows.</summary>
        AlwaysVisible,

        /// <summary>"Integrado en el escritorio": part of the desktop, above the wallpaper and behind icons and windows.</summary>
        DesktopIntegrated
    }

    public class WidgetInfo : INotifyPropertyChanged
    {
        public const double MinWidth = 100;
        public const double MinHeight = 80;

        private WidgetLayer _layer = WidgetLayer.AlwaysVisible;
        private bool _locked;
        private bool _hidden;
        private bool _paused;
        private bool _editing;
        private string? _loadError;
        private string _name = "";
        private double _left = 100, _top = 100, _width = 320, _height = 180;
        private double _opacity = 1;

        public Guid Id { get; set; } = Guid.NewGuid();
        public WidgetType Type { get; set; } = WidgetType.Image;

        /// <summary>Internal file name inside Assets/Widgets (a GUID + extension).</summary>
        public string FileName { get; set; } = "";

        /// <summary>Custom name shown in the manager (defaults to the original file name).</summary>
        public string Name
        {
            get => _name;
            set { if (SetField(ref _name, value ?? "")) OnPropertyChanged(nameof(DisplayName)); }
        }

        /// <summary>Path relative to the user's data folder (see AppPaths.DataDirectory), or absolute.</summary>
        public string Source { get; set; } = "";

        // Position and size in WPF units (DIPs), screen coordinates.
        public double Left { get => _left; set => SetField(ref _left, Finite(value, _left)); }
        public double Top { get => _top; set => SetField(ref _top, Finite(value, _top)); }
        public double Width { get => _width; set => SetField(ref _width, Math.Max(MinWidth, Finite(value, _width))); }
        public double Height { get => _height; set => SetField(ref _height, Math.Max(MinHeight, Finite(value, _height))); }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public WidgetLayer Layer
        {
            get => _layer;
            set
            {
                if (!SetField(ref _layer, value)) return;
                OnPropertyChanged(nameof(LayerLabel));
                OnPropertyChanged(nameof(LayerGlyph));
                OnPropertyChanged(nameof(IsAlwaysVisible));
                OnPropertyChanged(nameof(IsDesktopIntegrated));
            }
        }

        /// <summary>
        /// Reads the "Topmost" flag written by earlier versions (true = always visible, false = desktop).
        /// It is never written back; <see cref="Layer"/> replaces it.
        /// </summary>
        [JsonPropertyName("Topmost")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? LegacyTopmost
        {
            get => null;
            set { if (value is bool topmost) Layer = topmost ? WidgetLayer.AlwaysVisible : WidgetLayer.DesktopIntegrated; }
        }

        /// <summary>Locked: can't be moved or resized, no menu, and clicks pass through.</summary>
        public bool Locked
        {
            get => _locked;
            set
            {
                if (!SetField(ref _locked, value)) return;
                if (value) IsEditing = false;
                OnPropertyChanged(nameof(LockIcon));
            }
        }

        public bool Hidden
        {
            get => _hidden;
            set
            {
                if (!SetField(ref _hidden, value)) return;
                OnPropertyChanged(nameof(IsShown));
                OnPropertyChanged(nameof(VisibilityIcon));
            }
        }

        /// <summary>Playback paused (videos and animated GIFs).</summary>
        public bool IsPaused
        {
            get => _paused;
            set { if (SetField(ref _paused, value)) OnPropertyChanged(nameof(PlaybackGlyph)); }
        }

        /// <summary>Why the media could not be displayed (damaged/unsupported file), or null.</summary>
        [JsonIgnore]
        public string? LoadError
        {
            get => _loadError;
            set { if (SetField(ref _loadError, value)) OnPropertyChanged(nameof(HasLoadError)); }
        }

        [JsonIgnore] public bool HasLoadError => LoadError != null;

        /// <summary>Opacity of the media (0.1 – 1).</summary>
        public double Opacity
        {
            get => _opacity;
            set => SetField(ref _opacity, Math.Clamp(Finite(value, _opacity), 0.1, 1));
        }

        /// <summary>Move/resize mode. While editing, a desktop widget is temporarily lifted above windows.</summary>
        [JsonIgnore]
        public bool IsEditing
        {
            get => _editing;
            set => SetField(ref _editing, value);
        }

        [JsonIgnore]
        public string FullPath => Path.IsPathRooted(Source)
            ? Source
            : Path.Combine(Services.AppPaths.DataDirectory, Source);

        // Bindings for the Widget Manager.
        [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? FileName : Name;
        [JsonIgnore] public bool IsGif => Type == WidgetType.Image && MediaFormats.IsGif(FileName);
        [JsonIgnore] public bool IsPlayable => Type == WidgetType.Video || IsGif;

        [JsonIgnore]
        public string TypeLabel => Type == WidgetType.Video ? "VÍDEO" : IsGif ? "GIF" : "IMAGEN";

        [JsonIgnore]
        public string TypeGlyph => Type == WidgetType.Video ? "" : "";

        [JsonIgnore]
        public bool IsShown
        {
            get => !Hidden;
            set => Hidden = !value;
        }

        [JsonIgnore]
        public bool IsAlwaysVisible
        {
            get => Layer == WidgetLayer.AlwaysVisible;
            set { if (value) Layer = WidgetLayer.AlwaysVisible; }
        }

        [JsonIgnore]
        public bool IsDesktopIntegrated
        {
            get => Layer == WidgetLayer.DesktopIntegrated;
            set { if (value) Layer = WidgetLayer.DesktopIntegrated; }
        }

        [JsonIgnore]
        public string LayerLabel => Layer == WidgetLayer.AlwaysVisible ? "Siempre visible" : "Integrado en el escritorio";

        [JsonIgnore] public string LayerGlyph => Layer == WidgetLayer.AlwaysVisible ? "" : "";
        [JsonIgnore] public string LockIcon => Locked ? "" : "";
        [JsonIgnore] public string VisibilityIcon => Hidden ? "" : "";
        [JsonIgnore] public string PlaybackGlyph => IsPaused ? "" : "";

        public event PropertyChangedEventHandler? PropertyChanged;

        private static double Finite(double value, double fallback) =>
            double.IsFinite(value) ? value : fallback;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
