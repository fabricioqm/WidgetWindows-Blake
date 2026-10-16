using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WidgetWindowDemo.Models;
using WidgetWindowDemo.Services;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using WidgetWindowDemo.Views;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace WidgetWindowDemo
{
    public partial class MainWindow : Window
    {
        private static readonly string StoragePath = AppPaths.WidgetsFile;
        private static readonly string AssetsDir = AppPaths.MediaDirectory;

        /// <summary>Properties stored in widgets.json. Changes are written after a short pause.</summary>
        private static readonly HashSet<string> PersistedProperties = new()
        {
            nameof(WidgetInfo.Name), nameof(WidgetInfo.Layer), nameof(WidgetInfo.Locked), nameof(WidgetInfo.Hidden),
            nameof(WidgetInfo.IsPaused), nameof(WidgetInfo.Opacity),
            nameof(WidgetInfo.Left), nameof(WidgetInfo.Top), nameof(WidgetInfo.Width), nameof(WidgetInfo.Height)
        };

        public ObservableCollection<WidgetInfo> Widgets { get; } = new();

        private readonly Dictionary<Guid, WidgetWindow> _windows = new();
        private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly DispatcherTimer _desktopChangedTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
        private bool _importing;
        private ICollectionView? _view;
        private string _filter = "All";
        private NotifyIcon trayIcon = null!;
        private bool reallyClose;

        public MainWindow()
        {
            InitializeComponent();
            InitializeTrayIcon();
            ShellEvents.Start();

            _view = CollectionViewSource.GetDefaultView(Widgets);
            _view.Filter = MatchesFilter;
            if (_view is ICollectionViewLiveShaping { CanChangeLiveFiltering: true } live)
            {
                live.LiveFilteringProperties.Add(nameof(WidgetInfo.Layer));
                live.LiveFilteringProperties.Add(nameof(WidgetInfo.Hidden));
                live.IsLiveFiltering = true;
            }
            ListWidgets.ItemsSource = _view;
            Widgets.CollectionChanged += (_, _) => UpdateSummary();

            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveWidgetsToDisk();
            };
            _desktopChangedTimer.Tick += (_, _) =>
            {
                _desktopChangedTimer.Stop();
                ShellEvents.NotifyDesktopChanged();
            };

            // Win + D / Win + M must not hide the manager: it is shielded before Explorer acts, so it never flashes
            // (see ShowDesktopShield). If the shortcut slips past the shield, WndProc still undoes the minimize.
            ShellEvents.ShowDesktopShortcutStarting += (_, desktopShown) =>
            {
                if (desktopShown || !IsVisible) return;
                var hwnd = new WindowInteropHelper(this).Handle;
                if (!NativeMethods.IsIconic(hwnd)) ShowDesktopShield.Raise(hwnd);
            };

            // Windows shutting down, or the installer closing the app to update it: save and really exit
            // (closing the window otherwise only hides it to the tray).
            if (System.Windows.Application.Current is { } application)
            {
                application.SessionEnding += (_, _) =>
                {
                    AppLog.Info("Cierre solicitado por Windows (apagado o instalador).");
                    SaveWidgetsToDisk();
                    reallyClose = true;
                    trayIcon.Visible = false;
                };
            }

            SourceInitialized += MainWindow_SourceInitialized;
            Loaded += (_, _) =>
            {
                LoadWidgetsFromDisk();
                UpdateSummary();
            };
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

            // Dark title bar to match the window (Windows 10 1809+; caption color on Windows 11). Ignored elsewhere.
            int dark = 1;
            if (NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));

            int captionColor = 0x001B1313; // #13131B as COLORREF (0x00BBGGRR)
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Win + D / Win + M: refuse the minimize Windows sends right after the shortcut. A normal minimize
            // (title bar button, taskbar click) is not affected.
            if (ShellEvents.ShowDesktopShortcutRecently)
            {
                if (msg == NativeMethods.WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == NativeMethods.SC_MINIMIZE)
                {
                    handled = true;
                    return IntPtr.Zero;
                }

                if (msg == NativeMethods.WM_SIZE && wParam.ToInt32() == NativeMethods.SIZE_MINIMIZED)
                    Dispatcher.BeginInvoke(KeepVisibleAfterShowDesktop, DispatcherPriority.Send);

            }

            // While shielded, Explorer's Win + D handling must not touch the manager: it would take it out of the
            // topmost band (HWND_NOTOPMOST on the active window), hide it or move it off-screen. Only the shield's
            // own topmost request goes through; the shield lowers itself after removing the window from its list.
            if (msg == NativeMethods.WM_WINDOWPOSCHANGING && ShowDesktopShield.IsShielded(hwnd))
            {
                var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
                bool changed = false;

                if ((pos.flags & NativeMethods.SWP_NOZORDER) == 0 && pos.hwndInsertAfter != NativeMethods.HWND_TOPMOST)
                {
                    pos.flags |= NativeMethods.SWP_NOZORDER;
                    changed = true;
                }
                if ((pos.flags & NativeMethods.SWP_HIDEWINDOW) != 0)
                {
                    pos.flags &= ~NativeMethods.SWP_HIDEWINDOW;
                    changed = true;
                }
                if ((pos.flags & NativeMethods.SWP_NOMOVE) == 0 && pos.x <= -30000 && pos.y <= -30000)
                {
                    pos.flags |= NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE;
                    changed = true;
                }

                if (changed)
                    System.Runtime.InteropServices.Marshal.StructureToPtr(pos, lParam, false);
            }

            // Explorer restarted, or the wallpaper/display changed (Explorer may then move the wallpaper above the
            // widgets). Give it a moment to settle, then let desktop widgets put themselves back in place.
            if (msg == ShellEvents.TaskbarCreatedMessage ||
                msg is NativeMethods.WM_SETTINGCHANGE or NativeMethods.WM_DISPLAYCHANGE)
            {
                _desktopChangedTimer.Stop();
                _desktopChangedTimer.Start();
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// After Win + D / Win + M: if the manager was minimized, restore it; if the raised desktop covers it,
        /// put it right above the desktop. Never activates it (the user's focus is left alone).
        /// </summary>
        private void KeepVisibleAfterShowDesktop()
        {
            if (!IsVisible) return; // closed to the tray: nothing to keep

            var hwnd = new WindowInteropHelper(this).Handle;
            if (ShowDesktopShield.IsShielded(hwnd)) return; // already protected
            if (NativeMethods.IsIconic(hwnd))
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);

            if (DesktopHost.IsAbove(DesktopHost.FindDesktopWindow(), hwnd))
                DesktopHost.PlaceAboveDesktop(hwnd);
        }

        #region Persistence

        private void LoadWidgetsFromDisk()
        {
            AppPaths.MigrateFromProgramFolder();
            if (!File.Exists(StoragePath)) return;

            try
            {
                var loaded = JsonSerializer.Deserialize<WidgetInfo[]>(File.ReadAllText(StoragePath));
                if (loaded == null) return;

                foreach (var info in loaded)
                    RegisterWidget(info);
            }
            catch (Exception ex)
            {
                ErrorReporter.Report("Error al cargar los widgets",
                    "No se pudo leer widgets.json: " + ex.Message, ex);
            }
        }

        private void SaveWidgetsToDisk()
        {
            _saveTimer.Stop();

            try
            {
                // Widget windows keep Info (position/size) in sync.
                var json = JsonSerializer.Serialize(Widgets, new JsonSerializerOptions { WriteIndented = true });
                Directory.CreateDirectory(AppPaths.DataDirectory);
                File.WriteAllText(StoragePath, json);
            }
            catch (Exception ex)
            {
                ErrorReporter.Report("Error al guardar los widgets",
                    "No se pudo escribir widgets.json: " + ex.Message, ex);
            }
        }

        private void ScheduleSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void WidgetInfo_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not WidgetInfo info || e.PropertyName is not { } property) return;

            if (PersistedProperties.Contains(property))
                ScheduleSave();

            // "Siempre visible" and "Integrado en el escritorio" (and editing a desktop widget) need different
            // kinds of window: replace it instead of moving one window in and out of the desktop.
            if (property is nameof(WidgetInfo.Layer) or nameof(WidgetInfo.IsEditing) &&
                _windows.TryGetValue(info.Id, out var current) && current.Kind != WidgetWindow.KindFor(info))
            {
                ReplaceWidgetWindow(info, current);
            }

            if (property is nameof(WidgetInfo.Hidden) or nameof(WidgetInfo.Layer))
            {
                // The window could never be opened (e.g. missing file): showing it again retries.
                if (property == nameof(WidgetInfo.Hidden) && !info.Hidden && !_windows.ContainsKey(info.Id))
                    OpenWidgetWindow(info);

                // Live filtering moves only this card in/out of the view (a Refresh() would rebuild every card
                // and restart every GIF preview). Count after the view has processed the change.
                Dispatcher.BeginInvoke(UpdateSummary, DispatcherPriority.Background);
            }
        }

        #endregion

        #region Widgets

        private void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Importar imágenes o vídeos",
                Filter = MediaFormats.OpenFileFilter,
                Multiselect = true
            };

            if (dlg.ShowDialog(this) == true)
                ImportFiles(dlg.FileNames);
        }

        /// <summary>
        /// Imports files after checking each one can really be displayed. Damaged or unsupported files are not
        /// copied (the originals are never modified); the user gets one dialog listing them with the reason.
        /// </summary>
        private async void ImportFiles(IEnumerable<string> paths)
        {
            if (_importing) return;
            _importing = true;
            Cursor = System.Windows.Input.Cursors.AppStarting;

            WidgetInfo? lastImported = null;
            var rejected = new List<string>();

            try
            {
                foreach (var path in paths)
                {
                    string? problem = MediaFormats.IsSupported(path)
                        ? await MediaValidator.ValidateAsync(path)
                        : "formato no compatible.";

                    if (problem != null)
                    {
                        rejected.Add($"{Path.GetFileName(path)}: {problem}");
                        continue;
                    }

                    try
                    {
                        lastImported = ImportValidatedFile(path);
                    }
                    catch (Exception ex)
                    {
                        rejected.Add($"{Path.GetFileName(path)}: no se pudo copiar ({ex.Message.TrimEnd('.')}).");
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorReporter.Report("Error al importar", ex.Message, ex);
            }
            finally
            {
                _importing = false;
                Cursor = null;
            }

            foreach (var line in rejected)
                AppLog.Warning("Importación rechazada: " + line);

            if (rejected.Count > 0)
            {
                AppDialog.Alert(DialogKind.Warning,
                    rejected.Count == 1 ? "No se importó un archivo" : $"No se importaron {rejected.Count} archivos",
                    "Estos archivos están dañados o no son compatibles, así que no se han añadido. " +
                    "Los originales no se han modificado: puedes repararlos o convertirlos con un editor e importar la copia.",
                    rejected, this);
            }

            if (lastImported != null)
            {
                SaveWidgetsToDisk();
                NavAll.IsChecked = true;
                ListWidgets.SelectedItem = lastImported;
                ListWidgets.ScrollIntoView(lastImported);
            }
        }

        /// <summary>Copies an already validated file into the app and creates its widget.</summary>
        private WidgetInfo ImportValidatedFile(string path)
        {
            string fileName = $"{Guid.NewGuid():N}{Path.GetExtension(path).ToLowerInvariant()}";
            Directory.CreateDirectory(AssetsDir);
            File.Copy(path, Path.Combine(AssetsDir, fileName), overwrite: true);

            // Cascade new widgets so they don't land exactly on top of each other.
            var area = SystemParameters.WorkArea;
            double offset = (Widgets.Count % 8) * 32;
            var info = new WidgetInfo
            {
                Type = MediaFormats.IsVideo(path) ? WidgetType.Video : WidgetType.Image,
                Source = Path.Combine("Assets", "Widgets", fileName),
                FileName = fileName,
                Name = Path.GetFileNameWithoutExtension(path),
                Left = area.Left + 80 + offset,
                Top = area.Top + 80 + offset
            };

            RegisterWidget(info);
            AppLog.Info($"Importado «{info.Name}» ({info.TypeLabel}) como {fileName}.");
            return info;
        }

        private void RegisterWidget(WidgetInfo info)
        {
            info.PropertyChanged += WidgetInfo_PropertyChanged;
            Widgets.Add(info);
            OpenWidgetWindow(info);
        }

        /// <summary>Opens the one desktop window for a widget (never two for the same widget).</summary>
        private void OpenWidgetWindow(WidgetInfo info, PlaybackState? resumeFrom = null)
        {
            if (_windows.ContainsKey(info.Id)) return;

            try
            {
                if (!File.Exists(info.FullPath))
                {
                    info.LoadError = "No se encuentra el archivo.";
                    ErrorReporter.Report("Archivo no encontrado",
                        $"No se encuentra el archivo de «{info.DisplayName}»:\n{info.FullPath}\n\nVuelve a importarlo.");
                    return;
                }

                var window = new WidgetWindow(info, resumeFrom);
                window.Closed += (_, _) =>
                {
                    // Closed after a load error (or on purpose): forget it so "show" can retry later.
                    if (_windows.TryGetValue(info.Id, out var current) && current == window)
                        _windows.Remove(info.Id);
                };
                _windows[info.Id] = window;

                if (!info.Hidden)
                    window.Show();
            }
            catch (Exception ex)
            {
                info.LoadError = ex.Message;
                ErrorReporter.Report("Error al abrir el widget", $"«{info.DisplayName}»: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Replaces a widget's window with one of the kind it now needs, continuing playback where it was.
        /// </summary>
        private void ReplaceWidgetWindow(WidgetInfo info, WidgetWindow old)
        {
            AppLog.Info($"«{info.DisplayName}»: modo {(info.IsEditing ? "edición" : info.LayerLabel)}.");

            PlaybackState? playback = null;
            try
            {
                playback = old.CapturePlayback();
            }
            catch (Exception ex)
            {
                AppLog.Warning($"«{info.DisplayName}»: no se pudo leer la posición de reproducción.", ex);
            }

            _windows.Remove(info.Id);
            old.Close();
            OpenWidgetWindow(info, playback);
        }

        private void RemoveWidget(WidgetInfo info)
        {
            if (ReferenceEquals(ListWidgets.SelectedItem, info))
                ListWidgets.SelectedItem = null; // also releases the preview video

            info.PropertyChanged -= WidgetInfo_PropertyChanged;

            if (_windows.Remove(info.Id, out var window))
            {
                window.Close();
            }

            Widgets.Remove(info);
            ThumbnailConverter.Evict(info.FullPath);
            GifInspector.Evict(info.FullPath);
            DeleteFileWhenReleased(info.FullPath);
            SaveWidgetsToDisk();
        }

        /// <summary>MediaElement may release the file a moment after closing, so retry once.</summary>
        private static void DeleteFileWhenReleased(string path)
        {
            if (TryDelete(path)) return;

            var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            retry.Tick += (_, _) =>
            {
                retry.Stop();
                TryDelete(path);
            };
            retry.Start();

            static bool TryDelete(string file)
            {
                try
                {
                    File.Delete(file);
                    return true;
                }
                catch
                {
                    return false; // file in use
                }
            }
        }

        private WidgetInfo? SelectedWidget => ListWidgets.SelectedItem as WidgetInfo;

        private void BtnEdit_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedWidget is not { } widget) return;

            if (widget.Locked) widget.Locked = false;
            widget.IsEditing = !widget.IsEditing;
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e) => ConfirmDelete(SelectedWidget);

        private void ConfirmDelete(WidgetInfo? widget)
        {
            if (widget == null) return;

            bool confirmed = AppDialog.Confirm(
                $"¿Eliminar «{widget.DisplayName}»?",
                "Se quitará del escritorio y se borrará la copia importada. El archivo original no se toca.",
                "Eliminar", "Cancelar", danger: true, owner: this);

            if (confirmed)
                RemoveWidget(widget);
        }

        private void BtnOpenLogs_Click(object sender, RoutedEventArgs e) => AppLog.OpenFolder();

        private void BtnCloseDetails_Click(object sender, RoutedEventArgs e) => ListWidgets.SelectedItem = null;

        #endregion

        #region View: filters, selection, preview

        private bool MatchesFilter(object item) => item is WidgetInfo w && _filter switch
        {
            "AlwaysVisible" => w.Layer == WidgetLayer.AlwaysVisible,
            "DesktopIntegrated" => w.Layer == WidgetLayer.DesktopIntegrated,
            "Hidden" => w.Hidden,
            _ => true
        };

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (_view == null || sender is not RadioButton { Tag: string filter } nav) return;

            _filter = filter;
            ViewTitle.Text = nav == NavAll ? "Todos los widgets"
                : nav == NavAlwaysVisible ? "Siempre visible"
                : nav == NavDesktop ? "Integrado en el escritorio"
                : "Ocultos";

            _view.Refresh();
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            if (_view == null) return;

            int total = Widgets.Count;
            int shown = Widgets.Count(w => !w.Hidden);

            CountAll.Text = total.ToString();
            CountAlwaysVisible.Text = Widgets.Count(w => w.Layer == WidgetLayer.AlwaysVisible).ToString();
            CountDesktop.Text = Widgets.Count(w => w.Layer == WidgetLayer.DesktopIntegrated).ToString();
            CountHidden.Text = (total - shown).ToString();

            ViewSubtitle.Text = total == 0
                ? "Importa contenido para crear tu primer widget"
                : $"{total} {(total == 1 ? "widget" : "widgets")} · {shown} {(shown == 1 ? "visible" : "visibles")} en el escritorio";

            EmptyState.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyFilterState.Visibility = total > 0 && _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ListWidgets_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var selected = SelectedWidget;
            DetailsPanel.Visibility = selected != null ? Visibility.Visible : Visibility.Collapsed;

            // Only one preview video at a time; release the previous file.
            PreviewVideo.Close();
            PreviewVideo.Source = null;
            PreviewVideo.Visibility = Visibility.Collapsed;

            if (selected is { Type: WidgetType.Video } && File.Exists(selected.FullPath))
            {
                PreviewVideo.Visibility = Visibility.Visible;
                PreviewVideo.Source = new Uri(selected.FullPath, UriKind.Absolute);
                PreviewVideo.Play();
            }
        }

        private void PreviewVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            PreviewVideo.Position = TimeSpan.Zero; // loop
            PreviewVideo.Play();
        }

        /// <summary>Enter commits a numeric field; Escape reverts it.</summary>
        private void NumberBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box) return;

            var binding = box.GetBindingExpression(TextBox.TextProperty);
            if (e.Key == Key.Enter)
            {
                binding?.UpdateSource();
                box.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                binding?.UpdateTarget();
                e.Handled = true;
            }
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                BtnImport_Click(this, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && ListWidgets.IsKeyboardFocusWithin)
            {
                ConfirmDelete(SelectedWidget);
                e.Handled = true;
            }
        }

        #endregion

        #region Drag and drop import

        private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            if (HasFiles(e)) DropOverlay.Visibility = Visibility.Visible;
            Window_DragOver(sender, e);
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_DragLeave(object sender, DragEventArgs e)
        {
            // DragLeave also fires when moving between child elements; only hide when leaving the window.
            var p = e.GetPosition(this);
            if (p.X > 0 && p.Y > 0 && p.X < ActualWidth && p.Y < ActualHeight) return;
            DropOverlay.Visibility = Visibility.Collapsed;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                ImportFiles(files);
        }

        #endregion

        #region System tray

        private void InitializeTrayIcon()
        {
            string iconPath = Path.Combine(AppPaths.ProgramDirectory, "Assets", "favicon.ico");

            System.Drawing.Icon appIcon = File.Exists(iconPath)
                ? new System.Drawing.Icon(iconPath)
                : System.Drawing.SystemIcons.Application;

            trayIcon = new NotifyIcon
            {
                Icon = appIcon,
                Visible = true,
                Text = "Widget Manager"
            };

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Abrir Widget Manager", null, (s, e) => ShowMainWindow());
            contextMenu.Items.Add("Mostrar todos los widgets", null, (s, e) => SetAllHidden(false));
            contextMenu.Items.Add("Ocultar todos los widgets", null, (s, e) => SetAllHidden(true));
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("Abrir carpeta de registros", null, (s, e) => AppLog.OpenFolder());
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("Salir", null, (s, e) => ExitApplication());
            trayIcon.ContextMenuStrip = contextMenu;

            trayIcon.DoubleClick += (s, e) => ShowMainWindow();
        }

        private void SetAllHidden(bool hidden)
        {
            foreach (var widget in Widgets)
                widget.Hidden = hidden;
        }

        private void ShowMainWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void ExitApplication()
        {
            SaveWidgetsToDisk(); // before closing anything, so positions aren't lost
            reallyClose = true;
            trayIcon.Visible = false;
            System.Windows.Application.Current.Shutdown();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            if (!reallyClose)
            {
                e.Cancel = true;
                SaveWidgetsToDisk();
                ListWidgets.SelectedItem = null; // stops the preview video while hidden
                ShowDesktopShield.Lower(new WindowInteropHelper(this).Handle);
                Hide();
            }
            else
            {
                ShowDesktopShield.Lower(new WindowInteropHelper(this).Handle);
                ShellEvents.Stop();
                trayIcon.Dispose();
            }
        }

        #endregion
    }
}
