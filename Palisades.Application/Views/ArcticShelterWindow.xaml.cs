using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Palisades.Models;
using Palisades.Services;
using Palisades.ViewModels;

namespace Palisades.Views
{
    public partial class ArcticShelterWindow : Window
    {
        private readonly MainViewModel _viewModel;

        public ArcticShelterWindow(MainViewModel viewModel)
        {
            _viewModel = viewModel;
            DataContext = _viewModel;
            InitializeComponent();

            _viewModel.Containers.CollectionChanged += (_, _) => { };
            _viewModel.ThemeChanged += OnThemeChanged;
            _viewModel.DefaultsImported += OnDefaultsImported;
        }

        private void OnThemeChanged()
        {
        }

        private void OnDefaultsImported()
        {
            if (LivePreviewToggle.IsChecked != true) return;
            if (PreviewContainerCombo.SelectedItem is not ContainerViewModel target) return;
            ContainerManager.Instance.ApplyModelTo(target.Model, _viewModel.DefaultModel);
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
            Close();
        }

        private void ContainerCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border { DataContext: ContainerViewModel container })
            {
                _viewModel.SelectedContainer = container;
                SwitchToTab("Containers");
            }
        }

        private void SwitchToTab(string tabName)
        {
            DashboardBtn.Style = (Style)FindResource("SidebarBtnStyle");
            ContainersBtn.Style = (Style)FindResource("SidebarBtnStyle");
            DefaultsBtn.Style = (Style)FindResource("SidebarBtnStyle");
            DeskBtn.Style = (Style)FindResource("SidebarBtnStyle");
            AppBtn.Style = (Style)FindResource("SidebarBtnStyle");
            SnapshotsBtn.Style = (Style)FindResource("SidebarBtnStyle");
            ThemesBtn.Style = (Style)FindResource("SidebarBtnStyle");
            PluginsBtn.Style = (Style)FindResource("SidebarBtnStyle");

            var btn = tabName switch
            {
                "Dashboard" => DashboardBtn,
                "Containers" => ContainersBtn,
                "Defaults" => DefaultsBtn,
                "Desk" => DeskBtn,
                "App" => AppBtn,
                "Snapshots" => SnapshotsBtn,
                "Themes" => ThemesBtn,
                "Plugins" => PluginsBtn,
                _ => null
            };
            if (btn != null)
                btn.Style = (Style)FindResource("SidebarBtnActiveStyle");

            DashboardPanel.Visibility = tabName == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
            ContainersPanel.Visibility = tabName == "Containers" ? Visibility.Visible : Visibility.Collapsed;
            DefaultsPanel.Visibility = tabName == "Defaults" ? Visibility.Visible : Visibility.Collapsed;
            DeskPanel.Visibility = tabName == "Desk" ? Visibility.Visible : Visibility.Collapsed;
            AppPanel.Visibility = tabName == "App" ? Visibility.Visible : Visibility.Collapsed;
            SnapshotsPanel.Visibility = tabName == "Snapshots" ? Visibility.Visible : Visibility.Collapsed;
            ThemesPanel.Visibility = tabName == "Themes" ? Visibility.Visible : Visibility.Collapsed;
            PluginsPanel.Visibility = tabName == "Plugins" ? Visibility.Visible : Visibility.Collapsed;

            if (tabName == "Themes" || tabName == "Containers")
            {
                _viewModel.RefreshThemeNames();
            }
        }

        private void Tab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is string tabName)
                SwitchToTab(tabName);
        }

        private void OpenPluginsFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins");
            if (System.IO.Directory.Exists(folder))
            {
                try { System.Diagnostics.Process.Start("explorer.exe", folder); } catch { }
            }
        }

        private void OpenThemesFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes");
            if (System.IO.Directory.Exists(folder))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }

        private void DefaultSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LivePreviewToggle.IsChecked != true) return;
            if (PreviewContainerCombo.SelectedItem is ContainerViewModel target &&
                sender is Slider slider && slider.Tag is string prop && _viewModel.DefaultModel != null)
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
                }
                ContainerManager.Instance.Save();
            }
        }

        private void DefaultComboBox_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (LivePreviewToggle.IsChecked != true) return;
            if (PreviewContainerCombo.SelectedItem is not ContainerViewModel target ||
                sender is not ComboBox cb || cb.Tag is not string prop || _viewModel.DefaultModel == null)
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
                case "ViewMode":
                    if (cb.SelectedValue is string mode)
                        target.ViewMode = mode;
                    break;
            }
            ContainerManager.Instance.Save();
        }

        private void DefaultTextBox_Changed(object sender, TextChangedEventArgs e)
        {
            if (LivePreviewToggle.IsChecked != true) return;
            if (PreviewContainerCombo.SelectedItem is ContainerViewModel target &&
                sender is TextBox tb && tb.Tag is string prop && _viewModel.DefaultModel != null)
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

        private void DefaultHeaderColorSwatch_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border { Tag: string colorStr } && _viewModel.DefaultModel != null)
            {
                _viewModel.DefaultModel.HeaderColor = colorStr;
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
            if (sender is Border { Tag: string colorStr } && _viewModel.DefaultModel != null)
            {
                _viewModel.DefaultModel.BodyColor = colorStr;
                if (LivePreviewToggle.IsChecked == true &&
                    PreviewContainerCombo.SelectedItem is ContainerViewModel target)
                {
                    var color = (Color)ColorConverter.ConvertFromString(colorStr);
                    target.BodyColor = color;
                    ContainerManager.Instance.Save();
                }
            }
        }

        private void HeaderColorSwatch_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border { Tag: string colorStr } && _viewModel.SelectedContainer != null)
            {
                var color = (Color)ColorConverter.ConvertFromString(colorStr);
                _viewModel.SelectedContainer.HeaderColor = color;
            }
        }

        private void BodyColorSwatch_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border { Tag: string colorStr } && _viewModel.SelectedContainer != null)
            {
                var color = (Color)ColorConverter.ConvertFromString(colorStr);
                _viewModel.SelectedContainer.BodyColor = color;
            }
        }

        private void AutoSortCategory_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox { Tag: string category } cb && _viewModel.SelectedContainer != null)
            {
                var cats = _viewModel.SelectedContainer.AutoSortCategories;
                if (cb.IsChecked == true)
                {
                    if (!cats.Contains(category))
                        cats.Add(category);
                    _viewModel.SelectedContainer.Model.IsAutoSortManaged = true;
                }
                else
                {
                    cats.Remove(category);
                }
                _viewModel.SelectedContainer.AutoSortCategories = new List<string>(cats);
                _viewModel.SelectedContainer.Save();
                ContainerManager.Instance.CollectDesktopItemsIntoContainer(_viewModel.SelectedContainer.Model);
            }
        }

        private void SnapshotRename_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string id })
            {
                var snap = _viewModel.Snapshots.FirstOrDefault(s => s.Identifier == id);
                if (snap == null) return;

                var dialog = new Window
                {
                    Width = 350, Height = 150,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this, WindowStyle = WindowStyle.None,
                    AllowsTransparency = true, Background = Brushes.Transparent,
                    ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
                };

                var border = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x20)),
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(20)
                };

                var stack = new StackPanel();
                stack.Children.Add(new TextBlock
                {
                    Text = "Rename Snapshot", FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC)),
                    Margin = new Thickness(0, 0, 0, 12)
                });

                var textBox = new TextBox
                {
                    Text = snap.Name,
                    Background = new SolidColorBrush(Color.FromRgb(0x11, 0x16, 0x1B)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x35)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xF1, 0xFF)),
                    Padding = new Thickness(8, 4, 8, 4), FontSize = 13,
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
                    Content = "Cancel",
                    Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x24, 0x2E)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x35)),
                    BorderThickness = new Thickness(1),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xF1, 0xFF)),
                Padding = new Thickness(12, 6, 12, 6), FontSize = 12,
                                Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand
                };
                cancelBtn.Click += (_, _) => dialog.Close();
                btnPanel.Children.Add(cancelBtn);

                var okBtn = new Button
                {
                    Content = "OK",
                    Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x24, 0x2E)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x35)),
                    BorderThickness = new Thickness(1),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC)),
                    Padding = new Thickness(12, 6, 12, 6), FontSize = 12,
                    Cursor = Cursors.Hand, IsEnabled = false
                };
                okBtn.Click += (_, _) =>
                {
                    if (!string.IsNullOrWhiteSpace(textBox.Text))
                    {
                        SnapshotManager.Instance.RenameSnapshot(id, textBox.Text);
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
                    Background = new SolidColorBrush(Color.FromArgb(200, 0x2C, 0x1A, 0x1A)),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x5C, 0x3A, 0x3A)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5)),
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

        #region Private Box

        private void SetPassword_Click(object sender, RoutedEventArgs e)
        {
            var vm = _viewModel.SelectedContainer;
            if (vm == null) return;
            ShowSetPasswordDialog(vm);
        }

        private void LockContainer_Click(object sender, RoutedEventArgs e)
        {
            var vm = _viewModel.SelectedContainer;
            if (vm == null || string.IsNullOrEmpty(vm.PasswordHash)) return;

            var t = TranslationService.Instance;
            var password = PromptPassword(t["PrivateBox_LockTitle"], t["PrivateBox_LockPrompt"]);
            if (password == null) return;

            if (!EncryptionService.VerifyPassword(password, vm.PasswordHash))
            {
                MessageBox.Show(t["PrivateBox_WrongPassword"], "Palisades", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            LockContainer(vm, password);
        }

        private void RemovePassword_Click(object sender, RoutedEventArgs e)
        {
            var vm = _viewModel.SelectedContainer;
            if (vm == null) return;

            var result = MessageBox.Show(TranslationService.Instance["PrivateBox_RemovePrompt"],
                "Palisades", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            vm.Model.PasswordHash = string.Empty;
            vm.Model.EncryptedShortcuts = null;
            vm.Model.IsPasswordLocked = false;
            vm.Save();
            _viewModel.RefreshContainers();
            _viewModel.NotifyRebuildOverlay();
        }

        private void ShowSetPasswordDialog(ContainerViewModel containerVm)
        {
            var t = TranslationService.Instance;
            var dialog = new Window
            {
                Width = 350, Height = 250,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, WindowStyle = WindowStyle.None,
                AllowsTransparency = true, Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
            };

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x20)),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(20)
            };

            var stack = new StackPanel();

            stack.Children.Add(new TextBlock
            {
                Text = t["PrivateBox_SetPasswordTitle"], FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC)),
                Margin = new Thickness(0, 0, 0, 12)
            });

            stack.Children.Add(new TextBlock
            {
                Text = t["PrivateBox_SetPassword_New"],
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x9C, 0xAE)),
                FontSize = 11, Margin = new Thickness(0, 0, 0, 4)
            });

            var pwBox1 = new PasswordBox
            {
                Background = new SolidColorBrush(Color.FromRgb(0x11, 0x16, 0x1B)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x35)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xF1, 0xFF)),
                Padding = new Thickness(8, 4, 8, 4), FontSize = 13
            };
            stack.Children.Add(pwBox1);

            stack.Children.Add(new TextBlock
            {
                Text = t["PrivateBox_SetPassword_Confirm"],
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x9C, 0xAE)),
                FontSize = 11, Margin = new Thickness(0, 8, 0, 4)
            });

            var pwBox2 = new PasswordBox
            {
                Background = new SolidColorBrush(Color.FromRgb(0x11, 0x16, 0x1B)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x35)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xF1, 0xFF)),
                Padding = new Thickness(8, 4, 8, 4), FontSize = 13
            };
            stack.Children.Add(pwBox2);

            var errorText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5)),
                FontSize = 11, Margin = new Thickness(0, 6, 0, 0)
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
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x24, 0x2E)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x35)),
                BorderThickness = new Thickness(1),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xF1, 0xFF)),
                    Padding = new Thickness(12, 6, 12, 6), FontSize = 12,
                    Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand
                };
                cancelBtn.Click += (_, _) => dialog.Close();
                btnPanel.Children.Add(cancelBtn);

                var okBtn = new Button
                {
                    Content = t["PrivateBox_SetPassword_Btn"],
                    Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x24, 0x2E)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x35)),
                    BorderThickness = new Thickness(1),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC)),
                    Padding = new Thickness(12, 6, 12, 6), FontSize = 12,
                Cursor = Cursors.Hand, IsEnabled = false
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

                containerVm.Model.PasswordHash = EncryptionService.HashPassword(p1);
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
                Width = 320, Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this, WindowStyle = WindowStyle.None,
                AllowsTransparency = true, Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
            };

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x20)),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(20)
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC)),
                Margin = new Thickness(0, 0, 0, 8)
            });
            stack.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x9C, 0xAE)),
                FontSize = 11, Margin = new Thickness(0, 0, 0, 8)
            });

            var pwBox = new PasswordBox
            {
                Background = new SolidColorBrush(Color.FromRgb(0x11, 0x16, 0x1B)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x35)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xF1, 0xFF)),
                Padding = new Thickness(8, 4, 8, 4), FontSize = 13
            };
            stack.Children.Add(pwBox);

            var errorText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5)),
                FontSize = 11, Margin = new Thickness(0, 6, 0, 0)
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
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x24, 0x2E)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x35)),
                Padding = new Thickness(12, 6, 12, 6), FontSize = 12,
                Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xF1, 0xFF))
            };
            cancelBtn.Click += (_, _) => dialog.Close();
            btnPanel.Children.Add(cancelBtn);

            var okBtn = new Button
            {
                Content = t2["Snapshot_OK"],
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x24, 0x2E)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x35)),
                Padding = new Thickness(12, 6, 12, 6), FontSize = 12,
                Cursor = Cursors.Hand,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC)),
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
                vm.Model.EncryptedShortcuts = EncryptionService.Encrypt(json, password);
                vm.Model.IsPasswordLocked = true;
                vm.Shortcuts.Clear();
                vm.Save();
                _viewModel.RefreshContainers();
                _viewModel.NotifyRebuildOverlay();
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(TranslationService.Instance["PrivateBox_FailedLock"], ex.Message), "Palisades",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item && item.Tag is string culture)
            {
                TranslationService.Instance.SetLanguage(culture);
            }
        }

        private void IconSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_viewModel?.DefaultModel != null)
            {
                _viewModel.DefaultModel.ShortcutIconSize = (int)e.NewValue;
                ContainerManager.Instance.SaveDefaults(_viewModel.DefaultModel);
                _viewModel.NotifyRebuildOverlay();
            }
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_viewModel?.DefaultModel != null)
            {
                _viewModel.DefaultModel.IdleOpacity = e.NewValue;
                ContainerManager.Instance.SaveDefaults(_viewModel.DefaultModel);
                _viewModel.NotifyRebuildOverlay();
            }
        }

        private void ActiveEffects_Checked(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.DefaultModel != null)
            {
                _viewModel.DefaultModel.TitleHoverEffect = true;
                ContainerManager.Instance.SaveDefaults(_viewModel.DefaultModel);
                _viewModel.NotifyRebuildOverlay();
            }
        }

        private void ActiveEffects_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.DefaultModel != null)
            {
                _viewModel.DefaultModel.TitleHoverEffect = false;
                ContainerManager.Instance.SaveDefaults(_viewModel.DefaultModel);
                _viewModel.NotifyRebuildOverlay();
            }
        }

        private void ActiveWidgetsListBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var listbox = sender as ListBox;
            if (listbox == null) return;

            var element = e.OriginalSource as DependencyObject;
            bool isOnItem = false;
            bool isOnScrollBar = false;

            while (element != null && element != listbox)
            {
                if (element is ListBoxItem)
                {
                    isOnItem = true;
                    break;
                }
                if (element is System.Windows.Controls.Primitives.ScrollBar)
                {
                    isOnScrollBar = true;
                    break;
                }
                element = VisualTreeHelper.GetParent(element);
            }

            if (!isOnItem && !isOnScrollBar)
            {
                e.Handled = true;
                listbox.Focus();
            }
        }

        private void NewButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                btn.ContextMenu.IsOpen = true;
            }
        }

        private bool _isUpdatingFields = false;

        private void SvgButtonsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SvgButtonsListBox.SelectedItem is ShortcutItem item)
            {
                _isUpdatingFields = true;
                DashboardTargetTextBox.Text = item.IsUrl ? item.UrlTarget : item.TargetPath;
                DashboardHotkeyTextBox.Text = item.Hotkey ?? string.Empty;
                _isUpdatingFields = false;
            }
        }

        private void SvgButtonTarget_Changed(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingFields) return;
            if (SvgButtonsListBox.SelectedItem is ShortcutItem item && _viewModel.SelectedContainer != null)
            {
                string text = DashboardTargetTextBox.Text.Trim();
                if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    item.IsUrl = true;
                    item.UrlTarget = text;
                    item.TargetPath = string.Empty;
                }
                else
                {
                    item.IsUrl = false;
                    item.TargetPath = text;
                    item.UrlTarget = string.Empty;
                }
                _viewModel.SelectedContainer.Save();
            }
        }

        private void SvgButtonProperty_Changed(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingFields) return;
            if (_viewModel.SelectedContainer != null)
            {
                _viewModel.SelectedContainer.Save();
            }
        }

        private void BrowseSvgButtonFile_Click(object sender, RoutedEventArgs e)
        {
            if (SvgButtonsListBox.SelectedItem is ShortcutItem item && _viewModel.SelectedContainer != null)
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select Target File",
                    Filter = "All Files (*.*)|*.*"
                };

                if (dialog.ShowDialog(this) == true)
                {
                    _isUpdatingFields = true;
                    item.IsUrl = false;
                    item.TargetPath = dialog.FileName;
                    item.UrlTarget = string.Empty;
                    DashboardTargetTextBox.Text = dialog.FileName;
                    _isUpdatingFields = false;
                    _viewModel.SelectedContainer.Save();
                }
            }
        }

        private void BrowseSvgButtonFolder_Click(object sender, RoutedEventArgs e)
        {
            if (SvgButtonsListBox.SelectedItem is ShortcutItem item && _viewModel.SelectedContainer != null)
            {
                var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "Select Target Folder"
                };

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _isUpdatingFields = true;
                    item.IsUrl = false;
                    item.TargetPath = dialog.SelectedPath;
                    item.UrlTarget = string.Empty;
                    DashboardTargetTextBox.Text = dialog.SelectedPath;
                    _isUpdatingFields = false;
                    _viewModel.SelectedContainer.Save();
                }
            }
        }

        private void DashboardHotkeyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (SvgButtonsListBox.SelectedItem is not ShortcutItem item || _viewModel.SelectedContainer == null)
                return;

            e.Handled = true;

            var key = e.Key;
            
            // Allow clearing using Escape or Delete/Backspace
            if (key == Key.Escape || key == Key.Back || key == Key.Delete)
            {
                _isUpdatingFields = true;
                item.Hotkey = string.Empty;
                DashboardHotkeyTextBox.Text = string.Empty;
                _isUpdatingFields = false;
                _viewModel.SelectedContainer.Save();
                var overlay = Application.Current.Windows.OfType<DesktopOverlayWindow>().FirstOrDefault();
                overlay?.RefreshGlobalHotkeys();
                return;
            }

            var modifiers = Keyboard.Modifiers;
            var sb = new System.Text.StringBuilder();
            if (modifiers.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
            if (modifiers.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
            if (modifiers.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
            if (modifiers.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");

            sb.Append(key.ToString());
            string hotkeyStr = sb.ToString();

            _isUpdatingFields = true;
            item.Hotkey = hotkeyStr;
            DashboardHotkeyTextBox.Text = hotkeyStr;
            _isUpdatingFields = false;
            _viewModel.SelectedContainer.Save();

            var overlayWin = Application.Current.Windows.OfType<DesktopOverlayWindow>().FirstOrDefault();
            overlayWin?.RefreshGlobalHotkeys();
        }

        private void ImportSvgButtonImage_Click(object sender, RoutedEventArgs e)
        {
            if (SvgButtonsListBox.SelectedItem is ShortcutItem item && _viewModel.SelectedContainer != null)
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Import Button Icon (PNG, JPG, SVG)",
                    Filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp;*.svg)|*.png;*.jpg;*.jpeg;*.bmp;*.svg"
                };

                if (dialog.ShowDialog(this) == true)
                {
                    string ext = System.IO.Path.GetExtension(dialog.FileName).ToLowerInvariant();
                    if (ext == ".svg")
                    {
                        try
                        {
                            string content = System.IO.File.ReadAllText(dialog.FileName);
                            item.SvgContent = content;
                            item.IconPath = string.Empty;
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(this, string.Format(TranslationService.Instance["Msg_FailedLoadSvg"], ex.Message), TranslationService.Instance["Msg_Error"], MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                    else
                    {
                        item.IconPath = dialog.FileName;
                        item.SvgContent = string.Empty;
                    }
                    _viewModel.SelectedContainer.Save();
                }
            }
        }

        private void EditSvgButtonXml_Click(object sender, RoutedEventArgs e)
        {
            if (SvgButtonsListBox.SelectedItem is ShortcutItem item && _viewModel.SelectedContainer != null)
            {
                var dialog = new Window
                {
                    Width = 450, Height = 350,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this, WindowStyle = WindowStyle.None,
                    AllowsTransparency = true, Background = Brushes.Transparent,
                    ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
                };

                var border = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x20)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x35)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(20)
                };

                var stack = new StackPanel();
                stack.Children.Add(new TextBlock
                {
                    Text = TranslationService.Instance["Db_EditSvgXmlTitle"], FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC)),
                    Margin = new Thickness(0, 0, 0, 12)
                });

                var textBox = new TextBox
                {
                    Text = item.SvgContent ?? string.Empty,
                    Background = new SolidColorBrush(Color.FromRgb(0x11, 0x16, 0x1B)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x35)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xF1, 0xFF)),
                    AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Height = 180, Padding = new Thickness(8), FontSize = 12,
                    FontFamily = new FontFamily("Consolas")
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
                    Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x24, 0x2E)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x35)),
                    BorderThickness = new Thickness(1),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xF1, 0xFF)),
                    Padding = new Thickness(12, 6, 12, 6), FontSize = 12,
                    Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand
                };
                cancelBtn.Click += (_, _) => dialog.Close();
                btnPanel.Children.Add(cancelBtn);

                var okBtn = new Button
                {
                    Content = TranslationService.Instance["DefaultOptions_Save"],
                    Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x24, 0x2E)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x35)),
                    BorderThickness = new Thickness(1),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC)),
                    Padding = new Thickness(12, 6, 12, 6), FontSize = 12,
                    Cursor = Cursors.Hand
                };
                okBtn.Click += (_, _) =>
                {
                    item.SvgContent = textBox.Text;
                    if (!string.IsNullOrEmpty(item.SvgContent))
                    {
                        item.IconPath = string.Empty;
                    }
                    _viewModel.SelectedContainer.Save();
                    dialog.Close();
                };
                btnPanel.Children.Add(okBtn);

                stack.Children.Add(btnPanel);
                border.Child = stack;
                dialog.Content = border;
                textBox.Focus();
                dialog.ShowDialog();
            }
        }

        private void DeleteSvgButton_Click(object sender, RoutedEventArgs e)
        {
            if (SvgButtonsListBox.SelectedItem is ShortcutItem item && _viewModel.SelectedContainer != null)
            {
                var result = MessageBox.Show(this, TranslationService.Instance["Msg_ConfirmDeleteSvg"], TranslationService.Instance["Msg_ConfirmDeleteTitle"], MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _viewModel.SelectedContainer.Shortcuts.Remove(item);
                    _viewModel.SelectedContainer.Save();
                    
                    var overlay = Application.Current.Windows.OfType<DesktopOverlayWindow>().FirstOrDefault();
                    overlay?.RefreshGlobalHotkeys();
                }
            }
        }

        private void AddSvgButtonDashboard_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.SelectedContainer != null)
            {
                var editWindow = new SvgButtonEditWindow();
                try
                {
                    editWindow.Owner = this;
                }
                catch { }

                if (editWindow.ShowDialog() == true)
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

                    _viewModel.SelectedContainer.Shortcuts.Add(newItem);
                    _viewModel.SelectedContainer.Save();

                    var overlay = Application.Current.Windows.OfType<DesktopOverlayWindow>().FirstOrDefault();
                    overlay?.RefreshGlobalHotkeys();
                }
            }
        }
    }
}
