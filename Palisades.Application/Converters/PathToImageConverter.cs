using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Palisades.Converters
{
    public class PathToImageConverter : IValueConverter
    {
        private static readonly ConcurrentDictionary<string, BitmapSource> _cache = new(StringComparer.OrdinalIgnoreCase);

        private static BitmapSource? _shortcutArrowBitmap;

        public bool ShowArrow { get; set; } = true;

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string path || string.IsNullOrEmpty(path))
                return null;

            if (path.Equals("shell:::{645FF040-5081-101B-9F08-00AA002F954E}", StringComparison.OrdinalIgnoreCase))
            {
                string cacheKeyRb = $"recycle_bin:arrow={ShowArrow}";
                if (_cache.TryGetValue(cacheKeyRb, out var cachedRb))
                    return cachedRb;

                try
                {
                    IntPtr hIconLarge = IntPtr.Zero;
                    IntPtr hIconSmall = IntPtr.Zero;
                    int hrRb = SHDefExtractIcon("shell32.dll", 31, 0, out hIconLarge, out hIconSmall, 256);
                    if (hrRb == 0 && hIconLarge != IntPtr.Zero)
                    {
                        var baseSource = RenderIconToBitmapSource(hIconLarge, 48);
                        DestroyIcon(hIconLarge);
                        if (hIconSmall != IntPtr.Zero) DestroyIcon(hIconSmall);

                        if (baseSource != null)
                        {
                            baseSource.Freeze();
                            _cache[cacheKeyRb] = baseSource;
                            return baseSource;
                        }
                    }
                }
                catch { }
            }

            string shortcutFlag = parameter as string;
            bool isShortcut = IsShortcut(path) || IsShortcut(shortcutFlag);

            string cacheKey = $"{path}:arrow={ShowArrow}:shortcut={isShortcut}";

            if (_cache.TryGetValue(cacheKey, out var cached))
                return cached;

            try
            {
                uint flags = SHGFI_SYSICONINDEX;

                if (!File.Exists(path) && !Directory.Exists(path))
                    flags |= SHGFI_USEFILEATTRIBUTES;

                uint fileAttributes = Directory.Exists(path) ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

                var shinfo = new SHFILEINFO();
                IntPtr hImg = SHGetFileInfo(path, fileAttributes, ref shinfo, Marshal.SizeOf<SHFILEINFO>(), flags);
                if (hImg == IntPtr.Zero)
                    return null;

                int baseIconIndex = shinfo.iIcon & 0xFFFFFF;

                Guid iid = s_imageListGuid;
                IImageList? iml = null;
                int hr = SHGetImageList(SHIL_EXTRALARGE, ref iid, ref iml);
                if (hr != 0 || iml == null)
                {
                    hr = SHGetImageList(SHIL_JUMBO, ref iid, ref iml);
                    if (hr != 0 || iml == null)
                    {
                        hr = SHGetImageList(SHIL_LARGE, ref iid, ref iml);
                        if (hr != 0 || iml == null)
                            return null;
                    }
                }

                IntPtr hIcon = IntPtr.Zero;
                iml.GetIcon(baseIconIndex, ILD_TRANSPARENT, ref hIcon);

                if (hIcon == IntPtr.Zero)
                    return null;

                var baseSource = RenderIconToBitmapSource(hIcon, 48);
                DestroyIcon(hIcon);

                if (baseSource == null) return null;

                BitmapSource finalSource = baseSource;

                if (ShowArrow && isShortcut)
                {
                    var arrowSource = GetCachedShortcutArrow();
                    if (arrowSource != null)
                        finalSource = CompositeIcons(baseSource, arrowSource);
                }

                finalSource.Freeze();
                _cache[cacheKey] = finalSource;
                return finalSource;
            }
            catch { }

            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();

        public static void ClearCache() => _cache.Clear();

        private static bool IsShortcut(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            var ext = Path.GetExtension(path);
            return ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".url", StringComparison.OrdinalIgnoreCase);
        }

        private static BitmapSource? GetCachedShortcutArrow()
        {
            if (_shortcutArrowBitmap != null) return _shortcutArrowBitmap;

            var sii = new SHSTOCKICONINFO();
            sii.cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>();

            int hr = SHGetStockIconInfo(29, 0x00000100, ref sii);
            if (hr == 0 && sii.hIcon != IntPtr.Zero)
            {
                var arrow = RenderIconToBitmapSource(sii.hIcon, 48);
                DestroyIcon(sii.hIcon);
                if (arrow != null)
                {
                    arrow.Freeze();
                    _shortcutArrowBitmap = arrow;
                }
            }
            return _shortcutArrowBitmap;
        }

        private static BitmapSource CompositeIcons(BitmapSource baseIcon, BitmapSource arrowIcon)
        {
            var visual = new DrawingVisual();
            using (var ctx = visual.RenderOpen())
            {
                ctx.DrawImage(baseIcon, new Rect(0, 0, baseIcon.Width, baseIcon.Height));

                double arrowSize = baseIcon.Width * 0.5;
                ctx.DrawImage(arrowIcon, new Rect(0, baseIcon.Height - arrowSize, arrowSize, arrowSize));
            }

            var rtb = new RenderTargetBitmap(
                (int)baseIcon.PixelWidth, (int)baseIcon.PixelHeight,
                baseIcon.DpiX, baseIcon.DpiY,
                PixelFormats.Pbgra32);

            rtb.Render(visual);
            return rtb;
        }

        private const uint SHGFI_SYSICONINDEX = 0x4000;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x00000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const int ILD_TRANSPARENT = 1;
        private const int SHIL_LARGE = 0x0;
        private const int SHIL_EXTRALARGE = 0x2;
        private const int SHIL_JUMBO = 0x4;

        private static readonly Guid s_imageListGuid = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHSTOCKICONINFO
        {
            public uint cbSize;
            public IntPtr hIcon;
            public int iSysImageIndex;
            public int iIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, int cbFileInfo, uint uFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetStockIconInfo(uint siid, uint uFlags, ref SHSTOCKICONINFO psii);

        [DllImport("shell32.dll", EntryPoint = "#727")]
        private static extern int SHGetImageList(int iImageList, ref Guid riid, ref IImageList ppv);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern int SHDefExtractIcon(string pszIconFile, int iIndex, uint uFlags, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIconSize);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("user32.dll")]
        private static extern int DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyWidth, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr h);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        private const int DI_NORMAL = 0x0003;

        private static BitmapSource? RenderIconToBitmapSource(IntPtr hIcon, int size)
        {
            IntPtr hdcScreen = GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero) return null;
            IntPtr hBitmap = CreateCompatibleBitmap(hdcScreen, size, size);
            if (hBitmap == IntPtr.Zero) { ReleaseDC(IntPtr.Zero, hdcScreen); return null; }
            IntPtr hdc = CreateCompatibleDC(hdcScreen);
            if (hdc == IntPtr.Zero) { DeleteObject(hBitmap); ReleaseDC(IntPtr.Zero, hdcScreen); return null; }
            SelectObject(hdc, hBitmap);
            int dibResult = DrawIconEx(hdc, 0, 0, hIcon, size, size, 0, IntPtr.Zero, DI_NORMAL);
            if (dibResult == 0) { DeleteDC(hdc); DeleteObject(hBitmap); ReleaseDC(IntPtr.Zero, hdcScreen); return null; }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            DeleteDC(hdc);
            DeleteObject(hBitmap);
            ReleaseDC(IntPtr.Zero, hdcScreen);
            return source;
        }

        [ComImport]
        [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IImageList
        {
            [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
            [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
            [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
            [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
            [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
            [PreserveSig] int Draw(ref IMAGELISTDRAWPARAMS pimldp);
            [PreserveSig] int Remove(int i);
            [PreserveSig] int GetIcon(int i, int flags, ref IntPtr picon);
            [PreserveSig] int GetImageInfo(int i, ref IMAGEINFO pImageInfo);
            [PreserveSig] int Copy(int iDst, IImageList punkSrc, int iSrc, int uFlags);
            [PreserveSig] int Merge(int i1, IImageList punk2, int i2, int dx, int dy, ref Guid riid, ref IntPtr ppv);
            [PreserveSig] int Clone(ref Guid riid, ref IntPtr ppv);
            [PreserveSig] int GetImageRect(int i, ref RECT prc);
            [PreserveSig] int GetIconSize(ref int cx, ref int cy);
            [PreserveSig] int SetIconSize(int cx, int cy);
            [PreserveSig] int GetImageCount(ref int pi);
            [PreserveSig] int SetImageCount(int uNewCount);
            [PreserveSig] int SetBkColor(int clrBk, ref int pclr);
            [PreserveSig] int GetBkColor(ref int pclr);
            [PreserveSig] int BeginDrag(int iTrack, int dxHotspot, int dyHotspot);
            [PreserveSig] int EndDrag();
            [PreserveSig] int DragEnter(IntPtr hwndLock, int x, int y);
            [PreserveSig] int DragLeave(IntPtr hwndLock);
            [PreserveSig] int DragMove(int x, int y);
            [PreserveSig] int SetDragCursorImage(ref IImageList punk, int iDrag, int dxHotspot, int dyHotspot);
            [PreserveSig] int DragShowNolock(int fShow);
            [PreserveSig] int GetDragImage(ref POINT ppt, ref POINT pptHotspot, ref Guid riid, ref IntPtr ppv);
            [PreserveSig] int GetItemFlags(int i, ref int dwFlags);
            [PreserveSig] int GetOverlayImage(int iOverlay, ref int piIndex);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { private int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct IMAGELISTDRAWPARAMS
        {
            public int cbSize; public IntPtr himl; public int i; public IntPtr hdcDst;
            public int x, y, cx, cy; public int xBitmap, yBitmap; public int rgbBk;
            public int rgbFg; public int fStyle; public int dwRop; public int fState;
            public int Frame; public int crEffect;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IMAGEINFO
        {
            public IntPtr hbmImage; public IntPtr hbmMask;
            public int Unused1, Unused2; public RECT rcImage;
        }
    }
}
