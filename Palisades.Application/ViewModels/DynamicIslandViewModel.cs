using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Media.Control;
using Palisades.Services;

namespace Palisades.ViewModels
{
    public enum IslandState { Minimized, Compact, Expanded }

    public sealed class IslandWidget : INotifyPropertyChanged
    {
        public string GadgetType { get; set; } = "";
        private string _name = "";
        public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
        private string _glyph = "\uE734";
        public string Glyph { get => _glyph; set { _glyph = value; OnPropertyChanged(); } }
        private bool _selected;
        /// <summary>Carte active (focus) : surbrillance dans le sélecteur.</summary>
        public bool IsSelected { get => _selected; set { _selected = value; OnPropertyChanged(); } }

        private FrameworkElement? _expandedView;
        public FrameworkElement ExpandedView
        {
            get
            {
                if (_expandedView == null)
                {
                    _expandedView = DynamicIslandViewModel.CreateGadgetView(GadgetType);
                    RefreshSettings();
                }
                return _expandedView;
            }
        }

        /// <summary>Re-applies the memorized type settings (same réglages as desktop widgets).</summary>
        public void RefreshSettings()
        {
            if (_expandedView is Palisades.Plugins.ICustomizableGadgetView c)
            {
                try { c.ApplyCustomSettings(GadgetTypeDefaults.Instance.Get(GadgetType)); } catch { }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public sealed class DynamicIslandViewModel : INotifyPropertyChanged
    {
        private static readonly Dictionary<string, string> Glyphs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["NowPlaying"] = "\uE8D6",
            ["Clock"] = "\uE121",
            ["SystemMonitor"] = "\uE9D9",
            ["Radio"] = "\uE8ED",
            ["Football"] = "\uE734",
            ["PostIt"] = "\uE70B",
        };

        /// <summary>Shared instance: overlay host + taskbar bar window show one island.
        /// Declared AFTER Glyphs (static init order: the ctor reads Glyphs).</summary>
        public static DynamicIslandViewModel Instance { get; } = new DynamicIslandViewModel();

        /// <summary>Resolves display name + glyph for any gadget type, including
        /// future third-party plugins (falls back to registered name + generic glyph).</summary>
        public static (string Name, string Glyph) ResolveGadgetMeta(string gadgetType)
        {
            string name = gadgetType;
            try
            {
                foreach (var p in PluginService.Instance.Plugins)
                {
                    var g = p.Context?.Gadgets?.FirstOrDefault(x =>
                        string.Equals(x.GadgetType, gadgetType, StringComparison.OrdinalIgnoreCase));
                    if (g != null) { name = g.Name; break; }
                }
            }
            catch { }
            if (!Glyphs.TryGetValue(gadgetType, out var glyph))
                glyph = "\uE734";
            return (name, glyph);
        }

        /// <summary>Every registered gadget type (built-in + external plugins).</summary>
        public static List<(string GadgetType, string Name)> GetAllGadgetTypes()
        {
            var list = new List<(string, string)>();
            try
            {
                foreach (var p in PluginService.Instance.Plugins)
                {
                    if (p.Context == null) continue;
                    foreach (var g in p.Context.Gadgets)
                    {
                        if (!list.Any(x => string.Equals(x.Item1, g.GadgetType, StringComparison.OrdinalIgnoreCase)))
                            list.Add((g.GadgetType, g.Name));
                    }
                }
            }
            catch { }
            return list;
        }

        public ObservableCollection<IslandWidget> Widgets { get; } = new();

        private int _selectedIndex;
        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                value = Widgets.Count == 0 ? 0 : Math.Clamp(value, 0, Widgets.Count - 1);
                if (_selectedIndex == value) return;
                _selectedIndex = value;
                try { IslandDiag.Log($"VM select idx={value} type={Widgets[value].GadgetType}"); } catch { }
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentWidget));
                OnPropertyChanged(nameof(ExpandedContent));
                OnPropertyChanged(nameof(IsCurrentNowPlaying));
                OnPropertyChanged(nameof(IsNpSummaryVisible));
                OnPropertyChanged(nameof(CurrentWidgetHeight));
                RefreshSelection();
                PersistSelection();
            }
        }

        public IslandWidget? CurrentWidget => Widgets.Count == 0 ? null : Widgets[Math.Clamp(_selectedIndex, 0, Widgets.Count - 1)];
        public FrameworkElement? ExpandedContent => CurrentWidget?.ExpandedView;

        private IslandState _state = IslandState.Compact;
        public IslandState State
        {
            get => _state;
            set { if (_state == value) return; var old = _state; _state = value; try { IslandDiag.Log($"VM state {old}->{value}"); } catch { } OnPropertyChanged(); OnPropertyChanged(nameof(IsExpanded)); OnPropertyChanged(nameof(IsCompact)); OnPropertyChanged(nameof(IsMinimized)); OnPropertyChanged(nameof(IsNpSummaryVisible)); }
        }

        public bool IsExpanded => _state == IslandState.Expanded;
        public bool IsCompact => _state == IslandState.Compact;
        public bool IsMinimized => _state == IslandState.Minimized;

        /// <summary>True pendant l'anim de fermeture. Garde le résumé NP + contrôles
        /// média visibles : sinon le panneau rétrécit en pleine fermeture et le clip
        /// (calculé sur la hauteur d'ouverture) vise une zone vide -> pilule clignote.</summary>
        private bool _isClosing;
        public bool IsClosing
        {
            get => _isClosing;
            set { if (_isClosing == value) return; _isClosing = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNpSummaryVisible)); }
        }

        public DynamicIslandTheme Theme => DynamicIslandService.Instance.CurrentTheme;
        public Brush BackgroundBrush => DynamicIslandService.Instance.BackgroundBrush;
        public Brush BorderBrush => DynamicIslandService.Instance.BorderBrush;
        public Brush ForegroundBrush => DynamicIslandService.Instance.ForegroundBrush;
        public Brush MutedBrush => DynamicIslandService.Instance.MutedBrush;
        public Brush AccentBrush => DynamicIslandService.Instance.AccentBrush;

        /// <summary>Theme background with user opacity applied.</summary>
        public Brush IslandBackgroundBrush
        {
            get
            {
                try
                {
                    var c = (Color)ColorConverter.ConvertFromString(Theme.Background);
                    c.A = (byte)(DynamicIslandService.Instance.BgOpacity * 255);
                    var b = new SolidColorBrush(c);
                    b.Freeze();
                    return b;
                }
                catch { return BackgroundBrush; }
            }
        }

        public CornerRadius IslandCornerRadius => new(DynamicIslandService.Instance.CornerRadius);

        public bool ExpandUp => DynamicIslandService.Instance.ExpandUp;

        /// <summary>Per-widget expanded height (dashboard réglable).</summary>
        public double CurrentWidgetHeight =>
            DynamicIslandService.Instance.GetWidgetHeight(CurrentWidget?.GadgetType ?? "");

        // --- Now Playing (lightweight SMTC + bridge mirror) ---
        private GlobalSystemMediaTransportControlsSessionManager? _smtc;
        private readonly List<GlobalSystemMediaTransportControlsSession> _sessions = new();
        private GlobalSystemMediaTransportControlsSession? _active;
        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _posTimer;

        private string _npTitle = "";
        public string NpTitle { get => _npTitle; set { if (_npTitle != value) { try { IslandDiag.Log($"VM NP title=[{value}]"); } catch { } } _npTitle = value; OnPropertyChanged(); } }

        private string _npArtist = "";
        public string NpArtist { get => _npArtist; set { if (_npArtist != value) { try { IslandDiag.Log($"VM NP artist=[{value}]"); } catch { } } _npArtist = value; OnPropertyChanged(); } }

        private bool _npPlaying;
        public bool NpPlaying { get => _npPlaying; set { if (_npPlaying != value) { try { IslandDiag.Log($"VM NP playing={value}"); } catch { } } _npPlaying = value; OnPropertyChanged(); OnPropertyChanged(nameof(PlayGlyph)); } }
        public string PlayGlyph => NpPlaying ? "\uE769" : "\uE768";

        private double _npPosition;
        public double NpPosition { get => _npPosition; set { _npPosition = value; OnPropertyChanged(); OnPropertyChanged(nameof(NpPositionText)); } }

        private double _npDuration = 1;
        public double NpDuration { get => _npDuration; set { _npDuration = Math.Max(1, value); OnPropertyChanged(); OnPropertyChanged(nameof(NpDurationText)); } }

        public string NpPositionText => TimeSpan.FromSeconds(NpPosition).ToString(@"m\:ss");
        public string NpDurationText => TimeSpan.FromSeconds(NpDuration).ToString(@"m\:ss");

        private string _clockText = DateTime.Now.ToString("HH:mm");
        public string ClockText { get => _clockText; set { _clockText = value; OnPropertyChanged(); } }

        private string _dateText = DateTime.Now.ToString("ddd d MMM");
        public string DateText { get => _dateText; set { _dateText = value; OnPropertyChanged(); } }

        public ICommand ToggleExpandCommand { get; }
        public ICommand NextWidgetCommand { get; }
        public ICommand PrevWidgetCommand { get; }
        public ICommand SelectWidgetCommand { get; }
        public ICommand TogglePlayCommand { get; }
        public ICommand NextTrackCommand { get; }
        public ICommand PrevTrackCommand { get; }
        public ICommand SeekCommand { get; }

        public DynamicIslandViewModel()
        {
            ToggleExpandCommand = new RelayCommand(() =>
                State = State == IslandState.Expanded ? IslandState.Compact : IslandState.Expanded);
            NextWidgetCommand = new RelayCommand(() =>
            { if (Widgets.Count > 0) SelectedIndex = (SelectedIndex + 1) % Widgets.Count; });
            PrevWidgetCommand = new RelayCommand(() =>
            { if (Widgets.Count > 0) SelectedIndex = (SelectedIndex - 1 + Widgets.Count) % Widgets.Count; });
            SelectWidgetCommand = new RelayCommand<object>(o =>
            {
                if (o is IslandWidget w) SelectedIndex = Widgets.IndexOf(w);
                else if (o is int i) SelectedIndex = i;
                if (State == IslandState.Minimized) State = IslandState.Compact;
            });
            TogglePlayCommand = new RelayCommand(() => _ = TogglePlayAsync());
            NextTrackCommand = new RelayCommand(() => _ = SkipAsync(true));
            PrevTrackCommand = new RelayCommand(() => _ = SkipAsync(false));
            SeekCommand = new RelayCommand<object>(o =>
            {
                if (o is double v) _ = SeekAsync(v);
                else _ = SeekAsync(NpPosition);
            });

            RebuildWidgets();
            DynamicIslandService.Instance.Changed += OnServiceChanged;
            PluginService.Instance.PluginsChanged += RebuildWidgets;
            GadgetTypeDefaults.Instance.Changed += RefreshAllSettings;
            ExternalNowPlaying.Changed += (_, _) => RefreshNowPlaying();

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (_, _) =>
            {
                var now = DateTime.Now;
                ClockText = now.ToString("HH:mm");
                DateText = now.ToString("ddd d MMM");
            };
            _clockTimer.Start();

            _posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _posTimer.Tick += (_, _) => RefreshTimeline();
            _posTimer.Start();

            _ = InitSmtcAsync();
        }

        public void RebuildWidgets()
        {
            var pinned = DynamicIslandService.Instance.Settings.PinnedWidgets;
            try { IslandDiag.Log($"VM rebuild pins=[{string.Join(",", pinned)}]"); } catch { }
            var keep = Widgets.ToDictionary(w => w.GadgetType, StringComparer.OrdinalIgnoreCase);
            Widgets.Clear();
            foreach (var g in pinned.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var (name, glyph) = ResolveGadgetMeta(g);
                if (keep.TryGetValue(g, out var existing))
                {
                    existing.Name = name;
                    existing.Glyph = glyph;
                    Widgets.Add(existing);
                }
                else
                {
                    Widgets.Add(new IslandWidget { GadgetType = g, Name = name, Glyph = glyph });
                }
            }
            _selectedIndex = Widgets.Count == 0
                ? 0
                : Math.Clamp(DynamicIslandService.Instance.Settings.SelectedIndex, 0, Widgets.Count - 1);
            OnPropertyChanged(nameof(SelectedIndex));
            OnPropertyChanged(nameof(CurrentWidget));
            OnPropertyChanged(nameof(ExpandedContent));
            OnPropertyChanged(nameof(IsNowPlayingPinned));
            OnPropertyChanged(nameof(IsCurrentNowPlaying));
            OnPropertyChanged(nameof(IsNpSummaryVisible));
            OnPropertyChanged(nameof(CurrentWidgetHeight));
            RefreshSelection();
        }

        public bool IsNowPlayingPinned =>
            Widgets.Any(w => string.Equals(w.GadgetType, "NowPlaying", StringComparison.OrdinalIgnoreCase));

        public bool IsCurrentNowPlaying =>
            CurrentWidget != null && string.Equals(CurrentWidget.GadgetType, "NowPlaying", StringComparison.OrdinalIgnoreCase);

        /// <summary>Résumé NP de l'îlot : affiché déplié seulement si un AUTRE widget
        /// est sélectionné (sinon doublon avec la vraie vue Now Playing dessous).</summary>
        public bool IsNpSummaryVisible => (IsExpanded || IsClosing) && !IsCurrentNowPlaying;

        private void RefreshSelection()
        {
            try
            {
                for (int i = 0; i < Widgets.Count; i++)
                    Widgets[i].IsSelected = (i == _selectedIndex);
            }
            catch { }
        }

        /// <summary>Auto-size clamp (content-driven width, no fixed sizes).</summary>
        public double CompactMaxWidth => DynamicIslandService.Instance.Settings.MaxWidth;

        /// <summary>Longueur mini de la pilule (réglage dashboard).</summary>
        public double CompactMinWidth => DynamicIslandService.Instance.Settings.Width;

        private void PersistSelection()
        {
            try
            {
                DynamicIslandService.Instance.Settings.SelectedIndex = _selectedIndex;
                DynamicIslandService.Instance.Save();
            }
            catch { }
        }

        private void RefreshAllSettings()
        {
            foreach (var w in Widgets)
                w.RefreshSettings();
        }

        private void OnServiceChanged()
        {
            OnPropertyChanged(nameof(Theme));
            OnPropertyChanged(nameof(BackgroundBrush));
            OnPropertyChanged(nameof(BorderBrush));
            OnPropertyChanged(nameof(ForegroundBrush));
            OnPropertyChanged(nameof(MutedBrush));
            OnPropertyChanged(nameof(AccentBrush));
            OnPropertyChanged(nameof(IslandBackgroundBrush));
            OnPropertyChanged(nameof(IslandCornerRadius));
            OnPropertyChanged(nameof(ExpandUp));
            OnPropertyChanged(nameof(CompactMaxWidth));
            OnPropertyChanged(nameof(CompactMinWidth));
            OnPropertyChanged(nameof(CurrentWidgetHeight));
            RebuildWidgets();
        }

        internal static FrameworkElement CreateGadgetView(string gadgetType)
        {
            try
            {
                foreach (var p in PluginService.Instance.Plugins)
                {
                    var ctx = p.Context;
                    if (ctx == null) continue;
                    foreach (var g in ctx.Gadgets)
                    {
                        if (string.Equals(g.GadgetType, gadgetType, StringComparison.OrdinalIgnoreCase))
                            return g.ViewFactory();
                    }
                }
            }
            catch { }
            return new TextBlock
            {
                Text = gadgetType,
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private async System.Threading.Tasks.Task InitSmtcAsync()
        {
            try
            {
                _smtc = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _smtc.CurrentSessionChanged += (_, _) => RefreshNowPlaying();
                _smtc.SessionsChanged += (_, _) => RefreshNowPlaying();
                RefreshNowPlaying();
            }
            catch { }
        }

        private void RefreshNowPlaying()
        {
            try
            {
                var ext = ExternalNowPlaying.Current;
                if (ext != null && ext.IsPlaying)
                {
                    NpTitle = string.IsNullOrEmpty(ext.Title) ? "Radio" : ext.Title;
                    NpArtist = string.IsNullOrEmpty(ext.Artist) ? ext.App : ext.Artist;
                    NpPlaying = ext.IsPlaying;
                    return;
                }

                if (_smtc == null) return;
                var sessions = _smtc.GetSessions().ToList();
                foreach (var s in sessions)
                {
                    if (!_sessions.Contains(s))
                    {
                        s.MediaPropertiesChanged += (_, _) => RefreshNowPlaying();
                        s.PlaybackInfoChanged += (_, _) => RefreshNowPlaying();
                        _sessions.Add(s);
                    }
                }
                _sessions.RemoveAll(s => !sessions.Contains(s));

                _active = sessions.FirstOrDefault(s =>
                {
                    try { return s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
                    catch { return false; }
                }) ?? sessions.FirstOrDefault();

                if (_active == null)
                {
                    if (ext != null)
                    {
                        NpTitle = string.IsNullOrEmpty(ext.Title) ? "Paused" : ext.Title;
                        NpArtist = ext.Artist;
                        NpPlaying = false;
                    }
                    else { NpTitle = ""; NpArtist = ""; NpPlaying = false; }
                    return;
                }
                _ = LoadMediaPropsAsync(_active);
            }
            catch { }
        }

        private async System.Threading.Tasks.Task LoadMediaPropsAsync(GlobalSystemMediaTransportControlsSession s)
        {
            try
            {
                var props = await s.TryGetMediaPropertiesAsync();
                bool playing = false;
                try { playing = s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; } catch { }
                App.Current?.Dispatcher.Invoke(() =>
                {
                    NpTitle = string.IsNullOrEmpty(props?.Title) ? "Unknown title" : props.Title!;
                    NpArtist = string.IsNullOrEmpty(props?.Artist) ? (s.SourceAppUserModelId ?? "") : props.Artist!;
                    NpPlaying = playing;
                });
                RefreshTimeline();
            }
            catch { }
        }

        private void RefreshTimeline()
        {
            try
            {
                var s = _active;
                if (s == null) return;
                var tp = s.GetTimelineProperties();
                App.Current?.Dispatcher.Invoke(() =>
                {
                    NpDuration = tp.EndTime.TotalSeconds > 0 ? tp.EndTime.TotalSeconds : 1;
                    NpPosition = Math.Clamp(tp.Position.TotalSeconds, 0, NpDuration);
                });
            }
            catch { }
        }

        private async System.Threading.Tasks.Task TogglePlayAsync()
        {
            var ext = ExternalNowPlaying.Current;
            if (ext != null && ExternalNowPlaying.TogglePlayPause != null)
            { try { IslandDiag.Log("CMD play (bridge radio)"); ExternalNowPlaying.TogglePlayPause(); } catch { } return; }
            try { IslandDiag.Log($"CMD play (SMTC active={_active != null})"); if (_active != null) await _active.TryTogglePlayPauseAsync(); } catch { }
            RefreshNowPlaying();
        }

        private async System.Threading.Tasks.Task SkipAsync(bool next)
        {
            var ext = ExternalNowPlaying.Current;
            if (ext != null)
            {
                try { IslandDiag.Log($"CMD skip {(next ? "next" : "prev")} (bridge radio)"); (next ? ExternalNowPlaying.SkipNext : ExternalNowPlaying.SkipPrevious)?.Invoke(); } catch { }
                return;
            }
            try { IslandDiag.Log($"CMD skip {(next ? "next" : "prev")} (SMTC active={_active != null})"); if (_active != null) { if (next) await _active.TrySkipNextAsync(); else await _active.TrySkipPreviousAsync(); } } catch { }
            RefreshNowPlaying();
        }

        private async System.Threading.Tasks.Task SeekAsync(double seconds)
        {
            try { IslandDiag.Log($"CMD seek {seconds:0}s (SMTC active={_active != null})"); if (_active != null) await _active.TryChangePlaybackPositionAsync((long)(seconds * 10_000_000)); } catch { }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
