using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using Newtonsoft.Json;
using Palisades.Models;
using Palisades.Plugins;
using Palisades.ViewModels;

namespace Palisades.Services
{
    public class PluginWrapper
    {
        public IPlugin Plugin { get; set; } = null!;
        public bool IsEnabled { get; set; }
        public bool IsBuiltIn { get; set; }
        public PluginContext? Context { get; set; }
    }

    public class PluginService
    {
        private static PluginService? _instance;
        public static PluginService Instance => _instance ??= new PluginService();

        private readonly string _pluginsConfigPath;
        private readonly string _gadgetsConfigPath;
        private readonly string _externalPluginsDir;
        private readonly List<PluginWrapper> _plugins = new List<PluginWrapper>();

        public event Action? PluginsChanged;
        public event Action? GadgetsChanged;

        public IReadOnlyList<PluginWrapper> Plugins => _plugins.AsReadOnly();

        private PluginService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(appData, "Palisades");
            _pluginsConfigPath = Path.Combine(dir, "plugins.json");
            _gadgetsConfigPath = Path.Combine(dir, "plugingadgets.json");
            _externalPluginsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins");

            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(_externalPluginsDir);
        }

        public void Initialize(MainViewModel vm, Window overlayWindow)
        {
            _plugins.Clear();

            // 1. Load built-in plugins (we will register them here)
            // They will be implemented in the Palisades.Plugins namespace.
            RegisterPlugin(new ClockGadgetPlugin(), true);
            RegisterPlugin(new SystemMonitorPlugin(), true);
            RegisterPlugin(new PostItGadgetPlugin(), true);

            // 2. Load external plugin assemblies
            LoadExternalPlugins();

            // 3. Load saved statuses from plugins.json
            LoadSettings();

            // 4. Initialize enabled plugins
            foreach (var wrapper in _plugins.Where(w => w.IsEnabled))
            {
                try
                {
                    wrapper.Context = new PluginContext(vm, overlayWindow);
                    wrapper.Plugin.OnLoad(wrapper.Context);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PluginService] Failed to load plugin {wrapper.Plugin.Id}: {ex.Message}");
                    wrapper.IsEnabled = false;
                }
            }

            PluginsChanged?.Invoke();
        }

        private void RegisterPlugin(IPlugin plugin, bool isBuiltIn)
        {
            if (_plugins.Any(p => p.Plugin.Id == plugin.Id)) return;
            _plugins.Add(new PluginWrapper
            {
                Plugin = plugin,
                IsEnabled = isBuiltIn,
                IsBuiltIn = isBuiltIn
            });
        }

        private void LoadExternalPlugins()
        {
            try
            {
                if (!Directory.Exists(_externalPluginsDir)) return;
                var dllFiles = Directory.GetFiles(_externalPluginsDir, "*.dll", SearchOption.AllDirectories);

                foreach (var file in dllFiles)
                {
                    try
                    {
                        var assembly = Assembly.LoadFrom(file);
                        foreach (var type in assembly.GetTypes())
                        {
                            if (typeof(IPlugin).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
                            {
                                if (Activator.CreateInstance(type) is IPlugin plugin)
                                {
                                    RegisterPlugin(plugin, false);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[PluginService] Failed to load assembly {file}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PluginService] Exception loading external plugins: {ex.Message}");
            }
        }

        public void TogglePlugin(string id, bool enable, MainViewModel vm, Window overlayWindow)
        {
            var wrapper = _plugins.FirstOrDefault(p => p.Plugin.Id == id);
            if (wrapper == null || wrapper.IsEnabled == enable) return;

            wrapper.IsEnabled = enable;
            SaveSettings();

            try
            {
                if (enable)
                {
                    wrapper.Context = new PluginContext(vm, overlayWindow);
                    wrapper.Plugin.OnLoad(wrapper.Context);
                }
                else
                {
                    wrapper.Plugin.OnUnload();
                    wrapper.Context = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PluginService] Error toggling plugin {id}: {ex.Message}");
            }

            PluginsChanged?.Invoke();
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_pluginsConfigPath))
                {
                    var json = File.ReadAllText(_pluginsConfigPath);
                    var dict = JsonConvert.DeserializeObject<Dictionary<string, bool>>(json);
                    if (dict != null)
                    {
                        foreach (var wrapper in _plugins)
                        {
                            if (dict.TryGetValue(wrapper.Plugin.Id, out bool isEnabled))
                            {
                                wrapper.IsEnabled = isEnabled;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        public void SaveSettings()
        {
            try
            {
                var dict = _plugins.ToDictionary(p => p.Plugin.Id, p => p.IsEnabled);
                var json = JsonConvert.SerializeObject(dict, Formatting.Indented);
                File.WriteAllText(_pluginsConfigPath, json);
            }
            catch { }
        }

        // --- GADGET (WIDGET) PERSISTENCE ---

        public List<PluginGadgetItem> LoadGadgets()
        {
            try
            {
                if (!File.Exists(_gadgetsConfigPath)) return new List<PluginGadgetItem>();
                var json = File.ReadAllText(_gadgetsConfigPath);
                return JsonConvert.DeserializeObject<List<PluginGadgetItem>>(json) ?? new List<PluginGadgetItem>();
            }
            catch
            {
                return new List<PluginGadgetItem>();
            }
        }

        public void SaveGadgets(List<PluginGadgetItem> gadgets)
        {
            try
            {
                var json = JsonConvert.SerializeObject(gadgets, Formatting.Indented);
                File.WriteAllText(_gadgetsConfigPath, json);
                GadgetsChanged?.Invoke();
            }
            catch { }
        }
    }
}
