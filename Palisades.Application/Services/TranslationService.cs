using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace Palisades.Services
{
    public class TranslationService : INotifyPropertyChanged
    {
        private static readonly TranslationService _instance = new();
        public static TranslationService Instance => _instance;

        private Dictionary<string, string> _translations = new();
        private string _currentCulture = "en";
        private string _basePath;

        public string CurrentCulture
        {
            get => _currentCulture;
            private set { _currentCulture = value; OnPropertyChanged(); }
        }

        public event Action? LanguageChanged;

        public TranslationService()
        {
            _basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Localization");
        }

        public void Initialize()
        {
            var saved = LoadSavedLanguage();
            SetLanguage(saved ?? "en");
        }

        public string this[string key]
        {
            get
            {
                if (_translations.TryGetValue(key, out var value))
                    return value;
                return $"[{key}]";
            }
        }

        public string Get(string key, string defaultValue = "")
        {
            if (_translations.TryGetValue(key, out var value))
                return value;
            return defaultValue;
        }

        public void SetLanguage(string culture)
        {
            _currentCulture = culture;
            LoadTranslations(culture);
            SaveLanguage(culture);
            OnPropertyChanged(string.Empty);
            OnPropertyChanged("Item");
            OnPropertyChanged("Item[]");
            LanguageChanged?.Invoke();
        }

        private void LoadTranslations(string culture)
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Palisades", "translation_debug.log");
            StringBuilder log = new StringBuilder();
            log.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] LoadTranslations started for culture: {culture}");
            log.AppendLine($"Base path: {_basePath}");

            try
            {
                string filePath = Path.Combine(_basePath, $"translations.{culture}.json");
                log.AppendLine($"Calculated filePath: {filePath}");
                log.AppendLine($"File exists: {File.Exists(filePath)}");

                if (!File.Exists(filePath))
                {
                    filePath = Path.Combine(_basePath, "translations.en.json");
                    log.AppendLine($"Fallback filePath: {filePath}");
                    log.AppendLine($"Fallback file exists: {File.Exists(filePath)}");
                }

                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    log.AppendLine($"JSON file read successfully, length: {json.Length}");
                    _translations = JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                        ?? new Dictionary<string, string>();
                    log.AppendLine($"Deserialized successfully, keys count: {_translations.Count}");
                }
                else
                {
                    _translations = new Dictionary<string, string>();
                    log.AppendLine("No translation file found, initialized empty dictionary.");
                }
            }
            catch (Exception ex)
            {
                _translations = new Dictionary<string, string>();
                log.AppendLine($"Exception occurred: {ex.ToString()}");
            }
            finally
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                    File.AppendAllText(logPath, log.ToString() + "\n");
                }
                catch {}
            }
        }

        private string? LoadSavedLanguage()
        {
            try
            {
                string appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palisades");
                string configFile = Path.Combine(appData, "language.json");
                if (File.Exists(configFile))
                {
                    var json = File.ReadAllText(configFile);
                    var data = JsonConvert.DeserializeAnonymousType(json, new { Language = "" });
                    return data?.Language;
                }
            }
            catch { }
            return null;
        }

        private void SaveLanguage(string culture)
        {
            try
            {
                string appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palisades");
                Directory.CreateDirectory(appData);
                string configFile = Path.Combine(appData, "language.json");
                File.WriteAllText(configFile, JsonConvert.SerializeObject(new { Language = culture }));
            }
            catch { }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
