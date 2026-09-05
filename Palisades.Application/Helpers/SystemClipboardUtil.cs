using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Palisades.Helpers
{
    public static class SystemClipboardUtil
    {
        private const uint CF_HDROP = 15;
        private const uint GMEM_MOVEABLE = 0x0002;
        private const uint GMEM_ZEROINIT = 0x0040;
        private const int DROPEFFECT_COPY = 0x0001;
        private const int DROPEFFECT_MOVE = 0x0002;

        private static readonly uint _preferredDropEffectFormat =
            RegisterClipboardFormat("Preferred DropEffect");

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint RegisterClipboardFormat(string lpszFormat);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        [StructLayout(LayoutKind.Sequential)]
        private struct DROPFILES
        {
            public int pFiles;
            public int x;
            public int y;
            public int fNC;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fWide;
        }

        /// <summary>
        /// Places a static CF_HDROP (FileDrop) clipboard entry with no delayed rendering.
        /// The clipboard owns the memory immediately, so pasting in Explorer never
        /// calls back into this process (no crash risk from OLE delayed rendering).
        /// </summary>
        public static bool SetFileDrop(string[] paths)
            => SetFileDrop(paths, move: false);

        /// <param name="move">
        /// When true, also publishes "Preferred DropEffect" = DROPEFFECT_MOVE so the
        /// paste target moves (cuts) the files instead of copying them.
        /// </param>
        public static bool SetFileDrop(string[] paths, bool move)
        {
            if (paths == null || paths.Length == 0) return false;

            int structSize = Marshal.SizeOf<DROPFILES>();
            var sb = new StringBuilder();
            foreach (var p in paths) sb.Append(p).Append('\0');
            sb.Append('\0');
            int byteLen = Encoding.Unicode.GetByteCount(sb.ToString());

            IntPtr hGlobal = IntPtr.Zero;
            IntPtr locked = IntPtr.Zero;
            try
            {
                hGlobal = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)(structSize + byteLen));
                if (hGlobal == IntPtr.Zero) return false;
                locked = GlobalLock(hGlobal);
                if (locked == IntPtr.Zero) return false;

                var df = new DROPFILES { pFiles = structSize, fWide = true };
                Marshal.StructureToPtr(df, locked, false);
                IntPtr dataStart = IntPtr.Add(locked, structSize);
                byte[] bytes = Encoding.Unicode.GetBytes(sb.ToString());
                Marshal.Copy(bytes, 0, dataStart, bytes.Length);

                GlobalUnlock(hGlobal);
                locked = IntPtr.Zero;

                if (!OpenClipboard(IntPtr.Zero)) { GlobalFree(hGlobal); return false; }
                try
                {
                    EmptyClipboard();
                    bool hdropSet = SetClipboardData(CF_HDROP, hGlobal) != IntPtr.Zero;
                    if (hdropSet) hGlobal = IntPtr.Zero;
                    if (move && hdropSet)
                    {
                        var eff = new byte[] { (byte)DROPEFFECT_MOVE, 0, 0, 0 };
                        IntPtr hEff = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)4);
                        if (hEff != IntPtr.Zero)
                        {
                            IntPtr lockEff = GlobalLock(hEff);
                            if (lockEff != IntPtr.Zero)
                            {
                                Marshal.Copy(eff, 0, lockEff, 4);
                                GlobalUnlock(hEff);
                            }
                            SetClipboardData(_preferredDropEffectFormat, hEff);
                        }
                    }
                    return hdropSet;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch
            {
                if (locked != IntPtr.Zero) GlobalUnlock(hGlobal);
                if (hGlobal != IntPtr.Zero) GlobalFree(hGlobal);
                return false;
            }
        }
        /// <summary>
        /// True when the clipboard's FileDrop came from a Cut (Preferred DropEffect = MOVE),
        /// so a Paste should move the files instead of copying them.
        /// </summary>
        public static bool ClipboardHasMoveEffect()
        {
            try
            {
                if (!OpenClipboard(IntPtr.Zero)) return false;
                try
                {
                    IntPtr h = GetClipboardData((uint)_preferredDropEffectFormat);
                    if (h == IntPtr.Zero) return false;
                    IntPtr p = GlobalLock(h);
                    if (p == IntPtr.Zero) return false;
                    try
                    {
                        byte[] raw = new byte[4];
                        Marshal.Copy(p, raw, 0, 4);
                        int effect = BitConverter.ToInt32(raw, 0);
                        return (effect & DROPEFFECT_MOVE) != 0;
                    }
                    finally
                    {
                        GlobalUnlock(h);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch { return false; }
        }
    }
}
