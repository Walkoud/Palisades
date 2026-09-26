using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Palisades.Services
{
    /// <summary>Détecte si la fenêtre au premier plan occupe tout un écran
    /// (jeu/app en plein écran). Utilisé par le mode Performance pour mettre
    /// Palisades en pause automatiquement.</summary>
    public static class FullscreenDetector
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>True si la fenêtre active couvre tout son écran et n'appartient
        /// pas à Palisades.</summary>
        public static bool IsForegroundFullscreen()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                if (IsIconic(fg)) return false;

                var cls = new StringBuilder(256);
                GetClassName(fg, cls, cls.Capacity);
                string c = cls.ToString();
                if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "XamlExplorerHostIslandWindow")
                    return false;

                // Ignore les fenêtres de Palisades (dashboard, overlay...) : sinon
                // mettre le dashboard plein écran déclencherait la pause en boucle.
                try
                {
                    GetWindowThreadProcessId(fg, out uint pid);
                    if (pid == (uint)Process.GetCurrentProcess().Id) return false;
                }
                catch { }

                if (!GetWindowRect(fg, out RECT r)) return false;
                var screen = System.Windows.Forms.Screen.FromHandle(fg);
                var b = screen.Bounds;
                return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
            }
            catch { return false; }
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }
}
