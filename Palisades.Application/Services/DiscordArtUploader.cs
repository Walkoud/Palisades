using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Palisades.Services
{
    /// <summary>
    /// Resolves direct artwork URLs (no upload): YouTube thumbnails via keyless
    /// Piped API instances, music artwork via Apple Search API. Discord proxies
    /// raw https URLs itself (PreMiD-style). Uploads were dropped: Discord's
    /// proxy refuses catbox/tmpfiles hosts, and IPC caps large_image at 300 chars
    /// (no data: URLs).
    /// </summary>
    public static class DiscordArtUploader
    {
        private static readonly HttpClient _http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            // No User-Agent → the connection gets killed mid-upload by filtering.
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Palisades/1.0");
            return c;
        }
        private static readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
        private static readonly object _cacheLock = new object();

        public static bool TryGetCached(string trackKey, out string url)
        {
            lock (_cacheLock)
                return _cache.TryGetValue(trackKey, out url);
        }

        public static void Cache(string trackKey, string url)
        {
            lock (_cacheLock)
            {
                if (_cache.Count > 100)
                    _cache.Clear();
                _cache[trackKey] = url;
            }
        }

        /// <summary>
        /// Resolves a direct YouTube thumbnail URL (i.ytimg.com, PreMiD-style) by
        /// searching keyless Piped API instances for the video ID. No upload involved.
        /// </summary>
        public static async Task<string?> ResolveYouTubeThumbnailAsync(string title, CancellationToken ct = default)
        {
            try
            {
                string query = (title ?? "").Trim();
                if (query.Length < 2) return null;
                string[] instances =
                {
                    "https://pipedapi.adminforge.de",
                    "https://pipedapi.kavin.rocks",
                    "https://pipedapi.reallyaweso.me",
                    "https://pipedapi.leptons.xyz",
                    "https://api.piped.private.coffee"
                };
                foreach (var inst in instances)
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        string url = inst + "/search?q=" + Uri.EscapeDataString(query) + "&filter=videos";
                        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode) continue;
                        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        var json = Newtonsoft.Json.Linq.JObject.Parse(body);
                        var items = json["items"] as Newtonsoft.Json.Linq.JArray;
                        if (items == null || items.Count == 0) continue;
                        foreach (var it in items)
                        {
                            string? watchUrl = it["url"]?.ToString();
                            string? id = ExtractYouTubeId(watchUrl);
                            if (!string.IsNullOrEmpty(id))
                                return "https://i.ytimg.com/vi/" + id + "/hqdefault.jpg";
                        }
                    }
                    catch { /* try next instance */ }
                }
                return null;
            }
            catch { return null; }
        }

        private static string? ExtractYouTubeId(string? watchUrl)
        {
            try
            {
                if (string.IsNullOrEmpty(watchUrl)) return null;
                // "/watch?v=ID..." or "ID"
                int v = watchUrl.IndexOf("v=", StringComparison.OrdinalIgnoreCase);
                string id = v >= 0 ? watchUrl.Substring(v + 2) : watchUrl.Trim();
                int amp = id.IndexOf('&');
                if (amp >= 0) id = id.Substring(0, amp);
                int q = id.IndexOf('?');
                if (q >= 0) id = id.Substring(0, q);
                id = id.Trim().Trim('/');
                if (id.Length < 5 || id.Length > 20) return null;
                foreach (char c in id)
                    if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return null;
                return id;
            }
            catch { return null; }
        }

        private static readonly string[] BrowserMarkers =
        {
            "chrome", "edge", "msedge", "brave", "firefox", "opera", "vivaldi", "whale", "arc"
        };

        public static bool IsBrowserApp(string? appId, string? appName)
        {
            try
            {
                string s = ((appId ?? "") + " " + (appName ?? "")).ToLowerInvariant();
                foreach (var m in BrowserMarkers)
                    if (s.Contains(m)) return true;
            }
            catch { }
            return false;
        }
        public static async Task<string?> ResolveArtworkUrlAsync(string title, string artist, CancellationToken ct = default)
        {
            try
            {
                string query = ((artist ?? "") + " " + (title ?? "")).Trim();
                if (query.Length < 2) return null;
                string url = "https://itunes.apple.com/search?term=" + Uri.EscapeDataString(query)
                    + "&media=music&entity=song&limit=5";
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var json = Newtonsoft.Json.Linq.JObject.Parse(body);
                var results = json["results"] as Newtonsoft.Json.Linq.JArray;
                if (results == null || results.Count == 0) return null;
                foreach (var r in results)
                {
                    string? art = r["artworkUrl100"]?.ToString();
                    if (!string.IsNullOrEmpty(art) && art.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        return art.Replace("100x100bb", "600x600bb");
                }
                return null;
            }
            catch { return null; }
        }
    }
}
