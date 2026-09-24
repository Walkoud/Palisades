using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Palisades.Models;

namespace Palisades.Services
{
    /// <summary>
    /// Remembers the last used settings (CustomData) per gadget type, so a newly
    /// created widget — on the desktop or in the Dynamic Island — starts with the
    /// same réglages the user already tuned. Content types (Post-it text) excluded.
    /// </summary>
    public sealed class GadgetTypeDefaults
    {
        private static GadgetTypeDefaults? _instance;
        public static GadgetTypeDefaults Instance => _instance ??= new GadgetTypeDefaults();

        private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase) { "PostIt" };

        private readonly string _path;
        private Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

        public event Action? Changed;

        private GadgetTypeDefaults()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palisades");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "gadget_type_defaults.json");
            Load();
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_path))
                    _map = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(_path))
                        ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch { _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        }

        public void Save()
        {
            try { File.WriteAllText(_path, JsonConvert.SerializeObject(_map, Formatting.Indented)); }
            catch { }
        }

        public string Get(string gadgetType)
        {
            if (string.IsNullOrEmpty(gadgetType)) return "";
            return _map.TryGetValue(gadgetType, out var data) ? data ?? "" : "";
        }

        public void Remember(string gadgetType, string customData)
        {
            if (string.IsNullOrEmpty(gadgetType) || string.IsNullOrEmpty(customData)) return;
            if (Excluded.Contains(gadgetType)) return;
            if (_map.TryGetValue(gadgetType, out var cur) && string.Equals(cur, customData, StringComparison.Ordinal))
                return;
            _map[gadgetType] = customData;
            Save();
            try { Changed?.Invoke(); } catch { }
        }

        /// <summary>Choke point: every gadget save refreshes the per-type memory.</summary>
        public void RememberFrom(IEnumerable<PluginGadgetItem> items)
        {
            bool any = false;
            foreach (var item in items)
            {
                if (item == null || string.IsNullOrEmpty(item.GadgetType)) continue;
                if (Excluded.Contains(item.GadgetType)) continue;
                if (string.IsNullOrEmpty(item.CustomData)) continue;
                if (_map.TryGetValue(item.GadgetType, out var cur)
                    && string.Equals(cur, item.CustomData, StringComparison.Ordinal))
                    continue;
                _map[item.GadgetType] = item.CustomData;
                any = true;
            }
            if (!any) return;
            Save();
            try { Changed?.Invoke(); } catch { }
        }
    }
}
