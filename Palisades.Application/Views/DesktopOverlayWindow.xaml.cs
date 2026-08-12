using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        private readonly Dictionary<string, ContainerWindow> _containerWindows = new();
        private readonly Dictionary<string, ContainerControl> _containerControls = new();
        private readonly Dictionary<string, Border> _iconElements = new();
        private readonly Dictionary<Guid, NoteControl> _noteControls = new();
        private readonly Dictionary<Guid, PluginGadgetWrapper> _gadgetControls = new();
        private readonly HashSet<ShortcutItem> _selectedIcons = new();
        private readonly HashSet<Border> _selectionHighlighted = new(); // borders currently showing selection brush (rect select optimization)
        private volatile int _selectedDeleteCount;
        private Rect _lastSelRect = Rect.Empty; // last processed rect in HandleRectSelectMove
        private HwndSource? _hwndSource;
        private bool _windowReady;
        private IntPtr _overlayHwnd;
        internal double OverlayOffsetX { get; private set; }
        internal double OverlayOffsetY { get; private set; }
        private IntPtr _desktopHwnd;

        private bool _isDragging;
        private bool _isContainerDrag;
        private List<ShortcutItem>? _containerDragItems;
        public List<ShortcutItem>? ContainerDragItems => _containerDragItems;
        private ContainerViewModel? _containerDragSource;
        private bool _isRectSelecting;
        private Point _mouseDownPoint;
        private Rectangle? _selectRect;
        private Border? _drawMenuPopup;
        private double _dpiScaleX = 1.0;
        private double _dpiScaleY = 1.0;

        // Android-style folder open state
        private bool _androidFolderOpen;
        private ContainerViewModel? _androidFolderVm;
        private Rect _androidPanelRect;
        private double _androidPanelWidth;
        private double _androidPanelHeight;
        private Point _androidPanelCenter;
        // Center of the closed tile, captured at open. The close shrink recedes the panel
        // toward this point so it ends exactly under the tile (no dot stranded in empty
        // space). Null when unknown → shrink in place.
        private Point? _androidTileCenter;
        // Delays the tile's re-show during a close until the folder has receded part-way
        // into the tile, so the shrink and the reveal read as one motion (no tile popping
        // in while the folder is still large — the "tp").
        private System.Windows.Threading.DispatcherTimer? _androidTileReShowTimer;
        private ScaleTransform? _androidPanelScale;
        private TranslateTransform? _androidPanelTranslate;
        private double _androidPanelStartScale;
        private double _androidPanelStartTx;
        private double _androidPanelStartTy;
        private int _androidGen;
        private Task<BitmapSource?>? _backdropCaptureTask;
        private System.Diagnostics.Stopwatch? _androidAnimSw;
        private System.Windows.Media.Effects.Effect? _androidPanelEffect;
        private bool _androidOpenAnimFinished;
        private BitmapSource? _pendingBackdropSource;
        private static readonly CubicEase _androidEase = new CubicEase { EasingMode = EasingMode.EaseInOut };
        // Single pre-rendered snapshot for the Scale open animation (atClick): the live
        // panel is collapsed and a frozen full-size bitmap of it is stretched per frame via
        // RenderTransform. Software bilinear stretch of a leaf Image is ~1-3ms/frame — the
        // earlier 40-60ms was BitmapCache re-rasterizing the complex panel tree, not a blit.
        private BitmapSource? _androidPanelSnapshot;
        // Pre-filtered (Fant) downscaled snapshots for the close. Swapping the Image source
        // as the panel shrinks keeps the per-frame LowQuality blit cheap (no big downscale
        // = no skipped-pixel shimmer), because the aliasing-prone detail is already removed.
        private BitmapSource? _androidSnapHalf;
        private BitmapSource? _androidSnapQuarter;
        private BitmapSource? _androidSnapEighth;
        private MatrixTransform? _androidGrowTransform;
        private System.Diagnostics.Stopwatch? _androidGrowSw;
        private double _androidGrowDurMs;
        private int _androidGrowGen;
        private string _androidGrowStyle = "Scale";
        private double _androidGrowTargetOpacity = 1;
        private static readonly BackEase _growZoomEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
        private static readonly CubicEase _growSlideEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        private static readonly ElasticEase _growElasticEase = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 2, Springiness = 4 };

        // Android folder drag/select/reorder state (within the open panel)
        private bool _isAndroidIconDrag;
        private bool _isAndroidRectSelect;
        private readonly HashSet<ShortcutItem> _androidSelectedItems = new();
        private readonly HashSet<Border> _androidHighlighted = new();
        private Point _androidDragStartLocal;
        private ShortcutItem? _androidDragSourceItem;
        private bool _androidCtrlHeld;
        private bool _androidIgnoreThisPress;
        private Rectangle? _androidSelRect;
        private Rectangle? _androidInsertionMarker;
        private bool _isAndroidRenameActive;
        // Frame-time profiler for the open/close — measures real per-frame cost of the
        // transparent overlay so backdrop-mode jank can be diagnosed without guessing.
        private System.Diagnostics.Stopwatch? _androidFrameSw;
        private double _androidFrameMax;
        private long _androidFrameCount;
        private long _androidSlowFrames;
        private bool _androidFinalized;

        /// <summary>Icon size (px) of shortcuts in the open Android folder panel.</summary>
        public static readonly DependencyProperty AndroidFolderIconSizeProperty =
            DependencyProperty.Register(nameof(AndroidFolderIconSize), typeof(double), typeof(DesktopOverlayWindow), new PropertyMetadata(72.0));
        public double AndroidFolderIconSize
        {
            get => (double)GetValue(AndroidFolderIconSizeProperty);
            set => SetValue(AndroidFolderIconSizeProperty, value);
        }

        /// <summary>True = shortcut names wrap to 2 lines in the open Android folder panel.</summary>
        public static readonly DependencyProperty AndroidTwoLineNamesProperty =
            DependencyProperty.Register(nameof(AndroidTwoLineNames), typeof(bool), typeof(DesktopOverlayWindow), new PropertyMetadata(false));
        public bool AndroidTwoLineNames
        {
            get => (bool)GetValue(AndroidTwoLineNamesProperty);
            set => SetValue(AndroidTwoLineNamesProperty, value);
        }

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

        private static IntPtr _hookId = IntPtr.Zero;
        private static LowLevelMouseProcDelegate? _hookProcInstance;
        private LowLevelMouseProcDelegate? _hookProc;

        private const int VK_DELETE = 0x2E;

        // Global keyboard hook – runs on dedicated thread with own message pump
        private delegate IntPtr LowLevelKeyboardProcDelegate(int nCode, IntPtr wParam, IntPtr lParam);
        private static LowLevelKeyboardProcDelegate? _globalKbHookProc;  // static = never GC'd
        private static volatile IntPtr _globalKbHookId = IntPtr.Zero;
        private static DesktopOverlayWindow? _instance; // for static callback access
        private volatile bool _overlayHasFocus;          // set in mouse hook, read in kb hook
        private Thread? _keyboardHookThread;

        private delegate IntPtr LowLevelMouseProcDelegate(int nCode, IntPtr wParam, IntPtr lParam);

        private bool _isContextMenuOpen;
        private ContextMenu? _currentContextMenu;
        private readonly System.Text.StringBuilder _classNameBuf = new(256);
        private DateTime _lastClickTime;
        private ShortcutItem? _lastClickItem;



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
            PreviewKeyDown += Window_KeyDown;
            Unloaded += (_, _) =>
            {
                _explorerCheckTimer?.Stop();
                UninstallHook();
            };

            SnapshotManager.ScreenshotCaptureCallback = CaptureOverlayScreenshot;
            LogAndroidPerf($"overlay window constructed ({DateTime.Now:yyyy-MM-dd})");
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
            RepositionOverlay();
            ContainerManager.Instance.RefreshUnassignedShortcuts();
            RebuildDesktopIcons();
            ContainerManager.Instance.UnassignedShortcutsChanged += RebuildDesktopIcons;
            RebuildNotes();
            var noteSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            noteSaveTimer.Tick += (_, _) => { SaveNotesToDisk(); SaveGadgetsToDisk(); };
            noteSaveTimer.Start();

            InstallHook();
            _instance = this;
            StartGlobalKeyboardHookThread();
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

        public void RebuildDesktopIcons()
        {
            foreach (var kvp in _iconElements)
                OverlayCanvas.Children.Remove(kvp.Value);
            _iconElements.Clear();

            var vm = _mainViewModel ?? DataContext as MainViewModel;
            if (vm != null && vm.ShowRecycleBin)
            {
                var name = (TranslationService.Instance != null) 
                    ? (TranslationService.Instance["RecycleBin_Name"] ?? "Recycle Bin") 
                    : "Recycle Bin";

                var rbItem = new ShortcutItem
                {
                    Name = name,
                    TargetPath = "shell:::{645FF040-5081-101B-9F08-00AA002F954E}",
                    IconPath = "shell:::{645FF040-5081-101B-9F08-00AA002F954E}",
                    ShortcutPath = "shell:::{645FF040-5081-101B-9F08-00AA002F954E}"
                };
                AddIconElement(rbItem);
            }

            foreach (var item in ContainerManager.Instance.UnassignedShortcuts)
                AddIconElement(item);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                if (FocusManager.GetFocusedElement(this) is TextBox)
                    return;

                DeleteSelectedOverlayIcons();
            }
        }

        private void DeleteSelectedOverlayIcons()
        {
            if (_selectedIcons.Count == 0) return;

            var itemsToDelete = _selectedIcons.Where(item => 
                item.TargetPath != "shell:::{645FF040-5081-101B-9F08-00AA002F954E}").ToList();

            if (itemsToDelete.Count == 0) return;

            string confirmMsg = string.Format(
                TranslationService.Instance["Dialog_DeleteOverlayConfirm"] ?? "Are you sure you want to send the selected {0} item(s) to the Recycle Bin?",
                itemsToDelete.Count);
            string confirmTitle = TranslationService.Instance["Dialog_DeleteOverlayTitle"] ?? "Delete Items";

            var result = MessageBox.Show(confirmMsg, confirmTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            foreach (var item in itemsToDelete)
            {
                string? path = item.ShortcutPath ?? item.TargetPath;
                if (!string.IsNullOrEmpty(path))
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        }
                        else if (Directory.Exists(path))
                        {
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        }
                    }
                    catch (Exception ex)
                    {
                        string errTitle = TranslationService.Instance["Dialog_Error"] ?? "Error";
                        MessageBox.Show($"Error deleting {path}: {ex.Message}", errTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }

            _selectedIcons.Clear();
            _selectedDeleteCount = 0;
            ContainerManager.Instance.RefreshUnassignedShortcuts();
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

                // Android folder open: a click outside the panel closes it.
                // Over real apps the folder closes and the click reaches the app;
                // over the desktop it is swallowed so it never hits what's underneath.
                if (_androidFolderOpen && (wParam.ToInt32() == WM_LBUTTONDOWN || wParam.ToInt32() == WM_RBUTTONDOWN))
                {
                    if (onOverlay && !IsOverOpenPanel(canvasPt))
                    {
                        CloseAndroidFolder();
                        return (IntPtr)1;
                    }
                    if (!onOverlay)
                    {
                        CloseAndroidFolder();
                    }
                }

                if (onOverlay || _isDragging || _isRectSelecting || _isAndroidIconDrag)
                {
                    int msg = wParam.ToInt32();
                    var hitItem = onOverlay ? HitTestIcon(canvasPt) : null;
                    bool overContainer = onOverlay && IsOverContainer(canvasPt);
                    bool overNote = onOverlay && IsOverNote(canvasPt);
                    bool overGadget = onOverlay && IsOverGadget(canvasPt);

                    switch (msg)
                    {
                        case WM_LBUTTONDOWN:
                            if (_activeRenameTextBox != null)
                            {
                                try
                                {
                                    var screenTopLeft = _activeRenameTextBox.PointToScreen(new Point(0, 0));
                                    var width = _activeRenameTextBox.ActualWidth;
                                    var height = _activeRenameTextBox.ActualHeight;
                                    double clickX = hookStruct.pt.X;
                                    double clickY = hookStruct.pt.Y;
                                    if (clickX < screenTopLeft.X || clickX > screenTopLeft.X + width ||
                                        clickY < screenTopLeft.Y || clickY > screenTopLeft.Y + height)
                                    {
                                        _activeRenameCommitAction?.Invoke();
                                    }
                                }
                                catch { _activeRenameCommitAction?.Invoke(); }
                            }
                            if (!IsDesktopPoint(hookStruct.pt))
                            {
                                _overlayHasFocus = false;
                                CancelDragOrRectSelect();
                                break;
                            }
                            _overlayHasFocus = true;
                            ActivateDesktopWindow();
                            if (overContainer || overNote || overGadget || IsOverDrawMenu(canvasPt) || (_androidFolderOpen && IsOverOpenPanel(canvasPt)))
                            {
                                if (IsOverDrawMenu(canvasPt))
                                {
                                    break;
                                }
                                CancelDragOrRectSelect();
                                break;
                            }
                            if (hitItem != null && hitItem == _lastClickItem
                                && (DateTime.Now - _lastClickTime).TotalMilliseconds < 300)
                            {
                                _lastClickItem = null;

                                var border = GetElementForItem(hitItem);
                                if (border != null)
                                {
                                    double top = Canvas.GetTop(border);
                                    double relativeY = canvasPt.Y - top;
                                    if (relativeY >= 50)
                                    {
                                        RenameIconInline(hitItem.ShortcutPath ?? hitItem.TargetPath ?? hitItem.Name);
                                        return (IntPtr)1;
                                    }
                                }

                                LaunchItem(hitItem);
                                return (IntPtr)1;
                            }
                            _lastClickTime = DateTime.Now;
                            _lastClickItem = hitItem;
                            HandleLeftButtonDown(canvasPt, hitItem);
                            return (IntPtr)1;

                        case WM_RBUTTONDOWN:
                            if (_activeRenameTextBox != null)
                            {
                                try
                                {
                                    var screenTopLeft = _activeRenameTextBox.PointToScreen(new Point(0, 0));
                                    var width = _activeRenameTextBox.ActualWidth;
                                    var height = _activeRenameTextBox.ActualHeight;
                                    double clickX = hookStruct.pt.X;
                                    double clickY = hookStruct.pt.Y;
                                    if (clickX < screenTopLeft.X || clickX > screenTopLeft.X + width ||
                                        clickY < screenTopLeft.Y || clickY > screenTopLeft.Y + height)
                                    {
                                        _activeRenameCommitAction?.Invoke();
                                    }
                                }
                                catch { _activeRenameCommitAction?.Invoke(); }
                            }
                            if (!IsDesktopPoint(hookStruct.pt))
                                break;
                            ActivateDesktopWindow();
                            if (overContainer || overNote || overGadget)
                            {
                                CancelDragOrRectSelect();
                                break;
                            }
                            HandleRightButtonDown(canvasPt, hitItem);
                            return (IntPtr)1;

                        case WM_MOUSEMOVE:
                            if (_isContainerDrag || _isDragging || _isAndroidIconDrag)
                            {
                                ContainerControl? activeCtrl = null;
                                foreach (var kvp in _containerControls)
                                {
                                    if (!(kvp.Value.DataContext is ContainerViewModel vm))
                                        continue;
                                    if (vm.IsVisuallyCollapsed && !vm.IsCurtainMode)
                                        continue;
                                    double left = Canvas.GetLeft(kvp.Value);
                                    double top = Canvas.GetTop(kvp.Value);
                                    double w = double.IsNaN(kvp.Value.Width) ? kvp.Value.ActualWidth : kvp.Value.Width;
                                    double h = double.IsNaN(kvp.Value.Height) ? kvp.Value.ActualHeight : kvp.Value.Height;
                                    if (canvasPt.X >= left && canvasPt.X <= left + w &&
                                        canvasPt.Y >= top && canvasPt.Y <= top + h)
                                    {
                                        activeCtrl = kvp.Value;
                                        break;
                                    }
                                }

                                foreach (var kvp in _containerControls)
                                {
                                    if (kvp.Value == activeCtrl)
                                    {
                                        kvp.Value.UpdateInsertionMarker(canvasPt);
                                    }
                                    else
                                    {
                                        kvp.Value.ClearInsertionMarker();
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
                                // A drop on the open Android panel swallows the mouse-up so the
                                // folder does not close right after receiving the items.
                                if (FinishContainerDrag(canvasPt))
                                    return (IntPtr)1;
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



        private void StartGlobalKeyboardHookThread()
        {
            _keyboardHookThread = new Thread(() =>
            {
                _globalKbHookProc = GlobalKeyboardHookCallback;
                IntPtr kbFnPtr = Marshal.GetFunctionPointerForDelegate(_globalKbHookProc);
                _globalKbHookId = SetWindowsHookExRaw(13, kbFnPtr, GetModuleHandle(null), 0);
                Console.WriteLine($"[KbHookThread] hook={_globalKbHookId}, err={Marshal.GetLastWin32Error()}");
                Console.Out.Flush();

                // Dedicated message pump so Windows can deliver hook callbacks reliably
                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                if (_globalKbHookId != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_globalKbHookId);
                    _globalKbHookId = IntPtr.Zero;
                }
            });
            _keyboardHookThread.Name = "PalisadesKbHook";
            _keyboardHookThread.IsBackground = true;
            _keyboardHookThread.Start();
        }

        private static IntPtr GlobalKeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam.ToInt32() == 0x0100) // WM_KEYDOWN
            {
                try
                {
                    var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    Console.WriteLine($"[KbHookThread] vk={kb.vkCode:X}");
                    Console.Out.Flush();
                    if (kb.vkCode == VK_DELETE)
                    {
                        var win = _instance;
                        if (win != null && win._activeRenameTextBox == null)
                        {
                            // Volatile read ensures latest value from UI thread
                            int selCount = win._selectedDeleteCount;
                            Console.WriteLine($"[KbHookThread] Delete pressed, _selectedDeleteCount={selCount}");
                            Console.Out.Flush();
                            if (selCount > 0)
                            {
                                // Only trigger delete and swallow if the desktop or our overlay is the active foreground window
                                IntPtr fg = GetForegroundWindow();
                                if (fg != IntPtr.Zero)
                                {
                                    bool isDesktopOrOverlay = false;
                                    if (fg == win._overlayHwnd)
                                    {
                                        isDesktopOrOverlay = true;
                                    }
                                    else
                                    {
                                        var buf = new System.Text.StringBuilder(256);
                                        GetClassName(fg, buf, 256);
                                        string cls = buf.ToString();
                                        if (cls is "Progman" or "WorkerW")
                                        {
                                            isDesktopOrOverlay = true;
                                        }
                                    }

                                    if (isDesktopOrOverlay)
                                    {
                                        Console.WriteLine("[KbHookThread] Triggering delete!");
                                        Console.Out.Flush();
                                        win.Dispatcher.BeginInvoke(new Action(() => win.DeleteSelectedOverlayIcons()));
                                        return (IntPtr)1; // swallow
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            return CallNextHookEx(_globalKbHookId, nCode, wParam, lParam);
        }

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
                    _selectedDeleteCount = _selectedIcons.Count;
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
                        _selectedDeleteCount = _selectedIcons.Count;
                    }
                    UpdateSelectionVisual();
                    return;
                }

                if (!_selectedIcons.Contains(hitItem))
                {
                    _selectedIcons.Clear();
                    _selectedDeleteCount = 1;
                    _selectedIcons.Add(hitItem);
                    UpdateSelectionVisual();
                }
                // Give Win32 keyboard focus to the overlay, then propagate to WPF
                SetFocus(_overlayHwnd);
                Dispatcher.BeginInvoke(new Action(() => Keyboard.Focus(this)), System.Windows.Threading.DispatcherPriority.Input);
                _isDragging = true;
                _mouseDownPoint = canvasPt;
                return;
            }

            // Empty area → rectangle selection
            ClearOverlayIconSelection();
            ClearAllContainerSelections();
            _lastSelRect = Rect.Empty;
            _isRectSelecting = true;
            _mouseDownPoint = canvasPt;
            _selectRect = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0xD0, 0x00, 0x78, 0xD7)),
                StrokeThickness = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x78, 0xD7)),
                StrokeDashArray = new DoubleCollection { 4, 2 }
            };
            Panel.SetZIndex(_selectRect, 99999);
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
                    _selectedDeleteCount = 1;
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
                ContainerManager.Instance.RefreshUnassignedShortcuts();
                return;
            }

            ClearOverlayIconSelection();
            ClearAllContainerSelections();
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
                    _selectedDeleteCount = 0;
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

            // Skip update if rect barely changed (sub-pixel jitter, tiny movements)
            if (Math.Abs(x - _lastSelRect.X) < 3 && Math.Abs(y - _lastSelRect.Y) < 3 &&
                Math.Abs(w - _lastSelRect.Width) < 3 && Math.Abs(h - _lastSelRect.Height) < 3)
                return;

            _lastSelRect = new Rect(x, y, w, h);

            // Only update selection rectangle visual – don't touch icon backgrounds during drag.
            // Updating all icon backgrounds on every mouse move causes massive GPU re-render.
            Canvas.SetLeft(_selectRect, x);
            Canvas.SetTop(_selectRect, y);
            _selectRect.Width = w;
            _selectRect.Height = h;
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
                    _selectionHighlighted.Clear();
                    _selectedIcons.Clear();
                    _selectedDeleteCount = 0;
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
                _selectionHighlighted.Clear();
                UpdateSelectionVisual();

                if (_selectedIcons.Count > 0)
                {
                    Thread.MemoryBarrier(); // flush write so background hook thread sees current state
                    _overlayHasFocus = true;
                    SetFocus(_overlayHwnd);
                }

                if (_selectedIcons.Count == 0 && w >= 50 && h >= 50)
                {
                    ShowDrawToCreateMenu(x + OverlayOffsetX, y + OverlayOffsetY, w, h, canvasPt);
                }
                else if (_selectedIcons.Count > 1)
                {
                    OverlayCanvas.Children.Remove(_selectRect);
                    _selectRect = null;
                    ShowMultiSelectMenu(canvasPt);
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

        /// <returns>True when the drop targeted the open Android folder panel (caller should swallow the mouse-up).</returns>
        private bool FinishContainerDrag(Point canvasPt)
        {
            _isContainerDrag = false;
            foreach (var kvp in _containerControls)
                kvp.Value.ClearInsertionMarker();
            if (_containerDragSource != null && _containerControls.TryGetValue(_containerDragSource.Identifier, out var srcCtrl))
                srcCtrl.ResetDragState();
            if (_containerDragItems == null || _containerDragItems.Count == 0) return false;

            double sx = (canvasPt.X + OverlayOffsetX) * _dpiScaleX;
            double sy = (canvasPt.Y + OverlayOffsetY) * _dpiScaleY;
            var screenPt = new POINT { X = (int)sx, Y = (int)sy };

            string? targetIdentifier = null;
            ContainerViewModel? targetVM = null;
            foreach (var kvp in _containerControls)
            {
                if (!(kvp.Value.DataContext is ContainerViewModel vm))
                    continue;
                // Auto-hidden containers are collapsed to a thin strip; a drop below
                // them (within the expanded bounds) must not land inside the container.
                // Curtain strips stay interactive for hover-to-open.
                if (vm.IsVisuallyCollapsed && !vm.IsCurtainMode)
                    continue;
                double left = Canvas.GetLeft(kvp.Value);
                double top = Canvas.GetTop(kvp.Value);
                double w = double.IsNaN(kvp.Value.Width) ? kvp.Value.ActualWidth : kvp.Value.Width;
                double h = double.IsNaN(kvp.Value.Height) ? kvp.Value.ActualHeight : kvp.Value.Height;
                if (canvasPt.X >= left && canvasPt.X <= left + w &&
                    canvasPt.Y >= top && canvasPt.Y <= top + h)
                {
                    targetIdentifier = vm.Identifier;
                    targetVM = vm;
                    break;
                }
            }

            // The open Android folder panel is a valid drop target even though it is
            // rendered in the overlay layer and not present in _containerControls.
            if (targetVM == null && _androidFolderOpen && _androidFolderVm != null &&
                _androidPanelRect.Contains(canvasPt))
            {
                var dropItems = _containerDragItems.ToList();
                var dropSrcVM = _containerDragSource;
                _containerDragItems = null;
                _containerDragSource = null;

                foreach (var item in dropItems)
                {
                    if (dropSrcVM != null) dropSrcVM.Shortcuts.Remove(item);
                    _androidFolderVm.Shortcuts.Add(item);
                }
                dropSrcVM?.Save();
                _androidFolderVm.Save();
                return true;
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
                // Desktop background → return to unassigned, placed under the cursor.
                // The position must be saved before ReturnToUnassigned so the
                // synchronous RebuildDesktopIcons picks it up.
                int i = 0;
                foreach (var item in items)
                {
                    srcVM.Shortcuts.Remove(item);
                    string key = item.ShortcutPath ?? item.TargetPath ?? item.Name;
                    // Center the 80x90 icon tile on the drop point; stagger multi-drops.
                    ContainerManager.Instance.SetDesktopIconPosition(key,
                        canvasPt.X - 40 + (i % 3) * 24,
                        canvasPt.Y - 45 + (i / 3) * 24);
                    ContainerManager.Instance.ReturnToUnassigned(item);
                    i++;
                }
                srcVM.Save();
            }
            return false;
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
            catch (Exception ex)
            {
                App.Log(ex, "SaveGadgetsToDisk");
            }
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

            double dpiX = 1.0;
            double dpiY = 1.0;
            try
            {
                var dpiInfo = VisualTreeHelper.GetDpi(this);
                dpiX = dpiInfo.DpiScaleX > 0 ? dpiInfo.DpiScaleX : 1.0;
                dpiY = dpiInfo.DpiScaleY > 0 ? dpiInfo.DpiScaleY : 1.0;
            }
            catch
            {
                dpiX = _dpiScaleX;
                dpiY = _dpiScaleY;
            }

            _dpiScaleX = dpiX;
            _dpiScaleY = dpiY;

            OverlayOffsetX = minX / dpiX;
            OverlayOffsetY = minY / dpiY;

            Left = minX / dpiX;
            Top = minY / dpiY;
            Width = (maxX - minX) / dpiX;
            Height = (maxY - minY) / dpiY;
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
            const int WM_KEYDOWN = 0x0100;
            const int WM_SYSKEYDOWN = 0x0104;
            switch (msg)
            {
                case WM_KEYDOWN:
                case WM_SYSKEYDOWN:
                {
                    int vk = wParam.ToInt32();
                    if (vk == VK_DELETE && _activeRenameTextBox == null && _selectedIcons.Count > 0)
                    {
                        Dispatcher.BeginInvoke(new Action(() => DeleteSelectedOverlayIcons()));
                        handled = true;
                    }
                    break;
                }

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
                    // Auto-hidden containers should not block overlay selection —
                    // they're collapsed to a small strip and the user expects to
                    // draw selection rectangles through them.
                    // Curtain containers are excluded — their strip must remain
                    // interactive for hover-to-open.
                    if (ctrl.DataContext is ContainerViewModel vm && vm.IsVisuallyCollapsed && !vm.IsCurtainMode)
                        continue;

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
                    && !(vm.IsVisuallyCollapsed && !vm.IsCurtainMode)
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
                if (child is ContainerControl ctrl && ctrl.Visibility == Visibility.Visible
                    && ctrl.DataContext is ContainerViewModel vm
                    && !(vm.IsVisuallyCollapsed && !vm.IsCurtainMode))
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

        private TextBox? _activeRenameTextBox;
        private Action? _activeRenameCommitAction;
        private bool _activationEnabled;

        private void EnableWindowActivation()
        {
            try
            {
                if (_overlayHwnd == IntPtr.Zero) return;
                const int GWL_EXSTYLE = -20;
                const int WS_EX_NOACTIVATE = 0x08000000;
                int exStyle = GetWindowLong(_overlayHwnd, GWL_EXSTYLE);
                if ((exStyle & WS_EX_NOACTIVATE) != 0)
                {
                    SetWindowLong(_overlayHwnd, GWL_EXSTYLE, exStyle & ~WS_EX_NOACTIVATE);
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
                if (_overlayHwnd == IntPtr.Zero) return;
                const int GWL_EXSTYLE = -20;
                const int WS_EX_NOACTIVATE = 0x08000000;
                int exStyle = GetWindowLong(_overlayHwnd, GWL_EXSTYLE);
                SetWindowLong(_overlayHwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
                _activationEnabled = false;
            }
            catch { }
        }

        private void RenameIconInline(string filePath)
        {
            if (!_iconElements.TryGetValue(filePath, out var border))
                return;

            if (border.Child is not StackPanel stack || stack.Children.Count < 2)
                return;

            var item = border.Tag as ShortcutItem;
            if (item == null) return;

            var oldLabel = stack.Children[1] as TextBlock;
            if (oldLabel == null) return;

            // Create TextBox
            var textBox = new TextBox
            {
                Text = item.DisplayName,
                FontSize = 11,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
                BorderThickness = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                MinWidth = 70,
                MaxWidth = 76,
                Padding = new Thickness(2),
                Margin = new Thickness(0, 2, 0, 0),
                CaretBrush = Brushes.White
            };

            // Replace TextBlock with TextBox
            stack.Children.RemoveAt(1);
            stack.Children.Add(textBox);

            EnableWindowActivation();

            textBox.Focus();
            textBox.SelectAll();

            bool isFinished = false;

            Action finishEdit = () =>
            {
                if (isFinished) return;
                isFinished = true;

                _activeRenameTextBox = null;
                _activeRenameCommitAction = null;

                DisableWindowActivation();

                string newName = textBox.Text.Trim();
                if (!string.IsNullOrEmpty(newName) && newName != item.DisplayName)
                {
                    try
                    {
                        string parentDir = System.IO.Path.GetDirectoryName(filePath)!;
                        string finalNewName = newName;
                        if (File.Exists(filePath))
                        {
                            string ext = System.IO.Path.GetExtension(filePath);
                            if (!newName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                                finalNewName = newName + ext;
                        }

                        string newPath = System.IO.Path.Combine(parentDir, finalNewName);
                        if (newPath != filePath)
                        {
                            if (Directory.Exists(filePath))
                            {
                                Directory.Move(filePath, newPath);
                            }
                            else if (File.Exists(filePath))
                            {
                                File.Move(filePath, newPath);
                            }

                            // Re-position pos key in ContainerManager
                            var oldKey = item.ShortcutPath ?? item.TargetPath ?? item.Name;
                            var oldPos = ContainerManager.Instance.GetDesktopIconPosition(oldKey);
                            if (oldPos.HasValue)
                            {
                                ContainerManager.Instance.ClearDesktopIconPositions(oldKey);
                                ContainerManager.Instance.SetDesktopIconPosition(newPath, oldPos.Value.X, oldPos.Value.Y);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error renaming: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }

                ContainerManager.Instance.RefreshUnassignedShortcuts();
            };

            _activeRenameTextBox = textBox;
            _activeRenameCommitAction = finishEdit;

            textBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    finishEdit();
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    isFinished = true;
                    _activeRenameTextBox = null;
                    _activeRenameCommitAction = null;
                    DisableWindowActivation();
                    RebuildDesktopIcons();
                }
            };

            textBox.LostFocus += (s, e) =>
            {
                finishEdit();
            };
        }

        private void ShowDesktopContextMenu(Point canvasPt)
        {
            double sx = (Left + canvasPt.X) * _dpiScaleX;
            double sy = (Top + canvasPt.Y) * _dpiScaleY;
            _isContextMenuOpen = true;
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            var filesBefore = Directory.Exists(desktopPath)
                ? Directory.GetFileSystemEntries(desktopPath)
                : Array.Empty<string>();

            ContainerControl.ShellContextMenu.ShowMenu(
                _overlayHwnd, desktopPath, (int)sx, (int)sy, true);
            _isContextMenuOpen = false;

            var filesAfter = Directory.Exists(desktopPath)
                ? Directory.GetFileSystemEntries(desktopPath)
                : Array.Empty<string>();

            var newFiles = filesAfter.Except(filesBefore, StringComparer.OrdinalIgnoreCase).ToList();
            if (newFiles.Count > 0)
            {
                string newFilePath = newFiles[0];

                double posX = canvasPt.X - 40;
                double posY = canvasPt.Y - 45;
                if (posX < 10) posX = 10;
                if (posY < 10) posY = 10;
                if (posX > Width - 90) posX = Width - 90;
                if (posY > Height - 100) posY = Height - 100;

                ContainerManager.Instance.SetDesktopIconPosition(newFilePath, posX, posY);
            }

            ContainerManager.Instance.RefreshUnassignedShortcuts();

            if (newFiles.Count > 0)
            {
                string newFilePath = newFiles[0];
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    RenameIconInline(newFilePath);
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void InstallHook()
        {
            if (_hookId == IntPtr.Zero)
            {
                _hookProc = LowLevelMouseProc;
                _hookProcInstance = _hookProc;
                IntPtr hMod = GetModuleHandle(null);
                _hookId = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, hMod, 0);
                int mouseErr = Marshal.GetLastWin32Error();
                Console.WriteLine($"[InstallHook] Mouse Hook Result={_hookId}, Error={mouseErr}");
                Console.Out.Flush();
            }
        }

        private void UninstallHook()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            _hookProc = null;
            _hookProcInstance = null;
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

                if (vm.IsAndroidFolderContainer)
                    control.AndroidFolderOpenRequested += () => OpenAndroidFolder(vm);
            }
            catch (Exception ex)
            {
                LogAndroidPerf($"AddContainer FAILED ({vm.Name}, type={vm.IsAndroidFolderContainer}): {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Failed to add container to overlay: {ex.Message}");
            }
        }

        public void RemoveContainer(string identifier)
        {
            if (_androidFolderVm != null && _androidFolderVm.Identifier == identifier)
                CloseAndroidFolder();

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

        public void ClearOverlayIconSelection()
        {
            try
            {
                _selectionHighlighted.Clear();
                _selectedIcons?.Clear();
                _selectedDeleteCount = 0;
                UpdateSelectionVisual();
            }
            catch { }
        }

        public void ClearAllContainerSelections()
        {
            try
            {
                foreach (var cc in _containerControls.Values)
                {
                    try { cc?.ClearSelection(); } catch { }
                }
                foreach (var cw in _containerWindows.Values)
                {
                    try { cw?.ClearSelection(); } catch { }
                }
            }
            catch { }
        }

        public void SetAllVisible(bool visible)
        {
            if (!visible && _androidFolderOpen)
                CloseAndroidFolder();
            foreach (var ctrl in _containerControls.Values)
                ctrl.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion

        #region Android-style folder (open state)

        private static void Animate(DependencyObject target, DependencyProperty prop, double from, double to,
            double ms, IEasingFunction? ease, Action? onComplete = null)
        {
            var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };
            if (onComplete != null)
                anim.Completed += (_, _) => onComplete();
            // IAnimatable covers both Animatable (transforms/brushes) and UIElement (Grid/Border).
            // A bare `as Animatable` cast would silently no-op for UIElement targets.
            if (target is IAnimatable animatable)
                animatable.BeginAnimation(prop, anim);
        }

        private void OnAndroidRenderFrame(object? sender, EventArgs e)
        {
            var sw = _androidFrameSw;
            if (sw == null) return;
            double delta = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            _androidFrameCount++;
            if (delta > _androidFrameMax) _androidFrameMax = delta;
            if (delta > 25)
            {
                _androidSlowFrames++;
                double t = _androidAnimSw?.Elapsed.TotalMilliseconds ?? -1;
                double sc = _androidPanelScale?.ScaleX ?? -1;
                // During the grow the panel transform stays pinned at start scale — report
                // the snapshot Image's live transform scale instead.
                if (_androidGrowSw != null && _androidGrowDurMs > 0)
                {
                    double tt = _androidGrowSw.Elapsed.TotalMilliseconds;
                    double eased = _androidEase.Ease(Math.Min(1.0, tt / _androidGrowDurMs));
                    sc = _androidPanelStartScale + (1 - _androidPanelStartScale) * eased;
                }
                // No GC.GetTotalMemory here: it walks the managed heap with the UI thread
                // blocked, which would manufacture the very slow frames it is logging.
                LogAndroidPerf($"SLOWFRAME {delta:F0}ms open={_androidFolderOpen} t={t:F0}ms scale={sc:F2} gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)}");
            }
            // Profile through the close animation: only finalize once FinalizeAndroidClose
            // has run (the close anim is where the user reported lag).
            if (!_androidFolderOpen && _androidFinalized && _androidFrameCount > 3)
            {
                CompositionTarget.Rendering -= OnAndroidRenderFrame;
                _androidFrameSw = null;
                LogAndroidPerf($"FRAMES count={_androidFrameCount} max={_androidFrameMax:F1}ms slow(>25ms)={_androidSlowFrames} ({100.0 * _androidSlowFrames / _androidFrameCount:F0}%)");
            }
        }

        /// <summary>
        /// Capture the desktop behind the overlay, downscale, optionally box-blur it, and
        /// bake a dim into it. Runs on a background thread: GDI capture + a plain C# blur
        /// avoid the WPF BlurEffect RenderTargetBitmap pass (software rasterizer) that used
        /// to block the UI thread and stall the open animation. Caller must pass
        /// physical-pixel coordinates. keep = 1 - dim/100 (1 = full brightness, 0 = black).
        /// </summary>
        private static BitmapSource? CaptureBlurredBackdrop(int x, int y, int w, int h, bool blur, double keep)
        {
            try
            {
                if (w <= 0 || h <= 0) return null;

                using var src = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(src))
                {
                    g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
                }

                // Downscale before blurring so the pre-blur and the full-screen upscale stay cheap.
                // Blur hides aliasing, so 0.10 (4% of the pixels) is fine there; the unblurred
                // wallpaper keeps 0.25 so it stays readable when upscaled.
                double capScale = blur ? 0.10 : 0.25;
                int sw = Math.Max(1, (int)Math.Round(w * capScale));
                int sh = Math.Max(1, (int)Math.Round(h * capScale));
                using var small = new System.Drawing.Bitmap(sw, sh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(small))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
                    g.DrawImage(src, 0, 0, sw, sh);
                }

                int stride = sw * 4;
                var pixels = new byte[stride * sh];
                var data = small.LockBits(new System.Drawing.Rectangle(0, 0, sw, sh),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                }
                finally
                {
                    small.UnlockBits(data);
                }

                // Desktop capture is opaque, so blur only RGB and keep alpha. Radius 4 at 0.10
                // scale ≈ radius 10 at full res — softer but still hides details, runs ~4× faster.
                if (blur)
                    BoxBlur(pixels, sw, sh, stride, 4, 3);
                // Bake the dim into the capture so the backdrop needs no separate full-screen
                // dim border — a second full-screen software blend every frame is what made
                // the full-screen-mode fade stutter.
                ApplyDim(pixels, keep);

                var bs = BitmapSource.Create(sw, sh, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                bs.Freeze();
                return bs;
            }
            catch { return null; }
        }

        /// <summary>Darken a BGRA buffer by a keep factor (0.30 = 70% dim), preserving alpha.</summary>
        private static void ApplyDim(byte[] pixels, double keep)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = (byte)(pixels[i] * keep);
                pixels[i + 1] = (byte)(pixels[i + 1] * keep);
                pixels[i + 2] = (byte)(pixels[i + 2] * keep);
            }
        }

        /// <summary>Separable box blur (sliding-window sums) on a BGRA buffer. Safe on any thread.</summary>
        private static void BoxBlur(byte[] pixels, int width, int height, int stride, int radius, int passes)
        {
            var tmp = new byte[pixels.Length];
            for (int p = 0; p < passes; p++)
            {
                BlurAxis(pixels, tmp, width, height, stride, radius, horizontal: true);
                BlurAxis(tmp, pixels, width, height, stride, radius, horizontal: false);
            }
        }

        private static void BlurAxis(byte[] src, byte[] dst, int width, int height, int stride, int radius, bool horizontal)
        {
            int kernel = radius * 2 + 1;
            if (horizontal)
            {
                for (int y = 0; y < height; y++)
                {
                    int row = y * stride;
                    long sb = 0, sg = 0, sr = 0;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = row + x * 4;
                        sb += src[idx];
                        sg += src[idx + 1];
                        sr += src[idx + 2];
                        if (x >= kernel)
                        {
                            int o = row + (x - kernel) * 4;
                            sb -= src[o];
                            sg -= src[o + 1];
                            sr -= src[o + 2];
                        }
                        int count = Math.Min(x + 1, kernel);
                        dst[idx] = (byte)(sb / count);
                        dst[idx + 1] = (byte)(sg / count);
                        dst[idx + 2] = (byte)(sr / count);
                        dst[idx + 3] = src[idx + 3];
                    }
                }
            }
            else
            {
                for (int x = 0; x < width; x++)
                {
                    long sb = 0, sg = 0, sr = 0;
                    for (int y = 0; y < height; y++)
                    {
                        int idx = y * stride + x * 4;
                        sb += src[idx];
                        sg += src[idx + 1];
                        sr += src[idx + 2];
                        if (y >= kernel)
                        {
                            int o = (y - kernel) * stride + x * 4;
                            sb -= src[o];
                            sg -= src[o + 1];
                            sr -= src[o + 2];
                        }
                        int count = Math.Min(y + 1, kernel);
                        dst[idx] = (byte)(sb / count);
                        dst[idx + 1] = (byte)(sg / count);
                        dst[idx + 2] = (byte)(sr / count);
                        dst[idx + 3] = src[idx + 3];
                    }
                }
            }
        }

        private Rect GetTileCanvasRect(ContainerViewModel vm)
        {
            if (_containerControls.TryGetValue(vm.Identifier, out var ctrl))
            {
                double left = Canvas.GetLeft(ctrl);
                double top = Canvas.GetTop(ctrl);
                double w = double.IsNaN(ctrl.Width) ? ctrl.ActualWidth : ctrl.Width;
                double h = double.IsNaN(ctrl.Height) ? ctrl.ActualHeight : ctrl.Height;
                if (w > 0 && h > 0)
                    return new Rect(left, top, w, h);
            }
            return Rect.Empty;
        }

        private bool IsOverOpenPanel(Point canvasPt)
        {
            return _androidFolderOpen && _androidPanelRect.Contains(canvasPt);
        }

        private void SetAndroidTileHidden(ContainerViewModel vm, bool hidden, int animationDurationMs = 200)
        {
            if (_containerControls.TryGetValue(vm.Identifier, out var control))
                control.SetAndroidTileHidden(hidden, animationDurationMs);
        }

        private void OpenAndroidFolder(ContainerViewModel vm)
        {
            try
            {
                if (_androidFolderOpen)
                {
                    _androidFolderOpen = false;
                    if (_androidFolderVm != null)
                        SetAndroidTileHidden(_androidFolderVm, false);
                    FinalizeAndroidClose();
                }

                var tile = GetTileCanvasRect(vm);
                if (tile == Rect.Empty) return;

                // A pending delayed tile re-show belongs to a prior close — cancel it so the
                // new open's hide is not fought by a stale re-show.
                StopAndroidTileReShowTimer();
                var setupSw = System.Diagnostics.Stopwatch.StartNew();
                _androidGen++;
                int gen = _androidGen;
                _androidFolderOpen = true;
                _androidFolderVm = vm;
                vm.IsAndroidFolderOpen = true;
                SetAndroidTileHidden(vm, true, vm.AndroidAnimationDurationMs);

                _androidFrameSw = System.Diagnostics.Stopwatch.StartNew();
                _androidFrameMax = 0;
                _androidFrameCount = 0;
                _androidSlowFrames = 0;
                _androidFinalized = false;
                CompositionTarget.Rendering += OnAndroidRenderFrame;

                double panelW = Math.Clamp(vm.AndroidPanelWidth, 240, Width - 16);
                double panelH = Math.Clamp(vm.AndroidPanelHeight, 200, Height - 16);

                // Center of the closed tile (canvas coordinates).
                double tileCx = tile.X + tile.Width / 2;
                double tileCy = tile.Y + tile.Height / 2;
                _androidTileCenter = new Point(tileCx, tileCy);
                double screenCx, screenCy;
                // Working area of the monitor that contains the tile, in overlay DIP coords.
                // The overlay spans all monitors, so clamping to the COMBINED bounds lets a
                // panel on a tile near an internal edge bleed onto the neighbor monitor;
                // clamping to the tile's own monitor keeps every panel on its own screen.
                Rect monitorArea = Rect.Empty;
                try
                {
                    var s = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
                        (int)Math.Round((tileCx + OverlayOffsetX) * _dpiScaleX),
                        (int)Math.Round((tileCy + OverlayOffsetY) * _dpiScaleY))).WorkingArea;
                    monitorArea = new Rect(
                        s.Left / _dpiScaleX - OverlayOffsetX,
                        s.Top / _dpiScaleY - OverlayOffsetY,
                        s.Width / _dpiScaleX,
                        s.Height / _dpiScaleY);
                }
                catch { }

                if (vm.AndroidOpenAtClick)
                {
                    // Open centered on the tile where the user clicked (then clamped below).
                    screenCx = tileCx;
                    screenCy = tileCy;
                }
                else
                {
                    // Center on the monitor that contains the tile center.
                    screenCx = monitorArea != Rect.Empty ? monitorArea.X + monitorArea.Width / 2 : Width / 2;
                    screenCy = monitorArea != Rect.Empty ? monitorArea.Y + monitorArea.Height / 2 : Height / 2;
                }

                panelW = Math.Min(panelW, Width - 16);
                panelH = Math.Min(panelH, Height - 16);
                if (monitorArea != Rect.Empty)
                {
                    screenCx = ClampAndroidCenter(screenCx, monitorArea.X, monitorArea.Width, panelW);
                    screenCy = ClampAndroidCenter(screenCy, monitorArea.Y, monitorArea.Height, panelH);
                }
                else
                {
                    screenCx = Math.Clamp(screenCx, 8 + panelW / 2, Width - 8 - panelW / 2);
                    screenCy = Math.Clamp(screenCy, 8 + panelH / 2, Height - 8 - panelH / 2);
                }

                _androidPanelWidth = panelW;
                _androidPanelHeight = panelH;
                _androidPanelCenter = new Point(screenCx, screenCy);
                _androidPanelRect = new Rect(screenCx - panelW / 2, screenCy - panelH / 2, panelW, panelH);

                AndroidPanel.DataContext = vm;

                AndroidBackdropRoot.Opacity = 0;
                AndroidBackdropImage.Source = null;
                AndroidBackdropImage.Effect = null; // pre-blurred once — no live blur per frame
                bool fullScreen = vm.AndroidBackdropMode != "Panel";
                _backdropCaptureTask = null;
                bool capBlur = vm.AndroidBackdropStyle == "Blur";
                bool needsCapture = fullScreen && (capBlur || vm.AndroidBackdropStyle == "Darkening");
                if (needsCapture)
                {
                    // Capture (+ blur) off the UI thread so the open animation starts immediately.
                    int capX = (int)Math.Round(Left * _dpiScaleX);
                    int capY = (int)Math.Round(Top * _dpiScaleY);
                    int capW = (int)Math.Round(Width * _dpiScaleX);
                    int capH = (int)Math.Round(Height * _dpiScaleY);
                    double keep = 1.0 - Math.Clamp(vm.AndroidBackdropDim, 0, 100) / 100.0;
                    _backdropCaptureTask = Task.Run(() => CaptureBlurredBackdrop(capX, capY, capW, capH, capBlur, keep));
                }
                ConfigureAndroidBackdrop();

                AndroidPanelTitle.Text = vm.Name;
                AndroidPanelTitle.Visibility = vm.AndroidShowHeader ? Visibility.Visible : Visibility.Collapsed;
                AndroidFolderIconSize = vm.AndroidIconSize;
                AndroidTwoLineNames = vm.TwoLineShortcuts;
                AndroidIconsList.ItemsSource = vm.Shortcuts;

                AndroidPanel.Width = panelW;
                AndroidPanel.Height = panelH;
                AndroidPanel.Margin = new Thickness(screenCx - panelW / 2, screenCy - panelH / 2, 0, 0);
                AndroidPanel.RenderTransformOrigin = new Point(0.5, 0.5);

                // Start the bloom much smaller than the tile so the panel reads as a fluid
                // zoom from a tiny dot (bitmap-cached, so scaling is cheap).
                _androidPanelStartScale = 0.02;
                // Pure scale at the clamped center — the SAME animation at every position.
                // The panel never translates from the tile, so an edge tile (whose clamped
                // center is elsewhere) no longer produces a position-dependent slide/arc.
                _androidPanelStartTx = 0;
                _androidPanelStartTy = 0;
                _androidPanelScale = new ScaleTransform(_androidPanelStartScale, _androidPanelStartScale);
                _androidPanelTranslate = new TranslateTransform(_androidPanelStartTx, _androidPanelStartTy);
                var transform = new TransformGroup();
                transform.Children.Add(_androidPanelTranslate);
                transform.Children.Add(_androidPanelScale);
                AndroidPanel.RenderTransform = transform;

                _androidOpenAnimFinished = false;
                _pendingBackdropSource = null;
                AndroidBackdropImage.Source = null;
                AndroidBackdropImage.Opacity = 0;
                AndroidBackdropImage.Visibility = Visibility.Collapsed;

                AndroidFolderLayer.Visibility = Visibility.Visible;

                // Force a synchronous layout update so first frame of rendering does not
                // have to execute Measure/Arrange and compile bitmap cache simultaneously.
                AndroidFolderLayer.UpdateLayout();

                SubscribeAndroidVm(vm);
                RefreshAndroidPanelStyle();
                // The 40px drop shadow re-rasterizes every frame it animates — drop it
                // for the open/close and re-attach when the motion stops.
                _androidPanelEffect = AndroidPanel.Effect;
                AndroidPanel.Effect = null;
                // Create the bitmap cache LAST, after every panel mutation (title, effect).
                // The growth animation then scales a stable cached bitmap — if anything
                // invalidates the cache mid-animation the icons re-rasterize every frame
                // and the panel visibly "modifies itself" while it grows.
                // Pre-render the panel once and drive EVERY open animation by transforming
                // that frozen bitmap per frame (see OnAndroidGrowRenderFrame). The live panel
                // stays collapsed during the animation: a live-panel BitmapCache animating
                // inside this layered (software-composited) window was measured to render
                // blank on non-Scale styles, while the snapshot blit is proven smooth for
                // Scale opens and every close.
                RenderAndroidSnapshot();
                setupSw.Stop();
                LogAndroidPerf($"open setup {setupSw.Elapsed.TotalMilliseconds:F1}ms mode={vm.AndroidBackdropMode} atClick={vm.AndroidOpenAtClick} anim={vm.AndroidOpenAnimation} panel={panelW:F0}x{panelH:F0}@{screenCx:F0},{screenCy:F0} tile={tileCx:F0},{tileCy:F0} startTx={_androidPanelStartTx:F0} startTy={_androidPanelStartTy:F0}");
                AnimateAndroidOpen(gen);
                _ = ApplyAndroidBackdropAsync(gen);
            }
            catch (Exception ex)
            {
                LogAndroidPerf($"OpenAndroidFolder FAILED: {ex.GetType().Name}: {ex.Message}");
                // The tile was already hidden for the open animation — restore it so a
                // failed open never leaves the folder invisible on the desktop.
                try { vm.IsAndroidFolderOpen = false; } catch { }
                FinalizeAndroidClose();
                try { SetAndroidTileHidden(vm, false, 150); } catch { }
            }
        }

        private void CloseAndroidFolder()
        {
            if (!_androidFolderOpen)
            {
                LogAndroidPerf("CloseAndroidFolder called while NOT open (ignored)");
                return;
            }
            LogAndroidPerf($"CloseAndroidFolder START vm={_androidFolderVm?.Name}");
            _androidGen++;
            int gen = _androidGen;
            _androidFolderOpen = false;
            var vm = _androidFolderVm;
            _androidFolderVm = null;
            if (vm != null)
            {
                vm.IsAndroidFolderOpen = false;
                // Re-show the tile AFTER the folder has receded part-way into it. An
                // immediate re-show fades the tile in while the folder is still large and
                // (for edge tiles) 90px away — two separated objects, reads as a "tp".
                // Delaying until the shrink has nearly reached the tile makes the folder's
                // recede and the tile's reveal one continuous motion.
                ScheduleAndroidTileReShow(vm, gen, vm.AndroidAnimationDurationMs);
            }
            UnsubscribeAndroidVm(vm);
            if (_isAndroidRenameActive) CancelAndroidTitleRename();

            AnimateAndroidClose(vm, gen);
        }

        private void ScheduleAndroidTileReShow(ContainerViewModel vm, int gen, int durMs)
        {
            StopAndroidTileReShowTimer();
            // Start the tile reveal as the shrinking folder begins its fade-out (the last
            // ~45% of the shrink, see OnAndroidCloseRenderFrame) so the two cross-fade at
            // the same spot — the folder melts into the tile instead of popping away.
            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, durMs * 0.55));
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                // A new open/close superseded this close — don't re-show the tile over it.
                if (_androidGen != gen) return;
                SetAndroidTileHidden(vm, false, 200);
            };
            timer.Start();
            _androidTileReShowTimer = timer;
        }

        private void StopAndroidTileReShowTimer()
        {
            if (_androidTileReShowTimer != null)
            {
                _androidTileReShowTimer.Stop();
                _androidTileReShowTimer = null;
            }
        }

        private void FinalizeAndroidClose()
        {
            LogAndroidPerf("FinalizeAndroidClose");
            _androidFinalized = true;
            try
            {
                StopAndroidGrow();
                _androidPanelSnapshot = null;
                AndroidPanel.CacheMode = null;
                AndroidPanel.RenderTransform = null;
                AndroidFolderLayer.Visibility = Visibility.Collapsed;
                AndroidIconsList.ItemsSource = null;
                AndroidBackdropImage.Source = null;
                _backdropCaptureTask = null;
                AndroidBackdropRoot.CacheMode = null;
                AndroidBackdropRoot.BeginAnimation(OpacityProperty, null);
                AndroidBackdropRoot.Opacity = 1;
                AndroidBackdropRoot.HorizontalAlignment = HorizontalAlignment.Stretch;
                AndroidBackdropRoot.VerticalAlignment = VerticalAlignment.Stretch;
                AndroidBackdropRoot.Margin = new Thickness(0);
                AndroidBackdropRoot.Width = double.NaN;
                AndroidBackdropRoot.Height = double.NaN;
                _androidFolderOpen = false;
                UnsubscribeAndroidVm(_androidFolderVm);
                _androidFolderVm = null;

                // Reset any in-progress drag/selection inside the panel.
                _isAndroidIconDrag = false;
                _isAndroidRectSelect = false;
                _androidDragSourceItem = null;
                _androidCtrlHeld = false;
                foreach (var b in _androidHighlighted)
                {
                    try { b.ClearValue(Border.BackgroundProperty); } catch { }
                }
                _androidHighlighted.Clear();
                _androidSelectedItems.Clear();
                ClearAndroidSelectionRect();
                ClearAndroidInsertionMarker();
                AndroidFolderScroller.ReleaseMouseCapture();
                if (_isAndroidRenameActive) CancelAndroidTitleRename();

                // Release any held opacity animation so the bound value is restored.
                AndroidPanel.BeginAnimation(OpacityProperty, null);
                if (_androidPanelEffect != null)
                {
                    AndroidPanel.Effect = _androidPanelEffect;
                    _androidPanelEffect = null;
                }
            }
            catch { }
        }

        private void AndroidPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            // A click on the title is reserved for double-click rename; don't close on it.
            if (IsAndroidTitleArea(e.OriginalSource)) return;
            CloseAndroidFolder();
        }

        private static bool IsAndroidTitleArea(object? src)
        {
            var dep = src as DependencyObject;
            while (dep != null && dep is not Window)
            {
                if (dep is FrameworkElement fe &&
                    (fe.Name == "AndroidPanelTitle" || fe.Name == "AndroidTitleEditBox"))
                    return true;
                dep = VisualTreeHelper.GetParent(dep);
            }
            return false;
        }

        // === Android folder: open/close animations, live style refresh, title rename ===

        private static readonly HashSet<string> _androidRefreshNames = new()
        {
            nameof(ContainerViewModel.Name),
            nameof(ContainerViewModel.AndroidShowHeader),
            nameof(ContainerViewModel.AndroidHeaderFontSize),
            nameof(ContainerViewModel.AndroidTitleTwoLine),
            nameof(ContainerViewModel.AndroidPanelBackgroundBrush),
            nameof(ContainerViewModel.AndroidPanelBorderBrush),
            nameof(ContainerViewModel.AndroidOpenOpacity),
            nameof(ContainerViewModel.AndroidPanelCornerRadius),
            nameof(ContainerViewModel.AndroidPanelShowBorder),
            nameof(ContainerViewModel.AndroidBackdropMode),
            nameof(ContainerViewModel.AndroidBackdropStyle),
            nameof(ContainerViewModel.AndroidBackdropColor),
            nameof(ContainerViewModel.AndroidBackdropDim),
        };

        private void SubscribeAndroidVm(ContainerViewModel? vm)
        {
            if (vm != null) vm.PropertyChanged += OnAndroidFolderVmPropertyChanged;
        }

        private void UnsubscribeAndroidVm(ContainerViewModel? vm)
        {
            if (vm != null) vm.PropertyChanged -= OnAndroidFolderVmPropertyChanged;
        }

        private void OnAndroidFolderVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender != _androidFolderVm || e.PropertyName is null) return;
            if (_androidRefreshNames.Contains(e.PropertyName))
                RefreshAndroidPanelStyle();
        }

        private void RefreshAndroidPanelStyle()
        {
            var vm = _androidFolderVm;
            if (vm == null) return;
            AndroidPanelTitle.Text = vm.Name;
            AndroidPanelTitle.Visibility = vm.AndroidShowHeader ? Visibility.Visible : Visibility.Collapsed;
            ConfigureAndroidBackdrop();
        }

        private void ConfigureAndroidBackdrop()
        {
            var vm = _androidFolderVm;
            if (vm == null) return;

            if (vm.AndroidBackdropMode == "Panel")
            {
                // Panel mode: no dark backdrop — the folder floats over the live desktop with its drop shadow.
                AndroidBackdropImage.Source = null;
                AndroidBackdropImage.Visibility = Visibility.Collapsed;
                AndroidBackdropDim.Visibility = Visibility.Collapsed;
            }
            else
            {
                AndroidBackdropRoot.HorizontalAlignment = HorizontalAlignment.Stretch;
                AndroidBackdropRoot.VerticalAlignment = VerticalAlignment.Stretch;
                AndroidBackdropRoot.Margin = new Thickness(0);
                AndroidBackdropRoot.Width = double.NaN;
                AndroidBackdropRoot.Height = double.NaN;
                AndroidBackdropDim.CornerRadius = new CornerRadius(0);
                if (vm.AndroidBackdropStyle == "Color")
                {
                    // Flat solid fill from the user's color — no capture, no blur. The
                    // default is opaque #FF1F1F1F: an opaque fill fully occludes the desktop
                    // widgets so the software renderer skips them (measured ~40ms/frame with
                    // a semi-transparent veil). A user-chosen semi-transparent color still
                    // honors their pick, at the cost of that per-frame re-blend.
                    AndroidBackdropDim.Background = vm.AndroidBackdropColorBrush;
                    AndroidBackdropImage.Visibility = Visibility.Collapsed;
                }
                else
                {
                    // Blur / Darkening: the dim is baked into the captured image; a
                    // transparent (but non-null) background keeps the border hit-testable
                    // so backdrop clicks close the folder.
                    AndroidBackdropDim.Background = Brushes.Transparent;
                    AndroidBackdropImage.Visibility = AndroidBackdropImage.Source != null ? Visibility.Visible : Visibility.Collapsed;
                }
                AndroidBackdropDim.Visibility = Visibility.Visible;
            }
        }

        // Buffered async perf logger. Writing the log synchronously on the UI thread
        // (the frame profiler logs from inside CompositionTarget.Rendering) delayed the
        // NEXT frame by the file-I/O time — a logged 30ms frame caused the following
        // frame to measure 30ms, cascading into a self-sustaining burst of false "slow"
        // frames. Enqueue + flush on a low-priority background thread keeps profiling
        // from perturbing the measurement it records.
        private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _perfLogQueue = new();
        private static int _perfFlusherStarted;

        internal static void LogAndroidPerf(string msg)
        {
            try
            {
                _perfLogQueue.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
                if (System.Threading.Interlocked.CompareExchange(ref _perfFlusherStarted, 1, 0) == 0)
                {
                    var thread = new System.Threading.Thread(() =>
                    {
                        string path = System.IO.Path.Combine(
                            System.IO.Path.GetTempPath(), "palisades-android-perf.log");
                        while (true)
                        {
                            try
                            {
                                if (_perfLogQueue.TryDequeue(out var line))
                                    System.IO.File.AppendAllText(path, line);
                                else
                                    System.Threading.Thread.Sleep(50);
                            }
                            catch { System.Threading.Thread.Sleep(100); }
                        }
                    })
                    { IsBackground = true, Priority = System.Threading.ThreadPriority.Lowest, Name = "PalisadesPerfLog" };
                    thread.Start();
                }
            }
            catch { }
        }

        /// <summary>Swap the off-thread backdrop capture in when it finishes, without stalling the open animation.</summary>
        private async Task ApplyAndroidBackdropAsync(int gen)
        {
            var vm = _androidFolderVm;
            if (vm == null || vm.AndroidBackdropMode == "Panel") return;
            if (vm.AndroidBackdropStyle == "Color")
            {
                // Solid color backdrop — no screen capture to await. Snapping it on instead
                // of fading: a full-screen opacity fade re-blends the whole window in
                // software every frame (~40ms/frame measured), the fade is exactly what
                // lags. An instant veil pop reads as a light switch — the panel growth is
                // then the only animated element.
                LogAndroidPerf("color backdrop snapped on");
                AndroidBackdropRoot.Opacity = 1;
                return;
            }
            var task = _backdropCaptureTask;
            if (task == null) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            BitmapSource? bmp;
            try { bmp = await task; }
            catch { bmp = null; }
            sw.Stop();
            LogAndroidPerf($"backdrop capture landed in {sw.Elapsed.TotalMilliseconds:F1}ms (dropped={gen != _androidGen || !_androidFolderOpen})");
            if (gen != _androidGen || !_androidFolderOpen) return;
            if (_androidFolderVm == null) return;

            // Start the backdrop fade now that the capture is done. A failed capture
            // (bmp == null) still fades in the dim so the folder never opens backdrop-less.
            _pendingBackdropSource = bmp;
            ApplyReadyBackdrop();
        }

        private void ApplyReadyBackdrop()
        {
            if (!_androidFolderOpen) return;
            // Bake the completed full-screen backdrop into a bitmap cache, then fade the
            // CACHE in (a per-frame alpha-blit). The cache renders at 0.25× — the backdrop
            // is already blurred, so quarter-res is visually identical, but building a
            // full-res full-screen cache stalls the software renderer for several frames.
            if (_pendingBackdropSource != null)
            {
                AndroidBackdropImage.Source = _pendingBackdropSource;
                AndroidBackdropImage.Opacity = 1;
                AndroidBackdropImage.Visibility = Visibility.Visible;
                _pendingBackdropSource = null;
            }
            AndroidBackdropRoot.CacheMode = new BitmapCache { RenderAtScale = 0.25 };
            double dur = _androidFolderVm?.AndroidAnimationDurationMs ?? 320;
            double fadeMs = Math.Min(150, dur * 0.5);
            LogAndroidPerf($"backdrop fade started ({fadeMs:F0}ms, cached)");
            Animate(AndroidBackdropRoot, OpacityProperty, 0, 1, fadeMs, null);
        }

        /// <summary>Clamp a panel center to keep the panel (of size panelSize) inside a screen
        /// area, leaving an 8px inset. If the panel is larger than the area, center it.</summary>
        private static double ClampAndroidCenter(double center, double areaLeft, double areaSize, double panelSize)
        {
            double inset = Math.Min(8 + panelSize / 2, areaSize / 2);
            double min = areaLeft + inset;
            double max = areaLeft + areaSize - inset;
            return min >= max ? areaLeft + areaSize / 2 : Math.Clamp(center, min, max);
        }

        private static double GetAndroidOpenOpacity(ContainerViewModel? vm)
            => (vm?.AndroidOpenOpacity ?? 100) / 100.0;

        private void ResetAndroidTransforms(double? scale, double? tx, double? ty)
        {
            if (_androidPanelScale != null)
            {
                _androidPanelScale.ScaleX = scale ?? 1.0;
                _androidPanelScale.ScaleY = scale ?? 1.0;
            }
            if (_androidPanelTranslate != null)
            {
                _androidPanelTranslate.X = tx ?? 0;
                _androidPanelTranslate.Y = ty ?? 0;
            }
        }

        private void AnimateAndroidOpen(int gen)
        {
            var vm = _androidFolderVm;
            if (vm == null) return;
            // Every open runs from the frozen snapshot (RenderAndroidSnapshot). The style
            // drives the per-frame transform in OnAndroidGrowRenderFrame; a centered open
            // is a plain fade (no scale/translate). _androidGrowStyle is picked here so
            // the render-frame handler does not branch on open mode.
            _androidGrowStyle = vm.AndroidOpenAtClick ? vm.AndroidOpenAnimation : "Fade";
            // Panel mode has no backdrop — nothing full-screen to fade.
            if (vm.AndroidBackdropMode == "Panel")
                AndroidBackdropRoot.Opacity = 1;
            // Full-screen mode: the backdrop fade is DEFERRED until the blurred capture
            // lands (ApplyReadyBackdrop). Fading a full-screen backdrop while the capture
            // thread is still building it forces per-frame re-rasterization of the whole
            // backdrop (image scale + dim blend) on a software-composited transparent
            // overlay → stutter. Once the capture is baked into a BitmapCache, the fade is
            // a cheap per-frame alpha-blit of a cached bitmap.

            _androidAnimSw = System.Diagnostics.Stopwatch.StartNew();
            if (_androidPanelSnapshot != null)
                StartAndroidGrow(gen);
            else
                FinishAndroidOpen(gen);
        }

        private void FinishAndroidOpen(int gen)
        {
            if (_androidGen != gen || !_androidFolderOpen) return;
            if (_androidAnimSw != null)
            {
                _androidAnimSw.Stop();
                LogAndroidPerf($"open animation wall-clock {_androidAnimSw.Elapsed.TotalMilliseconds:F1}ms (dur={_androidFolderVm?.AndroidAnimationDurationMs ?? 0})");
                _androidAnimSw = null;
            }
            // Re-create the cache at FULL resolution now that the grow is done (crisp
            // settled state). Keeping a cache here instead of nulling it re-rasterizes the
            // panel content once into the cache, then the shadow re-blur is a single
            // post-cache pass — the old null-then-blur sequence read as several slow frames
            // at the end of every open. The cache re-renders on live style changes too.
            double scale = _dpiScaleX > 0 ? _dpiScaleX : 1.0;
            AndroidPanel.CacheMode = new BitmapCache { RenderAtScale = scale };
            // Drop the open animation so the bound opacity/brushes stay live.
            AndroidPanel.BeginAnimation(OpacityProperty, null);
            if (_androidPanelScale != null)
            {
                _androidPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _androidPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _androidPanelScale.ScaleX = 1.0;
                _androidPanelScale.ScaleY = 1.0;
            }
            if (_androidPanelTranslate != null)
            {
                _androidPanelTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                _androidPanelTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                _androidPanelTranslate.X = 0;
                _androidPanelTranslate.Y = 0;
            }
            if (_androidPanelEffect != null)
            {
                // Re-attach the drop shadow at full strength in ONE frame. Animating its
                // Opacity would re-run the software blur every frame for 160ms — ten heavy
                // re-blurs that read as a stutter at the end of every open. A single snap
                // costs one heavy frame and is barely visible on the dimmed backdrop.
                AndroidPanel.Effect = _androidPanelEffect;
                _androidPanelEffect = null;
            }

            _androidOpenAnimFinished = true;
            if (_pendingBackdropSource != null)
            {
                ApplyReadyBackdrop();
            }
        }

        /// <summary>
        /// Render the Android panel once, full-size, to a frozen bitmap and park the grow
        /// Image over the final panel rect. Playback stretches that single bitmap per frame
        /// with a RenderTransform — no layout, no BitmapCache, no live panel re-raster.
        /// </summary>
        private void RenderAndroidSnapshot()
        {
            _androidPanelSnapshot = null;
            double pw = _androidPanelWidth;
            double ph = _androidPanelHeight;
            if (pw < 8 || ph < 8) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            double renderScale = _dpiScaleX > 0 ? _dpiScaleX : 1.0;
            int fw = Math.Max(1, (int)Math.Round(pw * renderScale));
            int fh = Math.Max(1, (int)Math.Round(ph * renderScale));

            // Render the panel at identity (no grow transform) so the snapshot is full-size.
            // The panel is ARRANGED at its canvas margin inside the layer grid; RenderTargetBitmap
            // rasterizes a visual at its layout offset (VisualOffset), so a panel parked at
            // (332, 8) was drawn off the RTB's origin and clipped to a ~24% sliver — the folder
            // visibly jumped to a strip at the start of the close ("se tp") and stayed cut during
            // the whole shrink ("coupé"). Park the panel at the origin for the raster, then
            // restore it; all synchronous and off-screen, so no frame ever shows the moved panel.
            var savedTransform = AndroidPanel.RenderTransform;
            var savedMargin = AndroidPanel.Margin;
            var savedVisibility = AndroidPanel.Visibility;
            AndroidPanel.RenderTransform = null;
            AndroidPanel.Margin = new Thickness(0);
            // A superseded grow/close may have collapsed the panel; the snapshot must still
            // rasterize the real content. All synchronous and off-screen, no frame shows it.
            AndroidPanel.Visibility = Visibility.Visible;
            try
            {
                AndroidPanel.UpdateLayout();
                var rtb = new RenderTargetBitmap(fw, fh, 96 * renderScale, 96 * renderScale, PixelFormats.Pbgra32);
                rtb.Render(AndroidPanel);
                rtb.Freeze();
                _androidPanelSnapshot = rtb;
            }
            catch
            {
                AndroidPanel.RenderTransform = savedTransform;
                AndroidPanel.Margin = savedMargin;
                AndroidPanel.Visibility = savedVisibility;
                return;
            }
            AndroidPanel.RenderTransform = savedTransform;
            AndroidPanel.Margin = savedMargin;
            AndroidPanel.Visibility = savedVisibility;

            // Pre-filtered (Fant) downscaled copies for the close's mipmap swap. Generated
            // once here — one-time cost — so the per-frame close blit never minifies by more
            // than ~2x (no skipped-pixel shimmer).
            _androidSnapHalf = DownscaleSnapshot(_androidPanelSnapshot, 0.5);
            _androidSnapQuarter = DownscaleSnapshot(_androidPanelSnapshot, 0.25);
            _androidSnapEighth = DownscaleSnapshot(_androidPanelSnapshot, 0.125);

            // Park the Image at the final panel rect ONCE. Per frame only RenderTransform
            // changes, so the software renderer never re-runs Measure/Arrange mid-animation.
            AndroidGrowImage.Width = pw;
            AndroidGrowImage.Height = ph;
            AndroidGrowImage.Margin = new Thickness(_androidPanelRect.X, _androidPanelRect.Y, 0, 0);
            AndroidGrowImage.Source = _androidPanelSnapshot;
            AndroidGrowImage.Opacity = 1;
            sw.Stop();
            LogAndroidPerf($"grow snapshot rendered in {sw.Elapsed.TotalMilliseconds:F1}ms panel={pw:F0}x{ph:F0} renderScale={renderScale:F2}");
        }

        /// <summary>
        /// Render a high-quality downscaled copy of a snapshot using the Fant (area-average)
        /// filter. Fant averages the covered pixels instead of skipping them, so the source
        /// carries no aliasing-prone high-frequency detail for the cheap per-frame blit to
        /// reproduce. One-time cost; runs off the animation path.
        /// </summary>
        private static BitmapSource? DownscaleSnapshot(BitmapSource? source, double scale)
        {
            if (source == null) return null;
            try
            {
                int w = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
                int h = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
                var dv = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.Fant);
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawImage(source, new Rect(0, 0, w, h));
                }
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            catch { return null; }
        }

        private void StartAndroidGrow(int gen)
        {
            if (_androidPanelSnapshot == null)
            {
                FinishAndroidOpen(gen);
                return;
            }
            _androidGrowGen = gen;
            _androidGrowDurMs = _androidFolderVm?.AndroidAnimationDurationMs ?? 320;
            _androidGrowTargetOpacity = GetAndroidOpenOpacity(_androidFolderVm);
            // The live panel is collapsed — only the snapshot Image renders, so nothing can
            // invalidate or re-rasterize the panel mid-animation.
            AndroidPanel.Visibility = Visibility.Collapsed;
            _androidGrowTransform = new MatrixTransform();
            AndroidGrowImage.RenderTransform = _androidGrowTransform;
            AndroidGrowImage.Visibility = Visibility.Collapsed;
            _androidGrowSw = System.Diagnostics.Stopwatch.StartNew();
            CompositionTarget.Rendering += OnAndroidGrowRenderFrame;
        }

        private void OnAndroidGrowRenderFrame(object? sender, EventArgs e)
        {
            if (_androidGrowSw == null || _androidPanelSnapshot == null || _androidGrowTransform == null) return;
            if (!_androidFolderOpen || _androidGen != _androidGrowGen)
            {
                // The open was superseded (close/another open) — stop and let the new state own it.
                FinishAndroidGrow(_androidGrowGen);
                return;
            }
            double t = _androidGrowSw.Elapsed.TotalMilliseconds;
            if (t >= _androidGrowDurMs)
            {
                FinishAndroidGrow(_androidGrowGen);
                return;
            }
            double t01 = Math.Min(1.0, t / _androidGrowDurMs);
            double s, tx, ty;
            double op = _androidGrowTargetOpacity;
            switch (_androidGrowStyle)
            {
                case "Zoom":
                    // Scale 0.4→1.0 with a BackEase overshoot, fade completes by ~70%.
                    double z = _growZoomEase.Ease(t01);
                    s = 0.4 + 0.6 * z;
                    tx = _androidPanelCenter.X - (_androidPanelRect.Width * s) / 2 - _androidPanelRect.X;
                    ty = _androidPanelCenter.Y - (_androidPanelRect.Height * s) / 2 - _androidPanelRect.Y;
                    op *= SmoothOpenStep(t01 / 0.7);
                    break;
                case "SlideUp":
                    // Slide from 80px below into place; scale stays 1.
                    double u = _growSlideEase.Ease(t01);
                    s = 1;
                    tx = 0;
                    ty = 80 * (1 - u);
                    op *= SmoothOpenStep(t01);
                    break;
                case "Elastic":
                    // Scale 0.3→1.0 with an elastic overshoot, fade done by ~60%.
                    double el = _growElasticEase.Ease(t01);
                    s = 0.3 + 0.7 * el;
                    tx = _androidPanelCenter.X - (_androidPanelRect.Width * s) / 2 - _androidPanelRect.X;
                    ty = _androidPanelCenter.Y - (_androidPanelRect.Height * s) / 2 - _androidPanelRect.Y;
                    op *= SmoothOpenStep(t01 / 0.6);
                    break;
                case "Fade":
                    // Centered open: pure fade of the full-size snapshot, no transform.
                    s = 1;
                    tx = 0;
                    ty = 0;
                    op *= SmoothOpenStep(t01);
                    break;
                default: // "Scale" — keep the exact proven bloom (full opacity from frame 1)
                    double eased = _androidEase.Ease(t01);
                    s = _androidPanelStartScale + (1 - _androidPanelStartScale) * eased;
                    tx = _androidPanelCenter.X - (_androidPanelRect.Width * s) / 2 - _androidPanelRect.X;
                    ty = _androidPanelCenter.Y - (_androidPanelRect.Height * s) / 2 - _androidPanelRect.Y;
                    break;
            }
            _androidGrowTransform.Matrix = new Matrix(s, 0, 0, s, tx, ty);
            AndroidGrowImage.Opacity = op;
            AndroidGrowImage.Visibility = Visibility.Visible;
        }

        // Smoothstep so the fade eases to full opacity instead of being linear.
        private static double SmoothOpenStep(double x)
        {
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            return x * x * (3 - 2 * x);
        }

        private void FinishAndroidGrow(int gen)
        {
            // Always stop the grow (also when the open was superseded mid-animation).
            StopAndroidGrow();
            if (_androidGen != gen || !_androidFolderOpen) return;
            // Panel back on screen at identity — the final grow frame IS the identity
            // transform, so the swap to the real panel is seamless, then the settle runs.
            AndroidPanel.Visibility = Visibility.Visible;
            ResetAndroidTransforms(1.0, 0, 0);
            FinishAndroidOpen(gen);
        }

        private void StopAndroidGrow()
        {
            // Unsubscribe both the grow and the close shrink handlers (a no-op for whichever
            // is not subscribed) so a superseded close/open never leaves a stale subscriber.
            CompositionTarget.Rendering -= OnAndroidGrowRenderFrame;
            CompositionTarget.Rendering -= OnAndroidCloseRenderFrame;
            _androidGrowSw = null;
            _androidGrowTransform = null;
            _androidSnapHalf = null;
            _androidSnapQuarter = null;
            _androidSnapEighth = null;
            AndroidGrowImage.RenderTransform = null;
            AndroidGrowImage.Visibility = Visibility.Collapsed;
            AndroidGrowImage.Opacity = 1;
            AndroidGrowImage.Source = null;
        }

        /// <summary>
        /// Close the "Scale" animation with the same frozen-snapshot blit the open uses.
        /// The open was smooth because it stretched a pre-rendered bitmap per frame;
        /// the close animated the LIVE panel (CacheMode re-raster + DropShadow re-blur
        /// every frame), which measured 4 consecutive ~40ms frames during shrink — the
        /// "sacadé ça se tp" on the way back. This captures the settled panel once,
        /// collapses it, and drives the reverse shrink on the leaf Image.
        /// </summary>
        private void StartAndroidClose(ContainerViewModel? vm, int gen)
        {
            // Drop the shadow and cache BEFORE capturing so the snapshot is clean and the
            // RTB is not rendered through a half-res cached bitmap. FinalizeAndroidClose
            // re-attaches the shadow after the layer is hidden.
            if (_androidPanelEffect == null)
            {
                _androidPanelEffect = AndroidPanel.Effect;
                AndroidPanel.Effect = null;
            }
            AndroidPanel.CacheMode = null;
            RenderAndroidSnapshot();
            if (_androidPanelSnapshot == null)
            {
                FinalizeAndroidClose();
                return;
            }
            _androidGrowGen = gen;
            // Same duration as the open so the close is a true mirror, not a quick cut.
            _androidGrowDurMs = vm?.AndroidAnimationDurationMs ?? 320;
            AndroidPanel.Visibility = Visibility.Collapsed;
            _androidGrowTransform = new MatrixTransform();
            AndroidGrowImage.RenderTransform = _androidGrowTransform;
            AndroidGrowImage.Visibility = Visibility.Collapsed;
            _androidGrowSw = System.Diagnostics.Stopwatch.StartNew();
            CompositionTarget.Rendering += OnAndroidCloseRenderFrame;
        }

        private void OnAndroidCloseRenderFrame(object? sender, EventArgs e)
        {
            if (_androidGrowSw == null || _androidPanelSnapshot == null || _androidGrowTransform == null) return;
            if (_androidGen != _androidGrowGen)
            {
                // The close was superseded (another open/close bumped the gen) — stop the
                // shrink and let the new state own the layer.
                FinishAndroidClose(_androidGrowGen);
                return;
            }
            double t = _androidGrowSw.Elapsed.TotalMilliseconds;
            if (t >= _androidGrowDurMs)
            {
                FinishAndroidClose(_androidGrowGen);
                return;
            }
            double t01 = Math.Min(1.0, t / _androidGrowDurMs);
            // Time-reverse of the open bloom: the SAME curve evaluated at (1-t01), so the
            // close is the exact mirror — a pure shrink to the same tiny dot the open grew
            // from. No fade: dissolving while still large reads as "melting", not a shrink.
            double rev = 1.0 - t01;
            double eased = _androidEase.Ease(rev);
            double s = _androidPanelStartScale + (1 - _androidPanelStartScale) * eased;
            // Mipmap swap: keep the source just above the on-screen size so the cheap
            // LowQuality blit never minifies by much (the Fant pre-filter already removed
            // the aliasing-prone detail). The Image is parked at panel size with Stretch=Fill,
            // so swapping the source needs no layout change.
            BitmapSource? mip = s > 0.6 ? _androidPanelSnapshot : (s > 0.3 ? _androidSnapHalf : (s > 0.15 ? _androidSnapQuarter : _androidSnapEighth));
            if (mip != null && !ReferenceEquals(AndroidGrowImage.Source, mip))
                AndroidGrowImage.Source = mip;
            // Recede toward the tile: the shrink's center slides from the clamped panel
            // center to the tile center, so the dot lands exactly under the tile and the
            // tile fades in over it — no dot stranded in empty space (the "tp"). A folder
            // whose tile already sits at the panel center stays a pure in-place shrink.
            double cx = _androidPanelCenter.X;
            double cy = _androidPanelCenter.Y;
            if (_androidTileCenter is Point tc)
            {
                double k = 1.0 - eased; // eased 1→0, so the center glides panel→tile
                cx += (tc.X - cx) * k;
                cy += (tc.Y - cy) * k;
            }
            double tx = cx - (_androidPanelRect.Width * s) / 2 - _androidPanelRect.X;
            double ty = cy - (_androidPanelRect.Height * s) / 2 - _androidPanelRect.Y;
            _androidGrowTransform.Matrix = new Matrix(s, 0, 0, s, tx, ty);
            // Fade the dot out over the last ~45% (rev<0.45) so it melts into the tile
            // reveal instead of popping away. The tile re-show is delayed to start at
            // exactly this point (ScheduleAndroidTileReShow), so folder and tile cross-fade
            // at the same spot — no visual jump at the handoff.
            AndroidGrowImage.Opacity = Math.Min(1.0, rev / 0.45);
            AndroidGrowImage.Visibility = Visibility.Visible;
        }

        private void FinishAndroidClose(int gen)
        {
            StopAndroidGrow();
            _androidPanelSnapshot = null;
            if (_androidGen != gen) return;
            FinalizeAndroidClose();
        }

        private void AnimateAndroidClose(ContainerViewModel? vm, int gen)
        {
            double dur = vm?.AndroidAnimationDurationMs ?? 320;
            double targetOpacity = GetAndroidOpenOpacity(vm);

            // Only the capture-based backdrops (Blur/Darkening) get a closing fade. Panel
            // mode has no backdrop and the solid color veil is snap-on/snap-off — a
            // full-screen fade re-blends the whole window in software every frame (the
            // measured ~40ms/frame close lag).
            if (vm?.AndroidBackdropMode == "Panel" || vm?.AndroidBackdropStyle == "Color")
                AndroidBackdropRoot.Opacity = 0;
            else
                Animate(AndroidBackdropRoot, OpacityProperty, 1, 0, Math.Min(150, dur * 0.5), null);

            // Centered (not at click): mirror the open with a plain fade out, no scale.
            if (vm?.AndroidOpenAtClick == false)
            {
                if (_androidPanelEffect == null)
                {
                    _androidPanelEffect = AndroidPanel.Effect;
                    AndroidPanel.Effect = null;
                }
                Animate(AndroidPanel, OpacityProperty, targetOpacity, 0, dur * 0.6, new CubicEase { EasingMode = EasingMode.EaseIn }, () =>
                { if (_androidGen == gen) FinalizeAndroidClose(); });
                return;
            }

            // Every atClick close shrinks the frozen snapshot back to the tile — the exact
            // mirror of the open bloom. The live-panel + BitmapCache close was measured to
            // re-rasterize and flicker on non-Scale styles; the snapshot blit is proven
            // smooth for every style.
            StartAndroidClose(vm, gen);
        }

        private void AndroidPanelTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount < 2 || _androidFolderVm == null || !_androidFolderVm.AndroidShowHeader) return;
            BeginAndroidTitleRename();
            e.Handled = true;
        }

        private void BeginAndroidTitleRename()
        {
            var vm = _androidFolderVm;
            if (vm == null) return;
            AndroidTitleEditBox.Text = vm.Name;
            AndroidTitleEditBox.Visibility = Visibility.Visible;
            EnableWindowActivation();
            AndroidTitleEditBox.Focus();
            AndroidTitleEditBox.SelectAll();
            _isAndroidRenameActive = true;
            _activeRenameTextBox = AndroidTitleEditBox;
            _activeRenameCommitAction = CommitAndroidTitleRename;
        }

        private void CommitAndroidTitleRename()
        {
            if (!_isAndroidRenameActive) return;
            _isAndroidRenameActive = false;
            _activeRenameTextBox = null;
            _activeRenameCommitAction = null;
            DisableWindowActivation();
            AndroidTitleEditBox.Visibility = Visibility.Collapsed;

            var vm = _androidFolderVm;
            string newName = AndroidTitleEditBox.Text.Trim();
            if (vm != null && !string.IsNullOrEmpty(newName) && newName != vm.Name)
                vm.Name = newName;
        }

        private void CancelAndroidTitleRename()
        {
            if (!_isAndroidRenameActive) return;
            _isAndroidRenameActive = false;
            _activeRenameTextBox = null;
            _activeRenameCommitAction = null;
            DisableWindowActivation();
            AndroidTitleEditBox.Visibility = Visibility.Collapsed;
        }

        private void AndroidTitleEditBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitAndroidTitleRename();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelAndroidTitleRename();
            }
        }

        private void AndroidTitleEditBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitAndroidTitleRename();
        }

        private void AndroidBackdrop_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            CloseAndroidFolder();
        }

        // === Android folder open panel: selection, drag & reorder ===

        private void AndroidScroller_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            _androidIgnoreThisPress = false;

            // Let the scrollbar thumb keep its own drag behavior.
            var src = e.OriginalSource as DependencyObject;
            while (src != null && src != AndroidFolderScroller)
            {
                if (src is ScrollBar)
                {
                    _androidIgnoreThisPress = true;
                    return;
                }
                src = VisualTreeHelper.GetParent(src);
            }

            var item = FindAndroidFolderItem(e.OriginalSource);
            _androidCtrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (item != null)
            {
                if (_androidCtrlHeld)
                {
                    if (_androidSelectedItems.Contains(item)) _androidSelectedItems.Remove(item);
                    else _androidSelectedItems.Add(item);
                }
                else if (!_androidSelectedItems.Contains(item))
                {
                    _androidSelectedItems.Clear();
                    _androidSelectedItems.Add(item);
                }
                _androidDragSourceItem = item;
            }
            else
            {
                if (!_androidCtrlHeld) _androidSelectedItems.Clear();
                _androidDragSourceItem = null;
            }

            _androidDragStartLocal = e.GetPosition(AndroidIconsList);
            _isAndroidIconDrag = false;
            _isAndroidRectSelect = false;
            AndroidFolderScroller.CaptureMouse();
            UpdateAndroidSelectionVisual();
        }

        private void AndroidScroller_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            var pos = e.GetPosition(AndroidIconsList);
            double dx = pos.X - _androidDragStartLocal.X;
            double dy = pos.Y - _androidDragStartLocal.Y;
            double threshold = Math.Max(SystemParameters.MinimumHorizontalDragDistance, 4);

            if (!_isAndroidIconDrag && !_isAndroidRectSelect &&
                (Math.Abs(dx) > threshold || Math.Abs(dy) > threshold))
            {
                if (_androidDragSourceItem != null)
                {
                    _isAndroidIconDrag = true;
                    if (!_androidSelectedItems.Contains(_androidDragSourceItem))
                    {
                        _androidSelectedItems.Clear();
                        _androidSelectedItems.Add(_androidDragSourceItem);
                    }
                }
                else
                {
                    _isAndroidRectSelect = true;
                }
            }

            if (_isAndroidRectSelect)
            {
                UpdateAndroidRectSelect(pos);
            }
            else if (_isAndroidIconDrag)
            {
                // Only draw the in-panel insertion marker while still over the panel;
                // over the desktop the mouse hook updates the container markers instead.
                if (_androidPanelRect.Contains(e.GetPosition(AndroidFolderLayer)))
                    UpdateAndroidDragInsertionMarker(pos);
            }
        }

        private void AndroidScroller_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            AndroidFolderScroller.ReleaseMouseCapture();

            var posLocal = e.GetPosition(AndroidIconsList);

            if (_isAndroidRectSelect)
            {
                FinishAndroidRectSelect(posLocal);
                e.Handled = true;
            }
            else if (_isAndroidIconDrag)
            {
                FinishAndroidIconDrag(e.GetPosition(AndroidFolderLayer));
                e.Handled = true;
            }
            else if (_androidDragSourceItem != null && !_androidCtrlHeld)
            {
                // Plain click on an item → launch it (mouse is captured to the scroller,
                // so the item's own MouseLeftButtonUp never fires).
                e.Handled = true;
                ContainerControl.LaunchShortcut(_androidDragSourceItem);
                CloseAndroidFolder();
            }

            _isAndroidIconDrag = false;
            _isAndroidRectSelect = false;
            _androidDragSourceItem = null;
            _androidCtrlHeld = false;
            ClearAndroidSelectionRect();
            ClearAndroidInsertionMarker();
        }

        private ShortcutItem? FindAndroidFolderItem(object source)
        {
            var dep = source as DependencyObject;
            while (dep != null && dep != AndroidIconsList)
            {
                if (dep is FrameworkElement fe && fe.DataContext is ShortcutItem si)
                    return si;
                dep = VisualTreeHelper.GetParent(dep);
            }
            return null;
        }

        private void AndroidIcon_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ShortcutItem item) return;

            string menuPath = !string.IsNullOrEmpty(item.ShortcutPath) ? item.ShortcutPath : item.TargetPath;
            if (string.IsNullOrEmpty(menuPath)) return;

            var pt = e.GetPosition(AndroidFolderLayer);
            double screenX = (Left + pt.X) * _dpiScaleX;
            double screenY = (Top + pt.Y) * _dpiScaleY;

            _isContextMenuOpen = true;
            try
            {
                ContainerControl.ShellContextMenu.ShowMenu(_overlayHwnd, menuPath, (int)screenX, (int)screenY);
            }
            catch { }
            finally
            {
                _isContextMenuOpen = false;
            }

            ContainerManager.Instance.SyncDeletedShortcuts();
            ContainerManager.Instance.RefreshUnassignedShortcuts();
            e.Handled = true;
        }

        private void UpdateAndroidRectSelect(Point posLocal)
        {
            if (_androidSelRect == null)
            {
                _androidSelRect = new Rectangle
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(200, 99, 179, 255)),
                    StrokeThickness = 1,
                    Fill = new SolidColorBrush(Color.FromArgb(40, 99, 179, 255)),
                    IsHitTestVisible = false
                };
                AndroidSelectionCanvas.Children.Add(_androidSelRect);
            }
            double x = Math.Min(_androidDragStartLocal.X, posLocal.X);
            double y = Math.Min(_androidDragStartLocal.Y, posLocal.Y);
            Canvas.SetLeft(_androidSelRect, x);
            Canvas.SetTop(_androidSelRect, y);
            _androidSelRect.Width = Math.Abs(posLocal.X - _androidDragStartLocal.X);
            _androidSelRect.Height = Math.Abs(posLocal.Y - _androidDragStartLocal.Y);
        }

        private void FinishAndroidRectSelect(Point posLocal)
        {
            if (_androidFolderVm == null) return;

            var selRect = new Rect(
                Math.Min(_androidDragStartLocal.X, posLocal.X),
                Math.Min(_androidDragStartLocal.Y, posLocal.Y),
                Math.Abs(posLocal.X - _androidDragStartLocal.X),
                Math.Abs(posLocal.Y - _androidDragStartLocal.Y));

            if (selRect.Width < 4 && selRect.Height < 4)
            {
                UpdateAndroidSelectionVisual();
                return;
            }

            foreach (var item in _androidFolderVm.Shortcuts.ToList())
            {
                var container = AndroidIconsList.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
                if (container == null) continue;
                var tr = container.TransformToVisual(AndroidIconsList);
                var bounds = tr.TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
                var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                if (selRect.Contains(center))
                    _androidSelectedItems.Add(item);
            }
            UpdateAndroidSelectionVisual();
        }

        private void UpdateAndroidDragInsertionMarker(Point posLocal)
        {
            if (_androidFolderVm == null || AndroidSelectionCanvas == null) return;

            if (_androidInsertionMarker == null)
            {
                _androidInsertionMarker = new Rectangle
                {
                    Width = 2,
                    Height = 40,
                    Fill = Brushes.White,
                    RadiusX = 1,
                    RadiusY = 1,
                    IsHitTestVisible = false
                };
                AndroidSelectionCanvas.Children.Add(_androidInsertionMarker);
            }

            try
            {
                var items = new List<(ShortcutItem Item, Rect Bounds)>();
                for (int i = 0; i < _androidFolderVm.Shortcuts.Count; i++)
                {
                    var item = _androidFolderVm.Shortcuts[i];
                    if (_androidSelectedItems.Contains(item)) continue;

                    var container = AndroidIconsList.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
                    if (container != null && container.IsVisible)
                    {
                        var tr = container.TransformToVisual(AndroidSelectionCanvas);
                        var rectInControl = tr.TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
                        items.Add((item, rectInControl));
                    }
                }
                if (items.Count == 0) return;

                var rows = new List<List<(ShortcutItem Item, Rect Bounds)>>();
                foreach (var item in items.OrderBy(x => x.Bounds.Top).ThenBy(x => x.Bounds.Left))
                {
                    bool added = false;
                    foreach (var row in rows)
                    {
                        double avgTop = row.Average(r => r.Bounds.Top);
                        if (Math.Abs(item.Bounds.Top - avgTop) < 15.0)
                        {
                            row.Add(item);
                            added = true;
                            break;
                        }
                    }
                    if (!added) rows.Add(new List<(ShortcutItem Item, Rect Bounds)> { item });
                }

                List<(ShortcutItem Item, Rect Bounds)> closestRow = rows[0];
                double minRowDistance = double.MaxValue;
                foreach (var row in rows)
                {
                    double rowTop = row.Min(r => r.Bounds.Top);
                    double rowBottom = row.Max(r => r.Bounds.Bottom);
                    double distance = posLocal.Y < rowTop ? rowTop - posLocal.Y
                        : posLocal.Y > rowBottom ? posLocal.Y - rowBottom : 0;
                    if (distance < minRowDistance)
                    {
                        minRowDistance = distance;
                        closestRow = row;
                    }
                }

                var sortedRowItems = closestRow.OrderBy(r => r.Bounds.Left).ToList();
                int closestIdxInRow = 0;
                double minXDist = double.MaxValue;
                for (int i = 0; i < sortedRowItems.Count; i++)
                {
                    var b = sortedRowItems[i];
                    double centerX = b.Bounds.Left + b.Bounds.Width / 2.0;
                    double dist = Math.Abs(posLocal.X - centerX);
                    if (dist < minXDist)
                    {
                        minXDist = dist;
                        closestIdxInRow = i;
                    }
                }

                var targetItem = sortedRowItems[closestIdxInRow];
                double targetCenterX = targetItem.Bounds.Left + targetItem.Bounds.Width / 2.0;
                bool insertAfter = posLocal.X > targetCenterX;

                double markerX;
                if (insertAfter)
                {
                    if (closestIdxInRow < sortedRowItems.Count - 1)
                    {
                        var nextItem = sortedRowItems[closestIdxInRow + 1];
                        markerX = (targetItem.Bounds.Right + nextItem.Bounds.Left) / 2.0;
                    }
                    else markerX = targetItem.Bounds.Right + 4;
                }
                else
                {
                    if (closestIdxInRow > 0)
                    {
                        var prevItem = sortedRowItems[closestIdxInRow - 1];
                        markerX = (prevItem.Bounds.Right + targetItem.Bounds.Left) / 2.0;
                    }
                    else markerX = targetItem.Bounds.Left - 4;
                }

                markerX -= 1;
                if (markerX < 2) markerX = 2;

                Canvas.SetLeft(_androidInsertionMarker, markerX);
                Canvas.SetTop(_androidInsertionMarker, targetItem.Bounds.Top);
                _androidInsertionMarker.Height = targetItem.Bounds.Height;
                _androidInsertionMarker.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Android insertion marker error: {ex}");
            }
        }

        private void ClearAndroidSelectionRect()
        {
            if (_androidSelRect != null)
            {
                AndroidSelectionCanvas.Children.Remove(_androidSelRect);
                _androidSelRect = null;
            }
        }

        private void ClearAndroidInsertionMarker()
        {
            if (_androidInsertionMarker != null)
            {
                AndroidSelectionCanvas.Children.Remove(_androidInsertionMarker);
                _androidInsertionMarker = null;
            }
        }

        private void FinishAndroidIconDrag(Point canvasPt)
        {
            if (_androidFolderVm == null || _androidSelectedItems.Count == 0) return;
            var items = _androidSelectedItems.ToList();

            if (_androidPanelRect.Contains(canvasPt))
            {
                ReorderAndroidItems(canvasPt, items);
            }
            else
            {
                // Released outside the panel → delegate to the existing container-drag
                // system (move to another container / closed folder / unassigned).
                StartContainerDrag(items, _androidFolderVm);
                FinishContainerDrag(canvasPt);
            }
            _androidFolderVm.Save();
        }

        private void ReorderAndroidItems(Point canvasPt, List<ShortcutItem> items)
        {
            if (_androidFolderVm == null || _androidFolderVm.Shortcuts.Count == 0) return;

            var toList = AndroidFolderLayer.TransformToVisual(AndroidIconsList);
            Point localPt = toList.Transform(canvasPt);

            double bestDist = double.MaxValue;
            int bestIdx = -1;

            var remainingItems = new List<(ShortcutItem Item, int OriginalIndex)>();
            for (int i = 0; i < _androidFolderVm.Shortcuts.Count; i++)
            {
                var item = _androidFolderVm.Shortcuts[i];
                if (items.Contains(item)) continue;
                remainingItems.Add((item, i));
            }
            if (remainingItems.Count == 0) return;

            for (int i = 0; i < remainingItems.Count; i++)
            {
                var container = AndroidIconsList.ItemContainerGenerator.ContainerFromItem(remainingItems[i].Item) as FrameworkElement;
                if (container == null) continue;
                var tr = container.TransformToVisual(AndroidIconsList);
                var rectInControl = tr.TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
                Point center = new Point(rectInControl.X + rectInControl.Width / 2, rectInControl.Y + rectInControl.Height / 2);
                double dist = Math.Sqrt(Math.Pow(localPt.X - center.X, 2) + Math.Pow(localPt.Y - center.Y, 2));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = localPt.X > center.X ? remainingItems[i].OriginalIndex + 1 : remainingItems[i].OriginalIndex;
                }
            }
            if (bestIdx == -1) return;

            var itemsToReorder = items.Where(i => _androidFolderVm.Shortcuts.Contains(i)).ToList();
            foreach (var item in itemsToReorder)
            {
                int oldIdx = _androidFolderVm.Shortcuts.IndexOf(item);
                if (oldIdx < 0) continue;
                int newIdx = bestIdx;
                if (oldIdx < newIdx) newIdx--;
                newIdx = Math.Clamp(newIdx, 0, _androidFolderVm.Shortcuts.Count - 1);
                if (oldIdx != newIdx)
                    _androidFolderVm.Shortcuts.Move(oldIdx, newIdx);
            }
            _androidFolderVm.Save();
        }

        private void UpdateAndroidSelectionVisual()
        {
            if (_androidFolderVm == null) return;
            foreach (var item in _androidFolderVm.Shortcuts)
            {
                var container = AndroidIconsList.ItemContainerGenerator.ContainerFromItem(item);
                if (container == null) continue;
                var border = FindVisualChild<Border>(container, b => b.DataContext is ShortcutItem);
                if (border == null) continue;
                bool sel = _androidSelectedItems.Contains(item);
                if (sel)
                {
                    if (!_androidHighlighted.Contains(border))
                    {
                        _androidHighlighted.Add(border);
                        border.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x63, 0xB3, 0xFF));
                    }
                }
                else if (_androidHighlighted.Remove(border))
                {
                    border.ClearValue(Border.BackgroundProperty); // restore hover style trigger
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
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public int message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

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

        [DllImport("user32.dll", EntryPoint = "SetWindowsHookEx", SetLastError = true)]
        private static extern IntPtr SetWindowsHookExRaw(int idHook, IntPtr lpfn, IntPtr hmod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private void ActivateDesktopWindow()
        {
            IntPtr progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                SetForegroundWindow(progman);
            }
        }

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

            // 2. Scan and register user-defined shortcut hotkeys
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
            try
            {
                SaveNotesToDisk();
                SaveGadgetsToDisk();
            }
            catch (Exception ex)
            {
                App.Log(ex, "OnClosed_Saving");
            }

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

            // Custom template to prevent Windows/WPF default hover styling
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.Name = "border";
            borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));

            FrameworkElementFactory contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Button.HorizontalContentAlignmentProperty));
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, new TemplateBindingExtension(Button.VerticalContentAlignmentProperty));

            borderFactory.AppendChild(contentFactory);
            template.VisualTree = borderFactory;
            btnStyle.Setters.Add(new Setter(Button.TemplateProperty, template));

            var trigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            trigger.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x35, 0x40, 0x50))));
            trigger.Setters.Add(new Setter(Button.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC))));
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

            var btnAndroid = new Button { Content = "Android Folder", Style = btnStyle };
            btnAndroid.Click += (s, e) =>
            {
                CreateContainerRequested?.Invoke(rx, ry, rw, rh, SelectedContainerType.AndroidFolder);
                CancelDrawMenu();
            };
            stack.Children.Add(btnAndroid);

            _drawMenuPopup.Child = stack;

            // Position menu slightly offset from cursor so cursor starts inside/near the border
            double menuX = mousePos.X - 5;
            double menuY = mousePos.Y - 5;

            menuX = Math.Clamp(menuX, 0, Width - 170);
            menuY = Math.Clamp(menuY, 0, Height - 160);

            Canvas.SetLeft(_drawMenuPopup, menuX);
            Canvas.SetTop(_drawMenuPopup, menuY);
            Canvas.SetZIndex(_drawMenuPopup, 99999);

            _drawMenuPopup.MouseLeave += (s, e) => CancelDrawMenu();

            OverlayCanvas.Children.Add(_drawMenuPopup);
        }

        private void ShowMultiSelectMenu(Point mousePos)
        {
            CancelDrawMenu();
            var items = _selectedIcons.ToList();
            if (items.Count == 0) return;

            _drawMenuPopup = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(0, 3, 0, 3),
                Width = 200,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, Direction = 320, ShadowDepth = 3,
                    Opacity = 0.45, BlurRadius = 8
                }
            };

            var stack = new StackPanel();

            Style btnStyle = new Style(typeof(Button));
            btnStyle.Setters.Add(new Setter(Button.BackgroundProperty, Brushes.Transparent));
            btnStyle.Setters.Add(new Setter(Button.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE))));
            btnStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(0)));
            btnStyle.Setters.Add(new Setter(Button.HeightProperty, 28.0));
            btnStyle.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
            btnStyle.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(16, 0, 16, 0)));
            btnStyle.Setters.Add(new Setter(Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            btnStyle.Setters.Add(new Setter(Button.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            btnStyle.Setters.Add(new Setter(Button.FontSizeProperty, 12.0));
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.Name = "border";
            borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            FrameworkElementFactory contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Button.HorizontalContentAlignmentProperty));
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, new TemplateBindingExtension(Button.VerticalContentAlignmentProperty));
            borderFactory.AppendChild(contentFactory);
            template.VisualTree = borderFactory;
            btnStyle.Setters.Add(new Setter(Button.TemplateProperty, template));
            var trigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            trigger.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x35, 0x40, 0x50))));
            trigger.Setters.Add(new Setter(Button.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC))));
            btnStyle.Triggers.Add(trigger);

            var btnOpen = new Button { Content = $"Open all ({items.Count})", Style = btnStyle };
            btnOpen.Click += (_, _) =>
            {
                foreach (var item in items) LaunchItem(item);
                _selectedIcons.Clear();
                _selectedDeleteCount = 0;
                UpdateSelectionVisual();
            };
            stack.Children.Add(btnOpen);

            var btnContainer = new Button { Content = "Create container with selection", Style = btnStyle };
            btnContainer.Click += (_, _) =>
            {
                double cx = mousePos.X - OverlayOffsetX;
                double cy = mousePos.Y - OverlayOffsetY;
                CreateContainerWithIconsRequested?.Invoke(cx, cy, 300, 200, items);
                _selectedIcons.Clear();
                _selectedDeleteCount = 0;
            };
            stack.Children.Add(btnContainer);

            _drawMenuPopup.Child = stack;

            double menuX = Math.Clamp(mousePos.X - 5, 0, Width - 200);
            double menuY = Math.Clamp(mousePos.Y - 5, 0, Height - 80);
            Canvas.SetLeft(_drawMenuPopup, menuX);
            Canvas.SetTop(_drawMenuPopup, menuY);
            Canvas.SetZIndex(_drawMenuPopup, 99999);

            _drawMenuPopup.MouseLeave += (_, _) =>
            {
                CancelDrawMenu();
            };

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
