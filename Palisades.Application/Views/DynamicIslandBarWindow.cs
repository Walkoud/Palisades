using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Palisades.Services;
using Palisades.Views.Controls;

namespace Palisades.Views
{
    /// <summary>
    /// Dynamic Island pinned in front of the Windows taskbar (same recipe as the
    /// Now Playing pin: topmost + NOACTIVATE, tight bounding box, drag by background,
    /// auto-hide on fullscreen). The card always grows upward here (ForceExpandUp)
    /// and the window bottom stays glued so expansion never covers the taskbar.
    /// </summary>
    public class DynamicIslandBarWindow : Window
    {
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_NOACTIVATE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public int flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        private readonly DynamicIslandControl _island;
        private IntPtr _barHwnd = IntPtr.Zero;
        private HwndSource? _hwndSource;
        private System.Windows.Threading.DispatcherTimer? _topmostTimer;
        private bool _placed;
        private double _gluedBottom = double.NaN;
        private bool _glueActive = true;
        /// <summary>True tant que la colle vient d'une estimation (restauration
        /// avant 1er layout) : le 1er layout calibre sans déplacer.</summary>
        private bool _glueEstimated;

        public DynamicIslandBarWindow()
        {
            Topmost = true;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowActivated = false;

            _island = new DynamicIslandControl
            {
                EnableFreeDrag = false,
                ForceExpandUp = true,
                IsBarHost = true,
                Margin = new Thickness(0)
            };
            Content = _island;

            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
            Closed += OnClosed;
            SizeChanged += OnSizeChanged;
            PreviewMouseRightButtonUp += OnRightClick;

            _topmostTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _topmostTimer.Tick += (_, _) =>
            {
                // Menu ouvert : ne PAS réaffirmer Topmost, sinon la barre repasse
                // au-dessus du popup du ContextMenu.
                if (_island.IsMenuOpen || _barMenuOpen) return;
                ReassertTopmost();
                UpdateFullscreenVisibility();
            };
            _island.PreviewMouseLeftButtonDown += Island_BackgroundDrag;
        }

        /// <summary>Bottom glued (expansion grows upward). Auto-placed bar also
        /// re-centers horizontally once the real width is known.</summary>
        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
                try { App.Log($"[IslandBarGeom] L={Left:0} T={Top:0} W={ActualWidth:0} H={ActualHeight:0} glue={_gluedBottom:0} est={_glueEstimated}"); } catch { }
                if (!double.IsNaN(_gluedBottom) && ActualHeight > 0)
                {
                    if (_glueEstimated)
                    {
                        // 1er vrai layout après restauration : calibre la colle
                        // SANS bouger (sinon la barre saute au démarrage)
                        _gluedBottom = Top + ActualHeight;
                        _glueEstimated = false;
                    }
                    else
                        Top = _gluedBottom - ActualHeight;
                }
                // Pastille centrée FIXE : à chaque changement de largeur la fenêtre
                // se recentre sur SON centre (pas l'écran). Sinon le contenu live
                // fait glisser la pastille sous un curseur fixe -> le sondage
                // croit à un départ -> boucle ouvrir/fermer.
                if (e.WidthChanged && e.PreviousSize.Width > 0 && ActualWidth > 0)
                {
                    // Seuil 2px : ignore le jitter des chiffres de l'horloge,
                    // corrige les vrais décalages (pastille fixe, pas de boucle)
                    double dW = e.PreviousSize.Width - ActualWidth;
                    if (Math.Abs(dW) >= 2.0)
                        Left += dW / 2;
                }
            }
            catch { }
        }

        private void Island_BackgroundDrag(object sender, MouseButtonEventArgs e)
        {
            // Drag the bar by the pill background only (never hijack buttons,
            // widget cards, slider, resize grip — sinon clics mangés).
            DependencyObject? d = e.OriginalSource as DependencyObject;
            while (d != null)
            {
                if (d is Button) return;
                if (d is System.Windows.Controls.Primitives.Thumb) return;
                if (d is Slider) return;
                if (d is ScrollViewer) return;
                if (d is Border b && b.Tag is ViewModels.IslandWidget) return;
                // Zone dépliée (vues widgets plugins, sélecteur...) : jamais de drag
                // fenêtre, sinon les contrôles custom (Border/MouseDown) sont avalés
                // et l'îlot se referme au lâcher.
                if (d is FrameworkElement fe && fe.Name == "ExpandedPanel") return;
                d = VisualTreeHelper.GetParent(d);
            }
            try
            {
                try { App.Log($"[IslandBarDrag] grab L={Left:0} T={Top:0} W={ActualWidth:0} H={ActualHeight:0} expanded={Palisades.ViewModels.DynamicIslandViewModel.Instance.IsExpanded}"); } catch { }
                // Fige la taille pendant le DragMove : SizeToContent + contenu live
                // (slider 500 ms, horloge, SMTC) = la fenêtre saute pendant le drag,
                // typiquement vers le haut. Restaure l'auto après le lâcher.
                double w = ActualWidth > 0 ? ActualWidth : 340;
                double h = ActualHeight > 0 ? ActualHeight : 60;
                _glueActive = false; // pendant le drag, ne pas forcer la colle
                SizeToContent = SizeToContent.Manual;
                Width = w; Height = h;
                try { DragMove(); }
                catch { }
                try
                {
                    // Lâché déplié : effondre le layout SYNCHRONE avant de dégeler
                    // (un fondu seul ne retire rien du layout -> le dégel remesure
                    // grand = ballon). État final = FinishClose, sans anim.
                    var vm = Palisades.ViewModels.DynamicIslandViewModel.Instance;
                    if (vm.IsExpanded) vm.State = Palisades.ViewModels.IslandState.Compact;
                    _island.CollapseInstant();
                }
                catch { }
                finally
                {
                    SizeToContent = SizeToContent.WidthAndHeight;
                    ClearValue(WidthProperty);
                    ClearValue(HeightProperty);
                }
                var svc = DynamicIslandService.Instance;
                if (!double.IsNaN(Left) && !double.IsNaN(Top))
                {
                    svc.SetBarPosition(Left, Top);
                    _gluedBottom = Top + ActualHeight;
                    _glueEstimated = false;
                    try { App.Log($"[IslandBarDrag] dropped L={Left:0} T={Top:0}"); } catch { }
                }
                _glueActive = true;
            }
            catch { }
        }

        private bool _barMenuOpen;

        private void OnRightClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                // Clic droit dans la zone dépliée : laisser le contrôle îlot gérer
                // (menu réglages widget), pas le menu "Unpin".
                var d = e.OriginalSource as DependencyObject;
                while (d != null)
                {
                    if (d is FrameworkElement fe && fe.Name == "ExpandedPanel") return;
                    d = VisualTreeHelper.GetParent(d);
                }
                var menu = new ContextMenu();
                var unpin = new MenuItem { Header = "Unpin Dynamic Island" };
                unpin.Click += (_, _) => DynamicIslandService.Instance.PinToTaskbar = false;
                menu.Items.Add(unpin);
                _barMenuOpen = true;
                menu.Closed += (_, _) => _barMenuOpen = false;
                DynamicIslandControl.EnsureMenuTopmost(menu);
                menu.IsOpen = true;
            }
            catch { }
        }

        public void RefreshGeometry()
        {
            try { ApplyPosition(); ReassertTopmost(); } catch { }
        }

        private void ApplyPosition()
        {
            try
            {
                var svc = DynamicIslandService.Instance;
                if (!double.IsNaN(svc.Settings.BarLeft) && !double.IsNaN(svc.Settings.BarTop)
                    && IsOnAnyScreen(svc.Settings.BarLeft, svc.Settings.BarTop))
                {
                    Left = svc.Settings.BarLeft;
                    Top = svc.Settings.BarTop;
                    _placed = true;
                    // Colle le bas DÈS la restauration. Hauteur pas encore mesurée
                    // au 1er passage -> estimation marquée : le 1er vrai layout
                    // calibre SANS bouger (sinon saut au démarrage).
                    _gluedBottom = Top + (ActualHeight > 0 ? ActualHeight : 60);
                    _glueEstimated = ActualHeight <= 0;
                }
                else if (!_placed)
                {
                    PlaceOverTaskbar();
                    _placed = true;
                }
                try
                {
                    var dpi0 = VisualTreeHelper.GetDpi(this);
                    string scr = "";
                    try { foreach (var s in System.Windows.Forms.Screen.AllScreens) scr += $"[{s.Bounds}]"; } catch { }
                    App.Log($"[IslandBarPos] L={Left:0} T={Top:0} placed={_placed} dpi={dpi0.DpiScaleX}x{dpi0.DpiScaleY} cfg=({svc.Settings.BarLeft:0},{svc.Settings.BarTop:0}) screens={scr}");
                }
                catch { }
            }
            catch { }
        }

        private bool IsOnAnyScreen(double x, double y)
        {
            try
            {
                // Config en DIPs, Bounds en pixels : convertir avec le DPI courant
                double dx = 1, dy = 1;
                try
                {
                    var dpi = VisualTreeHelper.GetDpi(this);
                    if (dpi.DpiScaleX > 0) dx = dpi.DpiScaleX;
                    if (dpi.DpiScaleY > 0) dy = dpi.DpiScaleY;
                }
                catch { }
                foreach (var s in System.Windows.Forms.Screen.AllScreens)
                {
                    var b = s.Bounds;
                    if (x >= b.Left / dx - 200 && x <= b.Right / dx + 200
                        && y >= b.Top / dy - 200 && y <= b.Bottom / dy + 200)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private void PlaceOverTaskbar()
        {
            try
            {
                var screen = System.Windows.Forms.Screen.PrimaryScreen;
                if (screen == null) return;
                var dpi = VisualTreeHelper.GetDpi(this);
                double dx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
                double dy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
                var b = screen.Bounds;
                var w = screen.WorkingArea;

                string edge = "bottom";
                if (w.Top > b.Top) edge = "top";
                else if (w.Left > b.Left) edge = "left";
                else if (w.Right < b.Right) edge = "right";

                double scrL = b.Left / dx, scrT = b.Top / dy;
                double scrR = b.Right / dx, scrB = b.Bottom / dy;
                double bw = ActualWidth > 0 ? ActualWidth : 340;
                double bh = ActualHeight > 0 ? ActualHeight : 60;

                switch (edge)
                {
                    case "top": Left = (scrL + scrR - bw) / 2; Top = scrT; break;
                    case "left": Left = scrL; Top = (scrT + scrB - bh) / 2; break;
                    case "right": Left = scrR - bw; Top = (scrT + scrB - bh) / 2; break;
                    default: Left = (scrL + scrR - bw) / 2; Top = scrB - bh; break;
                }
                _gluedBottom = Top + bh;
                _glueEstimated = bh <= 0 || ActualHeight <= 0;
            }
            catch { }
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            try
            {
                _barHwnd = new WindowInteropHelper(this).Handle;
                _hwndSource = HwndSource.FromHwnd(_barHwnd);
                _hwndSource?.AddHook(WndProcBar);

                const int GWL_EXSTYLE = -20;
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_NOACTIVATE = 0x08000000;
                int exStyle = GetWindowLong(_barHwnd, GWL_EXSTYLE);
                SetWindowLong(_barHwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

                ReassertTopmost();
            }
            catch { }
        }

        private IntPtr WndProcBar(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_WINDOWPOSCHANGING)
            {
                try
                {
                    var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                    wp.hwndInsertAfter = HWND_TOPMOST;
                    wp.flags &= ~0x0004; // ~SWP_NOZORDER
                    Marshal.StructureToPtr(wp, lParam, true);
                }
                catch { }
            }
            return IntPtr.Zero;
        }

        private void ReassertTopmost()
        {
            try
            {
                if (_barHwnd == IntPtr.Zero) return;
                SetWindowPos(_barHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { }
        }

        private void UpdateFullscreenVisibility()
        {
            try
            {
                if (!DynamicIslandService.Instance.BarHideFullscreen)
                {
                    if (!IsVisible) Visibility = Visibility.Visible;
                    return;
                }
                if (IsForegroundFullscreen())
                {
                    if (IsVisible) Visibility = Visibility.Hidden;
                }
                else if (!IsVisible)
                {
                    Visibility = Visibility.Visible;
                    ReassertTopmost();
                }
            }
            catch { }
        }

        private bool IsForegroundFullscreen()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == _barHwnd) return false;
                if (IsIconic(fg)) return false;

                var cls = new System.Text.StringBuilder(256);
                GetClassName(fg, cls, cls.Capacity);
                string c = cls.ToString();
                if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "XamlExplorerHostIslandWindow")
                    return false;

                if (!GetWindowRect(fg, out RECT r)) return false;
                var screen = System.Windows.Forms.Screen.FromHandle(fg);
                var b = screen.Bounds;
                return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
            }
            catch { return false; }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _topmostTimer?.Start();
            ReassertTopmost();
            ApplyPosition();
            try
            {
                Palisades.App.Log($"[IslandPin] bar loaded L={Left:0} T={Top:0} W={ActualWidth:0} H={ActualHeight:0} vis={IsVisible}");
            }
            catch { }
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            try { _topmostTimer?.Stop(); } catch { }
            _topmostTimer = null;
            try
            {
                _island.PreviewMouseLeftButtonDown -= Island_BackgroundDrag;
                if (_hwndSource != null) { _hwndSource.RemoveHook(WndProcBar); _hwndSource = null; }
            }
            catch { }
        }
    }
}
