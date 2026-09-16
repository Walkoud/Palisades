using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Palisades.Services
{
    public enum EspnApiError
    {
        None,
        Offline,
        Unknown
    }

    public sealed class EspnApiException : Exception
    {
        public EspnApiError Kind { get; }
        public EspnApiException(EspnApiError kind, string message) : base(message)
        {
            Kind = kind;
        }
    }

    public sealed class EspnTeam
    {
        public string Id = "";
        public string Name = "";
        public string Abbr = "";
        public string Logo = "";
    }

    public sealed class EspnMatch
    {
        public string Id = "";
        public string LeagueSlug = "";
        public string LeagueName = "";
        public string LeagueLogo = "";
        public DateTime UtcDate;
        public string State = "";
        public string Clock = "";
        public string Detail = "";
        public EspnTeam Home = new EspnTeam();
        public EspnTeam Away = new EspnTeam();
        public int? HomeScore;
        public int? AwayScore;

        public bool IsLive => State == "in";
        public bool IsFinished => State == "post";
        public bool IsUpcoming => State == "pre";
    }

    public sealed class EspnLeague
    {
        public string Slug = "";
        public string Name = "";
    }

    public sealed class EspnRosterTeam
    {
        public string Id = "";
        public string Name = "";
        public string Abbr = "";
        public string Logo = "";
        public string LeagueSlug = "";
    }

    /// <summary>
    /// ESPN scoreboard client via the CDN host (cdn.espn.com), which — unlike
    /// site.api.espn.com — does not fingerprint-block .NET HTTP clients (Akamai 403).
    /// No key, 400ms spacing between calls (ESPN tolerates rapid fire; 429 backed off),
    /// plus a short per-league match cache and a 30-day world team directory on disk.
    /// </summary>
    public static class EspnService
    {
        public static readonly List<EspnLeague> CuratedLeagues = new List<EspnLeague>
        {
            new EspnLeague { Slug = "eng.1", Name = "Premier League" },
            new EspnLeague { Slug = "eng.2", Name = "Championship" },
            new EspnLeague { Slug = "esp.1", Name = "La Liga" },
            new EspnLeague { Slug = "esp.2", Name = "Segunda División" },
            new EspnLeague { Slug = "ita.1", Name = "Serie A" },
            new EspnLeague { Slug = "ita.2", Name = "Serie B" },
            new EspnLeague { Slug = "ger.1", Name = "Bundesliga" },
            new EspnLeague { Slug = "ger.2", Name = "2. Bundesliga" },
            new EspnLeague { Slug = "fra.1", Name = "Ligue 1" },
            new EspnLeague { Slug = "ned.1", Name = "Eredivisie" },
            new EspnLeague { Slug = "por.1", Name = "Liga Portugal" },
            new EspnLeague { Slug = "sco.1", Name = "Premiership (SCO)" },
            new EspnLeague { Slug = "bel.1", Name = "Pro League (BEL)" },
            new EspnLeague { Slug = "aut.1", Name = "Bundesliga (AUT)" },
            new EspnLeague { Slug = "den.1", Name = "Superliga (DEN)" },
            new EspnLeague { Slug = "nor.1", Name = "Eliteserien" },
            new EspnLeague { Slug = "swe.1", Name = "Allsvenskan" },
            new EspnLeague { Slug = "tur.1", Name = "Süper Lig" },
            new EspnLeague { Slug = "usa.1", Name = "MLS" },
            new EspnLeague { Slug = "mex.1", Name = "Liga MX" },
            new EspnLeague { Slug = "bra.1", Name = "Serie A (BRA)" },
            new EspnLeague { Slug = "arg.1", Name = "Primera División (ARG)" },
            new EspnLeague { Slug = "chn.1", Name = "Super League (CHN)" },
            new EspnLeague { Slug = "jpn.1", Name = "J1 League" },
            new EspnLeague { Slug = "aus.1", Name = "A-League" },
            new EspnLeague { Slug = "uefa.champions", Name = "Champions League" },
            new EspnLeague { Slug = "uefa.europa", Name = "Europa League" },
            new EspnLeague { Slug = "uefa.europa.conf", Name = "Conference League" },
            new EspnLeague { Slug = "uefa.nations", Name = "Nations League" },
            new EspnLeague { Slug = "fifa.world", Name = "World Cup" },
            new EspnLeague { Slug = "fifa.worldq.conmebol", Name = "World Cup Qual. (CONMEBOL)" },
            new EspnLeague { Slug = "conmebol.america", Name = "Copa América" },
            new EspnLeague { Slug = "fifa.friendly", Name = "Friendlies" },
            new EspnLeague { Slug = "uefa.euro", Name = "Euro" },
            new EspnLeague { Slug = "fifa.cwc", Name = "Club World Cup" },
            new EspnLeague { Slug = "eng.fa", Name = "FA Cup" },
            new EspnLeague { Slug = "esp.copa_del_rey", Name = "Copa del Rey" },
            new EspnLeague { Slug = "ita.coppa_italia", Name = "Coppa Italia" }
        };

        private static readonly HttpClient _http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Palisades/1.0");
            return c;
        }

        private static readonly object _rateLock = new object();
        private static DateTime _lastCallUtc = DateTime.MinValue;
        private static readonly TimeSpan MinCallGap = TimeSpan.FromMilliseconds(150);

        private static readonly object _cacheLock = new object();
        private static readonly Dictionary<string, (DateTime At, List<EspnMatch> Matches)> _cache
            = new Dictionary<string, (DateTime, List<EspnMatch>)>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan MatchesCacheTime = TimeSpan.FromSeconds(90);

        private static readonly object _rosterLock = new object();
        private static readonly Dictionary<string, (DateTime At, List<EspnRosterTeam> Teams)> _rosters
            = new Dictionary<string, (DateTime, List<EspnRosterTeam>)>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _rosterFetching = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan RosterCacheTime = TimeSpan.FromDays(30);

        private static string RosterCachePath =>
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Palisades", "football_rosters.json");

        /// <summary>Cached full roster for a league (memory, then disk). Empty if never fetched.</summary>
        public static List<EspnRosterTeam> GetCachedRoster(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug)) return new List<EspnRosterTeam>();
            lock (_rosterLock)
            {
                if (_rosters.TryGetValue(slug, out var entry) && DateTime.UtcNow - entry.At < RosterCacheTime)
                    return entry.Teams.Select(t => new EspnRosterTeam { Id = t.Id, Name = t.Name, Abbr = t.Abbr, Logo = t.Logo }).ToList();
            }
            // Fall back to disk cache (warms memory too).
            try
            {
                if (File.Exists(RosterCachePath))
                {
                    var json = JObject.Parse(File.ReadAllText(RosterCachePath));
                    var node = json[slug];
                    if (node != null && DateTime.TryParse(node["fetchedAt"]?.ToString(), out var at)
                        && DateTime.UtcNow - at.ToUniversalTime() < RosterCacheTime)
                    {
                        var teams = new List<EspnRosterTeam>();
                        foreach (var t in node["teams"] ?? new JArray())
                        {
                            teams.Add(new EspnRosterTeam
                            {
                                Id = t["id"]?.ToString() ?? "",
                                Name = t["name"]?.ToString() ?? "",
                                Abbr = t["abbr"]?.ToString() ?? "",
                                Logo = t["logo"]?.ToString() ?? ""
                            });
                        }
                        teams.RemoveAll(t => string.IsNullOrEmpty(t.Id));
                        lock (_rosterLock) { _rosters[slug] = (DateTime.UtcNow, teams); }
                        return teams;
                    }
                }
            }
            catch { }
            return new List<EspnRosterTeam>();
        }

        private static readonly object _diskLock = new object();

        private static void SaveRoster(string slug, List<EspnRosterTeam> teams)
        {
            lock (_diskLock)
            {
            try
            {
                JObject root;
                if (File.Exists(RosterCachePath))
                    root = JObject.Parse(File.ReadAllText(RosterCachePath));
                else
                    root = new JObject();
                var arr = new JArray();
                foreach (var t in teams)
                {
                    arr.Add(new JObject
                    {
                        ["id"] = t.Id,
                        ["name"] = t.Name,
                        ["abbr"] = t.Abbr,
                        ["logo"] = t.Logo
                    });
                }
                root[slug] = new JObject
                {
                    ["fetchedAt"] = DateTime.UtcNow.ToString("o"),
                    ["teams"] = arr
                };
                string? dir = System.IO.Path.GetDirectoryName(RosterCachePath);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                File.WriteAllText(RosterCachePath, root.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch { }
            }
        }

        /// <summary>World directory progress: leagues resolved / total. -1 = idle.</summary>
        public static int RosterDoneCount { get; private set; }
        public static int RosterTotalCount { get; private set; }
        public static event EventHandler? RostersChanged;

        private static void RaiseRostersChanged()
        {
            try { RostersChanged?.Invoke(null, EventArgs.Empty); } catch { }
        }

        /// <summary>Warms every memory roster from the disk cache (once per run).</summary>
        private static bool _rostersWarmed;
        private static void WarmRostersFromDisk()
        {
            lock (_rosterLock)
            {
                if (_rostersWarmed) return;
                _rostersWarmed = true;
            }
            try
            {
                if (!File.Exists(RosterCachePath)) return;
                var json = JObject.Parse(File.ReadAllText(RosterCachePath));
                foreach (var prop in json.Properties())
                {
                    try
                    {
                        var node = prop.Value as JObject;
                        if (node == null) continue;
                        if (!DateTime.TryParse(node["fetchedAt"]?.ToString(), out var at)
                            || DateTime.UtcNow - at.ToUniversalTime() >= RosterCacheTime)
                            continue;
                        var teams = new List<EspnRosterTeam>();
                        foreach (var t in node["teams"] ?? new JArray())
                        {
                            var rt = new EspnRosterTeam
                            {
                                Id = t["id"]?.ToString() ?? "",
                                Name = t["name"]?.ToString() ?? "",
                                Abbr = t["abbr"]?.ToString() ?? "",
                                Logo = t["logo"]?.ToString() ?? "",
                                LeagueSlug = prop.Name
                            };
                            if (!string.IsNullOrEmpty(rt.Id) && !string.IsNullOrEmpty(rt.Name))
                                teams.Add(rt);
                        }
                        if (teams.Count > 0)
                            lock (_rosterLock) { _rosters[prop.Name] = (DateTime.UtcNow, teams); }
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>Every cached team worldwide, tagged with its league. Empty if never fetched.</summary>
        public static List<EspnRosterTeam> GetDirectory()
        {
            WarmRostersFromDisk();
            var out_ = new List<EspnRosterTeam>();
            lock (_rosterLock)
            {
                foreach (var kv in _rosters)
                {
                    if (DateTime.UtcNow - kv.Value.At >= RosterCacheTime) continue;
                    foreach (var t in kv.Value.Teams)
                    {
                        out_.Add(new EspnRosterTeam
                        {
                            Id = t.Id, Name = t.Name, Abbr = t.Abbr, Logo = t.Logo,
                            LeagueSlug = kv.Key
                        });
                    }
                }
            }
            return out_;
        }

        /// <summary>
        /// Ensures the WORLD directory in background (fire-and-forget, guarded).
        /// No slugs = every league ESPN knows (enumerated live, cached 30d).
        /// Priority slugs are fetched first so subscribed leagues resolve fast.
        /// </summary>
        public static void EnsureWorldRostersAsync(IEnumerable<string>? slugs = null, IEnumerable<string>? priority = null)
        {
            WarmRostersFromDisk();
            if (slugs == null)
            {
                lock (_rosterLock)
                {
                    if (_worldEnumRunning) return;
                    _worldEnumRunning = true;
                }
                var prio = priority?.Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    ?? new List<string>();
                Palisades.App.Log("[Football] world directory: enumerating leagues");
                _ = FetchWorldAllAsync(prio);
                return;
            }
            var wanted = slugs.Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var missing = new List<string>();
            lock (_rosterLock)
            {
                foreach (var s in wanted)
                {
                    if (_rosters.TryGetValue(s, out var entry) && DateTime.UtcNow - entry.At < RosterCacheTime)
                        continue;
                    if (_rosterFetching.Add(s))
                        missing.Add(s);
                }
            }
            if (missing.Count == 0) return;
            RosterTotalCount = missing.Count;
            RosterDoneCount = 0;
            Palisades.App.Log("[Football] world directory: fetching " + missing.Count + " leagues");
            _ = FetchWorldRostersAsync(missing);
        }

        private static bool _worldEnumRunning;

        private static string LeagueListCachePath =>
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Palisades", "football_leagues.json");

        private static async Task FetchWorldAllAsync(List<string> priority)
        {
            try
            {
                var slugs = await GetAllLeagueSlugsAsync().ConfigureAwait(false);
                Palisades.App.Log("[Football] world directory: " + slugs.Count + " leagues known");
                var missing = new List<string>();
                var missingPrio = new List<string>();
                lock (_rosterLock)
                {
                    var prioSet = new HashSet<string>(priority, StringComparer.OrdinalIgnoreCase);
                    foreach (var s in slugs)
                    {
                        if (_rosterDead.Contains(s)) continue;
                        if (_rosters.TryGetValue(s, out var entry) && DateTime.UtcNow - entry.At < RosterCacheTime)
                            continue;
                        if (!_rosterFetching.Add(s))
                            continue;
                        if (prioSet.Contains(s)) missingPrio.Add(s);
                        else missing.Add(s);
                    }
                }
                // Subscribed leagues first (wave 1), world after (wave 2).
                if (missingPrio.Count + missing.Count == 0)
                {
                    Palisades.App.Log("[Football] world directory: all cached (" + GetDirectory().Count + " teams)");
                    return;
                }
                if (missingPrio.Count > 0)
                {
                    RosterTotalCount = missingPrio.Count + missing.Count;
                    RosterDoneCount = 0;
                    Palisades.App.Log("[Football] world directory: priority " + missingPrio.Count + " leagues first");
                    await FetchWorldRostersAsync(missingPrio, false).ConfigureAwait(false);
                }
                if (missing.Count == 0) return;
                RosterTotalCount = RosterDoneCount + missing.Count;
                Palisades.App.Log("[Football] world directory: fetching " + missing.Count + " leagues");
                await FetchWorldRostersAsync(missing).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Palisades.App.Log("[Football] world enum FAILED: " + ex.Message);
            }
            finally
            {
                lock (_rosterLock) { _worldEnumRunning = false; }
            }
        }

        /// <summary>Every league slug ESPN exposes (live enumeration, cached 30d).</summary>
        public static async Task<List<string>> GetAllLeagueSlugsAsync(CancellationToken ct = default)
        {
            try
            {
                if (File.Exists(LeagueListCachePath))
                {
                    var cached = JObject.Parse(File.ReadAllText(LeagueListCachePath));
                    if (DateTime.TryParse(cached["fetchedAt"]?.ToString(), out var at)
                        && DateTime.UtcNow - at.ToUniversalTime() < RosterCacheTime)
                    {
                        var list = new List<string>();
                        foreach (var s in cached["slugs"] ?? new JArray())
                        {
                            string slug = s?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(slug)) list.Add(slug);
                        }
                        if (list.Count > 0) return list;
                    }
                }
            }
            catch { }
            var slugs = new List<string>();
            string? next = "https://sports.core.api.espn.com/v2/sports/soccer/leagues?limit=200&lang=en&region=us";
            int pages = 0;
            while (!string.IsNullOrEmpty(next) && pages < 10)
            {
                pages++;
                string body = await GetRawAsync(next, ct).ConfigureAwait(false);
                var json = JObject.Parse(body);
                foreach (var it in json["items"] ?? new JArray())
                {
                    string r = it["$ref"]?.ToString() ?? "";
                    // .../leagues/{slug}?lang=.. → slug
                    try
                    {
                        var uri = new Uri(FixRefUrl(r));
                        string seg = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? "";
                        if (!string.IsNullOrEmpty(seg)) slugs.Add(seg);
                    }
                    catch { }
                }
                next = null;
                if ((json["pageCount"]?.ToObject<int>() ?? 1) > (json["pageIndex"]?.ToObject<int>() ?? 1))
                    next = "https://sports.core.api.espn.com/v2/sports/soccer/leagues?limit=200&lang=en&region=us&page=" + ((json["pageIndex"]?.ToObject<int>() ?? 1) + 1);
            }
            slugs = slugs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (slugs.Count > 0)
            {
                try
                {
                    var root = new JObject
                    {
                        ["fetchedAt"] = DateTime.UtcNow.ToString("o"),
                        ["slugs"] = new JArray(slugs)
                    };
                    string? dir = System.IO.Path.GetDirectoryName(LeagueListCachePath);
                    if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                    File.WriteAllText(LeagueListCachePath, root.ToString(Newtonsoft.Json.Formatting.None));
                }
                catch { }
            }
            return slugs;
        }

        /// <summary>Ensures a single league roster in background (fire-and-forget, guarded).</summary>
        public static void EnsureRosterAsync(string slug)
        {
            if (!string.IsNullOrWhiteSpace(slug))
                EnsureWorldRostersAsync(new[] { slug });
        }

        private static readonly SemaphoreSlim _rosterParallel = new SemaphoreSlim(8);
        private static readonly HashSet<string> _rosterDead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly object _leagueLogoLock = new object();
        private static readonly Dictionary<string, (DateTime At, string Logo)> _leagueLogos
            = new Dictionary<string, (DateTime, string)>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _leagueLogoFetching = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string LeagueLogoCachePath =>
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Palisades", "football_league_logos.json");

        /// <summary>Cached league logo URL (memory, then disk). Empty if unknown.</summary>
        public static string GetCachedLeagueLogo(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug)) return "";
            try
            {
                if (File.Exists(LeagueLogoCachePath))
                {
                    var json = JObject.Parse(File.ReadAllText(LeagueLogoCachePath));
                    // v2: prefers the white "dark" variant (visible on dark widgets).
                    if ((json["v"]?.ToString() ?? "") != "2")
                    {
                        lock (_leagueLogoLock) { _leagueLogos.Remove(slug); }
                        return "";
                    }
                    var node = json[slug];
                    if (node != null && DateTime.TryParse(node["fetchedAt"]?.ToString(), out var at)
                        && DateTime.UtcNow - at.ToUniversalTime() < RosterCacheTime)
                    {
                        string logo = node["logo"]?.ToString() ?? "";
                        lock (_leagueLogoLock) { _leagueLogos[slug] = (DateTime.UtcNow, logo); }
                        return logo;
                    }
                }
            }
            catch { }
            lock (_leagueLogoLock)
            {
                if (_leagueLogos.TryGetValue(slug, out var entry) && DateTime.UtcNow - entry.At < RosterCacheTime)
                    return entry.Logo;
            }
            return "";
        }

        private static void SaveLeagueLogo(string slug, string logo)
        {
            lock (_diskLock)
            {
                try
                {
                    JObject root;
                    if (File.Exists(LeagueLogoCachePath))
                        root = JObject.Parse(File.ReadAllText(LeagueLogoCachePath));
                    else
                        root = new JObject();
                    root["v"] = "2";
                    root[slug] = new JObject
                    {
                        ["fetchedAt"] = DateTime.UtcNow.ToString("o"),
                        ["logo"] = logo
                    };
                    string? dir = System.IO.Path.GetDirectoryName(LeagueLogoCachePath);
                    if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                    File.WriteAllText(LeagueLogoCachePath, root.ToString(Newtonsoft.Json.Formatting.None));
                }
                catch { }
            }
        }

        /// <summary>Fetches + caches a league logo (guarded, silent).</summary>
        public static async Task EnsureLeagueLogoAsync(string slug, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(slug)) return;
            lock (_leagueLogoLock)
            {
                if (_leagueLogos.TryGetValue(slug, out var entry) && DateTime.UtcNow - entry.At < RosterCacheTime)
                    return;
                if (!_leagueLogoFetching.Add(slug)) return;
            }
            if (!string.IsNullOrEmpty(GetCachedLeagueLogo(slug)))
            {
                lock (_leagueLogoLock) { _leagueLogoFetching.Remove(slug); }
                return;
            }
            try
            {
                string url = "https://sports.core.api.espn.com/v2/sports/soccer/leagues/" + Uri.EscapeDataString(slug.Trim()) + "?lang=en&region=us";
                string body = await GetRawAsync(url, ct).ConfigureAwait(false);
                var json = JObject.Parse(body);
                // Prefer the white "dark" variant (visible on dark widgets), else default.
                string logo = "", fallback = "";
                foreach (var l in json["logos"] ?? new JArray())
                {
                    string href = l["href"]?.ToString() ?? "";
                    if (!href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
                    string rel = (l["rel"]?.ToString() ?? "").ToLowerInvariant();
                    if (rel.Contains("dark")) { logo = href; break; }
                    if (string.IsNullOrEmpty(fallback)) fallback = href;
                }
                if (string.IsNullOrEmpty(logo)) logo = fallback;
                lock (_leagueLogoLock) { _leagueLogos[slug] = (DateTime.UtcNow, logo); }
                if (!string.IsNullOrEmpty(logo)) SaveLeagueLogo(slug, logo);
            }
            catch { }
            finally
            {
                lock (_leagueLogoLock) { _leagueLogoFetching.Remove(slug); }
            }
        }

        private static async Task FetchWorldRostersAsync(List<string> slugs, bool final = true)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(60));
                var ct = cts.Token;
                var tasks = slugs.Select(s => FetchOneRosterAsync(s, ct)).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(false);
                if (final)
                    Palisades.App.Log("[Football] world directory done: " + GetDirectory().Count + " teams");
            }
            catch (Exception ex)
            {
                Palisades.App.Log("[Football] world directory FAILED: " + ex.Message);
            }
            finally
            {
                if (final)
                    RosterDoneCount = RosterTotalCount;
                RaiseRostersChanged();
            }
        }

        private static async Task FetchOneRosterAsync(string slug, CancellationToken ct)
        {
            try
            {
                // League logo rides along (1 extra call, cached 30d).
                await EnsureLeagueLogoAsync(slug, ct).ConfigureAwait(false);
                string leagueUrl = "https://sports.core.api.espn.com/v2/sports/soccer/leagues/" + Uri.EscapeDataString(slug) + "?lang=en&region=us";
                string teamsUrl = await GetRefAsync(leagueUrl, new[] { "teams" }, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(teamsUrl))
                {
                    Palisades.App.Log("[Football] roster " + slug + ": no teams $ref, skipping");
                    lock (_rosterLock) { _rosterDead.Add(slug); }
                    return;
                }
                var refs = await GetRefListAsync(teamsUrl, ct).ConfigureAwait(false);
                var teamTasks = refs.Select(r => ResolveRosterTeamAsync(r, slug, ct)).ToArray();
                var resolved = await Task.WhenAll(teamTasks).ConfigureAwait(false);
                var teams = resolved.Where(t => t != null).Select(t => t!).ToList();
                if (teams.Count > 0)
                {
                    lock (_rosterLock) { _rosters[slug] = (DateTime.UtcNow, teams); }
                    SaveRoster(slug, teams);
                    Palisades.App.Log("[Football] roster " + slug + ": cached " + teams.Count + " teams");
                }
                else
                {
                    Palisades.App.Log("[Football] roster " + slug + ": 0 teams resolved");
                }
            }
            catch (Exception ex)
            {
                Palisades.App.Log("[Football] roster " + slug + " FAILED: " + ex.Message);
            }
            finally
            {
                lock (_rosterLock) { _rosterFetching.Remove(slug); }
                RosterDoneCount++;
                RaiseRostersChanged();
            }
        }

        private static async Task<EspnRosterTeam?> ResolveRosterTeamAsync(string teamUrl, string slug, CancellationToken ct)
        {
            await _rosterParallel.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string body = await GetRawAsync(FixRefUrl(teamUrl), ct).ConfigureAwait(false);
                var json = JObject.Parse(body);
                var t = new EspnRosterTeam
                {
                    Id = json["id"]?.ToString() ?? "",
                    Name = json["displayName"]?.ToString() ?? json["name"]?.ToString() ?? "",
                    Abbr = (json["abbreviation"]?.ToString() ?? "").ToUpperInvariant(),
                    LeagueSlug = slug
                };
                var logos = json["logos"] as JArray;
                if (logos != null)
                {
                    foreach (var l in logos)
                    {
                        string href = l["href"]?.ToString() ?? "";
                        if (href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { t.Logo = href; break; }
                    }
                }
                if (string.IsNullOrEmpty(t.Id) || string.IsNullOrEmpty(t.Name))
                    return null;
                return t;
            }
            catch { return null; }
            finally { _rosterParallel.Release(); }
        }

        private static string FixRefUrl(string url)
        {
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return "https://" + url.Substring("http://".Length);
            return url;
        }

        private static async Task<string> GetRefAsync(string url, string[] path, CancellationToken ct)
        {
            string body = await GetRawAsync(url, ct).ConfigureAwait(false);
            var node = JObject.Parse(body);
            foreach (var key in path)
            {
                node = node[key] as JObject;
                if (node == null) return "";
            }
            return node["$ref"]?.ToString() ?? "";
        }

        private static async Task<List<string>> GetRefListAsync(string collectionUrl, CancellationToken ct)
        {
            var refs = new List<string>();
            string sep0 = collectionUrl.Contains("?") ? "&" : "?";
            string? next = collectionUrl + sep0 + "limit=100";
            int pages = 0;
            while (!string.IsNullOrEmpty(next) && pages < 10)
            {
                pages++;
                string body = await GetRawAsync(next, ct).ConfigureAwait(false);
                var json = JObject.Parse(body);
                foreach (var it in json["items"] ?? new JArray())
                {
                    string r = it["$ref"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(r)) refs.Add(r);
                }
                // Follow pagination if present (count/pageIndex/pageCount style).
                next = null;
                if ((json["pageCount"]?.ToObject<int>() ?? 1) > (json["pageIndex"]?.ToObject<int>() ?? 1))
                {
                    string sep = collectionUrl.Contains("?") ? "&" : "?";
                    next = collectionUrl + sep + "limit=100&page=" + ((json["pageIndex"]?.ToObject<int>() ?? 1) + 1);
                }
                if (refs.Count >= 500) break;
            }
            return refs;
        }

        private static async Task<string> GetRawAsync(string url, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                TimeSpan wait;
                lock (_rateLock)
                {
                    wait = MinCallGap - (DateTime.UtcNow - _lastCallUtc);
                    if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                    _lastCallUtc = DateTime.UtcNow + wait;
                }
                if (wait > TimeSpan.Zero)
                {
                    try { await Task.Delay(wait, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                }
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode == 429 && attempt < 3)
                {
                    TimeSpan backoff = TimeSpan.FromSeconds(8);
                    try
                    {
                        var retryAfter = resp.Headers.RetryAfter?.Delta;
                        if (retryAfter.HasValue && retryAfter.Value < TimeSpan.FromMinutes(2))
                            backoff = retryAfter.Value;
                    }
                    catch { }
                    Palisades.App.Log("[Football] 429, backing off " + (int)backoff.TotalSeconds + "s");
                    try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
        }

        public static async Task<List<EspnMatch>> GetMatchesAsync(string leagueSlug, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(leagueSlug))
                return new List<EspnMatch>();

            lock (_cacheLock)
            {
                if (_cache.TryGetValue(leagueSlug, out var entry) && DateTime.UtcNow - entry.At < MatchesCacheTime)
                {
                    var cached = entry.Matches.Select(CloneMatch).ToList();
                    FillLeagueLogos(cached, leagueSlug);
                    return cached;
                }
            }

            TimeSpan wait;
            lock (_rateLock)
            {
                wait = MinCallGap - (DateTime.UtcNow - _lastCallUtc);
                if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                _lastCallUtc = DateTime.UtcNow + wait;
            }
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }

            string url = "https://cdn.espn.com/core/soccer/scoreboard?league=" + Uri.EscapeDataString(leagueSlug.Trim()) + "&xhr=1";
            string body;
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return new List<EspnMatch>();
                resp.EnsureSuccessStatusCode();
                body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new EspnApiException(EspnApiError.Offline, "Network error: " + ex.Message);
            }

            List<EspnMatch> matches;
            try
            {
                matches = ParseScoreboard(body, leagueSlug);
            }
            catch (Exception ex)
            {
                throw new EspnApiException(EspnApiError.Unknown, "Bad ESPN payload: " + ex.Message);
            }

            lock (_cacheLock)
            {
                if (_cache.Count > 40) _cache.Clear();
                _cache[leagueSlug] = (DateTime.UtcNow, matches.Select(CloneMatch).ToList());
            }
            FillLeagueLogos(matches, leagueSlug);
            return matches;
        }

        private static void FillLeagueLogos(List<EspnMatch> matches, string leagueSlug)
        {
            try
            {
                string logo = GetCachedLeagueLogo(leagueSlug);
                if (string.IsNullOrEmpty(logo))
                {
                    _ = EnsureLeagueLogoAsync(leagueSlug);
                    return;
                }
                foreach (var m in matches)
                    if (string.IsNullOrEmpty(m.LeagueLogo))
                        m.LeagueLogo = logo;
            }
            catch { }
        }

        /// <summary>
        /// Normalizes an ESPN date to UTC. Newtonsoft already converts zoned
        /// strings ("Z", "+02:00") to machine-local on parse — convert back to
        /// UTC to keep the instant. Zonenless strings are ESPN-UTC.
        /// </summary>
        private static DateTime ToUtcDate(JToken? t)
        {
            DateTime dt;
            try { dt = t?.ToObject<DateTime>() ?? DateTime.MinValue; } catch { return DateTime.MinValue; }
            if (dt == DateTime.MinValue) return dt;
            if (dt.Kind == DateTimeKind.Local) return dt.ToUniversalTime();
            if (dt.Kind == DateTimeKind.Unspecified) return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            return dt;
        }

        private static EspnMatch CloneMatch(EspnMatch m)
        {
            return new EspnMatch
            {
                Id = m.Id,
                LeagueSlug = m.LeagueSlug,
                LeagueName = m.LeagueName,
                LeagueLogo = m.LeagueLogo,
                UtcDate = m.UtcDate,
                State = m.State,
                Clock = m.Clock,
                Detail = m.Detail,
                Home = new EspnTeam { Id = m.Home.Id, Name = m.Home.Name, Abbr = m.Home.Abbr, Logo = m.Home.Logo },
                Away = new EspnTeam { Id = m.Away.Id, Name = m.Away.Name, Abbr = m.Away.Abbr, Logo = m.Away.Logo },
                HomeScore = m.HomeScore,
                AwayScore = m.AwayScore
            };
        }

        private static List<EspnMatch> ParseScoreboard(string body, string leagueSlug)
        {
            var list = new List<EspnMatch>();
            var json = JObject.Parse(body);
            // CDN shape: content.sbData.events (site.api shape nested one level deeper).
            var root = json["content"]?["sbData"] ?? json;
            // League display name comes from our curated list (CDN only carries calendar data).
            string leagueName = CuratedLeagues
                .FirstOrDefault(l => l.Slug.Equals(leagueSlug, StringComparison.OrdinalIgnoreCase))?.Name
                ?? leagueSlug;
            foreach (var e in root["events"] ?? new JArray())
            {
                var comp = e["competitions"]?.FirstOrDefault();
                if (comp == null) continue;
                var m = new EspnMatch
                {
                    Id = e["id"]?.ToString() ?? Guid.NewGuid().ToString("N"),
                    LeagueSlug = leagueSlug,
                    LeagueName = leagueName
                };
                try { m.UtcDate = ToUtcDate(comp["date"]); } catch { m.UtcDate = DateTime.MinValue; }
                m.State = (comp["status"]?["type"]?["state"]?.ToString() ?? "").ToLowerInvariant();
                m.Clock = comp["status"]?["displayClock"]?.ToString() ?? "";
                m.Detail = comp["status"]?["type"]?["shortDetail"]?.ToString()
                    ?? comp["status"]?["type"]?["detail"]?.ToString() ?? "";
                var teams = new List<JToken>();
                foreach (var t in comp["competitors"] ?? new JArray())
                    teams.Add(t);
                var home = teams.FirstOrDefault(t => (t["homeAway"]?.ToString() ?? "") == "home") ?? teams.FirstOrDefault();
                var away = teams.FirstOrDefault(t => (t["homeAway"]?.ToString() ?? "") == "away") ?? teams.Skip(1).FirstOrDefault();
                m.Home = ParseTeam(home);
                m.Away = ParseTeam(away);
                m.HomeScore = ParseScore(home?["score"]);
                m.AwayScore = ParseScore(away?["score"]);
                list.Add(m);
            }
            return list;
        }

        private static EspnTeam ParseTeam(JToken? t)
        {
            var team = new EspnTeam();
            if (t == null) return team;
            var info = t["team"] ?? t;
            team.Id = info["id"]?.ToString() ?? "";
            team.Name = info["displayName"]?.ToString() ?? info["name"]?.ToString() ?? "";
            team.Abbr = (info["abbreviation"]?.ToString() ?? "").ToUpperInvariant();
            team.Logo = info["logo"]?.ToString() ?? "";
            return team;
        }

        private static int? ParseScore(JToken? t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Object)
            {
                string dv = t["displayValue"]?.ToString()?.Trim() ?? "";
                if (int.TryParse(dv, out int vo)) return vo;
                return null;
            }
            string s = t.ToString().Trim();
            if (int.TryParse(s, out int v)) return v;
            return null;
        }

        private static readonly object _schedLock = new object();
        private static readonly Dictionary<string, (DateTime At, List<EspnMatch> Matches)> _schedCache
            = new Dictionary<string, (DateTime, List<EspnMatch>)>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan SchedCacheTime = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Team schedule via site.web.api (NOT Akamai-blocked, unlike site.api):
        /// covers finished matches the CDN scoreboard window already rotated out.
        /// </summary>
        public static async Task<List<EspnMatch>> GetTeamScheduleAsync(string leagueSlug, string teamId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(leagueSlug) || string.IsNullOrWhiteSpace(teamId))
                return new List<EspnMatch>();
            string key = leagueSlug.Trim().ToLowerInvariant() + "/" + teamId.Trim();
            lock (_schedLock)
            {
                if (_schedCache.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.At < SchedCacheTime)
                {
                    var cached = entry.Matches.Select(CloneMatch).ToList();
                    FillLeagueLogos(cached, leagueSlug);
                    return cached;
                }
            }
            string url = "https://site.web.api.espn.com/apis/site/v2/sports/soccer/"
                + Uri.EscapeDataString(leagueSlug.Trim()) + "/teams/" + Uri.EscapeDataString(teamId.Trim()) + "/schedule";
            string body;
            try
            {
                body = await GetRawAsync(url, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Palisades.App.Log("[Football] schedule " + leagueSlug + "/" + teamId + " FETCH FAIL: " + ex.Message);
                return new List<EspnMatch>();
            }
            List<EspnMatch> matches;
            try
            {
                matches = ParseSchedule(body, leagueSlug);
            }
            catch (Exception ex)
            {
                Palisades.App.Log("[Football] schedule " + leagueSlug + "/" + teamId + " PARSE FAIL: " + ex.Message);
                return new List<EspnMatch>();
            }
            // Schedules carry no crests: backfill from the world directory.
            try
            {
                var logos = new Dictionary<string, string>();
                foreach (var t in GetDirectory())
                    if (!string.IsNullOrEmpty(t.Id) && !string.IsNullOrEmpty(t.Logo) && !logos.ContainsKey(t.Id))
                        logos[t.Id] = t.Logo;
                foreach (var m in matches)
                {
                    if (string.IsNullOrEmpty(m.Home.Logo) && logos.TryGetValue(m.Home.Id, out var hl)) m.Home.Logo = hl;
                    if (string.IsNullOrEmpty(m.Away.Logo) && logos.TryGetValue(m.Away.Id, out var al)) m.Away.Logo = al;
                }
            }
            catch { }
            lock (_schedLock)
            {
                if (_schedCache.Count > 200) _schedCache.Clear();
                _schedCache[key] = (DateTime.UtcNow, matches.Select(CloneMatch).ToList());
            }
            FillLeagueLogos(matches, leagueSlug);
            return matches;
        }

        private static List<EspnMatch> ParseSchedule(string body, string leagueSlug)
        {
            var list = new List<EspnMatch>();
            var json = JObject.Parse(body);
            string leagueName = CuratedLeagues
                .FirstOrDefault(l => l.Slug.Equals(leagueSlug, StringComparison.OrdinalIgnoreCase))?.Name
                ?? leagueSlug;
            foreach (var e in json["events"] ?? new JArray())
            {
                var comp = e["competitions"]?.FirstOrDefault();
                if (comp == null) continue;
                var m = new EspnMatch
                {
                    Id = e["id"]?.ToString() ?? Guid.NewGuid().ToString("N"),
                    LeagueSlug = leagueSlug,
                    LeagueName = leagueName
                };
                try { m.UtcDate = ToUtcDate(e["date"] ?? comp["date"]); } catch { m.UtcDate = DateTime.MinValue; }
                m.State = (comp["status"]?["type"]?["state"]?.ToString() ?? "").ToLowerInvariant();
                m.Clock = comp["status"]?["displayClock"]?.ToString() ?? "";
                m.Detail = comp["status"]?["type"]?["shortDetail"]?.ToString()
                    ?? comp["status"]?["type"]?["detail"]?.ToString() ?? "";
                var teams = new List<JToken>();
                foreach (var t in comp["competitors"] ?? new JArray())
                    teams.Add(t);
                var home = teams.FirstOrDefault(t => (t["homeAway"]?.ToString() ?? "") == "home") ?? teams.FirstOrDefault();
                var away = teams.FirstOrDefault(t => (t["homeAway"]?.ToString() ?? "") == "away") ?? teams.Skip(1).FirstOrDefault();
                m.Home = ParseTeam(home);
                m.Away = ParseTeam(away);
                m.HomeScore = ParseScore(home?["score"]);
                m.AwayScore = ParseScore(away?["score"]);
                list.Add(m);
            }
            return list;
        }

        public sealed class EspnKeyEvent
        {
            public string Clock = "";
            public string Kind = ""; // goal, yellow, red, sub, whistle
            public string Text = "";
        }

        public sealed class EspnMatchDetail
        {
            public string HomeName = "";
            public string AwayName = "";
            public string Venue = "";
            public List<EspnKeyEvent> Events = new List<EspnKeyEvent>();
            public List<(string Name, string Home, string Away)> Stats = new List<(string, string, string)>();
        }

        private static readonly object _detailLock = new object();
        private static readonly Dictionary<string, (DateTime At, EspnMatchDetail Detail)> _detailCache
            = new Dictionary<string, (DateTime, EspnMatchDetail)>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan DetailCacheTime = TimeSpan.FromMinutes(30);

        /// <summary>Full match report: goals/cards/subs + team stats (site.web.api, cached 30min).</summary>
        public static async Task<EspnMatchDetail?> GetMatchSummaryAsync(string leagueSlug, string eventId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(leagueSlug) || string.IsNullOrWhiteSpace(eventId)) return null;
            if (!long.TryParse(eventId.Trim(), out _)) return null;
            string key = leagueSlug.Trim().ToLowerInvariant() + "/" + eventId.Trim();
            lock (_detailLock)
            {
                if (_detailCache.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.At < DetailCacheTime)
                    return entry.Detail;
            }
            string url = "https://site.web.api.espn.com/apis/site/v2/sports/soccer/"
                + Uri.EscapeDataString(leagueSlug.Trim()) + "/summary?event=" + Uri.EscapeDataString(eventId.Trim());
            EspnMatchDetail detail;
            try
            {
                string body = await GetRawAsync(url, ct).ConfigureAwait(false);
                detail = ParseSummary(body);
            }
            catch
            {
                return null;
            }
            lock (_detailLock)
            {
                if (_detailCache.Count > 100) _detailCache.Clear();
                _detailCache[key] = (DateTime.UtcNow, detail);
            }
            return detail;
        }

        private static EspnMatchDetail ParseSummary(string body)
        {
            var detail = new EspnMatchDetail();
            var json = JObject.Parse(body);
            var teams = new List<JToken>();
            foreach (var t in json["boxscore"]?["teams"] ?? new JArray())
                teams.Add(t);
            string homeId = "", awayId = "";
            try
            {
                var comp = json["header"]?["competitions"]?.FirstOrDefault();
                foreach (var c in comp?["competitors"] ?? new JArray())
                {
                    string ha = c["homeAway"]?.ToString() ?? "";
                    string id = c["id"]?.ToString() ?? c["team"]?["id"]?.ToString() ?? "";
                    if (ha == "home") homeId = id;
                    else if (ha == "away") awayId = id;
                }
            }
            catch { }
            var byId = new Dictionary<string, JToken>();
            foreach (var t in teams)
            {
                string id = t["team"]?["id"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(id) && !byId.ContainsKey(id)) byId[id] = t;
                if (string.IsNullOrEmpty(detail.HomeName) && id == homeId)
                    detail.HomeName = t["team"]?["displayName"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(detail.AwayName) && id == awayId)
                    detail.AwayName = t["team"]?["displayName"]?.ToString() ?? "";
            }
            if (string.IsNullOrEmpty(detail.HomeName) && teams.Count > 0)
                detail.HomeName = teams[0]["team"]?["displayName"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(detail.AwayName) && teams.Count > 1)
                detail.AwayName = teams[1]["team"]?["displayName"]?.ToString() ?? "";
            JToken? homeTeam = homeId != "" && byId.TryGetValue(homeId, out var ht) ? ht : teams.FirstOrDefault();
            JToken? awayTeam = awayId != "" && byId.TryGetValue(awayId, out var at) ? at : teams.Skip(1).FirstOrDefault();
            var homeStats = new Dictionary<string, string>();
            var awayStats = new Dictionary<string, string>();
            var order = new List<string>();
            foreach (var s in homeTeam?["statistics"] ?? new JArray())
            {
                string n = s["name"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(n) || homeStats.ContainsKey(n)) continue;
                homeStats[n] = s["displayValue"]?.ToString() ?? "-";
                order.Add(n);
            }
            foreach (var s in awayTeam?["statistics"] ?? new JArray())
            {
                string n = s["name"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(n) && !awayStats.ContainsKey(n))
                    awayStats[n] = s["displayValue"]?.ToString() ?? "-";
            }
            foreach (var n in order)
                detail.Stats.Add((DisplayStatName(n), homeStats[n], awayStats.TryGetValue(n, out var av) ? av : "-"));
            detail.Venue = json["gameInfo"]?["venue"]?["fullName"]?.ToString() ?? "";

            foreach (var ke in json["keyEvents"] ?? new JArray())
            {
                string type = ke["type"]?["text"]?.ToString() ?? "";
                string kind = ClassifyKeyEvent(type);
                if (kind == "") continue;
                detail.Events.Add(new EspnKeyEvent
                {
                    Clock = ke["clock"]?["displayValue"]?.ToString() ?? "",
                    Kind = kind,
                    Text = ke["text"]?.ToString() ?? ke["type"]?["text"]?.ToString() ?? ""
                });
            }
            return detail;
        }

        private static string ClassifyKeyEvent(string type)
        {
            string t = (type ?? "").ToLowerInvariant();
            if (t.Contains("goal") || t.Contains("penalty") && t.Contains("scor")) return "goal";
            if (t.Contains("penalty")) return "penalty";
            if (t.Contains("red")) return "red";
            if (t.Contains("yellow")) return "yellow";
            if (t.Contains("substitution")) return "sub";
            if (t.Contains("full") || (t.Contains("end") && t.Contains("time"))) return "whistle";
            return "";
        }

        private static string DisplayStatName(string name)
        {
            switch (name)
            {
                case "possessionPct": return "Possession %";
                case "totalShots": return "Shots";
                case "shotsOnTarget": return "On target";
                case "wonCorners": return "Corners";
                case "foulsCommitted": return "Fouls";
                case "yellowCards": return "Yellow";
                case "redCards": return "Red";
                case "offsides": return "Offsides";
                case "saves": return "Saves";
                case "shotPct": return "Shot %";
                case "penaltyKickGoals": return "Pen. goals";
                case "penaltyKickShots": return "Penalties";
                default: return name;
            }
        }
    }
}
