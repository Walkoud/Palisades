using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Palisades.Models;
using Palisades.Plugins;

namespace Palisades.Services
{
    public class SnapshotManager
    {
        private static SnapshotManager? _instance;
        public static SnapshotManager Instance => _instance ??= new SnapshotManager();

        private readonly string _savePath;
        private List<SnapshotModel> _snapshots = new();

        public IReadOnlyList<SnapshotModel> Snapshots => _snapshots.AsReadOnly();
        public event Action? SnapshotsChanged;

        private SnapshotManager()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _savePath = Path.Combine(appData, "Palisades", "snapshots.json");
            Directory.CreateDirectory(Path.GetDirectoryName(_savePath)!);
            Load();
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_savePath))
                {
                    var json = File.ReadAllText(_savePath);
                    var loaded = JsonConvert.DeserializeObject<List<SnapshotModel>>(json);
                    if (loaded != null)
                        _snapshots = loaded;
                }
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                var json = JsonConvert.SerializeObject(_snapshots, Formatting.Indented);
                File.WriteAllText(_savePath, json);
            }
            catch { }
        }

        public static Func<string?>? ScreenshotCaptureCallback { get; set; }

        public SnapshotModel CreateSnapshot(string name, string type = "Manual")
        {
            var containers = ContainerManager.Instance.Containers
                .Select(c => DeepCopyContainer(c))
                .ToList();

            var notes = ContainerManager.Instance.LoadNotes()
                .Select(n => DeepCopy(n))
                .ToList();

            var gadgets = PluginService.Instance.LoadGadgets()
                .Select(g => DeepCopy(g))
                .ToList();

            var theme = ThemeService.Instance.Settings;

            var snapshot = new SnapshotModel
            {
                Name = name,
                Type = type,
                CreatedAt = DateTime.Now,
                Containers = containers,
                Notes = notes,
                Gadgets = gadgets,
                IsDarkMode = theme.IsDarkMode,
                SelectedTheme = theme.SelectedTheme,
                GlobalOpacity = theme.GlobalOpacity
            };

            if (ScreenshotCaptureCallback != null)
            {
                string? screenshotsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Palisades", "snapshots");
                Directory.CreateDirectory(screenshotsDir);

                string filePath = Path.Combine(screenshotsDir, $"{snapshot.Identifier}.png");
                string? result = ScreenshotCaptureCallback();
                if (result != null)
                {
                    File.Copy(result, filePath, true);
                    try { File.Delete(result); } catch { }
                    snapshot.ScreenshotPath = filePath;
                }
            }

            _snapshots.Add(snapshot);
            Save();
            SnapshotsChanged?.Invoke();
            return snapshot;
        }

        public void DeleteSnapshot(string identifier)
        {
            var snap = _snapshots.FirstOrDefault(s => s.Identifier == identifier);
            if (snap != null && !string.IsNullOrEmpty(snap.ScreenshotPath))
            {
                try { File.Delete(snap.ScreenshotPath); } catch { }
            }
            _snapshots.RemoveAll(s => s.Identifier == identifier);
            Save();
            SnapshotsChanged?.Invoke();
        }

        public void RenameSnapshot(string identifier, string newName)
        {
            var snap = _snapshots.FirstOrDefault(s => s.Identifier == identifier);
            if (snap != null)
            {
                snap.Name = newName;
                Save();
                SnapshotsChanged?.Invoke();
            }
        }

        public SnapshotModel? RestoreSnapshot(string identifier)
        {
            var snap = _snapshots.FirstOrDefault(s => s.Identifier == identifier);
            if (snap == null) return null;

            // Deep copy saved containers and restore them
            var restored = snap.Containers
                .Select(c => DeepCopyContainer(c))
                .ToList();
            ContainerManager.Instance.RestoreAll(restored);

            // Restore notes (clear if snapshot has none)
            var restoredNotes = snap.Notes?.Select(n => DeepCopy(n)).ToList() ?? new List<NoteItem>();
            ContainerManager.Instance.SaveNotes(restoredNotes);

            // Restore gadgets (clear if snapshot has none)
            var restoredGadgets = snap.Gadgets?.Select(g => DeepCopy(g)).ToList() ?? new List<PluginGadgetItem>();
            PluginService.Instance.SaveGadgets(restoredGadgets);

            // Restore theme
            var theme = ThemeService.Instance;
            theme.IsDarkMode = snap.IsDarkMode;
            theme.SelectedTheme = snap.SelectedTheme;
            theme.GlobalOpacity = snap.GlobalOpacity;

            return snap;
        }

        private static ContainerModel DeepCopyContainer(ContainerModel container)
        {
            var json = JsonConvert.SerializeObject(container);
            return JsonConvert.DeserializeObject<ContainerModel>(json)!;
        }

        private static T DeepCopy<T>(T obj)
        {
            var json = JsonConvert.SerializeObject(obj);
            return JsonConvert.DeserializeObject<T>(json)!;
        }
    }
}
