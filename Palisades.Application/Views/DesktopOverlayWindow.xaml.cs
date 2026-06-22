using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Palisades.Converters;
using Palisades.Models;
using Palisades.Plugins;
using Palisades.Services;
using Palisades.ViewModels;
using Palisades.Views.Controls;

namespace Palisades.Views
{
    public partial class DesktopOverlayWindow : Window
    {
        private MainViewModel? _mainViewModel;
        private readonly Dictionary<string, ContainerControl> _containerControls = new();
        private readonly Dictionary<string, Border> _iconElements = new();
        private readonly Dictionary<Guid, NoteControl> _noteControls = new();
        private readonly Dictionary<Guid, PluginGadgetWrapper> _gadgetControls = new();
        private readonly HashSet<ShortcutItem> _selectedIcons = new();
        private HwndSource? _hwndSource;
        private bool _windowReady;
        private IntPtr _overlayHwnd;
        internal int OverlayOffsetX { get; private set; }
        internal int OverlayOffsetY { get; private set; }
        private IntPtr _desktopHwnd;

        private bool _isDragging;
        private bool _isContainerDrag;
        private List<ShortcutItem>? _containerDragItems;
        private ContainerViewModel? _containerDragSource;
        private bool _isRectSelecting;
        private Point _mouseDownPoint;
        private Rectangle? _selectRect;
        private Border? _drawMenuPopup;
        private double _dpiScaleX = 1.0;
        private double _dpiScaleY = 1.0;

        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOACTIVATE = 0x0010;
        private const int SWP_SHOWWINDOW = 0x0040;
        private const int SW_RESTORE = 9;

        private static readonly IntPtr HWND_BOTTOM = (IntPtr)1;
        private static readonly IntPtr HWND_NOTOPMOST = (IntPtr)(-2);
        private const int SWP_NOZORDER = 0x0004;

        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MOUSEMOVE = 0x0200;

        private IntPtr _hookId = IntPtr.Zero;
        private LowLevelMouseProcDelegate? _hookProc;
        private bool _isContextMenuOpen;
        private ContextMenu? _currentContextMenu;
        private readonly System.Text.StringBuilder _classNameBuf = new(256);
        private DateTime _lastClickTime;
        private ShortcutItem? _lastClickItem;

        private delegate IntPtr LowLevelMouseProcDelegate(int nCode, IntPtr wParam, IntPtr lParam);

        public event Action<double, double, double, double, SelectedContainerType>? CreateContainerRequested;
        public event Action<double, double, double, double, List<ShortcutItem>>? CreateContainerWithIconsRequested;
        public event Action<double, double>? CreateFolderPortalRequested;

        private DispatcherTimer? _explorerCheckTimer;

        private PathToImageConverter _iconConverter = new() { ShowArrow = true };
        private static readonly Brush _invisibleBrush = new SolidColorBrush(Color.FromArgb(0x01, 0xFF, 0xFF, 0xFF));
        private static readonly Brush _selectionBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));

        private const double GridCellWidth = 88;
        private const double GridCellHeight = 96;
        private const double GridOriginX = 12;
        private const double GridOriginY = 12;

        public DesktopOverlayWindow()
        {
            InitializeComponent();
            OverlayCanvas.Background = new SolidColorBrush(Color.FromArgb(0x01, 0xFF, 0xFF, 0xFF));
            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
            Unloaded += (_, _) =>
            {
                _explorerCheckTimer?.Stop();
                UninstallHook();
            };

            SnapshotManager.ScreenshotCaptureCallback = CaptureOverlayScreenshot;
        }

        private string? CaptureOverlayScreenshot()
        {
            try
            {
                string tempPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "Palisades", $"overlay_{Guid.NewGuid()}.png");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(tempPath)!);

                double w = OverlayCanvas.ActualWidth;
                double h = OverlayCanvas.ActualHeight;
                if (w <= 0 || h <= 0) return null;

                int renderW = (int)(w * _dpiScaleX);
                int renderH = (int)(h * _dpiScaleY);

                var renderTarget = new RenderTargetBitmap(renderW, renderH,
                    96.0 * _dpiScaleX, 96.0 * _dpiScaleY, PixelFormats.Pbgra32);
                renderTarget.Render(OverlayCanvas);

                var drawingVisual = new DrawingVisual();
                using (var ctx = drawingVisual.RenderOpen())
                {
                    ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 25, 32)), null,
                        new Rect(0, 0, renderW, renderH));
                    ctx.DrawImage(renderTarget, new Rect(0, 0, renderW, renderH));
                }

                var fullBitmap = new RenderTargetBitmap(renderW, renderH, 96, 96, PixelFormats.Pbgra32);
                fullBitmap.Render(drawingVisual);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(fullBitmap));

                using var stream = System.IO.File.OpenWrite(tempPath);
                encoder.Save(stream);

                return tempPath;
            }
            catch { return null; }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            PositionOverlay();
            ContainerManager.Instance.RefreshUnassignedShortcuts();
            RebuildDesktopIcons();
            ContainerManager.Instance.UnassignedShortcutsChanged += RebuildDesktopIcons;
            RebuildNotes();
            var noteSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            noteSaveTimer.Tick += (_, _) => { SaveNotesToDisk(); SaveGadgetsToDisk(); };
            noteSaveTimer.Start();

            InstallHook();
            this.ContextMenuOpening += (_, e) =>
            {
                if (e.Source is not ContainerControl)
                    e.Handled = true;
            };
            OverlayCanvas.AllowDrop = true;
            OverlayCanvas.DragOver += OverlayCanvas_DragOver;
            OverlayCanvas.Drop += OverlayCanvas_Drop;
            this.AllowDrop = true;
            this.DragOver += OverlayCanvas_DragOver;
            this.Drop += OverlayCanvas_Drop;
        }

        private void OverlayCanvas_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(ShortcutItem)))
                e.Effects = DragDropEffects.Move;
            else
                e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void OverlayCanvas_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(ShortcutItem)) is ShortcutItem item)
            {
                var pt = e.GetPosition(OverlayCanvas);
                bool overContainer = IsOverContainer(pt);
                if (!overContainer)
                {
                    var srcContainer = ShortcutReorderHandler.FindContainerForShortcut(item);
                    if (srcContainer != null)
                    {
                        srcContainer.Shortcuts.Remove(item);
                        ContainerManager.Instance.ReturnToUnassigned(item);
                        srcContainer.Save();
                    }
                }
                e.Handled = true;
            }
        }

        private void RebuildDesktopIcons()
        {
            foreach (var kvp in _iconElements)
                OverlayCanvas.Children.Remove(kvp.Value);
            _iconElements.Clear();

            foreach (var item in ContainerManager.Instance.UnassignedShortcuts)
                AddIconElement(item);
        }

        private void AddIconElement(ShortcutItem item)
        {
            var img = new Image
            {
                Width = 48,
                Height = 48,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var source = _iconConverter.Convert(
                item.IconPath ?? item.TargetPath,
                typeof(BitmapSource), item.ShortcutPath, null) as BitmapSource;
            img.Source = source;

            var label = new TextBlock
            {
                Text = item.DisplayName,
                FontSize = 11,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 32,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var stack = new StackPanel();
            stack.Children.Add(img);
            stack.Children.Add(label);

            var border = new Border
            {
                Width = 80,
                Height = 90,
                Padding = new Thickness(4),
                Background = _invisibleBrush,
                Child = stack,
                Tag = item,
                ToolTip = item.TargetPath,
                Cursor = Cursors.Arrow
            };

            string key = item.ShortcutPath ?? item.TargetPath ?? item.Name;
            _iconElements[key] = border;
            OverlayCanvas.Children.Add(border);

            var pos = ContainerManager.Instance.GetDesktopIconPosition(key);
            if (pos.HasValue)
            {
                Canvas.SetLeft(border, pos.Value.X);
                Canvas.SetTop(border, pos.Value.Y);
            }
            else
            {
                int idx = _iconElements.Count - 1;
                int rows = Math.Max(1, (int)((Height - 12) / 96));
                int col = idx / rows;
                int row = idx % rows;
                double x = 12 + col * 88;
                double y = 12 + row * 96;
                Canvas.SetLeft(border, x);
                Canvas.SetTop(border, y);
            }
        }

        private void UpdateSelectionVisual()
        {
            foreach (var kvp in _iconElements)
            {
                var item = kvp.Value.Tag as ShortcutItem;
                bool sel = item != null && _selectedIcons.Contains(item);
                kvp.Value.Background = sel
                    ? _selectionBrush
                    : _invisibleBrush;
            }
        }

        private ShortcutItem? GetItemFromElement(DependencyObject? element)
        {
            while (element != null)
            {
                if (element is Border { Tag: ShortcutItem item })
                    return item;
                if (element is FrameworkElement fe && fe.DataContext is ShortcutItem di)
                    return di;
                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }

        private Border? GetElementForItem(ShortcutItem item)
        {
            string key = item.ShortcutPath ?? item.TargetPath ?? item.Name;
            return _iconElements.TryGetValue(key, out var b) ? b : null;
        }

        private static double SnapToGrid(double val, double gridSize, double origin)
        {
            return Math.Round((val - origin) / gridSize) * gridSize + origin;
        }

        private static void LaunchItem(ShortcutItem item)
        {
            try
            {
                if (!string.IsNullOrEmpty(item.TargetPath))
                {
                    if (item.IsUrl && !string.IsNullOrEmpty(item.UrlTarget))
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = item.UrlTarget,
                            UseShellExecute = true
                        };
                        System.Diagnostics.Process.Start(psi);
                    }
                    else
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = item.TargetPath,
                            Arguments = item.Arguments ?? "",
                            WorkingDirectory = item.WorkingDirectory ?? "",
                            UseShellExecute = true
                        };
                        System.Diagnostics.Process.Start(psi);
                    }
                }
            }
            catch { }
        }

        // === Mouse hook callback ===

        private IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && !_isContextMenuOpen)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                double dpiX = _dpiScaleX > 0 ? _dpiScaleX : 1.0;
                double dpiY = _dpiScaleY > 0 ? _dpiScaleY : 1.0;
                var canvasPt = new Point(
                    hookStruct.pt.X / dpiX - Left,
                    hookStruct.pt.Y / dpiY - Top);

                bool onOverlay = canvasPt.X >= 0 && canvasPt.X <= Width
                    && canvasPt.Y >= 0 && canvasPt.Y <= Height;

                if (onOverlay || _isDragging || _isRectSelecting)
                {
                    int msg = wParam.ToInt32();
                    var hitItem = onOverlay ? HitTestIcon(canvasPt) : null;
                    bool overContainer = onOverlay && IsOverContainer(canvasPt);
                    bool overNote = onOverlay && IsOverNote(canvasPt);
                    bool overGadget = onOverlay && IsOverGadget(canvasPt);

                    switch (msg)
                    {
                        case WM_LBUTTONDOWN:
                            if (!IsDesktopPoint(hookStruct.pt))
                            {
                                CancelDragOrRectSelect();
                                break;
                            }
                            if (overContainer || overNote || overGadget || IsOverDrawMenu(canvasPt))
                            {
                                if (IsOverDrawMenu(canvasPt))
                                {
                                    break;
                                }
                                CancelDragOrRectSelect();
                                break;
                            }
                            if (hitItem != null && hitItem == _lastClickItem
                                && (DateTime.Now - _lastClickTime).TotalMilliseconds < 200)
                            {
                                _lastClickItem = null;
                                LaunchItem(hitItem);
                                return (IntPtr)1;
                            }
                            _lastClickTime = DateTime.Now;
                            _lastClickItem = hitItem;
                            HandleLeftButtonDown(canvasPt, hitItem);
                            return (IntPtr)1;

                        case WM_RBUTTONDOWN:
                            if (!IsDesktopPoint(hookStruct.pt))
                                break;
                            if (overContainer || overNote || overGadget)
                            {
                                CancelDragOrRectSelect();
                                break;
                            }
                            HandleRightButtonDown(canvasPt, hitItem);
                            return (IntPtr)1;

                        case WM_MOUSEMOVE:
                            if (_isContainerDrag || _isDragging)
                            {
                                foreach (var kvp in _containerControls)
                                {
                                    double left = Canvas.GetLeft(kvp.Value);
                                    double top = Canvas.GetTop(kvp.Value);
                                    double w = double.IsNaN(kvp.Value.Width) ? kvp.Value.ActualWidth : kvp.Value.Width;
                                    double h = double.IsNaN(kvp.Value.Height) ? kvp.Value.ActualHeight : kvp.Value.Height;
                                    if (canvasPt.X >= left && canvasPt.X <= left + w &&
                                        canvasPt.Y >= top && canvasPt.Y <= top + h)
                                    {
                                        if (_isContainerDrag)
                                            kvp.Value.UpdateInsertionMarker(canvasPt);
                                        else
                                            kvp.Value.UpdateInsertionMarker(canvasPt);
                                        break;
                                    }
                                }
                                if (_isDragging)
                                    HandleDragMove(canvasPt);
                            }
                            else if (_isRectSelecting)
                                HandleRectSelectMove(canvasPt);
                            break;

                        case WM_LBUTTONUP:
                            if (_isContainerDrag)
                            {
                                FinishContainerDrag(canvasPt);
                                break;
                            }
                            if (_isDragging)
                            {
                                HandleDragEnd();
                                return (IntPtr)1;
                            }
                            if (_isRectSelecting)
                            {
                                HandleRectSelectEnd(canvasPt);
                                return (IntPtr)1;
                            }
                            break;

                        case WM_LBUTTONDBLCLK:
                            if (!IsDesktopPoint(hookStruct.pt))
                                break;
                            if (hitItem != null && !overContainer)
                            {
                                LaunchItem(hitItem);
                                return (IntPtr)1;
                            }
                            break;
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // === Hook helper methods ===

        private void CancelDragOrRectSelect()
        {
            foreach (var kvp in _containerControls)
                kvp.Value.ClearInsertionMarker();
            if (_isDragging)
                _isDragging = false;
            if (_isRectSelecting)
            {
                _isRectSelecting = false;
                if (_selectRect != null)
                {
                    OverlayCanvas.Children.Remove(_selectRect);
                    _selectRect = null;
                }
                UpdateSelectionVisual();
            }
            CancelDrawMenu();
        }

        private void HandleLeftButtonDown(Point canvasPt, ShortcutItem? hitItem)
        {
            _isRectSelecting = false;
            CancelDrawMenu();

            if (hitItem != null)
            {
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

                if (ctrl)
                {
                    if (_selectedIcons.Contains(hitItem))
                        _selectedIcons.Remove(hitItem);
                    else
                        _selectedIcons.Add(hitItem);
                    UpdateSelectionVisual();
                    return;
                }

                if (shift && _selectedIcons.Count > 0)
                {
                    var ordered = _iconElements.Values.ToList();
                    var el = GetElementForItem(hitItem);
                    int lastIdx = el != null ? ordered.IndexOf(el) : -1;
                    int firstSel = -1;
                    foreach (var kvp in _iconElements)
                    {
                        if (kvp.Value.Tag is ShortcutItem si && _selectedIcons.Contains(si))
                        {
                            int idx = ordered.IndexOf(kvp.Value);
                            if (firstSel < 0 || idx < firstSel) firstSel = idx;
                        }
                    }
                    if (firstSel >= 0 && lastIdx >= 0)
                    {
                        int min = Math.Min(firstSel, lastIdx);
                        int max = Math.Max(firstSel, lastIdx);
                        for (int i = min; i <= max; i++)
                        {
                            if (ordered[i].Tag is ShortcutItem si)
                                _selectedIcons.Add(si);
                        }
                    }
                    UpdateSelectionVisual();
                    return;
                }

                if (!_selectedIcons.Contains(hitItem))
                {
                    _selectedIcons.Clear();
                    _selectedIcons.Add(hitItem);
                    UpdateSelectionVisual();
                }
                _isDragging = true;
                _mouseDownPoint = canvasPt;
                return;
            }

            // Empty area → rectangle selection
            _isRectSelecting = true;
            _mouseDownPoint = canvasPt;
            _selectRect = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                StrokeDashArray = new DoubleCollection { 4, 2 }
            };
            Canvas.SetLeft(_selectRect, canvasPt.X);
            Canvas.SetTop(_selectRect, canvasPt.Y);
            _selectRect.Width = 0;
            _selectRect.Height = 0;
            OverlayCanvas.Children.Add(_selectRect);
        }

        private void HandleRightButtonDown(Point canvasPt, ShortcutItem? hitItem)
        {
            if (_isDragging)
                _isDragging = false;
            if (_isRectSelecting)
            {
                _isRectSelecting = false;
                if (_selectRect != null)
                {
                    OverlayCanvas.Children.Remove(_selectRect);
                    _selectRect = null;
                }
                UpdateSelectionVisual();
            }

            if (hitItem != null)
            {
                if (!_selectedIcons.Contains(hitItem))
                {
                    _selectedIcons.Clear();
                    _selectedIcons.Add(hitItem);
                    UpdateSelectionVisual();
                }

                string menuPath = !string.IsNullOrEmpty(hitItem.ShortcutPath)
                    ? hitItem.ShortcutPath
                    : hitItem.TargetPath;

                double screenX = (Left + canvasPt.X) * _dpiScaleX;
                double screenY = (Top + canvasPt.Y) * _dpiScaleY;
                _isContextMenuOpen = true;
                ContainerControl.ShellContextMenu.ShowMenu(
                    _overlayHwnd, menuPath, (int)screenX, (int)screenY);
                _isContextMenuOpen = false;
                ContainerManager.Instance.SyncDeletedShortcuts();
                return;
            }

            ShowDesktopContextMenu(canvasPt);
        }

        private void HandleDragMove(Point canvasPt)
        {
            double dx = canvasPt.X - _mouseDownPoint.X;
            double dy = canvasPt.Y - _mouseDownPoint.Y;

            foreach (var sel in _selectedIcons)
            {
                var el = GetElementForItem(sel);
                if (el == null) continue;
                double left = Canvas.GetLeft(el) + dx;
                double top = Canvas.GetTop(el) + dy;
                Canvas.SetLeft(el, left);
                Canvas.SetTop(el, top);
            }

            _mouseDownPoint = canvasPt;
        }

        private void HandleDragEnd()
        {
            _isDragging = false;
            foreach (var kvp in _containerControls)
                kvp.Value.ClearInsertionMarker();

            double avgX = 0, avgY = 0;
            foreach (var sel in _selectedIcons)
            {
                var el = GetElementForItem(sel);
                if (el == null) continue;
                avgX += Canvas.GetLeft(el) + el.Width / 2;
                avgY += Canvas.GetTop(el) + el.Height / 2;
            }
            if (_selectedIcons.Count > 0)
            {
                avgX /= _selectedIcons.Count;
                avgY /= _selectedIcons.Count;
                var targetCtrl = GetContainerAt(new Point(avgX, avgY));
                if (targetCtrl != null && targetCtrl.DataContext is ContainerViewModel vm)
                {
                    var itemsToMove = _selectedIcons.ToList();
                    _selectedIcons.Clear();
                    ContainerManager.Instance.MoveAllToContainer(itemsToMove, vm.Model);
                    return;
                }
            }

            foreach (var sel in _selectedIcons)
            {
                var el = GetElementForItem(sel);
                if (el == null) continue;
                double snappedX = SnapToGrid(Canvas.GetLeft(el), GridCellWidth, GridOriginX);
                double snappedY = SnapToGrid(Canvas.GetTop(el), GridCellHeight, GridOriginY);
                Canvas.SetLeft(el, snappedX);
                Canvas.SetTop(el, snappedY);
            }

            ResolveCollisions();
            SaveAllPositions();
        }

        private void HandleRectSelectMove(Point canvasPt)
        {
            if (_selectRect == null) return;

            double x = Math.Min(_mouseDownPoint.X, canvasPt.X);
            double y = Math.Min(_mouseDownPoint.Y, canvasPt.Y);
            double w = Math.Abs(canvasPt.X - _mouseDownPoint.X);
            double h = Math.Abs(canvasPt.Y - _mouseDownPoint.Y);
            Canvas.SetLeft(_selectRect, x);
            Canvas.SetTop(_selectRect, y);
            _selectRect.Width = w;
            _selectRect.Height = h;

            if (w > 5 || h > 5)
            {
                var previewRect = new Rect(x, y, w, h);
                foreach (var kvp in _iconElements)
                {
                    double lx = Canvas.GetLeft(kvp.Value);
                    double ly = Canvas.GetTop(kvp.Value);
                    var itemRect = new Rect(lx, ly, kvp.Value.Width, kvp.Value.Height);
                    kvp.Value.Background = previewRect.IntersectsWith(itemRect)
                        ? _selectionBrush
                        : _invisibleBrush;
                }
            }
            else
            {
                foreach (var kvp in _iconElements)
                    kvp.Value.Background = _invisibleBrush;
            }
        }

        private void HandleRectSelectEnd(Point canvasPt)
        {
            _isRectSelecting = false;

            if (_selectRect != null)
            {
                double x = Math.Min(_mouseDownPoint.X, canvasPt.X);
                double y = Math.Min(_mouseDownPoint.Y, canvasPt.Y);
                double w = Math.Abs(canvasPt.X - _mouseDownPoint.X);
                double h = Math.Abs(canvasPt.Y - _mouseDownPoint.Y);
                var selRect = new Rect(x, y, w, h);

                if (w < 5 && h < 5)
                {
                    OverlayCanvas.Children.Remove(_selectRect);
                    _selectRect = null;
                    _selectedIcons.Clear();
                    UpdateSelectionVisual();
                    return;
                }

                _selectedIcons.Clear();
                foreach (var kvp in _iconElements)
                {
                    double lx = Canvas.GetLeft(kvp.Value);
                    double ly = Canvas.GetTop(kvp.Value);
                    var itemRect = new Rect(lx, ly, kvp.Value.Width, kvp.Value.Height);
                    if (selRect.IntersectsWith(itemRect) && kvp.Value.Tag is ShortcutItem si)
                        _selectedIcons.Add(si);
                }
                UpdateSelectionVisual();

                // Show custom popup menu at mouse coordinates and keep selection rect visible
                if (_selectedIcons.Count == 0 && w >= 50 && h >= 50)
                {
                    ShowDrawToCreateMenu(x + OverlayOffsetX, y + OverlayOffsetY, w, h, canvasPt);
                }
                else
                {
                    OverlayCanvas.Children.Remove(_selectRect);
                    _selectRect = null;
                }
            }
        }

        private void ResolveCollisions()
        {
            var sorted = _iconElements.Values
                .OrderBy(el => Canvas.GetTop(el))
                .ThenBy(el => Canvas.GetLeft(el))
                .ToList();

            var used = new HashSet<(int col, int row)>();

            foreach (var el in sorted)
            {
                double left = Canvas.GetLeft(el);
                double top = Canvas.GetTop(el);
                int col = (int)Math.Round((left - GridOriginX) / GridCellWidth);
                int row = (int)Math.Round((top - GridOriginY) / GridCellHeight);

                while (!used.Add((col, row)))
                {
                    col++;
                    if (GridOriginX + col * GridCellWidth > Width)
                    {
                        col = 0;
                        row++;
                    }
                }

                Canvas.SetLeft(el, GridOriginX + col * GridCellWidth);
                Canvas.SetTop(el, GridOriginY + row * GridCellHeight);
            }
        }

        private void SaveAllPositions()
        {
            foreach (var kvp in _iconElements)
                ContainerManager.Instance.SetDesktopIconPosition(kvp.Key,
                    Canvas.GetLeft(kvp.Value), Canvas.GetTop(kvp.Value));
        }

        #region Container drag support

        public void StartContainerDrag(List<ShortcutItem> items, ContainerViewModel source)
        {
            _containerDragItems = items;
            _containerDragSource = source;
            _isContainerDrag = true;
        }

        private void FinishContainerDrag(Point canvasPt)
        {
            _isContainerDrag = false;
            foreach (var kvp in _containerControls)
                kvp.Value.ClearInsertionMarker();
            if (_containerDragSource != null && _containerControls.TryGetValue(_containerDragSource.Identifier, out var srcCtrl))
                srcCtrl.ResetDragState();
            if (_containerDragItems == null || _containerDragItems.Count == 0) return;

            double sx = (canvasPt.X + OverlayOffsetX) * _dpiScaleX;
            double sy = (canvasPt.Y + OverlayOffsetY) * _dpiScaleY;
            var screenPt = new POINT { X = (int)sx, Y = (int)sy };

            string? targetIdentifier = null;
            ContainerViewModel? targetVM = null;
            foreach (var kvp in _containerControls)
            {
                double left = Canvas.GetLeft(kvp.Value);
                double top = Canvas.GetTop(kvp.Value);
                double w = double.IsNaN(kvp.Value.Width) ? kvp.Value.ActualWidth : kvp.Value.Width;
                double h = double.IsNaN(kvp.Value.Height) ? kvp.Value.ActualHeight : kvp.Value.Height;
                if (canvasPt.X >= left && canvasPt.X <= left + w &&
                    canvasPt.Y >= top && canvasPt.Y <= top + h &&
                    kvp.Value.DataContext is ContainerViewModel vm)
                {
                    targetIdentifier = vm.Identifier;
                    targetVM = vm;
                    break;
                }
            }

            var items = _containerDragItems.ToList();
            var srcVM = _containerDragSource;
            _containerDragItems = null;
            _containerDragSource = null;

            if (targetVM != null && targetVM == srcVM)
            {
                // Same container → reorder
                foreach (var kvp in _containerControls)
                {
                    if (kvp.Value.DataContext is ContainerViewModel vm && vm == targetVM)
                    {
                        kvp.Value.ReorderAtCanvasPoint(canvasPt, items);
                        break;
                    }
                }
            }
            else if (targetVM != null && srcVM != null)
            {
                // Different container → move
                foreach (var item in items)
                {
                    srcVM.Shortcuts.Remove(item);
                    targetVM.Shortcuts.Add(item);
                }
                srcVM.Save();
                targetVM.Save();
            }
            else if (srcVM != null)
            {
                // Desktop background → return to unassigned
                foreach (var item in items)
                {
                    srcVM.Shortcuts.Remove(item);
                    ContainerManager.Instance.ReturnToUnassigned(item);
                }
                srcVM.Save();
            }
        }

        #endregion

        #region Note management

        public void AddNote(NoteItem note)
        {
            if (note == null || _noteControls.ContainsKey(note.Id)) return;

            var ctrl = new NoteControl(note);
            _noteControls[note.Id] = ctrl;
            OverlayCanvas.Children.Add(ctrl);
            Canvas.SetLeft(ctrl, note.X);
            Canvas.SetTop(ctrl, note.Y);
            Canvas.SetZIndex(ctrl, 100);
        }

        public void RemoveNote(NoteItem note)
        {
            if (note == null) return;
            if (_noteControls.TryGetValue(note.Id, out var ctrl))
            {
                OverlayCanvas.Children.Remove(ctrl);
                _noteControls.Remove(note.Id);
            }
            var notes = ContainerManager.Instance.LoadNotes();
            notes.RemoveAll(n => n.Id == note.Id);
            ContainerManager.Instance.SaveNotes(notes);
        }

        public List<NoteItem> GetNotes()
        {
            return _noteControls.Values
                .Select(c => c.Note)
                .OrderBy(n => n.X).ThenBy(n => n.Y)
                .ToList();
        }

        public void SaveNotesToDisk()
        {
            try
            {
                var allNotes = GetNotes();
                if (allNotes.Count > 0)
                    ContainerManager.Instance.SaveNotes(allNotes);
            }
            catch { }
        }

        public void SetShortcutArrow(bool show)
        {
            _iconConverter.ShowArrow = show;
            PathToImageConverter.ClearCache();
            RebuildDesktopIcons();
        }

        public void SetResizeHandle(bool show)
        {
            foreach (var kvp in _containerControls)
                kvp.Value.SetResizeHandleVisibility(show);
        }

        public void RebuildNotes()
        {
            foreach (var kvp in _noteControls)
                OverlayCanvas.Children.Remove(kvp.Value);
            _noteControls.Clear();

            var notes = ContainerManager.Instance.LoadNotes();
            foreach (var note in notes)
                AddNote(note);
        }

        #endregion

        #region Gadget management

        public void InitializePluginService(MainViewModel vm)
        {
            _mainViewModel = vm;
            PluginService.Instance.Initialize(vm, this);
            PluginService.Instance.PluginsChanged += RebuildGadgets;
            PluginService.Instance.GadgetsChanged += SyncGadgets;
            RebuildGadgets();
        }

        public void SyncGadgets()
        {
            var list = PluginService.Instance.LoadGadgets();

            // 1. Remove controls for gadgets that are no longer active
            var toRemove = _gadgetControls.Keys.Where(id => !list.Any(g => g.Id == id)).ToList();
            foreach (var id in toRemove)
            {
                if (_gadgetControls.TryGetValue(id, out var wrapper))
                {
                    OverlayCanvas.Children.Remove(wrapper);
                    _gadgetControls.Remove(id);
                }
            }

            // 2. Add or update controls
            foreach (var item in list)
            {
                if (!_gadgetControls.TryGetValue(item.Id, out var wrapper))
                {
                    AddGadgetControl(item);
                }
                else
                {
                    var existing = wrapper.GadgetItem;
                    
                    existing.Title = item.Title;
                    existing.X = item.X;
                    existing.Y = item.Y;
                    existing.Width = item.Width;
                    existing.Height = item.Height;
                    existing.HideHeader = item.HideHeader;
                    existing.CustomData = item.CustomData;
                    existing.Opacity = item.Opacity;
                    existing.MarginLeft = item.MarginLeft;
                    existing.MarginTop = item.MarginTop;
                    existing.MarginRight = item.MarginRight;
                    existing.MarginBottom = item.MarginBottom;
                    existing.PaddingLeft = item.PaddingLeft;
                    existing.PaddingTop = item.PaddingTop;
                    existing.PaddingRight = item.PaddingRight;
                    existing.PaddingBottom = item.PaddingBottom;
                    existing.BgColor = item.BgColor;
                    existing.BgOpacity = item.BgOpacity;
                    existing.BorderColor = item.BorderColor;
                    existing.BorderThicknessValue = item.BorderThicknessValue;
                    existing.CornerRadiusValue = item.CornerRadiusValue;
                    existing.HeaderBgColor = item.HeaderBgColor;
                    existing.HeaderBorderColor = item.HeaderBorderColor;
                    existing.TitleColor = item.TitleColor;
                    existing.TitleFontSize = item.TitleFontSize;

                    if (wrapper.Width != item.Width) wrapper.Width = item.Width;
                    if (wrapper.Height != item.Height) wrapper.Height = item.Height;
                    if (Canvas.GetLeft(wrapper) != item.X) Canvas.SetLeft(wrapper, item.X);
                    if (Canvas.GetTop(wrapper) != item.Y) Canvas.SetTop(wrapper, item.Y);
                }
            }
        }

        public void AddGadgetControl(PluginGadgetItem item)
        {
            if (item == null || _gadgetControls.ContainsKey(item.Id)) return;

            var gadgetReg = PluginService.Instance.Plugins
                .Where(p => p.IsEnabled && p.Context != null)
                .SelectMany(p => p.Context.Gadgets)
                .FirstOrDefault(g => g.GadgetType == item.GadgetType);

            if (gadgetReg == null) return;

            try
            {
                var childView = gadgetReg.ViewFactory();
                var wrapper = new PluginGadgetWrapper(item, childView);
                _gadgetControls[item.Id] = wrapper;
                OverlayCanvas.Children.Add(wrapper);
                Canvas.SetZIndex(wrapper, 99);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DesktopOverlayWindow] Error instantiating gadget {item.GadgetType}: {ex.Message}");
            }
        }

        public void SpawnGadget(string pluginId, string gadgetType)
        {
            var gadgetReg = PluginService.Instance.Plugins
                .Where(p => p.Plugin.Id == pluginId && p.IsEnabled && p.Context != null)
                .SelectMany(p => p.Context.Gadgets)
                .FirstOrDefault(g => g.GadgetType == gadgetType);

            if (gadgetReg == null) return;

            var item = new PluginGadgetItem
            {
                PluginId = pluginId,
                GadgetType = gadgetType,
                Title = gadgetReg.Name,
                Width = gadgetReg.DefaultWidth,
                Height = gadgetReg.DefaultHeight,
                X = 200,
                Y = 200
            };

            var list = PluginService.Instance.LoadGadgets();
            list.Add(item);
            PluginService.Instance.SaveGadgets(list);

            AddGadgetControl(item);
        }

        public void RemoveGadget(Guid id)
        {
            if (_gadgetControls.TryGetValue(id, out var wrapper))
            {
                OverlayCanvas.Children.Remove(wrapper);
                _gadgetControls.Remove(id);
            }
            var list = PluginService.Instance.LoadGadgets();
            list.RemoveAll(g => g.Id == id);
            PluginService.Instance.SaveGadgets(list);
        }

        public List<PluginGadgetItem> GetGadgets()
        {
            return _gadgetControls.Values
                .Select(c => c.GadgetItem)
                .ToList();
        }

        public void SaveGadgetsToDisk()
        {
            try
            {
                var list = GetGadgets();
                PluginService.Instance.SaveGadgets(list);
            }
            catch { }
        }

        public void RebuildGadgets()
        {
            // Clear existing wrappers
            foreach (var wrapper in _gadgetControls.Values)
                OverlayCanvas.Children.Remove(wrapper);
            _gadgetControls.Clear();

            var gadgets = PluginService.Instance.LoadGadgets();
            foreach (var g in gadgets)
            {
                AddGadgetControl(g);
            }
        }

        #endregion

        // === Overlay code ===

        private void RefreshDesktopHwnd()
        {
            _desktopHwnd = FindWindow("Progman", null);
            if (_desktopHwnd == IntPtr.Zero)
            {
                IntPtr workerW = IntPtr.Zero;
                do
                {
                    workerW = FindWindowEx(IntPtr.Zero, workerW, "WorkerW", null);
                    if (workerW != IntPtr.Zero &&
                        FindWindowEx(workerW, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                    {
                        _desktopHwnd = workerW;
                        break;
                    }
                }
                while (workerW != IntPtr.Zero);
            }
        }

        private void PositionOverlay()
        {
            int minX = 0, minY = 0, maxX = 0, maxY = 0;
            bool first = true;

            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var wa = screen.WorkingArea;
                if (first)
                {
                    minX = wa.Left;
                    minY = wa.Top;
                    maxX = wa.Right;
                    maxY = wa.Bottom;
                    first = false;
                }
                else
                {
                    if (wa.Left < minX) minX = wa.Left;
                    if (wa.Top < minY) minY = wa.Top;
                    if (wa.Right > maxX) maxX = wa.Right;
                    if (wa.Bottom > maxY) maxY = wa.Bottom;
                }
            }

            OverlayOffsetX = minX;
            OverlayOffsetY = minY;

            Left = minX;
            Top = minY;
            Width = maxX - minX;
            Height = maxY - minY;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            if (_windowReady) return;
            _windowReady = true;

            try
            {
                _overlayHwnd = new WindowInteropHelper(this).Handle;
                _hwndSource = HwndSource.FromHwnd(_overlayHwnd);
                _hwndSource?.AddHook(WndProc);

                const int GWL_EXSTYLE = -20;
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_NOACTIVATE = 0x08000000;

                int exStyle = GetWindowLong(_overlayHwnd, GWL_EXSTYLE);
                SetWindowLong(_overlayHwnd, GWL_EXSTYLE,
                    exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

                ImmunizeAgainstWinD(_overlayHwnd);

                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget != null)
                {
                    _dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
                    _dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
                }

                var src = PresentationSource.FromVisual(this);
                if (src?.CompositionTarget != null)
                {
                    _dpiScaleX = src.CompositionTarget.TransformToDevice.M11;
                    _dpiScaleY = src.CompositionTarget.TransformToDevice.M22;
                }

                RefreshGlobalHotkeys();
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private void ImmunizeAgainstWinD(IntPtr overlayHwnd)
        {
            IntPtr desktopHwnd = FindWindow("Progman", null);
            IntPtr defView = IntPtr.Zero;
            if (desktopHwnd != IntPtr.Zero)
                defView = FindWindowEx(desktopHwnd, IntPtr.Zero, "SHELLDLL_DefView", null);

            if (defView == IntPtr.Zero)
            {
                IntPtr workerW = IntPtr.Zero;
                do
                {
                    workerW = FindWindowEx(IntPtr.Zero, workerW, "WorkerW", null);
                    if (workerW != IntPtr.Zero)
                        defView = FindWindowEx(workerW, IntPtr.Zero, "SHELLDLL_DefView", null);
                }
                while (defView == IntPtr.Zero && workerW != IntPtr.Zero);

                if (workerW != IntPtr.Zero)
                    desktopHwnd = workerW;
            }

            if (desktopHwnd != IntPtr.Zero)
            {
                if (IntPtr.Size == 8)
                    SetWindowLongPtr(overlayHwnd, GWLP_HWNDPARENT, desktopHwnd);
                else
                    SetWindowLong32(overlayHwnd, GWLP_HWNDPARENT, desktopHwnd.ToInt32());
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_WINDOWPOSCHANGING = 0x0046;
            const int WM_SHOWWINDOW = 0x0018;
            const int WM_SYSCOMMAND = 0x0112;
            const int WM_MOUSEACTIVATE = 0x0021;
            const int WM_CLOSE = 0x0010;
            const int SC_MINIMIZE = 0xF020;
            const int SC_SHOWDESKTOP = 0xF070;
            const int WM_HOTKEY = 0x0312;
            switch (msg)
            {
                case WM_HOTKEY:
                {
                    int id = wParam.ToInt32();
                    if (_registeredHotkeys.TryGetValue(id, out var item))
                    {
                        LaunchShortcut(item);
                        handled = true;
                    }
                    break;
                }

                case WM_WINDOWPOSCHANGING:
                {
                    var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                    wp.hwndInsertAfter = HWND_BOTTOM;
                    wp.flags &= ~SWP_HIDEWINDOW;
                    wp.flags &= ~SWP_NOZORDER;
                    Marshal.StructureToPtr(wp, lParam, true);
                    break;
                }

                case WM_SHOWWINDOW:
                    if (wParam == IntPtr.Zero)
                        handled = true;
                    break;

                case WM_MOUSEACTIVATE:
                    handled = true;
                    return (IntPtr)3; // MA_NOACTIVATE
            }

            return IntPtr.Zero;
        }

        private bool IsOverGadget(Point canvasPos)
        {
            foreach (var kvp in _gadgetControls.Values)
            {
                if (kvp.Visibility == Visibility.Visible)
                {
                    double left = Canvas.GetLeft(kvp);
                    double top = Canvas.GetTop(kvp);
                    double w = double.IsNaN(kvp.Width) ? kvp.ActualWidth : kvp.Width;
                    double h = double.IsNaN(kvp.Height) ? kvp.ActualHeight : kvp.Height;
                    if (canvasPos.X >= left && canvasPos.X <= left + w &&
                        canvasPos.Y >= top && canvasPos.Y <= top + h)
                        return true;
                }
            }
            return false;
        }

        private bool IsOverNote(Point canvasPos)
        {
            foreach (var kvp in _noteControls)
            {
                double left = Canvas.GetLeft(kvp.Value);
                double top = Canvas.GetTop(kvp.Value);
                double w = kvp.Value.Width;
                double h = kvp.Value.Height;
                if (canvasPos.X >= left && canvasPos.X <= left + w &&
                    canvasPos.Y >= top && canvasPos.Y <= top + h)
                    return true;
            }
            return false;
        }

        private bool IsOverContainer(Point canvasPos)
        {
            foreach (UIElement child in OverlayCanvas.Children)
            {
                if (child is ContainerControl ctrl && ctrl.Visibility == Visibility.Visible)
                {
                    double left = Canvas.GetLeft(ctrl);
                    double top = Canvas.GetTop(ctrl);
                    double w = double.IsNaN(ctrl.Width) ? ctrl.ActualWidth : ctrl.Width;
                    double h = double.IsNaN(ctrl.Height) ? ctrl.ActualHeight : ctrl.Height;
                    if (canvasPos.X >= left && canvasPos.X <= left + w &&
                        canvasPos.Y >= top && canvasPos.Y <= top + h)
                        return true;
                }
            }
            return false;
        }

        public ContainerViewModel? FindContainerAt(Point canvasPos, string? excludeIdentifier = null)
        {
            foreach (UIElement child in OverlayCanvas.Children)
            {
                if (child is ContainerControl ctrl && ctrl.Visibility == Visibility.Visible
                    && ctrl.DataContext is ContainerViewModel vm
                    && (excludeIdentifier == null || vm.Identifier != excludeIdentifier))
                {
                    double left = Canvas.GetLeft(ctrl);
                    double top = Canvas.GetTop(ctrl);
                    double w = double.IsNaN(ctrl.Width) ? ctrl.ActualWidth : ctrl.Width;
                    double h = double.IsNaN(ctrl.Height) ? ctrl.ActualHeight : ctrl.Height;
                    if (canvasPos.X >= left && canvasPos.X <= left + w &&
                        canvasPos.Y >= top && canvasPos.Y <= top + h)
                        return vm;
                }
            }
            return null;
        }

        private ContainerControl? GetContainerAt(Point canvasPos)
        {
            foreach (UIElement child in OverlayCanvas.Children)
            {
                if (child is ContainerControl ctrl && ctrl.Visibility == Visibility.Visible)
                {
                    double left = Canvas.GetLeft(ctrl);
                    double top = Canvas.GetTop(ctrl);
                    double w = double.IsNaN(ctrl.Width) ? ctrl.ActualWidth : ctrl.Width;
                    double h = double.IsNaN(ctrl.Height) ? ctrl.ActualHeight : ctrl.Height;
                    if (canvasPos.X >= left && canvasPos.X <= left + w &&
                        canvasPos.Y >= top && canvasPos.Y <= top + h)
                        return ctrl;
                }
            }
            return null;
        }

        private ShortcutItem? HitTestIcon(Point canvasPt)
        {
            foreach (var kvp in _iconElements)
            {
                double left = Canvas.GetLeft(kvp.Value);
                double top = Canvas.GetTop(kvp.Value);
                double w = kvp.Value.Width;
                double h = kvp.Value.Height;
                if (canvasPt.X >= left && canvasPt.X <= left + w &&
                    canvasPt.Y >= top && canvasPt.Y <= top + h &&
                    kvp.Value.Tag is ShortcutItem si)
                    return si;
            }
            return null;
        }

        private void ShowDesktopContextMenu(Point canvasPt)
        {
            double sx = (Left + canvasPt.X) * _dpiScaleX;
            double sy = (Top + canvasPt.Y) * _dpiScaleY;
            _isContextMenuOpen = true;
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            ContainerControl.ShellContextMenu.ShowMenu(
                _overlayHwnd, desktopPath, (int)sx, (int)sy, true);
            _isContextMenuOpen = false;
        }

        private void InstallHook()
        {
            if (_hookId != IntPtr.Zero) return;
            _hookProc = LowLevelMouseProc;
            _hookId = SetWindowsHookEx(WH_MOUSE_LL, _hookProc,
                Marshal.GetHINSTANCE(typeof(DesktopOverlayWindow).Module), 0);
        }

        private void UninstallHook()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            _hookProc = null;
        }

        public void RepositionOverlay()
        {
            PositionOverlay();
            foreach (var kvp in _containerControls)
            {
                var ctrl = kvp.Value;
                var vm = ctrl.DataContext as ContainerViewModel;
                if (vm != null)
                {
                    Canvas.SetLeft(ctrl, vm.X - OverlayOffsetX);
                    Canvas.SetTop(ctrl, vm.Y - OverlayOffsetY);
                }
            }
        }

        #region Container management

        public void AddContainer(ContainerViewModel vm)
        {
            if (vm == null || _containerControls.ContainsKey(vm.Identifier))
                return;

            try
            {
                var control = new ContainerControl(vm)
                {
                    OverlayOffsetX = OverlayOffsetX,
                    OverlayOffsetY = OverlayOffsetY
                };

                _containerControls[vm.Identifier] = control;
                OverlayCanvas.Children.Add(control);
                Canvas.SetZIndex(control, 1);

                Canvas.SetLeft(control, vm.X - OverlayOffsetX);
                Canvas.SetTop(control, vm.Y - OverlayOffsetY);

                control.Visibility = vm.IsVisible ? Visibility.Visible : Visibility.Collapsed;

                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ContainerViewModel.IsVisible))
                        control.Visibility = vm.IsVisible ? Visibility.Visible : Visibility.Collapsed;
                };

                vm.RequestClose += () => RemoveContainer(vm.Identifier);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to add container to overlay: {ex.Message}");
            }
        }

        public void RemoveContainer(string identifier)
        {
            if (_containerControls.TryGetValue(identifier, out var control))
            {
                OverlayCanvas.Children.Remove(control);
                _containerControls.Remove(identifier);
            }
        }

        public void RebuildContainers(System.Collections.Generic.IEnumerable<ContainerViewModel> viewModels)
        {
            foreach (var kvp in _containerControls)
                OverlayCanvas.Children.Remove(kvp.Value);
            _containerControls.Clear();

            foreach (var vm in viewModels)
                AddContainer(vm);
        }

        public void SetAllVisible(bool visible)
        {
            foreach (var ctrl in _containerControls.Values)
                ctrl.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion

        #region Win32 P/Invoke

        private const int SWP_HIDEWINDOW = 0x0080;
        private const int GWLP_HWNDPARENT = -8;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

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

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter,
            string lpszClass, string? lpszWindow);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProcDelegate lpfn, IntPtr hmod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT pt);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, int uFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        private bool IsDesktopPoint(POINT screenPt)
        {
            IntPtr hwnd = WindowFromPoint(screenPt);
            if (hwnd == IntPtr.Zero || hwnd == _overlayHwnd)
                return true;
            _classNameBuf.Clear();
            GetClassName(hwnd, _classNameBuf, 256);
            return _classNameBuf.ToString() is "Progman" or "WorkerW";
        }

        #region SVG Buttons Global Hotkeys Management

        private readonly System.Collections.Generic.Dictionary<int, ShortcutItem> _registeredHotkeys = new();
        private int _nextHotkeyId = 1000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public void RefreshGlobalHotkeys()
        {
            if (_overlayHwnd == IntPtr.Zero) return;

            // 1. Unregister all existing hotkeys
            foreach (int id in _registeredHotkeys.Keys)
            {
                UnregisterHotKey(_overlayHwnd, id);
            }
            _registeredHotkeys.Clear();

            // 2. Scan and register hotkeys
            foreach (var container in ContainerManager.Instance.Containers)
            {
                foreach (var item in container.Shortcuts)
                {
                    if (!string.IsNullOrEmpty(item.Hotkey))
                    {
                        var (modifiers, vk) = ParseHotkeyString(item.Hotkey);
                        if (vk != 0)
                        {
                            int id = _nextHotkeyId++;
                            if (RegisterHotKey(_overlayHwnd, id, modifiers, vk))
                            {
                                _registeredHotkeys[id] = item;
                            }
                        }
                    }
                }
            }

            // 3. Scan active plugin gadgets (placeholder)
            try { }
            catch { }
        }

        private static (uint modifiers, uint key) ParseHotkeyString(string hotkeyStr)
        {
            uint modifiers = 0;
            uint key = 0;

            const uint MOD_ALT = 0x0001;
            const uint MOD_CONTROL = 0x0002;
            const uint MOD_SHIFT = 0x0004;
            const uint MOD_WIN = 0x0008;

            string[] parts = hotkeyStr.Split('+');
            foreach (var part in parts)
            {
                string p = part.Trim().ToLowerInvariant();
                if (p == "ctrl" || p == "control") modifiers |= MOD_CONTROL;
                else if (p == "alt") modifiers |= MOD_ALT;
                else if (p == "shift") modifiers |= MOD_SHIFT;
                else if (p == "win" || p == "windows") modifiers |= MOD_WIN;
                else
                {
                    try
                    {
                        if (Enum.TryParse<System.Windows.Input.Key>(part, true, out var wpfKey))
                        {
                            int vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(wpfKey);
                            key = (uint)vk;
                        }
                    }
                    catch { }
                }
            }

            return (modifiers, key);
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to launch shortcut: {ex.Message}");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            if (_overlayHwnd != IntPtr.Zero)
            {
                foreach (int id in _registeredHotkeys.Keys)
                {
                    UnregisterHotKey(_overlayHwnd, id);
                }
            }
            _registeredHotkeys.Clear();
            CancelDrawMenu();
        }

        private void ShowDrawToCreateMenu(double rx, double ry, double rw, double rh, Point mousePos)
        {
            CancelDrawMenu();

            _drawMenuPopup = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(0, 3, 0, 3),
                Width = 170,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    Direction = 320,
                    ShadowDepth = 3,
                    Opacity = 0.45,
                    BlurRadius = 8
                }
            };

            var stack = new StackPanel();

            Style btnStyle = new Style(typeof(Button));
            btnStyle.Setters.Add(new Setter(Button.BackgroundProperty, Brushes.Transparent));
            btnStyle.Setters.Add(new Setter(Button.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE))));
            btnStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(0)));
            btnStyle.Setters.Add(new Setter(Button.HeightProperty, 28.0));
            btnStyle.Setters.Add(new Setter(Button.MarginProperty, new Thickness(0)));
            btnStyle.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
            btnStyle.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(16, 0, 16, 0)));
            btnStyle.Setters.Add(new Setter(Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            btnStyle.Setters.Add(new Setter(Button.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            btnStyle.Setters.Add(new Setter(Button.FontSizeProperty, 12.0));

            Style borderStyle = new Style(typeof(Border));
            borderStyle.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(0)));
            btnStyle.Resources.Add(typeof(Border), borderStyle);

            var trigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            trigger.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D))));
            btnStyle.Triggers.Add(trigger);

            var btnNormal = new Button { Content = "Standard Container", Style = btnStyle };
            btnNormal.Click += (s, e) =>
            {
                CreateContainerRequested?.Invoke(rx, ry, rw, rh, SelectedContainerType.Normal);
                CancelDrawMenu();
            };
            stack.Children.Add(btnNormal);

            var btnSvg = new Button { Content = "SVG Button Container", Style = btnStyle };
            btnSvg.Click += (s, e) =>
            {
                CreateContainerRequested?.Invoke(rx, ry, rw, rh, SelectedContainerType.SvgButton);
                CancelDrawMenu();
            };
            stack.Children.Add(btnSvg);

            var btnPortal = new Button { Content = "Folder Portal", Style = btnStyle };
            btnPortal.Click += (s, e) =>
            {
                CreateContainerRequested?.Invoke(rx, ry, rw, rh, SelectedContainerType.FolderPortal);
                CancelDrawMenu();
            };
            stack.Children.Add(btnPortal);

            _drawMenuPopup.Child = stack;

            // Position menu slightly offset from cursor so cursor starts inside/near the border
            double menuX = mousePos.X - 5;
            double menuY = mousePos.Y - 5;

            menuX = Math.Clamp(menuX, 0, Width - 170);
            menuY = Math.Clamp(menuY, 0, Height - 100);

            Canvas.SetLeft(_drawMenuPopup, menuX);
            Canvas.SetTop(_drawMenuPopup, menuY);
            Canvas.SetZIndex(_drawMenuPopup, 99999);

            _drawMenuPopup.MouseLeave += (s, e) => CancelDrawMenu();

            OverlayCanvas.Children.Add(_drawMenuPopup);
        }

        private void CancelDrawMenu()
        {
            if (_drawMenuPopup != null)
            {
                OverlayCanvas.Children.Remove(_drawMenuPopup);
                _drawMenuPopup = null;
            }
            if (_selectRect != null)
            {
                OverlayCanvas.Children.Remove(_selectRect);
                _selectRect = null;
            }
        }

        private bool IsOverDrawMenu(Point canvasPt)
        {
            if (_drawMenuPopup == null || _drawMenuPopup.Visibility != Visibility.Visible)
                return false;

            double left = Canvas.GetLeft(_drawMenuPopup);
            double top = Canvas.GetTop(_drawMenuPopup);
            double w = double.IsNaN(_drawMenuPopup.Width) ? _drawMenuPopup.ActualWidth : _drawMenuPopup.Width;
            double h = double.IsNaN(_drawMenuPopup.Height) ? _drawMenuPopup.ActualHeight : _drawMenuPopup.Height;

            return canvasPt.X >= left && canvasPt.X <= left + w &&
                   canvasPt.Y >= top && canvasPt.Y <= top + h;
        }

        #endregion

        #endregion
    }
}
