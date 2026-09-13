using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Palisades.Services
{
    public sealed class RadioStation
    {
        public string Uuid = "";
        public string Name = "";
        public string StreamUrl = "";
        public string Favicon = "";
        public string Tags = "";
        public string Country = "";
        public string Language = "";
        public string Codec = "";
        public int Bitrate;
        public int Votes;
    }

    /// <summary>Radio Browser API client (free, no key). App User-Agent required.</summary>
    public static class RadioBrowserService
    {
        private const string Base = "https://de1.api.radio-browser.info/json/";

        private static readonly HttpClient _http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Palisades/1.0 (Windows; desktop-widget)");
            return c;
        }

        public static async Task<List<RadioStation>> SearchAsync(string query, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<RadioStation>();
            string q = query.Trim();
            // Name AND tag matches, merged (Radio Browser ANDs params, so two calls).
            var nameTask = FetchStationsAsync(Base + "stations/search?hidebroken=true&order=clickcount&reverse=true&limit=30&name=" + Uri.EscapeDataString(q), ct);
            var tagTask = FetchStationsAsync(Base + "stations/search?hidebroken=true&order=clickcount&reverse=true&limit=30&tagList=" + Uri.EscapeDataString(q), ct);
            await Task.WhenAll(nameTask, tagTask).ConfigureAwait(false);
            var merged = new List<RadioStation>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in nameTask.Result.Concat(tagTask.Result))
                if (seen.Add(s.Uuid)) merged.Add(s);
            return merged
                .OrderByDescending(s => s.Votes)
                .ThenBy(s => s.Name)
                .Take(40).ToList();
        }

        public static async Task<List<RadioStation>> TopAsync(string? countryCode = null, int limit = 20, CancellationToken ct = default)
        {
            string url = Base + "stations/search?hidebroken=true&order=clickcount&reverse=true&limit=" + Math.Clamp(limit, 1, 100);
            if (!string.IsNullOrWhiteSpace(countryCode))
                url += "&countrycode=" + Uri.EscapeDataString(countryCode.Trim().ToUpperInvariant());
            return await FetchStationsAsync(url, ct).ConfigureAwait(false);
        }

        /// <summary>Reports a listen (drives popularity ranking). Fire-and-forget.</summary>
        public static void ReportClick(string uuid)
        {
            if (string.IsNullOrWhiteSpace(uuid)) return;
            try
            {
                _ = _http.GetAsync(Base + "url/" + Uri.EscapeDataString(uuid.Trim()));
            }
            catch { }
        }

        private static async Task<List<RadioStation>> FetchStationsAsync(string url, CancellationToken ct)
        {
            var list = new List<RadioStation>();
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                foreach (var s in JArray.Parse(body))
                {
                    string stream = s["url_resolved"]?.ToString() ?? s["url"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(stream)) continue;
                    list.Add(new RadioStation
                    {
                        Uuid = s["stationuuid"]?.ToString() ?? "",
                        Name = (s["name"]?.ToString() ?? "").Trim(),
                        StreamUrl = stream.Trim(),
                        Favicon = (s["favicon"]?.ToString() ?? "").Trim(),
                        Tags = (s["tags"]?.ToString() ?? "").Trim(),
                        Country = s["country"]?.ToString() ?? "",
                        Language = s["language"]?.ToString() ?? "",
                        Codec = (s["codec"]?.ToString() ?? "").ToUpperInvariant()
                    });
                    try { list[list.Count - 1].Bitrate = s["bitrate"]?.ToObject<int>() ?? 0; } catch { }
                    try { list[list.Count - 1].Votes = s["votes"]?.ToObject<int>() ?? 0; } catch { }
                }
            }
            catch { }
            return list.Where(s => !string.IsNullOrEmpty(s.Name)).Take(50).ToList();
        }
    }
}
