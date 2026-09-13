using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json;
using Palisades.Services;

namespace Palisades.Plugins
{
    public class RadioPlugin : IPlugin
    {
        public string Name => "Radio";
        public string Id => "com.palisades.plugin.radio";
        public string Version => "1.0.0";
        public string Author => "Palisades Team";
        public string Description => "Live web radio widget (Radio Browser, 40k+ stations, no key needed).";

        public void OnLoad(PluginContext context)
        {
            context.RegisterGadget(
                gadgetType: "Radio",
                name: "Radio",
                viewFactory: () => new RadioView(),
                defaultWidth: 320,
                defaultHeight: 190
            );
        }

        public void OnUnload()
        {
        }
    }

    public class RadioFavStation
    {
        public string Uuid { get; set; } = "";
        public string Name { get; set; } = "";
        public string Stream { get; set; } = "";
        public string Favicon { get; set; } = "";
        public string Tags { get; set; } = "";
    }

    public class RadioSettings
    {
        public List<RadioFavStation> Favorites { get; set; } = new List<RadioFavStation>();
        public double Volume { get; set; } = 0.8;
        public string LastUuid { get; set; } = "";
        public bool ShowInNowPlaying { get; set; } = true;
        public bool ShowOnDiscord { get; set; } = true;
    }

    public class RadioView : Border, ICustomizableGadgetView
    {
        // ---- shared visual language (matches Football dark cards) ----
        private static readonly SolidColorBrush TextBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
        private static readonly SolidColorBrush DimBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        private static readonly SolidColorBrush FaintBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
        private static readonly SolidColorBrush MutedBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8E, 0x96));
        private static readonly SolidColorBrush AccentBrush = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC));
        private static readonly SolidColorBrush LiveBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0xF6, 0x26));
        private static readonly SolidColorBrush CardBrush = new SolidColorBrush(Color.FromRgb(0x18, 0x19, 0x1C));
        private static readonly SolidColorBrush ChipBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x22));
        private static readonly SolidColorBrush ChipBorder = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

        private RadioSettings _settings = new RadioSettings();
        private readonly MediaPlayer _player = new MediaPlayer();

        private readonly TextBlock _statusDot;
        private readonly TextBlock _statusText;
        private readonly Image _logo;
        private readonly TextBlock _name;
        private readonly TextBlock _tags;
        private readonly TextBlock _playGlyph;
        private readonly TextBlock _favGlyph;
        private readonly Slider _volume;
        private readonly StackPanel _listPanel;
        private readonly TextBlock _hint;

        private RadioFavStation? _current;
        private bool _playing;
        private bool _suppressVolume;
        private readonly DispatcherTimer _volumeSaveTimer;
        private Action? _toggleHook;
        private Action? _nextHook;
        private Action? _prevHook;

        public RadioView()
        {
            _volumeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _volumeSaveTimer.Tick += (_, _) => { _volumeSaveTimer.Stop(); SaveSettings(); };

            Background = Brushes.Transparent;
            BorderThickness = new Thickness(0);

            var root = new Grid { Margin = new Thickness(10, 8, 10, 8) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // now-playing card
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // controls
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // list

            // ---------- header ----------
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock
            {
                Text = "📻 Radio", FontWeight = FontWeights.SemiBold, FontSize = 13,
                Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center
            });
            var statusStack = new StackPanel
            {
                Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            _statusDot = new TextBlock { Text = "●", FontSize = 9, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center };
            statusStack.Children.Add(_statusDot);
            _statusText = new TextBlock
            {
                Text = "Stopped", FontSize = 10, Foreground = DimBrush,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0)
            };
            statusStack.Children.Add(_statusText);
            Grid.SetColumn(statusStack, 1);
            header.Children.Add(statusStack);
            var searchToggle = BuildIconButton("🔍", "Search stations", OpenSearch);
            Grid.SetColumn(searchToggle, 2);
            header.Children.Add(searchToggle);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // ---------- now-playing card ----------
            var card = new Border
            {
                Background = CardBrush, CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 6, 0, 6)
            };
            var cardGrid = new Grid();
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var logoBox = new Border
            {
                Width = 44, Height = 44, CornerRadius = new CornerRadius(6),
                Background = ChipBrush, Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _logo = new Image { Stretch = Stretch.Uniform, Width = 44, Height = 44 };
            RenderOptions.SetBitmapScalingMode(_logo, BitmapScalingMode.HighQuality);
            logoBox.Child = _logo;
            Grid.SetColumn(logoBox, 0);
            cardGrid.Children.Add(logoBox);

            var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _name = new TextBlock
            {
                Text = "Pick a station", FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush, TextTrimming = TextTrimming.CharacterEllipsis, Cursor = Cursors.Hand
            };
            _name.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (_current != null) OpenGoogle(_current.Name + " radio");
            };
            meta.Children.Add(_name);
            _tags = new TextBlock { Text = "", FontSize = 10, Foreground = MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis };
            meta.Children.Add(_tags);
            Grid.SetColumn(meta, 1);
            cardGrid.Children.Add(meta);

            var playBorder = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(20),
                Background = AccentBrush, Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
            };
            _playGlyph = new TextBlock
            {
                Text = "▶", FontSize = 15, Foreground = new SolidColorBrush(Colors.Black),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0)
            };
            playBorder.Child = _playGlyph;
            playBorder.MouseLeftButtonDown += (_, e) => { e.Handled = true; TogglePlay(); };

            _favGlyph = new TextBlock
            {
                Text = "☆", FontSize = 20, Foreground = MutedBrush,
                Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 4, 0), ToolTip = "Favorite"
            };
            _favGlyph.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                ToggleCurrentFavorite();
            };

            var actionRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            actionRow.Children.Add(_favGlyph);
            actionRow.Children.Add(playBorder);
            Grid.SetColumn(actionRow, 2);
            cardGrid.Children.Add(actionRow);
            card.Child = cardGrid;
            Grid.SetRow(card, 1);
            root.Children.Add(card);

            // ---------- controls ----------
            var controls = new Grid { Margin = new Thickness(2, 0, 2, 2) };
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var prev = BuildIconButton("⏮", "Previous favorite", () => StepFavorite(-1));
            Grid.SetColumn(prev, 0);
            controls.Children.Add(prev);
            var next = BuildIconButton("⏭", "Next favorite", () => StepFavorite(1));
            next.Margin = new Thickness(2, 0, 8, 0);
            Grid.SetColumn(next, 1);
            controls.Children.Add(next);

            _volume = new Slider
            {
                Minimum = 0, Maximum = 0.5, VerticalAlignment = VerticalAlignment.Center,
                SmallChange = 0.02, LargeChange = 0.05, ToolTip = "Volume", Margin = new Thickness(0, 0, 6, 0)
            };
            ApplySlimSliderStyle(_volume);
            _volume.ValueChanged += (_, _) =>
            {
                SetPlayerVolume(_volume.Value);
                if (_suppressVolume) return;
                _settings.Volume = Math.Min(1.0, _volume.Value * 2.0);
                _volumeSaveTimer.Stop();
                _volumeSaveTimer.Start();
            };
            Grid.SetColumn(_volume, 2);
            controls.Children.Add(_volume);

            var volIcon = new TextBlock { Text = "🔊", FontSize = 12, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(volIcon, 3);
            controls.Children.Add(volIcon);
            Grid.SetRow(controls, 2);
            root.Children.Add(controls);

            // ---------- list ----------
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 4, 0, 0)
            };
            _listPanel = new StackPanel();
            scroll.Content = _listPanel;
            _hint = new TextBlock
            {
                Text = "", FontSize = 10, Foreground = FaintBrush,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 2, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            var listHost = new Grid();
            listHost.Children.Add(scroll);
            listHost.Children.Add(_hint);
            Grid.SetRow(listHost, 3);
            root.Children.Add(listHost);

            Child = root;

            SetPlayerVolume(_settings.Volume);
            _player.MediaOpened += (_, _) => Dispatcher.InvokeAsync(() => SetStatus(true, "Playing"));
            _player.MediaFailed += (_, _) => Dispatcher.InvokeAsync(() => SetStatus(false, "Stream error"));

            Loaded += (_, _) =>
            {
                RenderAll();
                _toggleHook = () => Dispatcher.InvokeAsync(TogglePlay);
                _nextHook = () => Dispatcher.InvokeAsync(() => StepFavorite(1));
                _prevHook = () => Dispatcher.InvokeAsync(() => StepFavorite(-1));
                ExternalNowPlaying.TogglePlayPause = _toggleHook;
                ExternalNowPlaying.SkipNext = _nextHook;
                ExternalNowPlaying.SkipPrevious = _prevHook;
            };
            Unloaded += (_, _) =>
            {
                if (_toggleHook != null && ReferenceEquals(ExternalNowPlaying.TogglePlayPause, _toggleHook))
                {
                    ExternalNowPlaying.TogglePlayPause = null;
                    ExternalNowPlaying.SkipNext = null;
                    ExternalNowPlaying.SkipPrevious = null;
                }
                _toggleHook = _nextHook = _prevHook = null;
                try { _player.Stop(); _player.Close(); } catch { }
                try { ExternalNowPlaying.Clear("Radio"); } catch { }
            };
        }

        // ================= settings =================

        public void ApplyCustomSettings(string customData)
        {
            try
            {
                var s = string.IsNullOrEmpty(customData)
                    ? new RadioSettings()
                    : JsonConvert.DeserializeObject<RadioSettings>(customData) ?? new RadioSettings();
                _settings.Favorites = s.Favorites?
                    .Where(f => f != null && !string.IsNullOrEmpty(f.Uuid))
                    .GroupBy(f => f.Uuid).Select(g => g.First()).ToList() ?? new List<RadioFavStation>();
                _settings.Volume = s.Volume <= 0 ? 0.8 : Math.Clamp(s.Volume, 0, 1);
                _settings.LastUuid = s.LastUuid ?? "";
                _settings.ShowInNowPlaying = s.ShowInNowPlaying;
                _settings.ShowOnDiscord = s.ShowOnDiscord;
                double sliderVal = _settings.Volume / 2.0;
                SetPlayerVolume(sliderVal);
                if (Math.Abs(_volume.Value - sliderVal) > 0.001)
                {
                    _suppressVolume = true;
                    _volume.Value = sliderVal;
                    _suppressVolume = false;
                }
                _current = _current
                    ?? _settings.Favorites.FirstOrDefault(f => f.Uuid == _settings.LastUuid)
                    ?? _settings.Favorites.FirstOrDefault();
            }
            catch { }
            RenderAll();
        }

        private void SaveSettings()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_settings);
                DependencyObject d = this;
                while (d != null && !(d is Palisades.Views.Controls.PluginGadgetWrapper))
                    d = VisualTreeHelper.GetParent(d);
                if (d is Palisades.Views.Controls.PluginGadgetWrapper wrapper)
                    wrapper.SaveChildCustomData(json);
            }
            catch { }
        }

        public List<RadioFavStation> GetFavorites() => _settings.Favorites.ToList();

        public bool IsFavorite(string uuid) =>
            !string.IsNullOrEmpty(uuid) && _settings.Favorites.Any(f => f.Uuid == uuid);

        public void SetFavorite(RadioStation station, bool follow)
        {
            if (station == null || string.IsNullOrEmpty(station.Uuid)) return;
            var existing = _settings.Favorites.FirstOrDefault(f => f.Uuid == station.Uuid);
            if (follow && existing == null)
                _settings.Favorites.Add(new RadioFavStation
                {
                    Uuid = station.Uuid, Name = station.Name, Stream = station.StreamUrl,
                    Favicon = station.Favicon, Tags = station.Tags
                });
            else if (!follow && existing != null)
                _settings.Favorites.Remove(existing);
            SaveSettings();
            RenderFavorites();
        }

        // ================= playback =================

        public void PlayStation(RadioFavStation station)
        {
            if (station == null || string.IsNullOrEmpty(station.Stream)) return;
            _current = station;
            _settings.LastUuid = station.Uuid;
            SaveSettings();
            try
            {
                _player.Stop();
                _player.Open(new Uri(station.Stream));
                _player.Play();
                SetStatus(true, "Buffering…");
                RadioBrowserService.ReportClick(station.Uuid);
            }
            catch { SetStatus(false, "Stream error"); }
            RenderCurrent();
        }

        public void PlayStation(RadioStation station)
        {
            PlayStation(new RadioFavStation
            {
                Uuid = station.Uuid, Name = station.Name, Stream = station.StreamUrl,
                Favicon = station.Favicon, Tags = station.Tags
            });
        }

        private void TogglePlay()
        {
            if (_current == null)
            {
                if (_settings.Favorites.Count > 0) PlayStation(_settings.Favorites[0]);
                return;
            }
            try
            {
                if (_playing)
                {
                    // Live streams aren't seekable: MediaPlayer.Pause is unreliable,
                    // so we stop (and reopen on resume).
                    _player.Stop();
                    SetStatus(false, "Paused");
                }
                else
                {
                    _player.Open(new Uri(_current.Stream));
                    _player.Play();
                    SetStatus(true, "Playing");
                }
            }
            catch { SetStatus(false, "Stream error"); }
        }

        /// <summary>Star/unstar the currently selected station.</summary>
        private void ToggleCurrentFavorite()
        {
            if (_current == null) return;
            bool fav = IsFavorite(_current.Uuid);
            if (!fav)
            {
                if (!_settings.Favorites.Any(f => f.Uuid == _current.Uuid))
                    _settings.Favorites.Add(new RadioFavStation
                    {
                        Uuid = _current.Uuid, Name = _current.Name, Stream = _current.Stream,
                        Favicon = _current.Favicon, Tags = _current.Tags
                    });
            }
            else
            {
                _settings.Favorites.RemoveAll(f => f.Uuid == _current.Uuid);
            }
            SaveSettings();
            UpdateFavGlyph();
            RenderFavorites();
        }

        private void UpdateFavGlyph()
        {
            bool fav = _current != null && IsFavorite(_current.Uuid);
            _favGlyph.Text = fav ? "★" : "☆";
            _favGlyph.Foreground = fav ? AccentBrush : MutedBrush;
        }

        private void StepFavorite(int dir)
        {
            if (_settings.Favorites.Count == 0) return;
            int idx = _current == null ? -1 : _settings.Favorites.FindIndex(f => f.Uuid == _current.Uuid);
            int n = _settings.Favorites.Count;
            int next = ((idx + dir) % n + n) % n;
            PlayStation(_settings.Favorites[next]);
        }

        private void SetStatus(bool playing, string text)
        {
            _playing = playing;
            _playGlyph.Text = playing ? "⏸" : "▶";
            _statusText.Text = text;
            _statusDot.Foreground = playing ? LiveBrush : DimBrush;
            ReportNowPlaying(playing);
        }

        /// <summary>Surfaces the current station in the Now Playing widget.</summary>
        private void ReportNowPlaying(bool playing)
        {
            try
            {
                if (!_settings.ShowInNowPlaying || _current == null)
                {
                    ExternalNowPlaying.Clear("Radio");
                }
                else
                {
                    ExternalNowPlaying.Report("Radio", _current.Name, _current.Tags, playing);
                }

                var discord = Palisades.Services.DiscordPresenceService.Instance;
                if (!_settings.ShowOnDiscord || _current == null)
                {
                    discord.ReportSessionGone("Radio");
                }
                else
                {
                    discord.ReportSession("Radio", _current.Name, _current.Tags, playing);
                }
            }
            catch { }
        }

        /// <summary>
        /// Slider 0→0.5 maps to volume 0→1 (the whole bar covers the useful
        /// range, so the low end is finely adjustable; 0.5 = 100%).
        /// </summary>
        private void SetPlayerVolume(double slider)
        {
            _player.Volume = Math.Min(1.0, Math.Max(0.0, slider) * 2.0);
        }

        // ================= rendering =================

        private void RenderAll()
        {
            RenderCurrent();
            RenderFavorites();
        }

        private void RenderCurrent()
        {
            if (_current == null)
            {
                _name.Text = "Pick a station";
                _tags.Text = "Tap 🔍 to browse";
                _logo.Source = null;
                _logo.Visibility = Visibility.Collapsed;
                return;
            }
            _name.Text = _current.Name;
            _tags.Text = _current.Tags ?? "";
            IconLoader.Load(_current.Favicon, _logo, 44);
            UpdateFavGlyph();
        }

        private void RenderFavorites()
        {
            _listPanel.Children.Clear();
            if (_settings.Favorites.Count == 0)
            {
                _hint.Text = "No favorites yet — tap 🔍, then ★ to save a station.";
                _hint.Visibility = Visibility.Visible;
                return;
            }
            _hint.Visibility = Visibility.Collapsed;
            var row = new WrapPanel();
            foreach (var f in _settings.Favorites)
            {
                var fav = f;
                bool active = _current != null && _current.Uuid == fav.Uuid;
                row.Children.Add(BuildChip(fav.Name, fav.Favicon, active, () => PlayStation(fav)));
            }
            _listPanel.Children.Add(row);
        }

        private void OpenSearch()
        {
            try { new RadioSearchWindow(this).Show(); } catch { }
        }

        private Border BuildChip(string label, string favicon, bool active, Action onClick)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = active ? new SolidColorBrush(Color.FromArgb(0x40, 0x7D, 0xD3, 0xFC)) : ChipBrush,
                BorderBrush = active ? AccentBrush : ChipBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = Cursors.Hand,
                ToolTip = label
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (!string.IsNullOrEmpty(favicon))
            {
                var icon = new Image { Stretch = Stretch.Uniform, Width = 14, Height = 14, Margin = new Thickness(0, 0, 4, 0) };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                IconLoader.Load(favicon, icon, 14);
                row.Children.Add(icon);
            }
            row.Children.Add(new TextBlock
            {
                Text = label.Length > 18 ? label.Substring(0, 17) + "…" : label,
                FontSize = 10, Foreground = active ? Brushes.White : MutedBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            border.Child = row;
            border.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
            return border;
        }

        private static TextBlock BuildIconButton(string glyph, string tip, Action onClick)
        {
            var tb = new TextBlock
            {
                Text = glyph, FontSize = 13, Foreground = MutedBrush,
                Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0), ToolTip = tip
            };
            tb.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
            return tb;
        }

        private static ControlTemplate? _slimSliderTemplate;

        /// <summary>Slim accent slider: thin track, round thumb, no default chrome.</summary>
        private static void ApplySlimSliderStyle(Slider slider)
        {
            try
            {
                if (_slimSliderTemplate == null)
                {
                    const string xaml = "<ControlTemplate"
                        + " xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'"
                        + " TargetType='Slider'>"
                        + "<Grid VerticalAlignment='Center' Height='18' Background='Transparent'>"
                        + "<Border Height='4' CornerRadius='2' Background='#26FFFFFF' VerticalAlignment='Center'/>"
                        + "<Track Name='PART_Track' VerticalAlignment='Center'>"
                        + "<Track.DecreaseRepeatButton><RepeatButton Command='Slider.DecreaseLarge' Focusable='False'>"
                        + "<RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>"
                        + "<Border Height='4' CornerRadius='2' Background='#7DD3FC' VerticalAlignment='Center'/>"
                        + "</ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>"
                        + "<Track.Thumb><Thumb Width='12' Height='12' Focusable='False'>"
                        + "<Thumb.Template><ControlTemplate TargetType='Thumb'>"
                        + "<Ellipse Fill='#7DD3FC' Width='12' Height='12'/>"
                        + "</ControlTemplate></Thumb.Template></Thumb></Track.Thumb>"
                        + "<Track.IncreaseRepeatButton><RepeatButton Command='Slider.IncreaseLarge' Focusable='False'>"
                        + "<RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>"
                        + "<Border Background='Transparent' Height='4' VerticalAlignment='Center'/>"
                        + "</ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>"
                        + "</Track></Grid></ControlTemplate>";
                    _slimSliderTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
                }
                slider.Template = _slimSliderTemplate;
            }
            catch { }
        }

        internal static string? DefaultCountry()
        {
            try
            {
                string lang = TranslationService.Instance.CurrentCulture ?? "en";
                if (lang.StartsWith("fr", StringComparison.OrdinalIgnoreCase)) return "FR";
                if (lang.StartsWith("tr", StringComparison.OrdinalIgnoreCase)) return "TR";
            }
            catch { }
            return null;
        }

        private static void OpenGoogle(string query)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://www.google.com/search?q=" + Uri.EscapeDataString(query ?? ""),
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    /// <summary>Search + top stations dialog (separate window → normal keyboard focus).</summary>
    public class RadioSearchWindow : Window
    {
        private readonly RadioView _view;
        private readonly TextBox _searchBox;
        private readonly StackPanel _listPanel;
        private readonly TextBlock _statusText;
        private CancellationTokenSource? _cts;

        public RadioSearchWindow(RadioView view)
        {
            _view = view;
            Title = "Find stations";
            Width = 340;
            Height = 460;
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

            var root = new StackPanel { Margin = new Thickness(12) };
            root.Children.Add(new TextBlock
            {
                Text = "Search stations",
                FontWeight = FontWeights.SemiBold, FontSize = 14,
                Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8)
            });

            _searchBox = new TextBox
            {
                Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 0, 8)
            };
            _searchBox.TextChanged += (_, _) => DebouncedSearch();
            root.Children.Add(_searchBox);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 300 };
            _listPanel = new StackPanel();
            scroll.Content = _listPanel;
            root.Children.Add(scroll);

            _statusText = new TextBlock
            {
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(0, 6, 0, 0)
            };
            root.Children.Add(_statusText);

            var closeBtn = new Button
            {
                Content = "Close", Height = 28, Margin = new Thickness(0, 8, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
                Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = Cursors.Hand
            };
            closeBtn.Click += (_, _) => Close();
            root.Children.Add(closeBtn);

            Content = root;
            Loaded += async (_, _) =>
            {
                _searchBox.Focus();
                await LoadTopAsync();
            };
            Closed += (_, _) => { try { _cts?.Cancel(); } catch { } };
        }

        private void DebouncedSearch()
        {
            try { _cts?.Cancel(); } catch { }
            var cts = new CancellationTokenSource();
            _cts = cts;
            string q = _searchBox.Text ?? "";
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(350, cts.Token).ConfigureAwait(false); } catch { return; }
                List<RadioStation> results;
                if (string.IsNullOrWhiteSpace(q))
                    results = await RadioBrowserService.TopAsync(RadioView.DefaultCountry(), 20, cts.Token).ConfigureAwait(false);
                else
                    results = await RadioBrowserService.SearchAsync(q, cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested) return;
                await Dispatcher.InvokeAsync(() => ShowResults(results, string.IsNullOrWhiteSpace(q)));
            });
        }

        private async Task LoadTopAsync()
        {
            _statusText.Text = "Loading top stations…";
            var results = await RadioBrowserService.TopAsync(RadioView.DefaultCountry(), 20).ConfigureAwait(true);
            ShowResults(results, true);
        }

        private void ShowResults(List<RadioStation> stations, bool isTop)
        {
            _listPanel.Children.Clear();
            string? cc = RadioView.DefaultCountry();
            _statusText.Text = isTop ? "Top stations" + (cc != null ? " (" + cc + ")" : "") : stations.Count + " result(s)";
            foreach (var s in stations.Take(40))
            {
                var st = s;
                var rowBrush = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24));
                var row = new Border
                {
                    Background = rowBrush,
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 4, 6, 4),
                    Margin = new Thickness(0, 0, 0, 4),
                    Cursor = Cursors.Hand
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var icon = new Image { Stretch = Stretch.Uniform, Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 0) };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                IconLoader.Load(st.Favicon, icon, 26);
                Grid.SetColumn(icon, 0);
                grid.Children.Add(icon);

                var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                meta.Children.Add(new TextBlock
                {
                    Text = st.Name, Foreground = Brushes.White, FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                string sub = string.Join(" · ", new[]
                {
                    st.Country, st.Tags, st.Codec + (st.Bitrate > 0 ? " " + st.Bitrate + "k" : "")
                }.Where(x => !string.IsNullOrWhiteSpace(x)));
                meta.Children.Add(new TextBlock
                {
                    Text = sub, FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                Grid.SetColumn(meta, 1);
                grid.Children.Add(meta);

                bool fav = _view.IsFavorite(st.Uuid);
                var star = new TextBlock
                {
                    Text = fav ? "★" : "☆", FontSize = 14, Cursor = Cursors.Hand,
                    Foreground = fav ? new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC))
                                     : new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
                };
                star.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    _view.SetFavorite(st, !_view.IsFavorite(st.Uuid));
                    bool now = _view.IsFavorite(st.Uuid);
                    star.Text = now ? "★" : "☆";
                    star.Foreground = now ? new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC))
                                          : new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
                };
                Grid.SetColumn(star, 2);
                grid.Children.Add(star);

                row.Child = grid;
                row.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    // Play, but do not auto-favorite and keep the window open.
                    _view.PlayStation(st);
                    // Brief light-blue flash, then fade back.
                    var anim = new System.Windows.Media.Animation.ColorAnimationUsingKeyFrames();
                    anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearColorKeyFrame(
                        Color.FromRgb(0x3E, 0x6E, 0xA0),
                        System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
                    anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearColorKeyFrame(
                        Color.FromRgb(0x24, 0x24, 0x24),
                        System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(480))));
                    rowBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };
                _listPanel.Children.Add(row);
            }
        }
    }

    /// <summary>Async, cached favicon loader (no UI blocking, one fetch per URL).</summary>
    internal static class IconLoader
    {
        private static readonly ConcurrentDictionary<string, BitmapImage?> Cache =
            new ConcurrentDictionary<string, BitmapImage?>();
        private static readonly WebClient Client = CreateClient();

        private static WebClient CreateClient()
        {
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "Palisades/1.0 (Windows; desktop-widget)";
            return wc;
        }

        public static void Load(string? url, Image target, int decodeWidth)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                target.Source = null;
                target.Visibility = Visibility.Collapsed;
                return;
            }
            if (Cache.TryGetValue(url, out var cached))
            {
                if (cached != null) { target.Source = cached; target.Visibility = Visibility.Visible; }
                else target.Visibility = Visibility.Collapsed;
                return;
            }
            target.Visibility = Visibility.Collapsed;
            _ = Task.Run(() =>
            {
                BitmapImage? bmp = null;
                try
                {
                    byte[] data = Client.DownloadData(url);
                    bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = new MemoryStream(data);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = decodeWidth;
                    bmp.EndInit();
                    bmp.Freeze();
                }
                catch { bmp = null; }
                Cache[url] = bmp;
                if (bmp != null)
                    target.Dispatcher.InvokeAsync(() =>
                    {
                        target.Source = bmp;
                        target.Visibility = Visibility.Visible;
                    });
            });
        }
    }
}
