using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Palisades.Services
{
    public static class DesktopService
    {
        #region P/Invoke

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_SHOWNA = 8;
        private const int WM_COMMAND = 0x0111;
        private const int WM_CLOSE = 0x0010;

        // Undocumented: toggle desktop icons
        private const int TOGGLE_DESKTOP_ICONS = 0x7402;

        #endregion

        private static IntPtr _desktopListView = IntPtr.Zero;
        private static bool _iconsHidden = false;
        private static readonly object _lock = new();

        /// <summary>
        /// Find the SysListView32 that holds desktop icons.
        /// Checks both Progman (Windows 11) and WorkerW (Windows 10).
        /// </summary>
        private static IntPtr FindDesktopListView()
        {
            // Try Progman directly first (Windows 11)
            IntPtr progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero)
                {
                    IntPtr listView = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
                    if (listView != IntPtr.Zero) return listView;
                }

                // Ask Progman to create WorkerW and try again
                SendMessageTimeout(progman, 0x052C, new IntPtr(0), IntPtr.Zero, 0, 1000, out _);
            }

            // Try WorkerW windows (Windows 10/fallback)
            IntPtr workerW = IntPtr.Zero;
            int maxIterations = 50;
            while (maxIterations-- > 0)
            {
                workerW = FindWindowEx(IntPtr.Zero, workerW, "WorkerW", null);
                if (workerW == IntPtr.Zero) break;

                IntPtr defView = FindWindowEx(workerW, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero)
                {
                    IntPtr listView = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
                    if (listView != IntPtr.Zero) return listView;
                }
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Hide all desktop icons.
        /// </summary>
        public static bool HideDesktopIcons()
        {
            lock (_lock)
            {
                if (_iconsHidden) return true;

                try
                {
                    _desktopListView = FindDesktopListView();
                    if (_desktopListView != IntPtr.Zero)
                    {
                        ShowWindow(_desktopListView, SW_HIDE);
                        _iconsHidden = true;
                        return true;
                    }

                    // Last resort: undocumented toggle message
                    IntPtr progman = FindWindow("Progman", null);
                    if (progman != IntPtr.Zero)
                    {
                        SendMessageTimeout(progman, TOGGLE_DESKTOP_ICONS, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);
                        _iconsHidden = true;
                        return true;
                    }

                    return false;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"HideDesktopIcons failed: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Embed a window into the desktop layer so Win+D doesn't hide it.
        /// Tries three strategies:
        ///   1. WorkerW without SHELLDLL_DefView (wallpaper layer) — ideal for widgets
        ///   2. WorkerW with SHELLDLL_DefView (desktop-icons layer) — fallback
        ///   3. Direct child of Progman — last resort
        /// </summary>
        public static bool EmbedInDesktop(IntPtr windowHwnd)
        {
            if (windowHwnd == IntPtr.Zero) return false;

            try
            {
                IntPtr progman = FindWindow("Progman", null);
                if (progman == IntPtr.Zero) return false;

                // Ask Progman to create a WorkerW (splits the desktop into wallpaper + icons layers)
                SendMessageTimeout(progman, 0x052C, new IntPtr(0), IntPtr.Zero, 0, 1000, out _);

                // Enumerate WorkerW windows
                List<IntPtr> withDefView = new();
                List<IntPtr> withoutDefView = new();

                IntPtr workerW = IntPtr.Zero;
                int maxIterations = 50;
                while (maxIterations-- > 0)
                {
                    workerW = FindWindowEx(IntPtr.Zero, workerW, "WorkerW", null);
                    if (workerW == IntPtr.Zero) break;

                    if (FindWindowEx(workerW, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                        withDefView.Add(workerW);
                    else
                        withoutDefView.Add(workerW);
                }

                // Strategy 1: WorkerW without DefView (wallpaper-only layer)
                // This puts our window BETWEEN the wallpaper and the desktop icons
                if (withoutDefView.Count > 0)
                {
                    SetParent(windowHwnd, withoutDefView[0]);
                    return true;
                }

                // Strategy 2: WorkerW with DefView (desktop-icons layer)
                if (withDefView.Count > 0)
                {
                    SetParent(windowHwnd, withDefView[0]);
                    // Position above the desktop icons
                    IntPtr defView = FindWindowEx(withDefView[0], IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (defView != IntPtr.Zero)
                        SetWindowPos(windowHwnd, defView, 0, 0, 0, 0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    return true;
                }

                // Strategy 3: Direct child of Progman
                SetParent(windowHwnd, progman);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Show all desktop icons.
        /// </summary>
        public static bool ShowDesktopIcons()
        {
            lock (_lock)
            {
                if (!_iconsHidden) return true;

                try
                {
                    if (_desktopListView != IntPtr.Zero && IsWindow(_desktopListView))
                    {
                        ShowWindow(_desktopListView, SW_SHOW);
                    }
                    else
                    {
                        // Find and show
                        var listView = FindDesktopListView();
                        if (listView != IntPtr.Zero)
                            ShowWindow(listView, SW_SHOW);
                    }

                    _iconsHidden = false;
                    _desktopListView = IntPtr.Zero;
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        /// <summary>
        /// Toggle desktop icon visibility.
        /// </summary>
        public static bool ToggleDesktopIcons()
        {
            if (_iconsHidden)
                return ShowDesktopIcons();
            else
                return HideDesktopIcons();
        }

        public static bool AreIconsHidden => _iconsHidden;

        /// <summary>
        /// Scan the desktop for .lnk and .url files.
        /// </summary>
        public static List<string> ScanDesktopShortcuts()
        {
            var results = new List<string>();
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            try
            {
                if (Directory.Exists(desktopPath))
                {
                    foreach (var file in Directory.GetFiles(desktopPath, "*.lnk"))
                    {
                        // Skip common system shortcuts
                        string name = Path.GetFileNameWithoutExtension(file);
                        if (!name.Equals("desktop", StringComparison.OrdinalIgnoreCase))
                            results.Add(file);
                    }
                    foreach (var file in Directory.GetFiles(desktopPath, "*.url"))
                    {
                        results.Add(file);
                    }
                }
            }
            catch { }

            return results;
        }

        /// <summary>
        /// Get the desktop wallpaper path.
        /// </summary>
        public static string? GetDesktopWallpaper()
        {
            try
            {
                var buf = new StringBuilder(260);
                var ret = SystemParametersInfo(SPI_GETDESKWALLPAPER, buf.Capacity, buf, 0);
                if (ret && buf.Length > 0)
                    return buf.ToString();
            }
            catch { }
            return null;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool SystemParametersInfo(int uAction, int uParam, StringBuilder lpvParam, int fuWinIni);
        private const int SPI_GETDESKWALLPAPER = 0x0073;
    }
}
