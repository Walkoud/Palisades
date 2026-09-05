using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Newtonsoft.Json;

namespace Palisades.Plugins
{
    public class NowPlayingPlugin : IPlugin
    {
        public string Name => "Now Playing";
        public string Id => "com.palisades.plugin.nowplaying";
        public string Version => "1.0.0";
        public string Author => "Palisades Team";
        public string Description => "Displays the currently playing media with album art and playback controls.";

        public void OnLoad(PluginContext context)
        {
            context.RegisterGadget(
                gadgetType: "NowPlaying",
                name: "Now Playing",
                viewFactory: () => new NowPlayingView(),
                defaultWidth: 320,
                defaultHeight: 100
            );
        }

        public void OnUnload()
        {
        }
    }

    public class NowPlayingView : Border, ICustomizableGadgetView
    {
        private GlobalSystemMediaTransportControlsSessionManager? _smtcManager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;
        private DispatcherTimer? _positionTimer;

        private readonly Border _artBorder;
        private readonly Image _artImage;
        private readonly TextBlock _placeholderIcon;
        private readonly TextBlock _titleLabel;
        private readonly TextBlock _artistLabel;
        private readonly TextBlock _appLabel;
        private readonly Button _prevBtn;
        private readonly Button _playBtn;
        private readonly TextBlock _playIcon;
        private readonly Button _nextBtn;
        private readonly Border _outerBorder;
        private readonly ProgressBar _seekBar;
        private readonly TextBlock _positionLabel;
        private readonly TextBlock _durationLabel;
        private readonly StackPanel _controlsPanel;

        private bool _isPlaying;
        private bool _seekBarDragging;
        private string _currentTitle = "";
        private string _currentArtist = "";
        private string _layout = "Classic";
        private bool _showSeekBar = true;
        private bool _showControls = true;
        private bool _showAppLabel = true;
        private bool _showCover = true;
        private string _accentColor = "#FF7DD3FC";
        private string _buttonsColor = "#A0FFFFFF";

        // When set, the view ignores automatic selection and pins to this source.
        private GlobalSystemMediaTransportControlsSession? _forcedSession;
        // Persisted identity of the pinned source (survives reboot + export/import).
        private string _forcedSourceAppId = "";

        private class NowPlayingSettings
        {
            public string Layout { get; set; } = "Classic";
            public bool ShowSeekBar { get; set; } = true;
            public bool ShowControls { get; set; } = true;
            public bool ShowAppLabel { get; set; } = true;
            public bool ShowCover { get; set; } = true;
            public string AccentColor { get; set; } = "#FF7DD3FC";
            public string ButtonsColor { get; set; } = "#A0FFFFFF";
            public string ForcedSourceAppId { get; set; } = "";
        }

        public NowPlayingView()
        {
            Background = Brushes.Transparent;
            BorderThickness = new Thickness(0);

            _outerBorder = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };

            // Album art
            _artBorder = new Border
            {
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))
            };

            _artImage = new Image
            {
                Stretch = Stretch.UniformToFill,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            _placeholderIcon = new TextBlock
            {
                Text = "\uE8D6",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 28,
                Foreground = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Labels
            _titleLabel = new TextBlock
            {
                Text = "No media playing",
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 260
            };

            _artistLabel = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 260
            };

            _appLabel = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 260
            };

            // Transport controls
            _controlsPanel = new StackPanel { Orientation = Orientation.Horizontal };

            var btnStyle = CreateTransportButtonStyle();

            _prevBtn = new Button
            {
                Content = "\uE892",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 0, 4, 0),
                Style = btnStyle
            };
            _prevBtn.Click += PrevBtn_Click;
            _controlsPanel.Children.Add(_prevBtn);

            _playIcon = new TextBlock
            {
                Text = "\uE768",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _playBtn = new Button
            {
                Content = _playIcon,
                Width = 32,
                Height = 32,
                Margin = new Thickness(0, 0, 4, 0),
                Style = CreatePlayButtonStyle()
            };
            _playBtn.Click += PlayBtn_Click;
            _controlsPanel.Children.Add(_playBtn);

            _nextBtn = new Button
            {
                Content = "\uE893",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Width = 28,
                Height = 28,
                Style = btnStyle
            };
            _nextBtn.Click += NextBtn_Click;
            _controlsPanel.Children.Add(_nextBtn);

            // Seek bar
            _positionLabel = new TextBlock
            {
                Text = "0:00",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            _seekBar = new ProgressBar
            {
                Height = 4,
                Minimum = 0,
                Maximum = 100,
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC)),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };

            _durationLabel = new TextBlock
            {
                Text = "0:00",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            BuildClassicLayout();

            Loaded += NowPlayingView_Loaded;
            Unloaded += NowPlayingView_Unloaded;
        }

        private FrameworkElement BuildSeekBarRow()
        {
            var seekGrid = new Grid();
            seekGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            seekGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            seekGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

            _positionLabel.Margin = new Thickness(0);
            _seekBar.Height = 4;
            _seekBar.Margin = new Thickness(6, 0, 6, 0);
            _durationLabel.Margin = new Thickness(0);

            Grid.SetColumn(_positionLabel, 0);
            Grid.SetColumn(_seekBar, 1);
            Grid.SetColumn(_durationLabel, 2);

            seekGrid.Children.Add(_positionLabel);
            seekGrid.Children.Add(_seekBar);
            seekGrid.Children.Add(_durationLabel);

            _seekBar.Visibility = _showSeekBar ? Visibility.Visible : Visibility.Collapsed;
            _positionLabel.Visibility = _showSeekBar ? Visibility.Visible : Visibility.Collapsed;
            _durationLabel.Visibility = _showSeekBar ? Visibility.Visible : Visibility.Collapsed;

            return seekGrid;
        }

        private FrameworkElement BuildControlsRow(bool compact)
        {
            _controlsPanel.Visibility = _showControls ? Visibility.Visible : Visibility.Collapsed;
            _controlsPanel.LayoutTransform = null;
            if (compact)
            {
                _controlsPanel.Margin = new Thickness(0);
                _controlsPanel.HorizontalAlignment = HorizontalAlignment.Right;
                _controlsPanel.VerticalAlignment = VerticalAlignment.Center;
            }
            else
            {
                _controlsPanel.Margin = new Thickness(0, 4, 0, 0);
                _controlsPanel.HorizontalAlignment = HorizontalAlignment.Left;
                _controlsPanel.VerticalAlignment = VerticalAlignment.Top;
            }
            return _controlsPanel;
        }

        private void BuildClassicLayout()
        {
            _artBorder.Width = 72;
            _artBorder.Height = 72;
            _artBorder.Margin = new Thickness(0, 0, 10, 0);
            _artBorder.HorizontalAlignment = HorizontalAlignment.Left;
            _artBorder.VerticalAlignment = VerticalAlignment.Center;
            _artBorder.Visibility = _showCover ? Visibility.Visible : Visibility.Collapsed;

            _titleLabel.FontSize = 13;
            _titleLabel.Margin = new Thickness(0);
            _artistLabel.Margin = new Thickness(0, 1, 0, 0);
            _appLabel.Margin = new Thickness(0, 2, 0, 0);
            _appLabel.FontSize = 9;

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var contentGrid = new Grid { Margin = new Thickness(10, 8, 10, 4) };
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var infoStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = _showCover ? new Thickness(82, 0, 0, 0) : new Thickness(0)
            };
            infoStack.Children.Add(_titleLabel);
            infoStack.Children.Add(_artistLabel);
            infoStack.Children.Add(_appLabel);
            infoStack.Children.Add(BuildControlsRow(false));

            Grid.SetColumn(_artBorder, 0);
            Grid.SetColumn(infoStack, 1);

            contentGrid.Children.Add(_artBorder);
            contentGrid.Children.Add(infoStack);
            Grid.SetRow(contentGrid, 0);
            rootGrid.Children.Add(contentGrid);

            var seekRow = BuildSeekBarRow();
            seekRow.Margin = new Thickness(10, 0, 10, 8);
            Grid.SetRow(seekRow, 1);
            rootGrid.Children.Add(seekRow);

            _outerBorder.Child = rootGrid;
            Child = _outerBorder;
        }

        private void BuildCompactLayout()
        {
            _artBorder.Width = 40;
            _artBorder.Height = 40;
            _artBorder.Margin = new Thickness(0, 0, 8, 0);
            _artBorder.HorizontalAlignment = HorizontalAlignment.Left;
            _artBorder.VerticalAlignment = VerticalAlignment.Center;
            _artBorder.Visibility = _showCover ? Visibility.Visible : Visibility.Collapsed;

            _titleLabel.FontSize = 12;
            _titleLabel.Margin = new Thickness(0);
            _artistLabel.Margin = new Thickness(0, 1, 0, 0);
            _appLabel.Margin = new Thickness(0, 1, 0, 0);
            _appLabel.FontSize = 8;

            var rootGrid = new Grid { Margin = new Thickness(8, 6, 8, 6) };
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            infoStack.Children.Add(_titleLabel);
            infoStack.Children.Add(_artistLabel);
            if (_showAppLabel)
                infoStack.Children.Add(_appLabel);

            Grid.SetColumn(_artBorder, 0);
            Grid.SetColumn(infoStack, 1);

            var controlsPanel = BuildControlsRow(true);
            Grid.SetColumn(controlsPanel, 2);

            rootGrid.Children.Add(_artBorder);
            rootGrid.Children.Add(infoStack);
            rootGrid.Children.Add(controlsPanel);

            _outerBorder.Child = rootGrid;
            Child = _outerBorder;
        }

        private void BuildFluentLayout()
        {
            _artBorder.Width = 96;
            _artBorder.Height = 96;
            _artBorder.Margin = new Thickness(0, 0, 14, 0);
            _artBorder.HorizontalAlignment = HorizontalAlignment.Left;
            _artBorder.VerticalAlignment = VerticalAlignment.Stretch;
            _artBorder.Visibility = _showCover ? Visibility.Visible : Visibility.Collapsed;

            _titleLabel.FontSize = 16;
            _titleLabel.Margin = new Thickness(0);
            _artistLabel.FontSize = 12;
            _artistLabel.Margin = new Thickness(0, 2, 0, 0);
            _appLabel.FontSize = 10;
            _appLabel.Margin = new Thickness(0, 3, 0, 0);

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var topGrid = new Grid { Margin = new Thickness(12, 10, 12, 6) };
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var infoStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            infoStack.Children.Add(_titleLabel);
            infoStack.Children.Add(_artistLabel);
            infoStack.Children.Add(_appLabel);
            infoStack.Children.Add(BuildControlsRow(false));

            Grid.SetColumn(_artBorder, 0);
            Grid.SetColumn(infoStack, 1);

            topGrid.Children.Add(_artBorder);
            topGrid.Children.Add(infoStack);
            Grid.SetRow(topGrid, 0);
            rootGrid.Children.Add(topGrid);

            var seekRow = BuildSeekBarRow();
            seekRow.Margin = new Thickness(12, 0, 12, 10);
            Grid.SetRow(seekRow, 1);
            rootGrid.Children.Add(seekRow);

            _outerBorder.Child = rootGrid;
            Child = _outerBorder;
        }

        private void BuildTaskbarLayout()
        {
            // Taskbar bar: compact single row [cover | title/artist | controls] + thin progress.
            // (Takes over the former Slim role: fits the Windows taskbar height.)
            _artBorder.Width = 32;
            _artBorder.Height = 32;
            _artBorder.Margin = new Thickness(0, 0, 6, 0);
            _artBorder.HorizontalAlignment = HorizontalAlignment.Left;
            _artBorder.VerticalAlignment = VerticalAlignment.Center;
            _artBorder.Visibility = _showCover ? Visibility.Visible : Visibility.Collapsed;

            _titleLabel.FontSize = 11;
            _titleLabel.Margin = new Thickness(0);
            _artistLabel.FontSize = 9;
            _artistLabel.Margin = new Thickness(0, 0, 0, 0);
            _appLabel.Visibility = Visibility.Collapsed;

            _seekBar.Height = 2;
            _seekBar.Margin = new Thickness(0);
            _seekBar.Visibility = _showSeekBar ? Visibility.Visible : Visibility.Collapsed;
            _positionLabel.Visibility = Visibility.Collapsed;
            _durationLabel.Visibility = Visibility.Collapsed;

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var topGrid = new Grid { Margin = new Thickness(6, 4, 6, 4) };
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            infoStack.Children.Add(_titleLabel);
            infoStack.Children.Add(_artistLabel);

            Grid.SetColumn(_artBorder, 0);
            Grid.SetColumn(infoStack, 1);

            var controlsPanel = BuildControlsRow(true);
            Grid.SetColumn(controlsPanel, 2);

            topGrid.Children.Add(_artBorder);
            topGrid.Children.Add(infoStack);
            topGrid.Children.Add(controlsPanel);
            Grid.SetRow(topGrid, 0);
            rootGrid.Children.Add(topGrid);

            Grid.SetRow(_seekBar, 1);
            rootGrid.Children.Add(_seekBar);

            _outerBorder.Child = rootGrid;
            Child = _outerBorder;
        }

        private void BuildTaskbarSlimLayout()
        {
            // Micro bar: ~30% smaller than Taskbar (scaled controls, mini cover).
            _artBorder.Width = 22;
            _artBorder.Height = 22;
            _artBorder.Margin = new Thickness(0, 0, 6, 0);
            _artBorder.HorizontalAlignment = HorizontalAlignment.Left;
            _artBorder.VerticalAlignment = VerticalAlignment.Center;
            _artBorder.Visibility = _showCover ? Visibility.Visible : Visibility.Collapsed;

            _titleLabel.FontSize = 9;
            _titleLabel.Margin = new Thickness(0);
            _artistLabel.FontSize = 8;
            _artistLabel.Margin = new Thickness(0, 0, 0, 0);
            _appLabel.Visibility = Visibility.Collapsed;

            _seekBar.Height = 2;
            _seekBar.Margin = new Thickness(0);
            _seekBar.Visibility = _showSeekBar ? Visibility.Visible : Visibility.Collapsed;
            _positionLabel.Visibility = Visibility.Collapsed;
            _durationLabel.Visibility = Visibility.Collapsed;

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var topGrid = new Grid { Margin = new Thickness(6, 3, 6, 3) };
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            infoStack.Children.Add(_titleLabel);
            infoStack.Children.Add(_artistLabel);

            Grid.SetColumn(_artBorder, 0);
            Grid.SetColumn(infoStack, 1);

            var controlsPanel = BuildControlsRow(true);
            _controlsPanel.LayoutTransform = new ScaleTransform(0.7, 0.7);
            Grid.SetColumn(controlsPanel, 2);

            topGrid.Children.Add(_artBorder);
            topGrid.Children.Add(infoStack);
            topGrid.Children.Add(controlsPanel);
            Grid.SetRow(topGrid, 0);
            rootGrid.Children.Add(topGrid);

            Grid.SetRow(_seekBar, 1);
            rootGrid.Children.Add(_seekBar);

            _outerBorder.Child = rootGrid;
            Child = _outerBorder;
        }

        private void Detach(FrameworkElement el)
        {
            var parent = el.Parent;
            if (parent is Panel panel) panel.Children.Remove(el);
            else if (parent is ContentControl cc) cc.Content = null;
            else if (parent is Decorator dec) dec.Child = null;
        }

        private void DetachAllPrimitives()
        {
            Detach(_artBorder);
            Detach(_titleLabel);
            Detach(_artistLabel);
            Detach(_appLabel);
            Detach(_controlsPanel);
            Detach(_seekBar);
            Detach(_positionLabel);
            Detach(_durationLabel);
        }

        private void RebuildLayout()
        {
            _outerBorder.Child = null;
            DetachAllPrimitives();
            switch (_layout)
            {
                case "Compact":
                    BuildCompactLayout();
                    break;
                case "Fluent":
                    BuildFluentLayout();
                    break;
                case "Taskbar":
                    BuildTaskbarLayout();
                    break;
                case "TaskbarSlim":
                    BuildTaskbarSlimLayout();
                    break;
                default:
                    BuildClassicLayout();
                    break;
            }
            ApplyLabelVisibility();
        }

        private void ApplyLabelVisibility()
        {
            bool showApp = _showAppLabel && !string.IsNullOrEmpty(_appLabel.Text) && _layout != "TaskbarSlim";
            _appLabel.Visibility = showApp ? Visibility.Visible : Visibility.Collapsed;
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

        private void ApplyAccentColors()
        {
            try
            {
                Color accent = ParseColor(_accentColor, Color.FromRgb(0x7D, 0xD3, 0xFC));
                Color buttons = ParseColor(_buttonsColor, Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));

                _seekBar.Foreground = new SolidColorBrush(accent);
                var btnStyle = CreateTransportButtonStyle(buttons);
                _prevBtn.Style = btnStyle;
                _nextBtn.Style = btnStyle;
                _playBtn.Style = CreatePlayButtonStyle(accent);
            }
            catch { }
        }

        private Style CreateTransportButtonStyle()
        {
            return CreateTransportButtonStyle(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));
        }

        private Style CreateTransportButtonStyle(Color foreground)
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Button.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Button.ForegroundProperty, new SolidColorBrush(foreground)));
            style.Setters.Add(new Setter(Button.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets")));
            style.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(Button.FocusableProperty, false));
            style.Setters.Add(new Setter(Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(Button.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(Button.MinWidthProperty, 0.0));
            style.Setters.Add(new Setter(Button.MinHeightProperty, 0.0));

            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            template.VisualTree = border;
            style.Setters.Add(new Setter(Button.TemplateProperty, template));

            var hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF))));
            style.Triggers.Add(hover);

            return style;
        }

        private Style CreatePlayButtonStyle()
        {
            return CreatePlayButtonStyle(Color.FromRgb(0x7D, 0xD3, 0xFC));
        }

        private Style CreatePlayButtonStyle(Color accent)
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B))));
            style.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Button.ForegroundProperty, new SolidColorBrush(accent)));
            style.Setters.Add(new Setter(Button.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets")));
            style.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(Button.FocusableProperty, false));
            style.Setters.Add(new Setter(Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(Button.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(Button.MinWidthProperty, 0.0));
            style.Setters.Add(new Setter(Button.MinHeightProperty, 0.0));

            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            template.VisualTree = border;
            style.Setters.Add(new Setter(Button.TemplateProperty, template));

            var hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x50, accent.R, accent.G, accent.B))));
            style.Triggers.Add(hover);

            return style;
        }

        private async void NowPlayingView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _smtcManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _smtcManager.CurrentSessionChanged += SmtcManager_CurrentSessionChanged;
                _smtcManager.SessionsChanged += SmtcManager_SessionsChanged;

                RefreshSessions();
            }
            catch { }

            _positionTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _positionTimer.Tick += PositionTimer_Tick;
            _positionTimer.Start();
        }

        private void NowPlayingView_Unloaded(object sender, RoutedEventArgs e)
        {
            _positionTimer?.Stop();
            _positionTimer = null;

            if (_smtcManager != null)
            {
                try
                {
                    _smtcManager.CurrentSessionChanged -= SmtcManager_CurrentSessionChanged;
                    _smtcManager.SessionsChanged -= SmtcManager_SessionsChanged;
                }
                catch { }
                _smtcManager = null;
            }

            DetachAll();
        }

        // Track every session so we can follow the one that is actually active.
        private readonly List<GlobalSystemMediaTransportControlsSession> _knownSessions = new();

        private void DetachAll()
        {
            foreach (var s in _knownSessions)
            {
                s.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                s.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
                s.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
            }
            _knownSessions.Clear();
            _currentSession = null;
        }

        private void RefreshSessions()
        {
            if (_smtcManager == null) return;

            try
            {
                var current = _smtcManager.GetSessions().ToList();
                foreach (var s in current)
                {
                    if (!_knownSessions.Contains(s))
                    {
                        s.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                        s.PlaybackInfoChanged += Session_PlaybackInfoChanged;
                        s.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
                        _knownSessions.Add(s);
                    }
                }

                // Drop sessions that no longer exist / are disconnected
                var dropped = _knownSessions.Where(s => !current.Contains(s)).ToList();
                _knownSessions.RemoveAll(s => !current.Contains(s));
                foreach (var s in dropped)
                {
                    try
                    {
                        string goneId = s.SourceAppUserModelId ?? "";
                        if (!string.IsNullOrEmpty(goneId))
                            Palisades.Services.DiscordPresenceService.Instance.ReportSessionGone(goneId);
                    }
                    catch { }
                }

                ReportSessionsSnapshot(current);

                ResolveActiveSession();
            }
            catch { }
        }

        /// <summary>Feeds every known session to Discord so its priority list sees
        /// all apps, not just the followed one (lightweight: no cover bytes here).</summary>
        private void ReportSessionsSnapshot(System.Collections.Generic.List<Windows.Media.Control.GlobalSystemMediaTransportControlsSession> current)
        {
            var svc = Palisades.Services.DiscordPresenceService.Instance;
            var ids = new System.Collections.Generic.List<string>();
            foreach (var s in current)
            {
                string appId = "";
                try { appId = s.SourceAppUserModelId ?? ""; } catch { continue; }
                if (string.IsNullOrEmpty(appId)) continue;
                ids.Add(appId);
                bool playing = false;
                try { playing = s.GetPlaybackInfo()?.PlaybackStatus == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; } catch { }
                _ = ReportOneSessionAsync(svc, s, appId, playing);
            }
            svc.RetainOnly(ids);
        }

        private static async System.Threading.Tasks.Task ReportOneSessionAsync(
            Palisades.Services.DiscordPresenceService svc,
            Windows.Media.Control.GlobalSystemMediaTransportControlsSession s,
            string appId, bool playing)
        {
            try
            {
                var props = await s.TryGetMediaPropertiesAsync();
                svc.ReportSession(appId, props?.Title ?? "", props?.Artist ?? "", playing);
            }
            catch { }
        }

        private void SmtcManager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            Dispatcher.Invoke(RefreshSessions);
        }

        private void SmtcManager_SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        {
            // New source arrived / gone (app launch/close, reboot restore) → re-resolve,
            // re-applying the persisted pin when its app is back.
            Dispatcher.Invoke(RefreshSessions);
        }

        // Pick the session that is currently Playing; fall back to the first known one.
        private void ResolveActiveSession()
        {
            GlobalSystemMediaTransportControlsSession? active = null;

            // User pinned a specific source (persisted app id) → keep it while it still exists.
            _forcedSession = null;
            if (!string.IsNullOrEmpty(_forcedSourceAppId))
            {
                foreach (var s in _knownSessions)
                {
                    try
                    {
                        if (string.Equals(s.SourceAppUserModelId, _forcedSourceAppId, StringComparison.OrdinalIgnoreCase))
                        {
                            _forcedSession = s;
                            break;
                        }
                    }
                    catch { }
                }
                if (_forcedSession != null)
                    active = _forcedSession;
                // else: pinned app absent → keep the id (re-pins when it comes back),
                // fall through to automatic display meanwhile.
            }

            if (active == null)
            {
                // Prefer the OS-focused/current session if exposed.
                try
                {
                    var current = _smtcManager?.GetCurrentSession();
                    if (current != null && _knownSessions.Contains(current))
                        active = current;
                }
                catch { }

                if (active == null)
                {
                    foreach (var s in _knownSessions)
                    {
                        try
                        {
                            var info = s.GetPlaybackInfo();
                            if (info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                            {
                                active = s;
                                break;
                            }
                        }
                        catch { }
                    }
                }

                if (active == null && _knownSessions.Count > 0)
                    active = _knownSessions[0];
            }

            if (active != null && !ReferenceEquals(active, _currentSession))
            {
                _currentSession = active;
                UpdateMediaProperties(active);
                UpdatePlaybackState(active);
                UpdateTimeline(active);
            }
            else if (active == null)
            {
                ShowEmpty();
            }
        }

        private void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            Dispatcher.Invoke(() =>
            {
                if (ReferenceEquals(sender, _currentSession))
                    UpdateMediaProperties(sender);
                else
                    ResolveActiveSession();
            });
        }

        private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            Dispatcher.Invoke(() =>
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus status;
                try { status = sender.GetPlaybackInfo().PlaybackStatus; }
                catch { return; }

                bool isPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                if (!ReferenceEquals(sender, _currentSession))
                {
                    // Another session started playing → it takes focus (pin respected).
                    if (isPlaying)
                    {
                        ResolveActiveSession();
                        return;
                    }
                    // Paused/stopped event from a background session: ignore.
                    // (Reporting it would clobber the followed session's state,
                    // e.g. closing a YouTube video while Spotify still plays.)
                    return;
                }

                // Event from the followed session.
                if (!isPlaying)
                {
                    // It paused/stopped: follow another playing session if any
                    // (or keep the pin), instead of reporting idle.
                    ResolveActiveSession();
                    if (ReferenceEquals(_currentSession, sender))
                        UpdatePlaybackState(sender); // truly idle → reports paused
                    return;
                }

                UpdatePlaybackState(sender);
            });
        }

        private void Session_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            Dispatcher.Invoke(() =>
            {
                if (ReferenceEquals(sender, _currentSession))
                    UpdateTimeline(sender);
            });
        }

        private async void UpdateMediaProperties(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                _currentTitle = props.Title ?? "";
                _currentArtist = props.Artist ?? "";
                var app = session.SourceAppUserModelId ?? "";

                _titleLabel.Text = string.IsNullOrEmpty(_currentTitle) ? "Unknown" : _currentTitle;
                _artistLabel.Text = _currentArtist;
                _artistLabel.Visibility = string.IsNullOrEmpty(_currentArtist) ? Visibility.Collapsed : Visibility.Visible;

                // Clean up app name (e.g. "Spotify" from "Spotify.exe")
                if (app.Contains('.'))
                    app = app.Substring(0, app.LastIndexOf('.'));
                _appLabel.Text = app;
                ApplyLabelVisibility();

                // Album art (bytes kept for the Discord cover upload)
                byte[]? coverBytes = null;
                if (props.Thumbnail != null)
                {
                    try
                    {
                        var stream = await props.Thumbnail.OpenReadAsync();
                        using (var ms = new System.IO.MemoryStream())
                        {
                            await stream.AsStreamForRead().CopyToAsync(ms);
                            coverBytes = ms.ToArray();
                        }
                        if (coverBytes.Length == 0) coverBytes = null;
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = new System.IO.MemoryStream(coverBytes);
                        bitmap.DecodePixelWidth = 144;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze();

                        _artImage.Source = bitmap;
                        _artBorder.Child = _artImage;
                    }
                    catch
                    {
                        coverBytes = null;
                        _artBorder.Child = _placeholderIcon;
                    }
                }
                else
                {
                    _artBorder.Child = _placeholderIcon;
                }
                _lastCoverBytes = (coverBytes != null && coverBytes.Length > 0) ? coverBytes : null;
                Palisades.App.Log("[Discord] thumb for '" + _currentTitle + "': streamNull=" + (props.Thumbnail == null) + " bytes=" + (_lastCoverBytes?.Length ?? 0));
            }
            catch
            {
                _titleLabel.Text = "Unknown";
                _artistLabel.Text = "";
                _artBorder.Child = _placeholderIcon;
            }

            ReportToDiscord();
        }

        private byte[]? _lastCoverBytes;

        private void ReportToDiscord()
        {
            try
            {
                TimeSpan pos = TimeSpan.Zero;
                string appId = "";
                try
                {
                    if (_currentSession != null)
                    {
                        pos = _currentSession.GetTimelineProperties().Position;
                        appId = _currentSession.SourceAppUserModelId ?? "";
                    }
                }
                catch { }
                Palisades.Services.DiscordPresenceService.Instance.Report(
                    _currentTitle, _currentArtist, _appLabel.Text ?? "", appId, _isPlaying, pos, _lastCoverBytes);
            }
            catch { }
        }

        private void UpdatePlaybackState(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                var info = session.GetPlaybackInfo();
                _isPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                _playIcon.Text = _isPlaying ? "\uE769" : "\uE768"; // Pause : Play

                bool canPrev = info.Controls.IsPreviousEnabled;
                bool canNext = info.Controls.IsNextEnabled;
                bool canPlay = info.Controls.IsPlayEnabled || info.Controls.IsPauseEnabled;

                _prevBtn.Visibility = canPrev ? Visibility.Visible : Visibility.Collapsed;
                _nextBtn.Visibility = canNext ? Visibility.Visible : Visibility.Collapsed;
                _playBtn.Visibility = canPlay ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }

            // Thumbnail sometimes arrives after the first props (or was null then):
            // refetch once so the Discord cover can still resolve.
            if (_lastCoverBytes == null && _currentSession != null && !string.IsNullOrEmpty(_currentTitle))
            {
                try { UpdateMediaProperties(_currentSession); } catch { }
            }

            ReportToDiscord();
        }

        private void UpdateTimeline(GlobalSystemMediaTransportControlsSession session)
        {
            if (_seekBarDragging) return;

            try
            {
                var timeline = session.GetTimelineProperties();
                var pos = timeline.Position;
                var end = timeline.EndTime;

                if (end.TotalMilliseconds > 0)
                {
                    _seekBar.Maximum = end.TotalMilliseconds;
                    _seekBar.Value = Math.Min(pos.TotalMilliseconds, end.TotalMilliseconds);
                    _positionLabel.Text = FormatTime(pos);
                    _durationLabel.Text = FormatTime(end);
                }
                else
                {
                    _seekBar.Maximum = 100;
                    _seekBar.Value = 0;
                    _positionLabel.Text = "0:00";
                    _durationLabel.Text = "0:00";
                }
            }
            catch { }
        }

        private void PositionTimer_Tick(object? sender, EventArgs e)
        {
            if (_currentSession == null) return;
            UpdateTimeline(_currentSession);
        }

        private async void PrevBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentSession != null)
                    await _currentSession.TrySkipPreviousAsync();
            }
            catch { }
        }

        private async void PlayBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentSession != null)
                    await _currentSession.TryTogglePlayPauseAsync();
            }
            catch { }
        }

        private async void NextBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentSession != null)
                    await _currentSession.TrySkipNextAsync();
            }
            catch { }
        }

        private void ShowEmpty()
        {
            _titleLabel.Text = "No media playing";
            _artistLabel.Text = "";
            _appLabel.Text = "";
            _artBorder.Child = _placeholderIcon;
            _playIcon.Text = "\uE768";
            _seekBar.Value = 0;
            _positionLabel.Text = "0:00";
            _durationLabel.Text = "0:00";
        }

        private static string FormatTime(TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }

        // ---- Manual source selection (used by the gadget context menu) ----

        public bool IsManualSource => !string.IsNullOrEmpty(_forcedSourceAppId);

        public string PinnedSourceAppId => _forcedSourceAppId;

        public string PinnedSourceName
        {
            get
            {
                var app = _forcedSourceAppId ?? "";
                if (app.Contains('.'))
                    app = app.Substring(0, app.LastIndexOf('.'));
                return string.IsNullOrEmpty(app) ? "Unknown" : app;
            }
        }

        public GlobalSystemMediaTransportControlsSession? ActiveSession => _currentSession;

        public IReadOnlyList<GlobalSystemMediaTransportControlsSession> SourceSessions => _knownSessions;

        public string SourceDisplayName(GlobalSystemMediaTransportControlsSession s)
        {
            var app = "";
            try { app = s.SourceAppUserModelId ?? ""; } catch { }
            if (app.Contains('.'))
                app = app.Substring(0, app.LastIndexOf('.'));
            return string.IsNullOrEmpty(app) ? "Unknown" : app;
        }

        public void FollowSource(GlobalSystemMediaTransportControlsSession? session)
        {
            _forcedSession = session;
            try { _forcedSourceAppId = session?.SourceAppUserModelId ?? ""; } catch { _forcedSourceAppId = ""; }
            if (session == null)
            {
                ResolveActiveSession();
                return;
            }
            if (_knownSessions.Contains(session))
            {
                _currentSession = session;
                UpdateMediaProperties(session);
                UpdatePlaybackState(session);
                UpdateTimeline(session);
            }
            else
            {
                ResolveActiveSession();
            }
        }

        public void ApplyCustomSettings(string customData)
        {
            try
            {
                var settings = string.IsNullOrEmpty(customData) ? new NowPlayingSettings() : JsonConvert.DeserializeObject<NowPlayingSettings>(customData);
                if (settings == null) return;

                _layout = string.IsNullOrEmpty(settings.Layout) ? "Classic" : settings.Layout;
                _showSeekBar = settings.ShowSeekBar;
                _showControls = settings.ShowControls;
                _showAppLabel = settings.ShowAppLabel;
                _showCover = settings.ShowCover;
                _accentColor = string.IsNullOrEmpty(settings.AccentColor) ? "#FF7DD3FC" : settings.AccentColor;
                _buttonsColor = string.IsNullOrEmpty(settings.ButtonsColor) ? "#A0FFFFFF" : settings.ButtonsColor;
                _forcedSourceAppId = settings.ForcedSourceAppId ?? "";

                RebuildLayout();
                ApplyAccentColors();
                if (_smtcManager != null)
                    ResolveActiveSession();
            }
            catch { }
        }
    }
}
