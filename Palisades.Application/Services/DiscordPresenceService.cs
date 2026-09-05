using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Palisades.Services
{
    /// <summary>
    /// Discord Rich Presence over the local IPC protocol (named pipes), no external
    /// dependency. Shows the current Now Playing track as "Listening to" status.
    /// Images (large/small keys) must be uploaded as art assets in the user's own
    /// Discord application (discord.com/developers) whose Client ID is configured.
    /// </summary>
    public sealed class DiscordPresenceService
    {
        public static DiscordPresenceService Instance { get; } = new DiscordPresenceService();

        private readonly object _lock = new object();
        private NamedPipeClientStream? _pipe;
        private Thread? _readerThread;
        private Timer? _retryTimer;
        private volatile bool _wantConnection;
        private volatile bool _connected;

        // Settings (via ApplySettings)
        private bool _enabled = true;
        private string _clientId = "";
        private bool _showWhenIdle;
        private bool _showWhenPaused = true;
        private bool _showTitle = true;
        private bool _showArtist = true;
        private bool _showApp = true;
        private bool _showElapsed = true;
        private bool _showCover = true;
        private string _largeImage = "palisades";
        private string _smallImage = "";
        private bool _privateMode;
        private bool _buttonsEnabled = true;
        private string _button1Label = "Palisades";
        private string _button1Url = "https://github.com/Walkoud/Palisades";
        private string _button2Label = "";
        private string _button2Url = "";
        private string _stateUrl = "https://github.com/Walkoud/Palisades";
        private string _detailsUrl = "";
        private string _largeUrl = "https://github.com/Walkoud/Palisades";
        private List<string> _priority = new List<string>();

        // One entry per known media app (fed by the Now Playing views, which see
        // every SMTC session — not just the followed one). Discord selection is
        // independent from the widget pin: highest-priority playing app wins.
        private sealed class AppReport
        {
            public string Title = "";
            public string Artist = "";
            public string App = "";
            public string AppId = "";
            public bool IsPlaying;
            public TimeSpan Position;
            public byte[]? CoverBytes;
            public DateTime SeenUtc = DateTime.MinValue;
        }
        private readonly Dictionary<string, AppReport> _appReports = new Dictionary<string, AppReport>(StringComparer.OrdinalIgnoreCase);

        // Last reported track
        private string _title = "";
        private string _artist = "";
        private string _app = "";
        private bool _isPlaying;
        private TimeSpan _position;
        private string _coverKey = "";
        private string _coverUrl = "";
        private volatile bool _coverUploading;
        // Debounced sending: Discord tolerates ~5 updates / 20s. Bursts from the
        // widget + bar views collapse into one send; identical states never resend
        // (the elapsed timer runs client-side from `start`, no refresh needed).
        private Timer? _sendTimer;
        private string _lastSentSignature = "";
        private DateTime _lastSendUtc = DateTime.MinValue;

        private DiscordPresenceService()
        {
            _retryTimer = new Timer(_ =>
            {
                if (_wantConnection && !_connected)
                    BeginConnect();
            }, null, Timeout.Infinite, Timeout.Infinite);
            _sendTimer = new Timer(SendPending, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void ApplySettings(bool enabled, string clientId, bool showWhenIdle, bool showWhenPaused,
            bool showTitle, bool showArtist, bool showApp, bool showElapsed, bool showCover,
            string largeImage, string smallImage, bool privateMode,
            bool buttonsEnabled, string button1Label, string button1Url,
            string button2Label, string button2Url, string stateUrl, string detailsUrl,
            string largeUrl, List<string>? priority)
        {
            lock (_lock)
            {
                _enabled = enabled;
                _clientId = (clientId ?? "").Trim();
                _showWhenIdle = showWhenIdle;
                _showWhenPaused = showWhenPaused;
                _showTitle = showTitle;
                _showArtist = showArtist;
                _showApp = showApp;
                _showElapsed = showElapsed;
                _showCover = showCover;
                _largeImage = (largeImage ?? "").Trim();
                _smallImage = (smallImage ?? "").Trim();
                _privateMode = privateMode;
                _buttonsEnabled = buttonsEnabled;
                _button1Label = (button1Label ?? "").Trim();
                _button1Url = (button1Url ?? "").Trim();
                _button2Label = (button2Label ?? "").Trim();
                _button2Url = (button2Url ?? "").Trim();
                _stateUrl = (stateUrl ?? "").Trim();
                _detailsUrl = (detailsUrl ?? "").Trim();
                _largeUrl = (largeUrl ?? "").Trim();
                _priority = priority != null ? new List<string>(priority) : new List<string>();
            }

            if (_enabled && !string.IsNullOrEmpty(_clientId))
            {
                _wantConnection = true;
                Palisades.App.Log("[Discord] enabled, clientId set, connecting...");
                BeginConnect();
                RefreshActivity();
            }
            else
            {
                Palisades.App.Log("[Discord] disabled or clientId empty (enabled=" + _enabled + ")");
                Disconnect();
            }
        }

        private static string ReportKey(string appId, string app)
        {
            return !string.IsNullOrEmpty(appId) ? appId : "name:" + (app ?? "");
        }

        /// <summary>Best automatic pick: most recent playing track, else most recent
        /// track (when paused display is allowed). Used by "*" and as fallback.</summary>
        private AppReport? BestAuto()
        {
            AppReport? best = null;
            foreach (var e in _appReports.Values)
            {
                if (!HasTrack(e)) continue;
                if (!e.IsPlaying && !_showWhenPaused) continue;
                if (best == null || (e.IsPlaying && !best.IsPlaying) ||
                    (e.IsPlaying == best.IsPlaying && e.SeenUtc > best.SeenUtc))
                    best = e;
            }
            return best;
        }

        private static bool HasTrack(AppReport e)
        {
            return !string.IsNullOrEmpty(e.Title) || !string.IsNullOrEmpty(e.Artist);
        }

        /// <summary>Full report for one session (followed view: title, state, cover bytes).</summary>
        public void Report(string title, string artist, string app, string appId, bool isPlaying, TimeSpan position, byte[]? coverBytes = null)
        {
            lock (_lock)
            {
                string key = ReportKey(appId, app);
                if (!_appReports.TryGetValue(key, out var e))
                {
                    e = new AppReport();
                    _appReports[key] = e;
                }
                bool trackChangedHere = (e.Title != (title ?? "")) || (e.Artist != (artist ?? ""));
                e.Title = title ?? "";
                e.Artist = artist ?? "";
                e.App = app ?? "";
                e.AppId = appId ?? "";
                e.IsPlaying = isPlaying;
                e.Position = position;
                e.SeenUtc = DateTime.UtcNow;
                if (trackChangedHere)
                    e.CoverBytes = null;
                if (coverBytes != null && coverBytes.Length > 0 && coverBytes.Length < 1024 * 1024)
                    e.CoverBytes = coverBytes;
            }
            RefreshSelection();
        }

        /// <summary>Lightweight snapshot for every known session (no cover bytes).</summary>
        public void ReportSession(string appId, string title, string artist, bool isPlaying)
        {
            lock (_lock)
            {
                string key = ReportKey(appId, "");
                if (!_appReports.TryGetValue(key, out var e))
                {
                    e = new AppReport { AppId = appId ?? "" };
                    _appReports[key] = e;
                }
                if (e.Title != (title ?? "") || e.Artist != (artist ?? ""))
                {
                    e.Title = title ?? "";
                    e.Artist = artist ?? "";
                    e.CoverBytes = null;
                }
                e.IsPlaying = isPlaying;
                e.SeenUtc = DateTime.UtcNow;
            }
            RefreshSelection();
        }

        public void ReportSessionGone(string appId)
        {
            lock (_lock)
            {
                _appReports.Remove(ReportKey(appId, ""));
            }
            RefreshSelection();
        }

        public void RetainOnly(System.Collections.Generic.IEnumerable<string> appIds)
        {
            lock (_lock)
            {
                var keep = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                foreach (var id in appIds)
                    keep.Add(ReportKey(id, ""));
                var dead = new System.Collections.Generic.List<string>();
                foreach (var k in _appReports.Keys)
                    if (!keep.Contains(k)) dead.Add(k);
                foreach (var k in dead)
                    _appReports.Remove(k);
            }
            RefreshSelection();
        }

        /// <summary>Pick what Discord shows. Priority list non-empty: ONLY the list
        /// decides (widget focus/pin never interferes). Empty list: legacy
        /// behavior (most recent track). "*" entry means "anything else".</summary>
        private void RefreshSelection()
        {
            string trackKey;
            string t;
            string a;
            string appId;
            string appName;
            byte[]? bytes;
            bool wantResolve;
            bool trackChanged;
            lock (_lock)
            {
                AppReport? pick = null;
                if (_priority.Count > 0)
                {
                    foreach (var id in _priority)
                    {
                        if (id == "*")
                        {
                            pick = BestAuto();
                            if (pick != null) break;
                            continue;
                        }
                        if (_appReports.TryGetValue(ReportKey(id, ""), out var e) && e.IsPlaying && HasTrack(e))
                        {
                            pick = e;
                            break;
                        }
                    }
                    if (pick == null && _showWhenPaused)
                    {
                        foreach (var id in _priority)
                        {
                            if (id == "*") continue;
                            if (_appReports.TryGetValue(ReportKey(id, ""), out var e) && HasTrack(e))
                            {
                                pick = e;
                                break;
                            }
                        }
                    }
                }
                if (pick == null && _priority.Count == 0)
                    pick = BestAuto();

                if (pick != null)
                {
                    _title = pick.Title;
                    _artist = pick.Artist;
                    _app = pick.App;
                    if (string.IsNullOrEmpty(_app) && !string.IsNullOrEmpty(pick.AppId))
                    {
                        _app = pick.AppId;
                        if (_app.Contains('.')) _app = _app.Substring(0, _app.LastIndexOf('.'));
                    }
                    _isPlaying = pick.IsPlaying;
                    _position = pick.Position;
                }
                else
                {
                    // No known track left (all sessions gone) → fall back to idle/clear.
                    _title = "";
                    _artist = "";
                    _app = "";
                    _isPlaying = false;
                    _position = TimeSpan.Zero;
                }

                trackKey = (_title ?? "") + "\0" + (_artist ?? "") + "\0" + (_app ?? "");
                t = _title;
                a = _artist;
                bytes = pick?.CoverBytes;
                wantResolve = _showCover;
                appId = pick?.AppId ?? "";
                appName = _app;
                trackChanged = trackKey != _coverKey;
                if (trackChanged)
                {
                    _coverKey = trackKey;
                    _coverUrl = "";
                    if (DiscordArtUploader.TryGetCached(trackKey, out string cached))
                        _coverUrl = cached;
                }
            }

            if (!trackChanged)
            {
                RefreshActivity();
                return;
            }
            if (!wantResolve)
            {
                Palisades.App.Log("[Discord] no cover resolve for '" + t + "': showCover disabled");
                RefreshActivity();
                return;
            }
            if (!string.IsNullOrEmpty(_coverUrl))
            {
                RefreshActivity(); // cache hit
                return;
            }

            // Resolve the picked track's cover in the background (once per track).
            // Browser (YouTube…): video ID via Piped → i.ytimg.com direct (exact).
            // Other apps: Apple CDN artwork.
            // (No uploads: Discord's proxy refuses catbox/tmpfiles hosts.
            // No data: URLs: Discord IPC caps large_image at 300 chars.)
            if (!_coverUploading)
            {
                _coverUploading = true;
                Palisades.App.Log("[Discord] resolving artwork for '" + t + "'");
                string videoTitle = t;
                string aid = appId;
                string aname = appName;
                ThreadPool.QueueUserWorkItem(async _ =>
                {
                    try
                    {
                        string? url = null;
                        if (DiscordArtUploader.IsBrowserApp(aid, aname))
                            url = await DiscordArtUploader.ResolveYouTubeThumbnailAsync(videoTitle).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(url))
                            url = await DiscordArtUploader.ResolveArtworkUrlAsync(t, a).ConfigureAwait(false);
                        Palisades.App.Log("[Discord] cover result: " + (url == null ? "<FAILED>" : url.Substring(0, Math.Min(80, url.Length))));
                        if (!string.IsNullOrEmpty(url))
                        {
                            DiscordArtUploader.Cache(trackKey, url!);
                            lock (_lock)
                            {
                                if (trackKey == _coverKey)
                                    _coverUrl = url!;
                            }
                            Palisades.App.Log("[Discord] cover url: " + url!);
                            RefreshActivity();
                            // One safety re-send in case Discord dropped the update.
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                                    ForceResend();
                                }
                                catch { }
                            });
                        }
                    }
                    catch (Exception ex) { Palisades.App.Log(ex, "[Discord] cover resolve"); }
                    finally { _coverUploading = false; }
                });
            }

            RefreshActivity();
        }

        public void Shutdown()
        {
            Disconnect();
            try { _retryTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        }

        private void BeginConnect()
        {
            lock (_lock)
            {
                if (_connected || string.IsNullOrEmpty(_clientId)) return;
            }
            var t = new Thread(ConnectLoop) { IsBackground = true, Name = "DiscordRpcConnect" };
            t.Start();
        }

        private void ConnectLoop()
        {
            try
            {
                if (!_wantConnection) return;
                lock (_lock)
                {
                    if (_connected) return;
                }

                string clientId;
                lock (_lock) { clientId = _clientId; }

                NamedPipeClientStream? pipe = null;
                for (int i = 0; i < 10; i++)
                {
                    if (!_wantConnection) return;
                    try
                    {
                        var p = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                        p.Connect(1500);
                        pipe = p;
                        break;
                    }
                    catch { }
                }

                if (pipe == null)
                {
                    Palisades.App.Log("[Discord] no IPC pipe found (Discord client running?)");
                    ScheduleRetry();
                    return;
                }

                lock (_lock)
                {
                    if (!_wantConnection || _connected || _clientId != clientId)
                    {
                        try { pipe.Dispose(); } catch { }
                        return;
                    }
                    _pipe = pipe;
                }

                // Handshake
                WriteFrame(pipe, 0, JsonConvert.SerializeObject(new { v = 1, client_id = clientId }));
                string? reply = ReadFrame(pipe, 5000);
                Palisades.App.Log("[Discord] handshake reply: " + (reply == null ? "<none>" : reply.Substring(0, Math.Min(200, reply.Length))));
                if (reply == null)
                {
                    ClosePipe();
                    ScheduleRetry();
                    return;
                }

                lock (_lock) { _connected = true; _lastSentSignature = ""; }

                _readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "DiscordRpcReader" };
                _readerThread.Start();

                RefreshActivity();
            }
            catch
            {
                ClosePipe();
                ScheduleRetry();
            }
        }

        private void ReaderLoop()
        {
            try
            {
                while (true)
                {
                    NamedPipeClientStream? pipe;
                    lock (_lock) { pipe = _pipe; }
                    if (pipe == null) break;
                    string? frame = ReadFrame(pipe, Timeout.Infinite);
                    if (frame == null) break; // closed / error
                    // Responses (READY, activity ACKs) need no handling; reading keeps the pipe drained.
                    // Log protocol errors (e.g. bad client id / bad asset) to help debugging.
                    try
                    {
                        if (frame.Contains("\"code\""))
                            Palisades.App.Log("[Discord] IPC error frame: " + frame.Substring(0, Math.Min(300, frame.Length)));
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                bool want;
                lock (_lock) { _connected = false; _pipe = null; want = _wantConnection; }
                if (want) ScheduleRetry();
            }
        }

        private void ScheduleRetry()
        {
            try { _retryTimer?.Change(TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan); } catch { }
        }

        private void Disconnect()
        {
            _wantConnection = false;
            lock (_lock)
            {
                if (_connected && _pipe != null)
                {
                    try
                    {
                        // Best-effort clear; Discord also clears when the pipe closes.
                        WriteFrame(_pipe, 1, JsonConvert.SerializeObject(new
                        {
                            cmd = "SET_ACTIVITY",
                            args = new { pid = Environment.ProcessId, activity = (object?)null },
                            nonce = Guid.NewGuid().ToString()
                        }));
                    }
                    catch { }
                }
                _lastSentSignature = "";
            }
            ClosePipe();
        }

        private void ClosePipe()
        {
            lock (_lock)
            {
                _connected = false;
                try { _pipe?.Dispose(); } catch { }
                _pipe = null;
            }
        }

        private string BuildSignature()
        {
            // Semantic state only: timestamps.start jitters every second and must not
            // defeat dedup (Discord runs the elapsed timer client-side anyway).
            return _title + "\0" + _artist + "\0" + _app + "\0" + _isPlaying
                + "\0" + _coverUrl + "\0" + _showWhenIdle + "\0" + _showWhenPaused + "\0" + _showTitle
                + "\0" + _showArtist + "\0" + _showApp + "\0" + _showElapsed
                + "\0" + _showCover + "\0" + _largeImage + "\0" + _smallImage + "\0" + _privateMode
                + "\0" + _buttonsEnabled + "\0" + _button1Label + "\0" + _button1Url
                + "\0" + _button2Label + "\0" + _button2Url + "\0" + _stateUrl + "\0" + _detailsUrl
                + "\0" + _largeUrl;
        }

        private void RefreshActivity()
        {
            lock (_lock)
            {
                if (BuildSignature() == _lastSentSignature) return; // nothing new
            }
            // Coalesce bursts: (re)arm a single send 1.5s out.
            try { _sendTimer?.Change(TimeSpan.FromMilliseconds(1500), Timeout.InfiniteTimeSpan); } catch { }
        }

        private void ForceResend()
        {
            lock (_lock) { _lastSentSignature = ""; }
            RefreshActivity();
        }

        private void SendPending(object? _)
        {
            string payload;
            lock (_lock)
            {
                if (BuildSignature() == _lastSentSignature) return;
                // Minimum 2.5s between sends (Discord rate limit).
                var wait = TimeSpan.FromMilliseconds(2500) - (DateTime.UtcNow - _lastSendUtc);
                if (wait > TimeSpan.Zero)
                {
                    try { _sendTimer?.Change(wait, Timeout.InfiniteTimeSpan); } catch { }
                    return;
                }
                if (!_connected || _pipe == null) return; // retry on next change / connect
                payload = BuildActivityJson();
                _lastSentSignature = BuildSignature();
                _lastSendUtc = DateTime.UtcNow;
            }
            // Never block the UI thread on pipe I/O.
            bool hasCover;
            lock (_lock) { hasCover = !string.IsNullOrEmpty(_coverUrl); }
            Palisades.App.Log("[Discord] send activity: title='" + _title + "' playing=" + _isPlaying + " hasCover=" + hasCover);
            ThreadPool.QueueUserWorkItem(__ =>
            {
                try
                {
                    NamedPipeClientStream? pipe;
                    lock (_lock) { pipe = _pipe; }
                    if (pipe == null) return;
                    WriteFrame(pipe, 1, payload);
                }
                catch { }
            });
        }

        private string BuildActivityJson()
        {
            JObject? activity = null;

            bool hasTrack = !string.IsNullOrEmpty(_title) || !string.IsNullOrEmpty(_artist);
            if (hasTrack && (_isPlaying || _showWhenPaused) && _privateMode)
            {
                // Private mode: generic presence, no titles, no cover, no timer.
                var act = new JObject
                {
                    ["type"] = 2,
                    ["name"] = "Palisades",
                    ["details"] = Palisades.Services.TranslationService.Instance["Discord_PrivateListening"]
                };
                if (!string.IsNullOrEmpty(_largeImage))
                {
                    act["assets"] = new JObject
                    {
                        ["large_image"] = _largeImage,
                        ["large_text"] = "Palisades"
                    };
                }
                activity = act;
            }
            else if (hasTrack && (_isPlaying || _showWhenPaused))
            {
                // 4 slots: header "Listening to {title}" (custom name),
                // details = title, state = artist (or Paused marker), cover caption = "Palisades - {app}".
                bool paused = !_isPlaying;
                string titlePart = _showTitle && !string.IsNullOrEmpty(_title) ? _title : "";
                string artistPart = _showArtist && !string.IsNullOrEmpty(_artist) ? _artist : "";
                string where = (_showApp && !string.IsNullOrEmpty(_app)) ? "Palisades - " + _app : "Palisades";

                var act = new JObject
                {
                    // Type 2 (Listening), raw https URL as large_image (PreMiD-style):
                    // the Discord client itself proxies it. Do NOT prefix mp:external/
                    // manually — Discord treats that as a literal asset key and drops it.
                    ["type"] = 2,
                    ["name"] = Truncate(!string.IsNullOrEmpty(titlePart) ? titlePart : "Palisades", 120)
                };
                if (!string.IsNullOrEmpty(titlePart))
                    act["details"] = Truncate(titlePart, 120);
                else if (!string.IsNullOrEmpty(artistPart))
                    act["details"] = Truncate(artistPart, 120);
                if (act["details"] != null && IsHttpUrl(_detailsUrl))
                    act["details_url"] = _detailsUrl;
                string statePart = paused
                    ? "⏸ Paused" + (!string.IsNullOrEmpty(artistPart) ? " • " + artistPart : "")
                    : artistPart;
                if (!string.IsNullOrEmpty(statePart))
                {
                    act["state"] = Truncate(statePart, 120);
                    if (IsHttpUrl(_stateUrl))
                        act["state_url"] = _stateUrl;
                }

                var assets = new JObject();
                string dynamicCover = "";
                if (_showCover && !string.IsNullOrEmpty(_coverUrl))
                    dynamicCover = _coverUrl.Trim();
                if (!string.IsNullOrEmpty(dynamicCover))
                {
                    assets["large_image"] = dynamicCover;
                    assets["large_text"] = Truncate(where, 120); // caption under the cover
                    if (IsHttpUrl(_largeUrl))
                        assets["large_url"] = _largeUrl;
                    // Static small slot + dynamic large mix works (PreMiD-style).
                    string smallFallback = !string.IsNullOrEmpty(_smallImage) ? _smallImage : _largeImage;
                    if (!string.IsNullOrEmpty(smallFallback))
                    {
                        assets["small_image"] = smallFallback;
                        assets["small_text"] = "Palisades";
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(_largeImage))
                    {
                        assets["large_image"] = _largeImage;
                        assets["large_text"] = Truncate(!string.IsNullOrEmpty(_app) ? _app : "Palisades", 120);
                        if (IsHttpUrl(_largeUrl))
                            assets["large_url"] = _largeUrl;
                    }
                    if (!string.IsNullOrEmpty(_smallImage))
                    {
                        assets["small_image"] = _smallImage;
                        assets["small_text"] = "Palisades";
                    }
                }
                if (assets.Count > 0)
                    act["assets"] = assets;

                if (_isPlaying && _showElapsed && _position >= TimeSpan.Zero)
                {
                    long start = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)_position.TotalSeconds;
                    act["timestamps"] = new JObject { ["start"] = start };
                }

                // Presence buttons (max 2). Empty label or non-http link = hidden.
                if (_buttonsEnabled)
                {
                    var buttons = new JArray();
                    AddPresenceButton(buttons, _button1Label, _button1Url);
                    AddPresenceButton(buttons, _button2Label, _button2Url);
                    if (buttons.Count > 0)
                        act["buttons"] = buttons;
                }

                activity = act;
            }
            else if (_showWhenIdle)
            {
                var act = new JObject { ["details"] = "Palisades" };
                if (!string.IsNullOrEmpty(_largeImage))
                {
                    act["assets"] = new JObject
                    {
                        ["large_image"] = _largeImage,
                        ["large_text"] = "Palisades"
                    };
                }
                activity = act;
            }

            var root = new JObject
            {
                ["cmd"] = "SET_ACTIVITY",
                ["args"] = new JObject
                {
                    ["pid"] = Environment.ProcessId,
                    ["activity"] = activity
                },
                ["nonce"] = Guid.NewGuid().ToString()
            };
            return root.ToString(Formatting.None);
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private static bool IsHttpUrl(string? url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddPresenceButton(JArray buttons, string label, string url)
        {
            try
            {
                if (buttons.Count >= 2) return;
                label = (label ?? "").Trim();
                url = (url ?? "").Trim();
                if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(url)) return;
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
                buttons.Add(new JObject
                {
                    ["label"] = Truncate(label, 32),
                    ["url"] = url
                });
            }
            catch { }
        }

        private static void WriteFrame(NamedPipeClientStream pipe, uint opcode, string json)
        {
            byte[] data = Encoding.UTF8.GetBytes(json);
            byte[] header = new byte[8];
            BitConverter.GetBytes(opcode).CopyTo(header, 0);
            BitConverter.GetBytes(data.Length).CopyTo(header, 4);
            pipe.Write(header, 0, header.Length);
            pipe.Write(data, 0, data.Length);
            pipe.Flush();
        }

        private static string? ReadFrame(NamedPipeClientStream pipe, int timeoutMs)
        {
            try
            {
                byte[] header = new byte[8];
                if (!ReadExact(pipe, header, timeoutMs)) return null;
                int len = BitConverter.ToInt32(header, 4);
                if (len <= 0 || len > 16 * 1024 * 1024) return null;
                byte[] data = new byte[len];
                if (!ReadExact(pipe, data, timeoutMs)) return null;
                return Encoding.UTF8.GetString(data);
            }
            catch { return null; }
        }

        private static bool ReadExact(NamedPipeClientStream pipe, byte[] buffer, int timeoutMs)
        {
            int offset = 0;
            var start = Environment.TickCount;
            while (offset < buffer.Length)
            {
                if (timeoutMs != Timeout.Infinite && unchecked(Environment.TickCount - start) > timeoutMs)
                    return false;
                int read;
                try { read = pipe.Read(buffer, offset, buffer.Length - offset); }
                catch { return false; }
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }
    }
}
