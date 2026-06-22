using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using Palisades.Models;

namespace Palisades.Services
{
    public class AutoSortManager
    {
        private static AutoSortManager? _instance;
        public static AutoSortManager Instance => _instance ??= new AutoSortManager();

        private FileSystemWatcher? _watcher;
        private DispatcherTimer? _debounceTimer;
        private readonly HashSet<string> _pendingFiles = new(StringComparer.OrdinalIgnoreCase);
        private bool _isRunning;

        public event Action<string>? ShortcutSorted;
        public event Action<string>? NewShortcutDetected;

        private AutoSortManager() { }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;

            try
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!Directory.Exists(desktopPath)) return;

                _watcher = new FileSystemWatcher(desktopPath)
                {
                    EnableRaisingEvents = true,
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite
                };

                _watcher.Created += OnFileCreated;
                _watcher.Renamed += OnFileRenamed;

                // Debounce timer: batch rapid events together
                _debounceTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1500)
                };
                _debounceTimer.Tick += OnDebounceTick;
            }
            catch { }
        }

        public void Stop()
        {
            _isRunning = false;
            _debounceTimer?.Stop();
            _debounceTimer = null;

            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }

            _pendingFiles.Clear();
        }

        private void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            EnqueueFile(e.FullPath);
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            EnqueueFile(e.FullPath);
        }

        private void EnqueueFile(string path)
        {
            bool isDir = false;
            try
            {
                if (Directory.Exists(path))
                    isDir = true;
            }
            catch { }

            if (!isDir)
            {
                string ext = Path.GetExtension(path);
                if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                    return;
            }

            _pendingFiles.Add(path);
            _debounceTimer?.Stop();
            _debounceTimer?.Start();
        }

        private void OnDebounceTick(object? sender, EventArgs e)
        {
            _debounceTimer?.Stop();

            if (_pendingFiles.Count == 0) return;

            var files = _pendingFiles.ToList();
            _pendingFiles.Clear();

            // Check if a global target container is configured
            ContainerModel? targetContainer = null;
            var def = ContainerManager.Instance.LoadDefaults();
            if (def != null && !string.IsNullOrEmpty(def.AutoSortTargetIdentifier))
            {
                targetContainer = ContainerManager.Instance.Containers
                    .FirstOrDefault(c => c.Identifier == def.AutoSortTargetIdentifier);
            }

            int sorted = 0;
            bool refreshUnassigned = targetContainer == null;

            foreach (var file in files)
            {
                bool isDir = false;
                try { isDir = Directory.Exists(file); } catch { }

                if (!File.Exists(file) && !isDir) continue;

                try { NewShortcutDetected?.Invoke(file); }
                catch { }

                var containers = ContainerManager.Instance.Containers;
                ContainerModel? target = null;

                if (targetContainer != null)
                {
                    target = targetContainer;
                }
                else
                {
                    string? category = ContainerManager.GetFileCategory(file);
                    if (category == null)
                    {
                        refreshUnassigned = true;
                        continue;
                    }

                    target = containers.FirstOrDefault(c =>
                        c.IsVisible && c.AutoSortCategories.Contains(category, StringComparer.OrdinalIgnoreCase));
                }

                if (target == null)
                {
                    refreshUnassigned = true;
                    continue;
                }

                ShortcutItem? item = null;
                try
                {
                    if (isDir)
                    {
                        var dir = new DirectoryInfo(file);
                        item = new ShortcutItem
                        {
                            Name = dir.Name,
                            TargetPath = dir.FullName,
                            IconPath = dir.FullName,
                            ShortcutPath = dir.FullName,
                            WorkingDirectory = dir.Parent?.FullName ?? ""
                        };
                    }
                    else if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                        item = ShortcutItem.FromLnk(file);
                    else if (file.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                        item = ShortcutItem.FromUrl(file);
                }
                catch { }

                if (item != null)
                {
                    target.Shortcuts.Add(item);
                    ContainerManager.Instance.Save();
                    sorted++;

                    try { ShortcutSorted?.Invoke($"{item.Name} → {target.Name}"); }
                    catch { }
                }
            }

            if (refreshUnassigned)
                ContainerManager.Instance.RefreshUnassignedShortcuts();
        }
    }
}
