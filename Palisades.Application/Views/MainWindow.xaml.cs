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
using Newtonsoft.Json;
using Palisades.Models;
using Palisades.Services;
using Palisades.ViewModels;

namespace Palisades.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private HwndSource? _hwndSource;
        private const int HOTKEY_TOGGLE_CONTAINERS = 1;
        private const int HOTKEY_NEW_CONTAINER = 2;

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _vm = viewModel;
            DataContext = viewModel;

            _vm.Containers.CollectionChanged += (_, _) => UpdateStatus();
            _vm.RequestEditContainer += OnRequestEditContainer;
            _vm.ThemeChanged += OnThemeChanged;
            _vm.PropertyChanged += OnMainViewModelPropertyChanged;
            _vm.DefaultsImported += OnDefaultsImported;

            SourceInitialized += OnSourceInitialized;

            UpdateStatus();
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);

            // Register global hotkeys
            // Win+Shift+H = toggle containers visibility
            RegisterHotKey(hwnd, HOTKEY_TOGGLE_CONTAINERS, MOD_WIN | MOD_SHIFT, (uint)Key.H);
            // Win+Shift+N = new container
            RegisterHotKey(hwnd, HOTKEY_NEW_CONTAINER, MOD_WIN | MOD_SHIFT, (uint)Key.N);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;

            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HOTKEY_TOGGLE_CONTAINERS)
                {
                    ToggleContainersVisibility();
                    handled = true;
                }
                else if (id == HOTKEY_NEW_CONTAINER)
                {
                    _vm.CreateContainer();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private void ToggleContainersVisibility()
        {
            bool anyVisible = false;
            foreach (var vm in _vm.Containers)
                if (vm.IsVisible) { anyVisible = true; break; }

            foreach (var vm in _vm.Containers)
                vm.IsVisible = !anyVisible;
        }

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Minimize to tray instead of closing
            e.Cancel = true;
            Hide();
        }

        protected override void OnClosed(EventArgs e)
        {
            // Unregister hotkeys
            var hwnd = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(hwnd, HOTKEY_TOGGLE_CONTAINERS);
            UnregisterHotKey(hwnd, HOTKEY_NEW_CONTAINER);

            base.OnClosed(e);
        }

        private void OnMainViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
        }

        private void UpdateStatus()
        {
            int total = _vm.Containers.Sum(c => c.Shortcuts.Count);
            StatusText.Text = string.Format(TranslationService.Instance["MainWindow_Status"], _vm.Containers.Count, total);
        }

        private void OnRequestEditContainer(ContainerViewModel? vm)
        {
            if (vm != null)
                _vm.SelectedContainer = vm;
        }

        private void OnThemeChanged()
        {
            UpdateStatus();
        }

        private void OnDefaultsImported()
        {
            if (LivePreviewToggle.IsChecked != true) return;
            if (PreviewContainerCombo.SelectedItem is not ContainerViewModel target) return;
            ContainerManager.Instance.ApplyModelTo(target.Model, _vm.DefaultModel);
            target.RefreshAllBindings();
            ContainerManager.Instance.Save();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void ActionsButton_Click(object sender, RoutedEventArgs e)
        {
            ActionsPopup.IsOpen = !ActionsPopup.IsOpen;
        }

        private void ContainerItem_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is ContainerViewModel vm)
            {
                _vm.SelectedContainer = vm;
            }
        }

        private void HeaderColorSwatch_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border { Tag: string colorStr } && _vm.SelectedContainer != null)
            {
                var color = (Color)ColorConverter.ConvertFromString(colorStr);
                _vm.SelectedContainer.HeaderColor = color;
            }
        }

        private void BodyColorSwatch_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border { Tag: string colorStr } && _vm.SelectedContainer != null)
            {
                var color = (Color)ColorConverter.ConvertFromString(colorStr);
                _vm.SelectedContainer.BodyColor = color;
            }
        }

        private void DefaultHeaderColorSwatch_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border { Tag: string colorStr } && _vm.DefaultModel != null)
            {
                _vm.DefaultModel.HeaderColor = colorStr;
                if (LivePreviewToggle.IsChecked == true &&
                    PreviewContainerCombo.SelectedItem is ContainerViewModel target)
                {
                    var color = (Color)ColorConverter.ConvertFromString(colorStr);
                    target.HeaderColor = color;
                    ContainerManager.Instance.Save();
                }
            }
        }

        private void DefaultBodyColorSwatch_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border { Tag: string colorStr } && _vm.DefaultModel != null)
            {
                _vm.DefaultModel.BodyColor = colorStr;
                if (LivePreviewToggle.IsChecked == true &&
                    PreviewContainerCombo.SelectedItem is ContainerViewModel target)
                {
                    var color = (Color)ColorConverter.ConvertFromString(colorStr);
                    target.BodyColor = color;
                    ContainerManager.Instance.Save();
                }
            }
        }

        private void DefaultSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LivePreviewToggle.IsChecked != true) return;
            if (PreviewContainerCombo.SelectedItem is ContainerViewModel target &&
                sender is Slider slider && slider.Tag is string prop && _vm.DefaultModel != null)
            {
                double val = e.NewValue;
                switch (prop)
                {
                    case "IdleOpacity": target.IdleOpacityPercent = val; break;
                    case "ActiveOpacity": target.ActiveOpacityPercent = val; break;
                    case "ShortcutIconSize": target.ShortcutIconSize = (int)val; break;
                    case "AnimationSpeedMs": target.AnimationSpeedMs = (int)val; break;
                    case "CornerRadius": target.CornerRadius = (int)val; break;
                    case "TitleFontSize": target.TitleFontSize = val; break;
                    case "HeaderIconSize": target.HeaderIconSize = (int)val; break;
                }
                ContainerManager.Instance.Save();
            }
        }

        private void DefaultCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (LivePreviewToggle.IsChecked != true) return;
            if (PreviewContainerCombo.SelectedItem is ContainerViewModel target &&
                sender is CheckBox cb && cb.Tag is string prop && cb.IsChecked.HasValue)
            {
                bool val = cb.IsChecked.Value;
                switch (prop)
                {
                    case "AutoHide": target.AutoHide = val; break;
                    case "ShowBorder": target.ShowBorder = val; break;
                    case "RoundedCorners": target.RoundedCornersEnabled = val; break;
                    case "ShowTitle": target.ShowTitle = val; break;
                    case "TitleHoverEffect": target.TitleHoverEffect = val; break;
                    case "IsLocked": target.IsLocked = val; break;
                    case "AutoHideOnEdge": target.AutoHideOnEdge = val; break;
                    case "OpenOnDoubleClick": target.OpenOnDoubleClick = !val; break;
                    case "UseShellContextMenu": target.UseShellContextMenu = val; break;
                    case "TwoLineShortcuts": target.TwoLineShortcuts = val; break;
                }
                ContainerManager.Instance.Save();
            }
        }

        private void DefaultComboBox_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (LivePreviewToggle.IsChecked != true) return;
            if (PreviewContainerCombo.SelectedItem is not ContainerViewModel target ||
                sender is not ComboBox cb || cb.Tag is not string prop || _vm.DefaultModel == null)
                return;

            switch (prop)
            {
                case "TitleFontFamily":
                    if (cb.SelectedItem is string font)
                        target.TitleFontFamily = font;
                    break;
                case "TitleAlignment":
                    if (cb.SelectedValue is string align)
                        target.TitleAlignment = align;
                    break;
            }
            ContainerManager.Instance.Save();
        }

        private void DefaultTextBox_Changed(object sender, TextChangedEventArgs e)
        {
            if (LivePreviewToggle.IsChecked != true) return;
            if (PreviewContainerCombo.SelectedItem is ContainerViewModel target &&
                sender is TextBox tb && tb.Tag is string prop && _vm.DefaultModel != null)
            {
                switch (prop)
                {
                    case "AutoHideDelayMs":
                        if (int.TryParse(tb.Text, out int ms))
                            target.AutoHideDelayMs = ms;
                        break;
                }
                ContainerManager.Instance.Save();
            }
        }

        private void AutoSortCategory_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox { Tag: string category } cb && _vm.SelectedContainer != null)
            {
                var cats = _vm.SelectedContainer.AutoSortCategories;
                if (cb.IsChecked == true)
                {
                    if (!cats.Contains(category))
                        cats.Add(category);
                    _vm.SelectedContainer.Model.IsAutoSortManaged = true;
                }
                else
                {
                    cats.Remove(category);
                }
                // Replace list to trigger UI refresh (List<T> doesn't notify)
                _vm.SelectedContainer.AutoSortCategories = new List<string>(cats);
                _vm.SelectedContainer.Save();

                // Immédiatement collecter les raccourcis du Bureau correspondant à cette catégorie
                ContainerManager.Instance.CollectDesktopItemsIntoContainer(_vm.SelectedContainer.Model);
            }
        }

        private void SnapshotRename_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string id })
            {
                var snap = _vm.Snapshots.FirstOrDefault(s => s.Identifier == id);
                if (snap == null) return;

                var dialog = new Window
                {
                    Title = TranslationService.Instance["Snapshot_RenameTitle"],
                    Width = 350,
                    Height = 150,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false
                };

                var border = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(20)
                };

                var stack = new StackPanel();
                stack.Children.Add(new TextBlock
                {
                    Text = TranslationService.Instance["Snapshot_RenameTitle"],
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                    Margin = new Thickness(0, 0, 0, 12)
                });

                var textBox = new TextBox
                {
                    Text = snap.Name,
                    Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)),
                    Padding = new Thickness(8, 4, 8, 4),
                    FontSize = 13,
                    SelectionLength = snap.Name.Length
                };
                stack.Children.Add(textBox);

                var btnPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 12, 0, 0)
                };

            var cancelBtn = new Button
            {
                Content = TranslationService.Instance["Snapshot_Cancel"],
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                BorderThickness = new Thickness(1),
                Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                Padding = new Thickness(12, 6, 12, 6),
                FontSize = 12,
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand
            };
            cancelBtn.Click += (_, _) => dialog.Close();
            btnPanel.Children.Add(cancelBtn);

            var okBtn = new Button
            {
                Content = TranslationService.Instance["Snapshot_OK"],
                    Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x4A, 0x2A)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x6A, 0x4A)),
                    BorderThickness = new Thickness(1),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8D, 0xD8, 0x8D)),
                    Padding = new Thickness(12, 6, 12, 6),
                    FontSize = 12,
                    Cursor = Cursors.Hand,
                    IsEnabled = false
                };
                okBtn.Click += (_, _) =>
                {
                    if (!string.IsNullOrWhiteSpace(textBox.Text))
                    {
                        Services.SnapshotManager.Instance.RenameSnapshot(id, textBox.Text);
                        snap.Name = textBox.Text;
                    }
                    dialog.Close();
                };
                btnPanel.Children.Add(okBtn);

                textBox.TextChanged += (_, _) => okBtn.IsEnabled = !string.IsNullOrWhiteSpace(textBox.Text);
                textBox.KeyDown += (_, e2) =>
                {
                    if (e2.Key == Key.Enter && okBtn.IsEnabled)
                        okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };

                stack.Children.Add(btnPanel);
                border.Child = stack;
                dialog.Content = border;

                textBox.Focus();
                dialog.ShowDialog();
            }
        }

        #region Private Box

        private void SetPassword_Click(object sender, RoutedEventArgs e)
        {
            var vm = _vm.SelectedContainer;
            if (vm == null) return;
            ShowSetPasswordDialog(vm);
        }

        private void LockContainer_Click(object sender, RoutedEventArgs e)
        {
            var vm = _vm.SelectedContainer;
            if (vm == null || string.IsNullOrEmpty(vm.PasswordHash)) return;

            var t = TranslationService.Instance;
            var password = PromptPassword(t["PrivateBox_LockTitle"], t["PrivateBox_LockPrompt"]);
            if (password == null) return;

            if (!Services.EncryptionService.VerifyPassword(password, vm.PasswordHash))
            {
                MessageBox.Show(TranslationService.Instance["PrivateBox_WrongPassword"], "Palisades", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            LockContainer(vm, password);
        }

        private void RemovePassword_Click(object sender, RoutedEventArgs e)
        {
            var vm = _vm.SelectedContainer;
            if (vm == null) return;

            var result = MessageBox.Show(TranslationService.Instance["PrivateBox_RemovePrompt"],
                "Palisades", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            vm.Model.PasswordHash = string.Empty;
            vm.Model.EncryptedShortcuts = null;
            vm.Model.IsPasswordLocked = false;
            vm.Save();
            _vm.RefreshContainers();
            _vm.NotifyRebuildOverlay();
        }

        private void ShowSetPasswordDialog(ContainerViewModel containerVm)
        {
            var t = TranslationService.Instance;
            var dialog = new Window
            {
                Title = t["PrivateBox_SetPasswordTitle"],
                Width = 350,
                Height = 250,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(20)
            };

            var stack = new StackPanel();

            stack.Children.Add(new TextBlock
            {
                Text = t["PrivateBox_SetPasswordTitle"],
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                Margin = new Thickness(0, 0, 0, 12)
            });

            stack.Children.Add(new TextBlock
            {
                Text = t["PrivateBox_SetPassword_New"],
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 4)
            });

            var pwBox1 = new System.Windows.Controls.PasswordBox
            {
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)),
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 13
            };
            stack.Children.Add(pwBox1);

            stack.Children.Add(new TextBlock
            {
                Text = t["PrivateBox_SetPassword_Confirm"],
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 4)
            });

            var pwBox2 = new System.Windows.Controls.PasswordBox
            {
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)),
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 13
            };
            stack.Children.Add(pwBox2);

            var errorText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66)),
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0)
            };
            stack.Children.Add(errorText);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };

            var cancelBtn = new Button
            {
                Content = t["Snapshot_Cancel"],
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                BorderThickness = new Thickness(1),
                Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                Padding = new Thickness(12, 6, 12, 6),
                FontSize = 12,
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand
            };
            cancelBtn.Click += (_, _) => dialog.Close();
            btnPanel.Children.Add(cancelBtn);

            var okBtn = new Button
            {
                Content = t["PrivateBox_SetPassword_Btn"],
                Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x3A, 0x1E)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x7A, 0x5A, 0x3E)),
                BorderThickness = new Thickness(1),
                Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xB8, 0x88)),
                Padding = new Thickness(12, 6, 12, 6),
                FontSize = 12,
                Cursor = Cursors.Hand,
                IsEnabled = false
            };
            okBtn.Click += (_, _) =>
            {
                errorText.Text = "";
                string p1 = pwBox1.Password;
                string p2 = pwBox2.Password;

                if (string.IsNullOrEmpty(p1))
                {
                    errorText.Text = t["PrivateBox_EmptyPassword"];
                    return;
                }
                if (p1 != p2)
                {
                    errorText.Text = t["PrivateBox_PasswordsDontMatch"];
                    return;
                }
                if (p1.Length < 4)
                {
                    errorText.Text = t["PrivateBox_PasswordTooShort"];
                    return;
                }

                containerVm.Model.PasswordHash = Services.EncryptionService.HashPassword(p1);
                containerVm.NotifyPasswordChanged();
                containerVm.Save();
                dialog.Close();
            };
            btnPanel.Children.Add(okBtn);

            void Validate()
            {
                okBtn.IsEnabled = pwBox1.Password.Length > 0 && pwBox2.Password.Length > 0;
                errorText.Text = "";
            }
            pwBox1.PasswordChanged += (_, _) => Validate();
            pwBox2.PasswordChanged += (_, _) => Validate();
            pwBox1.KeyDown += (_, e2) => { if (e2.Key == Key.Enter) pwBox2.Focus(); };
            pwBox2.KeyDown += (_, e2) => { if (e2.Key == Key.Enter && okBtn.IsEnabled) okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };

            stack.Children.Add(btnPanel);
            border.Child = stack;
            dialog.Content = border;

            pwBox1.Focus();
            dialog.ShowDialog();
        }

        private string? PromptPassword(string title, string message)
        {
            string? result = null;
            var dialog = new Window
            {
                Title = title,
                Width = 320,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(20)
            };

            var stack = new StackPanel();

            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                Margin = new Thickness(0, 0, 0, 8)
            });

            stack.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var pwBox = new System.Windows.Controls.PasswordBox
            {
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)),
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 13
            };
            stack.Children.Add(pwBox);

            var errorText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66)),
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0)
            };
            stack.Children.Add(errorText);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };

            var t2 = TranslationService.Instance;
            var cancelBtn = new Button
            {
                Content = t2["Snapshot_Cancel"],
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                Padding = new Thickness(12, 6, 12, 6),
                FontSize = 12,
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD))
            };
            cancelBtn.Click += (_, _) => dialog.Close();
            btnPanel.Children.Add(cancelBtn);

            var okBtn = new Button
            {
                Content = t2["Snapshot_OK"],
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x4A, 0x2A)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x6A, 0x4A)),
                Padding = new Thickness(12, 6, 12, 6),
                FontSize = 12,
                Cursor = Cursors.Hand,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8D, 0xD8, 0x8D)),
                IsEnabled = false
            };
            okBtn.Click += (_, _) =>
            {
                errorText.Text = "";
                if (string.IsNullOrEmpty(pwBox.Password))
                {
                    errorText.Text = t2["PrivateBox_EnterPassword"];
                    return;
                }
                result = pwBox.Password;
                dialog.Close();
            };
            btnPanel.Children.Add(okBtn);

            pwBox.PasswordChanged += (_, _) => okBtn.IsEnabled = pwBox.Password.Length > 0;
            pwBox.KeyDown += (_, e2) => { if (e2.Key == Key.Enter && okBtn.IsEnabled) okBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };

            stack.Children.Add(btnPanel);
            border.Child = stack;
            dialog.Content = border;

            pwBox.Focus();
            dialog.ShowDialog();
            return result;
        }

        private void LockContainer(ContainerViewModel vm, string password)
        {
            try
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(vm.Shortcuts.ToList(),
                    Newtonsoft.Json.Formatting.None);
                vm.Model.EncryptedShortcuts = Services.EncryptionService.Encrypt(json, password);
                vm.Model.IsPasswordLocked = true;
                vm.Shortcuts.Clear();
                vm.Save();
                _vm.RefreshContainers();
                _vm.NotifyRebuildOverlay();
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(TranslationService.Instance["PrivateBox_FailedLock"], ex.Message), "Palisades",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        private void ArcticShelter_Click(object sender, RoutedEventArgs e)
        {
            var window = new ArcticShelterWindow(_vm);
            window.Owner = this;
            window.Show();
        }

        private void LanguageCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (sender is System.Windows.Controls.ComboBox combo && combo.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag is string culture)
            {
                TranslationService.Instance.SetLanguage(culture);
            }
        }
        private void SnapshotPreview_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is SnapshotModel snap &&
                !string.IsNullOrEmpty(snap.ScreenshotPath) && System.IO.File.Exists(snap.ScreenshotPath))
            {
                var uri = new Uri("file:///" + snap.ScreenshotPath.Replace('\\', '/'));
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = uri;
                img.EndInit();

                var popup = new Window
                {
                    Title = snap.Name,
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Width = SystemParameters.PrimaryScreenWidth * 0.85,
                    Height = SystemParameters.PrimaryScreenHeight * 0.85,
                    MaxWidth = SystemParameters.WorkArea.Width,
                    MaxHeight = SystemParameters.WorkArea.Height,
                    WindowState = WindowState.Normal
                };

                var image = new Image
                {
                    Source = img,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    MaxWidth = popup.Width - 80,
                    MaxHeight = popup.Height - 140,
                    Margin = new Thickness(40, 40, 40, 10)
                };

                var closeBtn = new Button
                {
                    Content = "✕",
                    Width = 40,
                    Height = 40,
                    Background = new SolidColorBrush(Color.FromArgb(200, 0x3A, 0x1E, 0x1E)),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x3A, 0x3A)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66)),
                    FontSize = 18,
                    Cursor = Cursors.Hand,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 40)
                };
                closeBtn.Click += (_, _) => popup.Close();

                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(image, 0);
                Grid.SetRow(closeBtn, 1);
                grid.Children.Add(image);
                grid.Children.Add(closeBtn);
                grid.MouseLeftButtonDown += (_, args) =>
                {
                    if (args.OriginalSource == grid)
                        popup.Close();
                };

                popup.Content = grid;
                popup.KeyDown += (_, args) =>
                {
                    if (args.Key == Key.Escape)
                        popup.Close();
                };
                popup.ShowDialog();
            }
        }
    }
}
