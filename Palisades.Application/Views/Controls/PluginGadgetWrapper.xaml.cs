using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Palisades.Models;
using Palisades.Services;

namespace Palisades.Views.Controls
{
    public partial class PluginGadgetWrapper : UserControl
    {
        private Point _captureStart;
        private double _startLeft, _startTop, _startW, _startH;
        private bool _isDragging, _isResizing;

        public PluginGadgetItem GadgetItem { get; }

        public PluginGadgetWrapper(PluginGadgetItem item, FrameworkElement childView)
        {
            InitializeComponent();
            GadgetItem = item;
            DataContext = item;
            ChildContainer.Child = childView;

            Width = item.Width;
            Height = item.Height;

            ApplyCustomSettingsToChild();

            GadgetItem.PropertyChanged += GadgetItem_PropertyChanged;
            Unloaded += (s, e) => GadgetItem.PropertyChanged -= GadgetItem_PropertyChanged;
        }

        private void GadgetItem_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PluginGadgetItem.CustomData))
            {
                ApplyCustomSettingsToChild();
            }
        }

        private void UpdatePosition()
        {
            GadgetItem.X = Canvas.GetLeft(this);
            GadgetItem.Y = Canvas.GetTop(this);
        }

        private void UpdateSize()
        {
            GadgetItem.Width = Width;
            GadgetItem.Height = Height;
        }

        private bool IsResizeHandleVisible()
        {
            var def = ContainerManager.Instance.LoadDefaults();
            return def?.ShowResizeHandle ?? true;
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonDown(e);

            var pos = e.GetPosition(this);

            // Resize handle hit test (always allow resizing, only gripper is visually hidden)
            double rhLeft = Width - 12, rhTop = Height - 12;
            if (pos.X >= rhLeft && pos.Y >= rhTop)
            {
                _isResizing = true;
                _isDragging = false;
                _startW = Width;
                _startH = Height;
                _captureStart = pos;
                CaptureMouse();
                e.Handled = true;
                return;
            }

            // Header drag hit test (exclude delete button area)
            if (pos.Y <= 28 && pos.X < Width - 30 && e.ClickCount == 1)
            {
                _isDragging = true;
                _isResizing = false;
                var canvas = FindParentCanvas();
                if (canvas != null)
                {
                    _captureStart = e.GetPosition(canvas);
                    _startLeft = Canvas.GetLeft(this);
                    _startTop = Canvas.GetTop(this);
                }
                CaptureMouse();
                e.Handled = false;
                return;
            }
        }

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);

            if (_isDragging)
            {
                var canvas = FindParentCanvas();
                if (canvas == null) return;
                var pt = e.GetPosition(canvas);
                double dx = pt.X - _captureStart.X;
                double dy = pt.Y - _captureStart.Y;
                Canvas.SetLeft(this, Math.Max(0, _startLeft + dx));
                Canvas.SetTop(this, Math.Max(0, _startTop + dy));
                UpdatePosition();
            }
            else if (_isResizing)
            {
                var pt = e.GetPosition(this);
                double dw = pt.X - _captureStart.X;
                double dh = pt.Y - _captureStart.Y;
                Width = Math.Max(120, _startW + dw);
                Height = Math.Max(80, _startH + dh);
                UpdateSize();
            }
        }

        protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonUp(e);

            if (_isDragging || _isResizing)
            {
                _isDragging = false;
                _isResizing = false;
                ReleaseMouseCapture();
                UpdatePosition();
                UpdateSize();

                var overlay = Window.GetWindow(this) as DesktopOverlayWindow;
                overlay?.SaveGadgetsToDisk();
            }
        }

        private Canvas? FindParentCanvas()
        {
            var p = VisualTreeHelper.GetParent(this);
            while (p != null && p is not Canvas)
                p = VisualTreeHelper.GetParent(p);
            return p as Canvas;
        }

        private void ApplyCustomSettingsToChild()
        {
            if (ChildContainer.Child is Palisades.Plugins.ICustomizableGadgetView customizable)
            {
                customizable.ApplyCustomSettings(GadgetItem.CustomData);
            }
        }

        private void SaveGadgetSettings()
        {
            var overlay = Window.GetWindow(this) as DesktopOverlayWindow;
            overlay?.SaveGadgetsToDisk();
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e);
            e.Handled = true; // prevent bubbling up to desktop icon selection or desktop context menu

            var menu = CreateContextMenu();
            menu.IsOpen = true;
        }

        private ContextMenu CreateContextMenu()
        {
            var menu = new ContextMenu();

            // 1. Rename Option
            var renameItem = new MenuItem { Header = "Rename Widget" };
            renameItem.Click += (s, e) =>
            {
                TitleEditBox.Text = GadgetItem.Title;
                TitleBlock.Visibility = Visibility.Collapsed;
                TitleEditBox.Visibility = Visibility.Visible;
                TitleEditBox.Focus();
                TitleEditBox.SelectAll();
            };
            menu.Items.Add(renameItem);

            // 2. Toggle Header
            var headerItem = new MenuItem { Header = "Show Header Bar", IsCheckable = true, IsChecked = !GadgetItem.HideHeader };
            headerItem.Click += (s, e) =>
            {
                GadgetItem.HideHeader = !GadgetItem.HideHeader;
                SaveGadgetSettings();
            };
            menu.Items.Add(headerItem);

            // 3. Customize Submenu (dynamic depending on GadgetType)
            if (GadgetItem.GadgetType.Equals("Clock", StringComparison.OrdinalIgnoreCase))
            {
                menu.Items.Add(new Separator());
                var customizeItem = new MenuItem { Header = "Clock Settings..." };
                BuildClockCustomMenu(customizeItem);
                menu.Items.Add(customizeItem);
            }
            else if (GadgetItem.GadgetType.Equals("SystemMonitor", StringComparison.OrdinalIgnoreCase))
            {
                menu.Items.Add(new Separator());
                var customizeItem = new MenuItem { Header = "Monitor Settings..." };
                BuildSysMonCustomMenu(customizeItem);
                menu.Items.Add(customizeItem);
            }

            menu.Items.Add(new Separator());

            // 4. Delete Option
            var deleteItem = new MenuItem { Header = "Delete Widget" };
            deleteItem.Click += (s, e) =>
            {
                var overlay = Window.GetWindow(this) as DesktopOverlayWindow;
                overlay?.RemoveGadget(GadgetItem.Id);
            };
            menu.Items.Add(deleteItem);

            return menu;
        }

        private class ClockSettings
        {
            public bool ShowSeconds { get; set; } = true;
            public bool Is24Hour { get; set; } = true;
            public string Color { get; set; } = "#7DD3FC";
            public double FontSize { get; set; } = 36;
        }

        private class SysMonSettings
        {
            public bool ShowCpu { get; set; } = true;
            public bool ShowRam { get; set; } = true;
            public double Interval { get; set; } = 1.5;
        }

        private void BuildClockCustomMenu(MenuItem parent)
        {
            var settings = GetClockSettings();

            // Show Seconds
            var secondsItem = new MenuItem { Header = "Show Seconds", IsCheckable = true, IsChecked = settings.ShowSeconds };
            secondsItem.Click += (s, e) =>
            {
                settings.ShowSeconds = !settings.ShowSeconds;
                SaveClockSettings(settings);
            };
            parent.Items.Add(secondsItem);

            // 24-Hour Format
            var formatItem = new MenuItem { Header = "24-Hour Format", IsCheckable = true, IsChecked = settings.Is24Hour };
            formatItem.Click += (s, e) =>
            {
                settings.Is24Hour = !settings.Is24Hour;
                SaveClockSettings(settings);
            };
            parent.Items.Add(formatItem);

            // Text Color submenu
            var colorMenu = new MenuItem { Header = "Clock Color" };
            string[] colors = { "Ice Blue", "White", "Matrix Green", "Amber Orange", "Cyber Red" };
            string[] hexCodes = { "#7DD3FC", "#FFFFFF", "#4AF626", "#FFB000", "#FF3E3E" };
            for (int i = 0; i < colors.Length; i++)
            {
                string code = hexCodes[i];
                var colItem = new MenuItem { Header = colors[i], IsCheckable = true, IsChecked = settings.Color.Equals(code, StringComparison.OrdinalIgnoreCase) };
                colItem.Click += (s, e) =>
                {
                    settings.Color = code;
                    SaveClockSettings(settings);
                };
                colorMenu.Items.Add(colItem);
            }
            parent.Items.Add(colorMenu);

            // Font Size submenu
            var sizeMenu = new MenuItem { Header = "Font Size" };
            double[] sizes = { 24, 36, 48, 64 };
            string[] sizeNames = { "Small (24pt)", "Medium (36pt)", "Large (48pt)", "Huge (64pt)" };
            for (int i = 0; i < sizes.Length; i++)
            {
                double sz = sizes[i];
                var szItem = new MenuItem { Header = sizeNames[i], IsCheckable = true, IsChecked = Math.Abs(settings.FontSize - sz) < 0.1 };
                szItem.Click += (s, e) =>
                {
                    settings.FontSize = sz;
                    SaveClockSettings(settings);
                };
                sizeMenu.Items.Add(szItem);
            }
            parent.Items.Add(sizeMenu);
        }

        private ClockSettings GetClockSettings()
        {
            try
            {
                if (!string.IsNullOrEmpty(GadgetItem.CustomData))
                {
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<ClockSettings>(GadgetItem.CustomData) ?? new ClockSettings();
                }
            }
            catch { }
            return new ClockSettings();
        }

        private void SaveClockSettings(ClockSettings settings)
        {
            GadgetItem.CustomData = Newtonsoft.Json.JsonConvert.SerializeObject(settings);
            ApplyCustomSettingsToChild();
            SaveGadgetSettings();
        }

        private void BuildSysMonCustomMenu(MenuItem parent)
        {
            var settings = GetSysMonSettings();

            // Show CPU
            var cpuItem = new MenuItem { Header = "Show CPU Utilization", IsCheckable = true, IsChecked = settings.ShowCpu };
            cpuItem.Click += (s, e) =>
            {
                settings.ShowCpu = !settings.ShowCpu;
                SaveSysMonSettings(settings);
            };
            parent.Items.Add(cpuItem);

            // Show RAM
            var ramItem = new MenuItem { Header = "Show Memory Utilization", IsCheckable = true, IsChecked = settings.ShowRam };
            ramItem.Click += (s, e) =>
            {
                settings.ShowRam = !settings.ShowRam;
                SaveSysMonSettings(settings);
            };
            parent.Items.Add(ramItem);

            // Refresh Interval
            var intervalMenu = new MenuItem { Header = "Refresh Rate" };
            double[] rates = { 0.5, 1.0, 1.5, 2.0, 5.0 };
            string[] rateNames = { "Fast (0.5s)", "Normal (1.0s)", "Medium (1.5s)", "Slow (2.0s)", "Very Slow (5.0s)" };
            for (int i = 0; i < rates.Length; i++)
            {
                double r = rates[i];
                var rateItem = new MenuItem { Header = rateNames[i], IsCheckable = true, IsChecked = Math.Abs(settings.Interval - r) < 0.1 };
                rateItem.Click += (s, e) =>
                {
                    settings.Interval = r;
                    SaveSysMonSettings(settings);
                };
                intervalMenu.Items.Add(rateItem);
            }
            parent.Items.Add(intervalMenu);
        }

        private SysMonSettings GetSysMonSettings()
        {
            try
            {
                if (!string.IsNullOrEmpty(GadgetItem.CustomData))
                {
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<SysMonSettings>(GadgetItem.CustomData) ?? new SysMonSettings();
                }
            }
            catch { }
            return new SysMonSettings();
        }

        private void SaveSysMonSettings(SysMonSettings settings)
        {
            GadgetItem.CustomData = Newtonsoft.Json.JsonConvert.SerializeObject(settings);
            ApplyCustomSettingsToChild();
            SaveGadgetSettings();
        }

        // Renaming Title
        private void TitleBlock_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                TitleEditBox.Text = GadgetItem.Title;
                TitleBlock.Visibility = Visibility.Collapsed;
                TitleEditBox.Visibility = Visibility.Visible;
                TitleEditBox.Focus();
                TitleEditBox.SelectAll();
                e.Handled = true;
            }
        }

        private void TitleEditBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitTitleEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelTitleEdit();
                e.Handled = true;
            }
        }

        private void CommitTitleEdit()
        {
            if (!string.IsNullOrWhiteSpace(TitleEditBox.Text))
            {
                GadgetItem.Title = TitleEditBox.Text.Trim();
                TitleBlock.Text = GadgetItem.Title;
                var overlay = Window.GetWindow(this) as DesktopOverlayWindow;
                overlay?.SaveGadgetsToDisk();
            }
            TitleEditBox.Visibility = Visibility.Collapsed;
            TitleBlock.Visibility = Visibility.Visible;
        }

        private void CancelTitleEdit()
        {
            TitleEditBox.Visibility = Visibility.Collapsed;
            TitleBlock.Visibility = Visibility.Visible;
        }

        private void TitleEditBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitTitleEdit();
        }

        private void TitleEditBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Focus helper
        }

        // Delete Gadget
        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var overlay = Window.GetWindow(this) as DesktopOverlayWindow;
            overlay?.RemoveGadget(GadgetItem.Id);
        }
    }
}
