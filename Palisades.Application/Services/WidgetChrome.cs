using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Palisades.Services
{
    /// <summary>Global widget chrome: scrollbar behavior for every desktop widget.</summary>
    public static class WidgetChrome
    {
        // "auto" = always visible, "fade" = hidden until mouse-over,
        // "hidden" = never, "overlay" = floating inside, no space taken.
        private static string _scrollbarMode = "overlay";
        public static string ScrollbarMode
        {
            get => _scrollbarMode;
            set
            {
                string m = (value ?? "").ToLowerInvariant();
                if (m != "auto" && m != "hidden" && m != "overlay") m = "overlay";
                if (m == _scrollbarMode) return;
                _scrollbarMode = m;
                Save();
                try { Changed?.Invoke(null, EventArgs.Empty); } catch { }
            }
        }

        private static bool _headerOnHover;
        public static bool HeaderOnHover
        {
            get => _headerOnHover;
            set
            {
                if (value == _headerOnHover) return;
                _headerOnHover = value;
                Save();
                try { Changed?.Invoke(null, EventArgs.Empty); } catch { }
            }
        }

        public static event EventHandler? Changed;

        private static string Path =>
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Palisades", "widget_chrome.json");

        static WidgetChrome()
        {
            try
            {
                if (File.Exists(Path))
                {
                    var json = JObject.Parse(File.ReadAllText(Path));
                    string m = json["scrollbarMode"]?.ToString() ?? "overlay";
                    if (m == "auto" || m == "hidden" || m == "overlay" || m == "fade") _scrollbarMode = m;
                    _headerOnHover = json["headerOnHover"]?.ToObject<bool>() ?? false;
                }
            }
            catch { }
        }

        private static void Save()
        {
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                File.WriteAllText(Path, new JObject
                {
                    ["scrollbarMode"] = _scrollbarMode,
                    ["headerOnHover"] = _headerOnHover
                }.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch { }
        }
    }
}
