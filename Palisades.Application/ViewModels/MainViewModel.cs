using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Palisades.Models;
using Palisades.Plugins;
using Forms = System.Windows.Forms;
using Palisades.Services;
using Palisades.Views;

namespace Palisades.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly ContainerManager _manager;
        private readonly ThemeService _theme;
        private ContainerViewModel? _selectedContainer;
        private bool _autoHideEnabled = true;
        private bool _iconsHidden;
        private string _searchFilter = string.Empty;
        private ContainerModel? _defaultModel;

        public ContainerModel DefaultModel
        {
            get => _defaultModel ??= new ContainerModel();
            set { _defaultModel = value; OnPropertyChanged(); }
        }

        public bool IsDefaultModelLoaded => _defaultModel != null;

        public ObservableCollection<ContainerViewModel> Containers { get; } = new();
        public ThemeService Theme => _theme;

        public ContainerViewModel? SelectedContainer
        {
            get => _selectedContainer;
            set { _selectedContainer = value; OnPropertyChanged(); }
        }

        public bool AutoHideEnabled
        {
            get => _autoHideEnabled;
            set { _autoHideEnabled = value; OnPropertyChanged(); }
        }

        public bool IconsHidden
        {
            get => _iconsHidden;
            set { _iconsHidden = value; OnPropertyChanged(); OnPropertyChanged(nameof(ToggleIconsText)); }
        }

        public string ToggleIconsText => _iconsHidden
            ? TranslationService.Instance["Sidebar_ToggleIcons_Show"]
            : TranslationService.Instance["Sidebar_ToggleIcons_Hide"];

        public bool IsAutoSortEnabled
        {
            get => DefaultModel.IsAutoSortEnabled;
            set
            {
                DefaultModel.IsAutoSortEnabled = value;
                OnPropertyChanged();
                if (value) AutoSortManager.Instance.Start();
                else AutoSortManager.Instance.Stop();
                ContainerManager.Instance.SaveDefaults(DefaultModel);
            }
        }

        public string AutoSortTargetIdentifier
        {
            get => DefaultModel.AutoSortTargetIdentifier ?? "";
            set
            {
                DefaultModel.AutoSortTargetIdentifier = value;
                OnPropertyChanged();
                ContainerManager.Instance.SaveDefaults(DefaultModel);
            }
        }

        public bool IsAutoSnapshotEnabled
        {
            get => DefaultModel.AutoSnapshotEnabled;
            set
            {
                DefaultModel.AutoSnapshotEnabled = value;
                OnPropertyChanged();
                ContainerManager.Instance.SaveDefaults(DefaultModel);
            }
        }

        public bool ShowDesktopShortcutArrow
        {
            get => DefaultModel.ShowShortcutArrow;
            set
            {
                DefaultModel.ShowShortcutArrow = value;
                OnPropertyChanged();
                ContainerManager.Instance.SaveDefaults(DefaultModel);
                var overlay = System.Windows.Application.Current.Windows.OfType<Window>()
                    .FirstOrDefault(w => w is DesktopOverlayWindow) as DesktopOverlayWindow;
                overlay?.SetShortcutArrow(value);
            }
        }

        public bool ShowRecycleBin
        {
            get => DefaultModel.ShowRecycleBin;
            set
            {
                DefaultModel.ShowRecycleBin = value;
                OnPropertyChanged();
                ContainerManager.Instance.SaveDefaults(DefaultModel);
                var overlay = System.Windows.Application.Current.Windows.OfType<Window>()
                    .FirstOrDefault(w => w is DesktopOverlayWindow) as DesktopOverlayWindow;
                overlay?.RebuildDesktopIcons();
            }
        }

        public bool ShowDesktopResizeHandle
        {
            get => DefaultModel.ShowResizeHandle;
            set
            {
                DefaultModel.ShowResizeHandle = value;
                OnPropertyChanged();
                ContainerManager.Instance.SaveDefaults(DefaultModel);
                var overlay = System.Windows.Application.Current.Windows.OfType<Window>()
                    .FirstOrDefault(w => w is DesktopOverlayWindow) as DesktopOverlayWindow;
                overlay?.SetResizeHandle(value);
            }
        }

        public int LanguageIndex
        {
            get => TranslationService.Instance.CurrentCulture == "fr" ? 1 : 0;
            set
            {
                var culture = value == 1 ? "fr" : "en";
                TranslationService.Instance.SetLanguage(culture);
                OnPropertyChanged();
                OnPropertyChanged(nameof(ToggleIconsText));
            }
        }

        public string SearchFilter
        {
            get => _searchFilter;
            set { _searchFilter = value; OnPropertyChanged(); }
        }

        public string[] ThemeNames => _theme.GetAvailableThemeNames();
        public string[] ContainerThemeNames => ContainerViewModel.ContainerThemeNames;

        public void RefreshThemeNames()
        {
            OnPropertyChanged(nameof(ThemeNames));
            OnPropertyChanged(nameof(ContainerThemeNames));
        }

        public string SelectedThemeName
        {
            get => _theme.SelectedTheme;
            set
            {
                if (string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(_theme.SelectedTheme))
                {
                    OnPropertyChanged(nameof(SelectedThemeName));
                    return;
                }
                if (_theme.SelectedTheme == value) return;
                _theme.SelectedTheme = value;
                OnPropertyChanged();
                ApplyOldTheme();
            }
        }

        public bool IsDarkMode
        {
            get => _theme.IsDarkMode;
            set
            {
                _theme.IsDarkMode = value;
                OnPropertyChanged();
                ApplyOldTheme();
            }
        }

        public string GuiBackgroundColor
        {
            get => _theme.GuiBackgroundColor;
            set
            {
                _theme.GuiBackgroundColor = value;
                OnPropertyChanged();
            }
        }

        public string GuiTextColor
        {
            get => _theme.GuiTextColor;
            set
            {
                _theme.GuiTextColor = value;
                OnPropertyChanged();
            }
        }

        private ContainerViewModel? _applyTargetContainer;
        public ContainerViewModel? ApplyTargetContainer
        {
            get => _applyTargetContainer;
            set { _applyTargetContainer = value; OnPropertyChanged(); }
        }

        // Plugin properties
        public IReadOnlyList<PluginWrapper> Plugins => PluginService.Instance.Plugins;

        private PluginWrapper? _selectedPlugin;
        public PluginWrapper? SelectedPlugin
        {
            get => _selectedPlugin;
            set
            {
                if (value == null && _selectedPlugin != null && Plugins.Contains(_selectedPlugin))
                {
                    OnPropertyChanged(nameof(SelectedPlugin));
                    return;
                }
                _selectedPlugin = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedPluginHasGadgets));
                OnPropertyChanged(nameof(SelectedPluginGadgets));
            }
        }

        public bool SelectedPluginHasGadgets =>
            SelectedPlugin != null && SelectedPlugin.IsEnabled && SelectedPlugin.Context != null && SelectedPlugin.Context.Gadgets.Count > 0;

        public List<PluginGadget> SelectedPluginGadgets =>
            SelectedPlugin?.Context?.Gadgets ?? new List<PluginGadget>();

        // Active Widgets for Dashboard Customization
        private ObservableCollection<PluginGadgetItem> _activeWidgets = new();
        public ObservableCollection<PluginGadgetItem> ActiveWidgets => _activeWidgets;

        private PluginGadgetItem? _selectedWidget;
        public PluginGadgetItem? SelectedWidget
        {
            get => _selectedWidget;
            set
            {
                if (value == null && _selectedWidget != null && ActiveWidgets.Contains(_selectedWidget))
                {
                    OnPropertyChanged(nameof(SelectedWidget));
                    return;
                }
                _selectedWidget = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsWidgetSelected));
                OnPropertyChanged(nameof(SelectedWidgetIsClock));
                OnPropertyChanged(nameof(SelectedWidgetIsSystemMonitor));
                RefreshSelectedWidgetSettings();
            }
        }

        public bool IsWidgetSelected => SelectedWidget != null;
        public bool SelectedWidgetIsClock => SelectedWidget != null && SelectedWidget.GadgetType.Equals("Clock", StringComparison.OrdinalIgnoreCase);
        public bool SelectedWidgetIsSystemMonitor => SelectedWidget != null && SelectedWidget.GadgetType.Equals("SystemMonitor", StringComparison.OrdinalIgnoreCase);

        private bool _isRefreshingSettings;

        // Clock settings fields
        private bool _clockShowSeconds;
        public bool ClockShowSeconds
        {
            get => _clockShowSeconds;
            set
            {
                _clockShowSeconds = value;
                OnPropertyChanged();
                SaveSelectedWidgetCustomData();
            }
        }

        private bool _clockIs24Hour;
        public bool ClockIs24Hour
        {
            get => _clockIs24Hour;
            set
            {
                _clockIs24Hour = value;
                OnPropertyChanged();
                SaveSelectedWidgetCustomData();
            }
        }

        private string _clockColor = "#7DD3FC";
        public string ClockColor
        {
            get => _clockColor;
            set
            {
                _clockColor = value;
                OnPropertyChanged();
                SaveSelectedWidgetCustomData();
            }
        }

        private double _clockFontSize = 36;
        public double ClockFontSize
        {
            get => _clockFontSize;
            set
            {
                _clockFontSize = value;
                OnPropertyChanged();
                SaveSelectedWidgetCustomData();
            }
        }

        // System Monitor settings fields
        private bool _sysMonShowCpu;
        public bool SysMonShowCpu
        {
            get => _sysMonShowCpu;
            set
            {
                _sysMonShowCpu = value;
                OnPropertyChanged();
                SaveSelectedWidgetCustomData();
            }
        }

        private bool _sysMonShowRam;
        public bool SysMonShowRam
        {
            get => _sysMonShowRam;
            set
            {
                _sysMonShowRam = value;
                OnPropertyChanged();
                SaveSelectedWidgetCustomData();
            }
        }

        private double _sysMonInterval = 1.5;
        public double SysMonInterval
        {
            get => _sysMonInterval;
            set
            {
                _sysMonInterval = value;
                OnPropertyChanged();
                SaveSelectedWidgetCustomData();
            }
        }

        public class ClockSettings
        {
            public bool ShowSeconds { get; set; } = true;
            public bool Is24Hour { get; set; } = true;
            public string Color { get; set; } = "#7DD3FC";
            public double FontSize { get; set; } = 36;
        }

        public class SysMonSettings
        {
            public bool ShowCpu { get; set; } = true;
            public bool ShowRam { get; set; } = true;
            public double Interval { get; set; } = 1.5;
        }

        private void RefreshSelectedWidgetSettings()
        {
            if (SelectedWidget == null) return;

            if (SelectedWidgetIsClock)
            {
                try
                {
                    var settings = new ClockSettings();
                    if (!string.IsNullOrEmpty(SelectedWidget.CustomData))
                    {
                        settings = Newtonsoft.Json.JsonConvert.DeserializeObject<ClockSettings>(SelectedWidget.CustomData) ?? new ClockSettings();
                    }
                    _isRefreshingSettings = true;
                    ClockShowSeconds = settings.ShowSeconds;
                    ClockIs24Hour = settings.Is24Hour;
                    ClockColor = settings.Color;
                    ClockFontSize = settings.FontSize;
                    _isRefreshingSettings = false;
                }
                catch { _isRefreshingSettings = false; }
            }
            else if (SelectedWidgetIsSystemMonitor)
            {
                try
                {
                    var settings = new SysMonSettings();
                    if (!string.IsNullOrEmpty(SelectedWidget.CustomData))
                    {
                        settings = Newtonsoft.Json.JsonConvert.DeserializeObject<SysMonSettings>(SelectedWidget.CustomData) ?? new SysMonSettings();
                    }
                    _isRefreshingSettings = true;
                    SysMonShowCpu = settings.ShowCpu;
                    SysMonShowRam = settings.ShowRam;
                    SysMonInterval = settings.Interval;
                    _isRefreshingSettings = false;
                }
                catch { _isRefreshingSettings = false; }
            }
        }

        private void SaveSelectedWidgetCustomData()
        {
            if (SelectedWidget == null || _isRefreshingSettings) return;

            if (SelectedWidgetIsClock)
            {
                var settings = new ClockSettings
                {
                    ShowSeconds = ClockShowSeconds,
                    Is24Hour = ClockIs24Hour,
                    Color = ClockColor,
                    FontSize = ClockFontSize
                };
                SelectedWidget.CustomData = Newtonsoft.Json.JsonConvert.SerializeObject(settings);
            }
            else if (SelectedWidgetIsSystemMonitor)
            {
                var settings = new SysMonSettings
                {
                    ShowCpu = SysMonShowCpu,
                    ShowRam = SysMonShowRam,
                    Interval = SysMonInterval
                };
                SelectedWidget.CustomData = Newtonsoft.Json.JsonConvert.SerializeObject(settings);
            }

            PluginService.Instance.SaveGadgets(_activeWidgets.ToList());
        }

        public void LoadActiveWidgets()
        {
            var list = PluginService.Instance.LoadGadgets();

            // 1. Remove widgets that are no longer active
            for (int i = _activeWidgets.Count - 1; i >= 0; i--)
            {
                var w = _activeWidgets[i];
                if (!list.Any(item => item.Id == w.Id))
                {
                    w.PropertyChanged -= Widget_PropertyChanged;
                    _activeWidgets.RemoveAt(i);
                }
            }

            // 2. Add or update widgets
            foreach (var item in list)
            {
                var existing = _activeWidgets.FirstOrDefault(w => w.Id == item.Id);
                if (existing == null)
                {
                    item.PropertyChanged += Widget_PropertyChanged;
                    _activeWidgets.Add(item);
                }
                else
                {
                    // Update properties in-place, temporarily unsubscribing to avoid PropertyChanged recursion/save loop
                    existing.PropertyChanged -= Widget_PropertyChanged;
                    existing.Title = item.Title;
                    existing.X = item.X;
                    existing.Y = item.Y;
                    existing.Width = item.Width;
                    existing.Height = item.Height;
                    existing.HideHeader = item.HideHeader;
                    existing.CustomData = item.CustomData;
                    existing.Opacity = item.Opacity;
                    existing.MarginLeft = item.MarginLeft;
                    existing.MarginTop = item.MarginTop;
                    existing.MarginRight = item.MarginRight;
                    existing.MarginBottom = item.MarginBottom;
                    existing.PaddingLeft = item.PaddingLeft;
                    existing.PaddingTop = item.PaddingTop;
                    existing.PaddingRight = item.PaddingRight;
                    existing.PaddingBottom = item.PaddingBottom;
                    existing.BgColor = item.BgColor;
                    existing.BgOpacity = item.BgOpacity;
                    existing.BorderColor = item.BorderColor;
                    existing.BorderThicknessValue = item.BorderThicknessValue;
                    existing.CornerRadiusValue = item.CornerRadiusValue;
                    existing.HeaderBgColor = item.HeaderBgColor;
                    existing.HeaderBorderColor = item.HeaderBorderColor;
                    existing.TitleColor = item.TitleColor;
                    existing.TitleFontSize = item.TitleFontSize;
                    existing.PropertyChanged += Widget_PropertyChanged;
                }
            }

            // 3. Keep Selection if it still exists
            if (SelectedWidget != null)
            {
                var stillExists = _activeWidgets.FirstOrDefault(w => w.Id == SelectedWidget.Id);
                if (stillExists != null)
                {
                    if (SelectedWidget != stillExists)
                    {
                        SelectedWidget = stillExists;
                    }
                }
                else
                {
                    SelectedWidget = null;
                }
            }
        }

        private void Widget_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            PluginService.Instance.SaveGadgets(_activeWidgets.ToList());
        }

        public ICommand SetThemeCommand { get; }
        public ICommand ApplyThemeToAllContainersCommand { get; }
        public ICommand ApplyThemeToSpecificContainerCommand { get; }
        public ICommand CreateContainerCommand { get; }
        public ICommand DeleteContainerCommand { get; }
        public ICommand ToggleIconsCommand { get; }
        public ICommand ToggleAutoSortCommand { get; }
        public ICommand ToggleAutoSnapshotCommand { get; }
        public ICommand ExitCommand { get; }
        public ICommand SaveAllCommand { get; }
        public ICommand ResetThemeCommand { get; }
        public ICommand ShowAllContainersCommand { get; }
        public ICommand HideAllContainersCommand { get; }

        // Snapshot commands
        public ICommand CreateSnapshotCommand { get; }
        public ICommand RestoreSnapshotCommand { get; }
        public ICommand DeleteSnapshotCommand { get; }
        public ICommand RenameSnapshotCommand { get; }

        // Sort commands
        public ICommand SortAllCommand { get; }
        public ICommand SortUnassignedCommand { get; }
        public ICommand RefreshAllContainersCommand { get; }

        // New feature commands
        public ICommand SyncAllFolderPortalsCommand { get; }
        public ICommand ApplyOptionsToAllCommand { get; }
        public ICommand ExportConfigCommand { get; }
        public ICommand ImportConfigCommand { get; }
        public ICommand HardResetCommand { get; }
        public ICommand ToggleStartupCommand { get; }

        // Plugin commands
        public ICommand TogglePluginCommand { get; }
        public ICommand SpawnGadgetCommand { get; }
        public ICommand RefreshPluginsCommand { get; }
        public ICommand DeleteGadgetCommand { get; }

        // Quick Actions commands
        public ICommand CreateNormalContainerCommand { get; }
        public ICommand CreateSvgContainerCommand { get; }
        public ICommand CreateFolderPortalCommand { get; }
        public ICommand SpawnClockCommand { get; }
        public ICommand SpawnSysMonCommand { get; }
        public ICommand SpawnPostItCommand { get; }
        public bool IsStartupEnabled
        {
            get
            {
                try
                {
                    using var rk = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run", false);
                    return rk?.GetValue("Palisades") != null;
                }
                catch { return false; }
            }
        }
        public ICommand ApplyDefaultsToContainerCommand { get; }
        public ICommand NavigateHomeCommand { get; }
        public ICommand SaveDefaultOptionsCommand { get; }
        public ICommand ImportDefaultsFromContainerCommand { get; }

        private ContainerViewModel? _importSourceContainer;
        public ContainerViewModel? ImportSourceContainer
        {
            get => _importSourceContainer;
            set { _importSourceContainer = value; OnPropertyChanged(); }
        }

        public ObservableCollection<DestinationOption> DestinationOptions { get; } = new();

        public void RefreshDestinationOptions()
        {
            DestinationOptions.Clear();
            DestinationOptions.Add(new DestinationOption(TranslationService.Instance["DestinationOption_Desktop"], ""));
            foreach (var c in Containers)
                DestinationOptions.Add(new DestinationOption(c.Name, c.Identifier));
            OnPropertyChanged(nameof(DestinationOptions));
        }

        public ObservableCollection<SnapshotModel> Snapshots { get; } = new();

        public ObservableCollection<ContributorModel> Contributors { get; } = new();

        public async Task FetchContributorsAsync()
        {
            try
            {
                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.UserAgent.TryParseAdd("Palisades");
                client.Timeout = TimeSpan.FromSeconds(5);
                var json = await client.GetStringAsync("https://api.github.com/repos/Walkoud/Palisades/contributors");
                var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<ContributorModel>>(json);
                if (list == null) return;

                Contributors.Clear();
                foreach (var c in list.OrderByDescending(c => c.Contributions))
                    Contributors.Add(c);
                OnPropertyChanged(nameof(Contributors));
            }
            catch
            {
                // Silently fail — network issue or rate limit
            }
        }

        public event Action? RequestExit;
        public event Action<ContainerViewModel>? RequestEditContainer;
        public void InvokeRequestEditContainer(ContainerViewModel vm) => RequestEditContainer?.Invoke(vm);
        public event Action<ContainerViewModel>? RequestShowContainer;
        public event Action<ContainerViewModel>? RequestChangeFolderPortalPath;
        public event Action<ContainerViewModel>? RequestRecenter;
        public event Action? ThemeChanged;
        public event Action? RequestRebuildOverlay;
        public event Action? DefaultsImported;
        public event Action<double, double>? RequestCreateFolderPortal;
        public Func<List<NoteItem>>? GetNotesFromOverlay { get; set; }

        /// <summary>
        /// Show a container window by firing the RequestShowContainer event.
        /// Must be called from within MainViewModel since events can only be invoked by the declaring class.
        /// </summary>
        public void ShowContainerWindow(ContainerViewModel vm)
        {
            RequestShowContainer?.Invoke(vm);
        }

        public MainViewModel()
        {
            _manager = ContainerManager.Instance;
            _theme = ThemeService.Instance;

            Containers.CollectionChanged += (_, _) => RefreshDestinationOptions();
            RefreshDestinationOptions();

            SetThemeCommand = new RelayCommand<string>(theme =>
            {
                if (theme != null)
                {
                    SelectedThemeName = theme;
                }
            });
            ApplyThemeToAllContainersCommand = new RelayCommand(() =>
            {
                if (!string.IsNullOrEmpty(SelectedThemeName))
                {
                    foreach (var c in Containers)
                        c.ContainerThemeName = SelectedThemeName;
                }
            });
            ApplyThemeToSpecificContainerCommand = new RelayCommand(() =>
            {
                if (ApplyTargetContainer != null && !string.IsNullOrEmpty(SelectedThemeName))
                {
                    ApplyTargetContainer.ContainerThemeName = SelectedThemeName;
                }
            });

            CreateContainerCommand = new RelayCommand(() => CreateContainer());
            DeleteContainerCommand = new RelayCommand<ContainerViewModel>(DeleteContainer);

            // Quick Actions commands
            CreateNormalContainerCommand = new RelayCommand(() => {
                var vm = CreateContainer();
                if (vm != null)
                {
                    vm.IsSvgButtonContainer = false;
                    ContainerManager.Instance.Save();
                }
            });
            CreateSvgContainerCommand = new RelayCommand(() => {
                var vm = CreateContainer();
                if (vm != null)
                {
                    vm.IsSvgButtonContainer = true;
                    ContainerManager.Instance.Save();
                }
            });
            CreateFolderPortalCommand = new RelayCommand(() => RequestCreateFolderPortal?.Invoke(100, 100));

            SpawnClockCommand = new RelayCommand(() => SpawnBuiltInGadget("com.palisades.plugin.clock", "Clock"));
            SpawnSysMonCommand = new RelayCommand(() => SpawnBuiltInGadget("com.palisades.plugin.sysmon", "SysMon"));
            SpawnPostItCommand = new RelayCommand(() => SpawnBuiltInGadget("com.palisades.plugin.postit", "PostIt"));
            ToggleIconsCommand = new RelayCommand(ToggleIcons);
            ToggleAutoSortCommand = new RelayCommand(() => IsAutoSortEnabled = !IsAutoSortEnabled);
            ToggleAutoSnapshotCommand = new RelayCommand(() => IsAutoSnapshotEnabled = !IsAutoSnapshotEnabled);
            ExitCommand = new RelayCommand(() => Application.Current.Shutdown());
            SaveAllCommand = new RelayCommand(_manager.Save);
            ResetThemeCommand = new RelayCommand(ResetTheme);
            ShowAllContainersCommand = new RelayCommand(() => { foreach (var c in Containers) c.IsVisible = true; });
            HideAllContainersCommand = new RelayCommand(() => { foreach (var c in Containers) c.IsVisible = false; });

            // Snapshot commands
            CreateSnapshotCommand = new RelayCommand(() =>
            {
                var count = Snapshots.Count(s => s.Type == "Manual") + 1;
                var name = $"{TranslationService.Instance["Snapshots_Title"]} {count}";
                SnapshotManager.Instance.CreateSnapshot(name, "Manual");
            });
            RestoreSnapshotCommand = new RelayCommand<string>(id =>
            {
                var snap = SnapshotManager.Instance.RestoreSnapshot(id);
                if (snap != null)
                {
                    RefreshContainers();
                    RequestRebuildOverlay?.Invoke();
                    var overlay = System.Windows.Application.Current.Windows.OfType<Window>()
                        .FirstOrDefault(w => w is DesktopOverlayWindow) as DesktopOverlayWindow;
                    overlay?.RebuildNotes();
                    overlay?.RebuildGadgets();
                }
            });

            GetNotesFromOverlay = () =>
            {
                var overlay = System.Windows.Application.Current.Windows.OfType<Window>()
                    .FirstOrDefault(w => w is DesktopOverlayWindow) as DesktopOverlayWindow;
                return overlay?.GetNotes() ?? new List<NoteItem>();
            };
            DeleteSnapshotCommand = new RelayCommand<string>(id =>
            {
                SnapshotManager.Instance.DeleteSnapshot(id);
                var existing = Snapshots.FirstOrDefault(s => s.Identifier == id);
                if (existing != null) Snapshots.Remove(existing);
            });
            RenameSnapshotCommand = new RelayCommand<string>(id =>
            {
                var snap = Snapshots.FirstOrDefault(s => s.Identifier == id);
                if (snap == null) return;
                // Triggered from XAML via a rename dialog — handled in code-behind
            });

            // Sort commands
            SortAllCommand = new RelayCommand(() =>
            {
                string? selectedId = SelectedContainer?.Identifier;
                _manager.SortAllShortcuts(false);
                RefreshContainers();
                if (selectedId != null)
                    SelectedContainer = Containers.FirstOrDefault(vm => vm.Identifier == selectedId);
                RequestRebuildOverlay?.Invoke();
            });
            SortUnassignedCommand = new RelayCommand(() =>
            {
                if (SelectedContainer == null) return;
                string? selectedId = SelectedContainer.Identifier;
                _manager.CollectDesktopItemsIntoContainer(SelectedContainer.Model);
                RefreshContainers();
                if (selectedId != null)
                    SelectedContainer = Containers.FirstOrDefault(vm => vm.Identifier == selectedId);
                RequestRebuildOverlay?.Invoke();
            });
            RefreshAllContainersCommand = new RelayCommand(() =>
            {
                foreach (var vm in Containers)
                    vm.RefreshHeader();
            });

            SyncAllFolderPortalsCommand = new RelayCommand(() =>
            {
                foreach (var vm in Containers)
                {
                    if (!string.IsNullOrEmpty(vm.FolderPortalPath))
                    {
                        var container = ContainerManager.Instance.GetContainer(vm.Identifier);
                        if (container != null)
                        {
                            container.Shortcuts.Clear();
                            var dir = new System.IO.DirectoryInfo(vm.FolderPortalPath);
                            if (dir.Exists)
                            {
                                foreach (var subDir in dir.EnumerateDirectories())
                                {
                                    try
                                    {
                                        container.Shortcuts.Add(new ShortcutItem
                                        {
                                            Name = subDir.Name,
                                            TargetPath = subDir.FullName,
                                            WorkingDirectory = vm.FolderPortalPath,
                                            IconPath = subDir.FullName
                                        });
                                    }
                                    catch { }
                                }
                                foreach (var file in dir.EnumerateFiles())
                                {
                                    try
                                    {
                                        if (file.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                                        {
                                            var item = ShortcutItem.FromLnk(file.FullName);
                                            if (item != null) container.Shortcuts.Add(item);
                                        }
                                        else if (file.Extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                                        {
                                            var item = ShortcutItem.FromUrl(file.FullName);
                                            if (item != null) container.Shortcuts.Add(item);
                                        }
                                        else
                                        {
                                            container.Shortcuts.Add(new ShortcutItem
                                            {
                                                Name = System.IO.Path.GetFileNameWithoutExtension(file.Name),
                                                TargetPath = file.FullName,
                                                WorkingDirectory = vm.FolderPortalPath,
                                                IconPath = file.FullName
                                            });
                                        }
                                    }
                                    catch { }
                                }
                            }
                            ContainerManager.Instance.Save();
                            vm.RefreshHeader();
                        }
                    }
                }
                RequestRebuildOverlay?.Invoke();
            });

            NavigateHomeCommand = new RelayCommand(() => SelectedContainer = null);

            SaveDefaultOptionsCommand = new RelayCommand(() =>
            {
                if (_defaultModel != null)
                    ContainerManager.Instance.SaveDefaults(_defaultModel);
            });

            ImportDefaultsFromContainerCommand = new RelayCommand(() =>
            {
                var src = ImportSourceContainer;
                if (src == null) return;
                ContainerManager.Instance.ApplyModelTo(DefaultModel, src.Model);
                DefaultsImported?.Invoke();
            }, () => ImportSourceContainer != null);

            ApplyDefaultsToContainerCommand = new RelayCommand(() =>
            {
                var target = SelectedContainer;
                if (target == null) return;
                ContainerManager.Instance.ApplyDefaults(target.Model);
                target.RefreshAllBindings();
                ContainerManager.Instance.Save();
            }, () => SelectedContainer != null);

            ApplyOptionsToAllCommand = new RelayCommand(() =>
            {
                bool useDefaults = SelectedContainer == null;
                var src = SelectedContainer ?? new ContainerViewModel(DefaultModel);

                var t = TranslationService.Instance;
                var result = MessageBox.Show(
                    useDefaults
                        ? t["Dialog_ApplyDefaultsToAll"]
                        : t["Dialog_ApplySelectedToAll"],
                    t["Dialog_Confirmation"], MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes) return;

                foreach (var c in Containers)
                {
                    if (!useDefaults && c == src) continue;
                    c.IdleOpacityPercent = src.IdleOpacityPercent;
                    c.ActiveOpacityPercent = src.ActiveOpacityPercent;
                    c.AutoHide = src.AutoHide;
                    c.AutoHideDelayMs = src.AutoHideDelayMs;
                    c.ShowTitle = src.ShowTitle;
                    c.ShowBorder = src.ShowBorder;
                    c.ShowCounter = src.ShowCounter;
                    c.RoundedCornersEnabled = src.RoundedCornersEnabled;
                    c.CornerRadius = src.CornerRadius;
                    c.TitleFontFamily = src.TitleFontFamily;
                    c.TitleFontSize = src.TitleFontSize;
                    c.TitleAlignment = src.TitleAlignment;
                    c.HeaderColor = src.HeaderColor;
                    c.BodyColor = src.BodyColor;
                    c.TitleColor = src.TitleColor;
                    c.LabelsColor = src.LabelsColor;
                    c.OpenOnDoubleClick = src.OpenOnDoubleClick;
                    c.UseShellContextMenu = src.UseShellContextMenu;
                    c.TwoLineShortcuts = src.TwoLineShortcuts;
                    c.FilterEnabled = src.FilterEnabled;
                    c.ShowShortcutArrow = src.ShowShortcutArrow;
                    c.ShowRecycleBin = src.ShowRecycleBin;
                    c.HeaderIconSize = src.HeaderIconSize;
                    c.PrivateBoxAutoLockSeconds = src.PrivateBoxAutoLockSeconds;
                    if (!string.IsNullOrEmpty(src.FilterType))
                        c.FilterType = src.FilterType;
                    if (!string.IsNullOrEmpty(src.FilterPattern))
                        c.FilterPattern = src.FilterPattern;
                    c.Save();
                }

                ContainerManager.Instance.SaveDefaults(DefaultModel);
            });

            ExportConfigCommand = new RelayCommand(() =>
            {
                try
                {
                    var dialog = new Microsoft.Win32.SaveFileDialog
                    {
                        Filter = "JSON files (*.json)|*.json",
                        FileName = $"Palisades_config_{DateTime.Now:yyyyMMdd}.json"
                    };
                    if (dialog.ShowDialog() == true)
                    {
                        var config = new
                        {
                            Containers = _manager.Containers.Select(c => c),
                            Defaults = ContainerManager.Instance.LoadDefaults(),
                            Notes = GetNotesFromOverlay?.Invoke() ?? ContainerManager.Instance.LoadNotes(),
                            Plugins = PluginService.Instance.Plugins.ToDictionary(p => p.Plugin.Id, p => p.IsEnabled),
                            Gadgets = PluginService.Instance.LoadGadgets()
                        };
                        File.WriteAllText(dialog.FileName,
                            Newtonsoft.Json.JsonConvert.SerializeObject(config,
                                Newtonsoft.Json.Formatting.Indented));
                        MessageBox.Show(TranslationService.Instance["Dialog_ExportSuccess"], TranslationService.Instance["Dialog_Export"],
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(string.Format(TranslationService.Instance["Dialog_ExportError"], ex.Message), TranslationService.Instance["Dialog_Export"],
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });

            ImportConfigCommand = new RelayCommand(() =>
            {
                try
                {
                    var dialog = new Microsoft.Win32.OpenFileDialog
                    {
                        Filter = "JSON files (*.json)|*.json"
                    };
                    if (dialog.ShowDialog() == true)
                    {
                        var t2 = TranslationService.Instance;
                        var result = MessageBox.Show(
                            t2["Dialog_ImportWarning"],
                            t2["Dialog_Confirmation"], MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (result != MessageBoxResult.Yes) return;

                        var json = File.ReadAllText(dialog.FileName);
                        var data = Newtonsoft.Json.JsonConvert.DeserializeAnonymousType(json,
                            new {
                                Containers = new List<ContainerModel>(),
                                Defaults = (ContainerModel?)null,
                                Notes = new List<NoteItem>(),
                                Plugins = (Dictionary<string, bool>?)null,
                                Gadgets = (List<PluginGadgetItem>?)null
                            });

                        if (data?.Containers == null) return;

                        if (data.Plugins != null)
                        {
                            foreach (var p in PluginService.Instance.Plugins)
                            {
                                if (data.Plugins.TryGetValue(p.Plugin.Id, out bool isEnabled))
                                    p.IsEnabled = isEnabled;
                            }
                            PluginService.Instance.SaveSettings();
                        }

                        if (data.Gadgets != null)
                        {
                            PluginService.Instance.SaveGadgets(data.Gadgets);
                        }

                        foreach (var c in _manager.Containers.ToList())
                            _manager.DeleteContainer(c.Identifier);

                        foreach (var model in data.Containers)
                        {
                            var created = _manager.CreateContainer(model.Name);
                            created.X = model.X;
                            created.Y = model.Y;
                            created.Width = model.Width;
                            created.Height = model.Height;
                            foreach (var s in model.Shortcuts)
                            {
                                if (!created.Shortcuts.Any(ex =>
                                    ex.Name == s.Name && ex.TargetPath == s.TargetPath))
                                    created.Shortcuts.Add(s);
                            }
                        }

                        // Restore notes if present
                        if (data.Notes?.Count > 0)
                            ContainerManager.Instance.SaveNotes(data.Notes);

                        _manager.Save();
                        LoadContainers();
                        OnPropertyChanged(nameof(Containers));

                        var overlay = System.Windows.Application.Current.Windows.OfType<Window>()
                            .FirstOrDefault(w => w is DesktopOverlayWindow) as DesktopOverlayWindow;
                        overlay?.RebuildNotes();

                        MessageBox.Show(TranslationService.Instance["Dialog_ImportSuccess"],
                            TranslationService.Instance["Dialog_Import"], MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(string.Format(TranslationService.Instance["Dialog_ExportError"], ex.Message), TranslationService.Instance["Dialog_Import"],
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });

            HardResetCommand = new RelayCommand(() =>
            {
                try
                {
                    var t = TranslationService.Instance;
                    var result = MessageBox.Show(
                        t["Dialog_HardResetWarning"],
                        t["Dialog_HardResetTitle"], MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result != MessageBoxResult.Yes) return;

                    string appData = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palisades");

                    if (Directory.Exists(appData))
                        foreach (var file in Directory.GetFiles(appData, "*.json"))
                            try { File.Delete(file); } catch { }

                    string backupDir = Path.Combine(appData, "backups");
                    if (Directory.Exists(backupDir))
                        try { Directory.Delete(backupDir, true); } catch { }

                    foreach (var c in Containers.ToList())
                        _manager.DeleteContainer(c.Identifier);
                    Containers.Clear();
                    OnPropertyChanged(nameof(Containers));
                    RefreshDestinationOptions();

                    var overlay = System.Windows.Application.Current.Windows.OfType<Window>()
                        .FirstOrDefault(w => w is DesktopOverlayWindow) as DesktopOverlayWindow;
                    if (overlay != null)
                    {
                        foreach (var n in overlay.GetNotes().ToList())
                            overlay.RemoveNote(n);
                        overlay.RebuildContainers(Containers);
                    }

                    foreach (var s in SnapshotManager.Instance.Snapshots.ToList())
                        SnapshotManager.Instance.DeleteSnapshot(s.Identifier);

                    try { DesktopService.ShowDesktopIcons(); } catch { }

                    var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
                    try { System.Diagnostics.Process.Start(exePath); } catch { }
                    System.Windows.Application.Current.Shutdown();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(string.Format(TranslationService.Instance["Dialog_ExportError"], ex.Message), TranslationService.Instance["Dialog_HardResetTitle"],
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });

            ToggleStartupCommand = new RelayCommand(() =>
            {
                try
                {
                    const string key = @"Software\Microsoft\Windows\CurrentVersion\Run";
                    const string appName = "Palisades";
                    using var rk = Registry.CurrentUser.OpenSubKey(key, true);
                    if (rk == null) return;
                    var current = rk.GetValue(appName) as string;
                    if (current != null)
                    {
                        rk.DeleteValue(appName);
                        MessageBox.Show(TranslationService.Instance["Dialog_StartupDisabled"],
                            TranslationService.Instance["Dialog_Startup"], MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
                        rk.SetValue(appName, $"\"{exePath}\" --autostart");
                        MessageBox.Show(TranslationService.Instance["Dialog_StartupEnabled"],
                            TranslationService.Instance["Dialog_Startup"], MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    OnPropertyChanged(nameof(IsStartupEnabled));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(string.Format(TranslationService.Instance["Dialog_ExportError"], ex.Message), TranslationService.Instance["Dialog_Startup"],
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });

            // Load existing snapshots
            foreach (var snap in SnapshotManager.Instance.Snapshots)
                Snapshots.Add(snap);

            SnapshotManager.Instance.SnapshotsChanged += () =>
            {
                Snapshots.Clear();
                foreach (var snap in SnapshotManager.Instance.Snapshots)
                    Snapshots.Add(snap);
                OnPropertyChanged(nameof(Snapshots));
            };

            // Load default options
            var loaded = ContainerManager.Instance.LoadDefaults();
            if (loaded != null)
                DefaultModel = loaded;

            if (DefaultModel.IsAutoSortEnabled)
                AutoSortManager.Instance.Start();

            TogglePluginCommand = new RelayCommand<PluginWrapper>(wrapper =>
            {
                if (wrapper == null) return;
                var overlay = System.Windows.Application.Current.Windows.OfType<Window>()
                    .FirstOrDefault(w => w is DesktopOverlayWindow) as DesktopOverlayWindow;
                if (overlay != null)
                {
                    PluginService.Instance.TogglePlugin(wrapper.Plugin.Id, !wrapper.IsEnabled, this, overlay);
                    OnPropertyChanged(nameof(Plugins));
                    OnPropertyChanged(nameof(SelectedPluginHasGadgets));
                    OnPropertyChanged(nameof(SelectedPluginGadgets));
                }
            });

            SpawnGadgetCommand = new RelayCommand<PluginGadget>(gadget =>
            {
                if (gadget == null || SelectedPlugin == null) return;
                var overlay = System.Windows.Application.Current.Windows.OfType<Window>()
                    .FirstOrDefault(w => w is DesktopOverlayWindow) as DesktopOverlayWindow;
                overlay?.SpawnGadget(SelectedPlugin.Plugin.Id, gadget.GadgetType);
            });

            RefreshPluginsCommand = new RelayCommand(() =>
            {
                var overlay = System.Windows.Application.Current.Windows.OfType<Window>()
                    .FirstOrDefault(w => w is DesktopOverlayWindow) as DesktopOverlayWindow;
                if (overlay != null)
                {
                    PluginService.Instance.Initialize(this, overlay);
                    OnPropertyChanged(nameof(Plugins));
                    if (SelectedPlugin != null)
                    {
                        SelectedPlugin = Plugins.FirstOrDefault(p => p.Plugin.Id == SelectedPlugin.Plugin.Id);
                    }
                }
            });

            DeleteGadgetCommand = new RelayCommand<PluginGadgetItem>(item =>
            {
                if (item == null) return;
                var overlay = System.Windows.Application.Current.Windows.OfType<Window>()
                    .FirstOrDefault(w => w is DesktopOverlayWindow) as DesktopOverlayWindow;
                overlay?.RemoveGadget(item.Id);
                if (SelectedWidget?.Id == item.Id)
                    SelectedWidget = null;
            });

            PluginService.Instance.GadgetsChanged += LoadActiveWidgets;
            LoadActiveWidgets();

            LoadContainers();
        }

        public void LoadContainers()
        {
            Containers.Clear();

            foreach (var container in _manager.Containers)
            {
                var vm = CreateContainerVm(container);
                Containers.Add(vm);
            }

            OnPropertyChanged(nameof(Containers));
            RefreshDestinationOptions();
        }

        private ContainerViewModel CreateContainerVm(ContainerModel container)
        {
            var vm = new ContainerViewModel(container);
            vm.RequestClose += () => DeleteContainer(vm);
            vm.RequestEdit += () => RequestEditContainer?.Invoke(vm);
            vm.RequestDuplicate += () => DuplicateContainer(vm);
            vm.FolderPortalPathChanged += _ => RequestChangeFolderPortalPath?.Invoke(vm);
            vm.RequestRecenter += () => RequestRecenter?.Invoke(vm);
            return vm;
        }

        public void RefreshContainers()
        {
            LoadContainers();
        }

        public ContainerViewModel? CreateContainer(string? name = null)
        {
            var model = _manager.CreateContainer(name ?? TranslationService.Instance["Menu_NewContainer"]);
            _theme.ApplyPresetToContainer(model);
            var vm = CreateContainerVm(model);
            Containers.Add(vm);

            // Show the new container
            RequestShowContainer?.Invoke(vm);

            return vm;
        }

        public void DeleteContainer(ContainerViewModel? vm)
        {
            if (vm == null) return;
            _manager.DeleteContainer(vm.Identifier);
            Containers.Remove(vm);
        }

        public void DuplicateContainer(ContainerViewModel? source)
        {
            if (source == null) return;
            var model = _manager.DuplicateContainer(source.Model);
            var vm = CreateContainerVm(model);
            Containers.Add(vm);
            RequestShowContainer?.Invoke(vm);
        }

        private void ToggleIcons()
        {
            if (DesktopService.AreIconsHidden)
                DesktopService.ShowDesktopIcons();
            else
                DesktopService.HideDesktopIcons();

            IconsHidden = DesktopService.AreIconsHidden;
        }

        private void ResetTheme()
        {
            _theme.ResetToDefaults();
            OnPropertyChanged(nameof(IsDarkMode));
            OnPropertyChanged(nameof(SelectedThemeName));
            ApplyOldTheme();
        }

        private void ApplyOldTheme()
        {
            ThemeChanged?.Invoke();
        }

        private void ApplyThemeColorsToAll()
        {
            var preset = _theme.CurrentPreset;
            if (preset == null) return;
            foreach (var c in Containers)
            {
                c.HeaderColor = (Color)ColorConverter.ConvertFromString(preset.HeaderColor);
                c.BodyColor = (Color)ColorConverter.ConvertFromString(preset.BodyColor);
                c.TitleColor = (Color)ColorConverter.ConvertFromString(preset.TitleColor);
                c.LabelsColor = (Color)ColorConverter.ConvertFromString(preset.LabelsColor);
            }
            ThemeChanged?.Invoke();
        }

        public void NotifyRebuildOverlay()
        {
            RequestRebuildOverlay?.Invoke();
        }

        private void SpawnBuiltInGadget(string pluginId, string gadgetType)
        {
            var overlay = System.Windows.Application.Current.Windows.OfType<Window>()
                .FirstOrDefault(w => w is DesktopOverlayWindow) as DesktopOverlayWindow;
            if (overlay == null) return;

            // Auto-enable the plugin if it's currently disabled
            var plugin = PluginService.Instance.Plugins.FirstOrDefault(p => p.Plugin.Id == pluginId);
            if (plugin != null && !plugin.IsEnabled)
            {
                PluginService.Instance.TogglePlugin(pluginId, true, this, overlay);
            }

            overlay.SpawnGadget(pluginId, gadgetType);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class DestinationOption
    {
        public string Name { get; set; }
        public string Identifier { get; set; }
        public bool IsDesktop => string.IsNullOrEmpty(Identifier);

        public DestinationOption(string name, string identifier)
        {
            Name = name;
            Identifier = identifier;
        }
    }
}
