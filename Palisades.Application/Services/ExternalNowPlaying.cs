using System;

namespace Palisades.Services
{
    /// <summary>
    /// Lightweight bridge so a gadget (e.g. Radio) can surface its current
    /// stream in the Now Playing widget without a real SMTC session.
    /// </summary>
    public static class ExternalNowPlaying
    {
        public sealed class Info
        {
            public string App = "";
            public string Title = "";
            public string Artist = "";
            public bool IsPlaying;
        }

        private static readonly object _lock = new object();
        private static Info? _current;

        /// <summary>Raised on any report/clear so the widget can re-render.</summary>
        public static event EventHandler? Changed;

        /// <summary>Control hooks the Now Playing widget can invoke (radio).</summary>
        public static Action? TogglePlayPause;
        public static Action? SkipNext;
        public static Action? SkipPrevious;

        public static Info? Current
        {
            get { lock (_lock) return _current; }
        }

        public static void Report(string app, string title, string artist, bool isPlaying)
        {
            lock (_lock)
            {
                _current = new Info
                {
                    App = app ?? "",
                    Title = title ?? "",
                    Artist = artist ?? "",
                    IsPlaying = isPlaying
                };
            }
            try { Changed?.Invoke(null, EventArgs.Empty); } catch { }
        }

        /// <summary>Clears only if the current entry belongs to <paramref name="app"/>.</summary>
        public static void Clear(string app)
        {
            bool cleared = false;
            lock (_lock)
            {
                if (_current != null && string.Equals(_current.App, app, StringComparison.OrdinalIgnoreCase))
                {
                    _current = null;
                    cleared = true;
                }
            }
            if (cleared)
            {
                try { Changed?.Invoke(null, EventArgs.Empty); } catch { }
            }
        }
    }
}
