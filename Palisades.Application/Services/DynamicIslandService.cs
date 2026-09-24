using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using Newtonsoft.Json;

namespace Palisades.Services
{
    public sealed class DynamicIslandTheme
    {
        public string Name { get; set; } = "AMOLED";
        public string Background { get; set; } = "#000000";
        public string Border { get; set; } = "#262626";
        public string Foreground { get; set; } = "#FFFFFF";
        public string Muted { get; set; } = "#A0A0A0";
        public string Accent { get; set; } = "#FFFFFF";
        public double BorderThickness { get; set; } = 1;
        public double CornerRadius { get; set; } = 24;
    }

    /// <summary>Anchor presets. Custom = free position (Alt+drag the island).</summary>
    public static class IslandPlacements
    {
        public static readonly string[] All =
            { "TopCenter", "TopLeft", "TopRight", "BottomCenter", "BottomLeft", "BottomRight", "Custom" };
    }

    public sealed class DynamicIslandSettings
    {
        public bool Enabled { get; set; } = true;
        public string ThemeName { get; set; } = "AMOLED";
        /// <summary>Ordered gadget types hosted in the island (empty = none).</summary>
        public List<string> PinnedWidgets { get; set; } = new();
        /// <summary>Auto-expand on hover (with delay), auto-collapse on leave.</summary>
        public bool ExpandOnHover { get; set; } = true;
        public int SelectedIndex { get; set; }
        public string Placement { get; set; } = "TopCenter";
        /// <summary>Margin offsets (DIPs) applied around the anchor.</summary>
        public double OffsetX { get; set; }
        public double OffsetY { get; set; } = 8;
        /// <summary>Free position (DIPs, overlay-window relative) when Placement == Custom.</summary>
        public double CustomLeft { get; set; } = 300;
        public double CustomTop { get; set; } = 8;
        /// <summary>Content-driven width clamp (island auto-sizes up to this).</summary>
        public double MaxWidth { get; set; } = 460;
        /// <summary>Longueur (largeur fixe) de la pilule. Le contenu ne la fait pas
        /// grandir. 0 = auto (largeur = contenu, capée par MaxWidth).</summary>
        public double Width { get; set; } = 340;
        /// <summary>True = expanded card opens upward, false = downward.</summary>
        public bool ExpandUp { get; set; }
        /// <summary>Per-widget expanded height (px), keyed by gadget type.</summary>
        public Dictionary<string, double> WidgetHeights { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public double CornerRadius { get; set; } = 22;
        /// <summary>Background opacity 0.2..1.</summary>
        public double BgOpacity { get; set; } = 1.0;
        /// <summary>Pinned in front of the taskbar (like Now Playing pin).</summary>
        public bool PinToTaskbar { get; set; }
        public bool BarHideFullscreen { get; set; } = true;
        public double BarLeft { get; set; } = double.NaN;
        public double BarTop { get; set; } = double.NaN;
        /// <summary>All animation timings (ms), customizable from the dashboard.</summary>
        public int HoverOpenDelayMs { get; set; } = 250;
        public int HoverCloseDelayMs { get; set; } = 700;
        public int ExpandDurationMs { get; set; } = 380;
        public int ExpandFadeMs { get; set; } = 240;
    }

    public sealed class DynamicIslandService
    {
        private static DynamicIslandService? _instance;
        public static DynamicIslandService Instance => _instance ??= new DynamicIslandService();

        private readonly string _path;
        private DynamicIslandSettings _settings = new();

        public event Action? Changed;

        public static IReadOnlyList<DynamicIslandTheme> Presets { get; } = new List<DynamicIslandTheme>
        {
            new() { Name = "AMOLED", Background = "#000000", Border = "#262626", Foreground = "#FFFFFF", Muted = "#A0A0A0", Accent = "#FFFFFF" },
            new() { Name = "Catppuccin", Background = "#1E1E2E", Border = "#45475A", Foreground = "#CDD6F4", Muted = "#A6ADC8", Accent = "#89B4FA" },
            new() { Name = "Nord", Background = "#2E3440", Border = "#4C566A", Foreground = "#ECEFF4", Muted = "#D8DEE9", Accent = "#88C0D0" },
            new() { Name = "Gruvbox", Background = "#282828", Border = "#504945", Foreground = "#EBDBB2", Muted = "#A89984", Accent = "#FABD2F" },
            new() { Name = "TokyoNight", Background = "#1A1B26", Border = "#414868", Foreground = "#C0CAF5", Muted = "#9AA5CE", Accent = "#7AA2F7" },
            new() { Name = "Cyberpunk", Background = "#0D0221", Border = "#FF2A6D", Foreground = "#FFFFFF", Muted = "#D1F7FF", Accent = "#05FFA1" },
            new() { Name = "Dracula", Background = "#282A36", Border = "#44475A", Foreground = "#F8F8F2", Muted = "#6272A4", Accent = "#BD93F9" },
            new() { Name = "Solarized", Background = "#002B36", Border = "#073642", Foreground = "#839496", Muted = "#586E75", Accent = "#268BD2" },
            new() { Name = "Everforest", Background = "#2D353B", Border = "#475258", Foreground = "#D3C6AA", Muted = "#9DA9A0", Accent = "#A7C080" },
            new() { Name = "RosePine", Background = "#191724", Border = "#26233A", Foreground = "#E0DEF4", Muted = "#908CAA", Accent = "#EB6F92" },
        };

        public DynamicIslandSettings Settings => _settings;

        public DynamicIslandTheme CurrentTheme =>
            Presets.FirstOrDefault(t => t.Name.Equals(_settings.ThemeName, StringComparison.OrdinalIgnoreCase))
            ?? Presets[0];

        private DynamicIslandService()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palisades");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "dynamic_island.json");
            Load();
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_path))
                    _settings = JsonConvert.DeserializeObject<DynamicIslandSettings>(File.ReadAllText(_path))
                        ?? new DynamicIslandSettings();
            }
            catch { _settings = new DynamicIslandSettings(); }
            _settings.PinnedWidgets ??= new List<string>();
            if (!IslandPlacements.All.Contains(_settings.Placement))
                _settings.Placement = "TopCenter";
            _settings.MaxWidth = Math.Clamp(_settings.MaxWidth, 220, 1600);
            _settings.Width = Math.Clamp(_settings.Width, 180, 1600);
            _settings.CornerRadius = Math.Clamp(_settings.CornerRadius, 8, 32);
            _settings.BgOpacity = Math.Clamp(_settings.BgOpacity, 0.2, 1.0);
            _settings.HoverOpenDelayMs = Math.Clamp(_settings.HoverOpenDelayMs, 0, 1000);
            _settings.HoverCloseDelayMs = Math.Clamp(_settings.HoverCloseDelayMs, 100, 2000);
            _settings.ExpandDurationMs = Math.Clamp(_settings.ExpandDurationMs, 100, 800);
            _settings.ExpandFadeMs = Math.Clamp(_settings.ExpandFadeMs, 50, 500);
            _settings.WidgetHeights ??= new Dictionary<string, double>();
            // Migration : les sliders d'offset ont été supprimés, des valeurs
            // résiduelles décaleraient l'îlot sans aucun moyen de les corriger.
            if (_settings.OffsetX != 0 || _settings.OffsetY != 8)
            {
                _settings.OffsetX = 0;
                _settings.OffsetY = 8;
                try { Save(); } catch { }
            }
        }

        /// <summary>Save robuste : écriture atomique (temp + replace, jamais de
        /// fichier tronqué en cas de kill) + retry car la copie backup toutes
        /// les 5 min verrouille les *.json en lecture (sinon save perdue, silencieuse).</summary>
        public void Save()
        {
            string json;
            try { json = JsonConvert.SerializeObject(_settings, Formatting.Indented); }
            catch { return; }
            string? tmp = null;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    tmp = _path + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, _path, true); // rename atomique : jamais tronqué
                    return;
                }
                catch
                {
                    System.Threading.Thread.Sleep(50 * (attempt + 1));
                }
            }
            try { App.Log("[Island] Save FAILED after retries: " + _path); } catch { }
        }

        private void Notify() { try { Changed?.Invoke(); } catch { } }

        public bool Enabled
        {
            get => _settings.Enabled;
            set { if (_settings.Enabled == value) return; _settings.Enabled = value; Save(); IslandDiag.Log("SET Enabled=" + value); Notify(); }
        }

        public string ThemeName
        {
            get => _settings.ThemeName;
            set { if (string.Equals(_settings.ThemeName, value, StringComparison.OrdinalIgnoreCase)) return; _settings.ThemeName = value; Save(); IslandDiag.Log("SET ThemeName=" + value); Notify(); }
        }

        public string Placement
        {
            get => _settings.Placement;
            set { if (string.Equals(_settings.Placement, value, StringComparison.OrdinalIgnoreCase)) return; _settings.Placement = value; Save(); IslandDiag.Log("SET Placement=" + value); Notify(); }
        }

        public double OffsetX
        {
            get => _settings.OffsetX;
            set { value = Math.Clamp(value, -800, 800); if (_settings.OffsetX == value) return; _settings.OffsetX = value; Save(); IslandDiag.Log("SET OffsetX=" + value); Notify(); }
        }

        public double OffsetY
        {
            get => _settings.OffsetY;
            set { value = Math.Clamp(value, -800, 800); if (_settings.OffsetY == value) return; _settings.OffsetY = value; Save(); IslandDiag.Log("SET OffsetY=" + value); Notify(); }
        }

        public double MaxWidth
        {
            get => _settings.MaxWidth;
            set { value = Math.Clamp(value, 220, 1600); if (_settings.MaxWidth == value) return; _settings.MaxWidth = value; Save(); IslandDiag.Log("SET MaxWidth=" + value); Notify(); }
        }

        public double Width
        {
            get => _settings.Width;
            set { value = Math.Clamp(value, 180, 1600); if (Math.Abs(_settings.Width - value) < 0.5) return; _settings.Width = value; Save(); IslandDiag.Log("SET Width=" + value); Notify(); }
        }

        public bool ExpandUp
        {
            get => _settings.ExpandUp;
            set { if (_settings.ExpandUp == value) return; _settings.ExpandUp = value; Save(); IslandDiag.Log("SET ExpandUp=" + value); Notify(); }
        }

        public bool ExpandOnHover
        {
            get => _settings.ExpandOnHover;
            set { if (_settings.ExpandOnHover == value) return; _settings.ExpandOnHover = value; Save(); IslandDiag.Log("SET ExpandOnHover=" + value); Notify(); }
        }

        public double CornerRadius
        {
            get => _settings.CornerRadius;
            set { value = Math.Clamp(value, 8, 32); if (_settings.CornerRadius == value) return; _settings.CornerRadius = value; Save(); IslandDiag.Log("SET CornerRadius=" + value); Notify(); }
        }

        public double BgOpacity
        {
            get => _settings.BgOpacity;
            set { value = Math.Clamp(value, 0.2, 1.0); if (Math.Abs(_settings.BgOpacity - value) < 0.001) return; _settings.BgOpacity = value; Save(); IslandDiag.Log("SET BgOpacity=" + value); Notify(); }
        }

        public bool PinToTaskbar
        {
            get => _settings.PinToTaskbar;
            set { if (_settings.PinToTaskbar == value) return; _settings.PinToTaskbar = value; Save(); IslandDiag.Log("SET PinToTaskbar=" + value); Notify(); }
        }

        public bool BarHideFullscreen
        {
            get => _settings.BarHideFullscreen;
            set { if (_settings.BarHideFullscreen == value) return; _settings.BarHideFullscreen = value; Save(); IslandDiag.Log("SET BarHideFullscreen=" + value); Notify(); }
        }

        public double GetWidgetHeight(string gadgetType)
        {
            if (string.IsNullOrEmpty(gadgetType)) return 260;
            foreach (var kv in _settings.WidgetHeights)
                if (string.Equals(kv.Key, gadgetType, StringComparison.OrdinalIgnoreCase))
                    return Math.Clamp(kv.Value, 120, 700);
            return 260;
        }

        public void SetWidgetHeight(string gadgetType, double height)
        {
            if (string.IsNullOrEmpty(gadgetType)) return;
            height = Math.Clamp(height, 120, 700);
            string? key = null;
            foreach (var k in _settings.WidgetHeights.Keys)
                if (string.Equals(k, gadgetType, StringComparison.OrdinalIgnoreCase)) { key = k; break; }
            double cur = key == null ? 260 : _settings.WidgetHeights[key];
            if (Math.Abs(cur - height) < 0.5) return;
            _settings.WidgetHeights[gadgetType] = height;
            Save(); IslandDiag.Log($"SET WidgetHeight {gadgetType}={height:0}"); Notify();
        }

        public void SetBarPosition(double left, double top)
        {
            _settings.BarLeft = left;
            _settings.BarTop = top;
            Save(); IslandDiag.Log($"SET BarPos=({left:0},{top:0})"); Notify();
        }

        public int HoverOpenDelayMs
        {
            get => _settings.HoverOpenDelayMs;
            set { value = Math.Clamp(value, 0, 1000); if (_settings.HoverOpenDelayMs == value) return; _settings.HoverOpenDelayMs = value; Save(); IslandDiag.Log("SET HoverOpenDelayMs=" + value); Notify(); }
        }

        public int HoverCloseDelayMs
        {
            get => _settings.HoverCloseDelayMs;
            set { value = Math.Clamp(value, 100, 2000); if (_settings.HoverCloseDelayMs == value) return; _settings.HoverCloseDelayMs = value; Save(); IslandDiag.Log("SET HoverCloseDelayMs=" + value); Notify(); }
        }

        public int ExpandDurationMs
        {
            get => _settings.ExpandDurationMs;
            set { value = Math.Clamp(value, 100, 800); if (_settings.ExpandDurationMs == value) return; _settings.ExpandDurationMs = value; Save(); IslandDiag.Log("SET ExpandDurationMs=" + value); Notify(); }
        }

        public int ExpandFadeMs
        {
            get => _settings.ExpandFadeMs;
            set { value = Math.Clamp(value, 50, 500); if (_settings.ExpandFadeMs == value) return; _settings.ExpandFadeMs = value; Save(); IslandDiag.Log("SET ExpandFadeMs=" + value); Notify(); }
        }

        public bool IsPinned(string gadgetType) =>
            _settings.PinnedWidgets.Any(g => string.Equals(g, gadgetType, StringComparison.OrdinalIgnoreCase));

        public void SetPinned(string gadgetType, bool pinned)
        {
            bool has = IsPinned(gadgetType);
            if (has == pinned) return;
            if (pinned) _settings.PinnedWidgets.Add(gadgetType);
            else _settings.PinnedWidgets.RemoveAll(g => string.Equals(g, gadgetType, StringComparison.OrdinalIgnoreCase));
            _settings.SelectedIndex = _settings.PinnedWidgets.Count == 0
                ? 0
                : Math.Clamp(_settings.SelectedIndex, 0, _settings.PinnedWidgets.Count - 1);
            Save(); IslandDiag.Log($"SET pin {gadgetType}={pinned} list=[{string.Join(",", _settings.PinnedWidgets)}]"); Notify();
        }

        public void MovePinned(string gadgetType, int delta)
        {
            int i = _settings.PinnedWidgets.FindIndex(g => string.Equals(g, gadgetType, StringComparison.OrdinalIgnoreCase));
            int j = i + delta;
            if (i < 0 || j < 0 || j >= _settings.PinnedWidgets.Count) return;
            (_settings.PinnedWidgets[i], _settings.PinnedWidgets[j]) = (_settings.PinnedWidgets[j], _settings.PinnedWidgets[i]);
            Save(); IslandDiag.Log($"SET order [{string.Join(",", _settings.PinnedWidgets)}]"); Notify();
        }

        public void SetCustomPosition(double left, double top)
        {
            _settings.CustomLeft = Math.Max(0, left);
            _settings.CustomTop = Math.Max(0, top);
            Save(); IslandDiag.Log($"SET CustomPos=({left:0},{top:0})"); Notify();
        }

        public Brush BackgroundBrush => BrushOf(CurrentTheme.Background);
        public Brush BorderBrush => BrushOf(CurrentTheme.Border);
        public Brush ForegroundBrush => BrushOf(CurrentTheme.Foreground);
        public Brush MutedBrush => BrushOf(CurrentTheme.Muted);
        public Brush AccentBrush => BrushOf(CurrentTheme.Accent);

        private static Brush BrushOf(string hex)
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { return Brushes.Black; }
        }
    }
}
