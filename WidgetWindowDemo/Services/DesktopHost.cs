using static WidgetWindowDemo.NativeMethods;

namespace WidgetWindowDemo.Services
{
    /// <summary>
    /// Finds the shell's desktop window: the top-level window that hosts the desktop icons (SHELLDLL_DefView).
    /// That is Progman on Windows 11 24H2+, and Progman or a WorkerW on earlier versions.
    ///
    /// "Desktop integrated" widgets stay directly above this window in the z-order. Normally the desktop is at the
    /// bottom, so the widget sits behind every application window. "Show desktop" (Win + D) raises the desktop
    /// above the normal windows, and the widget, kept right above it, is shown with it.
    ///
    /// Why not make the widget a child of Progman (the classic wallpaper technique)? On Windows 11 24H2 Progman
    /// has no redirection surface (WS_EX_NOREDIRECTIONBITMAP) and only composes children drawn with
    /// DirectComposition / DXGI. WPF windows draw through redirection, so as children they are never displayed.
    /// </summary>
    internal static class DesktopHost
    {
        public static IntPtr FindDesktopWindow()
        {
            IntPtr progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero && FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                return progman;

            // The icons were moved to a WorkerW (Windows 10, or a wallpaper app split the desktop).
            IntPtr host = IntPtr.Zero;
            EnumWindows((topLevel, _) =>
            {
                if (FindWindowEx(topLevel, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
                    return true;

                host = topLevel;
                return false;
            }, IntPtr.Zero);

            return host != IntPtr.Zero ? host : progman;
        }

        /// <summary>
        /// True while "Show desktop" is in effect: the desktop window has been raised, so visible windows lie below
        /// it (normally it is the bottom window). Win + D then restores instead of showing the desktop.
        /// </summary>
        public static bool IsDesktopRaised()
        {
            IntPtr desktop = FindDesktopWindow();
            if (desktop == IntPtr.Zero) return false;

            for (IntPtr w = GetWindow(desktop, GW_HWNDNEXT); w != IntPtr.Zero; w = GetWindow(w, GW_HWNDNEXT))
                if (IsWindowVisible(w)) return true;
            return false;
        }

        /// <summary>True when <paramref name="upper"/> is above <paramref name="window"/> in the z-order.</summary>
        public static bool IsAbove(IntPtr upper, IntPtr window)
        {
            if (upper == IntPtr.Zero) return false;
            for (IntPtr w = GetWindow(window, GW_HWNDPREV); w != IntPtr.Zero; w = GetWindow(w, GW_HWNDPREV))
                if (w == upper) return true;
            return false;
        }

        /// <summary>
        /// Places <paramref name="window"/> directly above the desktop window, without activating it.
        /// Returns false when the desktop window can't be found (Explorer not running).
        /// </summary>
        public static bool PlaceAboveDesktop(IntPtr window)
        {
            IntPtr desktop = FindDesktopWindow();
            if (desktop == IntPtr.Zero) return false;

            // SetWindowPos inserts *below* hWndInsertAfter, so insert below the window that is above the desktop.
            IntPtr above = GetWindow(desktop, GW_HWNDPREV);
            if (above == window) return true; // already in place

            // Never insert after a topmost window (when the desktop is raised it is the first normal window):
            // HWND_TOP of the normal band is the same place, right above the desktop.
            if (above == IntPtr.Zero || (GetWindowLong(above, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0)
                above = HWND_TOP;

            SetWindowPos(window, above, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            return true;
        }
    }
}
