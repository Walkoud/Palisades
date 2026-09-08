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

            if (GadgetItem.IsLocked) return;

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
            var tr = TranslationService.Instance;
            var menu = new ContextMenu();

            // 1. Rename Option
            var renameItem = new MenuItem { Header = tr["Widget_Ctx_RenameWidget"] };
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
            var headerItem = new MenuItem { Header = tr["Widget_Ctx_ShowHeaderBar"], IsCheckable = true, IsChecked = !GadgetItem.HideHeader };
            headerItem.Click += (s, e) =>
            {
                GadgetItem.HideHeader = !GadgetItem.HideHeader;
                SaveGadgetSettings();
            };
            menu.Items.Add(headerItem);

            // 3. Lock/Unlock Widget
            var lockItem = new MenuItem { Header = GadgetItem.IsLocked ? tr["Widget_Ctx_UnlockWidget"] : tr["Widget_Ctx_LockWidget"] };
            lockItem.Click += (s, e) =>
            {
                GadgetItem.IsLocked = !GadgetItem.IsLocked;
                SaveGadgetSettings();
            };
            menu.Items.Add(lockItem);

            // 3. Customize Submenu (dynamic depending on GadgetType)
            if (GadgetItem.GadgetType.Equals("Clock", StringComparison.OrdinalIgnoreCase))
            {
                menu.Items.Add(new Separator());
                var customizeItem = new MenuItem { Header = tr["Widget_Ctx_ClockSettings"] };
                BuildClockCustomMenu(customizeItem);
                menu.Items.Add(customizeItem);
            }
            else if (GadgetItem.GadgetType.Equals("SystemMonitor", StringComparison.OrdinalIgnoreCase))
            {
                menu.Items.Add(new Separator());
                var customizeItem = new MenuItem { Header = tr["Widget_Ctx_MonitorSettings"] };
                BuildSysMonCustomMenu(customizeItem);
                menu.Items.Add(customizeItem);
            }
            else if (GadgetItem.GadgetType.Equals("NowPlaying", StringComparison.OrdinalIgnoreCase))
            {
                var pinItem = new MenuItem { Header = tr["Widget_Ctx_PinToTaskbar"], IsCheckable = true, IsChecked = GadgetItem.DockToTaskbar };
                pinItem.Click += (s, e) =>
                {
                    // Pin = hide overlay widget, show the taskbar bar with this gadget's
                    // options (Widget Customization). Unpin restores the widget.
                    // Lock stays independent (dashboard or Lock menu item).
                    GadgetItem.DockToTaskbar = !GadgetItem.DockToTaskbar;
                    SaveGadgetSettings();
                    (Window.GetWindow(this) as DesktopOverlayWindow)?.RefreshNowPlayingPin();
                };
                menu.Items.Add(pinItem);

                if (ChildContainer.Child is Palisades.Plugins.NowPlayingView npView)
                {
                    var sourceItem = new MenuItem { Header = tr["Widget_Ctx_NpSource"] };
                    BuildNowPlayingSourceMenu(npView, sourceItem);
                    menu.Items.Add(sourceItem);
                }
                menu.Items.Add(new Separator());
                var customizeItem = new MenuItem { Header = tr["Widget_Ctx_NpSettings"] };
                BuildNowPlayingMenu(customizeItem);
                menu.Items.Add(customizeItem);
            }

            menu.Items.Add(new Separator());

            // 4. Edit Properties → open dashboard Widget Customization
            var editItem = new MenuItem { Header = tr["Widget_Ctx_EditProperties"] };
            editItem.Click += (s, e) =>
            {
                var app = Application.Current as App;
                var win = app?.GetDashboardWindow();
                if (win == null) return;
                win.Show();
                if (win.WindowState == WindowState.Minimized)
                    win.WindowState = WindowState.Normal;
                win.ShowWidgetProperties(GadgetItem);
                win.Activate();
                win.Focus();
            };
            menu.Items.Add(editItem);

            menu.Items.Add(new Separator());

            // 5. Delete Option
            var deleteItem = new MenuItem { Header = tr["Widget_Ctx_DeleteWidget"] };
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

        private class NowPlayingSettings
        {
            public string Layout { get; set; } = "Classic";
            public bool ShowSeekBar { get; set; } = true;
            public bool ShowControls { get; set; } = true;
            public bool ShowAppLabel { get; set; } = true;
            public bool ShowCover { get; set; } = true;
            public string AccentColor { get; set; } = "#FF7DD3FC";
            public string ButtonsColor { get; set; } = "#A0FFFFFF";
            public string TextColor { get; set; } = "#FFF0F0F0";
            public string ForcedSourceAppId { get; set; } = "";
            public bool DarkMode { get; set; }
        }

        private void BuildClockCustomMenu(MenuItem parent)
        {
            var tr = TranslationService.Instance;
            var settings = GetClockSettings();

            // Show Seconds
            var secondsItem = new MenuItem { Header = tr["Widget_Ctx_ShowSeconds"], IsCheckable = true, IsChecked = settings.ShowSeconds };
            secondsItem.Click += (s, e) =>
            {
                settings.ShowSeconds = !settings.ShowSeconds;
                SaveClockSettings(settings);
            };
            parent.Items.Add(secondsItem);

            // 24-Hour Format
            var formatItem = new MenuItem { Header = tr["Widget_Ctx_24Hour"], IsCheckable = true, IsChecked = settings.Is24Hour };
            formatItem.Click += (s, e) =>
            {
                settings.Is24Hour = !settings.Is24Hour;
                SaveClockSettings(settings);
            };
            parent.Items.Add(formatItem);

            // Text Color submenu
            var colorMenu = new MenuItem { Header = tr["Widget_Ctx_ClockColor"] };
            string[] colors = { tr["Widget_Ctx_Col_IceBlue"], tr["Widget_Ctx_Col_White"], tr["Widget_Ctx_Col_Matrix"], tr["Widget_Ctx_Col_Amber"], tr["Widget_Ctx_Col_Cyber"] };
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
            var sizeMenu = new MenuItem { Header = tr["Widget_Ctx_FontSize"] };
            double[] sizes = { 24, 36, 48, 64 };
            string[] sizeNames = { tr["Widget_Ctx_Size_Small"], tr["Widget_Ctx_Size_Medium"], tr["Widget_Ctx_Size_Large"], tr["Widget_Ctx_Size_Huge"] };
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
            var tr = TranslationService.Instance;
            var settings = GetSysMonSettings();

            // Show CPU
            var cpuItem = new MenuItem { Header = tr["Widget_Ctx_ShowCpu"], IsCheckable = true, IsChecked = settings.ShowCpu };
            cpuItem.Click += (s, e) =>
            {
                settings.ShowCpu = !settings.ShowCpu;
                SaveSysMonSettings(settings);
            };
            parent.Items.Add(cpuItem);

            // Show RAM
            var ramItem = new MenuItem { Header = tr["Widget_Ctx_ShowRam"], IsCheckable = true, IsChecked = settings.ShowRam };
            ramItem.Click += (s, e) =>
            {
                settings.ShowRam = !settings.ShowRam;
                SaveSysMonSettings(settings);
            };
            parent.Items.Add(ramItem);

            // Refresh Interval
            var intervalMenu = new MenuItem { Header = tr["Widget_Ctx_RefreshRate"] };
            double[] rates = { 0.5, 1.0, 1.5, 2.0, 5.0 };
            string[] rateNames = { tr["Widget_Ctx_Rate_Fast"], tr["Widget_Ctx_Rate_Normal"], tr["Widget_Ctx_Rate_Medium"], tr["Widget_Ctx_Rate_Slow"], tr["Widget_Ctx_Rate_VerySlow"] };
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

        private void BuildNowPlayingSourceMenu(Palisades.Plugins.NowPlayingView view, MenuItem parent)
        {
            var tr = TranslationService.Instance;

            var autoItem = new MenuItem
            {
                Header = tr["Widget_Ctx_NpSourceAuto"],
                IsCheckable = true,
                IsChecked = !view.IsManualSource
            };
            autoItem.Click += (s, e) => PinNowPlayingSource(view, null);
            parent.Items.Add(autoItem);

            if (view.SourceSessions.Count > 0)
            {
                parent.Items.Add(new Separator());

                bool anyChecked = false;
                foreach (var session in view.SourceSessions)
                {
                    bool isPlaying = false;
                    try
                    {
                        var info = session.GetPlaybackInfo();
                        isPlaying = info.PlaybackStatus == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    }
                    catch { }

                    bool isChecked = view.IsManualSource && ReferenceEquals(session, view.ActiveSession);
                    anyChecked |= isChecked;
                    var item = new MenuItem
                    {
                        Header = (isPlaying ? "\u25B6 " : "") + view.SourceDisplayName(session),
                        IsCheckable = true,
                        IsChecked = isChecked
                    };
                    item.Click += (s, e) => PinNowPlayingSource(view, session);
                    parent.Items.Add(item);
                }

                // Pinned app currently absent (closed / not yet launched after reboot):
                // keep the pin visible so it re-applies when the app comes back.
                if (view.IsManualSource && !anyChecked)
                {
                    parent.Items.Add(new Separator());
                    var waitingItem = new MenuItem
                    {
                        Header = "\u25B6 " + view.PinnedSourceName,
                        IsCheckable = true,
                        IsChecked = true,
                        IsEnabled = false
                    };
                    parent.Items.Add(waitingItem);
                }
            }
            else if (view.IsManualSource)
            {
                // No sessions at all, but a pin is stored → show it waiting.
                parent.Items.Add(new Separator());
                var waitingItem = new MenuItem
                {
                    Header = "\u25B6 " + view.PinnedSourceName,
                    IsCheckable = true,
                    IsChecked = true,
                    IsEnabled = false
                };
                parent.Items.Add(waitingItem);
            }
        }

        private void PinNowPlayingSource(Palisades.Plugins.NowPlayingView view, Windows.Media.Control.GlobalSystemMediaTransportControlsSession? session)
        {
            // Persist the pin (app id) so it survives reboot + export/import.
            var settings = GetNowPlayingSettings();
            try { settings.ForcedSourceAppId = session?.SourceAppUserModelId ?? ""; }
            catch { settings.ForcedSourceAppId = ""; }
            SaveNowPlayingSettings(settings); // → ApplyCustomSettingsToChild → view re-resolves
        }

        private void BuildNowPlayingMenu(MenuItem parent)
        {
            var tr = TranslationService.Instance;
            var settings = GetNowPlayingSettings();

            // Layout submenu
            var layoutMenu = new MenuItem { Header = tr["Widget_Ctx_NpLayout"] };
            string[] layouts = { "Classic", "Compact", "Fluent", "Taskbar", "TaskbarSlim" };
            string[] layoutKeys = { "Widget_Ctx_NpLayout_Classic", "Widget_Ctx_NpLayout_Compact", "Widget_Ctx_NpLayout_Fluent", "Widget_Ctx_NpLayout_Taskbar", "Widget_Ctx_NpLayout_TaskbarSlim" };
            for (int i = 0; i < layouts.Length; i++)
            {
                string l = layouts[i];
                var layoutItem = new MenuItem { Header = tr[layoutKeys[i]], IsCheckable = true, IsChecked = settings.Layout.Equals(l, StringComparison.OrdinalIgnoreCase) };
                layoutItem.Click += (s, e) =>
                {
                    settings.Layout = l;
                    SaveNowPlayingSettings(settings);
                };
                layoutMenu.Items.Add(layoutItem);
            }
            parent.Items.Add(layoutMenu);

            // Show Seek Bar
            var seekItem = new MenuItem { Header = tr["Widget_Ctx_NpShowSeekBar"], IsCheckable = true, IsChecked = settings.ShowSeekBar };
            seekItem.Click += (s, e) =>
            {
                settings.ShowSeekBar = !settings.ShowSeekBar;
                SaveNowPlayingSettings(settings);
            };
            parent.Items.Add(seekItem);

            // Show Controls
            var controlsItem = new MenuItem { Header = tr["Widget_Ctx_NpShowControls"], IsCheckable = true, IsChecked = settings.ShowControls };
            controlsItem.Click += (s, e) =>
            {
                settings.ShowControls = !settings.ShowControls;
                SaveNowPlayingSettings(settings);
            };
            parent.Items.Add(controlsItem);

            // Show App Label
            var appItem = new MenuItem { Header = tr["Widget_Ctx_NpShowAppLabel"], IsCheckable = true, IsChecked = settings.ShowAppLabel };
            appItem.Click += (s, e) =>
            {
                settings.ShowAppLabel = !settings.ShowAppLabel;
                SaveNowPlayingSettings(settings);
            };
            parent.Items.Add(appItem);

            // Show Album Cover
            var coverItem = new MenuItem { Header = tr["Widget_Ctx_NpShowCover"], IsCheckable = true, IsChecked = settings.ShowCover };
            coverItem.Click += (s, e) =>
            {
                settings.ShowCover = !settings.ShowCover;
                SaveNowPlayingSettings(settings);
            };
            parent.Items.Add(coverItem);

            // Dark mode (black pill background)
            var darkItem = new MenuItem { Header = tr["Widget_Ctx_NpDarkMode"], IsCheckable = true, IsChecked = settings.DarkMode };
            darkItem.Click += (s, e) =>
            {
                settings.DarkMode = !settings.DarkMode;
                SaveNowPlayingSettings(settings);
            };
            parent.Items.Add(darkItem);

            // Accent color submenu (seek bar + play button)
            var accentMenu = new MenuItem { Header = tr["Widget_Ctx_NpAccentColor"] };
            string[] colorNames = { tr["Widget_Ctx_Col_IceBlue"], tr["Widget_Ctx_Col_White"], tr["Widget_Ctx_Col_Matrix"], tr["Widget_Ctx_Col_Amber"], tr["Widget_Ctx_Col_Cyber"], tr["Widget_Ctx_Col_KawaiiPink"], tr["Widget_Ctx_Col_Purple"], tr["Widget_Ctx_Col_Teal"], tr["Widget_Ctx_Col_Gold"], tr["Widget_Ctx_Col_Orange"], tr["Widget_Ctx_Col_Rose"], tr["Widget_Ctx_Col_Lime"] };
            string[] accentHex = { "#FF7DD3FC", "#FFFFFFFF", "#FF4AF626", "#FFFFB000", "#FFFF3E3E", "#FFFF71CE", "#FFA855F7", "#FF2DD4BF", "#FFFACC15", "#FFFB923C", "#FFFB7185", "#FFA3E635" };
            for (int i = 0; i < colorNames.Length; i++)
            {
                string code = accentHex[i];
                var colItem = new MenuItem { Header = colorNames[i], IsCheckable = true, IsChecked = settings.AccentColor.Equals(code, StringComparison.OrdinalIgnoreCase) };
                colItem.Click += (s, e) =>
                {
                    settings.AccentColor = code;
                    SaveNowPlayingSettings(settings);
                };
                accentMenu.Items.Add(colItem);
            }
            parent.Items.Add(accentMenu);

            // Buttons color submenu (transport icons)
            var buttonsMenu = new MenuItem { Header = tr["Widget_Ctx_NpButtonsColor"] };
            string[] btnNames = { tr["Widget_Ctx_NpButtons_Default"], tr["Widget_Ctx_Col_IceBlue"], tr["Widget_Ctx_Col_White"], tr["Widget_Ctx_Col_Matrix"], tr["Widget_Ctx_Col_Amber"], tr["Widget_Ctx_Col_Cyber"], tr["Widget_Ctx_Col_KawaiiPink"], tr["Widget_Ctx_Col_Purple"], tr["Widget_Ctx_Col_Teal"], tr["Widget_Ctx_Col_Gold"], tr["Widget_Ctx_Col_Orange"], tr["Widget_Ctx_Col_Rose"], tr["Widget_Ctx_Col_Lime"] };
            string[] btnHex = { "#A0FFFFFF", "#FF7DD3FC", "#FFFFFFFF", "#FF4AF626", "#FFFFB000", "#FFFF3E3E", "#FFFF71CE", "#FFA855F7", "#FF2DD4BF", "#FFFACC15", "#FFFB923C", "#FFFB7185", "#FFA3E635" };
            for (int i = 0; i < btnNames.Length; i++)
            {
                string code = btnHex[i];
                var colItem = new MenuItem { Header = btnNames[i], IsCheckable = true, IsChecked = settings.ButtonsColor.Equals(code, StringComparison.OrdinalIgnoreCase) };
                colItem.Click += (s, e) =>
                {
                    settings.ButtonsColor = code;
                    SaveNowPlayingSettings(settings);
                };
                buttonsMenu.Items.Add(colItem);
            }
            parent.Items.Add(buttonsMenu);

            // Text color submenu (title full, artist/app dimmed)
            var textMenu = new MenuItem { Header = tr["Widget_Ctx_NpTextColor"] };
            string[] txtNames = { tr["Widget_Ctx_NpButtons_Default"], tr["Widget_Ctx_Col_IceBlue"], tr["Widget_Ctx_Col_White"], tr["Widget_Ctx_Col_Matrix"], tr["Widget_Ctx_Col_Amber"], tr["Widget_Ctx_Col_Cyber"], tr["Widget_Ctx_Col_KawaiiPink"], tr["Widget_Ctx_Col_Purple"], tr["Widget_Ctx_Col_Teal"], tr["Widget_Ctx_Col_Gold"], tr["Widget_Ctx_Col_Orange"], tr["Widget_Ctx_Col_Rose"], tr["Widget_Ctx_Col_Lime"] };
            string[] txtHex = { "#FFF0F0F0", "#FF7DD3FC", "#FFFFFFFF", "#FF4AF626", "#FFFFB000", "#FFFF3E3E", "#FFFF71CE", "#FFA855F7", "#FF2DD4BF", "#FFFACC15", "#FFFB923C", "#FFFB7185", "#FFA3E635" };
            for (int i = 0; i < txtNames.Length; i++)
            {
                string code = txtHex[i];
                var colItem = new MenuItem { Header = txtNames[i], IsCheckable = true, IsChecked = settings.TextColor.Equals(code, StringComparison.OrdinalIgnoreCase) };
                colItem.Click += (s, e) =>
                {
                    settings.TextColor = code;
                    SaveNowPlayingSettings(settings);
                };
                textMenu.Items.Add(colItem);
            }
            parent.Items.Add(textMenu);
        }

        private NowPlayingSettings GetNowPlayingSettings()
        {
            try
            {
                if (!string.IsNullOrEmpty(GadgetItem.CustomData))
                {
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<NowPlayingSettings>(GadgetItem.CustomData) ?? new NowPlayingSettings();
                }
            }
            catch { }
            return new NowPlayingSettings();
        }

        private void SaveNowPlayingSettings(NowPlayingSettings settings)
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
