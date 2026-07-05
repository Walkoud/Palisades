using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json;
using Palisades.Models;
using Palisades.Services;
using Palisades.ViewModels;

namespace Palisades.Views
{
    public partial class ContainerWindow : Window
    {
        private readonly ContainerViewModel _vm = null!;
        private bool _isResizing;
        private string _resizeDirection = "";
        private Point _resizeStartPoint;
        private Rect _resizeStartRect;
        private bool _windowReady;
        private HwndSource? _hwndSource;
        private RectangleGeometry? _clipGeometry; // cached, reused per frame

        public ContainerWindow(ContainerViewModel viewModel)
        {
            try
            {
                InitializeComponent();
                _vm = viewModel;
                DataContext = viewModel;

                viewModel.PropertyChanged += (_, e) =>
                {
                    try
                    {
                        if (e.PropertyName == nameof(ContainerViewModel.CurrentOpacity))
                            UpdateWindowAlpha();

                        if (e.PropertyName is nameof(ContainerViewModel.FilterEnabled)
                            or nameof(ContainerViewModel.FilterType)
                            or nameof(ContainerViewModel.FilterPattern))
                        {
                            if (Resources["ShortcutsView"] is CollectionViewSource cvs)
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

                        if (e.PropertyName == nameof(ContainerViewModel.ContainerThemeName))
                            UpdateContainerTheme();
                    }
                    catch { }
                };

                viewModel.RequestCreateShortcut += OnRequestCreateShortcut;

                SourceInitialized += OnSourceInitialized;
                Loaded += OnLoaded;
                SizeChanged += (_, _) => UpdateClip();
                Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error creating container window: {ex.Message}", "Palisades Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            if (Resources["ShortcutsView"] is CollectionViewSource cvs && cvs.View != null)
                cvs.View.Filter = FilterShortcut;

            // Set initial chevron rotation if visually collapsed on load
            if (_vm.IsVisuallyCollapsed && ChevronPath?.RenderTransform is RotateTransform rt)
                rt.Angle = 180;

            UpdateClip();
            UpdateContainerTheme();
        }

        private bool FilterShortcut(object obj)
        {
            if (obj is not ShortcutItem shortcut) return false;

            var model = _vm.Model;
            if (!model.FilterEnabled) return true;

            bool passesTypeFilter = model.FilterType switch
            {
                "Programs" => IsProgram(shortcut),
                "Documents" => IsDocument(shortcut),
                "Folders" => IsFolder(shortcut),
                "Custom" => MatchesCustom(shortcut, model.FilterPattern),
                _ => true
            };

            if (!passesTypeFilter) return false;

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
            return ext is ".exe" or ".lnk" or ".url" or ".bat" or ".cmd" or ".ps1" or ".msi" or ".appref-ms";
        }

        private static bool IsDocument(ShortcutItem s)
        {
            var ext = Path.GetExtension(s.TargetPath)?.ToLowerInvariant();
            return ext is ".doc" or ".docx" or ".pdf" or ".txt" or ".xls" or ".xlsx"
                or ".ppt" or ".pptx" or ".odt" or ".ods" or ".odp" or ".rtf"
                or ".csv" or ".md" or ".json" or ".xml";
        }

        private static bool IsFolder(ShortcutItem s)
        {
            try { return File.GetAttributes(s.TargetPath).HasFlag(FileAttributes.Directory); }
            catch { return false; }
        }

        private static bool MatchesCustom(ShortcutItem s, string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return true;
            bool isExclude = pattern.StartsWith("!");
            string p = isExclude ? pattern[1..] : pattern;
            bool match = s.Name.Contains(p, StringComparison.OrdinalIgnoreCase);
            return isExclude ? !match : match;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            if (_windowReady) return;
            _windowReady = true;

            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                _hwndSource = HwndSource.FromHwnd(hwnd);
                _hwndSource?.AddHook(WndProc);

                // Embed in desktop so Win+D doesn't affect it
                // Temporarily disabled to debug visibility — containers should show as normal windows
                // DesktopService.EmbedInDesktop(hwnd);

                // Remove from Alt+Tab and prevent activation/deactivation
                const int GWL_EXSTYLE = -20;
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_NOACTIVATE = 0x08000000;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

                // Keep at bottom of Z-order so we stay behind all app windows
                SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

                // Apply initial transparency
                UpdateWindowAlpha();

                // Apply Mica backdrop effect (Windows 11) - graceful fallback if unavailable
                EnableMica(hwnd);
            }
            catch { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        private static void EnableMica(IntPtr hwnd)
        {
            try
            {
                // DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMSBT_MAINWINDOW = 2 (Mica)
                int backdropType = 2;
                DwmSetWindowAttribute(hwnd, 38, ref backdropType, sizeof(int));

                // Use dark mode for the window backdrop
                int useDarkMode = 1;
                DwmSetWindowAttribute(hwnd, 20, ref useDarkMode, sizeof(int));
            }
            catch
            {
                // Mica not supported (pre-Windows 11) - silently ignore
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_WINDOWPOSCHANGING = 0x0046;
            const int WM_WINDOWPOSCHANGED = 0x0047;
            const int WM_SHOWWINDOW = 0x0018;
            const int WM_SYSCOMMAND = 0x0112;
            const int WM_ACTIVATEAPP = 0x001C;
            const int SC_MINIMIZE = 0xF020;
            const int SC_SHOWDESKTOP = 0xF070;

            switch (msg)
            {
                case WM_WINDOWPOSCHANGING:
                {
                    var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                    if ((wp.flags & SWP_HIDEWINDOW) != 0)
                    {
                        // Remove the hide flag so Show Desktop can't hide us
                        wp.flags &= ~SWP_HIDEWINDOW;
                        Marshal.StructureToPtr(wp, lParam, true);
                    }
                    break;
                }
                case WM_WINDOWPOSCHANGED:
                {
                    // After any Z-order change, reinforce HWND_BOTTOM so we stay behind apps
                    var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                    if ((wp.flags & SWP_NOZORDER) == 0 && wp.hwndInsertAfter != HWND_BOTTOM)
                    {
                        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    }
                    break;
                }
                case WM_SHOWWINDOW:
                    if (wParam == IntPtr.Zero)
                        handled = true;
                    break;
                case WM_SYSCOMMAND:
                {
                    int cmd = wParam.ToInt32() & 0xFFF0;
                    if (cmd == SC_MINIMIZE || cmd == SC_SHOWDESKTOP)
                        handled = true;
                    break;
                }
                case WM_ACTIVATEAPP:
                    // Prevent Win+D from deactivating our window
                    if (wParam == IntPtr.Zero)
                        handled = true;
                    break;
            }
            return IntPtr.Zero;
        }

        private const int SWP_HIDEWINDOW = 0x0080;
        private static readonly IntPtr HWND_BOTTOM = new(1);

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

        private void UpdateWindowAlpha()
        {
            if (!_windowReady) return;
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
            if (ch <= 0) ch = 1;
            Rect r = new Rect(0, 0, MainBorder.ActualWidth, ch);
            if (_clipGeometry == null)
                _clipGeometry = new RectangleGeometry(r, cr, cr);
            else
                _clipGeometry.Rect = r;
            MainBorder.Clip = _clipGeometry;
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

                dict["ContainerBackgroundBrush"] = new SolidColorBrush(bodyColor);
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


        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOZORDER = 0x0004;

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_vm.IsLocked) return;
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                ReleaseCapture();
                SendMessage(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            }
            catch { }
        }

        private void HeaderBar_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_vm.TitleHoverEffect)
                _vm.IsTitleHovered = true;
        }

        private void HeaderBar_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _vm.IsTitleHovered = false;
        }

        private void MainBorder_RightClick(object sender, MouseButtonEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SendMessage(hwnd, WM_CONTEXTMENU, IntPtr.Zero, 0);
        }

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 0x0002;
        private const int WM_CONTEXTMENU = 0x007B;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        private void MainBorder_MouseEnter(object sender, MouseEventArgs e)
        {
            _vm.IsHovered = true;
            ResetAutoLockTimer();
        }

        private void MainBorder_MouseLeave(object sender, MouseEventArgs e)
        {
            _vm.IsHovered = false;
        }

        private void MainBorder_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            try
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (var file in files)
                {
                    var extension = System.IO.Path.GetExtension(file)?.ToLower();
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

        public void ClearSelection()
        {
            try
            {
                if (_vm != null)
                    _vm.SelectedShortcut = null;
            }
            catch { }
        }

        private void ContainerBody_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                var overlay = System.Windows.Application.Current.Windows.OfType<DesktopOverlayWindow>().FirstOrDefault();
                overlay?.ClearAllContainerSelections();
                overlay?.ClearOverlayIconSelection();
            }
        }

        // Right-click on a shortcut → show the Windows original context menu
        private void Shortcut_OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is ShortcutItem item)
            {
                if (e.ChangedButton == MouseButton.Right)
                {
                    e.Handled = true;
                    ShowWindowsContextMenu(item);
                    return;
                }

                if (e.ClickCount == 2)
                {
                    LaunchShortcut(item);
                    return;
                }
                _vm.SelectedShortcut = item;
            }
        }

        private void ShowWindowsContextMenu(ShortcutItem item)
        {
            try
            {
                string? target = item.ShortcutPath;
                if (string.IsNullOrEmpty(target) || !File.Exists(target))
                {
                    target = item.IsUrl ? item.UrlTarget : item.TargetPath;
                    if (string.IsNullOrEmpty(target) || (!File.Exists(target) && !Directory.Exists(target)))
                        return;
                }

                var hwnd = new WindowInteropHelper(this).Handle;
                var pt = GetMouseScreenPoint();

                // Use ShellExecute to show the context menu via Windows shell
                string? verb = Palisades.Views.Controls.ContainerControl.ShellContextMenu.ShowMenu(hwnd, target, (int)pt.X, (int)pt.Y, false, true);
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
                            if (File.Exists(target))
                                File.Delete(target);
                            else if (Directory.Exists(target))
                                Directory.Delete(target, true);
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
            catch { }
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

        #region Resize Handling

        private void Resize_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_vm.IsLocked) return;

            _vm.NotifyResizeStarted();

            _isResizing = true;
            _resizeStartPoint = e.GetPosition(this);
            _resizeStartRect = new Rect(Left, Top, Width, Height);

            if (sender == ResizeLeft) _resizeDirection = "Left";
            else if (sender == ResizeRight) _resizeDirection = "Right";
            else if (sender == ResizeTop) _resizeDirection = "Top";
            else if (sender == ResizeBottom) _resizeDirection = "Bottom";
            else if (sender == ResizeTopLeft) _resizeDirection = "TopLeft";
            else if (sender == ResizeTopRight) _resizeDirection = "TopRight";
            else if (sender == ResizeBottomLeft) _resizeDirection = "BottomLeft";
            else if (sender == ResizeBottomRight) _resizeDirection = "BottomRight";

            Mouse.Capture(sender as UIElement);
        }

        private void Resize_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isResizing) return;

            try
            {
                var currentPos = e.GetPosition(this);
                double dx = currentPos.X - _resizeStartPoint.X;
                double dy = currentPos.Y - _resizeStartPoint.Y;

                double newX = _resizeStartRect.X;
                double newY = _resizeStartRect.Y;
                double newW = _resizeStartRect.Width;
                double newH = _resizeStartRect.Height;

                const double minW = 200, minH = 100;

                if (_resizeDirection.Contains("Left"))
                {
                    double possibleW = newW - dx;
                    if (possibleW >= minW)
                    {
                        newX = _resizeStartRect.X + dx;
                        newW = possibleW;
                    }
                    else
                    {
                        newX = _resizeStartRect.X + (_resizeStartRect.Width - minW);
                        newW = minW;
                    }
                }
                if (_resizeDirection.Contains("Right"))
                {
                    newW = Math.Max(minW, _resizeStartRect.Width + dx);
                }
                if (_resizeDirection.Contains("Top"))
                {
                    double possibleH = newH - dy;
                    if (possibleH >= minH)
                    {
                        newY = _resizeStartRect.Y + dy;
                        newH = possibleH;
                    }
                    else
                    {
                        newY = _resizeStartRect.Y + (_resizeStartRect.Height - minH);
                        newH = minH;
                    }
                }
                if (_resizeDirection.Contains("Bottom"))
                {
                    newH = Math.Max(minH, _resizeStartRect.Height + dy);
                }

                Left = Math.Max(0, newX);
                Top = Math.Max(0, newY);
                Width = newW;
                Height = newH;
            }
            catch { }
        }

        private void Resize_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isResizing) return;

            _isResizing = false;
            Mouse.Capture(null);

            try
            {
                _vm.X = Left;
                _vm.Y = Top;
                _vm.Width = Width;
                _vm.Height = Height;

                ContainerManager.Instance.ResolveCollisions(_vm.Model);

                var model = ContainerManager.Instance.GetContainer(_vm.Identifier);
                if (model != null)
                {
                    Left = model.X;
                    Top = model.Y;
                }
            }
            catch { }
        }

        #endregion

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            _vm.NotifyPositionChanged();
        }

        #region iTop-like Header: Search, Hamburger, Title

        private DispatcherTimer? _clickTimer;
        private DispatcherTimer? _searchIdleTimer;

        private bool _activationEnabled;

        private void EnableWindowActivation()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
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
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                const int GWL_EXSTYLE = -20;
                const int WS_EX_NOACTIVATE = 0x08000000;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
                _activationEnabled = false;
            }
            catch { }
        }

        private void TitleTextBlock_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            if (e.ClickCount == 2)
            {
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
                CancelSearchIdleTimer();
                _vm.SearchQuery = string.Empty;
                _vm.IsSearchActive = false;
                DisableWindowActivation();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter)
            {
                CancelSearchIdleTimer();
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
            if (sender is Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.IsOpen = true;
            }
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            _vm.IsSearchActive = true;
            _vm.SearchQuery = string.Empty;
            EnableWindowActivation();
            this.Activate();
            StartSearchIdleTimer();
            Dispatcher.BeginInvoke(
                new Action(() => SearchBox?.Focus()),
                DispatcherPriority.Input);
        }

        private void StartSearchIdleTimer()
        {
            _searchIdleTimer?.Stop();
            _searchIdleTimer = new DispatcherTimer
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
            if (sender is MenuItem item && item.CommandParameter is string param)
            {
                switch (param)
                {
                    case "View":
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
                        _vm.ShowCounter = !_vm.ShowCounter;
                        break;
                    case "Rules":
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
                var ext = Path.GetExtension(dialog.FileName)?.ToLowerInvariant();
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

        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        #region Private Box Unlock

        private DispatcherTimer? _autoLockTimer;

        private void ResetAutoLockTimer()
        {
            _autoLockTimer?.Stop();
            if (_vm.PrivateBoxAutoLockSeconds <= 0 || _vm.IsPasswordLocked) return;
            _autoLockTimer = new DispatcherTimer
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
                    UnlockErrorText.Text = "Decryption failed.";
                    return;
                }

                var shortcuts = JsonConvert.DeserializeObject<System.Collections.ObjectModel.ObservableCollection<ShortcutItem>>(json);
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
    }
}
