using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Palisades.Views.Controls
{
    /// <summary>
    /// The overlay is a NOACTIVATE window: clicks on it never deactivate the
    /// menu owner, so an open ContextMenu would stay stuck. Remedy: a temporary
    /// low-level mouse hook that closes the menu on any click landing on the
    /// owner window (clicks always pass through, never swallowed).
    /// Same pattern as PluginGadgetWrapper / the native shell menu.
    /// </summary>
    public static class OverlayMenuDismiss
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct HookPoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HookStruct
        {
            public HookPoint pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(HookPoint pt);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private sealed class dismissState
        {
            public ContextMenu? Menu;
            public IntPtr OwnerHwnd;
            public IntPtr HookHandle;
            public HookProc? Proc;
        }

        /// <summary>Install the dismiss hook for an open/opening menu. Uninstalls itself on Closed.</summary>
        public static void Attach(ContextMenu menu, UIElement owner)
        {
            if (menu == null || owner == null) return;
            var state = new dismissState { Menu = menu };
            try
            {
                var win = Window.GetWindow(owner);
                if (win != null)
                    state.OwnerHwnd = new WindowInteropHelper(win).Handle;
            }
            catch { state.OwnerHwnd = IntPtr.Zero; }

            menu.Closed += (_, _) => Uninstall(state);
            try
            {
                state.Proc = (nCode, wParam, lParam) => Callback(nCode, wParam, lParam, state);
                state.HookHandle = SetWindowsHookEx(WH_MOUSE_LL, state.Proc, GetModuleHandle(null), 0);
            }
            catch { }
        }

        private static void Uninstall(dismissState state)
        {
            if (state.HookHandle != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(state.HookHandle); } catch { }
                state.HookHandle = IntPtr.Zero;
            }
            state.Proc = null;
            state.Menu = null;
        }

        private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam, dismissState state)
        {
            if (nCode >= 0 && state.Menu != null &&
                (wParam == (IntPtr)WM_LBUTTONDOWN || wParam == (IntPtr)WM_RBUTTONDOWN))
            {
                try
                {
                    // Only clicks landing directly on the owner window dismiss the menu.
                    // Anything else (menu itself, taskbar, apps) is left to Windows,
                    // so a menu-item click can never be eaten by this hook.
                    var hookStruct = Marshal.PtrToStructure<HookStruct>(lParam);
                    IntPtr hWnd = WindowFromPoint(hookStruct.pt);
                    if (hWnd != IntPtr.Zero && hWnd == state.OwnerHwnd)
                    {
                        var menu = state.Menu;
                        menu.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try { menu.IsOpen = false; } catch { }
                            Uninstall(state);
                        }));
                    }
                }
                catch { }
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
    }
}
