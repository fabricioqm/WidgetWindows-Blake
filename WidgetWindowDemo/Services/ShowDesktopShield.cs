using static WidgetWindowDemo.NativeMethods;

namespace WidgetWindowDemo.Services
{
    /// <summary>
    /// Keeps the app's windows untouched by "Show desktop" (Win + D) and "Minimize all" (Win + M / Win + Home).
    ///
    /// Explorer handles those shortcuts by minimizing every window that has a minimize box and raising the desktop
    /// above the normal windows. Undoing that afterwards makes the window flash, so the shield acts *before*
    /// Explorer (the keyboard hook sees the key first, see <see cref="ShellEvents"/>): the window temporarily loses
    /// its minimize box (so it is not minimized) and becomes topmost (so the raised desktop can't cover it).
    ///
    /// The shield stays up while the desktop is shown. A normal window left visible above the shown desktop would
    /// make Explorer think the desktop isn't fully shown, and the next Win + D would show it again instead of
    /// restoring the user's windows; topmost windows don't count. Once the desktop is lowered again (second
    /// Win + D, or the user activates a window) the shield comes down and each window returns to its place.
    /// </summary>
    internal static class ShowDesktopShield
    {
        private sealed record Entry(int Style, IntPtr RestoreAfter, Action? OnReleased);

        private static readonly Dictionary<IntPtr, Entry> Shielded = new();

        public static bool IsShielded(IntPtr window) => Shielded.ContainsKey(window);

        /// <summary>
        /// Called from the keyboard hook: must be fast and must not throw. <paramref name="onReleased"/> runs when
        /// the shield comes down (e.g. to put a desktop widget back right above the desktop).
        /// </summary>
        public static void Raise(IntPtr window, Action? onReleased = null)
        {
            if (window == IntPtr.Zero || Shielded.ContainsKey(window)) return;

            int style = GetWindowLong(window, GWL_STYLE);
            var entry = new Entry(style, VisibleNormalWindowAbove(window), onReleased);

            if ((style & WS_MINIMIZEBOX) != 0)
                SetWindowLong(window, GWL_STYLE, style & ~WS_MINIMIZEBOX);

            SetWindowPos(window, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

            // Registered only now: windows filter z-order changes while shielded, and must not filter this one.
            Shielded[window] = entry;
        }

        /// <summary>
        /// Lowers every shield once the desktop is no longer shown. Called on foreground changes and shortly after
        /// each shortcut (event-driven: nothing runs while the desktop stays shown and nobody touches anything).
        /// </summary>
        public static void ReleaseIfDesktopLowered()
        {
            if (Shielded.Count == 0 || DesktopHost.IsDesktopRaised()) return;

            foreach (var window in Shielded.Keys.ToList())
                Lower(window);
        }

        /// <summary>Lowers one window's shield now (the window is closing or hiding).</summary>
        public static void Lower(IntPtr window)
        {
            if (!Shielded.Remove(window, out var entry)) return;

            if ((entry.Style & WS_MINIMIZEBOX) != 0)
                SetWindowLong(window, GWL_STYLE, GetWindowLong(window, GWL_STYLE) | WS_MINIMIZEBOX);

            // Back to the normal band, then to where it was before the shortcut (below the same window, if that
            // window is still there), so other windows don't end up under it.
            SetWindowPos(window, HWND_NOTOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            if (entry.RestoreAfter != IntPtr.Zero && IsWindowVisible(entry.RestoreAfter) &&
                (GetWindowLong(entry.RestoreAfter, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0)
            {
                SetWindowPos(window, entry.RestoreAfter, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            }

            try { entry.OnReleased?.Invoke(); }
            catch (Exception ex) { AppLog.Warning("Error al recolocar una ventana tras Win + D.", ex); }
        }

        /// <summary>The nearest visible, non-topmost window above <paramref name="window"/>, or Zero.</summary>
        private static IntPtr VisibleNormalWindowAbove(IntPtr window)
        {
            for (IntPtr w = GetWindow(window, GW_HWNDPREV); w != IntPtr.Zero; w = GetWindow(w, GW_HWNDPREV))
            {
                if (!IsWindowVisible(w)) continue;
                return (GetWindowLong(w, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0 ? w : IntPtr.Zero;
            }
            return IntPtr.Zero;
        }
    }
}
