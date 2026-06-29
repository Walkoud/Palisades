using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Newtonsoft.Json;
using System.IO;
using Palisades.Models;

namespace Palisades.Services
{
    public class ContainerManager
    {
        private static ContainerManager? _instance;
        public static ContainerManager Instance => _instance ??= new ContainerManager();

        private readonly string _savePath;
        private readonly string _defaultsPath;
        private readonly string _iconsPosPath;
        private readonly string _notesPath;
        private readonly List<ContainerModel> _containers = new();
        private const double MIN_SPACING = 10.0;

        public event Action? ContainersChanged;
        public event Action? UnassignedShortcutsChanged;

        public IReadOnlyList<ContainerModel> Containers => _containers.AsReadOnly();

        public ObservableCollection<ShortcutItem> UnassignedShortcuts { get; } = new();

        public Dictionary<string, List<DesktopIconPosition>> DesktopIconPositions { get; set; } = new();

        private ContainerManager()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(appData, "Palisades");
            _savePath = Path.Combine(dir, "containers.json");
            _defaultsPath = Path.Combine(dir, "container_defaults.json");
            _iconsPosPath = Path.Combine(dir, "desktop_icons_positions.json");
            _notesPath = Path.Combine(dir, "notes.json");
            Directory.CreateDirectory(dir);
            LoadDesktopIconPositions();
        }

        public void SaveDefaults(ContainerModel src)
        {
            try
            {
                var json = JsonConvert.SerializeObject(src, Formatting.Indented);
                File.WriteAllText(_defaultsPath, json);
            }
            catch { }
        }

        private void LoadDesktopIconPositions()
        {
            try
            {
                if (File.Exists(_iconsPosPath))
                {
                    var json = File.ReadAllText(_iconsPosPath);
                    var data = JsonConvert.DeserializeObject<DesktopIconPositionsData>(json);
                    if (data != null)
                        DesktopIconPositions = data.Positions;
                }
            }
            catch { }
        }

        public void SaveDesktopIconPositions()
        {
            try
            {
                var data = new DesktopIconPositionsData { Positions = DesktopIconPositions };
                var json = JsonConvert.SerializeObject(data, Formatting.Indented);
                File.WriteAllText(_iconsPosPath, json);
            }
            catch { }
        }

        public Point? GetDesktopIconPosition(string shortcutPath)
        {
            string sig = GetScreenSignature();
            if (DesktopIconPositions.TryGetValue(sig, out var list))
            {
                var entry = list.FirstOrDefault(p =>
                    p.ShortcutPath.Equals(shortcutPath, StringComparison.OrdinalIgnoreCase));
                if (entry != null)
                    return new Point(entry.X, entry.Y);
            }
            return null;
        }

        public void SetDesktopIconPosition(string shortcutPath, double x, double y)
        {
            string sig = GetScreenSignature();
            if (!DesktopIconPositions.TryGetValue(sig, out var list))
            {
                list = new List<DesktopIconPosition>();
                DesktopIconPositions[sig] = list;
            }
            var entry = list.FirstOrDefault(p =>
                p.ShortcutPath.Equals(shortcutPath, StringComparison.OrdinalIgnoreCase));
            if (entry != null)
            {
                entry.X = x;
                entry.Y = y;
            }
            else
            {
                list.Add(new DesktopIconPosition { ShortcutPath = shortcutPath, X = x, Y = y });
            }
            SaveDesktopIconPositions();
        }

        public void ClearDesktopIconPositions(string shortcutPath)
        {
            string sig = GetScreenSignature();
            if (DesktopIconPositions.TryGetValue(sig, out var list))
            {
                list.RemoveAll(p =>
                    p.ShortcutPath.Equals(shortcutPath, StringComparison.OrdinalIgnoreCase));
                SaveDesktopIconPositions();
            }
        }

        public ContainerModel? LoadDefaults()
        {
            try
            {
                if (!File.Exists(_defaultsPath)) return null;
                var json = File.ReadAllText(_defaultsPath);
                return JsonConvert.DeserializeObject<ContainerModel>(json);
            }
            catch { return null; }
        }

        public void ApplyDefaults(ContainerModel target)
        {
            var def = LoadDefaults();
            if (def != null)
                ApplyModelTo(target, def);
        }

        public void ApplyModelTo(ContainerModel target, ContainerModel source)
        {
            target.Opacity = source.Opacity;
            target.IdleOpacity = source.IdleOpacity;
            target.ActiveOpacity = source.ActiveOpacity;
            target.Width = source.Width;
            target.Height = source.Height;
            target.FullHeight = source.FullHeight;
            target.AutoHide = source.AutoHide;
            target.AutoHideDelayMs = source.AutoHideDelayMs;
            target.ShowTitle = source.ShowTitle;
            target.ShowBorder = source.ShowBorder;
            target.ShowCounter = source.ShowCounter;
            target.RoundedCorners = source.RoundedCorners;
            target.CornerRadius = source.CornerRadius;
            target.TitleFontFamily = source.TitleFontFamily;
            target.TitleFontSize = source.TitleFontSize;
            target.TitleAlignment = source.TitleAlignment;
            target.HeaderColor = source.HeaderColor;
            target.BodyColor = source.BodyColor;
            target.TitleColor = source.TitleColor;
            target.LabelsColor = source.LabelsColor;
            target.OpenOnDoubleClick = source.OpenOnDoubleClick;
            target.UseShellContextMenu = source.UseShellContextMenu;
            target.ShowShortcutArrow = source.ShowShortcutArrow;
            target.ShowRecycleBin = source.ShowRecycleBin;
            target.HeaderIconSize = source.HeaderIconSize;
            target.ShortcutIconSize = source.ShortcutIconSize;
            target.TwoLineShortcuts = source.TwoLineShortcuts;
            target.TitleHoverEffect = source.TitleHoverEffect;
            target.AnimationSpeedMs = source.AnimationSpeedMs;
            target.FilterEnabled = source.FilterEnabled;
            target.FilterType = source.FilterType;
            target.FilterPattern = source.FilterPattern;
            target.PrivateBoxAutoLockSeconds = source.PrivateBoxAutoLockSeconds;
            target.IsCurtainMode = source.IsCurtainMode;
            target.CurtainHeaderMode = source.CurtainHeaderMode;
            target.CurtainOpenWidth = source.CurtainOpenWidth;
            target.CurtainOpenHeight = source.CurtainOpenHeight;
            target.CurtainShortcutIconSize = source.CurtainShortcutIconSize;
            target.CurtainDirection = source.CurtainDirection;
            target.IsLocked = source.IsLocked;
            target.CollapsedHeight = source.CollapsedHeight;
            target.AutoHideOnEdge = source.AutoHideOnEdge;
            target.ContainerThemeName = source.ContainerThemeName;
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_savePath))
                {
                    var json = File.ReadAllText(_savePath);
                    var loaded = JsonConvert.DeserializeObject<List<ContainerModel>>(json);
                    if (loaded != null)
                    {
                        _containers.Clear();
                        _containers.AddRange(loaded);
                    }
                }
            }
            catch { }

            // Backward compat: mark containers with categories as managed
            foreach (var c in _containers)
            {
                if (c.AutoSortCategories.Count > 0)
                    c.IsAutoSortManaged = true;
            }

            ContainersChanged?.Invoke();
        }

        public void Save()
        {
            try
            {
                // Curtain containers: save open height (48.0 + CurtainOpenHeight)
                // instead of closed height, so restart restores expanded size.
                var restoreList = new List<(ContainerModel model, double origHeight)>();
                foreach (var c in _containers)
                {
                    if (c.IsCurtainMode && c.Height <= 48.0)
                    {
                        restoreList.Add((c, c.Height));
                        c.Height = 48.0 + c.CurtainOpenHeight;
                    }
                }
                var json = JsonConvert.SerializeObject(_containers, Formatting.Indented);
                File.WriteAllText(_savePath, json);
                foreach (var (model, origHeight) in restoreList)
                    model.Height = origHeight;
            }
            catch { }
        }

        public List<NoteItem> LoadNotes()
        {
            try
            {
                if (!File.Exists(_notesPath)) return new List<NoteItem>();
                var json = File.ReadAllText(_notesPath);
                return JsonConvert.DeserializeObject<List<NoteItem>>(json) ?? new List<NoteItem>();
            }
            catch { return new List<NoteItem>(); }
        }

        public void SaveNotes(List<NoteItem> notes)
        {
            try
            {
                var json = JsonConvert.SerializeObject(notes, Formatting.Indented);
                File.WriteAllText(_notesPath, json);
            }
            catch { }
        }

        public ContainerModel CreateContainer(string name = "New Container")
        {
            var (fx, fy) = FindFreePosition();
            var container = new ContainerModel
            {
                Name = name,
                X = fx,
                Y = fy,
                SortOrder = _containers.Count
            };

            ApplyDefaults(container);

            _containers.Add(container);
            Save();
            ContainersChanged?.Invoke();
            return container;
        }

        public ContainerModel DuplicateContainer(ContainerModel source)
        {
            var (fx, fy) = FindFreePosition();
            var dup = new ContainerModel
            {
                Name = source.Name + " (copy)",
                X = fx,
                Y = fy,
                Width = source.Width,
                Height = source.Height,
                Opacity = source.Opacity,
                IdleOpacity = source.IdleOpacity,
                ActiveOpacity = source.ActiveOpacity,
                AutoHide = source.AutoHide,
                AutoHideDelayMs = source.AutoHideDelayMs,
                IsLocked = source.IsLocked,
                ShowTitle = source.ShowTitle,
                FilterPattern = source.FilterPattern,
                FilterEnabled = source.FilterEnabled,
                FilterType = source.FilterType,
                FolderPortalPath = source.FolderPortalPath,
                HeaderColor = source.HeaderColor,
                BodyColor = source.BodyColor,
                TitleColor = source.TitleColor,
                LabelsColor = source.LabelsColor,
                GradientEndColor = source.GradientEndColor,
                GradientAngle = source.GradientAngle,
                HeaderGradientEnabled = source.HeaderGradientEnabled,
                BodyGradientEnabled = source.BodyGradientEnabled,
                CornerRadius = source.CornerRadius,
                BodyOpacity = source.BodyOpacity,
                IsExpanded = source.IsExpanded,
                CollapsedHeight = source.CollapsedHeight,
                SortOrder = _containers.Count,
                IsVisible = source.IsVisible,
                OpenOnDoubleClick = source.OpenOnDoubleClick,
                FullHeight = source.FullHeight,
                TitleFontFamily = source.TitleFontFamily,
                TitleFontSize = source.TitleFontSize,
                TitleAlignment = source.TitleAlignment,
                ShowBorder = source.ShowBorder,
                RoundedCorners = source.RoundedCorners,
                AutoHideOnEdge = source.AutoHideOnEdge,
                UseShellContextMenu = source.UseShellContextMenu,
                TitleHoverEffect = source.TitleHoverEffect,
                IsSvgButtonContainer = source.IsSvgButtonContainer,
                IsCurtainMode = source.IsCurtainMode,
                CurtainHeaderMode = source.CurtainHeaderMode,
                CurtainOpenWidth = source.CurtainOpenWidth,
                CurtainOpenHeight = source.CurtainOpenHeight,
                CurtainShortcutIconSize = source.CurtainShortcutIconSize,
                CurtainDirection = source.CurtainDirection,
                HideAddSvgButton = source.HideAddSvgButton,
                SvgImageSize = source.SvgImageSize,
                SvgButtonSize = source.SvgButtonSize,
                SvgButtonShowBg = source.SvgButtonShowBg,
                ShowCounter = source.ShowCounter,
                KeepOriginalsAfterSort = source.KeepOriginalsAfterSort,
                IsAutoSortManaged = source.IsAutoSortManaged,
                IsAutoSortEnabled = source.IsAutoSortEnabled,
                AutoSortTargetIdentifier = source.AutoSortTargetIdentifier,
                AutoSnapshotEnabled = source.AutoSnapshotEnabled,
                ShowShortcutArrow = source.ShowShortcutArrow,
                ShowRecycleBin = source.ShowRecycleBin,
                ShowResizeHandle = source.ShowResizeHandle,
                HeaderIconSize = source.HeaderIconSize,
                ShortcutIconSize = source.ShortcutIconSize,
                TwoLineShortcuts = source.TwoLineShortcuts,
                ContainerThemeName = source.ContainerThemeName,
                AnimationSpeedMs = source.AnimationSpeedMs,
                ViewMode = source.ViewMode,
            };

            _containers.Add(dup);
            Save();
            ContainersChanged?.Invoke();
            return dup;
        }

        private (double x, double y) FindFreePosition()
        {
            double defaultW = 500;
            double defaultH = 400;
            try
            {
                var screens = System.Windows.Forms.Screen.AllScreens;
                if (screens.Length > 0)
                {
                    var prim = screens[0].WorkingArea;
                    double startX = prim.Left + 100;
                    double startY = prim.Top + 100;
                    double maxX = prim.Right - defaultW - 100;
                    double maxY = prim.Bottom - defaultH - 100;

                    int cols = Math.Max(1, (int)((maxX - startX) / (defaultW + 20)));
                    int rows = Math.Max(1, (int)((maxY - startY) / (defaultH + 20)));

                    for (int row = 0; row < rows; row++)
                    {
                        for (int col = 0; col < cols; col++)
                        {
                            double cx = startX + col * (defaultW + 20);
                            double cy = startY + row * (defaultH + 20);
                            var testRect = new Rect(cx, cy, defaultW, defaultH);
                            if (!WouldOverlap(testRect))
                                return (cx, cy);
                        }
                    }

                    // Fallback: scan from top-left in steps
                    for (double y = startY; y < maxY; y += 80)
                    {
                        for (double x = startX; x < maxX; x += 80)
                        {
                            var testRect = new Rect(x, y, defaultW, defaultH);
                            if (!WouldOverlap(testRect))
                                return (x, y);
                        }
                    }
                }
            }
            catch { }

            // Last resort: cascade from 100,100
            return (100 + (_containers.Count * 30), 100 + (_containers.Count * 30));
        }

        public void DeleteContainer(string identifier)
        {
            var container = _containers.FirstOrDefault(c => c.Identifier == identifier);
            if (container != null)
            {
                // Portal containers mirror a folder on disk — their shortcuts are virtual,
                // not real desktop shortcuts. Don't return them to unassigned.
                // SVG button containers contain custom SVG buttons and actions, not real desktop files either.
                if (string.IsNullOrEmpty(container.FolderPortalPath) && !container.IsSvgButtonContainer)
                {
                    // Return all shortcuts to unassigned before deleting
                    foreach (var item in container.Shortcuts.ToList())
                    {
                        if (!UnassignedShortcuts.Any(s =>
                            s.Name == item.Name && s.TargetPath == item.TargetPath))
                            UnassignedShortcuts.Add(item);
                    }
                }
                container.Shortcuts.Clear();
                _containers.Remove(container);
                Save();
                ContainersChanged?.Invoke();
                UnassignedShortcutsChanged?.Invoke();
            }
        }

        public void UpdateContainer(ContainerModel container)
        {
            var existing = _containers.FirstOrDefault(c => c.Identifier == container.Identifier);
            if (existing != null)
            {
                var idx = _containers.IndexOf(existing);
                _containers[idx] = container;
                Save();
                ContainersChanged?.Invoke();
            }
        }

        public ContainerModel? GetContainer(string identifier)
        {
            return _containers.FirstOrDefault(c => c.Identifier == identifier);
        }

        /// <summary>
        /// Get a signature string for the current screen configuration (e.g. "1920x1080+0+0|2560x1440+1920+0").
        /// </summary>
        public static string GetScreenSignature()
        {
            var parts = new List<string>();
            try
            {
                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    var b = screen.Bounds;
                    parts.Add($"{b.Width}x{b.Height}+{b.Left}+{b.Top}");
                }
            }
            catch { }
            return string.Join("|", parts);
        }

        /// <summary>
        /// Save current container positions to the given screen signature.
        /// Call before resolution changes.
        /// </summary>
        public void SavePositionsForScreen(string signature)
        {
            foreach (var c in _containers)
            {
                c.ResolutionPositions[signature] = new PositionSnapshot
                {
                    X = c.X, Y = c.Y,
                    Width = c.Width, Height = c.Height,
                    FullHeight = c.FullHeight
                };
            }
            Save();
        }

        /// <summary>
        /// Restore container positions from the given screen signature.
        /// Call after resolution changes.
        /// </summary>
        public void RestorePositionsForScreen(string signature)
        {
            foreach (var c in _containers)
            {
                if (c.ResolutionPositions.TryGetValue(signature, out var pos))
                {
                    c.X = pos.X;
                    c.Y = pos.Y;
                    c.Width = pos.Width;
                    c.Height = pos.Height;
                    c.FullHeight = pos.FullHeight;
                }
            }
            Save();
        }

        /// <summary>
        /// Replace all containers with the given list (used by snapshot restore).
        /// </summary>
        public void RestoreAll(List<ContainerModel> containers)
        {
            _containers.Clear();
            _containers.AddRange(containers);
            Save();
            ContainersChanged?.Invoke();
        }

        /// <summary>
        /// Resolve collisions: ensure containers don't overlap with minimum spacing.
        /// </summary>
        public void ResolveCollisions(ContainerModel? movingContainer = null, bool pushRight = true)
        {
            bool changed;
            int maxIterations = 50;

            do
            {
                changed = false;

                for (int i = 0; i < _containers.Count; i++)
                {
                    for (int j = i + 1; j < _containers.Count; j++)
                    {
                        var a = _containers[i];
                        var b = _containers[j];

                        if (!a.IsVisible || !b.IsVisible) continue;

                        var rectA = new Rect(a.X, a.Y, a.Width, a.Height);
                        var rectB = new Rect(b.X, b.Y, b.Width, b.Height);

                        // Expand rects by minimum spacing
                        var expandedA = new Rect(a.X - MIN_SPACING, a.Y - MIN_SPACING,
                                                  a.Width + 2 * MIN_SPACING, a.Height + 2 * MIN_SPACING);

                        if (expandedA.IntersectsWith(rectB))
                        {
                            // Push B away from A
                            double overlapX = (rectA.Left < rectB.Left)
                                ? (rectA.Right + MIN_SPACING) - rectB.Left
                                : rectB.Right - (rectA.Left - MIN_SPACING);

                            double overlapY = (rectA.Top < rectB.Top)
                                ? (rectA.Bottom + MIN_SPACING) - rectB.Top
                                : rectB.Bottom - (rectA.Top - MIN_SPACING);

                            // Push in the direction of least overlap
                            if (pushRight)
                            {
                                if (Math.Abs(overlapX) <= Math.Abs(overlapY))
                                {
                                    if (rectA.Left < rectB.Left)
                                        b.X += Math.Abs(overlapX);
                                    else
                                        a.X += Math.Abs(overlapX);
                                }
                                else
                                {
                                    if (rectA.Top < rectB.Top)
                                        b.Y += Math.Abs(overlapY);
                                    else
                                        a.Y += Math.Abs(overlapY);
                                }
                            }
                            else
                            {
                                b.X += overlapX > 0 ? overlapX : -overlapX;
                            }

                            changed = true;
                        }
                    }
                }
            }
            while (changed && maxIterations-- > 0);

            Save();
        }

        /// <summary>
        /// Check if a given rect overlaps with any other container (excluding the one specified).
        /// </summary>
        public bool WouldOverlap(Rect rect, string? excludeId = null)
        {
            var expanded = new Rect(rect.X - MIN_SPACING, rect.Y - MIN_SPACING,
                                     rect.Width + 2 * MIN_SPACING, rect.Height + 2 * MIN_SPACING);

            foreach (var container in _containers)
            {
                if (container.Identifier == excludeId) continue;
                if (!container.IsVisible) continue;

                var otherRect = new Rect(container.X, container.Y, container.Width, container.Height);
                if (expanded.IntersectsWith(otherRect))
                    return true;
            }

            return false;
        }

        private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".doc", ".docx", ".pdf", ".txt", ".xls", ".xlsx", ".ppt", ".pptx",
            ".odt", ".ods", ".odp", ".rtf", ".csv", ".md", ".json", ".xml",
            ".log", ".ini", ".cfg", ".yaml", ".yml", ".toml", ".inf"
        };

        private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".lnk", ".url", ".bat", ".cmd", ".ps1", ".msi", ".appref-ms",
            ".vbs", ".js", ".wsf", ".jar", ".pyw"
        };

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tiff", ".tif", ".webp",
            ".svg", ".ico", ".raw", ".psd", ".ai", ".eps", ".heic", ".avif"
        };

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v",
            ".mpg", ".mpeg", ".3gp", ".ogv", ".ts", ".mts"
        };

        private static readonly HashSet<string> MusicExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a", ".opus",
            ".alac", ".aiff", ".dsf", ".mid", ".midi"
        };

        private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".zst",
            ".iso", ".cab", ".arj", ".lz", ".lzma", ".tgz"
        };

        private static readonly HashSet<string> LinkExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".url", ".website", ".desktop"
        };

        public static readonly Dictionary<string, HashSet<string>> CategoryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Programs"] = ProgramExtensions,
            ["Documents"] = DocExtensions,
            ["Images"] = ImageExtensions,
            ["Videos"] = VideoExtensions,
            ["Music"] = MusicExtensions,
            ["Archives"] = ArchiveExtensions,
            ["Links"] = LinkExtensions,
            ["Folders"] = new(StringComparer.OrdinalIgnoreCase) // special — handled by attribute check
        };

        public static string? GetFileCategory(string filePath)
        {
            try
            {
                if (File.GetAttributes(filePath).HasFlag(FileAttributes.Directory))
                    return "Folders";
            }
            catch { }

            string ext = Path.GetExtension(filePath);

            // For .lnk files pointing to directories, classify as Folders
            if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string? target = ShortcutItem.GetLnkTargetPath(filePath);
                if (target != null)
                {
                    try
                    {
                        if (File.GetAttributes(target).HasFlag(FileAttributes.Directory))
                            return "Folders";
                    }
                    catch { }
                }
            }

            foreach (var kvp in CategoryExtensions)
            {
                if (kvp.Value.Contains(ext))
                    return kvp.Key;
            }

            return null;
        }

        /// <summary>
        /// Check if a container accepts a given category (via AutoSortCategories or FilterType).
        /// </summary>
        private static bool ContainerAcceptsCategory(ContainerModel c, string category)
        {
            if (!c.IsVisible) return false;

            // Check explicit AutoSortCategories first
            if (c.AutoSortCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
                return true;

            // Fallback: match by FilterType (e.g. container with FilterType="Programs" accepts Programs)
            if (c.FilterEnabled && c.FilterType != null &&
                c.FilterType.Equals(category, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        /// <summary>
        /// Enumerate all desktop items: all files and directories (except hidden/system ones).
        /// </summary>
        private static List<string> GetDesktopItems()
        {
            var items = new List<string>();
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            try
            {
                if (Directory.Exists(desktopPath))
                {
                    foreach (var f in Directory.GetFiles(desktopPath))
                    {
                        try
                        {
                            var attr = File.GetAttributes(f);
                            if (attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System))
                                continue;
                        }
                        catch { }
                        items.Add(f);
                    }
                    foreach (var d in Directory.GetDirectories(desktopPath))
                    {
                        try
                        {
                            var attr = File.GetAttributes(d);
                            if (attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System))
                                continue;
                        }
                        catch { }
                        items.Add(d);
                    }
                }
            }
            catch { }
            return items;
        }

        /// <summary>
        /// Try to create a ShortcutItem from a desktop item (.lnk, .url, directory, or standard file).
        /// </summary>
        private static ShortcutItem? CreateItemFromDesktopPath(string path)
        {
            try
            {
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    return ShortcutItem.FromLnk(path);
                if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                    return ShortcutItem.FromUrl(path);

                bool isDir = File.GetAttributes(path).HasFlag(FileAttributes.Directory);
                if (isDir)
                {
                    var dir = new DirectoryInfo(path);
                    return new ShortcutItem
                    {
                        Name = dir.Name,
                        TargetPath = dir.FullName,
                        IconPath = dir.FullName,
                        ShortcutPath = dir.FullName,
                        WorkingDirectory = dir.Parent?.FullName ?? ""
                    };
                }
                else
                {
                    var file = new FileInfo(path);
                    return new ShortcutItem
                    {
                        Name = Path.GetFileNameWithoutExtension(file.Name),
                        TargetPath = file.FullName,
                        IconPath = file.FullName,
                        ShortcutPath = file.FullName,
                        WorkingDirectory = file.DirectoryName ?? ""
                    };
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Refresh the list of desktop shortcuts not yet assigned to any container.
        /// </summary>
        public void RefreshUnassignedShortcuts()
        {
            var desktopItems = GetDesktopItems();

            var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in _containers)
                foreach (var s in c.Shortcuts)
                {
                    if (!string.IsNullOrEmpty(s.ShortcutPath))
                        existingPaths.Add(s.ShortcutPath);
                    else if (!string.IsNullOrEmpty(s.TargetPath))
                        existingPaths.Add(s.TargetPath);
                }

            UnassignedShortcuts.Clear();
            foreach (var itemPath in desktopItems)
            {
                if (existingPaths.Contains(itemPath)) continue;
                var item = CreateItemFromDesktopPath(itemPath);
                if (item != null)
                    UnassignedShortcuts.Add(item);
            }
            UnassignedShortcutsChanged?.Invoke();
        }

        /// <summary>
        /// Remove shortcuts whose source file no longer exists on disk.
        /// Call after ShellContextMenu (Delete, rename, etc.).
        /// </summary>
        public void SyncDeletedShortcuts()
        {
            bool changed = false;

            // Check unassigned
            var deadUnassigned = UnassignedShortcuts
                .Where(s => !string.IsNullOrEmpty(s.ShortcutPath) && !FileOrDirectoryExists(s.ShortcutPath))
                .ToList();
            foreach (var item in deadUnassigned)
            {
                UnassignedShortcuts.Remove(item);
                changed = true;
            }

            // Check all containers
            foreach (var c in _containers)
            {
                var dead = c.Shortcuts
                    .Where(s => !string.IsNullOrEmpty(s.ShortcutPath) && !FileOrDirectoryExists(s.ShortcutPath))
                    .ToList();
                foreach (var item in dead)
                {
                    c.Shortcuts.Remove(item);
                    changed = true;
                }
            }

            if (changed)
            {
                Save();
                UnassignedShortcutsChanged?.Invoke();
            }
        }

        private static bool FileOrDirectoryExists(string path)
        {
            return File.Exists(path) || Directory.Exists(path);
        }

        /// <summary>
        /// Move a shortcut from unassigned into a container.
        /// </summary>
        public void MoveToContainer(ShortcutItem item, ContainerModel target)
        {
            if (UnassignedShortcuts.Remove(item))
            {
                target.Shortcuts.Add(item);
                Save();
                UnassignedShortcutsChanged?.Invoke();
            }
        }

        public void MoveAllToContainer(List<ShortcutItem> items, ContainerModel target)
        {
            bool changed = false;
            foreach (var item in items.ToList())
            {
                if (UnassignedShortcuts.Remove(item))
                {
                    target.Shortcuts.Add(item);
                    changed = true;
                }
            }
            if (changed)
            {
                Save();
                UnassignedShortcutsChanged?.Invoke();
            }
        }

        /// <summary>
        /// Return a shortcut to the unassigned list (e.g. when removed from a container).
        /// </summary>
        public void ReturnToUnassigned(ShortcutItem item)
        {
            UnassignedShortcuts.Add(item);
            UnassignedShortcutsChanged?.Invoke();
        }

        /// <summary>
        /// Move all unassigned shortcuts from the desktop into matching containers by category.
        /// Only affects shortcuts not already in any container.
        /// </summary>
        public void SortUnassignedShortcuts(bool keepOriginals)
        {
            var desktopItems = GetDesktopItems();

            // Collect all shortcut paths already in containers
            var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in _containers)
            {
                foreach (var s in c.Shortcuts)
                {
                    if (!string.IsNullOrEmpty(s.ShortcutPath))
                        existingPaths.Add(s.ShortcutPath);
                    else if (!string.IsNullOrEmpty(s.TargetPath))
                        existingPaths.Add(s.TargetPath);
                }
            }

            int sorted = 0;
            foreach (var itemPath in desktopItems)
            {
                if (existingPaths.Contains(itemPath)) continue;

                string? category = GetFileCategory(itemPath);
                if (category == null) continue;

                var target = _containers.FirstOrDefault(c =>
                    ContainerAcceptsCategory(c, category));

                if (target == null) continue;

                ShortcutItem? item = CreateItemFromDesktopPath(itemPath);

                if (item != null)
                {
                    bool alreadyExists = target.Shortcuts.Any(s =>
                        s.Name != null && s.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
                    if (alreadyExists) continue;

                    target.Shortcuts.Add(item);
                    sorted++;
                }
            }

            if (sorted > 0) Save();
        }

        /// <summary>
        /// Collect desktop shortcuts matching a specific container's categories into that container.
        /// </summary>
        public void CollectDesktopItemsIntoContainer(ContainerModel target)
        {
            if (target == null || !target.IsVisible) return;

            var desktopItems = GetDesktopItems();

            // Collect all shortcut paths already in containers (to avoid duplicates)
            var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in _containers)
            {
                foreach (var s in c.Shortcuts)
                {
                    if (!string.IsNullOrEmpty(s.ShortcutPath))
                        existingPaths.Add(s.ShortcutPath);
                    else if (!string.IsNullOrEmpty(s.TargetPath))
                        existingPaths.Add(s.TargetPath);
                }
            }

            int added = 0;
            foreach (var itemPath in desktopItems)
            {
                if (existingPaths.Contains(itemPath)) continue;

                string? category = GetFileCategory(itemPath);
                if (category == null) continue;

                if (!ContainerAcceptsCategory(target, category)) continue;

                ShortcutItem? item = CreateItemFromDesktopPath(itemPath);
                if (item != null)
                {
                    bool alreadyExists = target.Shortcuts.Any(s =>
                        s.Name != null && s.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
                    if (alreadyExists) continue;

                    target.Shortcuts.Add(item);
                    added++;
                }
            }

            if (added > 0) Save();
        }

        /// <summary>
        /// Sort each container's shortcuts: group by category, then alphabetically by name.
        /// Never adds or removes items from any container.
        /// </summary>
        public void SortAllShortcuts(bool keepOriginals)
        {
            bool changed = false;

            foreach (var container in _containers)
            {
                if (!container.IsVisible) continue;
                if (container.Shortcuts.Count == 0) continue;

                var sorted = container.Shortcuts
                    .OrderBy(s => GetShortcutCategory(s))
                    .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Check if order actually changed before modifying
                bool orderChanged = false;
                for (int i = 0; i < sorted.Count; i++)
                {
                    if (!ReferenceEquals(sorted[i], container.Shortcuts[i]))
                    {
                        orderChanged = true;
                        break;
                    }
                }

                if (orderChanged)
                {
                    container.Shortcuts.Clear();
                    foreach (var item in sorted)
                        container.Shortcuts.Add(item);
                    changed = true;
                }
            }

            if (changed) Save();
        }

        private static string GetShortcutCategory(ShortcutItem s)
        {
            if (string.IsNullOrEmpty(s.TargetPath))
                return "08_Other";

            try
            {
                if (File.GetAttributes(s.TargetPath).HasFlag(FileAttributes.Directory))
                    return "01_Folders";
            }
            catch { }

            string ext = Path.GetExtension(s.TargetPath)?.ToLowerInvariant() ?? "";
            if (ProgramExtensions.Contains(ext)) return "02_Programs";
            if (DocExtensions.Contains(ext)) return "03_Documents";
            if (ImageExtensions.Contains(ext)) return "04_Images";
            if (VideoExtensions.Contains(ext)) return "05_Videos";
            if (MusicExtensions.Contains(ext)) return "06_Music";
            if (ArchiveExtensions.Contains(ext)) return "07_Archives";

            return "08_Other";
        }

        /// <summary>
        /// Filter shortcuts that match a container's filter type and pattern.
        /// </summary>
        public List<ShortcutItem> GetFilteredShortcuts(ContainerModel container, List<ShortcutItem> allShortcuts)
        {
            if (!container.FilterEnabled)
                return new List<ShortcutItem>(allShortcuts);

            var type = container.FilterType ?? "All";

            // Preset filters
            if (type.Equals("Programs", StringComparison.OrdinalIgnoreCase))
                return allShortcuts.Where(s => ProgramExtensions.Contains(Path.GetExtension(s.TargetPath))).ToList();

            if (type.Equals("Documents", StringComparison.OrdinalIgnoreCase))
                return allShortcuts.Where(s => DocExtensions.Contains(Path.GetExtension(s.TargetPath))).ToList();

            if (type.Equals("Images", StringComparison.OrdinalIgnoreCase))
                return allShortcuts.Where(s => ImageExtensions.Contains(Path.GetExtension(s.TargetPath))).ToList();

            if (type.Equals("Videos", StringComparison.OrdinalIgnoreCase))
                return allShortcuts.Where(s => VideoExtensions.Contains(Path.GetExtension(s.TargetPath))).ToList();

            if (type.Equals("Music", StringComparison.OrdinalIgnoreCase))
                return allShortcuts.Where(s => MusicExtensions.Contains(Path.GetExtension(s.TargetPath))).ToList();

            if (type.Equals("Archives", StringComparison.OrdinalIgnoreCase))
                return allShortcuts.Where(s => ArchiveExtensions.Contains(Path.GetExtension(s.TargetPath))).ToList();

            if (type.Equals("Links", StringComparison.OrdinalIgnoreCase))
                return allShortcuts.Where(s => LinkExtensions.Contains(Path.GetExtension(s.TargetPath))).ToList();

            if (type.Equals("Folders", StringComparison.OrdinalIgnoreCase))
                return allShortcuts.Where(s =>
                {
                    try { return File.GetAttributes(s.TargetPath).HasFlag(FileAttributes.Directory); }
                    catch { return false; }
                }).ToList();

            // Custom regex filter
            if (type.Equals("Custom", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(container.FilterPattern))
            {
                var pattern = container.FilterPattern;
                bool isExclude = pattern.StartsWith("!");
                if (isExclude) pattern = pattern[1..];

                try
                {
                    var regex = new System.Text.RegularExpressions.Regex(pattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

                    var filtered = allShortcuts.Where(s => regex.IsMatch(s.Name)).ToList();
                    return isExclude ? allShortcuts.Except(filtered).ToList() : filtered;
                }
                catch
                {
                    var filtered = allShortcuts.Where(s =>
                        s.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)).ToList();
                    return isExclude ? allShortcuts.Except(filtered).ToList() : filtered;
                }
            }

            // All — no filter
            return new List<ShortcutItem>(allShortcuts);
        }
    }
}
