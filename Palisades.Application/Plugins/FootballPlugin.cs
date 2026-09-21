using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Markup;
using Newtonsoft.Json;
using Palisades.Services;

namespace Palisades.Plugins
{
    public class FootballPlugin : IPlugin
    {
        public string Name => "Football";
        public string Id => "com.palisades.plugin.football";
        public string Version => "1.0.0";
        public string Author => "Palisades Team";
        public string Description => "Live scores and upcoming matches for your favorite teams and leagues (ESPN, no key needed).";

        public void OnLoad(PluginContext context)
        {
            context.RegisterGadget(
                gadgetType: "Football",
                name: "Football",
                viewFactory: () => new FootballView(),
                defaultWidth: 340,
                defaultHeight: 240
            );
        }

        public void OnUnload()
        {
        }
    }

    public class FootballFavTeam
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        // "team" or "league". A favorited league shows ALL its matches.
        public string Kind { get; set; } = "team";
        // For teams: home league (auto-subscribed so its matches show up).
        public string LeagueSlug { get; set; } = "";
        // Dialog display (e.g. "Barcelona - Women"). Never persisted.
        [JsonIgnore]
        public string DisplayName { get; set; } = "";
    }

    public class FootballSettings
    {
        public List<string> Leagues { get; set; } = new List<string> { "eng.1", "esp.1", "ita.1", "ger.1", "fra.1", "tur.1" };
        public List<FootballFavTeam> Teams { get; set; } = new List<FootballFavTeam>();
        public int RefreshMinutes { get; set; } = 10;
        public int MaxMatches { get; set; } = 8;
        public bool ShowCrests { get; set; } = true;
        public int FinishedHours { get; set; } = 24;
        // "bottom" or "top": where finished matches sit in the list.
        public string FinishedPosition { get; set; } = "top";
        // Hex text color for finished rows (empty = normal colors).
        public string FinishedTextColor { get; set; } = "#808080";
        public bool ShowFinishedHeader { get; set; } = true;
        public bool ShowFinishedDates { get; set; } = true;
        // "classic" rows or "dark" cards.
        public string CardTheme { get; set; } = "classic";
        // "details" window or "google" search on match click.
        public string MatchClickAction { get; set; } = "details";
        // Show live followed-team matches on Discord presence (off by default).
        public bool ShowLiveOnDiscord { get; set; } = true;
        // Dark-cards zoom (0.7 - 1.3).
        public double CardScale { get; set; } = 1.0;
        // "text" (Sep 10) or "numeric" (10/09/26).
        public string DateFormat { get; set; } = "daynumeric";
    }

    public class FootballView : Border, ICustomizableGadgetView
    {
        private static readonly System.Net.Http.HttpClient _crestHttp =
            new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly ConcurrentDictionary<string, BitmapImage> _crestCache =
            new ConcurrentDictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);

        private FootballSettings _settings = new FootballSettings();
        private DispatcherTimer? _refreshTimer;
        private bool _everLoaded;
        private readonly TextBlock _headerLabel;
        private readonly TextBlock _liveLabel;
        private readonly StackPanel _rowsPanel;
        private readonly TextBlock _statusLabel;
        private readonly StackPanel _filterPanel;
        private readonly WrapPanel _dateRow;
        private readonly WrapPanel _leagueRow;
        private string? _dateFilter;
        private string? _leagueFilter;
        private bool _finishedOnly;
        private List<EspnMatch> _lastFull = new List<EspnMatch>();
        // Signature of the last rendered dataset: identical refresh data
        // skips the whole visual-tree rebuild (periodic CPU spikes for nothing).
        private string? _lastDataSignature;

        /// <summary>Applies hover-bar filters, then sorts/takes/renders.</summary>
        private void ApplyQuickFiltersAndRender()
        {
            var all = _lastFull.ToList();
            if (_finishedOnly)
            {
                all = all.Where(m => m.IsFinished).ToList();
            }
            if (!string.IsNullOrEmpty(_dateFilter))
            {
                all = all.Where(m => m.UtcDate != DateTime.MinValue
                    && m.UtcDate.ToLocalTime().ToString("yyyy-MM-dd") == _dateFilter).ToList();
            }
            if (!string.IsNullOrEmpty(_leagueFilter))
            {
                all = all.Where(m => (m.LeagueSlug ?? "").Equals(_leagueFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            // NOTE: no Take() here — RenderMatches() applies FinishedPosition
            // grouping (finished top/bottom) THEN takes MaxMatches. Truncating
            // here with CompareMatches (upcoming first) would crowd finished
            // out whenever upcoming >= MaxMatches, hiding them from main view
            // while the "Terminés (N)" chip still sees them in _lastFull.
            RenderMatches(all);
        }

        /// <summary>Rebuilds the hover filter chips from the full fetched set.</summary>
        private void BuildFilterBars(List<EspnMatch> all)
        {
            _dateRow.Children.Clear();
            _leagueRow.Children.Clear();
            var culture = AppCulture();

            var days = all
                .Where(m => m.UtcDate != DateTime.MinValue)
                .Select(m => m.UtcDate.ToLocalTime().Date)
                .Distinct().OrderBy(d => d).Take(8).ToList();
            string todayKey = DateTime.Today.ToString("yyyy-MM-dd");
            string yesterdayKey = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
            string tomorrowKey = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");
            string todayLabel = TranslationService.Instance["Widget_FootballToday"] ?? "Today";
            string yesterdayLabel = TranslationService.Instance["Widget_FootballYesterday"] ?? "Yesterday";
            string tomorrowLabel = TranslationService.Instance["Widget_FootballTomorrow"] ?? "Tomorrow";
            int finishedCount = all.Count(m => m.IsFinished);
            if (finishedCount > 0)
            {
                string finLabel = (TranslationService.Instance["Widget_FootballFinishedSection"] ?? "Finished")
                    + " (" + finishedCount + ")";
                _dateRow.Children.Add(BuildChip(finLabel, null, _finishedOnly,
                    () =>
                    {
                        _finishedOnly = !_finishedOnly;
                        BuildFilterBars(_lastFull);
                        ApplyQuickFiltersAndRender();
                    },
                    () =>
                    {
                        _finishedOnly = false;
                        BuildFilterBars(_lastFull);
                        ApplyQuickFiltersAndRender();
                    }));
            }
            foreach (var day in days)
            {
                string key = day.ToString("yyyy-MM-dd");
                string label = key == todayKey ? todayLabel
                    : key == yesterdayKey ? yesterdayLabel
                    : key == tomorrowKey ? tomorrowLabel
                    : day.ToString("ddd d", culture);
                _dateRow.Children.Add(BuildChip(label, null, _dateFilter == key,
                    () =>
                    {
                        _dateFilter = _dateFilter == key ? null : key;
                        BuildFilterBars(_lastFull);
                        ApplyQuickFiltersAndRender();
                    },
                    () =>
                    {
                        _dateFilter = null;
                        BuildFilterBars(_lastFull);
                        ApplyQuickFiltersAndRender();
                    }));
            }

            var leagues = all
                .GroupBy(m => (m.LeagueSlug ?? "").ToLowerInvariant())
                .Select(g => g.First())
                .OrderByDescending(m => IsLeagueFavorite(m.LeagueSlug))
                .ThenBy(m => m.LeagueName)
                .ToList();
            foreach (var lg in leagues)
            {
                string slug = (lg.LeagueSlug ?? "").ToLowerInvariant();
                string name = string.IsNullOrEmpty(lg.LeagueName) ? lg.LeagueSlug : lg.LeagueName;
                _leagueRow.Children.Add(BuildChip(name, lg.LeagueLogo, _leagueFilter == slug,
                    () =>
                    {
                        _leagueFilter = _leagueFilter == slug ? null : slug;
                        BuildFilterBars(_lastFull);
                        ApplyQuickFiltersAndRender();
                    },
                    () =>
                    {
                        _leagueFilter = null;
                        BuildFilterBars(_lastFull);
                        ApplyQuickFiltersAndRender();
                    }));
            }

            _dateRow.Visibility = days.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            _leagueRow.Visibility = leagues.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Pill chip: single click toggles, double-click clears.</summary>
        private static Border BuildChip(string label, string? logoUrl, bool active, Action onClick, Action onDoubleClick)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = active
                    ? new SolidColorBrush(Color.FromArgb(0x40, 0x7D, 0xD3, 0xFC))
                    : new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x22)),
                BorderBrush = active
                    ? new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC))
                    : new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = Cursors.Hand
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (!string.IsNullOrEmpty(logoUrl))
            {
                var icon = BuildLeagueIcon(logoUrl, 12);
                if (icon != null) row.Children.Add(icon);
            }
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 10,
                Foreground = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x8A, 0x8E, 0x96)),
                VerticalAlignment = VerticalAlignment.Center
            });
            border.Child = row;
            border.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (e.ClickCount >= 2) onDoubleClick();
                else onClick();
            };
            return border;
        }
        private bool _refreshing;
        private bool _refreshAgain;
        private List<EspnMatch> _lastMatches = new List<EspnMatch>();

        private static readonly SolidColorBrush TextBrush =
            new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
        private static readonly SolidColorBrush DimBrush =
            new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        private static readonly SolidColorBrush FaintBrush =
            new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
        private static readonly SolidColorBrush LiveBrush =
            new SolidColorBrush(Color.FromRgb(0xFF, 0x5F, 0x56));
        private static readonly SolidColorBrush AccentBrush =
            new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC));

        public FootballView()
        {
            Background = Brushes.Transparent;
            BorderThickness = new Thickness(0);

            var root = new Grid { Margin = new Thickness(10, 8, 10, 8) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _headerLabel = new TextBlock
            {
                Text = "⚽ Football",
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_headerLabel, 0);
            header.Children.Add(_headerLabel);

            _liveLabel = new TextBlock
            {
                Text = "",
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Foreground = LiveBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(_liveLabel, 1);
            header.Children.Add(_liveLabel);

            var refreshBtn = new Button
            {
                Content = "⟳",
                FontSize = 12,
                Width = 26,
                Height = 26,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = DimBrush,
                Cursor = Cursors.Hand,
                Focusable = false
            };
            refreshBtn.Click += (_, _) => RefreshNowAsync();
            Grid.SetColumn(refreshBtn, 2);
            header.Children.Add(refreshBtn);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 6, 0, 0)
            };
            // Slim/fade style comes from the wrapper (global widget setting).
            _rowsPanel = new StackPanel();
            scroll.Content = _rowsPanel;
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            _statusLabel = new TextBlock
            {
                Text = "",
                FontSize = 10,
                Foreground = FaintBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            Grid.SetRow(_statusLabel, 2);
            root.Children.Add(_statusLabel);

            // Hover filter bars: days + leagues (row 3, hidden until mouse-over).
            _dateRow = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
            _leagueRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            _filterPanel = new StackPanel { Visibility = Visibility.Collapsed };
            _filterPanel.Children.Add(_dateRow);
            _filterPanel.Children.Add(_leagueRow);
            Grid.SetRow(_filterPanel, 3);
            root.Children.Add(_filterPanel);
            MouseEnter += (_, _) => _filterPanel.Visibility = Visibility.Visible;
            MouseLeave += (_, _) => _filterPanel.Visibility = Visibility.Collapsed;

            Child = root;

            Loaded += (_, _) =>
            {
                RestartTimer();
                // Loaded can refire (visual-tree re-attach, hwnd resize):
                // only the first load fetches, later ones just restart the timer.
                if (!_everLoaded)
                {
                    _everLoaded = true;
                    RefreshNowAsync();
                }
            };
            Unloaded += (_, _) =>
            {
                _refreshTimer?.Stop();
                _refreshTimer = null;
            };
        }

        private string _lastAppliedCustomData = "\0unset\0";

        public void ApplyCustomSettings(string customData)
        {
            // Skip redundant applies (e.g. gadget sync rewriting identical
            // data): re-applying would restart the timer and fire a full
            // ESPN refresh for nothing.
            if (string.Equals(customData ?? "", _lastAppliedCustomData, StringComparison.Ordinal))
                return;
            _lastAppliedCustomData = customData ?? "";
            try
            {
                var settings = string.IsNullOrEmpty(customData)
                    ? new FootballSettings()
                    : JsonConvert.DeserializeObject<FootballSettings>(customData) ?? new FootballSettings();
                if (settings == null) return;

                var leagues = settings.Leagues?
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c.Trim().ToLowerInvariant())
                    .Distinct()
                    .ToList() ?? new List<string>();
                // One-time migration: widgets created with the original 5-league
                // default get the new defaults (tur.1…). Custom lists untouched.
                var oldDefaults = new HashSet<string> { "eng.1", "esp.1", "ita.1", "ger.1", "fra.1" };
                if (leagues.Count == 5 && leagues.All(oldDefaults.Contains))
                {
                    leagues = new List<string> { "eng.1", "esp.1", "ita.1", "ger.1", "fra.1", "tur.1" };
                    _settings.Leagues = leagues;
                    SaveSettings(); // persist the migration (no loop: list no longer matches old defaults)
                }
                else
                {
                    _settings.Leagues = leagues;
                }
                _settings.Teams = settings.Teams?
                    .Where(t => t != null && !string.IsNullOrEmpty(t.Id))
                    .GroupBy(t => t.Id)
                    .Select(g => g.First())
                    .ToList() ?? new List<FootballFavTeam>();
                _settings.RefreshMinutes = Math.Clamp(settings.RefreshMinutes, 1, 60);
                _settings.MaxMatches = Math.Clamp(settings.MaxMatches, 1, 50);
                _settings.ShowCrests = settings.ShowCrests;
                _settings.FinishedHours = Math.Clamp(settings.FinishedHours, 0, 720);
                _settings.FinishedPosition = (settings.FinishedPosition ?? "").ToLowerInvariant() == "top" ? "top" : "bottom";
                _settings.FinishedTextColor = settings.FinishedTextColor ?? "";
                _settings.ShowFinishedHeader = settings.ShowFinishedHeader;
                _settings.ShowFinishedDates = settings.ShowFinishedDates;
                _settings.CardTheme = (settings.CardTheme ?? "").ToLowerInvariant() == "dark" ? "dark" : "classic";
                _settings.MatchClickAction = (settings.MatchClickAction ?? "").ToLowerInvariant() == "google" ? "google" : "details";
                _settings.ShowLiveOnDiscord = settings.ShowLiveOnDiscord;
                _settings.CardScale = Math.Clamp(settings.CardScale <= 0 ? 1.0 : settings.CardScale, 0.5, 1.5);
                _settings.DateFormat = NormalizeDateFormat(settings.DateFormat);
            }
            catch { }
            _lastDataSignature = null; // render settings changed → force re-render
            RestartTimer();
            RefreshNowAsync();
        }

        private void RestartTimer()
        {
            _refreshTimer?.Stop();
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.RefreshMinutes))
            };
            _refreshTimer.Tick += (_, _) => RefreshNowAsync();
            _refreshTimer.Start();
        }

        public void RefreshNowAsync()
        {
            if (_refreshing)
            {
                _refreshAgain = true;
                return;
            }
            _refreshing = true;
            _ = RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            try
            {
                if (_settings.Leagues.Count == 0)
                {
                    ShowMessage("No leagues subscribed. Pick some in Dashboard → widget settings.");
                    return;
                }

                var favTeamIds = new HashSet<string>(_settings.Teams
                    .Where(t => (t.Kind ?? "team") == "team").Select(t => t.Id));
                var favLeagueSlugs = new HashSet<string>(_settings.Teams
                    .Where(t => (t.Kind ?? "") == "league").Select(t => t.Id.ToLowerInvariant()));

                // Followed teams bring their own leagues: no need to subscribe
                // a competition to see its matches (e.g. Barcelona → UCL games).
                var dirLeagues = EspnService.GetDirectory()
                    .GroupBy(t => t.Id)
                    .ToDictionary(g => g.Key, g => g.Select(t => t.LeagueSlug).Distinct().ToList());
                var favTeamsWithLeagues = new Dictionary<string, List<string>>();
                foreach (var id in favTeamIds)
                {
                    var leagues = new List<string>();
                    if (dirLeagues.TryGetValue(id, out var dl))
                        leagues.AddRange(dl.Where(s => !string.IsNullOrEmpty(s)));
                    var legacy = _settings.Teams.FirstOrDefault(t => t.Id == id && (t.Kind ?? "team") == "team");
                    if (legacy != null && !string.IsNullOrEmpty(legacy.LeagueSlug)
                        && !leagues.Any(l => l.Equals(legacy.LeagueSlug, StringComparison.OrdinalIgnoreCase)))
                        leagues.Add(legacy.LeagueSlug);
                    favTeamsWithLeagues[id] = leagues.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }

                // Fetch = subscribed leagues + every league a followed team plays in.
                var fetchLeagues = _settings.Leagues
                    .Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim())
                    .Concat(favTeamsWithLeagues.Values.SelectMany(v => v))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var tasks = fetchLeagues
                    .Select(code => LoadLeagueAsync(code))
                    .ToList();
                var results = await Task.WhenAll(tasks).ConfigureAwait(true);
                var all = results.Where(r => r != null).SelectMany(r => r!).ToList();

                // Backfill finished matches the CDN window already rotated out:
                // per-team schedules (site.web.api, cached 30min).
                try
                {
                    var schedTasks = new List<Task<List<EspnMatch>>>();
                    foreach (var kv in favTeamsWithLeagues)
                        foreach (var lg in kv.Value)
                            schedTasks.Add(EspnService.GetTeamScheduleAsync(lg, kv.Key));
                    var schedResults = await Task.WhenAll(schedTasks).ConfigureAwait(true);
                    var seen = new HashSet<string>(all.Select(m => m.LeagueSlug.ToLowerInvariant() + "/" + m.Id));
                    int schedTotal = 0, schedAdded = 0;
                    foreach (var sm in schedResults.SelectMany(r => r))
                    {
                        schedTotal++;
                        string k = (sm.LeagueSlug ?? "").ToLowerInvariant() + "/" + sm.Id;
                        if (seen.Add(k))
                        {
                            all.Add(sm);
                            schedAdded++;
                        }
                    }
                    Palisades.App.Log("[Football] refresh: cdn=" + (all.Count - schedAdded)
                        + " sched=" + schedTotal + " added=" + schedAdded);
                }
                catch (Exception ex)
                {
                    Palisades.App.Log("[Football] schedule backfill FAILED: " + ex.Message);
                }

                // Feed the world team directory (EVERY ESPN league, cached 30d):
                // the favorites search works for EVERY team. Subscribed leagues
                // resolve first so the search feels instant.
                EspnService.EnsureWorldRostersAsync(null, _settings.Leagues);

                // Directory first: EVERYTHING fetched (unfiltered) so search finds
                // all teams, even when the display is narrowed to favorites.
                _lastMatches = all.ToList();

                var favTeams = new HashSet<string>(_settings.Teams
                    .Where(t => (t.Kind ?? "team") == "team").Select(t => t.Id));
                var favLeagues = new HashSet<string>(_settings.Teams
                    .Where(t => (t.Kind ?? "") == "league").Select(t => t.Id.ToLowerInvariant()));
                if (favTeams.Count > 0 || favLeagues.Count > 0)
                    all = all.Where(m => favLeagues.Contains((m.LeagueSlug ?? "").ToLowerInvariant())
                        || favTeams.Contains(m.Home.Id) || favTeams.Contains(m.Away.Id)).ToList();

                // Drop finished matches older than the retention window
                // (end estimated at kickoff + 115 min; 0 = hide finished at once).
                if (_settings.FinishedHours <= 0)
                {
                    all = all.Where(m => !m.IsFinished).ToList();
                }
                else
                {
                    var now = DateTime.UtcNow;
                    all = all.Where(m => !m.IsFinished ||
                        (now - m.UtcDate.AddMinutes(115)).TotalHours <= _settings.FinishedHours).ToList();
                }

                // Hover filter bars (days + leagues), then apply active filters.
                _lastFull = all.ToList();
                string dataSig = BuildDataSignature(all);
                if (dataSig == _lastDataSignature)
                {
                    // Nothing changed since last render: skip the expensive
                    // visual-tree rebuild AND the Discord re-report (dedup
                    // would no-op it anyway). Just relax the timer if quiet.
                    TuneTimer(all);
                    return;
                }
                _lastDataSignature = dataSig;
                BuildFilterBars(_lastFull);
                ApplyQuickFiltersAndRender();
                ReportLiveToDiscord();
                TuneTimer(all);
            }
            catch (EspnApiException ex)
            {
                ShowMessage(ex.Kind == EspnApiError.Offline
                    ? "Offline — cannot reach ESPN."
                    : "Football API error: " + ex.Message);
            }
            catch (Exception ex)
            {
                ShowMessage("Football error: " + ex.Message);
            }
            finally
            {
                _refreshing = false;
                if (_refreshAgain)
                {
                    _refreshAgain = false;
                    RefreshNowAsync();
                }
            }
        }

        private async Task<List<EspnMatch>> LoadLeagueAsync(string code)
        {
            try
            {
                return await EspnService.GetMatchesAsync(code).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One dead league must never break the whole widget.
                Palisades.App.Log("[Football] league " + code + " failed: " + ex.Message);
                return new List<EspnMatch>();
            }
        }

        private static int CompareMatches(EspnMatch x, EspnMatch y)
        {
            bool xl = x.IsLive, yl = y.IsLive;
            if (xl != yl) return yl.CompareTo(xl);
            bool xf = x.IsFinished, yf = y.IsFinished;
            if (xf != yf) return xf.CompareTo(yf);
            return x.UtcDate.CompareTo(y.UtcDate);
        }

        /// <summary>Pushes the live followed-team match to Discord (teams only).</summary>
        private void ReportLiveToDiscord()
        {
            try
            {
                var svc = DiscordPresenceService.Instance;
                if (!_settings.ShowLiveOnDiscord)
                {
                    svc.ReportSessionGone("football");
                    return;
                }
                var favIds = new HashSet<string>(_settings.Teams
                    .Where(t => (t.Kind ?? "team") == "team").Select(t => t.Id));
                var live = _lastFull
                    .Where(m => m.IsLive && (favIds.Contains(m.Home.Id) || favIds.Contains(m.Away.Id)))
                    .OrderBy(m => m.UtcDate).FirstOrDefault();
                if (live == null)
                {
                    svc.ReportSessionGone("football");
                    return;
                }
                string score = (live.HomeScore.HasValue && live.AwayScore.HasValue)
                    ? live.HomeScore + " - " + live.AwayScore : "vs";
                string title = live.Home.Name + " " + score + " " + live.Away.Name;
                string clock = string.IsNullOrEmpty(live.Clock) ? "LIVE" : live.Clock;
                string artist = "🔴 " + clock + (string.IsNullOrEmpty(live.LeagueName) ? "" : " · " + live.LeagueName);
                // Score text, no artwork: clock changes every refresh and must
                // never trigger cover resolution (network storm → CPU spikes).
                svc.ReportSession("football", title, artist, true, resolveCover: false);
            }
            catch { }
        }

        /// <summary>Cheap fingerprint of everything the render depends on.</summary>
        private static string BuildDataSignature(List<EspnMatch> matches)
        {
            var sb = new System.Text.StringBuilder(matches.Count * 40 + 16);
            sb.Append(DateTime.Today.ToString("yyyy-MM-dd")).Append('|');
            foreach (var m in matches.OrderBy(m => m.LeagueSlug).ThenBy(m => m.Id))
            {
                sb.Append(m.LeagueSlug).Append('/').Append(m.Id)
                  .Append('/').Append(m.HomeScore).Append('-').Append(m.AwayScore)
                  .Append('/').Append(m.Clock).Append('/').Append(m.IsLive ? 'L' : m.IsFinished ? 'F' : 'U')
                  .Append('/').Append(m.UtcDate.Ticks).Append(';');
            }
            return sb.ToString();
        }

        /// <summary>Relaxes the refresh cadence when nothing is happening:
        /// no live match and none starting within 30 min → up to 15 min
        /// between ESPN polls instead of every minute or two.</summary>
        private void TuneTimer(List<EspnMatch> matches)
        {
            try
            {
                if (_refreshTimer == null) return;
                int baseMin = Math.Max(1, _settings.RefreshMinutes);
                var now = DateTime.UtcNow;
                bool hot = matches.Any(m => m.IsLive)
                    || matches.Any(m => !m.IsFinished && m.UtcDate != DateTime.MinValue
                        && m.UtcDate > now.AddMinutes(-5) && m.UtcDate <= now.AddMinutes(30));
                var want = TimeSpan.FromMinutes(hot ? baseMin : Math.Min(baseMin * 5, 15));
                if (_refreshTimer.Interval != want)
                {
                    _refreshTimer.Stop();
                    _refreshTimer.Interval = want;
                    _refreshTimer.Start();
                }
            }
            catch { }
        }

        private void ShowMessage(string text)
        {
            _rowsPanel.Children.Clear();
            _liveLabel.Text = "";
            _statusLabel.Text = text;
        }

        private void RenderMatches(List<EspnMatch> matches)
        {
            _rowsPanel.Children.Clear();
            int live = matches.Count(m => m.IsLive);
            _liveLabel.Text = live > 0 ? "● " + live + " LIVE" : "";

            if (matches.Count == 0)
            {
                _statusLabel.Text = "No live or upcoming matches right now.";
                return;
            }
            _statusLabel.Text = "";

            var favIds = new HashSet<string>(_settings.Teams
                .Where(t => (t.Kind ?? "team") == "team").Select(t => t.Id));

            // Finished group: newest first, placed top or bottom per settings.
            var upcoming = matches.Where(m => !m.IsFinished).OrderBy(m => m, Comparer<EspnMatch>.Create(CompareMatches)).ToList();
            var finished = matches.Where(m => m.IsFinished).OrderByDescending(m => m.UtcDate).ToList();
            var ordered = _settings.FinishedPosition == "top"
                ? finished.Concat(upcoming).ToList()
                : upcoming.Concat(finished).ToList();
            var shown = ordered.Take(Math.Max(1, _settings.MaxMatches)).ToList();
            var shownUp = shown.Where(m => !m.IsFinished).ToList();
            var shownFin = shown.Where(m => m.IsFinished).ToList();
            bool showHeader = _settings.ShowFinishedHeader && shownFin.Count > 0 && shownUp.Count > 0;

            if (_settings.CardTheme == "dark")
            {
                RenderDarkCards(shown);
                return;
            }

            if (_settings.FinishedPosition == "top")
            {
                if (showHeader)
                    _rowsPanel.Children.Add(BuildFinishedHeader(shownFin.Count));
                foreach (var m in shownFin)
                    _rowsPanel.Children.Add(BuildRow(m, favIds, IsLeagueFavorite(m.LeagueSlug)));
                if (showHeader)
                    _rowsPanel.Children.Add(BuildUpcomingHeader(shownUp.Count));
                foreach (var m in shownUp)
                    _rowsPanel.Children.Add(BuildRow(m, favIds, IsLeagueFavorite(m.LeagueSlug)));
            }
            else
            {
                if (showHeader)
                    _rowsPanel.Children.Add(BuildUpcomingHeader(shownUp.Count));
                foreach (var m in shownUp)
                    _rowsPanel.Children.Add(BuildRow(m, favIds, IsLeagueFavorite(m.LeagueSlug)));
                if (showHeader)
                    _rowsPanel.Children.Add(BuildFinishedHeader(shownFin.Count));
                foreach (var m in shownFin)
                    _rowsPanel.Children.Add(BuildRow(m, favIds, IsLeagueFavorite(m.LeagueSlug)));
            }
        }

        private TextBlock BuildFinishedHeader(int count)
        {
            return BuildSectionHeader(
                (TranslationService.Instance["Widget_FootballFinishedSection"] ?? "Finished")
                    + (count > 0 ? " (" + count + ")" : ""));
        }

        private TextBlock BuildUpcomingHeader(int count)
        {
            return BuildSectionHeader(
                (TranslationService.Instance["Widget_FootballUpcomingSection"] ?? "Upcoming")
                    + (count > 0 ? " (" + count + ")" : ""));
        }

        private static TextBlock BuildSectionHeader(string text)
        {
            return new TextBlock
            {
                Tag = "section-header",
                Text = text,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC)),
                Margin = new Thickness(0, 2, 0, 4)
            };
        }

        /// <summary>
        /// Assigns a remote crest without ever blocking the UI thread.
        /// Memory-cache hit = instant; otherwise async download then assign.
        /// Never throws; collapses the image on failure.
        /// </summary>
        private static void SetCrestAsync(Image img, string url)
        {
            if (img == null || string.IsNullOrEmpty(url))
            {
                if (img != null) img.Visibility = Visibility.Collapsed;
                return;
            }
            if (_crestCache.TryGetValue(url, out var cached))
            {
                img.Source = cached;
                return;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    byte[] data = await _crestHttp.GetByteArrayAsync(url).ConfigureAwait(false);
                    if (data == null || data.Length == 0 || data.Length > 2 * 1024 * 1024) return;
                    BitmapImage bmp;
                    using (var ms = new MemoryStream(data, false))
                    {
                        bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                        bmp.Freeze();
                    }
                    _crestCache[url] = bmp;
                    try { img.Dispatcher.BeginInvoke(new Action(() => { img.Source = bmp; })); } catch { }
                }
                catch
                {
                    try { img.Dispatcher.BeginInvoke(new Action(() => { img.Visibility = Visibility.Collapsed; })); } catch { }
                }
            });
        }

        /// <summary>Small league logo (null when none). Collapses on load failure.</summary>
        private static Image? BuildLeagueIcon(string url, double size)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                var img = new Image
                {
                    Stretch = Stretch.Uniform,
                    Width = size, Height = size,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 3, 0)
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                img.ImageFailed += (_, _) => img.Visibility = Visibility.Collapsed;
                SetCrestAsync(img, url);
                return img;
            }
            catch { return null; }
        }

        private static Brush? FinishedBrush(FootballSettings s)
        {
            if (string.IsNullOrWhiteSpace(s.FinishedTextColor)) return null;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(s.FinishedTextColor.Trim());
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }
            catch { return null; }
        }

        private static CultureInfo AppCulture()
        {
            try { return new CultureInfo(TranslationService.Instance.CurrentCulture); }
            catch { return CultureInfo.InvariantCulture; }
        }

        /// <summary>Short date per settings, with Today/Tomorrow/Yesterday + kickoff time.</summary>
        private static string FormatShortDate(DateTime local, CultureInfo culture, FootballSettings s)
        {
            if (local.Date == DateTime.Today)
                return (TranslationService.Instance["Widget_FootballToday"] ?? "Today") + " " + local.ToString("HH:mm");
            if (local.Date == DateTime.Today.AddDays(1))
                return (TranslationService.Instance["Widget_FootballTomorrow"] ?? "Tomorrow") + " " + local.ToString("HH:mm");
            if (local.Date == DateTime.Today.AddDays(-1))
                return (TranslationService.Instance["Widget_FootballYesterday"] ?? "Yesterday") + " " + local.ToString("HH:mm");
            string fmt = (s.DateFormat ?? "text").ToLowerInvariant();
            if (fmt == "numeric")
                return local.ToString("dd/MM/yy");
            if (fmt == "daynumeric")
                return local.ToString("ddd", culture) + " " + local.ToString("dd/MM/yy");
            return local.ToString("MMM d", culture);
        }

        /// <summary>Date-band date per settings.</summary>
        private static string FormatBandDate(DateTime local, CultureInfo culture, FootballSettings s)
        {
            string fmt = (s.DateFormat ?? "text").ToLowerInvariant();
            if (fmt == "numeric")
                return local.ToString("dd/MM/yyyy");
            if (fmt == "daynumeric")
                return local.ToString("ddd", culture) + " " + local.ToString("dd/MM/yyyy");
            return local.ToString("dddd d MMMM", culture);
        }

        private static string NormalizeDateFormat(string? v)
        {
            string f = (v ?? "").ToLowerInvariant();
            return (f == "numeric" || f == "daynumeric") ? f : "text";
        }

        private static readonly SolidColorBrush DarkCardBrush =
            new SolidColorBrush(Color.FromRgb(0x18, 0x19, 0x1C));
        private static readonly SolidColorBrush DarkDateBrush =
            new SolidColorBrush(Color.FromRgb(0x8A, 0x8E, 0x96));
        private static readonly SolidColorBrush PureWhiteBrush =
            new SolidColorBrush(Colors.White);

        /// <summary>Dark-cards theme: date bands + 3-column match cards.</summary>
        private void RenderDarkCards(List<EspnMatch> shown)
        {
            var shownUp = shown.Where(m => !m.IsFinished).ToList();
            var shownFin = shown.Where(m => m.IsFinished).ToList();
            bool showHeader = _settings.ShowFinishedHeader && shownFin.Count > 0 && shownUp.Count > 0;

            if (_settings.FinishedPosition == "top")
            {
                if (showHeader)
                    _rowsPanel.Children.Add(BuildFinishedHeader(shownFin.Count));
                RenderDarkGroup(shownFin);
                if (showHeader)
                    _rowsPanel.Children.Add(BuildUpcomingHeader(shownUp.Count));
                RenderDarkGroup(shownUp);
            }
            else
            {
                if (showHeader)
                    _rowsPanel.Children.Add(BuildUpcomingHeader(shownUp.Count));
                RenderDarkGroup(shownUp);
                if (showHeader)
                    _rowsPanel.Children.Add(BuildFinishedHeader(shownFin.Count));
                RenderDarkGroup(shownFin);
            }
        }

        private void RenderDarkGroup(List<EspnMatch> group)
        {
            var culture = AppCulture();
            string? lastDay = null;
            var favIds = new HashSet<string>(_settings.Teams
                .Where(t => (t.Kind ?? "team") == "team").Select(t => t.Id));
            foreach (var m in group)
            {
                string dayKey = m.UtcDate == DateTime.MinValue ? "?" : m.UtcDate.ToLocalTime().ToString("yyyy-MM-dd");
                if (dayKey != lastDay)
                {
                    lastDay = dayKey;
                    _rowsPanel.Children.Add(BuildDateBand(m, culture));
                }
                _rowsPanel.Children.Add(BuildDarkCard(m, favIds, IsLeagueFavorite(m.LeagueSlug), culture));
            }
        }

        private TextBlock BuildDateBand(EspnMatch m, CultureInfo culture)
        {
            string text;
            if (m.UtcDate == DateTime.MinValue)
            {
                text = "—";
            }
            else
            {
                var local = m.UtcDate.ToLocalTime();
                string today = TranslationService.Instance["Widget_FootballToday"] ?? "Today";
                string tomorrow = TranslationService.Instance["Widget_FootballTomorrow"] ?? "Tomorrow";
                text = local.Date == DateTime.Today
                    ? today
                    : local.Date == DateTime.Today.AddDays(1)
                        ? tomorrow
                        : FormatBandDate(local, culture, _settings);
            }
            return new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = DarkDateBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(2, 6, 0, 4)
            };
        }

        private static string TriCode(EspnTeam team)
        {
            if (!string.IsNullOrEmpty(team.Abbr) && team.Abbr.Length == 3)
                return team.Abbr.ToUpperInvariant();
            string n = (team.Name ?? "").Trim();
            if (n.Length <= 3) return n.ToUpperInvariant();
            return new string(n.Where(char.IsLetterOrDigit).Take(3).ToArray()).ToUpperInvariant();
        }

        private FrameworkElement BuildDarkCard(EspnMatch m, HashSet<string> favIds, bool leagueFav, CultureInfo culture)
        {
            var card = new Border
            {
                Background = DarkCardBrush,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 6),
                Margin = new Thickness(0, 0, 0, 6)
            };
            double scale = Math.Clamp(_settings.CardScale <= 0 ? 1.0 : _settings.CardScale, 0.5, 1.5);
            if (Math.Abs(scale - 1.0) > 0.01)
                card.LayoutTransform = new ScaleTransform(scale, scale);
            var stack = new StackPanel();
            card.Child = stack;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(BuildDarkTeam(m.Home, favIds.Contains(m.Home.Id), true));
            Grid.SetColumn(grid.Children[grid.Children.Count - 1], 0);
            grid.Children.Add(BuildDarkCenter(m));
            Grid.SetColumn(grid.Children[grid.Children.Count - 1], 1);
            grid.Children.Add(BuildDarkTeam(m.Away, favIds.Contains(m.Away.Id), false));
            Grid.SetColumn(grid.Children[grid.Children.Count - 1], 2);
            stack.Children.Add(grid);

            // Footer: league (center) + date bottom-right for finished.
            var footer = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var leagueStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = (leagueFav ? "Unfollow league " : "Follow league ") + m.LeagueName
            };
            leagueStack.Children.Add(new TextBlock
            {
                Text = leagueFav ? "★ " : "☆ ",
                FontSize = 8,
                Foreground = leagueFav ? AccentBrush : DarkDateBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            var darkLeagueIcon = BuildLeagueIcon(m.LeagueLogo, 12);
            if (darkLeagueIcon != null)
                leagueStack.Children.Add(darkLeagueIcon);
            leagueStack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(m.LeagueName) ? m.LeagueSlug : m.LeagueName,
                FontSize = 9,
                Foreground = DarkDateBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            string leagueSlug = m.LeagueSlug;
            string leagueName = m.LeagueName;
            leagueStack.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                SetLeagueFavorite(leagueSlug, leagueName, !IsLeagueFavorite(leagueSlug));
            };
            Grid.SetColumn(leagueStack, 0);
            footer.Children.Add(leagueStack);
            if (m.IsFinished && m.UtcDate != DateTime.MinValue)
            {
                footer.Children.Add(new TextBlock
                {
                    Text = FormatShortDate(m.UtcDate.ToLocalTime(), culture, _settings),
                    FontSize = 9,
                    Foreground = DarkDateBrush,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                });
                Grid.SetColumn(footer.Children[footer.Children.Count - 1], 1);
            }
            stack.Children.Add(footer);
            return card;
        }

        private FrameworkElement BuildDarkTeam(EspnTeam team, bool isFav, bool isHome)
        {
            var col = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = team.Name + " (Google)"
            };
            if (_settings.ShowCrests && !string.IsNullOrEmpty(team.Logo))
            {
                try
                {
                    var img = new Image
                    {
                        Stretch = Stretch.Uniform,
                        Width = 28, Height = 28,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0, 0, 4)
                    };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    img.ImageFailed += (_, _) => img.Visibility = Visibility.Collapsed;
                    SetCrestAsync(img, team.Logo);
                    col.Children.Add(img);
                }
                catch { }
            }
            var code = new TextBlock
            {
                Text = TriCode(team),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = PureWhiteBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            col.Children.Add(code);
            var star = new TextBlock
            {
                Text = isFav ? "★" : "☆",
                FontSize = 9,
                Foreground = isFav ? AccentBrush : DarkDateBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
                Cursor = Cursors.Hand,
                ToolTip = (isFav ? "Unfollow " : "Follow ") + team.Name
            };
            col.Children.Add(star);
            string teamId = team.Id;
            string teamName = team.Name;
            star.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                ToggleFavorite(teamId, teamName);
            };
            col.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                OpenGoogleSearch(teamName);
            };
            return col;
        }

        private FrameworkElement BuildDarkCenter(EspnMatch m)
        {
            var cell = new Grid
            {
                MinWidth = 64,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0),
                Cursor = Cursors.Hand,
                ToolTip = TranslationService.Instance["Widget_FootballDetails"] ?? "Match details"
            };
            string detailLeague = m.LeagueSlug;
            string detailId = m.Id;
            cell.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                OpenMatchDetails(detailLeague, detailId, m);
            };
            // Subtle ball watermark behind the score/time.
            cell.Children.Add(new TextBlock
            {
                Text = "⚽",
                FontSize = 34,
                Opacity = 0.08,
                Foreground = PureWhiteBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            var fg = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            string main;
            Brush mainBrush = PureWhiteBrush;
            string sub = "";
            Brush subBrush = DarkDateBrush;
            if (m.IsLive)
            {
                main = (m.HomeScore.HasValue && m.AwayScore.HasValue) ? m.HomeScore + " - " + m.AwayScore : "LIVE";
                sub = string.IsNullOrEmpty(m.Clock) ? "LIVE" : m.Clock;
                subBrush = LiveBrush;
            }
            else if (m.IsFinished)
            {
                main = (m.HomeScore.HasValue && m.AwayScore.HasValue) ? m.HomeScore + " - " + m.AwayScore : "FT";
                sub = "FT";
                var fin = FinishedBrush(_settings);
                if (fin != null) { mainBrush = fin; subBrush = fin; }
            }
            else
            {
                var local = m.UtcDate == DateTime.MinValue ? (DateTime?)null : m.UtcDate.ToLocalTime();
                main = local?.ToString("HH:mm") ?? "—";
            }
            fg.Children.Add(new TextBlock
            {
                Text = main,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = mainBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            if (!string.IsNullOrEmpty(sub))
            {
                fg.Children.Add(new TextBlock
                {
                    Text = "● " + sub,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = subBrush,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }
            cell.Children.Add(fg);
            return cell;
        }

        private FrameworkElement BuildRow(EspnMatch m, HashSet<string> favIds, bool leagueFav)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock status;
            Brush? finBrush = m.IsFinished ? FinishedBrush(_settings) : null;
            if (m.IsLive)
            {
                string clock = string.IsNullOrEmpty(m.Clock) ? "LIVE" : m.Clock;
                status = new TextBlock { Text = "● " + clock, FontWeight = FontWeights.Bold, FontSize = 10, Foreground = LiveBrush };
            }
            else if (m.IsFinished)
            {
                string label = "FT";
                if (_settings.ShowFinishedDates && m.UtcDate != DateTime.MinValue)
                {
                    CultureInfo culture;
                    try { culture = new CultureInfo(TranslationService.Instance.CurrentCulture); }
                    catch { culture = CultureInfo.InvariantCulture; }
                    label = FormatShortDate(m.UtcDate.ToLocalTime(), culture, _settings) + " · FT";
                }
                status = new TextBlock { Text = label, FontSize = 10, Foreground = finBrush ?? FaintBrush };
            }
            else
            {
                var local = m.UtcDate == DateTime.MinValue ? (DateTime?)null : m.UtcDate.ToLocalTime();
                // App language, not Windows locale (French Windows + English app = English days).
                CultureInfo culture;
                try { culture = new CultureInfo(TranslationService.Instance.CurrentCulture); }
                catch { culture = CultureInfo.InvariantCulture; }
                string today = TranslationService.Instance["Widget_FootballToday"] ?? "Today";
                string tomorrow = TranslationService.Instance["Widget_FootballTomorrow"] ?? "Tomorrow";
                string fmt = (_settings.DateFormat ?? "text").ToLowerInvariant();
                string when = local == null ? "—" :
                    (local.Value.Date == DateTime.Today ? today + " " + local.Value.ToString("HH:mm")
                    : local.Value.Date == DateTime.Today.AddDays(1) ? tomorrow + " " + local.Value.ToString("HH:mm")
                    : fmt == "numeric" ? local.Value.ToString("dd/MM/yy HH:mm")
                    : fmt == "daynumeric" ? local.Value.ToString("ddd dd/MM/yy HH:mm", culture)
                    : local.Value.ToString("ddd HH:mm", culture));
                status = new TextBlock { Text = when, FontSize = 10, Foreground = DimBrush };
            }
            status.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(status, 0);
            top.Children.Add(status);

            var leagueStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Cursor = Cursors.Hand,
                ToolTip = (leagueFav ? "Unfollow league " : "Follow league ") + m.LeagueName
            };
            var leagueStar = new TextBlock
            {
                Text = leagueFav ? "★ " : "☆ ",
                FontSize = 9,
                Foreground = leagueFav ? AccentBrush : FaintBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            var league = new TextBlock
            {
                Text = string.IsNullOrEmpty(m.LeagueName) ? m.LeagueSlug : m.LeagueName,
                FontSize = 9, Foreground = FaintBrush, VerticalAlignment = VerticalAlignment.Center
            };
            leagueStack.Children.Add(leagueStar);
            var leagueIcon = BuildLeagueIcon(m.LeagueLogo, 12);
            if (leagueIcon != null)
                leagueStack.Children.Add(leagueIcon);
            leagueStack.Children.Add(league);
            string leagueSlug = m.LeagueSlug;
            string leagueName = m.LeagueName;
            leagueStack.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                SetLeagueFavorite(leagueSlug, leagueName, !IsLeagueFavorite(leagueSlug));
            };
            Grid.SetColumn(leagueStack, 1);
            top.Children.Add(leagueStack);
            row.Children.Add(top);

            var line = new Grid { Margin = new Thickness(0, 1, 0, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var home = BuildTeamBlock(m.Home, favIds.Contains(m.Home.Id), true, finBrush);
            Grid.SetColumn(home, 0);
            line.Children.Add(home);

            string scoreText = (m.IsLive || m.IsFinished) && m.HomeScore.HasValue && m.AwayScore.HasValue
                ? m.HomeScore + " - " + m.AwayScore
                : "vs";
            var score = new TextBlock
            {
                Text = scoreText,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = m.IsLive ? LiveBrush : (finBrush ?? TextBrush),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                Cursor = Cursors.Hand,
                ToolTip = TranslationService.Instance["Widget_FootballDetails"] ?? "Match details"
            };
            string detailLeague = m.LeagueSlug;
            string detailId = m.Id;
            score.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                OpenMatchDetails(detailLeague, detailId, m);
            };
            Grid.SetColumn(score, 1);
            line.Children.Add(score);

            var away = BuildTeamBlock(m.Away, favIds.Contains(m.Away.Id), false, finBrush);
            Grid.SetColumn(away, 2);
            line.Children.Add(away);

            row.Children.Add(line);
            return row;
        }

        private FrameworkElement BuildTeamBlock(EspnTeam team, bool isFav, bool alignRight, Brush? nameOverride = null)
        {
            var outer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                ToolTip = (isFav ? "Unfollow " : "Follow ") + team.Name
            };

            var star = new TextBlock
            {
                Text = isFav ? "★ " : "☆ ",
                FontSize = 11,
                Foreground = isFav ? AccentBrush : FaintBrush,
                VerticalAlignment = VerticalAlignment.Center
            };

            var crestBox = new Grid
            {
                Width = 18, Height = 18,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            var tla = new TextBlock
            {
                Text = string.IsNullOrEmpty(team.Abbr) ? "?" : team.Abbr,
                FontSize = 7,
                Foreground = DimBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            crestBox.Children.Add(tla);
            if (_settings.ShowCrests && !string.IsNullOrEmpty(team.Logo))
            {
                try
                {
                    var img = new Image
                    {
                        Stretch = Stretch.Uniform,
                        Width = 18, Height = 18
                    };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    img.ImageFailed += (_, _) => img.Visibility = Visibility.Collapsed;
                    SetCrestAsync(img, team.Logo);
                    crestBox.Children.Add(img);
                }
                catch { }
            }

            string name = string.IsNullOrEmpty(team.Name) ? "?" : team.Name;
            if (name.Length > 16) name = name.Substring(0, 15) + "…";
            var nameBlock = new TextBlock
            {
                Text = name,
                FontSize = 12,
                Foreground = nameOverride ?? TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            if (alignRight)
            {
                outer.Children.Add(nameBlock);
                outer.Children.Add(crestBox);
                outer.Children.Add(star);
            }
            else
            {
                outer.Children.Add(star);
                outer.Children.Add(crestBox);
                outer.Children.Add(nameBlock);
            }

            string teamId = team.Id;
            string teamName = team.Name;
            star.Cursor = Cursors.Hand;
            star.ToolTip = (isFav ? "Unfollow " : "Follow ") + team.Name;
            star.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                ToggleFavorite(teamId, teamName);
            };
            outer.ToolTip = team.Name + " (Google)";
            outer.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                OpenGoogleSearch(teamName);
            };
            return outer;
        }

        private void OpenMatchDetails(string leagueSlug, string eventId, EspnMatch m)
        {
            try
            {
                if (_settings.MatchClickAction == "google")
                {
                    OpenGoogleSearch(m.Home.Name + " vs " + m.Away.Name);
                    return;
                }
                if (!long.TryParse((eventId ?? "").Trim(), out _)) return;
                var w = new FootballMatchDetailWindow(leagueSlug, eventId.Trim(), m);
                w.Show();
            }
            catch { }
        }

        internal static void OpenGoogleSearch(string query)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://www.google.com/search?q=" + Uri.EscapeDataString(query ?? ""),
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
        }

        private void ToggleFavorite(string teamId, string teamName)
        {
            SetFavorite(teamId, teamName, !IsFavorite(teamId));
        }

        public void SetFavorite(string teamId, string teamName, bool follow, string leagueSlug = "")
        {
            if (string.IsNullOrEmpty(teamId)) return;
            var existing = _settings.Teams.FirstOrDefault(t => t.Id == teamId && (t.Kind ?? "team") == "team");
            if (follow && existing == null)
                _settings.Teams.Add(new FootballFavTeam { Id = teamId, Name = teamName, Kind = "team", LeagueSlug = leagueSlug ?? "" });
            else if (!follow && existing != null)
                _settings.Teams.Remove(existing);
            // A followed team needs its league subscribed, otherwise no match ever shows.
            if (follow && !string.IsNullOrEmpty(leagueSlug)
                && !_settings.Leagues.Any(l => string.Equals(l, leagueSlug, StringComparison.OrdinalIgnoreCase)))
            {
                _settings.Leagues.Add(leagueSlug);
            }
            SaveSettings();
            RefreshNowAsync();
        }

        public bool IsFavorite(string teamId)
        {
            return !string.IsNullOrEmpty(teamId)
                && _settings.Teams.Any(t => t.Id == teamId && (t.Kind ?? "team") == "team");
        }

        public void SetLeagueFavorite(string slug, string name, bool follow)
        {
            if (string.IsNullOrEmpty(slug)) return;
            var existing = _settings.Teams.FirstOrDefault(t => t.Id == slug && (t.Kind ?? "") == "league");
            if (follow && existing == null)
                _settings.Teams.Add(new FootballFavTeam { Id = slug, Name = name, Kind = "league" });
            else if (!follow && existing != null)
                _settings.Teams.Remove(existing);
            SaveSettings();
            RefreshNowAsync();
        }

        public bool IsLeagueFavorite(string slug)
        {
            return !string.IsNullOrEmpty(slug)
                && _settings.Teams.Any(t => t.Id == slug && (t.Kind ?? "") == "league");
        }

        /// <summary>Slim translucent scrollbar (transparent track, soft thumb).</summary>
        internal static void StyleScrollViewer(ScrollViewer sv)
        {
            try
            {
                const string tpl = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ScrollBar'>"
                    + "<Grid Background='Transparent' Width='8'>"
                    + "<Track Name='PART_Track' IsDirectionReversed='True'>"
                    + "<Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'>"
                    + "<Border Name='b' CornerRadius='4' Background='#40FFFFFF' Margin='2,1,2,1'/>"
                    + "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'>"
                    + "<Setter TargetName='b' Property='Background' Value='#90FFFFFF'/>"
                    + "</Trigger></ControlTemplate.Triggers>"
                    + "</ControlTemplate></Thumb.Template></Thumb></Track.Thumb>"
                    + "</Track></Grid></ControlTemplate>";
                var style = new Style(typeof(ScrollBar));
                style.Setters.Add(new Setter(ScrollBar.BackgroundProperty, Brushes.Transparent));
                style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(tpl)));
                sv.Resources.Add(typeof(ScrollBar), style);
            }
            catch { }
        }
        public static bool IsWomenLeague(string slug)
        {
            if (string.IsNullOrEmpty(slug)) return false;
            string s = slug.ToLowerInvariant();
            // NOTE: never match the "fifa.w" prefix bare — men's "fifa.world",
            // "fifa.worldq.*" and "fifa.wcq.ply" all start with it.
            if (s.Contains(".w.") || s.EndsWith(".w")) return true;
            if (s.Contains("ww") || s.Contains("nwsl") || s.Contains("shebelieves")
                || s.Contains("femenina") || s.Contains("reina") || s.Contains("womens"))
                return true;
            if (s.StartsWith("fifa.w.") || s.StartsWith("uefa.w") || s.StartsWith("concacaf.w")
                || s.StartsWith("can.w") || s.StartsWith("aus.w"))
                return true;
            if (s.Contains("weuro") || s.Contains("wchampions") || s.Contains("w.nations")
                || s.Contains("w.gold") || s.Contains("w.finalissima") || s.Contains("w.olympics")
                || s.Contains("w.europa"))
                return true;
            return false;
        }

        /// <summary>
        /// Every known team worldwide, one row per squad (men/women split).
        /// Directory first, then matches, then favorites.
        /// </summary>
        public List<FootballFavTeam> GetKnownTeams()
        {
            var dir = EspnService.GetDirectory();
            var womenIds = new HashSet<string>(dir
                .Where(t => IsWomenLeague(t.LeagueSlug))
                .Select(t => t.Id));
            var byId = new Dictionary<string, FootballFavTeam>();
            foreach (var t in dir)
            {
                if (!byId.ContainsKey(t.Id))
                    byId[t.Id] = new FootballFavTeam { Id = t.Id, Name = t.Name, Kind = "team", LeagueSlug = t.LeagueSlug };
            }
            foreach (var m in _lastMatches)
            {
                if (!string.IsNullOrEmpty(m.Home.Id) && !byId.ContainsKey(m.Home.Id))
                    byId[m.Home.Id] = new FootballFavTeam { Id = m.Home.Id, Name = m.Home.Name, Kind = "team", LeagueSlug = m.LeagueSlug };
                if (!string.IsNullOrEmpty(m.Away.Id) && !byId.ContainsKey(m.Away.Id))
                    byId[m.Away.Id] = new FootballFavTeam { Id = m.Away.Id, Name = m.Away.Name, Kind = "team", LeagueSlug = m.LeagueSlug };
            }
            foreach (var t in _settings.Teams)
                if (!byId.ContainsKey(t.Id))
                    byId[t.Id] = new FootballFavTeam { Id = t.Id, Name = t.Name, Kind = "team", LeagueSlug = t.LeagueSlug ?? "" };
            foreach (var t in byId.Values)
            {
                bool women = womenIds.Contains(t.Id) || IsWomenLeague(t.LeagueSlug ?? "");
                t.DisplayName = women ? (t.Name ?? "") + " - Women" : (t.Name ?? "");
            }
            return byId.Values.OrderBy(t => t.DisplayName).ToList();
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
    }

    /// <summary>Search-and-follow dialog for favorite teams.</summary>
    public class FootballTeamSearchWindow : Window
    {
        private readonly FootballView _view;
        private readonly TextBox _searchBox;
        private readonly StackPanel _listPanel;
        private readonly TextBlock _statusText;
        private List<FootballFavTeam> _teams = new List<FootballFavTeam>();

        public FootballTeamSearchWindow(FootballView view)
        {
            _view = view;
            Title = "Follow teams";
            Width = 320;
            Height = 420;
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

            var root = new StackPanel { Margin = new Thickness(12) };
            root.Children.Add(new TextBlock
            {
                Text = "Search teams",
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 8)
            });

            _searchBox = new TextBox
            {
                Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 0, 8)
            };
            _searchBox.TextChanged += (_, _) => RefreshList();
            root.Children.Add(_searchBox);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 250
            };
            FootballView.StyleScrollViewer(scroll);
            _listPanel = new StackPanel();
            scroll.Content = _listPanel;
            root.Children.Add(scroll);

            var hintRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            _statusText = new TextBlock
            {
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center
            };
            hintRow.Children.Add(_statusText);
            var refreshBtn = new Button
            {
                Content = "⟳",
                FontSize = 12,
                Width = 26,
                Height = 24,
                Margin = new Thickness(6, 0, 0, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand,
                Focusable = false,
                ToolTip = "Refresh matches first"
            };
            refreshBtn.Click += (_, _) =>
            {
                _view.RefreshNowAsync();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    _teams = _view.GetKnownTeams();
                    RefreshList();
                };
                timer.Start();
            };
            hintRow.Children.Add(refreshBtn);
            root.Children.Add(hintRow);

            var closeBtn = new Button
            {
                Content = "Close",
                Height = 28,
                Margin = new Thickness(0, 8, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            closeBtn.Click += (_, _) => Close();
            root.Children.Add(closeBtn);

            Content = root;

            _teams = _view.GetKnownTeams();
            RefreshList();
            Loaded += (_, _) => _searchBox.Focus();
            EspnService.RostersChanged += OnRostersChanged;
            Closed += (_, _) => EspnService.RostersChanged -= OnRostersChanged;
        }

        private void OnRostersChanged(object? sender, EventArgs e)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    _teams = _view.GetKnownTeams();
                    RefreshList();
                });
            }
            catch { }
        }

        /// <summary>Lowercase + accents stripped ("Türkiye" → "turkiye").</summary>
        private static string SearchNorm(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            string lower = s.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(lower.Length);
            foreach (char c in lower)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
        }

        // French exonyms → normalized ESPN names (national teams differ FR/EN).
        private static readonly Dictionary<string, string> _frAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["angleterre"] = "england", ["espagne"] = "spain", ["allemagne"] = "germany",
            ["italie"] = "italy", ["pays-bas"] = "netherlands", ["pays bas"] = "netherlands",
            ["hollande"] = "netherlands", ["belgique"] = "belgium", ["suisse"] = "switzerland",
            ["pologne"] = "poland", ["croatie"] = "croatia", ["serbie"] = "serbia",
            ["turquie"] = "turkiye", ["turc"] = "turkiye", ["grece"] = "greece",
            ["suede"] = "sweden", ["norvege"] = "norway", ["danemark"] = "denmark",
            ["autriche"] = "austria", ["ecosse"] = "scotland", ["galles"] = "wales",
            ["pays de galles"] = "wales", ["irlande"] = "ireland", ["roumanie"] = "romania",
            ["hongrie"] = "hungary", ["tchequie"] = "czechia", ["armenie"] = "armenia",
            ["azerbaidjan"] = "azerbaijan", ["bielorussie"] = "belarus", ["chypre"] = "cyprus",
            ["georgie"] = "georgia", ["moldavie"] = "moldova", ["macedoine"] = "macedonia",
            ["montenegro"] = "montenegro",
        };

        private static bool MatchesAlias(string? espnName, string q)
        {
            if (string.IsNullOrEmpty(q) || string.IsNullOrEmpty(espnName)) return false;
            string norm = SearchNorm(espnName);
            foreach (var kv in _frAliases)
            {
                // Query in French matches the ESPN name, or vice-versa.
                if (q.Contains(kv.Key) && norm.Contains(kv.Value)) return true;
                if (q.Contains(kv.Value) && norm.Contains(kv.Value)) return true;
            }
            return false;
        }

        private void RefreshList()
        {
            _listPanel.Children.Clear();
            string q = SearchNorm(_searchBox.Text ?? "");

            _listPanel.Children.Add(new TextBlock
            {
                Text = "Leagues (follow = all its matches)",
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC)),
                Margin = new Thickness(0, 0, 0, 4)
            });
            foreach (var lg in Palisades.Services.EspnService.CuratedLeagues)
            {
                if (!string.IsNullOrEmpty(q) && !SearchNorm(lg.Name).Contains(q)
                    && !lg.Slug.ToLowerInvariant().Contains(q))
                    continue;
                string slug = lg.Slug;
                string name = lg.Name;
                var cb = new CheckBox
                {
                    Content = name,
                    Foreground = Brushes.White,
                    FontSize = 12,
                    Margin = new Thickness(0, 2, 0, 2),
                    IsChecked = _view.IsLeagueFavorite(slug)
                };
                cb.Checked += (_, _) => _view.SetLeagueFavorite(slug, name, true);
                cb.Unchecked += (_, _) => _view.SetLeagueFavorite(slug, name, false);
                _listPanel.Children.Add(cb);
            }

            _listPanel.Children.Add(new TextBlock
            {
                Text = "Teams",
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC)),
                Margin = new Thickness(0, 8, 0, 4)
            });
            var shown = 0;
            foreach (var t in _teams)
            {
                if (!string.IsNullOrEmpty(q) && !SearchNorm(t.Name).Contains(q)
                    && !SearchNorm(t.DisplayName).Contains(q)
                    && !MatchesAlias(t.Name, q))
                    continue;
                if (++shown > 60) break;
                string id = t.Id;
                string name = t.Name;
                string leagueSlug = t.LeagueSlug ?? "";
                var cb = new CheckBox
                {
                    Content = string.IsNullOrEmpty(t.DisplayName) ? name : t.DisplayName,
                    Foreground = Brushes.White,
                    FontSize = 12,
                    Margin = new Thickness(0, 2, 0, 2),
                    IsChecked = _view.IsFavorite(id)
                };
                cb.Checked += (_, _) => _view.SetFavorite(id, name, true, leagueSlug);
                cb.Unchecked += (_, _) => _view.SetFavorite(id, name, false);
                _listPanel.Children.Add(cb);
            }
            if (shown == 0)
            {
                _listPanel.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(q)
                        ? "No teams loaded yet — refresh the widget first."
                        : "No match. Try another spelling.",
                    Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            int done = EspnService.RosterDoneCount;
            int total = EspnService.RosterTotalCount;
            _statusText.Text = total > 0 && done < total
                ? "Loading world teams… " + done + "/" + total + " (" + _teams.Count + " so far)"
                : _teams.Count + " teams worldwide.";
        }
    }

    /// <summary>Match report dialog: key events (goals/cards/subs) + team stats.</summary>
    public class FootballMatchDetailWindow : Window
    {
        private readonly StackPanel _body = new StackPanel { Margin = new Thickness(12) };

        public FootballMatchDetailWindow(string leagueSlug, string eventId, EspnMatch m)
        {
            Title = (m.Home.Abbr != "" ? m.Home.Abbr : m.Home.Name) + " "
                + (m.HomeScore?.ToString() ?? "-") + " - " + (m.AwayScore?.ToString() ?? "-") + " "
                + (m.Away.Abbr != "" ? m.Away.Abbr : m.Away.Name);
            Width = 360;
            Height = 480;
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            FootballView.StyleScrollViewer(scroll);
            scroll.Content = _body;
            Content = scroll;

            _body.Children.Add(new TextBlock
            {
                Text = "Loading…",
                Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                FontSize = 12
            });
            _ = LoadAsync(leagueSlug, eventId, m);
        }

        private async Task LoadAsync(string leagueSlug, string eventId, EspnMatch m)
        {
            var detail = await EspnService.GetMatchSummaryAsync(leagueSlug, eventId).ConfigureAwait(true);
            _body.Children.Clear();
            if (detail == null)
            {
                _body.Children.Add(new TextBlock
                {
                    Text = "No details available for this match.",
                    Foreground = Brushes.White, FontSize = 12, TextWrapping = TextWrapping.Wrap
                });
                return;
            }
            string homeName = detail.HomeName != "" ? detail.HomeName : m.Home.Name;
            string awayName = detail.AwayName != "" ? detail.AwayName : m.Away.Name;
            string score = (m.HomeScore?.ToString() ?? "-") + " - " + (m.AwayScore?.ToString() ?? "-");

            _body.Children.Add(new TextBlock
            {
                Text = homeName + "  " + score + "  " + awayName,
                FontWeight = FontWeights.Bold, FontSize = 14, Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2)
            });
            string league = m.LeagueName != "" ? m.LeagueName : m.LeagueSlug;
            string sub = league + (detail.Venue != "" ? " · " + detail.Venue : "");
            _body.Children.Add(new TextBlock
            {
                Text = sub, FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(0, 0, 0, 10)
            });

            BuildScorers(detail, homeName, awayName);

            if (detail.Events.Count > 0)
            {
                _body.Children.Add(SectionTitle("Key moments"));
                foreach (var ev in detail.Events)
                    _body.Children.Add(BuildEventRow(ev));
            }
            if (detail.Stats.Count > 0)
            {
                _body.Children.Add(SectionTitle("Stats"));
                var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                int r = 0;
                foreach (var (name, home, away) in detail.Stats)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    grid.Children.Add(new TextBlock
                    {
                        Text = home, Foreground = Brushes.White, FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 1)
                    });
                    Grid.SetRow(grid.Children[grid.Children.Count - 1], r);
                    Grid.SetColumn(grid.Children[grid.Children.Count - 1], 0);
                    grid.Children.Add(new TextBlock
                    {
                        Text = name, FontSize = 10,
                        Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8, 1, 8, 1)
                    });
                    Grid.SetRow(grid.Children[grid.Children.Count - 1], r);
                    Grid.SetColumn(grid.Children[grid.Children.Count - 1], 1);
                    grid.Children.Add(new TextBlock
                    {
                        Text = away, Foreground = Brushes.White, FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 1, 0, 1)
                    });
                    Grid.SetRow(grid.Children[grid.Children.Count - 1], r);
                    Grid.SetColumn(grid.Children[grid.Children.Count - 1], 2);
                    r++;
                }
                _body.Children.Add(grid);
            }
        }

        private static TextBlock SectionTitle(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold, FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC)),
                Margin = new Thickness(0, 4, 0, 4)
            };
        }

        /// <summary>Scorers grouped per side under each team ("Raphinha 3', 57'").</summary>
        private void BuildScorers(EspnService.EspnMatchDetail detail, string homeName, string awayName)
        {
            var homeScorers = new List<(string Name, string Minute)>();
            var awayScorers = new List<(string Name, string Minute)>();
            foreach (var ev in detail.Events)
            {
                if (ev.Kind != "goal" && ev.Kind != "penalty") continue;
                // "Goal! Barcelona 1, Feyenoord 0. Raphinha (Barcelona) left footed shot…"
                var mt = System.Text.RegularExpressions.Regex.Match(ev.Text ?? "", @"\.\s*([^(]+?)\s*\(([^)]+)\)");
                if (!mt.Success) continue;
                string scorer = mt.Groups[1].Value.Trim();
                string team = mt.Groups[2].Value.Trim();
                bool ownGoal = (ev.Text ?? "").ToLowerInvariant().Contains("own goal");
                bool isHome = team.Equals(homeName, StringComparison.OrdinalIgnoreCase)
                    || homeName.ToLowerInvariant().Contains(team.ToLowerInvariant())
                    || team.ToLowerInvariant().Contains(homeName.ToLowerInvariant());
                if (ownGoal) isHome = !isHome;
                string minute = (ev.Clock ?? "").Trim();
                if (isHome) homeScorers.Add((scorer, minute));
                else awayScorers.Add((scorer, minute));
            }
            if (homeScorers.Count == 0 && awayScorers.Count == 0) return;
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(BuildScorerColumn(homeName, homeScorers, HorizontalAlignment.Left));
            Grid.SetColumn(grid.Children[grid.Children.Count - 1], 0);
            grid.Children.Add(BuildScorerColumn(awayName, awayScorers, HorizontalAlignment.Right));
            Grid.SetColumn(grid.Children[grid.Children.Count - 1], 1);
            _body.Children.Add(grid);
        }

        private static FrameworkElement BuildScorerColumn(string team, List<(string Name, string Minute)> scorers, HorizontalAlignment align)
        {
            var col = new StackPanel { HorizontalAlignment = align };
            col.Children.Add(new TextBlock
            {
                Text = team, FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = align, Margin = new Thickness(0, 0, 0, 2)
            });
            foreach (var grp in scorers.GroupBy(s => s.Name))
            {
                col.Children.Add(new TextBlock
                {
                    Text = grp.Key + "  " + string.Join(", ", grp.Select(s => s.Minute).Where(x => x != "")),
                    FontSize = 11, Foreground = Brushes.White,
                    HorizontalAlignment = align, TextWrapping = TextWrapping.Wrap
                });
            }
            return col;
        }

        private static FrameworkElement BuildEventRow(EspnService.EspnKeyEvent ev)
        {
            string icon = ev.Kind switch
            {
                "goal" => "⚽",
                "penalty" => "⚽",
                "red" => "🟥",
                "yellow" => "🟨",
                "sub" => "🔄",
                _ => "⏱"
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(ev.Clock) ? "" : ev.Clock,
                FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                Width = 38, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 4, 0)
            });
            row.Children.Add(new TextBlock
            {
                Text = icon, FontSize = 11, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 6, 0)
            });
            row.Children.Add(new TextBlock
            {
                Text = ev.Text, FontSize = 11, Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 250
            });
            return row;
        }
    }
}
