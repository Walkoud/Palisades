using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;
using Palisades.Models;

namespace Palisades.Services
{
    public class ThemePreset
    {
        public string Name { get; set; } = "";
        public string HeaderColor { get; set; } = "#C8000000";
        public string BodyColor { get; set; } = "#78000000";
        public string TitleColor { get; set; } = "#FFFFFFFF";
        public string LabelsColor { get; set; } = "#FFFFFFFF";
    }

    public class ThemeService
    {
        private static ThemeService? _instance;
        public static ThemeService Instance => _instance ??= new ThemeService();

        private readonly string _settingsPath;
        private ThemeSettings _settings;
        private ResourceDictionary? _currentDynamicThemeDict;

        public event Action? ThemeChanged;

        public ThemeSettings Settings => _settings;

        public static ThemePreset[] Presets { get; } =
        {
            new() { Name = "Dark",       HeaderColor = "#C8000000", BodyColor = "#78000000", TitleColor = "#FFFFFFFF", LabelsColor = "#FFFFFFFF" },
            new() { Name = "Light",      HeaderColor = "#C8FFFFFF", BodyColor = "#B0FFFFFF", TitleColor = "#FF000000", LabelsColor = "#FF000000" },
            new() { Name = "Frost",      HeaderColor = "#C81E3A5F", BodyColor = "#781E3A5F", TitleColor = "#FFFFFFFF", LabelsColor = "#FFCCCCCC" },
            new() { Name = "Glass",      HeaderColor = "#40FFFFFF", BodyColor = "#20FFFFFF", TitleColor = "#FFFFFFFF", LabelsColor = "#FFCCCCCC" },
            new() { Name = "Midnight",   HeaderColor = "#C8151525", BodyColor = "#78151525", TitleColor = "#FFFFFFFF", LabelsColor = "#FFAAAACC" },
            new() { Name = "Amber",      HeaderColor = "#C85F3A1E", BodyColor = "#783A1E0A", TitleColor = "#FFFFFFFF", LabelsColor = "#FFDDCCAA" },
            new() { Name = "Forest",     HeaderColor = "#C81E4A3A", BodyColor = "#781E4A3A", TitleColor = "#FFFFFFFF", LabelsColor = "#FFAACCAA" },
            new() { Name = "Plum",       HeaderColor = "#C83A1E5F", BodyColor = "#783A1E5F", TitleColor = "#FFFFFFFF", LabelsColor = "#FFCCAADD" },
        };

        public bool IsDarkMode
        {
            get => _settings.IsDarkMode;
            set
            {
                _settings.IsDarkMode = value;
                Save();
                ThemeChanged?.Invoke();
            }
        }

        public string SelectedTheme
        {
            get => _settings.SelectedTheme;
            set
            {
                _settings.SelectedTheme = value;
                Save();
                ApplyTheme(value);
                ThemeChanged?.Invoke();
            }
        }

        /// <summary>Global opacity percentage, 0-100 range.</summary>
        public double GlobalOpacity
        {
            get => _settings.GlobalOpacity;
            set
            {
                _settings.GlobalOpacity = Math.Clamp(value, 0.0, 100.0);
                Save();
                ThemeChanged?.Invoke();
            }
        }

        public ThemePreset? CurrentPreset =>
            Presets.FirstOrDefault(p => p.Name == _settings.SelectedTheme) ?? Presets[0];

        public string GuiBackgroundColor
        {
            get => _settings.GuiBackgroundColor;
            set
            {
                _settings.GuiBackgroundColor = value;
                Save();
                ApplyGuiTheme();
                ThemeChanged?.Invoke();
            }
        }

        public string GuiTextColor
        {
            get => _settings.GuiTextColor;
            set
            {
                _settings.GuiTextColor = value;
                Save();
                ApplyGuiTheme();
                ThemeChanged?.Invoke();
            }
        }

        public void ApplyGuiTheme()
        {
            if (Application.Current == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    var bg = (Color)ColorConverter.ConvertFromString(_settings.GuiBackgroundColor);
                    var fg = (Color)ColorConverter.ConvertFromString(_settings.GuiTextColor);

                    // Original Arctic Shelter palette values
                    var card = Color.FromRgb(0x15, 0x1B, 0x22);
                    var border = Color.FromRgb(0x22, 0x2A, 0x35);
                    var accent = Color.FromRgb(0x7D, 0xD3, 0xFC);

                    // If bg/fg differ from defaults, derive card/border/accent adaptively
                    var defaultBg = Color.FromRgb(0x11, 0x16, 0x1B);
                    var defaultFg = Color.FromRgb(0xE2, 0xF1, 0xFF);
                    if (bg != defaultBg || fg != defaultFg)
                    {
                        card = Color.FromRgb(
                            (byte)Math.Clamp(bg.R + 10, 0, 255),
                            (byte)Math.Clamp(bg.G + 10, 0, 255),
                            (byte)Math.Clamp(bg.B + 10, 0, 255));
                        border = Color.FromRgb(
                            (byte)Math.Clamp(bg.R + 25, 0, 255),
                            (byte)Math.Clamp(bg.G + 25, 0, 255),
                            (byte)Math.Clamp(bg.B + 25, 0, 255));
                        accent = Color.FromRgb(
                            (byte)Math.Max(fg.R, (byte)120),
                            (byte)Math.Max(fg.G, (byte)120),
                            (byte)Math.Max(fg.B, (byte)120));
                    }

                    Application.Current.Resources["GuiBackgroundMainBrush"] = new SolidColorBrush(bg);
                    Application.Current.Resources["GuiBackgroundCardBrush"] = new SolidColorBrush(card);
                    Application.Current.Resources["GuiTextForegroundBrush"] = new SolidColorBrush(fg);
                    Application.Current.Resources["GuiBorderBrush"] = new SolidColorBrush(border);
                    Application.Current.Resources["GuiAccentBrush"] = new SolidColorBrush(accent);
                }
                catch { }
            });
        }

        private ThemeService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _settingsPath = Path.Combine(appData, "Palisades", "theme.json");
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            _settings = LoadSettings();

            EnsureThemesDirectory();
            ApplyTheme(_settings.SelectedTheme);
            ApplyGuiTheme();
        }

        private ThemeSettings LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    return JsonConvert.DeserializeObject<ThemeSettings>(json) ?? new ThemeSettings();
                }
            }
            catch { }
            return new ThemeSettings();
        }

        public void Save()
        {
            try
            {
                var json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
                File.WriteAllText(_settingsPath, json);
            }
            catch { }
        }

        public void ResetToDefaults()
        {
            _settings = new ThemeSettings();
            Save();
            ApplyTheme(_settings.SelectedTheme);
            ApplyGuiTheme();
            ThemeChanged?.Invoke();
        }

        public void ApplyPresetToContainer(ContainerModel model)
        {
            var preset = CurrentPreset;
            if (preset == null) return;
            model.HeaderColor = preset.HeaderColor;
            model.BodyColor = preset.BodyColor;
            model.TitleColor = preset.TitleColor;
            model.LabelsColor = preset.LabelsColor;
        }

        public string GetHeaderColor() => _settings.IsDarkMode ? "#C8000000" : "#C8FFFFFF";
        public string GetBodyColor() => _settings.IsDarkMode ? "#78000000" : "#B0FFFFFF";
        public string GetTitleColor() => _settings.IsDarkMode ? "#FFFFFFFF" : "#FF000000";
        public string GetLabelsColor() => _settings.IsDarkMode ? "#FFFFFFFF" : "#FF000000";

        // --- DYNAMIC THEME SYSTEM ---

        public string[] GetAvailableThemeNames()
        {
            var list = new List<string>(Presets.Select(p => p.Name));
            try
            {
                string themesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes");
                if (Directory.Exists(themesDir))
                {
                    foreach (var file in Directory.GetFiles(themesDir, "*.xaml"))
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        if (!list.Contains(name))
                            list.Add(name);
                    }
                }
            }
            catch { }
            return list.ToArray();
        }

        public void ApplyTheme(string themeName)
        {
            string themesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes");
            string xamlPath = Path.Combine(themesDir, themeName + ".xaml");

            Console.WriteLine($"[ThemeService] Applying theme: {themeName} (Path: {xamlPath})");

            if (File.Exists(xamlPath))
            {
                try
                {
                    var dict = new ResourceDictionary { Source = new Uri(xamlPath, UriKind.Absolute) };
                    SwapThemeDictionary(dict);
                    Console.WriteLine("[ThemeService] Dynamic theme loaded successfully!");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ThemeService] ERROR loading dynamic theme: {ex.Message}");
                    Console.WriteLine(ex.StackTrace);
                    // Fallback to default Dark preset if theme fails to load
                    var defaultPreset = Presets[0];
                    ApplyPresetAsDynamicResources(defaultPreset);
                }
            }
            else
            {
                Console.WriteLine($"[ThemeService] Theme file not found, applying preset: {themeName}");
                var preset = Presets.FirstOrDefault(p => p.Name.Equals(themeName, StringComparison.OrdinalIgnoreCase)) ?? Presets[0];
                ApplyPresetAsDynamicResources(preset);
            }
        }

        private void SwapThemeDictionary(ResourceDictionary newDict)
        {
            if (Application.Current == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    var merged = Application.Current.Resources.MergedDictionaries;
                    if (_currentDynamicThemeDict != null)
                    {
                        merged.Remove(_currentDynamicThemeDict);
                    }
                    merged.Add(newDict);
                    _currentDynamicThemeDict = newDict;
                }
                catch { }
            });
        }

        private void ApplyPresetAsDynamicResources(ThemePreset preset)
        {
            var dict = new ResourceDictionary();

            var headerColor = (Color)ColorConverter.ConvertFromString(preset.HeaderColor);
            var bodyColor = (Color)ColorConverter.ConvertFromString(preset.BodyColor);
            var titleColor = (Color)ColorConverter.ConvertFromString(preset.TitleColor);
            var labelsColor = (Color)ColorConverter.ConvertFromString(preset.LabelsColor);

            dict["ContainerBackgroundBrush"] = new SolidColorBrush(bodyColor);
            dict["ContainerHeaderBrush"] = new SolidColorBrush(headerColor);
            dict["ContainerTitleForeground"] = new SolidColorBrush(titleColor);
            dict["ContainerLabelsForeground"] = new SolidColorBrush(labelsColor);
            dict["ContainerCornerRadius"] = new CornerRadius(12);
            dict["ContainerBorderThickness"] = new Thickness(1);

            if (preset.Name.Equals("Light", StringComparison.OrdinalIgnoreCase))
            {
                dict["ContainerBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x25, 0x00, 0x00, 0x00));
                dict["ContainerBorderBrushHover"] = new SolidColorBrush(Color.FromArgb(0x65, 0x00, 0x00, 0x00));
                dict["ContainerHeaderBrushHover"] = new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE7));
            }
            else
            {
                dict["ContainerBorderBrush"] = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF));
                dict["ContainerBorderBrushHover"] = new SolidColorBrush(Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF));
                dict["ContainerHeaderBrushHover"] = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));
            }

            SwapThemeDictionary(dict);
        }

        private void EnsureThemesDirectory()
        {
            try
            {
                string themesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes");
                Directory.CreateDirectory(themesDir);

                // Proactively create dirt.png and obsidian.png for Minecraft theme
                string dirtPath = Path.Combine(themesDir, "dirt.png");
                string obsidianPath = Path.Combine(themesDir, "obsidian.png");

                if (!File.Exists(dirtPath))
                    GenerateDirtTexture(dirtPath);

                if (!File.Exists(obsidianPath))
                    GenerateObsidianTexture(obsidianPath);
            }
            catch { }
        }

        private void GenerateDirtTexture(string path)
        {
            int size = 64;
            int pixelSize = 4; // 16x16 grid scaled to 64x64
            var wb = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
            var pixels = new byte[size * size * 4];

            int[] pattern = {
                0, 0, 2, 0,  0, 1, 0, 0,  2, 0, 0, 0,  0, 0, 1, 0,
                0, 2, 2, 2,  0, 1, 1, 0,  2, 2, 0, 0,  0, 1, 1, 0,
                2, 2, 0, 2,  2, 0, 1, 0,  0, 2, 2, 0,  2, 0, 1, 1,
                0, 2, 0, 0,  0, 0, 0, 0,  0, 0, 2, 2,  0, 0, 0, 1,
                
                0, 0, 0, 1,  0, 2, 0, 0,  1, 0, 0, 2,  2, 0, 0, 0,
                1, 1, 0, 1,  2, 2, 2, 0,  1, 1, 0, 0,  2, 2, 0, 0,
                0, 1, 0, 0,  0, 2, 0, 2,  0, 1, 0, 0,  0, 2, 2, 0,
                0, 0, 0, 0,  0, 0, 0, 2,  0, 0, 0, 0,  0, 0, 2, 0,
                
                0, 0, 2, 0,  0, 0, 1, 0,  0, 2, 2, 0,  0, 1, 0, 0,
                2, 2, 2, 0,  0, 1, 1, 1,  2, 2, 0, 0,  1, 1, 1, 0,
                0, 2, 0, 0,  2, 1, 0, 1,  0, 2, 0, 0,  0, 1, 0, 2,
                0, 0, 0, 2,  2, 0, 0, 0,  0, 0, 0, 2,  2, 0, 0, 2,
                
                1, 0, 0, 2,  0, 0, 0, 0,  1, 0, 0, 0,  0, 0, 0, 0,
                1, 1, 0, 0,  0, 2, 2, 0,  1, 1, 0, 2,  2, 2, 0, 0,
                0, 1, 1, 0,  2, 2, 0, 0,  0, 1, 0, 0,  2, 0, 0, 0,
                0, 0, 1, 0,  0, 2, 0, 0,  0, 0, 0, 0,  0, 0, 0, 0
            };

            for (int y = 0; y < size; y++)
            {
                int py = y / pixelSize;
                for (int x = 0; x < size; x++)
                {
                    int px = x / pixelSize;
                    int index = pattern[py * 16 + px];
                    
                    byte r = 87, g = 59, b = 43; // base brown
                    if (index == 1) { r = 115; g = 83; b = 63; } // light brown
                    else if (index == 2) { r = 58; g = 38; b = 27; } // dark brown

                    int offset = (y * size + x) * 4;
                    pixels[offset] = b;
                    pixels[offset + 1] = g;
                    pixels[offset + 2] = r;
                    pixels[offset + 3] = 255;
                }
            }

            wb.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
            SaveBitmapToPng(wb, path);
        }

        private void GenerateObsidianTexture(string path)
        {
            int size = 64;
            int pixelSize = 4;
            var wb = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
            var pixels = new byte[size * size * 4];

            int[] pattern = {
                1, 1, 0, 0,  2, 2, 0, 1,  1, 1, 0, 0,  2, 2, 0, 1,
                1, 0, 0, 2,  3, 2, 0, 0,  1, 0, 0, 2,  3, 2, 0, 0,
                0, 0, 2, 3,  2, 0, 0, 0,  0, 0, 2, 3,  2, 0, 0, 0,
                0, 2, 2, 2,  0, 0, 1, 1,  0, 2, 2, 2,  0, 0, 1, 1,
                
                2, 2, 0, 0,  1, 1, 0, 0,  2, 2, 0, 0,  1, 1, 0, 0,
                2, 0, 0, 1,  1, 0, 0, 2,  2, 0, 0, 1,  1, 0, 0, 2,
                0, 0, 1, 1,  0, 0, 2, 3,  0, 0, 1, 1,  0, 0, 2, 3,
                0, 2, 1, 0,  0, 2, 2, 2,  0, 2, 1, 0,  0, 2, 2, 2,
                
                1, 1, 0, 0,  2, 2, 0, 1,  1, 1, 0, 0,  2, 2, 0, 1,
                1, 0, 0, 2,  3, 2, 0, 0,  1, 0, 0, 2,  3, 2, 0, 0,
                0, 0, 2, 3,  2, 0, 0, 0,  0, 0, 2, 3,  2, 0, 0, 0,
                0, 2, 2, 2,  0, 0, 1, 1,  0, 2, 2, 2,  0, 0, 1, 1,
                
                2, 2, 0, 0,  1, 1, 0, 0,  2, 2, 0, 0,  1, 1, 0, 0,
                2, 0, 0, 1,  1, 0, 0, 2,  2, 0, 0, 1,  1, 0, 0, 2,
                0, 0, 1, 1,  0, 0, 2, 3,  0, 0, 1, 1,  0, 0, 2, 3,
                0, 2, 1, 0,  0, 2, 2, 2,  0, 2, 1, 0,  0, 2, 2, 2
            };

            for (int y = 0; y < size; y++)
            {
                int py = y / pixelSize;
                for (int x = 0; x < size; x++)
                {
                    int px = x / pixelSize;
                    int index = pattern[py * 16 + px];
                    
                    byte r = 22, g = 13, b = 29; // base dark purple
                    if (index == 1) { r = 10; g = 5; b = 15; }
                    else if (index == 2) { r = 35; g = 22; b = 47; }
                    else if (index == 3) { r = 60; g = 38; b = 80; }

                    int offset = (y * size + x) * 4;
                    pixels[offset] = b;
                    pixels[offset + 1] = g;
                    pixels[offset + 2] = r;
                    pixels[offset + 3] = 255;
                }
            }

            wb.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
            SaveBitmapToPng(wb, path);
        }

        private void SaveBitmapToPng(BitmapSource source, string path)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = File.OpenWrite(path);
            encoder.Save(stream);
        }
    }

    public class ThemeSettings
    {
        public bool IsDarkMode { get; set; } = true;
        /// <summary>Opacity percentage, 0-100.</summary>
        public double GlobalOpacity { get; set; } = 0.0;
        public string AccentColor { get; set; } = "#FF4488FF";
        public bool AutoHideTaskbar { get; set; }
        public bool ShowContainerTitles { get; set; } = true;
        public int AnimationSpeed { get; set; } = 200;
        public bool EnableAnimations { get; set; } = true;
        public string SelectedTheme { get; set; } = "Dark";
        public string GuiBackgroundColor { get; set; } = "#11161B";
        public string GuiTextColor { get; set; } = "#E2F1FF";
    }
}
