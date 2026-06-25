using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Palisades.Models;
using Palisades.Services;
using Palisades.ViewModels;
using Palisades.Views;

namespace Palisades
{
    public partial class App : System.Windows.Application
    {
        private TrayService? _trayService;
        private MainViewModel? _mainViewModel;
        private MainWindow? _mainWindow;
        private ArcticShelterWindow? _arcticWindow;
        private DesktopOverlayWindow? _overlayWindow;
        private readonly Dictionary<string, System.IO.FileSystemWatcher> _folderWatchers = new();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            TranslationService.Instance.Initialize();

            DispatcherUnhandledException += (_, args) =>
            {
                LogError(args.Exception);
                args.Handled = true;
            };

            // Handle command-line args (shell extension "Create container")
            if (e.Args.Length > 0 && e.Args[0] == "--create-container")
            {
                try
                {
                    ContainerManager.Instance.Load();
                    var container = ContainerManager.Instance.CreateContainer(TranslationService.Instance["App_NewContainer"]);
                    // Position at cursor
                    GetCursorPos(out POINT pt);
                    container.X = pt.X - 150;
                    container.Y = pt.Y - 100;
                    container.Width = 300;
                    container.Height = 200;
                    ContainerManager.Instance.Save();
                }
                catch { }
                Shutdown();
                return;
            }

            // Log application exit for diagnostics
            Exit += (_, _) =>
            {
                try
                {
                    string logPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Palisades", "debug.log");
                    File.AppendAllText(logPath,
                        $"[{DateTime.Now:HH:mm:ss}] Application.Exit fired\n");
                }
                catch { }
            };

            try
            {
                StartApplication();
            }
            catch (Exception ex)
            {
                LogError(ex);
                MessageBox.Show(string.Format(TranslationService.Instance["App_StartupError"], ex.Message), "Palisades", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private void StartApplication()
        {
            // Load saved state
            ContainerManager.Instance.Load();

            // Migrate old shortcut IconPath entries (previously saved as target path)
            // to use the original .lnk path, which enables the shortcut overlay arrow.
            MigrateShortcutIconPaths();

            // Clear icon cache to pick up any path changes from migration
            Converters.PathToImageConverter.ClearCache();

            // Create view model
            _mainViewModel = new MainViewModel();

            // Create the full-screen overlay window (single canvas for all containers)
            _overlayWindow = new DesktopOverlayWindow();
            _overlayWindow.DataContext = _mainViewModel;

            // Wire up container creation from overlay (drag-to-create)
            _overlayWindow.CreateContainerRequested += OnCreateContainerFromOverlay;
            _overlayWindow.CreateContainerWithIconsRequested += OnCreateContainerWithIconsFromOverlay;
            _overlayWindow.CreateFolderPortalRequested += OnCreateFolderPortalFromOverlay;

            // Wire up container creation: when a new container VM is created, add it to the overlay
            _mainViewModel.RequestShowContainer += (vm) => _overlayWindow?.AddContainer(vm);

            // Handle folder portal path change requests
            _mainViewModel.RequestChangeFolderPortalPath += OnFolderPortalChangeRequested;

            // Handle container recenter requests
            _mainViewModel.RequestRecenter += OnRecenterRequested;

            // Handle folder portal creation requests from dashboard
            _mainViewModel.RequestCreateFolderPortal += (x, y) => OnCreateFolderPortalFromOverlay(x, y);

            // Handle snapshot restore: rebuild overlay
            _mainViewModel.RequestRebuildOverlay += () =>
            {
                SafeDispatch(() =>
                {
                    if (_overlayWindow == null || _mainViewModel == null) return;
                    _overlayWindow.RebuildContainers(_mainViewModel.Containers);
                });
            };

            // Auto-snapshot + position memory on resolution change
            try
            {
                SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            }
            catch { /* May fail in some security contexts */ }

            // Log admin status
            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                bool isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                if (isAdmin)
                {
                    string logPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Palisades", "debug.log");
                    File.AppendAllText(logPath,
                        $"[{DateTime.Now:HH:mm:ss}] Running as ADMIN\n");
                }
            }
            catch { }

            // Start auto-sort engine (watches desktop for new shortcuts)
            AutoSortManager.Instance.ShortcutSorted += (msg) =>
            {
                SafeDispatch(() => _trayService?.ShowNotification("Palisades Auto-Sort", msg));
            };
            AutoSortManager.Instance.Start();

            // Set up tray icon
            SetupTray();

            // Create main window (legacy, kept for compatibility)
            _mainWindow = new MainWindow(_mainViewModel!);
            _mainWindow.Closed += (_, _) => { };

            // Create initial container if none exist
            if (ContainerManager.Instance.Containers.Count == 0)
            {
                CreateInitialContainer();
            }
            else
            {
                ShowExistingContainers();
            }

            // Show the overlay (behind all apps, covering all monitors)
            _overlayWindow.InitializePluginService(_mainViewModel!);
            _overlayWindow.Show();

            // Hide desktop icons (may fail on some configs - non-critical)
            try
            {
                DesktopService.HideDesktopIcons();
                if (_mainViewModel != null)
                    _mainViewModel.IconsHidden = true;
            }
            catch { }

            // Show Arctic Shelter as the main configuration window (skip on auto-start)
            if (!Environment.GetCommandLineArgs().Contains("--autostart"))
            {
                _arcticWindow = new ArcticShelterWindow(_mainViewModel!);
                _arcticWindow.Show();
            }

            // Show tray notification
            _trayService?.ShowNotification("Palisades", TranslationService.Instance["App_StartupNotification"]);

            StartAutoBackup();
        }

        private void StartAutoBackup()
        {
            try
            {
                string appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palisades");
                string backupDir = Path.Combine(appData, "backups");
                Directory.CreateDirectory(backupDir);

                var backupTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
                backupTimer.Tick += (_, _) =>
                {
                    try
                    {
                        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        foreach (var file in Directory.GetFiles(appData, "*.json"))
                        {
                            string name = Path.GetFileNameWithoutExtension(file);
                            File.Copy(file, Path.Combine(backupDir, $"{name}_{stamp}.json"), true);
                        }
                        // Keep only last 20 backups
                        var allBackups = Directory.GetFiles(backupDir, "*.json")
                            .OrderByDescending(f => f).ToList();
                        foreach (var old in allBackups.Skip(20))
                            File.Delete(old);
                    }
                    catch { }
                };
                backupTimer.Start();
            }
            catch { }
        }

        private void SetupTray()
        {
            _trayService = new TrayService();
            _trayService.ShowMainWindowRequested += () => SafeDispatch(ShowMainWindow);
            _trayService.CreateContainerRequested += () => SafeDispatch(CreateContainerFromTray);
            _trayService.ToggleContainersRequested += () => SafeDispatch(ToggleContainersVisibility);
            _trayService.ExitRequested += () => Dispatcher.BeginInvoke(new Action(Shutdown));
            _trayService.ToggleDesktopIconsRequested += () => SafeDispatch(ToggleDesktopIcons);
            _trayService.InstallContextMenuRequested += () => SafeDispatch(InstallDesktopContextMenu);

            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ressources", "icon.ico");
            if (File.Exists(iconPath))
                _trayService.SetIcon(iconPath);
        }

        private void CreateInitialContainer()
        {
            var container = ContainerManager.Instance.CreateContainer(TranslationService.Instance["App_AllApps"]);
            container.AutoHide = false;
            container.Width = 500;
            container.FullHeight = 400;

            var screen = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            container.X = (screen.Width - container.Width) / 2.0;
            container.Y = screen.Top + 20;
            ContainerManager.Instance.Save();

            ThemeService.Instance.ApplyPresetToContainer(container);
            var containerVm = new ContainerViewModel(container);
            containerVm.RequestClose += () => _mainViewModel?.DeleteContainer(containerVm);
            containerVm.RequestEdit += () =>
            {
                if (_mainViewModel != null)
                    _mainViewModel.SelectedContainer = containerVm;
            };
            _mainViewModel?.Containers.Add(containerVm);
            _overlayWindow?.AddContainer(containerVm);
        }

        private void ShowExistingContainers()
        {
            if (_mainViewModel == null) return;
            foreach (var vm in _mainViewModel.Containers)
            {
                _overlayWindow?.AddContainer(vm);
            }
        }

        private void SafeDispatch(Action action)
        {
            try
            {
                if (Dispatcher == null || Dispatcher.HasShutdownStarted)
                    return;

                if (Dispatcher.CheckAccess())
                    action();
                else
                    Dispatcher.Invoke(action);
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }

        private static void MigrateShortcutIconPaths()
        {
            try
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                bool changed = false;

                foreach (var container in ContainerManager.Instance.Containers)
                {
                    // Skip folder portals — their IconPath is the actual file path
                    if (!string.IsNullOrEmpty(container.FolderPortalPath))
                        continue;

                    foreach (var shortcut in container.Shortcuts)
                    {
                        // Already has ShortcutPath or IconPath points to .lnk — skip
                        if (!string.IsNullOrEmpty(shortcut.ShortcutPath))
                            continue;
                        if (shortcut.IconPath?.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) == true)
                            continue;

                        // Try to find the .lnk on the desktop
                        string possibleLnk = Path.Combine(desktopPath, shortcut.Name + ".lnk");
                        if (File.Exists(possibleLnk))
                        {
                            shortcut.ShortcutPath = possibleLnk;
                            shortcut.IconPath = possibleLnk;
                            changed = true;
                        }
                    }
                }

                if (changed)
                    ContainerManager.Instance.Save();
            }
            catch { }
        }

        private void ShowMainWindow()
        {
            if (_arcticWindow != null && _arcticWindow.IsLoaded)
            {
                _arcticWindow.WindowState = WindowState.Normal;
                _arcticWindow.Show();
                _arcticWindow.Activate();
                return;
            }

            // Re-create if was closed
            if (_mainViewModel != null)
            {
                _arcticWindow = new ArcticShelterWindow(_mainViewModel);
                _arcticWindow.Show();
                _arcticWindow.Activate();
            }
        }

        private void CreateContainerFromTray()
        {
            try
            {
                var container = ContainerManager.Instance.CreateContainer(TranslationService.Instance["App_NewContainer"]);
                container.AutoHide = false;

                ThemeService.Instance.ApplyPresetToContainer(container);
                var vm = new ContainerViewModel(container);
                vm.RequestClose += () => _mainViewModel?.DeleteContainer(vm);
                vm.RequestEdit += () =>
                {
                    if (_mainViewModel != null)
                        _mainViewModel.SelectedContainer = vm;
                };

                _mainViewModel?.Containers.Add(vm);
                _overlayWindow?.AddContainer(vm);
                ShowMainWindow();
            }
            catch (Exception ex)
            {
                LogError(ex);
                MessageBox.Show(string.Format(TranslationService.Instance["App_ErrorCreatingContainer"], ex.Message), "Palisades",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnCreateContainerFromOverlay(double x, double y, double width, double height, SelectedContainerType selectedType)
        {
            SafeDispatch(() =>
            {
                try
                {
                    if (selectedType == SelectedContainerType.None) return;

                    if (selectedType == SelectedContainerType.Normal)
                    {
                        var container = ContainerManager.Instance.CreateContainer(TranslationService.Instance["App_NewContainer"]);
                        container.X = x;
                        container.Y = y;
                        container.Width = Math.Max(width, 200);
                        container.Height = Math.Max(height, 150);
                        container.IsSvgButtonContainer = false;
                        container.AutoHide = false;
                        ContainerManager.Instance.Save();

                        ThemeService.Instance.ApplyPresetToContainer(container);
                        var vm = new ContainerViewModel(container);
                        vm.RequestClose += () => _mainViewModel?.DeleteContainer(vm);
                        vm.RequestEdit += () =>
                        {
                            if (_mainViewModel != null)
                                _mainViewModel.SelectedContainer = vm;
                        };
                        _mainViewModel?.Containers.Add(vm);
                        _overlayWindow?.AddContainer(vm);
                    }
                    else if (selectedType == SelectedContainerType.SvgButton)
                    {
                        var container = ContainerManager.Instance.CreateContainer(TranslationService.Instance["App_NewContainer"]);
                        container.X = x;
                        container.Y = y;
                        container.Width = Math.Max(width, 200);
                        container.Height = Math.Max(height, 150);
                        container.IsSvgButtonContainer = true;
                        container.AutoHide = false;
                        ContainerManager.Instance.Save();

                        ThemeService.Instance.ApplyPresetToContainer(container);
                        var vm = new ContainerViewModel(container);
                        vm.RequestClose += () => _mainViewModel?.DeleteContainer(vm);
                        vm.RequestEdit += () =>
                        {
                            if (_mainViewModel != null)
                                _mainViewModel.SelectedContainer = vm;
                        };
                        _mainViewModel?.Containers.Add(vm);
                        _overlayWindow?.AddContainer(vm);
                    }
                    else if (selectedType == SelectedContainerType.FolderPortal)
                    {
                        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
                        dialog.Description = TranslationService.Instance["App_FolderPortalDescription"];
                        dialog.ShowNewFolderButton = true;

                        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                            return;

                        string folderPath = dialog.SelectedPath;

                        var container = ContainerManager.Instance.CreateContainer(
                            System.IO.Path.GetFileName(folderPath));
                        container.X = x;
                        container.Y = y;
                        container.Width = Math.Max(width, 200);
                        container.Height = Math.Max(height, 150);
                        container.AutoHide = false;
                        container.FolderPortalPath = folderPath;
                        container.Shortcuts.Clear();

                        PopulateFolderShortcuts(container, folderPath);
                        ContainerManager.Instance.Save();

                        ThemeService.Instance.ApplyPresetToContainer(container);
                        var vm = new ContainerViewModel(container);
                        vm.RequestClose += () =>
                        {
                            _mainViewModel?.DeleteContainer(vm);
                            StopFolderWatcher(container.Identifier);
                        };
                        vm.RequestEdit += () =>
                        {
                            if (_mainViewModel != null)
                                _mainViewModel.SelectedContainer = vm;
                        };
                        vm.FolderPortalPathChanged += _ => OnFolderPortalChangeRequested(vm);

                        _mainViewModel?.Containers.Add(vm);
                        _overlayWindow?.AddContainer(vm);

                        StartFolderWatcher(container, folderPath);
                    }
                }
                catch (Exception ex)
                {
                    LogError(ex);
                }
            });
        }

        private void OnCreateContainerWithIconsFromOverlay(double x, double y, double width, double height, List<ShortcutItem> items)
        {
            SafeDispatch(() =>
            {
                try
                {
                    var container = ContainerManager.Instance.CreateContainer(TranslationService.Instance["App_NewContainer"]);
                    container.X = x;
                    container.Y = y;
                    container.Width = Math.Max(width, 200);
                    container.Height = Math.Max(height, 150);
                    container.AutoHide = false;
                    ContainerManager.Instance.Save();

                    ThemeService.Instance.ApplyPresetToContainer(container);
                    var vm = new ContainerViewModel(container);
                    vm.RequestClose += () => _mainViewModel?.DeleteContainer(vm);
                    vm.RequestEdit += () =>
                    {
                        if (_mainViewModel != null)
                            _mainViewModel.SelectedContainer = vm;
                    };

                    _mainViewModel?.Containers.Add(vm);
                    _overlayWindow?.AddContainer(vm);

                    // Move selected icons into the new container
                    foreach (var item in items)
                    {
                        ContainerManager.Instance.MoveToContainer(item, container);
                    }
                    ContainerManager.Instance.Save();
                }
                catch (Exception ex)
                {
                    LogError(ex);
                }
            });
        }

        private void OnCreateFolderPortalFromOverlay(double x, double y)
        {
            SafeDispatch(() =>
            {
                try
                {
                    using var dialog = new System.Windows.Forms.FolderBrowserDialog();
                    dialog.Description = TranslationService.Instance["App_FolderPortalDescription"];
                    dialog.ShowNewFolderButton = true;

                    if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                        return;

                    string folderPath = dialog.SelectedPath;

                    var container = ContainerManager.Instance.CreateContainer(
                        System.IO.Path.GetFileName(folderPath));
                    container.X = x;
                    container.Y = y;
                    container.Width = 400;
                    container.Height = 300;
                    container.AutoHide = false;
                    container.FolderPortalPath = folderPath;
                    container.Shortcuts.Clear();

                    // Populate shortcuts from the folder contents
                    PopulateFolderShortcuts(container, folderPath);

                    ContainerManager.Instance.Save();

                    ThemeService.Instance.ApplyPresetToContainer(container);
                    var vm = new ContainerViewModel(container);
                    vm.RequestClose += () =>
                    {
                        _mainViewModel?.DeleteContainer(vm);
                        StopFolderWatcher(container.Identifier);
                    };
                    vm.RequestEdit += () =>
                    {
                        if (_mainViewModel != null)
                            _mainViewModel.SelectedContainer = vm;
                    };
                    vm.FolderPortalPathChanged += _ => OnFolderPortalChangeRequested(vm);

                    _mainViewModel?.Containers.Add(vm);
                    _overlayWindow?.AddContainer(vm);

                    // Watch for file changes
                    StartFolderWatcher(container, folderPath);
                }
                catch (Exception ex)
                {
                    LogError(ex);
                }
            });
        }

        private void OnFolderPortalChangeRequested(ContainerViewModel vm)
        {
            SafeDispatch(() =>
            {
                try
                {
                    using var dialog = new System.Windows.Forms.FolderBrowserDialog();
                    dialog.Description = TranslationService.Instance["App_FolderPortalDescriptionChange"];
                    dialog.ShowNewFolderButton = true;
                    dialog.SelectedPath = vm.FolderPortalPath ?? "";

                    if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                        return;

                    string newPath = dialog.SelectedPath;
                    var container = ContainerManager.Instance.GetContainer(vm.Identifier);
                    if (container == null) return;

                    // Stop old watcher
                    StopFolderWatcher(container.Identifier);

                    // Update model
                    container.FolderPortalPath = newPath;
                    container.Name = System.IO.Path.GetFileName(newPath);
                    container.Shortcuts.Clear();
                    PopulateFolderShortcuts(container, newPath);
                    ContainerManager.Instance.Save();

                    // Refresh VM properties
                    vm.FolderPortalPath = newPath;
                    vm.Name = container.Name;

                    // Start new watcher
                    StartFolderWatcher(container, newPath);
                }
                catch (Exception ex)
                {
                    LogError(ex);
                }
            });
        }

        private static void PopulateFolderShortcuts(Models.ContainerModel container, string folderPath)
        {
            try
            {
                var dir = new System.IO.DirectoryInfo(folderPath);
                if (!dir.Exists) return;

                // Add subdirectories first
                foreach (var subDir in dir.EnumerateDirectories())
                {
                    try
                    {
                        var item = new ShortcutItem
                        {
                            Name = subDir.Name,
                            TargetPath = subDir.FullName,
                            WorkingDirectory = folderPath,
                            IconPath = subDir.FullName
                        };
                        container.Shortcuts.Add(item);
                    }
                    catch { }
                }

                // Add files
                foreach (var file in dir.EnumerateFiles())
                {
                    try
                    {
                        ShortcutItem? item = null;

                        if (file.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                            item = ShortcutItem.FromLnk(file.FullName);
                        else if (file.Extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                            item = ShortcutItem.FromUrl(file.FullName);
                        else
                            item = new ShortcutItem
                            {
                                Name = System.IO.Path.GetFileNameWithoutExtension(file.Name),
                                TargetPath = file.FullName,
                                WorkingDirectory = folderPath,
                                IconPath = file.FullName
                            };

                        if (item != null)
                            container.Shortcuts.Add(item);
                    }
                    catch { /* skip files that can't be read */ }
                }
            }
            catch { }
        }

        private void StartFolderWatcher(Models.ContainerModel container, string folderPath)
        {
            try
            {
                StopFolderWatcher(container.Identifier);

                var watcher = new System.IO.FileSystemWatcher(folderPath)
                {
                    EnableRaisingEvents = true,
                    IncludeSubdirectories = false,
                    NotifyFilter = System.IO.NotifyFilters.FileName
                                 | System.IO.NotifyFilters.LastWrite
                                 | System.IO.NotifyFilters.CreationTime
                };

                void Refresh()
                {
                    SafeDispatch(() =>
                    {
                        try
                        {
                            container.Shortcuts.Clear();
                            PopulateFolderShortcuts(container, folderPath);
                            ContainerManager.Instance.Save();
                        }
                        catch { }
                    });
                }

                watcher.Created += (_, _) => Refresh();
                watcher.Deleted += (_, _) => Refresh();
                watcher.Renamed += (_, _) => Refresh();

                _folderWatchers[container.Identifier] = watcher;
            }
            catch { }
        }

        private void StopFolderWatcher(string identifier)
        {
            if (_folderWatchers.TryGetValue(identifier, out var watcher))
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
                _folderWatchers.Remove(identifier);
            }
        }

        private void ToggleDesktopIcons()
        {
            if (DesktopService.AreIconsHidden)
                DesktopService.ShowDesktopIcons();
            else
                DesktopService.HideDesktopIcons();
        }

        private void ToggleContainersVisibility()
        {
            if (_mainViewModel == null) return;
            bool anyVisible = false;
            foreach (var vm in _mainViewModel.Containers)
                if (vm.IsVisible) { anyVisible = true; break; }

            foreach (var vm in _mainViewModel.Containers)
                vm.IsVisible = !anyVisible;
        }

        private void InstallDesktopContextMenu()
        {
            try
            {
                string exePath = Environment.ProcessPath ??
                    System.Reflection.Assembly.GetExecutingAssembly().Location;

                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Classes\DesktopBackground\Shell\Palisades");
                if (key != null)
                {
                    key.SetValue("", TranslationService.Instance["App_ContextMenuName"]);
                    key.SetValue("Icon", $"{exePath},0");
                    key.SetValue("Position", "Bottom");

                    using var cmdKey = key.CreateSubKey("command");
                    cmdKey?.SetValue("", $"\"{exePath}\" --create-container");
                }

                _trayService?.ShowNotification("Palisades", TranslationService.Instance["App_ContextMenuInstalled"]);
            }
            catch (Exception ex)
            {
                LogError(ex);
                MessageBox.Show(string.Format(TranslationService.Instance["App_FailedContextMenu"], ex.Message), "Palisades",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                if (_mainViewModel != null)
                    foreach (var vm in _mainViewModel.Containers)
                        vm.RestoreFullHeight();
            }
            catch { }
            AutoSortManager.Instance.Stop();
            try { ContainerManager.Instance.Save(); } catch { }
            try { DesktopService.ShowDesktopIcons(); } catch { }
            try { _trayService?.Dispose(); } catch { }
            try
            {
                var def = ContainerManager.Instance.LoadDefaults();
                if (def?.AutoSnapshotEnabled != false)
                {
                    int count = 1;
                    foreach (var s in SnapshotManager.Instance.Snapshots)
                        if (s.Type == "Auto") count++;
                    var autoName = $"{TranslationService.Instance["Snapshots_Auto"]} {count}";
                    SnapshotManager.Instance.CreateSnapshot(autoName, "Auto");
                }
            }
            catch { }
            base.OnExit(e);
        }

        private void OnRecenterRequested(ContainerViewModel vm)
        {
            SafeDispatch(() =>
            {
                try
                {
                    var primary = System.Windows.Forms.Screen.PrimaryScreen;
                    if (primary == null) return;

                    double cx = primary.WorkingArea.Left + (primary.WorkingArea.Width - vm.Width) / 2;
                    double cy = primary.WorkingArea.Top + (primary.WorkingArea.Height - vm.Height) / 2;

                    vm.X = cx;
                    vm.Y = cy;
                    vm.Save();

                    if (_overlayWindow != null)
                    {
                        _overlayWindow.RemoveContainer(vm.Identifier);
                        _overlayWindow.AddContainer(vm);
                    }
                }
                catch (Exception ex)
                {
                    LogError(ex);
                }
            });
        }

        private string _lastScreenSignature = ContainerManager.GetScreenSignature();

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            SafeDispatch(() =>
            {
                try
                {
                    // Save positions for the old screen config
                    string oldSig = _lastScreenSignature;
                    ContainerManager.Instance.SavePositionsForScreen(oldSig);

                    // Create auto-snapshot (if enabled)
                    var def = ContainerManager.Instance.LoadDefaults();
                    if (def?.AutoSnapshotEnabled != false)
                    {
                        int count = 1;
                        foreach (var s in SnapshotManager.Instance.Snapshots)
                            if (s.Type == "Auto") count++;
                        var autoName = $"{TranslationService.Instance["Snapshots_Auto"]} {count}";
                        SnapshotManager.Instance.CreateSnapshot(autoName, "Auto");
                    }

                    // Try to restore positions for the new screen config
                    string newSig = ContainerManager.GetScreenSignature();
                    _lastScreenSignature = newSig;
                    ContainerManager.Instance.RestorePositionsForScreen(newSig);

                    // Reposition overlay + rebuild containers with new positions
                    if (_overlayWindow != null && _mainViewModel != null)
                    {
                        _overlayWindow.RepositionOverlay();
                        _overlayWindow.RebuildContainers(_mainViewModel.Containers);
                    }
                }
                catch { }
            });
        }

        private static void LogError(Exception ex)
        {
            try
            {
                string logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Palisades", "crash.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n");
            }
            catch { }
        }
    }
}
