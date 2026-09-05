using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Newtonsoft.Json;
using Palisades.Models;
using Palisades.Plugins;
using Palisades.Services;

namespace Palisades.Views
{
    /// <summary>
    /// Standalone topmost bar placed IN FRONT of the Windows taskbar (embedding inside
    /// the taskbar is impossible: no API for third-party UI there since desk bands died).
    /// Tight bounding box so taskbar clicks outside the bar still reach the taskbar.
    /// Never activates (NOACTIVATE): transport buttons work by mouse without stealing focus.
    /// Z-order is forced via WM_WINDOWPOSCHANGING so taskbar clicks can't push it behind.
    /// All settings live on the pinned PluginGadgetItem (Widget Customization): layout,
    /// toggles, visual style, size, position — saved with gadgets, covered by export/import.
    /// </summary>
    public class NowPlayingBarWindow : Window
    {
        private class NpBarSettings
        {
            public string Layout { get; set; } = "TaskbarSlim";
            public bool ShowSeekBar { get; set; } = true;
            public bool ShowControls { get; set; } = true;
            public bool ShowAppLabel { get; set; } = false;
            public bool ShowCover { get; set; } = true;
            public string AccentColor { get; set; } = "#FF7DD3FC";
            public string ButtonsColor { get; set; } = "#A0FFFFFF";
            public string ForcedSourceAppId { get; set; } = "";
        }

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_NOACTIVATE = 0x0010;
        private const int SWP_NOZORDER = 0x0004;

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

        private PluginGadgetItem? _item;
        private readonly NowPlayingView _view;
        private readonly Border _frame;
        private readonly Rectangle _resizeGrip;
        private IntPtr _barHwnd = IntPtr.Zero;
        private HwndSource? _hwndSource;
        private System.Windows.Threading.DispatcherTimer? _topmostTimer;
        private bool _placed;
        private bool _resizing;
        private Point _resizeStart;
        private double _resizeStartWidth;

        public NowPlayingBarWindow(PluginGadgetItem item)
        {
            Topmost = true;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            ShowActivated = false;

            _view = new NowPlayingView();

            _resizeGrip = new Rectangle
            {
                Width = 14,
                Height = 14,
                Fill = Brushes.Transparent,
                Cursor = Cursors.SizeNWSE,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 2, 2)
            };
            _resizeGrip.PreviewMouseLeftButtonDown += ResizeGrip_PreviewMouseLeftButtonDown;
            _resizeGrip.PreviewMouseMove += ResizeGrip_PreviewMouseMove;
            _resizeGrip.PreviewMouseLeftButtonUp += ResizeGrip_PreviewMouseLeftButtonUp;

            var grid = new Grid();
            grid.Children.Add(_view);
            grid.Children.Add(_resizeGrip);

            _frame = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(2),
                Child = grid
            };
            Content = _frame;

            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
            Closed += OnClosed;

            _topmostTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _topmostTimer.Tick += (_, _) => { ReassertTopmost(); UpdateFullscreenVisibility(); };

            _frame.PreviewMouseLeftButtonDown += Frame_PreviewMouseLeftButtonDown;
            _frame.PreviewMouseRightButtonUp += Frame_PreviewMouseRightButtonUp;

            Rebind(item);
        }

        public void Rebind(PluginGadgetItem item)
        {
            if (_item != null)
                _item.PropertyChanged -= Item_PropertyChanged;
            _item = item;
            _item.PropertyChanged += Item_PropertyChanged;
            ApplyAll();
        }

        private void ApplyAll()
        {
            ApplySettingsToView();
            ApplyGadgetStyle();
            ApplySize();
            ApplyPosition();
            ApplyGripVisibility();
        }

        private NpBarSettings ReadSettings()
        {
            try
            {
                if (_item != null && !string.IsNullOrEmpty(_item.CustomData))
                    return JsonConvert.DeserializeObject<NpBarSettings>(_item.CustomData) ?? new NpBarSettings();
            }
            catch { }
            return new NpBarSettings();
        }

        private void WriteSettings(NpBarSettings settings)
        {
            if (_item == null) return;
            _item.CustomData = JsonConvert.SerializeObject(settings);
            FindOverlay()?.SaveGadgetsToDisk();
        }

        private void ApplySettingsToView()
        {
            try
            {
                var s = ReadSettings();
                _view.ApplyCustomSettings(JsonConvert.SerializeObject(s));
            }
            catch { }
        }

        private static Color ParseColor(string? s, Color fallback)
        {
            try
            {
                if (!string.IsNullOrEmpty(s))
                    return (Color)ColorConverter.ConvertFromString(s);
            }
            catch { }
            return fallback;
        }

        private void ApplyGadgetStyle()
        {
            try
            {
                if (_item == null) return;
                Color bg = ParseColor(_item.BgColor, Color.FromArgb(0xE6, 0x14, 0x14, 0x14));
                double a = (bg.A / 255.0) * _item.BgOpacity * _item.Opacity;
                a = Math.Clamp(a, 0, 1);
                _frame.Background = new SolidColorBrush(Color.FromArgb((byte)(a * 255), bg.R, bg.G, bg.B));
                _frame.BorderBrush = new SolidColorBrush(ParseColor(_item.BorderColor, Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
                _frame.BorderThickness = new Thickness(Math.Max(0, _item.BorderThicknessValue));
                // Full-width strip takes the taskbar shape: square corners.
                _frame.CornerRadius = _item.BarFullWidth
                    ? new CornerRadius(0)
                    : new CornerRadius(Math.Max(0, _item.CornerRadiusValue));
                _frame.Padding = _item.PaddingThickness;
            }
            catch { }
        }

        private (double L, double T, double R, double B) PrimaryScreenDips()
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen == null) return (0, 0, 800, 600);
            var dpi = VisualTreeHelper.GetDpi(this);
            double dx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
            double dy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
            var b = screen.Bounds;
            return (b.Left / dx, b.Top / dy, b.Right / dx, b.Bottom / dy);
        }

        private void ApplySize()
        {
            try
            {
                if (_item == null) return;
                if (_item.BarFullWidth)
                {
                    var (l, _, r, _) = PrimaryScreenDips();
                    double w = Math.Max(200, r - l);
                    if (Math.Abs(Width - w) > 0.5) Width = w;
                }
                else
                {
                    double w = Math.Clamp(_item.Width, 280, 1200);
                    if (Math.Abs(Width - w) > 0.5) Width = w;
                }
            }
            catch { }
        }

        private void ApplyPosition()
        {
            try
            {
                if (_item == null) return;
                if (_item.BarFullWidth)
                {
                    var (l, _, _, b) = PrimaryScreenDips();
                    double h = ActualHeight > 0 ? ActualHeight : 60;
                    Left = l;
                    Top = b - h;
                    _item.BarLeft = Left;
                    _item.BarTop = Top;
                    _placed = true;
                }
                else if (!double.IsNaN(_item.BarLeft) && !double.IsNaN(_item.BarTop))
                {
                    Left = _item.BarLeft;
                    Top = _item.BarTop;
                    _placed = true;
                }
                else if (!_placed)
                {
                    PlaceOverTaskbar();
                    _placed = true;
                }
            }
            catch { }
        }

        /// <summary>Re-glue the bar to the screen (display change). Called by the overlay.</summary>
        public void RefreshGeometry()
        {
            try
            {
                ApplySize();
                ApplyPosition();
                ReassertTopmost();
            }
            catch { }
        }

        private void ApplyGripVisibility()
        {
            bool show = _item?.BarShowResizeHandle == true && _item?.BarFullWidth != true;
            _resizeGrip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Item_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_item == null) return;
            switch (e.PropertyName)
            {
                case nameof(PluginGadgetItem.CustomData):
                    ApplySettingsToView();
                    break;
                case nameof(PluginGadgetItem.BgColor):
                case nameof(PluginGadgetItem.BgOpacity):
                case nameof(PluginGadgetItem.BorderColor):
                case nameof(PluginGadgetItem.BorderThicknessValue):
                case nameof(PluginGadgetItem.CornerRadiusValue):
                case nameof(PluginGadgetItem.PaddingLeft):
                case nameof(PluginGadgetItem.PaddingTop):
                case nameof(PluginGadgetItem.PaddingRight):
                case nameof(PluginGadgetItem.PaddingBottom):
                case nameof(PluginGadgetItem.Opacity):
                    ApplyGadgetStyle();
                    break;
                case nameof(PluginGadgetItem.Width):
                    ApplySize();
                    break;
                case nameof(PluginGadgetItem.BarShowResizeHandle):
                    ApplyGripVisibility();
                    break;
                case nameof(PluginGadgetItem.BarFullWidth):
                    ApplyGadgetStyle();
                    ApplyGripVisibility();
                    RefreshGeometry();
                    break;
                case nameof(PluginGadgetItem.DockToTaskbar):
                    if (!_item.DockToTaskbar)
                    {
                        try { Close(); } catch { }
                    }
                    break;
            }
        }

        private static DesktopOverlayWindow? FindOverlay()
        {
            try
            {
                foreach (Window w in Application.Current.Windows)
                    if (w is DesktopOverlayWindow o) return o;
            }
            catch { }
            return null;
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
            // Whatever tries to move us in z-order (e.g. taskbar click), stay TOPMOST.
            if (msg == WM_WINDOWPOSCHANGING)
            {
                try
                {
                    var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                    wp.hwndInsertAfter = HWND_TOPMOST;
                    wp.flags &= ~SWP_NOZORDER;
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
                if (_item == null) return;
                if (!_item.BarHideFullscreen)
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
                // Shell windows are never "fullscreen content".
                if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "XamlExplorerHostIslandWindow")
                    return false;

                if (!GetWindowRect(fg, out RECT r)) return false;
                var screen = System.Windows.Forms.Screen.FromHandle(fg);
                var b = screen.Bounds;
                return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
            }
            catch { return false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _topmostTimer?.Start();
            ReassertTopmost();
            ApplyPosition();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            try { _topmostTimer?.Stop(); } catch { }
            _topmostTimer = null;
            try
            {
                if (_hwndSource != null)
                {
                    _hwndSource.RemoveHook(WndProcBar);
                    _hwndSource = null;
                }
            }
            catch { }
            if (_item != null)
                _item.PropertyChanged -= Item_PropertyChanged;
            _item = null;
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
                double bw = Width;
                double bh = ActualHeight > 0 ? ActualHeight : 80;

                switch (edge)
                {
                    case "top":
                        Left = (scrL + scrR - bw) / 2; Top = scrT;
                        break;
                    case "left":
                        Left = scrL; Top = (scrT + scrB - bh) / 2;
                        break;
                    case "right":
                        Left = scrR - bw; Top = (scrT + scrB - bh) / 2;
                        break;
                    default:
                        Left = (scrL + scrR - bw) / 2; Top = scrB - bh;
                        break;
                }

                if (_item != null)
                {
                    // No save here: persisting raises GadgetsChanged → SyncGadgets →
                    // RefreshNowPlayingPin → new bar → infinite recursion (severe freeze).
                    // Position persists via the pin toggle save + 5s timer + drag/menu saves.
                    _item.BarLeft = Left;
                    _item.BarTop = Top;
                }
            }
            catch { }
        }

        private void Frame_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Drag the bar by its background; never hijack clicks on transport buttons.
            DependencyObject? d = e.OriginalSource as DependencyObject;
            while (d != null)
            {
                if (d is Button) return;
                d = VisualTreeHelper.GetParent(d);
            }
            try
            {
                DragMove();
                if (_item != null && !double.IsNaN(Left) && !double.IsNaN(Top))
                {
                    _item.BarLeft = Left;
                    _item.BarTop = Top;
                    // Manual drag leaves the full-width taskbar shape → floating card.
                    // (Position set first so the geometry refresh keeps the drop point.)
                    if (_item.BarFullWidth) _item.BarFullWidth = false;
                    FindOverlay()?.SaveGadgetsToDisk();
                }
            }
            catch { }
        }

        private void ResizeGrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _resizing = true;
            _resizeStart = PointToScreen(e.GetPosition(this));
            _resizeStartWidth = Width;
            try { Mouse.Capture(_resizeGrip); } catch { }
            e.Handled = true;
        }

        private void ResizeGrip_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_resizing) return;
            try
            {
                Point p = PointToScreen(e.GetPosition(this));
                Width = Math.Clamp(_resizeStartWidth + (p.X - _resizeStart.X), 280, 1200);
            }
            catch { }
        }

        private void ResizeGrip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_resizing) return;
            _resizing = false;
            try { Mouse.Capture(null); } catch { }
            try
            {
                if (_item != null)
                {
                    _item.Width = Width;
                    FindOverlay()?.SaveGadgetsToDisk();
                }
            }
            catch { }
            e.Handled = true;
        }

        private void Frame_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var tr = TranslationService.Instance;
            var menu = new ContextMenu();

            var layoutMenu = new MenuItem { Header = tr["Widget_Ctx_NpLayout"] };
            var s = ReadSettings();
            string[] layouts = { "Classic", "Compact", "Fluent", "Taskbar", "TaskbarSlim" };
            string[] layoutKeys = { "Widget_Ctx_NpLayout_Classic", "Widget_Ctx_NpLayout_Compact", "Widget_Ctx_NpLayout_Fluent", "Widget_Ctx_NpLayout_Taskbar", "Widget_Ctx_NpLayout_TaskbarSlim" };
            for (int i = 0; i < layouts.Length; i++)
            {
                string l = layouts[i];
                var layoutItem = new MenuItem { Header = tr[layoutKeys[i]], IsCheckable = true, IsChecked = s.Layout.Equals(l, StringComparison.OrdinalIgnoreCase) };
                layoutItem.Click += (_, _) => { s.Layout = l; WriteSettings(s); ApplySettingsToView(); };
                layoutMenu.Items.Add(layoutItem);
            }
            menu.Items.Add(layoutMenu);

            var sourceItem = new MenuItem { Header = tr["Widget_Ctx_NpSource"] };
            BuildSourceMenu(sourceItem);
            menu.Items.Add(sourceItem);

            menu.Items.Add(new Separator());

            var coverItem = new MenuItem { Header = tr["Widget_Ctx_NpShowCover"], IsCheckable = true, IsChecked = s.ShowCover };
            coverItem.Click += (_, _) => { s.ShowCover = !s.ShowCover; WriteSettings(s); ApplySettingsToView(); };
            menu.Items.Add(coverItem);

            var seekItem = new MenuItem { Header = tr["Widget_Ctx_NpShowSeekBar"], IsCheckable = true, IsChecked = s.ShowSeekBar };
            seekItem.Click += (_, _) => { s.ShowSeekBar = !s.ShowSeekBar; WriteSettings(s); ApplySettingsToView(); };
            menu.Items.Add(seekItem);

            var controlsItem = new MenuItem { Header = tr["Widget_Ctx_NpShowControls"], IsCheckable = true, IsChecked = s.ShowControls };
            controlsItem.Click += (_, _) => { s.ShowControls = !s.ShowControls; WriteSettings(s); ApplySettingsToView(); };
            menu.Items.Add(controlsItem);

            var appItem = new MenuItem { Header = tr["Widget_Ctx_NpShowAppLabel"], IsCheckable = true, IsChecked = s.ShowAppLabel };
            appItem.Click += (_, _) => { s.ShowAppLabel = !s.ShowAppLabel; WriteSettings(s); ApplySettingsToView(); };
            menu.Items.Add(appItem);

            var accentMenu = new MenuItem { Header = tr["Widget_Ctx_NpAccentColor"] };
            string[] colorNames = { tr["Widget_Ctx_Col_IceBlue"], tr["Widget_Ctx_Col_White"], tr["Widget_Ctx_Col_Matrix"], tr["Widget_Ctx_Col_Amber"], tr["Widget_Ctx_Col_Cyber"], tr["Widget_Ctx_Col_KawaiiPink"], tr["Widget_Ctx_Col_Purple"], tr["Widget_Ctx_Col_Teal"], tr["Widget_Ctx_Col_Gold"], tr["Widget_Ctx_Col_Orange"], tr["Widget_Ctx_Col_Rose"], tr["Widget_Ctx_Col_Lime"] };
            string[] accentHex = { "#FF7DD3FC", "#FFFFFFFF", "#FF4AF626", "#FFFFB000", "#FFFF3E3E", "#FFFF71CE", "#FFA855F7", "#FF2DD4BF", "#FFFACC15", "#FFFB923C", "#FFFB7185", "#FFA3E635" };
            for (int i = 0; i < colorNames.Length; i++)
            {
                string code = accentHex[i];
                var colItem = new MenuItem { Header = colorNames[i], IsCheckable = true, IsChecked = (s.AccentColor ?? "").Equals(code, StringComparison.OrdinalIgnoreCase) };
                colItem.Click += (_, _) => { s.AccentColor = code; WriteSettings(s); ApplySettingsToView(); };
                accentMenu.Items.Add(colItem);
            }
            menu.Items.Add(accentMenu);

            var buttonsMenu = new MenuItem { Header = tr["Widget_Ctx_NpButtonsColor"] };
            string[] btnNames = { tr["Widget_Ctx_NpButtons_Default"], tr["Widget_Ctx_Col_IceBlue"], tr["Widget_Ctx_Col_White"], tr["Widget_Ctx_Col_Matrix"], tr["Widget_Ctx_Col_Amber"], tr["Widget_Ctx_Col_Cyber"], tr["Widget_Ctx_Col_KawaiiPink"], tr["Widget_Ctx_Col_Purple"], tr["Widget_Ctx_Col_Teal"], tr["Widget_Ctx_Col_Gold"], tr["Widget_Ctx_Col_Orange"], tr["Widget_Ctx_Col_Rose"], tr["Widget_Ctx_Col_Lime"] };
            string[] btnHex = { "#A0FFFFFF", "#FF7DD3FC", "#FFFFFFFF", "#FF4AF626", "#FFFFB000", "#FFFF3E3E", "#FFFF71CE", "#FFA855F7", "#FF2DD4BF", "#FFFACC15", "#FFFB923C", "#FFFB7185", "#FFA3E635" };
            for (int i = 0; i < btnNames.Length; i++)
            {
                string code = btnHex[i];
                var colItem = new MenuItem { Header = btnNames[i], IsCheckable = true, IsChecked = (s.ButtonsColor ?? "").Equals(code, StringComparison.OrdinalIgnoreCase) };
                colItem.Click += (_, _) => { s.ButtonsColor = code; WriteSettings(s); ApplySettingsToView(); };
                buttonsMenu.Items.Add(colItem);
            }
            menu.Items.Add(buttonsMenu);

            menu.Items.Add(new Separator());

            var resizeItem = new MenuItem { Header = tr["Widget_Ctx_NpBarResize"], IsCheckable = true, IsChecked = _item?.BarShowResizeHandle == true };
            resizeItem.Click += (_, _) =>
            {
                if (_item == null) return;
                _item.BarShowResizeHandle = !_item.BarShowResizeHandle;
                FindOverlay()?.SaveGadgetsToDisk();
            };
            menu.Items.Add(resizeItem);

            var fullItem = new MenuItem { Header = tr["Widget_Ctx_NpBarFullWidth"], IsCheckable = true, IsChecked = _item?.BarFullWidth == true };
            fullItem.Click += (_, _) =>
            {
                if (_item == null) return;
                _item.BarFullWidth = !_item.BarFullWidth;
                FindOverlay()?.SaveGadgetsToDisk();
            };
            menu.Items.Add(fullItem);

            var fsItem = new MenuItem { Header = tr["Widget_Ctx_NpBarHideFs"], IsCheckable = true, IsChecked = _item?.BarHideFullscreen != false };
            fsItem.Click += (_, _) =>
            {
                if (_item == null) return;
                _item.BarHideFullscreen = !_item.BarHideFullscreen;
                FindOverlay()?.SaveGadgetsToDisk();
            };
            menu.Items.Add(fsItem);

            var unpinItem = new MenuItem { Header = tr["Widget_Ctx_UnpinFromTaskbar"] };
            unpinItem.Click += (_, _) =>
            {
                if (_item == null) return;
                _item.DockToTaskbar = false;
                _item.IsLocked = false;
                FindOverlay()?.SaveGadgetsToDisk();
                FindOverlay()?.RefreshNowPlayingPin();
            };
            menu.Items.Add(unpinItem);

            menu.IsOpen = true;
            e.Handled = true;
        }

        private void BuildSourceMenu(MenuItem parent)
        {
            var tr = TranslationService.Instance;

            var autoItem = new MenuItem
            {
                Header = tr["Widget_Ctx_NpSourceAuto"],
                IsCheckable = true,
                IsChecked = !_view.IsManualSource
            };
            autoItem.Click += (_, _) => PinBarSource(null);
            parent.Items.Add(autoItem);

            if (_view.SourceSessions.Count > 0)
            {
                parent.Items.Add(new Separator());
                bool anyChecked = false;
                foreach (var session in _view.SourceSessions)
                {
                    bool isPlaying = false;
                    try
                    {
                        var info = session.GetPlaybackInfo();
                        isPlaying = info.PlaybackStatus == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    }
                    catch { }

                    bool isChecked = _view.IsManualSource && ReferenceEquals(session, _view.ActiveSession);
                    anyChecked |= isChecked;
                    var item = new MenuItem
                    {
                        Header = (isPlaying ? "\u25B6 " : "") + _view.SourceDisplayName(session),
                        IsCheckable = true,
                        IsChecked = isChecked
                    };
                    item.Click += (_, _) => PinBarSource(session);
                    parent.Items.Add(item);
                }

                if (_view.IsManualSource && !anyChecked)
                {
                    parent.Items.Add(new Separator());
                    parent.Items.Add(new MenuItem
                    {
                        Header = "\u25B6 " + _view.PinnedSourceName,
                        IsCheckable = true,
                        IsChecked = true,
                        IsEnabled = false
                    });
                }
            }
            else if (_view.IsManualSource)
            {
                parent.Items.Add(new Separator());
                parent.Items.Add(new MenuItem
                {
                    Header = "\u25B6 " + _view.PinnedSourceName,
                    IsCheckable = true,
                    IsChecked = true,
                    IsEnabled = false
                });
            }
        }

        private void PinBarSource(Windows.Media.Control.GlobalSystemMediaTransportControlsSession? session)
        {
            // Persist the pin (app id) so it survives reboot + export/import.
            var s = ReadSettings();
            try { s.ForcedSourceAppId = session?.SourceAppUserModelId ?? ""; }
            catch { s.ForcedSourceAppId = ""; }
            WriteSettings(s); // → CustomData change → ApplySettingsToView → view re-resolves
            ApplySettingsToView();
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, int uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    }
}
