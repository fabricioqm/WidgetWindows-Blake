using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WidgetWindowDemo.Models;
using WidgetWindowDemo.Services;
using WpfAnimatedGif;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace WidgetWindowDemo
{
    /// <summary>How a widget window is built. It never changes during the window's life.</summary>
    public enum WidgetHostKind
    {
        /// <summary>Top-level, per-pixel transparent window ("Siempre visible", and any widget being edited).</summary>
        TopLevel,

        /// <summary>
        /// "Integrado en el escritorio": also a per-pixel transparent top-level window, kept directly above the
        /// shell's desktop window (see <see cref="DesktopHost"/>) and never interactive.
        /// </summary>
        Desktop
    }

    /// <summary>Where a widget's media was when its window was replaced, so the new one continues from there.</summary>
    public readonly record struct PlaybackState(TimeSpan? VideoPosition, int? GifFrame);

    public partial class WidgetWindow : Window
    {
        public WidgetInfo Info { get; }

        /// <summary>Fixed at creation. Changing between kinds means replacing the window (see <see cref="KindFor"/>).</summary>
        public WidgetHostKind Kind { get; }

        /// <summary>The window kind a widget needs right now. Editing lifts a desktop widget so it can be moved.</summary>
        public static WidgetHostKind KindFor(WidgetInfo info) =>
            info.Layer == WidgetLayer.DesktopIntegrated && !info.IsEditing ? WidgetHostKind.Desktop : WidgetHostKind.TopLevel;

        private readonly PlaybackState? _resumeFrom;
        private HwndSource? _hwndSource;
        private ContextMenu? _contextMenu;

        private bool _ready;          // native window exists
        private bool _closing;        // window is being closed on purpose (or was destroyed)
        private bool _cleanedUp;
        private bool _syncingBounds;  // Info is being updated from the native window: don't move it back

        /// <summary>
        /// Desktop widgets: second placement shortly after a foreground change. Explorer lowers the desktop window
        /// again (after Win + D) a moment after the new window is activated; this catches up with that move.
        /// One-shot, restarted per event: not a polling loop.
        /// </summary>
        private readonly DispatcherTimer _settleTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

        // Current drag operation: "M" = move, "L","R","T","B","TL","TR","BL","BR" = resize
        private string? _op;
        private FrameworkElement? _captured;
        private NativeMethods.POINT _startCursor;
        private NativeMethods.RECT _startRect;

        private IntPtr Handle => new WindowInteropHelper(this).Handle;
        private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

        public WidgetWindow(WidgetInfo info, PlaybackState? resumeFrom = null)
        {
            InitializeComponent();
            Info = info;
            Kind = KindFor(info);
            _resumeFrom = resumeFrom;

            MinWidth = WidgetInfo.MinWidth;
            MinHeight = WidgetInfo.MinHeight;
            Left = info.Left;
            Top = info.Top;
            Width = info.Width;
            Height = info.Height;

            Topmost = Kind == WidgetHostKind.TopLevel;
            _settleTimer.Tick += (_, _) =>
            {
                _settleTimer.Stop();
                if (ShouldStayVisible) ApplyLayer();
            };

            Info.PropertyChanged += Info_PropertyChanged;
            SourceInitialized += WidgetWindow_SourceInitialized;
            Loaded += WidgetWindow_Loaded;
            ImageBehavior.AddAnimationLoadedHandler(ImageViewer, (_, _) =>
            {
                ResumeGifFrame();
                ApplyPlayback();
            });

            // Move (whole widget) and resize (handles) share the same handlers.
            AttachOperation(Root);
            foreach (var handle in ResizeOverlay.Children.OfType<Border>().Where(b => b.Tag is string))
                AttachOperation(handle);
        }

        #region Lifecycle

        private void WidgetWindow_SourceInitialized(object? sender, EventArgs e)
        {
            _hwndSource = HwndSource.FromHwnd(Handle);
            _hwndSource?.AddHook(WndProc);

            // No minimize/restore animation, so a "Show desktop" attempt never flashes.
            int disable = 1;
            NativeMethods.DwmSetWindowAttribute(Handle,
                NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, ref disable, sizeof(int));

            _ready = true;
            ShellEvents.ForegroundChanged += ShellEvents_ForegroundChanged;
            ShellEvents.DesktopChanged += ShellEvents_DesktopChanged;
            ShellEvents.ShowDesktopShortcut += ShellEvents_ForegroundChanged;
            ShellEvents.ShowDesktopShortcutStarting += ShellEvents_ShowDesktopShortcutStarting;

            EnsureOnScreen();
            ApplyLayer();
            ApplyInteraction();
        }

        private void WidgetWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (MediaFormats.IsImage(Info.FullPath))
                    LoadImage();
                else if (MediaFormats.IsVideo(Info.FullPath))
                    LoadVideo();
                else
                    throw new NotSupportedException($"El formato {Path.GetExtension(Info.FullPath)} no es compatible.");

                Info.LoadError = null;
                ApplyBoundsFromInfo();
                ApplyAppearance();
                ApplyPlayback();
            }
            catch (Exception ex)
            {
                // Controlled failure: the file is identified in the manager and only this widget closes.
                Info.LoadError = ex.Message;
                ErrorReporter.Report("No se puede mostrar el widget",
                    $"«{Info.DisplayName}» no se puede mostrar.\n\nMotivo: {ex.Message}\n\n" +
                    "El archivo original no se ha modificado. Elimina este widget y vuelve a importar el archivo, " +
                    "o conviértelo a un formato válido con un editor e importa la copia.", ex);
                Close();
            }
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState != WindowState.Normal)
                WindowState = WindowState.Normal;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _closing = true; // from now on, hide/minimize messages are legitimate
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            Cleanup();
            base.OnClosed(e);
        }

        /// <summary>Releases every subscription and the media file. Safe to call more than once.</summary>
        private void Cleanup()
        {
            if (_cleanedUp) return;
            _cleanedUp = true;

            ShellEvents.ForegroundChanged -= ShellEvents_ForegroundChanged;
            ShellEvents.DesktopChanged -= ShellEvents_DesktopChanged;
            ShellEvents.ShowDesktopShortcut -= ShellEvents_ForegroundChanged;
            ShellEvents.ShowDesktopShortcutStarting -= ShellEvents_ShowDesktopShortcutStarting;
            ShowDesktopShield.Lower(Handle);
            Info.PropertyChanged -= Info_PropertyChanged;
            _settleTimer.Stop();
            _hwndSource?.RemoveHook(WndProc);
            VideoViewer.Close(); // releases the file
        }

        private void Info_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(WidgetInfo.Locked):
                    ApplyInteraction();
                    break;
                case nameof(WidgetInfo.Layer):
                case nameof(WidgetInfo.IsEditing):
                    // A change of window kind is handled by the owner, which replaces this window.
                    if (KindFor(Info) == Kind) ApplyInteraction();
                    break;
                case nameof(WidgetInfo.Hidden):
                    ApplyVisibility();
                    break;
                case nameof(WidgetInfo.Left):
                case nameof(WidgetInfo.Top):
                case nameof(WidgetInfo.Width):
                case nameof(WidgetInfo.Height):
                    ApplyBoundsFromInfo();
                    break;
                case nameof(WidgetInfo.Opacity):
                    ApplyAppearance();
                    break;
                case nameof(WidgetInfo.IsPaused):
                    ApplyPlayback();
                    break;
            }
        }

        private void ApplyVisibility()
        {
            // First time: let WPF create and show the window.
            if (!_ready)
            {
                if (!Info.Hidden) Show();
                return;
            }

            if (Info.Hidden)
            {
                Info.IsEditing = false;
                ShowDesktopShield.Lower(Handle);
            }

            // Afterwards use raw Win32: it doesn't change position or z-order state.
            NativeMethods.ShowWindow(Handle, Info.Hidden ? NativeMethods.SW_HIDE : NativeMethods.SW_SHOWNOACTIVATE);
            ApplyPlayback();

            if (!Info.Hidden) ApplyLayer();
        }

        #endregion

        #region Content

        private void LoadImage()
        {
            ImageViewer.Visibility = Visibility.Visible;
            VideoViewer.Visibility = Visibility.Collapsed;

            // Damaged GIFs are refused on import; this catches files damaged afterwards, with a clear reason.
            bool isGif = MediaFormats.IsGif(Info.FullPath);
            if (isGif && GifInspector.Inspect(Info.FullPath) is { IsValid: false } gif)
                throw new InvalidDataException(gif.Error);

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(Info.FullPath, UriKind.Absolute);
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();

            if (isGif)
            {
                // GIFs always loop, whatever the file says. Set before the source so the animation is built once.
                ImageBehavior.SetRepeatBehavior(ImageViewer, RepeatBehavior.Forever);
                ImageBehavior.SetAnimatedSource(ImageViewer, image);
            }
            else
            {
                ImageViewer.Source = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
            }
        }

        private void LoadVideo()
        {
            ImageViewer.Visibility = Visibility.Collapsed;
            VideoViewer.Visibility = Visibility.Visible;
            VideoViewer.Source = new Uri(Info.FullPath, UriKind.Absolute);
            if (_resumeFrom?.VideoPosition is TimeSpan position) VideoViewer.Position = position;
            VideoViewer.Play();
        }

        private void VideoViewer_MediaEnded(object sender, RoutedEventArgs e)
        {
            VideoViewer.Position = TimeSpan.Zero; // loop
            if (!Info.IsPaused && !Info.Hidden) VideoViewer.Play();
        }

        /// <summary>
        /// Current video position / GIF frame, to hand over to a replacement window. Null where there is nothing
        /// to continue: the library reports frame -1 while the animation hasn't started yet (e.g. the window was
        /// replaced again right after being created, when the display mode is switched quickly).
        /// </summary>
        public PlaybackState CapturePlayback()
        {
            int? frame = ImageBehavior.GetAnimationController(ImageViewer)?.CurrentFrame;
            return new PlaybackState(
                Info.Type == WidgetType.Video && VideoViewer.Source != null ? VideoViewer.Position : null,
                frame > 0 ? frame : null);
        }

        /// <summary>
        /// Continues a replaced window's GIF from the same frame. Only a convenience: if it can't be done the GIF
        /// simply starts from the beginning — it must never be reported as a problem with the file.
        /// </summary>
        private void ResumeGifFrame()
        {
            if (_resumeFrom?.GifFrame is not int frame) return;

            try
            {
                if (ImageBehavior.GetAnimationController(ImageViewer) is { } controller &&
                    frame >= 0 && frame < controller.FrameCount)
                    controller.GotoFrame(frame);
            }
            catch (Exception ex)
            {
                AppLog.Warning($"«{Info.DisplayName}»: no se pudo continuar el GIF en el fotograma {frame}; empieza desde el principio.", ex);
            }
        }

        private void ApplyAppearance()
        {
            ImageViewer.Opacity = Info.Opacity;
            VideoViewer.Opacity = Info.Opacity;
        }

        /// <summary>Plays or pauses videos and animated GIFs. Hidden widgets never play.</summary>
        private void ApplyPlayback()
        {
            bool play = !Info.IsPaused && !Info.Hidden;

            if (Info.Type == WidgetType.Video)
            {
                if (VideoViewer.Source == null) return;
                if (play) VideoViewer.Play(); else VideoViewer.Pause();
            }
            else if (ImageBehavior.GetAnimationController(ImageViewer) is { } controller)
            {
                if (play) controller.Play(); else controller.Pause();
            }
        }

        #endregion

        #region Interaction (lock, edit mode, context menu)

        /// <summary>
        /// Whether the widget reacts to the mouse. Desktop widgets never do (clicks reach the desktop and its icons);
        /// to move one, edit mode replaces it with an always-on-top window for a while.
        /// </summary>
        private bool IsInteractive => Kind == WidgetHostKind.TopLevel && (Info.IsEditing || !Info.Locked);

        private void ApplyInteraction()
        {
            if (IsInteractive)
            {
                Root.ContextMenu = _contextMenu ??= BuildContextMenu();
                Root.Cursor = System.Windows.Input.Cursors.SizeAll;
            }
            else
            {
                EndOperation();
                Root.ContextMenu = null;
                Root.Cursor = System.Windows.Input.Cursors.Arrow;
            }

            ResizeOverlay.Visibility = Info.IsEditing ? Visibility.Visible : Visibility.Collapsed;
            ApplyExStyles();
        }

        private void ApplyExStyles()
        {
            var handle = Handle;
            if (handle == IntPtr.Zero) return;

            int ex = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;

            // Non-interactive = mouse clicks go "through" the widget (to the window or icons below).
            if (IsInteractive) ex &= ~NativeMethods.WS_EX_TRANSPARENT;
            else ex |= NativeMethods.WS_EX_TRANSPARENT;

            NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE, ex);
        }

        private ContextMenu BuildContextMenu()
        {
            var editItem = new MenuItem();
            editItem.Click += (_, _) => Info.IsEditing = !Info.IsEditing;

            var layerItem = new MenuItem();
            layerItem.Click += (_, _) => Info.Layer = Info.Layer == WidgetLayer.AlwaysVisible
                ? WidgetLayer.DesktopIntegrated
                : WidgetLayer.AlwaysVisible;

            var lockItem = new MenuItem { Header = "Bloquear (los clics lo atraviesan)" };
            lockItem.Click += (_, _) => Info.Locked = true; // to unlock: use the Widget Manager

            var hideItem = new MenuItem { Header = "Ocultar" };
            hideItem.Click += (_, _) => Info.Hidden = true;

            var menu = new ContextMenu();
            menu.Items.Add(editItem);
            menu.Items.Add(layerItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(lockItem);
            menu.Items.Add(hideItem);
            menu.Opened += (_, _) =>
            {
                editItem.Header = Info.IsEditing ? "Terminar edición" : "Mover y redimensionar";
                layerItem.Header = Info.Layer == WidgetLayer.AlwaysVisible
                    ? "Cambiar a «Integrado en el escritorio»"
                    : "Cambiar a «Siempre visible»";
            };

            return menu;
        }

        #endregion

        #region Move and resize (no Aero Snap)

        private void AttachOperation(FrameworkElement element)
        {
            element.MouseLeftButtonDown += Op_MouseLeftButtonDown;
            element.MouseMove += Op_MouseMove;
            element.MouseLeftButtonUp += (_, _) => EndOperation();
            element.LostMouseCapture += (_, _) => EndOperation();
        }

        private void Op_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsInteractive || sender is not FrameworkElement element) return;

            // Double-click on the widget ends edit mode.
            if (e.ClickCount == 2 && Info.IsEditing && ReferenceEquals(element, Root))
            {
                Info.IsEditing = false;
                e.Handled = true;
                return;
            }

            if (element.Tag is not string op) return;

            _op = op;
            _captured = element;
            NativeMethods.GetCursorPos(out _startCursor);
            NativeMethods.GetWindowRect(Handle, out _startRect);
            element.CaptureMouse();
            e.Handled = true;
        }

        private void Op_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_op == null) return;

            NativeMethods.GetCursorPos(out var cur);
            int dx = cur.x - _startCursor.x;
            int dy = cur.y - _startCursor.y;
            var r = _startRect;

            if (_op == "M")
            {
                SetBoundsScreenPx(r.left + dx, r.top + dy, r.Width, r.Height);
            }
            else
            {
                int minW = (int)(MinWidth * Scale);
                int minH = (int)(MinHeight * Scale);
                int l = r.left, t = r.top, rt = r.right, b = r.bottom;

                if (_op.Contains('L')) l = Math.Min(r.left + dx, r.right - minW);
                if (_op.Contains('R')) rt = Math.Max(r.right + dx, r.left + minW);
                if (_op.Contains('T')) t = Math.Min(r.top + dy, r.bottom - minH);
                if (_op.Contains('B')) b = Math.Max(r.bottom + dy, r.top + minH);

                SetBoundsScreenPx(l, t, rt - l, b - t);
            }

            e.Handled = true;
        }

        private void EndOperation()
        {
            if (_op == null) return;
            _op = null;

            SyncInfoFromNative();
            _captured?.ReleaseMouseCapture();
            _captured = null;
        }

        /// <summary>Moves/resizes using screen coordinates in pixels.</summary>
        private void SetBoundsScreenPx(int x, int y, int w, int h)
        {
            NativeMethods.SetWindowPos(Handle, IntPtr.Zero, x, y, w, h,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        /// <summary>Stores the real screen position/size in Info (in WPF units).</summary>
        private void SyncInfoFromNative()
        {
            if (!NativeMethods.GetWindowRect(Handle, out var r)) return;

            var dpi = VisualTreeHelper.GetDpi(this);
            _syncingBounds = true;
            try
            {
                Info.Left = r.left / dpi.DpiScaleX;
                Info.Top = r.top / dpi.DpiScaleY;
                Info.Width = r.Width / dpi.DpiScaleX;
                Info.Height = r.Height / dpi.DpiScaleY;
            }
            finally
            {
                _syncingBounds = false;
            }
        }

        /// <summary>Applies position/size edited in the manager to the window.</summary>
        private void ApplyBoundsFromInfo()
        {
            if (!_ready || _syncingBounds) return;

            var dpi = VisualTreeHelper.GetDpi(this);
            SetBoundsScreenPx(
                (int)Math.Round(Info.Left * dpi.DpiScaleX),
                (int)Math.Round(Info.Top * dpi.DpiScaleY),
                (int)Math.Round(Info.Width * dpi.DpiScaleX),
                (int)Math.Round(Info.Height * dpi.DpiScaleY));
        }

        /// <summary>If the saved position is on a monitor that no longer exists, bring the widget back.</summary>
        private void EnsureOnScreen()
        {
            if (!NativeMethods.GetWindowRect(Handle, out var r)) return;
            if (NativeMethods.MonitorFromRect(ref r, NativeMethods.MONITOR_DEFAULTTONULL) != IntPtr.Zero) return;

            var area = SystemParameters.WorkArea;
            Info.Left = area.Left + 40;
            Info.Top = area.Top + 40;
        }

        #endregion

        #region Layer: "Siempre visible" / "Integrado en el escritorio"

        private bool ShouldStayVisible => _ready && !_closing && !Info.Hidden;

        /// <summary>
        /// Puts the window in the z-order its kind requires. The kind never changes: switching between
        /// "Siempre visible" and "Integrado en el escritorio" replaces the window.
        /// <list type="bullet">
        /// <item>Desktop: directly above the shell's desktop window. Normally that is behind every application
        /// window; when "Show desktop" (Win + D) raises the desktop, the widget is shown with it.</item>
        /// <item>TopLevel: topmost band. Other topmost windows (taskbar, some full-screen apps) go above it when
        /// they are activated, so the position is re-asserted on every foreground change, not by polling.</item>
        /// </list>
        /// </summary>
        private void ApplyLayer()
        {
            if (!_ready) return;
            if (ShowDesktopShield.IsShielded(Handle)) return; // Win + D in progress: stay shielded until released

            if (Kind == WidgetHostKind.Desktop)
            {
                Topmost = false;
                if (!DesktopHost.PlaceAboveDesktop(Handle))
                {
                    // Explorer not running: bottom of the normal band until it is back (DesktopChanged).
                    NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                }
            }
            else
            {
                Topmost = true;
                BringToTopmost();
            }

            ApplyExStyles();
        }

        private void BringToTopmost() =>
            NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);

        /// <summary>
        /// Another window became foreground, so the z-order just changed: an "always visible" widget goes back on
        /// top; a desktop widget follows the desktop window (raised by Win + D, lowered again afterwards).
        /// </summary>
        private void ShellEvents_ForegroundChanged(object? sender, EventArgs e)
        {
            if (!ShouldStayVisible) return;

            ApplyLayer();
            if (Kind == WidgetHostKind.Desktop)
            {
                _settleTimer.Stop();
                _settleTimer.Start();
            }
        }

        /// <summary>
        /// Win + D is about to show the desktop: a desktop widget is shielded so the raised desktop never covers it,
        /// even for a moment. (Always-visible widgets are topmost already.)
        /// </summary>
        private void ShellEvents_ShowDesktopShortcutStarting(object? sender, bool desktopShown)
        {
            if (desktopShown || Kind != WidgetHostKind.Desktop || !ShouldStayVisible) return;

            // When the shield comes down (desktop lowered again), go back right above the desktop.
            ShowDesktopShield.Raise(Handle, () => { if (ShouldStayVisible) ApplyLayer(); });
        }

        /// <summary>Explorer was recreated, or the wallpaper/display changed.</summary>
        private void ShellEvents_DesktopChanged(object? sender, EventArgs e)
        {
            if (ShouldStayVisible) ApplyLayer();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                // Someone asked to minimize us ("Minimize all", ...): refuse.
                case NativeMethods.WM_SYSCOMMAND:
                    if (ShouldStayVisible && (wParam.ToInt64() & 0xFFF0) == NativeMethods.SC_MINIMIZE)
                        handled = true;
                    break;

                case NativeMethods.WM_WINDOWPOSCHANGING:
                    {
                        var pos = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
                        bool changed = false;

                        if (ShouldStayVisible)
                        {
                            // Strip "hide window" requests.
                            if ((pos.flags & NativeMethods.SWP_HIDEWINDOW) != 0)
                            {
                                pos.flags &= ~NativeMethods.SWP_HIDEWINDOW;
                                changed = true;
                            }

                            // Strip the "move off-screen to minimize" position (-32000,-32000).
                            if ((pos.flags & NativeMethods.SWP_NOMOVE) == 0 && pos.x <= -30000 && pos.y <= -30000)
                            {
                                pos.flags |= NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE;
                                changed = true;
                            }
                        }

                        if (changed)
                            Marshal.StructureToPtr(pos, lParam, false);
                        break;
                    }

                // If it still got minimized or hidden, restore immediately (before the next frame).
                case NativeMethods.WM_SIZE:
                    if (wParam.ToInt32() == NativeMethods.SIZE_MINIMIZED && ShouldStayVisible)
                        Dispatcher.BeginInvoke(RestoreIfHiddenByShell, DispatcherPriority.Send);
                    break;

                case NativeMethods.WM_SHOWWINDOW:
                    if (wParam == IntPtr.Zero && ShouldStayVisible)
                        Dispatcher.BeginInvoke(RestoreIfHiddenByShell, DispatcherPriority.Send);
                    break;

            }

            return IntPtr.Zero;
        }

        private void RestoreIfHiddenByShell()
        {
            if (!ShouldStayVisible) return;

            var handle = Handle;
            if (NativeMethods.IsIconic(handle))
            {
                NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
                ApplyLayer();
            }
            else if (!NativeMethods.IsWindowVisible(handle))
            {
                NativeMethods.ShowWindow(handle, NativeMethods.SW_SHOWNOACTIVATE);
                ApplyLayer();
            }
        }

        #endregion
    }
}
