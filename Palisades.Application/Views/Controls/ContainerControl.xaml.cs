using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Newtonsoft.Json;
using Palisades.Models;
using Palisades.Services;
using Palisades.ViewModels;

namespace Palisades.Views.Controls
{
    public partial class ContainerControl : UserControl
    {
        private readonly ContainerViewModel _vm = null!;
        private Canvas? _parentCanvas;
        private bool _isDragging;
        private Point _dragStartCanvas;
        private double _dragStartLeft;
        private double _dragStartTop;
        private bool _isResizing;
        private string _resizeDirection = "";
        private Point _resizeStartPoint;
        private Point _resizeStartCanvasPoint;
        private Rect _resizeStartRect;
        private TranslateTransform? _dragTransform;
        private DateTime _lastDragUpdate = DateTime.MinValue;
        private ShortcutItem? _clickPendingItem;
        private Point? _clickPendingPoint;
        private readonly HashSet<ShortcutItem> _selectedShortcuts = new();
        private bool _isRectSelecting;
        private System.Windows.Shapes.Rectangle? _selectionRect;
        private System.Windows.Shapes.Rectangle? _insertionMarker;
        private Point _selectionStartPoint;

        /// <summary>Offset of parent overlay window from virtual desktop origin.</summary>
        internal double OverlayOffsetX { get; set; }
        internal double OverlayOffsetY { get; set; }

        public ContainerControl(ContainerViewModel viewModel)
        {
            try
            {
                InitializeComponent();
                _vm = viewModel;
                DataContext = viewModel;

                viewModel.Shortcuts.CollectionChanged += (_, _) =>
                {
                    _selectedShortcuts.RemoveWhere(s => !viewModel.Shortcuts.Contains(s));
                    foreach (var s in _vm.SelectedShortcuts.ToList())
                        if (!viewModel.Shortcuts.Contains(s))
                            _vm.SelectedShortcuts.Remove(s);
                };

                viewModel.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ContainerViewModel.CurrentOpacity))
                        UpdateOpacity();

                    if (e.PropertyName is nameof(ContainerViewModel.FilterEnabled)
                        or nameof(ContainerViewModel.FilterType)
                        or nameof(ContainerViewModel.FilterPattern))
                    {
                        if (Resources["ShortcutsView"] is System.Windows.Data.CollectionViewSource cvs)
                            cvs.View?.Refresh();
                    }

                    if (e.PropertyName == nameof(ContainerViewModel.IsPasswordLocked) && viewModel.IsPasswordLocked)
                    {
                        _autoLockTimer?.Stop();
                        viewModel.ClearUnlockPassword();
                    }

                    if (e.PropertyName == nameof(ContainerViewModel.ClipHeight))
                        UpdateClip();

                    if (e.PropertyName == nameof(ContainerViewModel.CornerRadius))
                        UpdateClip();

                    if (e.PropertyName == nameof(ContainerViewModel.IsHovered) && _parentCanvas != null)
                        Canvas.SetZIndex(this, viewModel.IsHovered ? 100 : 1);

                    if (e.PropertyName == nameof(ContainerViewModel.ContainerThemeName))
                        UpdateContainerTheme();

                    // Re-apply theme when BodyOpacity changes so background opacity is updated
                    if (e.PropertyName == nameof(ContainerViewModel.BodyOpacity))
                        UpdateContainerTheme();

                    if (e.PropertyName == nameof(ContainerViewModel.HeaderColor))
                        Resources["ContainerHeaderBrush"] = new SolidColorBrush(viewModel.HeaderColor);

                    if (e.PropertyName == nameof(ContainerViewModel.TitleColor))
                        Resources["ContainerTitleForeground"] = new SolidColorBrush(viewModel.TitleColor);

                    if (e.PropertyName == nameof(ContainerViewModel.LabelsColor))
                        Resources["ContainerLabelsForeground"] = new SolidColorBrush(viewModel.LabelsColor);

                    if (e.PropertyName == nameof(ContainerViewModel.BodyColorWithOpacity))
                    {
                        if (viewModel.BodyColorWithOpacity is Color startColor)
                        {
                            if (viewModel.IsGradient && viewModel.GradientEndColor != null &&
                                ColorConverter.ConvertFromString(viewModel.GradientEndColor) is Color endRaw)
                            {
                                var endColor = Color.FromArgb(startColor.A, endRaw.R, endRaw.G, endRaw.B);
                                Resources["ContainerBackgroundBrush"] = new LinearGradientBrush(startColor, endColor, new Point(0, 0), new Point(0, 1));
                            }
                            else
                            {
                                Resources["ContainerBackgroundBrush"] = new SolidColorBrush(startColor);
                            }
                        }
                    }
                };

                viewModel.RequestCreateShortcut += OnRequestCreateShortcut;

                Loaded += OnLoaded;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating container control: {ex.Message}");
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _parentCanvas = VisualTreeHelper.GetParent(this) as Canvas;
            UpdateOpacity();
            UpdateClip();
            UpdateContainerTheme();

            // Sync position from ViewModel
            if (_parentCanvas != null)
            {
                Canvas.SetLeft(this, _vm.X - OverlayOffsetX);
                Canvas.SetTop(this, _vm.Y - OverlayOffsetY);
            }

            // Set up dynamic filter for shortcuts
            if (Resources["ShortcutsView"] is System.Windows.Data.CollectionViewSource cvs && cvs.View != null)
                cvs.View.Filter = FilterShortcut;

                    // Set initial chevron rotation if visually collapsed on load
            if (_vm.IsVisuallyCollapsed && ChevronPath?.RenderTransform is RotateTransform rt)
                rt.Angle = 180;
        }

        private bool FilterShortcut(object obj)
        {
            if (obj is not ShortcutItem shortcut)
                return false;

            var model = _vm.Model;
            if (!model.FilterEnabled)
                return true;

            bool passesTypeFilter = model.FilterType switch
            {
                "Programs" => IsProgram(shortcut),
                "Documents" => IsDocument(shortcut),
                "Folders" => IsFolder(shortcut),
                "Custom" => MatchesCustom(shortcut, model.FilterPattern),
                _ => true // "All" or unknown
            };

            if (!passesTypeFilter)
                return false;

            // Text search filter when the search box is active
            if (_vm.IsSearchActive && !string.IsNullOrEmpty(_vm.SearchQuery))
            {
                return shortcut.Name.Contains(_vm.SearchQuery, StringComparison.OrdinalIgnoreCase)
                    || (shortcut.TargetPath?.Contains(_vm.SearchQuery, StringComparison.OrdinalIgnoreCase) ?? false);
            }

            return true;
        }

        private static bool IsProgram(ShortcutItem s)
        {
            var ext = Path.GetExtension(s.TargetPath)?.ToLowerInvariant();
            return ext switch
            {
                ".exe" or ".lnk" or ".url" or ".bat" or ".cmd" or ".ps1" or ".msi" or ".appref-ms" => true,
                _ => false
            };
        }

        private static bool IsDocument(ShortcutItem s)
        {
            var ext = Path.GetExtension(s.TargetPath)?.ToLowerInvariant();
            return ext switch
            {
                ".doc" or ".docx" or ".pdf" or ".txt" or ".xls" or ".xlsx"
                or ".ppt" or ".pptx" or ".odt" or ".ods" or ".odp" or ".rtf"
                or ".csv" or ".md" or ".json" or ".xml" => true,
                _ => false
            };
        }

        private static bool IsFolder(ShortcutItem s)
        {
            try { return File.GetAttributes(s.TargetPath).HasFlag(FileAttributes.Directory); }
            catch { return false; }
        }

        private static bool MatchesCustom(ShortcutItem s, string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return true;

            bool isExclude = pattern.StartsWith("!");
            string p = isExclude ? pattern[1..] : pattern;

            bool match = s.Name.Contains(p, StringComparison.OrdinalIgnoreCase);
            return isExclude ? !match : match;
        }

        private void UpdateOpacity()
        {
            Opacity = _vm.CurrentOpacity;
        }

        private void UpdateClip()
        {
            double cr = _vm.CornerRadius;
            if (cr <= 0)
            {
                MainBorder.Clip = null;
                return;
            }
            double ch = _vm.ClipHeight;
            double h = Height > 0 ? Height : ActualHeight;
            if (ch >= h) ch = h;
            if (ch > 0)
                MainBorder.Clip = new RectangleGeometry(
                    new Rect(0, 0, MainBorder.ActualWidth, ch),
                    cr, cr);
            else
                MainBorder.Clip = new RectangleGeometry(
                    new Rect(0, 0, MainBorder.ActualWidth, 1),
                    cr, cr);
        }

        private void ContainerControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateClip();
        }

        private void UpdateContainerTheme()
        {
            var themeName = _vm.ContainerThemeName;
            this.Resources.MergedDictionaries.Clear();

            if (string.IsNullOrEmpty(themeName) || themeName == "Theme" || themeName == "Custom")
                return;

            var preset = ThemeService.Presets.FirstOrDefault(p => p.Name.Equals(themeName, StringComparison.OrdinalIgnoreCase));
            if (preset != null)
            {
                var dict = new ResourceDictionary();
                var headerColor = (Color)ColorConverter.ConvertFromString(preset.HeaderColor);
                var bodyColor = (Color)ColorConverter.ConvertFromString(preset.BodyColor);
                var titleColor = (Color)ColorConverter.ConvertFromString(preset.TitleColor);
                var labelsColor = (Color)ColorConverter.ConvertFromString(preset.LabelsColor);

                // Apply BodyOpacity to background (user-configurable transparency)
                byte bgAlpha = (byte)Math.Round(_vm.BodyOpacity / 100.0 * 255);
                var bodyColorWithOpacity = Color.FromArgb(bgAlpha, bodyColor.R, bodyColor.G, bodyColor.B);
                dict["ContainerBackgroundBrush"] = new SolidColorBrush(bodyColorWithOpacity);
                dict["ContainerHeaderBrush"] = new SolidColorBrush(headerColor);
                dict["ContainerTitleForeground"] = new SolidColorBrush(titleColor);
                dict["ContainerLabelsForeground"] = new SolidColorBrush(labelsColor);
                dict["ContainerCornerRadius"] = new CornerRadius(12);
                dict["ContainerBorderThickness"] = new Thickness(1);

                if (preset.Name.Equals("Light", StringComparison.OrdinalIgnoreCase))
                {
                    dict["ContainerBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x25, 0x00, 0x00, 0x00));
                    dict["ContainerBorderBrushHover"] = new SolidColorBrush(Color.FromArgb(0x65, 0x00, 0x00, 0x00));
                    dict["ContainerHeaderBrushHover"] = new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE7));
                }
                else
                {
                    dict["ContainerBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF));
                    dict["ContainerBorderBrushHover"] = new SolidColorBrush(Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF));
                    dict["ContainerHeaderBrushHover"] = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));
                }
                this.Resources.MergedDictionaries.Add(dict);
            }
            else
            {
                string themesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes");
                string xamlPath = Path.Combine(themesDir, themeName + ".xaml");
                if (File.Exists(xamlPath))
                {
                    try
                    {
                        var dict = new ResourceDictionary { Source = new Uri(xamlPath, UriKind.Absolute) };
                        this.Resources.MergedDictionaries.Add(dict);
                    }
                    catch { }
                }
            }
        }

        #region Drag (header)

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_vm.IsLocked || _parentCanvas == null) return;
            _isDragging = true;
            _dragStartCanvas = e.GetPosition(_parentCanvas);
            _dragStartLeft = Canvas.GetLeft(this);
            _dragStartTop = Canvas.GetTop(this);
            _dragTransform = new TranslateTransform(0, 0);
            RenderTransform = _dragTransform;
            // Cache visual tree as bitmap during drag — avoids re-rendering effects per frame
            var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            CacheMode = new BitmapCache { RenderAtScale = dpiScale };
            MainBorder.Effect = null;
            Mouse.Capture(this);
            e.Handled = true;
        }

        private void HeaderBar_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_vm.TitleHoverEffect)
                _vm.IsTitleHovered = true;
        }

        private void HeaderBar_MouseLeave(object sender, MouseEventArgs e)
        {
            _vm.IsTitleHovered = false;
        }

        private void ContainerControl_MouseMove(object sender, MouseEventArgs e)
        {
            if (_parentCanvas == null) return;

            if (_isDragging && _dragTransform != null)
            {
                // Throttle RenderTransform updates to ~16ms (60fps) — prevents GPU saturation
                var now = DateTime.UtcNow;
                if ((now - _lastDragUpdate).TotalMilliseconds < 16)
                {
                    e.Handled = true;
                    return;
                }
                _lastDragUpdate = now;

                var current = e.GetPosition(_parentCanvas);
                double rawX = current.X - _dragStartCanvas.X;
                double rawY = current.Y - _dragStartCanvas.Y;

                // Apply snap to proposed absolute position, then convert back to transform
                double proposedLeft = _dragStartLeft + rawX;
                double proposedTop = _dragStartTop + rawY;
                var (snapX, snapY) = ApplySnap(proposedLeft, proposedTop, Width, Height);
                _dragTransform.X = snapX - _dragStartLeft;
                _dragTransform.Y = snapY - _dragStartTop;
                e.Handled = true;
            }
            else if (_isResizing)
            {
                ResizeUpdate(e);
                e.Handled = true;
            }
        }

        private void ContainerControl_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                ClearSnap();
                Mouse.Capture(null);

                // Apply final position — RenderTransform moves WITHOUT layout,
                // so we transfer to Canvas.Left/Top once on release
                if (_dragTransform != null)
                {
                    double finalX = _dragStartLeft + _dragTransform.X;
                    double finalY = _dragStartTop + _dragTransform.Y;
                    RenderTransform = null;
                    _dragTransform = null;

                    finalX = Math.Clamp(finalX, -Width + 40, _parentCanvas!.RenderSize.Width + 500);
                    finalY = Math.Clamp(finalY, -Height + 40, _parentCanvas.RenderSize.Height + 500);

                    Canvas.SetLeft(this, finalX);
                    Canvas.SetTop(this, finalY);
                }
                // Restore normal rendering (disable bitmap cache) and effects
                CacheMode = null;
                MainBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 20,
                    Opacity = 0.35,
                    ShadowDepth = 3,
                    Direction = 270
                };
                _vm.X = Canvas.GetLeft(this) + OverlayOffsetX;
                _vm.Y = Canvas.GetTop(this) + OverlayOffsetY;
                _vm.Save();

                // Check for merge with another container
                var overlay = Window.GetWindow(this) as DesktopOverlayWindow;
                if (overlay != null)
                {
                    var canvasPt = new Point(
                        Canvas.GetLeft(this) + Width / 2,
                        Canvas.GetTop(this) + Height / 2);
                    var other = overlay.FindContainerAt(canvasPt, _vm.Identifier);
                    if (other != null)
                    {
                        var result = System.Windows.MessageBox.Show(
                            $"Fusionner \"{_vm.Name}\" dans \"{other.Name}\" ?",
                            "Fusion", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (result == MessageBoxResult.Yes)
                        {
                            foreach (var item in _vm.Shortcuts.ToList())
                            {
                                if (!other.Shortcuts.Any(s =>
                                    s.TargetPath == item.TargetPath && s.Name == item.Name))
                                    other.Shortcuts.Add(item);
                            }
                            other.Save();
                            overlay.RemoveContainer(_vm.Identifier);
                            ContainerManager.Instance.DeleteContainer(_vm.Identifier);
                        }
                    }
                }

                // Auto-hide on edge if enabled
                if (_vm.AutoHideOnEdge)
                {
                    double left = Canvas.GetLeft(this) + OverlayOffsetX;
                    double top = Canvas.GetTop(this) + OverlayOffsetY;
                    double right = left + Width;
                    double bottom = top + Height;
                    const int edgeThreshold = 15;

                    foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                    {
                        var sb = screen.Bounds;
                        if (left <= sb.Left + edgeThreshold || right >= sb.Right - edgeThreshold ||
                            top <= sb.Top + edgeThreshold || bottom >= sb.Bottom - edgeThreshold)
                        {
                            _vm.AutoHide = true;
                            _vm.IsHovered = false;
                            break;
                        }
                    }
                }

                // Collision resolution
                try
                {
                    ContainerManager.Instance.ResolveCollisions(_vm.Model);
                    var model = ContainerManager.Instance.GetContainer(_vm.Identifier);
                    if (model != null && _parentCanvas != null)
                    {
                        Canvas.SetLeft(this, model.X - OverlayOffsetX);
                        Canvas.SetTop(this, model.Y - OverlayOffsetY);
                    }
                }
                catch { }
                e.Handled = true;
            }
            else if (_isResizing)
            {
                _isResizing = false;
                Mouse.Capture(null);

                _vm.Width = Width;
                _vm.Height = Height;

                try
                {
                    ContainerManager.Instance.ResolveCollisions(_vm.Model);
                }
                catch { }
                e.Handled = true;
            }
        }

        #endregion

        #region Snap

        private const double SNAP_THRESHOLD = 25.0;
        private const double SNAP_HYSTERESIS = 35.0;

        private bool _isSnapped;
        private readonly System.Windows.Threading.DispatcherTimer _snapFlashTimer = new();

        /// <summary>
        /// Snap the dragged container's edges/centers to nearby containers.
        /// Hold Alt while dragging to bypass snapping for pixel-perfect placement.
        /// </summary>
        private (double x, double y) ApplySnap(double currentLeft, double currentTop, double width, double height)
        {
            if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
            {
                ClearSnap();
                return (currentLeft, currentTop);
            }

            if (_parentCanvas == null)
                return (currentLeft, currentTop);

            double snapX = currentLeft;
            double snapY = currentTop;
            // Hysteresis: once snapped, harder to escape
            double threshold = _isSnapped ? SNAP_HYSTERESIS : SNAP_THRESHOLD;
            double bestDistX = threshold;
            double bestDistY = threshold;

            double r = currentLeft + width;
            double b = currentTop + height;
            double cx = currentLeft + width / 2;
            double cy = currentTop + height / 2;

            // Screen-edge snapping — snap to any monitor edge
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var sb = screen.Bounds;
                double sl = sb.Left - OverlayOffsetX;
                double sr = sb.Right - OverlayOffsetX;
                double st = sb.Top - OverlayOffsetY;
                double sbot = sb.Bottom - OverlayOffsetY;

                // Horizontal snap to screen edge — needs vertical overlap
                if (currentTop < sbot && b > st)
                {
                    CheckSnap(currentLeft, sl, ref bestDistX, ref snapX);
                    CheckSnap(r, sr, ref bestDistX, ref snapX, width);
                    CheckSnap(currentLeft, sr, ref bestDistX, ref snapX);
                    CheckSnap(r, sl, ref bestDistX, ref snapX, width);
                    double scx = (sl + sr) / 2;
                    CheckSnap(cx, scx, ref bestDistX, ref snapX, width / 2);
                }

                // Vertical snap to screen edge — needs horizontal overlap
                if (currentLeft < sr && r > sl)
                {
                    CheckSnap(currentTop, st, ref bestDistY, ref snapY);
                    CheckSnap(b, sbot, ref bestDistY, ref snapY, height);
                    CheckSnap(currentTop, sbot, ref bestDistY, ref snapY);
                    CheckSnap(b, st, ref bestDistY, ref snapY, height);
                    double scy = (st + sbot) / 2;
                    CheckSnap(cy, scy, ref bestDistY, ref snapY, height / 2);
                }
            }

            // Inter-container snapping
            foreach (UIElement child in _parentCanvas.Children)
            {
                if (child == this || child.Visibility != Visibility.Visible)
                    continue;
                if (child is not ContainerControl other)
                    continue;

                double ol = Canvas.GetLeft(other);
                double ot = Canvas.GetTop(other);
                double ow = other.Width;
                double oh = other.Height;
                double or_ = ol + ow;
                double ob = ot + oh;
                double ocx = ol + ow / 2;
                double ocy = ot + oh / 2;

                bool vertOverlap = currentTop < ob && b > ot;
                if (vertOverlap)
                {
                    CheckSnap(currentLeft, ol, ref bestDistX, ref snapX);
                    CheckSnap(currentLeft, or_, ref bestDistX, ref snapX);
                    CheckSnap(r, ol, ref bestDistX, ref snapX, width);
                    CheckSnap(r, or_, ref bestDistX, ref snapX, width);
                    CheckSnap(cx, ocx, ref bestDistX, ref snapX, width / 2);
                }

                bool horizOverlap = currentLeft < or_ && r > ol;
                if (horizOverlap)
                {
                    CheckSnap(currentTop, ot, ref bestDistY, ref snapY);
                    CheckSnap(currentTop, ob, ref bestDistY, ref snapY);
                    CheckSnap(b, ot, ref bestDistY, ref snapY, height);
                    CheckSnap(b, ob, ref bestDistY, ref snapY, height);
                    CheckSnap(cy, ocy, ref bestDistY, ref snapY, height / 2);
                }
            }

            bool snapped = Math.Abs(snapX - currentLeft) > 0.5 || Math.Abs(snapY - currentTop) > 0.5;
            if (snapped)
                ShowSnapFeedback();
            else
                ClearSnap();

            return (snapX, snapY);
        }

        private static void CheckSnap(double a, double b, ref double best, ref double snapResult, double offset = 0)
        {
            double d = Math.Abs(a - b);
            if (d < best)
            {
                best = d;
                snapResult = b - offset;
            }
        }

        private void ShowSnapFeedback()
        {
            if (_isSnapped) return;
            _isSnapped = true;

            // Bright white flash on the border
            MainBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF));

            _snapFlashTimer.Interval = TimeSpan.FromMilliseconds(250);
            _snapFlashTimer.Tick -= OnSnapFlashEnd;
            _snapFlashTimer.Tick += OnSnapFlashEnd;
            _snapFlashTimer.IsEnabled = true;
            _snapFlashTimer.Start();
        }

        private void ClearSnap()
        {
            if (!_isSnapped && !_snapFlashTimer.IsEnabled) return;
            _isSnapped = false;
            _snapFlashTimer.IsEnabled = false;
            MainBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF));
        }

        private void OnSnapFlashEnd(object? sender, EventArgs e)
        {
            _snapFlashTimer.IsEnabled = false;
            // Keep border bright while still snapped
            if (!_isSnapped)
                MainBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF));
        }

        #endregion

        #region Resize

        private void Resize_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_vm.IsLocked) return;

            _vm.NotifyResizeStarted();

            _isResizing = true;
            _resizeStartPoint = e.GetPosition(this);
            _resizeStartCanvasPoint = _parentCanvas != null ? e.GetPosition(_parentCanvas) : default;
            _resizeStartRect = new Rect(
                Canvas.GetLeft(this), Canvas.GetTop(this),
                Width, Height);

            if (sender == ResizeLeft) _resizeDirection = "Left";
            else if (sender == ResizeRight) _resizeDirection = "Right";
            else if (sender == ResizeTop) _resizeDirection = "Top";
            else if (sender == ResizeBottom) _resizeDirection = "Bottom";
            else if (sender == ResizeTopLeft) _resizeDirection = "TopLeft";
            else if (sender == ResizeTopRight) _resizeDirection = "TopRight";
            else if (sender == ResizeBottomLeft) _resizeDirection = "BottomLeft";
            else if (sender == ResizeBottomRight) _resizeDirection = "BottomRight";

            Mouse.Capture(this);
            e.Handled = true;
        }

        private void Resize_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isResizing) return;
            e.Handled = true;
        }

        private void Resize_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isResizing) return;
            _isResizing = false;
            Mouse.Capture(null);

            _vm.Width = Width;
            _vm.Height = Height;

            try { ContainerManager.Instance.ResolveCollisions(_vm.Model); }
            catch { }
            e.Handled = true;
        }

        private void ResizeUpdate(MouseEventArgs e)
        {
            try
            {
                var currentPos = e.GetPosition(this);
                double dx = currentPos.X - _resizeStartPoint.X;
                double dy = currentPos.Y - _resizeStartPoint.Y;

                double newLeft = _resizeStartRect.X;
                double newTop = _resizeStartRect.Y;
                double newW = _resizeStartRect.Width;
                double newH = _resizeStartRect.Height;

                const double minW = 200, minH = 100;

                if (_resizeDirection.Contains("Left"))
                {
                    var canvasPt = _parentCanvas != null ? e.GetPosition(_parentCanvas) : currentPos;
                    double canvasDx = canvasPt.X - _resizeStartCanvasPoint.X;
                    double possibleW = _resizeStartRect.Width - canvasDx;
                    if (possibleW >= minW)
                    {
                        newLeft = _resizeStartRect.X + canvasDx;
                        newW = possibleW;
                    }
                    else
                    {
                        newLeft = _resizeStartRect.Right - minW;
                        newW = minW;
                    }
                }
                if (_resizeDirection.Contains("Right"))
                {
                    newW = Math.Max(minW, _resizeStartRect.Width + dx);
                }
                if (_resizeDirection.Contains("Top"))
                {
                    var canvasPt = _parentCanvas != null ? e.GetPosition(_parentCanvas) : currentPos;
                    double canvasDy = canvasPt.Y - _resizeStartCanvasPoint.Y;
                    double possibleH = _resizeStartRect.Height - canvasDy;
                    if (possibleH >= minH)
                    {
                        newTop = _resizeStartRect.Y + canvasDy;
                        newH = possibleH;
                    }
                    else
                    {
                        newTop = _resizeStartRect.Bottom - minH;
                        newH = minH;
                    }
                }
                if (_resizeDirection.Contains("Bottom"))
                {
                    newH = Math.Max(minH, _resizeStartRect.Height + dy);
                }

                if (_parentCanvas != null)
                {
                    Canvas.SetLeft(this, newLeft);
                    Canvas.SetTop(this, newTop);
                }
                Width = newW;
                Height = newH;

                _vm.Width = newW;
                _vm.Height = newH;
                _vm.ClipHeight = newH;
                _vm.X = newLeft + OverlayOffsetX;
                _vm.Y = newTop + OverlayOffsetY;
            }
            catch { }
        }

        #endregion

        #region Context Menu & Shell

        /// <summary>
        /// If the right-click was on a shortcut item (Shell context menu case),
        /// suppress the container-level ContextMenu to avoid double menus.
        /// </summary>
        private void ContainerControl_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            // Walk up from the source to check if this is a shortcut item
            var elem = e.OriginalSource as DependencyObject;
            while (elem != null)
            {
                if (elem is FrameworkElement fe && fe.DataContext is ShortcutItem)
                {
                    e.Handled = true;
                    return;
                }
                elem = VisualTreeHelper.GetParent(elem);
            }
        }

        private void MainBorder_RightClick(object sender, MouseButtonEventArgs e)
        {
            // ContextMenu is handled by WPF automatically (defined on UserControl)
        }

        #endregion

        #region Drop

        private void ItemsArea_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void ItemsArea_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(ShortcutItem)) is ShortcutItem item)
            {
                DropShortcutInContainer(item, e);
            }
            else if (e.Data.GetData(typeof(List<ShortcutItem>)) is List<ShortcutItem> items && items.Count > 0)
            {
                foreach (var it in items)
                    DropShortcutInContainer(it, e);
            }
        }

        private void DropShortcutInContainer(ShortcutItem item, DragEventArgs e)
        {
            var list = _vm.Shortcuts;
            var srcVM = ShortcutReorderHandler.FindContainerForShortcut(item);

            if (srcVM == _vm)
            {
                // Reorder within same container
                int targetIdx = GetDropIndex(e);
                int curIdx = list.IndexOf(item);
                if (curIdx >= 0 && targetIdx >= 0 && curIdx != targetIdx)
                {
                    if (curIdx < targetIdx)
                        list.Move(curIdx, Math.Min(targetIdx, list.Count - 1));
                    else
                        list.Move(curIdx, targetIdx);
                }
            }
            else if (srcVM != null)
            {
                srcVM.Shortcuts.Remove(item);
                int targetIdx = GetDropIndex(e);
                if (!list.Contains(item))
                {
                    if (targetIdx >= 0 && targetIdx <= list.Count)
                        list.Insert(targetIdx, item);
                    else
                        list.Add(item);
                }
                srcVM.Save();
            }
            else
            {
                ContainerManager.Instance.MoveToContainer(item, _vm.Model);
            }
            _vm.Save();
        }

        public void ReorderAtCanvasPoint(Point canvasPt, List<ShortcutItem> items)
        {
            if (ShortcutsControl == null || _vm.Shortcuts.Count == 0) return;

            var overlayWindow = Window.GetWindow(this);
            if (overlayWindow == null) return;

            var transform = overlayWindow.TransformToDescendant(ShortcutsControl);
            Point localPt = transform.Transform(canvasPt);

            int bestIdx = _vm.Shortcuts.Count;
            double bestDist = double.MaxValue;

            for (int i = 0; i < _vm.Shortcuts.Count; i++)
            {
                var container = ShortcutsControl.ItemContainerGenerator.ContainerFromItem(_vm.Shortcuts[i]) as FrameworkElement;
                if (container == null) continue;

                var tr = container.TransformToAncestor(ShortcutsControl);
                var rectInControl = tr.TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
                Point center = new Point(rectInControl.X + rectInControl.Width / 2, rectInControl.Y + rectInControl.Height / 2);
                double dist = Math.Sqrt(Math.Pow(localPt.X - center.X, 2) + Math.Pow(localPt.Y - center.Y, 2));

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = localPt.X > center.X ? i + 1 : i;
                }
            }

            var itemsToReorder = items.Where(i => _vm.Shortcuts.Contains(i)).ToList();
            foreach (var item in itemsToReorder)
            {
                int oldIdx = _vm.Shortcuts.IndexOf(item);
                if (oldIdx < 0) continue;

                int newIdx = bestIdx;
                if (oldIdx < newIdx) newIdx--;
                newIdx = Math.Clamp(newIdx, 0, _vm.Shortcuts.Count - 1);

                if (oldIdx != newIdx)
                    _vm.Shortcuts.Move(oldIdx, newIdx);
            }
            _vm.Save();
        }

        public void UpdateInsertionMarker(Point canvasPt)
        {
            if (SelectionCanvas == null || ShortcutsControl == null) return;

            if (_insertionMarker == null)
            {
                _insertionMarker = new System.Windows.Shapes.Rectangle
                {
                    Width = 2,
                    Height = 40,
                    Fill = Brushes.White,
                    RadiusX = 1,
                    RadiusY = 1,
                    IsHitTestVisible = false
                };
                SelectionCanvas.Children.Add(_insertionMarker);
            }

            var overlayWindow = Window.GetWindow(this);
            if (overlayWindow == null) return;
            var transform = overlayWindow.TransformToDescendant(ShortcutsControl);
            Point localPt = transform.Transform(canvasPt);

            double bestDist = double.MaxValue;
            Rect targetRect = Rect.Empty;
            bool insertAfter = false;

            for (int i = 0; i < _vm.Shortcuts.Count; i++)
            {
                var container = ShortcutsControl.ItemContainerGenerator.ContainerFromItem(_vm.Shortcuts[i]) as FrameworkElement;
                if (container == null) continue;

                var tr = container.TransformToAncestor(ShortcutsControl);
                var rectInControl = tr.TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
                Point center = new Point(rectInControl.X + rectInControl.Width / 2, rectInControl.Y + rectInControl.Height / 2);
                double dist = Math.Sqrt(Math.Pow(localPt.X - center.X, 2) + Math.Pow(localPt.Y - center.Y, 2));

                if (dist < bestDist)
                {
                    bestDist = dist;
                    targetRect = rectInControl;
                    insertAfter = localPt.X > center.X;
                }
            }

            if (!targetRect.IsEmpty)
            {
                double markerX = insertAfter ? targetRect.Right + 2 : targetRect.Left - 4;
                Canvas.SetLeft(_insertionMarker, markerX);
                Canvas.SetTop(_insertionMarker, targetRect.Top);
                _insertionMarker.Height = targetRect.Height;
                _insertionMarker.Visibility = Visibility.Visible;
            }
        }

        public void ClearInsertionMarker()
        {
            if (_insertionMarker != null)
            {
                SelectionCanvas?.Children.Remove(_insertionMarker);
                _insertionMarker = null;
            }
        }

        private int GetDropIndex(DragEventArgs e)
        {
            var grid = ContentScrollViewer?.Content as Grid;
            if (grid == null) return -1;

            var dropPt = e.GetPosition(grid);
            double bestDist = double.MaxValue;
            int bestIdx = -1;

            for (int i = 0; i < _vm.Shortcuts.Count; i++)
            {
                var container = ShortcutsControl?.ItemContainerGenerator.ContainerFromItem(_vm.Shortcuts[i]);
                if (container == null) continue;

                var border = FindVisualChild<Border>(container, b => b.DataContext is ShortcutItem);
                if (border == null) continue;

                var transform = border.TransformToAncestor(grid);
                var bounds = transform.TransformBounds(new Rect(0, 0, border.ActualWidth, border.ActualHeight));
                double center = bounds.Top + bounds.Height / 2;

                double dist = Math.Abs(dropPt.Y - center);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = dropPt.Y > center ? i + 1 : i;
                }
            }

            return bestIdx;
        }

        private void MainBorder_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            try
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (var file in files)
                {
                    var extension = Path.GetExtension(file)?.ToLower();
                    ShortcutItem? item = null;

                    if (extension == ".lnk")
                        item = ShortcutItem.FromLnk(file);
                    else if (extension == ".url")
                        item = ShortcutItem.FromUrl(file);

                    if (item != null && !_vm.Shortcuts.Any(s =>
                        s.TargetPath == item.TargetPath && s.Name == item.Name))
                    {
                        _vm.Shortcuts.Add(item);
                    }
                }
                _vm.Save();
            }
            catch { }
        }

        #endregion

        #region Shortcut interactions

        private void Shortcut_OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is ShortcutItem item)
            {
                if (e.ChangedButton == MouseButton.Right)
                {
                    _clickPendingItem = null;
                    if (!_selectedShortcuts.Contains(item))
                    {
                        _selectedShortcuts.Clear();
                        _vm.SelectedShortcuts.Clear();
                        _selectedShortcuts.Add(item);
                        _vm.SelectedShortcuts.Add(item);
                    }
                    e.Handled = true;
                    try
                    {
                        if (_vm.UseShellContextMenu && !_vm.IsSvgButtonContainer)
                            ShowNativeShellContextMenu(item);
                        else
                            ShowWindowsContextMenu(item);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Context menu error: {ex.Message}");
                    }
                    return;
                }

                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

                if (ctrl)
                {
                    if (_selectedShortcuts.Contains(item))
                    {
                        _selectedShortcuts.Remove(item);
                        _vm.SelectedShortcuts.Remove(item);
                    }
                    else
                    {
                        _selectedShortcuts.Add(item);
                        _vm.SelectedShortcuts.Add(item);
                    }
                    UpdateSelectionVisual();
                    return;
                }

                if (shift && _selectedShortcuts.Count > 0)
                {
                    var ordered = _vm.Shortcuts.ToList();
                    int selIdx = ordered.IndexOf(item);
                    int firstSel = ordered.IndexOf(_selectedShortcuts.First());
                    if (selIdx >= 0 && firstSel >= 0)
                    {
                        int min = Math.Min(firstSel, selIdx);
                        int max = Math.Max(firstSel, selIdx);
                        _selectedShortcuts.Clear();
                        _vm.SelectedShortcuts.Clear();
                        for (int i = min; i <= max; i++)
                        {
                            _selectedShortcuts.Add(ordered[i]);
                            _vm.SelectedShortcuts.Add(ordered[i]);
                        }
                    }
                    UpdateSelectionVisual();
                    return;
                }

                if (!_selectedShortcuts.Contains(item))
                {
                    _selectedShortcuts.Clear();
                    _vm.SelectedShortcuts.Clear();
                    _selectedShortcuts.Add(item);
                    _vm.SelectedShortcuts.Add(item);
                    UpdateSelectionVisual();
                }

                _vm.SelectedShortcut = item;
                _clickPendingPoint = e.GetPosition(this);

                if (_vm.OpenOnDoubleClick)
                {
                    if (e.ClickCount == 2)
                    {
                        _clickPendingItem = null;
                        LaunchShortcut(item);
                        return;
                    }
                }
                else
                {
                    _clickPendingItem = item;
                }
            }
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonDown(e);

            if (_isRectSelecting) return;

            Grid? grid = null;
            if (e.OriginalSource is DependencyObject source)
            {
                var itemBorder = FindVisualParent<Border>(source, b => b.DataContext is ShortcutItem);
                if (itemBorder != null) return;

                // Do not initiate rubber band selection if the user clicked the "+" button
                var addSvgBorder = FindVisualParent<Border>(source, b => b.Name == "AddSvgButtonBorder");
                if (addSvgBorder != null) return;

                grid = ContentScrollViewer?.Content as Grid;
                if (grid == null || !IsDescendantOf(source, grid)) return;
            }

            grid ??= ContentScrollViewer?.Content as Grid;
            if (grid != null)
            {
                var gridPt = e.GetPosition(grid);
                System.Diagnostics.Debug.WriteLine($"RB: gridPt={gridPt}");
                if (gridPt.X < 0 || gridPt.Y < 0) return;
                _selectedShortcuts.Clear();
                _vm.SelectedShortcuts.Clear();
                _clickPendingItem = null;
                _clickPendingPoint = gridPt;
                UpdateSelectionVisual();
                StartRubberBand(gridPt);
            }
        }

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);

            if (_clickPendingPoint.HasValue && e.LeftButton == MouseButtonState.Pressed && !_isRectSelecting && !_isDragging)
            {
                var currentPos = e.GetPosition(this);
                double dx = currentPos.X - _clickPendingPoint.Value.X;
                double dy = currentPos.Y - _clickPendingPoint.Value.Y;
                double threshold = Math.Max(SystemParameters.MinimumHorizontalDragDistance, 4);
                if (Math.Abs(dx) > threshold || Math.Abs(dy) > threshold)
                {
                    _clickPendingItem = null;
                    var items = _vm.SelectedShortcuts.ToList();
                    if (items.Count == 0)
                        items = _vm.Shortcuts.Take(1).ToList();
                    if (items.Count > 0)
                    {
                        var overlay = Window.GetWindow(this) as DesktopOverlayWindow;
                        overlay?.StartContainerDrag(items, _vm);
                    }
                }
            }

            if (!_isRectSelecting) return;

            var grid = ContentScrollViewer?.Content as Grid;
            if (grid == null) return;

            var gridPt = e.GetPosition(grid);
            UpdateRubberBand(gridPt);
        }

        protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonUp(e);

            if (_isRectSelecting)
            {
                var grid = ContentScrollViewer?.Content as Grid;
                if (grid != null)
                {
                    var gridPt = e.GetPosition(grid);
                    EndRubberBand(gridPt);
                }
                return;
            }

            if (_clickPendingItem == null || !_clickPendingPoint.HasValue) return;

            var currentPos = e.GetPosition(this);
            double dx = currentPos.X - _clickPendingPoint.Value.X;
            double dy = currentPos.Y - _clickPendingPoint.Value.Y;
            double threshold = Math.Max(SystemParameters.MinimumHorizontalDragDistance, 4);

            if (Math.Abs(dx) < threshold && Math.Abs(dy) < threshold)
            {
                var item = _clickPendingItem;
                _clickPendingItem = null;
                LaunchShortcut(item);
            }
            else
            {
                _clickPendingItem = null;
            }
        }

        private void UpdateSelectionVisual()
        {
            var grid = ContentScrollViewer?.Content as Grid;
            if (grid == null) return;

            foreach (var item in _vm.Shortcuts)
            {
                var container = ShortcutsControl?.ItemContainerGenerator.ContainerFromItem(item);
                if (container == null) continue;

                var border = FindVisualChild<Border>(container, b => b.DataContext is ShortcutItem);
                if (border != null)
                {
                    bool sel = _selectedShortcuts.Contains(item);
                    border.Background = sel
                        ? new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF))
                        : Brushes.Transparent;
                }
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent, Func<T, bool>? predicate = null)
            where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T tChild && (predicate == null || predicate(tChild)))
                    return tChild;

                var found = FindVisualChild<T>(child, predicate);
                if (found != null)
                    return found;
            }
            return null;
        }

        private static bool IsDescendantOf(DependencyObject? child, DependencyObject? ancestor)
        {
            if (child == null || ancestor == null) return false;
            var current = child;
            while (current != null)
            {
                if (current == ancestor) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private static T? FindVisualParent<T>(DependencyObject child, Func<T, bool>? predicate = null)
            where T : DependencyObject
        {
            var current = child;
            while (current != null)
            {
                if (current is T tParent && (predicate == null || predicate(tParent)))
                    return tParent;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void StartRubberBand(Point gridPt)
        {
            var grid = ContentScrollViewer?.Content as Grid;
            if (grid == null) return;

            _isRectSelecting = true;
            _selectionStartPoint = gridPt;
            Mouse.Capture(this);
            _selectionRect = new System.Windows.Shapes.Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                StrokeDashArray = new DoubleCollection { 4, 2 }
            };
            Canvas.SetLeft(_selectionRect, gridPt.X);
            Canvas.SetTop(_selectionRect, gridPt.Y);
            _selectionRect.Width = 0;
            _selectionRect.Height = 0;
            SelectionCanvas.Children.Add(_selectionRect);
        }

        private void UpdateRubberBand(Point gridPt)
        {
            if (_selectionRect == null) return;

            var grid = ContentScrollViewer?.Content as Grid;
            if (grid == null) return;

            double x = Math.Min(_selectionStartPoint.X, gridPt.X);
            double y = Math.Min(_selectionStartPoint.Y, gridPt.Y);
            double w = Math.Abs(gridPt.X - _selectionStartPoint.X);
            double h = Math.Abs(gridPt.Y - _selectionStartPoint.Y);
            Canvas.SetLeft(_selectionRect, x);
            Canvas.SetTop(_selectionRect, y);
            _selectionRect.Width = w;
            _selectionRect.Height = h;

            // Highlight items as we go
            if (w > 5 || h > 5)
            {
                var selRect = new Rect(x, y, w, h);
                foreach (var item in _vm.Shortcuts)
                {
                    var container = ShortcutsControl?.ItemContainerGenerator.ContainerFromItem(item);
                    if (container == null) continue;

                    var border = FindVisualChild<Border>(container, b => b.DataContext is ShortcutItem);
                    if (border == null) continue;

                    var transform = border.TransformToAncestor(grid);
                    var bounds = transform.TransformBounds(new Rect(0, 0, border.ActualWidth, border.ActualHeight));
                    bool intersects = selRect.IntersectsWith(bounds);
                    border.Background = intersects
                        ? new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF))
                        : Brushes.Transparent;
                }
            }
        }

        private void EndRubberBand(Point gridPt)
        {
            _isRectSelecting = false;
            Mouse.Capture(null);

            var grid = ContentScrollViewer?.Content as Grid;
            if (grid == null) return;

            if (_selectionRect != null)
            {
                SelectionCanvas.Children.Remove(_selectionRect);
                _selectionRect = null;

                double x = Math.Min(_selectionStartPoint.X, gridPt.X);
                double y = Math.Min(_selectionStartPoint.Y, gridPt.Y);
                double w = Math.Abs(gridPt.X - _selectionStartPoint.X);
                double h = Math.Abs(gridPt.Y - _selectionStartPoint.Y);
                var selRect = new Rect(x, y, w, h);

                if (w < 5 && h < 5)
                {
                    _selectedShortcuts.Clear();
                    _vm.SelectedShortcuts.Clear();
                    UpdateSelectionVisual();
                    return;
                }

                _selectedShortcuts.Clear();
                _vm.SelectedShortcuts.Clear();
                foreach (var item in _vm.Shortcuts)
                {
                    var container = ShortcutsControl?.ItemContainerGenerator.ContainerFromItem(item);
                    if (container == null) continue;

                    var border = FindVisualChild<Border>(container, b => b.DataContext is ShortcutItem);
                    if (border == null) continue;

                    var transform = border.TransformToAncestor(grid);
                    var bounds = transform.TransformBounds(new Rect(0, 0, border.ActualWidth, border.ActualHeight));
                    if (selRect.IntersectsWith(bounds))
                    {
                        _selectedShortcuts.Add(item);
                        _vm.SelectedShortcuts.Add(item);
                    }
                }
            }
            UpdateSelectionVisual();
        }

        public void ResetDragState()
        {
            _clickPendingItem = null;
            _clickPendingPoint = null;
            if (_selectionRect != null)
            {
                SelectionCanvas.Children.Remove(_selectionRect);
                _selectionRect = null;
            }
            _isRectSelecting = false;
        }

        private void ShowWindowsContextMenu(ShortcutItem item)
        {
            try
            {
                string? target = item.IsUrl ? item.UrlTarget : item.TargetPath;
                if (!_vm.IsSvgButtonContainer && string.IsNullOrEmpty(target)) return;

                bool isFile = !string.IsNullOrEmpty(target) && File.Exists(target);
                bool isDir = !string.IsNullOrEmpty(target) && Directory.Exists(target);
                if (!_vm.IsSvgButtonContainer && !item.IsUrl && !isFile && !isDir) return;

                // Build a simple context menu with common file operations
                var contextMenu = new ContextMenu();
                contextMenu.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
                contextMenu.Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
                contextMenu.BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
                contextMenu.BorderThickness = new Thickness(1);

                // Edit SVG Button (Only in SVG Button Mode)
                if (_vm.IsSvgButtonContainer)
                {
                    var editSvgItem = new MenuItem { Header = "Edit SVG Button..." };
                    editSvgItem.Click += (_, _) =>
                    {
                        EnableWindowActivation();
                        var editWindow = new SvgButtonEditWindow(item);
                        editWindow.Topmost = true;
                        try
                        {
                            var owner = System.Windows.Application.Current.Windows.OfType<Window>()
                                .FirstOrDefault(w => w.IsVisible && !(w is DesktopOverlayWindow) && !(w is ContainerWindow));
                            if (owner != null)
                            {
                                editWindow.Owner = owner;
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Owner error: {ex.Message}");
                        }

                        bool? dialogResult = false;
                        try
                        {
                            dialogResult = editWindow.ShowDialog();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"ShowDialog error: {ex.Message}\n{ex.StackTrace}");
                        }
                        finally
                        {
                            DisableWindowActivation();
                        }

                        if (dialogResult == true)
                        {
                            item.Name = editWindow.ButtonName;
                            if (editWindow.TargetPath.StartsWith("http://") || editWindow.TargetPath.StartsWith("https://"))
                            {
                                item.IsUrl = true;
                                item.UrlTarget = editWindow.TargetPath;
                                item.TargetPath = string.Empty;
                            }
                            else
                            {
                                item.IsUrl = false;
                                item.TargetPath = editWindow.TargetPath;
                                item.UrlTarget = string.Empty;
                            }
                            item.Arguments = editWindow.TargetArguments;
                            item.Hotkey = editWindow.Hotkey;
                            item.SvgContent = editWindow.SvgContent;
                            _vm.Save();

                            var overlay = Window.GetWindow(this) as DesktopOverlayWindow;
                            overlay?.RefreshGlobalHotkeys();
                        }
                    };
                    contextMenu.Items.Add(editSvgItem);
                    contextMenu.Items.Add(new Separator());
                }

                // Open
                if (!string.IsNullOrEmpty(target))
                {
                    var openItem = new MenuItem { Header = "Open" };
                    openItem.Click += (_, _) => LaunchShortcut(item);
                    contextMenu.Items.Add(openItem);
                }

                // Open file location (only for .lnk)
                if (!string.IsNullOrEmpty(target) && !item.IsUrl && isFile && Path.GetExtension(target)?.ToLower() == ".lnk")
                {
                    var openLocationItem = new MenuItem { Header = "Open file location" };
                    openLocationItem.Click += (_, _) =>
                    {
                        try
                        {
                            string? lnkTarget = ShortcutItem.GetLnkTargetPath(target);
                            if (!string.IsNullOrEmpty(lnkTarget))
                            {
                                string? dir = Path.GetDirectoryName(lnkTarget);
                                if (Directory.Exists(dir))
                                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{lnkTarget}\"");
                            }
                        }
                        catch { }
                    };
                    contextMenu.Items.Add(openLocationItem);
                }

                if (!string.IsNullOrEmpty(target))
                {
                    contextMenu.Items.Add(new Separator());
                }

                // Copy path
                if (!string.IsNullOrEmpty(target))
                {
                    var copyPathItem = new MenuItem { Header = "Copy path" };
                    copyPathItem.Click += (_, _) =>
                    {
                        try { Clipboard.SetText(target); }
                        catch { }
                    };
                    contextMenu.Items.Add(copyPathItem);
                }

                // Copy file
                if (!string.IsNullOrEmpty(target))
                {
                    var copyFileItem = new MenuItem { Header = "Copy" };
                    copyFileItem.Click += (_, _) =>
                    {
                        try
                        {
                            var data = new DataObject();
                            data.SetData(DataFormats.FileDrop, new[] { target });
                            data.SetData(DataFormats.Text, target);
                            Clipboard.SetDataObject(data);
                        }
                        catch { }
                    };
                    contextMenu.Items.Add(copyFileItem);
                }

                // Delete
                var deleteItem = new MenuItem { Header = "Delete" };
                deleteItem.Click += (_, _) =>
                {
                    try
                    {
                        if (_vm.IsSvgButtonContainer)
                        {
                            _vm.Shortcuts.Remove(item);
                            _vm.Save();

                            var overlay = Window.GetWindow(this) as DesktopOverlayWindow;
                            overlay?.RefreshGlobalHotkeys();
                        }
                        else
                        {
                            var result = System.Windows.MessageBox.Show(
                                "Delete from container or from desktop?",
                                "Delete Shortcut",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Question);
                            _vm.Shortcuts.Remove(item);
                            _vm.Save();

                            if (result == MessageBoxResult.Yes)
                            {
                                try { File.Delete(target); } catch { }
                            }
                            else
                            {
                                ContainerManager.Instance.ReturnToUnassigned(item);
                            }
                        }
                    }
                    catch { }
                };
                contextMenu.Items.Add(deleteItem);

                // Properties
                if (!string.IsNullOrEmpty(target))
                {
                    contextMenu.Items.Add(new Separator());
                    var propertiesItem = new MenuItem { Header = "Properties" };
                    propertiesItem.Click += (_, _) =>
                    {
                        try
                        {
                            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{target}\"");
                        }
                        catch { }
                    };
                    contextMenu.Items.Add(propertiesItem);
                }

                // Position and show
                contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                contextMenu.IsOpen = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ShowWindowsContextMenu error: {ex}");
            }
        }

        private void ShowNativeShellContextMenu(ShortcutItem item)
        {
            try
            {
                // Use the shortcut file itself (.lnk / .url) not the resolved target
                string? menuPath = item.ShortcutPath;
                if (string.IsNullOrEmpty(menuPath) || !File.Exists(menuPath))
                {
                    menuPath = item.IsUrl ? item.UrlTarget : item.TargetPath;
                    if (string.IsNullOrEmpty(menuPath) || (!File.Exists(menuPath) && !Directory.Exists(menuPath)))
                        return;
                }

                var overlayWindow = Window.GetWindow(this);
                IntPtr overlayHwnd = overlayWindow != null
                    ? new WindowInteropHelper(overlayWindow).Handle
                    : IntPtr.Zero;

                var pt = GetMouseScreenPoint();
                string? verb = ShellContextMenu.ShowMenu(overlayHwnd, menuPath, (int)pt.X, (int)pt.Y, false, true);
                if (verb != null && verb.Equals("delete", StringComparison.OrdinalIgnoreCase))
                {
                    var result = System.Windows.MessageBox.Show(
                        "Delete from container or from desktop?",
                        "Delete Shortcut",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (_vm != null)
                    {
                        _vm.Shortcuts.Remove(item);
                        _vm.Save();
                    }

                    if (result == MessageBoxResult.Yes)
                    {
                        try
                        {
                            if (File.Exists(menuPath))
                                File.Delete(menuPath);
                            else if (Directory.Exists(menuPath))
                                Directory.Delete(menuPath, true);
                        }
                        catch { }
                    }
                    else
                    {
                        ContainerManager.Instance.ReturnToUnassigned(item);
                    }
                }
                ContainerManager.Instance.SyncDeletedShortcuts();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ShellContextMenu error: {ex.Message}");
            }
        }

        private static System.Windows.Point GetMouseScreenPoint()
        {
            var mousePos = System.Windows.Forms.Control.MousePosition;
            return new System.Windows.Point(mousePos.X, mousePos.Y);
        }

        private static void LaunchShortcut(ShortcutItem item)
        {
            try
            {
                if (item.IsUrl && !string.IsNullOrEmpty(item.UrlTarget))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = item.UrlTarget,
                        UseShellExecute = true
                    });
                }
                else if (!string.IsNullOrEmpty(item.TargetPath))
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = item.TargetPath,
                        UseShellExecute = true
                    };

                    if (!string.IsNullOrEmpty(item.Arguments))
                        psi.Arguments = item.Arguments;

                    if (!string.IsNullOrEmpty(item.WorkingDirectory))
                        psi.WorkingDirectory = item.WorkingDirectory;

                    System.Diagnostics.Process.Start(psi);
                }
            }
            catch { }
        }

        #endregion

        #region Hover

        private void MainBorder_MouseEnter(object sender, MouseEventArgs e)
        {
            _vm.IsHovered = true;
            ResetAutoLockTimer();
        }

        private void MainBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            _vm.IsHovered = false;
        }

        #endregion

        #region iTop-like Header: Search, Hamburger, Title

        private System.Windows.Threading.DispatcherTimer? _clickTimer;
        private System.Windows.Threading.DispatcherTimer? _searchIdleTimer;

        private void TitleTextBlock_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            if (e.ClickCount == 2)
            {
                // Double-click → rename
                _clickTimer?.Stop();
                _vm.BeginEditTitle();
                EnableWindowActivation();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    TitleEditBox?.Focus();
                    TitleEditBox?.SelectAll();
                }));
                e.Handled = true;
            }
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                _searchIdleTimer?.Stop();
                _vm.SearchQuery = string.Empty;
                _vm.IsSearchActive = false;
                DisableWindowActivation();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter)
            {
                _searchIdleTimer?.Stop();
                _vm.IsSearchActive = false;
                DisableWindowActivation();
                e.Handled = true;
                return;
            }
            CancelSearchIdleTimer();
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CancelSearchIdleTimer();
            _vm.SearchQuery = string.Empty;
            _vm.IsSearchActive = false;
            DisableWindowActivation();
        }

        private void HamburgerButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.IsOpen = true;
            }
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            _vm.IsSearchActive = true;
            _vm.SearchQuery = string.Empty;
            EnableWindowActivation();
            Window.GetWindow(this)?.Activate();
            StartSearchIdleTimer();
            Dispatcher.BeginInvoke(
                new Action(() => SearchBox?.Focus()),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        private void StartSearchIdleTimer()
        {
            _searchIdleTimer?.Stop();
            _searchIdleTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            EventHandler handler = null!;
            handler = (_, _) =>
            {
                _searchIdleTimer!.Stop();
                _searchIdleTimer.Tick -= handler;
                _vm.SearchQuery = string.Empty;
                _vm.IsSearchActive = false;
                DisableWindowActivation();
            };
            _searchIdleTimer.Tick += handler;
            _searchIdleTimer.Start();
        }

        private void CancelSearchIdleTimer()
        {
            _searchIdleTimer?.Stop();
        }

        private void LockBoxButton_Click(object sender, RoutedEventArgs e)
        {
            UnlockPasswordBox.Clear();
            _vm.LockPrivateBox();
        }

        private void HamburgerMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.MenuItem item && item.CommandParameter is string param)
            {
                switch (param)
                {
                    case "View":
                        // Cycle filter type: All → Programs → Documents → Folders → All
                        _vm.FilterType = _vm.FilterType switch
                        {
                            "All" => "Programs",
                            "Programs" => "Documents",
                            "Documents" => "Folders",
                            _ => "All"
                        };
                        _vm.FilterEnabled = _vm.FilterType != "All";
                        break;
                    case "Sort":
                        // Cycle sort: show counter on/off
                        _vm.ShowCounter = !_vm.ShowCounter;
                        break;
                    case "Rules":
                        // Toggle auto-sort categories dialog via edit
                        _vm.EditCommand.Execute(null);
                        break;
                }
            }
        }

        private void OnRequestCreateShortcut()
        {
            var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = "Shortcuts (*.lnk;*.url)|*.lnk;*.url|All files|*.*",
                Title = "Add shortcut"
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var ext = System.IO.Path.GetExtension(dialog.FileName)?.ToLowerInvariant();
                ShortcutItem? item = null;
                if (ext == ".lnk")
                    item = ShortcutItem.FromLnk(dialog.FileName);
                else if (ext == ".url")
                    item = ShortcutItem.FromUrl(dialog.FileName);

                if (item != null && !_vm.Shortcuts.Any(s => s.TargetPath == item.TargetPath))
                {
                    _vm.Shortcuts.Add(item);
                    _vm.Save();
                }
            }
        }

        #endregion

        #region Inline Title Editing

        private void TitleEdit_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _vm.CommitEditTitle();
                DisableWindowActivation();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _vm.CancelEditTitle();
                DisableWindowActivation();
                e.Handled = true;
            }
        }

        private void TitleEdit_LostFocus(object sender, RoutedEventArgs e)
        {
            _vm.CommitEditTitle();
            DisableWindowActivation();
        }

        #endregion

        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private bool _activationEnabled;

        private void EnableWindowActivation()
        {
            try
            {
                var window = Window.GetWindow(this);
                if (window == null) return;
                var helper = new WindowInteropHelper(window);
                var hwnd = helper.Handle;
                if (hwnd == IntPtr.Zero) return;
                const int GWL_EXSTYLE = -20;
                const int WS_EX_NOACTIVATE = 0x08000000;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                if ((exStyle & WS_EX_NOACTIVATE) != 0)
                {
                    SetWindowLong(hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_NOACTIVATE);
                    _activationEnabled = true;
                }
            }
            catch { }
        }

        private void DisableWindowActivation()
        {
            if (!_activationEnabled) return;
            try
            {
                var window = Window.GetWindow(this);
                if (window == null) return;
                var helper = new WindowInteropHelper(window);
                var hwnd = helper.Handle;
                if (hwnd == IntPtr.Zero) return;
                const int GWL_EXSTYLE = -20;
                const int WS_EX_NOACTIVATE = 0x08000000;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
                _activationEnabled = false;
            }
            catch { }
        }

        #region Private Box Unlock

        private System.Windows.Threading.DispatcherTimer? _autoLockTimer;

        private void ResetAutoLockTimer()
        {
            _autoLockTimer?.Stop();
            if (_vm.PrivateBoxAutoLockSeconds <= 0 || _vm.IsPasswordLocked) return;
            _autoLockTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(_vm.PrivateBoxAutoLockSeconds)
            };
            EventHandler handler = null!;
            handler = (_, _) =>
            {
                _autoLockTimer!.Stop();
                _autoLockTimer.Tick -= handler;
                _vm.LockPrivateBox();
            };
            _autoLockTimer.Tick += handler;
            _autoLockTimer.Start();
        }

        private void UnlockButton_Click(object sender, RoutedEventArgs e)
        {
            TryUnlock();
        }

        private void UnlockPassword_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            UnlockPasswordBox?.Focus();
        }

        private void UnlockPassword_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                TryUnlock();
                e.Handled = true;
            }
        }

        private void TryUnlock()
        {
            try
            {
                string password = UnlockPasswordBox.Password;
                if (string.IsNullOrEmpty(password))
                {
                    UnlockErrorText.Text = "Please enter a password.";
                    return;
                }

                if (!Services.EncryptionService.VerifyPassword(password, _vm.PasswordHash))
                {
                    UnlockErrorText.Text = "Incorrect password.";
                    UnlockPasswordBox.Clear();
                    return;
                }

                // Decrypt shortcuts
                var encrypted = _vm.Model.EncryptedShortcuts;
                if (string.IsNullOrEmpty(encrypted))
                {
                    _vm.IsPasswordLocked = false;
                    _vm.SetUnlockPassword(password);
                    _vm.Save();
                    ResetAutoLockTimer();
                    return;
                }

                string? json = Services.EncryptionService.Decrypt(encrypted, password);
                if (json == null)
                {
                    UnlockErrorText.Text = "Decryption failed. Wrong password?";
                    return;
                }

                var shortcuts = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.ObjectModel.ObservableCollection<ShortcutItem>>(json);
                if (shortcuts != null)
                {
                    _vm.Model.Shortcuts.Clear();
                    foreach (var s in shortcuts)
                        _vm.Model.Shortcuts.Add(s);
                    _vm.IsPasswordLocked = false;
                    _vm.SetUnlockPassword(password);
                    _vm.Model.EncryptedShortcuts = null;
                    _vm.Save();
                    UnlockPasswordBox.Clear();
                    ResetAutoLockTimer();
                    UnlockErrorText.Text = "";
                }
            }
            catch (Exception ex)
            {
                UnlockErrorText.Text = $"Error: {ex.Message}";
            }
        }

        #endregion

        #region Shell Context Menu (COM Interop)

        /// <summary>
        /// Shows the real Windows shell context menu on a dedicated STA thread.
        /// This isolates the COM context from the overlay window's reparented HWND,
        /// which causes the shell to return E_NOTIMPL or crash shell extensions.
        /// Uses ref IntPtr (not IntPtr[]) to avoid .NET 8 array marshaling bugs.
        /// </summary>
        internal static class ShellContextMenu
        {
            private static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
            private static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");

            private static bool IsWindowsDarkMode()
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                    if (key != null)
                    {
                        var val = key.GetValue("AppsUseLightTheme");
                        if (val is int i && i == 0)
                            return true;
                    }
                }
                catch { }
                return false;
            }

            private static string? GetCommandVerb(IContextMenu ctxMenu, IntPtr commandId)
            {
                const uint GCS_VERBW = 0x00000004;
                try
                {
                    var sb = new System.Text.StringBuilder(256);
                    int hr = ctxMenu.GetCommandString(commandId, GCS_VERBW, null, sb, sb.Capacity);
                    if (hr >= 0)
                        return sb.ToString();
                }
                catch { }

                const uint GCS_VERBA = 0x00000000;
                try
                {
                    var sb = new System.Text.StringBuilder(256);
                    int hr = ctxMenu.GetCommandString(commandId, GCS_VERBA, null, sb, sb.Capacity);
                    if (hr >= 0)
                        return sb.ToString();
                }
                catch { }

                return null;
            }

            [DllImport("uxtheme.dll", EntryPoint = "#135")]
            public static extern int SetPreferredAppMode(int preferredAppMode);

            [DllImport("uxtheme.dll", EntryPoint = "#136")]
            public static extern void FlushMenuThemes();

            [DllImport("dwmapi.dll")]
            private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

            public static string? ShowMenu(IntPtr ownerHwnd, string filePath, int x, int y, bool isBackground = false, bool interceptDelete = false)
            {
                // Create a clean HwndSource for COM isolation (GetUIObjectOf).
                using var source = new HwndSource(new HwndSourceParameters("MenuHost", 0, 0)
                {
                    WindowStyle = unchecked((int)0x80000000), // WS_POPUP
                });
                IntPtr hwnd = source.Handle;

                int hr = SHParseDisplayName(filePath, IntPtr.Zero, out IntPtr absolutePidl, 0, out _);
                if (hr < 0 || absolutePidl == IntPtr.Zero) return null;

                try
                {
                    var iidShellFolder = IID_IShellFolder;
                    hr = SHBindToParent(absolutePidl, ref iidShellFolder, out IntPtr parentFolderObj, out IntPtr relativePidl);
                    if (hr < 0 || parentFolderObj == IntPtr.Zero) return null;

                    try
                    {
                        var parentFolder = (IShellFolder)Marshal.GetObjectForIUnknown(parentFolderObj);
                        IShellFolder targetFolder = parentFolder;
                        IntPtr targetFolderObj = IntPtr.Zero;

                        if (isBackground)
                        {
                            hr = parentFolder.BindToObject(relativePidl, IntPtr.Zero, ref iidShellFolder, out targetFolderObj);
                            if (hr < 0 || targetFolderObj == IntPtr.Zero) return null;
                            targetFolder = (IShellFolder)Marshal.GetObjectForIUnknown(targetFolderObj);
                        }

                        try
                        {
                            IntPtr ctxMenuObj = IntPtr.Zero;
                            if (isBackground)
                            {
                                var iidContextMenu = IID_IContextMenu;
                                hr = targetFolder.CreateViewObject(hwnd, ref iidContextMenu, out ctxMenuObj);
                            }
                            else
                            {
                                var iidContextMenu = IID_IContextMenu;
                                hr = targetFolder.GetUIObjectOf(hwnd, 1, ref relativePidl, ref iidContextMenu, IntPtr.Zero, out ctxMenuObj);
                            }

                            if (hr < 0 || ctxMenuObj == IntPtr.Zero) return null;

                            try
                            {
                                var ctxMenu = (IContextMenu)Marshal.GetObjectForIUnknown(ctxMenuObj);
                                IntPtr hMenu = CreatePopupMenu();
                                if (hMenu == IntPtr.Zero) return null;

                                try
                                {
                                    ctxMenu.QueryContextMenu(hMenu, 0, 1, 0x7FFF, CMF_NORMAL);

                                    const uint flags = TPM_RETURNCMD | TPM_RIGHTBUTTON;

                                    // Auto-dismiss après 5 secondes
                                    const uint TIMER_ID = 1;
                                    SetTimer(hwnd, TIMER_ID, 5000, IntPtr.Zero);
                                    source.AddHook(MenuWndProc);

                                    IntPtr menuOwner = ownerHwnd != IntPtr.Zero ? ownerHwnd : hwnd;

                                    if (IsWindowsDarkMode())
                                    {
                                        try
                                        {
                                            SetPreferredAppMode(2); // ForceDark
                                            FlushMenuThemes();
                                            int trueVal = 1;
                                            DwmSetWindowAttribute(hwnd, 20, ref trueVal, sizeof(int));
                                            if (menuOwner != IntPtr.Zero)
                                            {
                                                DwmSetWindowAttribute(menuOwner, 20, ref trueVal, sizeof(int));
                                            }
                                        }
                                        catch { }
                                    }

                                    // Install low-level mouse hook to catch clicks on the WS_EX_NOACTIVATE overlay
                                    // that would otherwise not dismiss the menu
                                    _mouseHookHandle = IntPtr.Zero;
                                    _mouseProc = null;
                                    try
                                    {
                                        _mouseProc = LowLevelMouseProc;
                                        _mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc,
                                            Marshal.GetHINSTANCE(typeof(ShellContextMenu).Module), 0);
                                    }
                                    catch { /* hook may fail due to security policies */ }

                                    SetForegroundWindow(menuOwner);
                                    int cmd = TrackPopupMenuEx(hMenu, flags, x, y, menuOwner, IntPtr.Zero);
                                    PostMessage(menuOwner, 0x0000, IntPtr.Zero, IntPtr.Zero);

                                    if (_mouseHookHandle != IntPtr.Zero)
                                        UnhookWindowsHookEx(_mouseHookHandle);
                                    _mouseHookHandle = IntPtr.Zero;
                                    _mouseProc = null;

                                    KillTimer(hwnd, TIMER_ID);

                                    if (cmd > 0)
                                    {
                                        string? verb = GetCommandVerb(ctxMenu, (IntPtr)(cmd - 1));
                                        if (interceptDelete && verb?.Equals("delete", StringComparison.OrdinalIgnoreCase) == true)
                                        {
                                            return "delete";
                                        }

                                        string? workingDir = isBackground ? filePath : Path.GetDirectoryName(filePath);
                                        var invokeInfo = new CMINVOKECOMMANDINFOEX
                                        {
                                            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                                            fMask = CMIC_MASK_UNICODE,
                                            hwnd = hwnd,
                                            lpVerb = (IntPtr)(cmd - 1),
                                            nShow = SW_SHOWNORMAL,
                                            lpDirectory = workingDir,
                                            lpVerbW = (IntPtr)(cmd - 1),
                                            lpDirectoryW = workingDir,
                                            ptInvoke = new POINT { X = x, Y = y }
                                        };
                                        ctxMenu.InvokeCommand(ref invokeInfo);
                                        return verb;
                                    }
                                }
                                finally
                                {
                                    DestroyMenu(hMenu);
                                }
                            }
                            finally
                            {
                                if (ctxMenuObj != IntPtr.Zero) Marshal.Release(ctxMenuObj);
                            }
                        }
                        finally
                        {
                            if (targetFolderObj != IntPtr.Zero) Marshal.Release(targetFolderObj);
                        }
                    }
                    finally
                    {
                        Marshal.Release(parentFolderObj);
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(absolutePidl);
                }
                return null;
            }

            private static IntPtr MenuWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                const int WM_TIMER = 0x0113;
                if (msg == WM_TIMER && (uint)wParam == 1)
                {
                    EndMenu();
                    handled = true;
                }
                return IntPtr.Zero;
            }

            // Low-level mouse hook to detect clicks on the overlay (WS_EX_NOACTIVATE/TRANSPARENT)
            private static IntPtr _mouseHookHandle = IntPtr.Zero;
            private static LowLevelMouseProcDelegate? _mouseProc;

            private delegate IntPtr LowLevelMouseProcDelegate(int nCode, IntPtr wParam, IntPtr lParam);

            private static IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam)
            {
                if (nCode >= 0)
                {
                    uint msg = (uint)wParam;
                    if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN)
                    {
                        var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        var pt = new POINT { X = hookStruct.pt.X, Y = hookStruct.pt.Y };
                        IntPtr hWnd = WindowFromPoint(pt);

                        // Only dismiss if click is NOT on the popup menu (#32768 class)
                        char[] clsBuf = new char[256];
                        int len = GetClassName(hWnd, clsBuf, clsBuf.Length);
                        string cls = len > 0 ? new string(clsBuf, 0, len) : "";

                        if (cls != "#32768")
                        {
                            EndMenu();
                        }
                    }
                }
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            private const uint CMF_NORMAL = 0x00000000;
            private const uint TPM_RETURNCMD = 0x0100;
            private const uint TPM_RIGHTBUTTON = 0x0002;
            private const uint CMIC_MASK_UNICODE = 0x00004000;
            private const int SW_SHOWNORMAL = 1;

            [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
            private static extern int SHParseDisplayName([MarshalAs(UnmanagedType.LPWStr)] string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

            [DllImport("shell32.dll")]
            private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

            [DllImport("user32.dll")]
            private static extern IntPtr CreatePopupMenu();

            [DllImport("user32.dll")]
            private static extern bool DestroyMenu(IntPtr hMenu);

            [DllImport("user32.dll")]
            private static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

            [DllImport("user32.dll")]
            private static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            private static extern IntPtr SetTimer(IntPtr hWnd, uint nIDEvent, uint uElapse, IntPtr lpTimerFunc);

            [DllImport("user32.dll")]
            private static extern bool KillTimer(IntPtr hWnd, uint uIDEvent);

            [DllImport("user32.dll")]
            private static extern bool EndMenu();

            [DllImport("user32.dll")]
            private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProcDelegate lpfn, IntPtr hmod, uint dwThreadId);

            [DllImport("user32.dll")]
            private static extern bool UnhookWindowsHookEx(IntPtr hhk);

            [DllImport("user32.dll")]
            private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

            [DllImport("user32.dll")]
            private static extern IntPtr WindowFromPoint(POINT pt);

            private const int WH_MOUSE_LL = 14;
            private const uint WM_LBUTTONDOWN = 0x0201;
            private const uint WM_RBUTTONDOWN = 0x0204;

            [StructLayout(LayoutKind.Sequential)]
            private struct MSLLHOOKSTRUCT
            {
                public POINT pt;
                public uint mouseData;
                public uint flags;
                public uint time;
                public IntPtr dwExtraInfo;
            }

            [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E6-0000-0000-C000-000000000046")]
            private interface IShellFolder
            {
                [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
                [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
                [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
                [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
                [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
                [PreserveSig] int CreateViewObject(IntPtr hwnd, ref Guid riid, out IntPtr ppv);
                [PreserveSig] int GetAttributesOf(uint cidl, [In] ref IntPtr apidl, ref uint rgfInOut);
                [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint cidl, [In] ref IntPtr apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
                [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pstr);
                [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
            }

            [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E4-0000-0000-C000-000000000046")]
            private interface IContextMenu
            {
                [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint iMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
                [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
                [PreserveSig] int GetCommandString(IntPtr pCmd, uint uType, [MarshalAs(UnmanagedType.LPArray)] [Out] int[] pReserved, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder? pszName, int cchMax);
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct POINT
            {
                public int X;
                public int Y;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct CMINVOKECOMMANDINFOEX
            {
                public int cbSize;
                public uint fMask;
                public IntPtr hwnd;
                public IntPtr lpVerb;
                [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
                [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
                public int nShow;
                public int dwHotKey;
                public IntPtr hIcon;
                [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
                public IntPtr lpVerbW;
                [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
                [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
                [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
                public POINT ptInvoke;
                public int dwHotKey2;
            }
        }

        #endregion

        public void SetResizeHandleVisibility(bool visible)
        {
            // Keep ResizeHandleGrid always visible so that the transparent hit-test areas
            // remain active for resizing even when the visual gripper is hidden.
            ResizeHandleGrid.Visibility = Visibility.Visible;
        }
        private void AddSvgButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                e.Handled = true;

                EnableWindowActivation();
                var editWindow = new SvgButtonEditWindow();
                editWindow.Topmost = true;
                try
                {
                    var owner = System.Windows.Application.Current.Windows.OfType<Window>()
                        .FirstOrDefault(w => w.IsVisible && !(w is DesktopOverlayWindow) && !(w is ContainerWindow));
                    if (owner != null)
                    {
                        editWindow.Owner = owner;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Owner error: {ex.Message}");
                }

                bool? dialogResult = false;
                try
                {
                    dialogResult = editWindow.ShowDialog();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ShowDialog error: {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    DisableWindowActivation();
                }

                if (dialogResult == true)
                {
                    var newItem = new ShortcutItem
                    {
                        Name = editWindow.ButtonName,
                        Arguments = editWindow.TargetArguments,
                        Hotkey = editWindow.Hotkey,
                        SvgContent = editWindow.SvgContent
                    };

                    if (editWindow.TargetPath.StartsWith("http://") || editWindow.TargetPath.StartsWith("https://"))
                    {
                        newItem.IsUrl = true;
                        newItem.UrlTarget = editWindow.TargetPath;
                    }
                    else
                    {
                        newItem.TargetPath = editWindow.TargetPath;
                    }

                    _vm.Shortcuts.Add(newItem);
                    _vm.Save();

                    var overlay = Window.GetWindow(this) as DesktopOverlayWindow;
                    overlay?.RefreshGlobalHotkeys();
                }
            }
        }
    }
}
