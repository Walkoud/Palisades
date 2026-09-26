using System;
using System.Linq;
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
        private double _collapsedHeight = 44;
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

            // Exclusivité : une seule barre à la fois (sinon double îlot si un
            // overlay est recréé / pin re-déclenché).
            foreach (var w in _liveBars.ToArray())
            {
                if (ReferenceEquals(w, this)) continue;
                try { w.Close(); } catch { }
            }
            _liveBars.Add(this);
            Closed += (_, _) => { try { _liveBars.Remove(this); } catch { } };
        }

        private static readonly System.Collections.Generic.List<DynamicIslandBarWindow> _liveBars = new();

        /// <summary>Ferme toutes les barres d'îlot vivantes (dépinglage / avant création).</summary>
        public static void CloseAll()
        {
            foreach (var w in _liveBars.ToArray())
            {
                try { w.Close(); } catch { }
            }
        }

        public static bool HasLive => _liveBars.Count > 0;

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
                try { App.Log($"[IslandBarGeom] L={Left:0} T={Top:0} W={ActualWidth:0} H={ActualHeight:0} glue={_gluedBottom:0} est={_glueEstimated} drag={!_glueActive}"); } catch { }
                try { if (!Palisades.ViewModels.DynamicIslandViewModel.Instance.IsExpanded && ActualHeight > 2 && ActualHeight < 120) _collapsedHeight = ActualHeight; } catch { }
                // Pendant un drag, on ne recolle PAS (sinon la fenêtre revient en arrière).
                if (_glueActive && !double.IsNaN(_gluedBottom) && ActualHeight > 0)
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
                // croit à un départ -> boucle ouvrir/fermer. (Pas pendant un drag.)
                if (_glueActive && e.WidthChanged && e.PreviousSize.Width > 0 && ActualWidth > 0)
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
                // Minimal : on gèle le survol et la colle, on laisse la fenêtre
                // suivre le curseur telle quelle (pas de resize/collapse -> pas de saut).
                _island.SuppressHover = true;
                _glueActive = false;
                try { DragMove(); }
                catch { }
                finally
                {
                    _glueActive = true;
                    _island.SuppressHover = false;
                }
                var svc = DynamicIslandService.Instance;
                if (!double.IsNaN(Left) && !double.IsNaN(Top))
                {
                    // BarTop doit référencer la fenêtre REPLIÉE (sinon au reboot la
                    // pilule apparaît décalée de la hauteur du panneau déplié).
                    double bottom = Top + (ActualHeight > 1 ? ActualHeight : _collapsedHeight);
                    double compactTop = bottom - (_collapsedHeight > 0 ? _collapsedHeight : 44);
                    svc.SetBarPosition(Left, compactTop);
                    _gluedBottom = bottom;
                    _glueEstimated = false;
                    try { App.Log($"[IslandBarDrag] dropped L={Left:0} T={Top:0} bottom={bottom:0}"); } catch { }
                }
            }
            catch { _glueActive = true; _island.SuppressHover = false; }
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
                    // Tolérance faible : une position très hors écran (cfg corrompu)
                    // doit retomber sur PlaceOverTaskbar au lieu de perdre l'îlot.
                    if (x >= b.Left / dx - 2 && x <= b.Right / dx + 2
                        && y >= b.Top / dy - 2 && y <= b.Bottom / dy + 2)
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
