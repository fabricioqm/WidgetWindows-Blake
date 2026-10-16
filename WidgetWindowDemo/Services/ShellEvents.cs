using System.Runtime.InteropServices;
using System.Windows.Threading;
using static WidgetWindowDemo.NativeMethods;

namespace WidgetWindowDemo.Services
{
    /// <summary>
    /// Shell notifications shared by every window of the app, so no window needs its own timer:
    /// <list type="bullet">
    /// <item><see cref="ForegroundChanged"/>: another application (or the taskbar) became the foreground window.
    /// That is exactly when Windows moves it to the top of its z-order band, so "always visible" widgets re-assert
    /// their position then.</item>
    /// <item><see cref="DesktopChanged"/>: Explorer restarted ("TaskbarCreated"), or the wallpaper or display layout
    /// changed. Desktop widgets put themselves back in place.</item>
    /// <item><see cref="ShowDesktopShortcut"/>: the user pressed Win + D, Win + M or Win + Home. The app ignores
    /// these shortcuts: its windows undo the minimize/cover that Windows applies right after.</item>
    /// </list>
    /// Must be started from the UI thread: hook callbacks are delivered through that thread's message loop.
    /// </summary>
    internal static class ShellEvents
    {
        public static event EventHandler? ForegroundChanged;
        public static event EventHandler? DesktopChanged;
        public static event EventHandler? ShowDesktopShortcut;

        /// <summary>
        /// Raised synchronously from the keyboard hook, BEFORE Explorer handles Win + D / M / Home, so windows can
        /// shield themselves (<see cref="ShowDesktopShield"/>) instead of being hidden and restored (a visible flash).
        /// The argument is true when the desktop is currently shown, i.e. the shortcut will restore the windows.
        /// Handlers must be fast.
        /// </summary>
        public static event EventHandler<bool>? ShowDesktopShortcutStarting;

        /// <summary>Broadcast to top-level windows when the taskbar (Explorer) is (re)created.</summary>
        public static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        /// <summary>How long after the shortcut a minimize/cover is attributed to it.</summary>
        private static readonly TimeSpan ShortcutWindow = TimeSpan.FromSeconds(1.5);

        private static IntPtr _foregroundHook;
        private static WinEventProc? _foregroundCallback;   // kept alive while the hook exists
        private static IntPtr _keyboardHook;
        private static LowLevelKeyboardProc? _keyboardCallback;
        private static Dispatcher? _dispatcher;
        private static DateTime _lastShortcutUtc = DateTime.MinValue;

        /// <summary>
        /// Our own record of the Win + D toggle: true after a Win + D that showed the desktop. Asking the window
        /// list alone is unreliable right after a restore (windows are still coming back).
        /// </summary>
        private static bool _desktopShownByShortcut;

        /// <summary>One-shot checks after a shortcut: lower the shields once Explorer has lowered the desktop.</summary>
        private static DispatcherTimer? _shieldCheckTimer;
        private static int _shieldChecks;

        /// <summary>True for a short while after Win + D / M / Home was pressed.</summary>
        public static bool ShowDesktopShortcutRecently => DateTime.UtcNow - _lastShortcutUtc < ShortcutWindow;

        public static void Start()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _shieldCheckTimer ??= new DispatcherTimer(DispatcherPriority.Normal, _dispatcher);
            _shieldCheckTimer.Tick -= ShieldCheck_Tick;
            _shieldCheckTimer.Tick += ShieldCheck_Tick;

            if (_foregroundHook == IntPtr.Zero)
            {
                _foregroundCallback = OnWinEvent;
                _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                    _foregroundCallback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            }

            if (_keyboardHook == IntPtr.Zero)
            {
                // Observe-only: every key is passed on (CallNextHookEx); nothing is blocked for other programs.
                _keyboardCallback = OnKeyboard;
                _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardCallback, GetModuleHandle(null), 0);
                if (_keyboardHook == IntPtr.Zero)
                    AppLog.Warning($"No se pudo instalar el detector de Win + D (error {Marshal.GetLastWin32Error()}).");
            }
        }

        public static void Stop()
        {
            _shieldCheckTimer?.Stop();
            if (_foregroundHook != IntPtr.Zero)
            {
                UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
                _foregroundCallback = null;
            }

            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
                _keyboardCallback = null;
            }
        }

        public static void NotifyDesktopChanged() => DesktopChanged?.Invoke(null, EventArgs.Empty);

        private static void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint idEventThread, uint time)
        {
            // The user activating a window lowers a shown desktop: the shields can come down. Not right after a
            // shortcut, though: Explorer activates the desktop before it has finished raising it.
            CheckDesktopLowered();

            ForegroundChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>When the desktop is no longer shown (by Win + D or by activating a window), lower the shields.</summary>
        private static void CheckDesktopLowered()
        {
            // Right after a shortcut Explorer may not have raised the desktop yet: too early to tell.
            if (DateTime.UtcNow - _lastShortcutUtc < TimeSpan.FromMilliseconds(500)) return;
            if (DesktopHost.IsDesktopRaised()) return;
            _desktopShownByShortcut = false;
            ShowDesktopShield.ReleaseIfDesktopLowered();
        }

        private static void ShieldCheck_Tick(object? sender, EventArgs e)
        {
            CheckDesktopLowered();

            // Two checks: 0.6 s (Win + M, or a restore that finished quickly) and 1.5 s (slower restores).
            if (++_shieldChecks >= 2) _shieldCheckTimer!.Stop();
            else _shieldCheckTimer!.Interval = TimeSpan.FromMilliseconds(900);
        }

        private static IntPtr OnKeyboard(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
            {
                uint vk = (uint)Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode
                bool winDown = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

                if (winDown && vk is VK_D or VK_M or VK_HOME)
                {
                    _lastShortcutUtc = DateTime.UtcNow;
                    _shieldCheckTimer?.Stop(); // a check left over from a previous shortcut must not lower new shields
                    try
                    {
                        // Win + D toggles: when the desktop is already shown it restores the windows instead.
                        bool desktopShown = vk == VK_D && _desktopShownByShortcut && DesktopHost.IsDesktopRaised();
                        if (vk == VK_D) _desktopShownByShortcut = !desktopShown;
                        ShowDesktopShortcutStarting?.Invoke(null, desktopShown);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warning("Error al proteger las ventanas antes de Win + D.", ex);
                    }

                    // Low-level hooks must return fast: react after this keystroke has been passed on.
                    _dispatcher?.BeginInvoke(() =>
                    {
                        AppLog.Info("Atajo de mostrar escritorio detectado: las ventanas de la app lo ignoran.");
                        _shieldChecks = 0;
                        _shieldCheckTimer!.Stop();
                        _shieldCheckTimer.Interval = TimeSpan.FromMilliseconds(600);
                        _shieldCheckTimer.Start();
                        ShowDesktopShortcut?.Invoke(null, EventArgs.Empty);
                    });
                }
            }

            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }
    }
}
