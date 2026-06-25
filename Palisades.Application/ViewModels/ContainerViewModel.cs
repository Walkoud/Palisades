using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Palisades.Models;
using Palisades.Services;

namespace Palisades.ViewModels
{
    public class ContainerViewModel : INotifyPropertyChanged
    {
        private readonly ContainerModel _model;
        private DispatcherTimer? _autoHideTimer;
        private bool _isHovered;
        private double _currentOpacity = 1.0;
        private double _idleOpacityPercent = 100;
        private double _activeOpacityPercent = 100;
        private bool _isEditing;
        private double _fullHeight;
        private bool _suppressSave;

        private const double MIN_AUTO_HIDE_HEIGHT = 52.0;
        private double _clipHeight;
        private int _savedCornerRadius = 12;

        public double ClipHeight
        {
            get => _clipHeight;
            set { _clipHeight = value; OnPropertyChanged(); }
        }

        public ContainerModel Model => _model;
        public string Identifier => _model.Identifier;

        public string Name
        {
            get => _model.Name;
            set { _model.Name = value; OnPropertyChanged(); OnPropertyChanged(nameof(HeaderText)); Save(); }
        }

        public double X
        {
            get => _model.X;
            set { _model.X = Math.Round(value); OnPropertyChanged(); Save(); }
        }

        public double Y
        {
            get => _model.Y;
            set { _model.Y = Math.Round(value); OnPropertyChanged(); Save(); }
        }

        public double Width
        {
            get => _model.Width;
            set { _model.Width = Math.Max(200, value); OnPropertyChanged(); Save(); }
        }

        public double Height
        {
            get => _model.Height;
            set
            {
                _model.Height = Math.Max(0, value);
                OnPropertyChanged();
                if (!_isAnimatingHeight)
                    ClipHeight = _model.Height;
                if (value > _fullHeight)
                {
                    _fullHeight = value;
                    _model.FullHeight = value;
                }
                if (!_suppressSave)
                    Save();
            }
        }

        public double FullHeight
        {
            get => _fullHeight;
            set { _fullHeight = Math.Max(100, value); }
        }

        public double IdleOpacityPercent
        {
            get => _idleOpacityPercent;
            set
            {
                _idleOpacityPercent = Math.Clamp(value, 0, 100);
                _model.IdleOpacity = _idleOpacityPercent;
                OnPropertyChanged();
                Save();
                StartOpacityAnimation(_isHovered ? ActiveTargetOpacity : IdleTargetOpacity);
            }
        }

        public double ActiveOpacityPercent
        {
            get => _activeOpacityPercent;
            set
            {
                _activeOpacityPercent = Math.Clamp(value, 0, 100);
                _model.ActiveOpacity = _activeOpacityPercent;
                OnPropertyChanged();
                Save();
                StartOpacityAnimation(_isHovered ? ActiveTargetOpacity : IdleTargetOpacity);
            }
        }

        public double IdleTargetOpacity => _idleOpacityPercent / 100.0;
        public double ActiveTargetOpacity => _activeOpacityPercent / 100.0;

        public double CurrentOpacity
        {
            get => _currentOpacity;
            set { _currentOpacity = value; OnPropertyChanged(nameof(CurrentOpacity)); }
        }

        public bool AutoHide
        {
            get => _model.AutoHide;
            set
            {
                _model.AutoHide = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsVisuallyCollapsed));
                Save();
                if (value)
                {
                    EnsureTimer();
                    if (!_isHovered && Height > MIN_AUTO_HIDE_HEIGHT)
                        StartHeightAnimation(MIN_AUTO_HIDE_HEIGHT, HideDurMs);
                }
                else
                {
                    _autoHideTimer?.Stop();
                    Show();
                }
            }
        }

        public int AutoHideDelayMs
        {
            get => _model.AutoHideDelayMs;
            set { _model.AutoHideDelayMs = Math.Clamp(value, 100, 5000); OnPropertyChanged(); Save(); }
        }

        public bool IsLocked
        {
            get => _model.IsLocked;
            set { _model.IsLocked = value; OnPropertyChanged(); Save(); }
        }

        public bool ShowTitle
        {
            get => _model.ShowTitle;
            set { _model.ShowTitle = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowTitleVisibility)); Save(); }
        }

        public Visibility ShowTitleVisibility => _model.ShowTitle ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>True when the container is visually collapsed (auto-hide enabled AND not hovered).</summary>
        public bool IsVisuallyCollapsed => AutoHide && !_isHovered;

        public string HeaderText
        {
            get
            {
                string baseName = string.IsNullOrEmpty(_model.Name) ? TranslationService.Instance["Menu_Container"] : _model.Name;
                if (_model.ShowCounter)
                {
                    int count = _model.Shortcuts?.Count ?? 0;
                    return $"{baseName} ({count})";
                }
                return baseName;
            }
        }

        public List<string> AutoSortCategories
        {
            get => _model.AutoSortCategories;
            set { _model.AutoSortCategories = value; OnPropertyChanged(); Save(); }
        }

        public bool ShowCounter
        {
            get => _model.ShowCounter;
            set
            {
                _model.ShowCounter = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HeaderText));
                Save();
            }
        }


        public string FilterPattern
        {
            get => _model.FilterPattern;
            set { _model.FilterPattern = value; OnPropertyChanged(); Save(); }
        }

        public string FilterType
        {
            get => _model.FilterType;
            set { _model.FilterType = value; OnPropertyChanged(); Save(); }
        }

        public bool FilterEnabled
        {
            get => _model.FilterEnabled;
            set { _model.FilterEnabled = value; OnPropertyChanged(); Save(); }
        }

        public Color HeaderColor
        {
            get => (Color)ColorConverter.ConvertFromString(_model.HeaderColor);
            set { _model.HeaderColor = value.ToString(); OnPropertyChanged(); Save(); }
        }

        public Color BodyColor
        {
            get => (Color)ColorConverter.ConvertFromString(_model.BodyColor);
            set
            {
                _model.BodyColor = value.ToString();
                OnPropertyChanged();
                OnPropertyChanged(nameof(BodyColorWithOpacity));
                Save();
            }
        }

        public Color BodyColorWithOpacity
        {
            get
            {
                var color = (Color)ColorConverter.ConvertFromString(_model.BodyColor);
                byte alpha = (byte)Math.Round(BodyOpacity / 100.0 * 255);
                return Color.FromArgb(alpha, color.R, color.G, color.B);
            }
        }

        public bool IsGradient => !string.IsNullOrEmpty(_model.GradientEndColor);

        public string? GradientEndColor
        {
            get => _model.GradientEndColor;
            set
            {
                _model.GradientEndColor = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsGradient));
                Save();
            }
        }

        public void ApplyGradient(Color startColor, Color endColor)
        {
            HeaderColor = startColor;
            BodyColor = startColor;
            GradientEndColor = endColor.ToString();
        }

        public Color TitleColor
        {
            get => (Color)ColorConverter.ConvertFromString(_model.TitleColor);
            set { _model.TitleColor = value.ToString(); OnPropertyChanged(); Save(); }
        }

        public Color LabelsColor
        {
            get => (Color)ColorConverter.ConvertFromString(_model.LabelsColor);
            set { _model.LabelsColor = value.ToString(); OnPropertyChanged(); Save(); }
        }

        public int CornerRadius
        {
            get => _model.CornerRadius;
            set
            {
                _savedCornerRadius = Math.Max(1, value);
                _model.CornerRadius = Math.Clamp(value, 0, 30);
                OnPropertyChanged();
                Save();
            }
        }

        public bool OpenOnDoubleClick
        {
            get => _model.OpenOnDoubleClick;
            set { _model.OpenOnDoubleClick = value; OnPropertyChanged(); Save(); }
        }

        public static string[] FontFamilies { get; } =
        {
            "Segoe UI", "Segoe UI Semibold", "Calibri", "Arial", "Consolas",
            "Tahoma", "Verdana", "Trebuchet MS", "Georgia", "Microsoft Sans Serif"
        };

        public string TitleFontFamily
        {
            get => _model.TitleFontFamily;
            set { _model.TitleFontFamily = value; OnPropertyChanged(); OnPropertyChanged(nameof(HeaderText)); Save(); }
        }

        public double TitleFontSize
        {
            get => _model.TitleFontSize;
            set { _model.TitleFontSize = Math.Clamp(value, 9, 20); OnPropertyChanged(); Save(); }
        }

        public string TitleAlignment
        {
            get => _model.TitleAlignment;
            set { _model.TitleAlignment = value; OnPropertyChanged(); Save(); }
        }

        public bool ShowBorder
        {
            get => _model.ShowBorder;
            set { _model.ShowBorder = value; OnPropertyChanged(); Save(); }
        }

        public bool RoundedCornersEnabled
        {
            get => _model.RoundedCorners;
            set
            {
                _model.RoundedCorners = value;
                if (value)
                    _model.CornerRadius = _savedCornerRadius;
                else
                    _model.CornerRadius = 0;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CornerRadius));
                Save();
            }
        }

        public bool AutoHideOnEdge
        {
            get => _model.AutoHideOnEdge;
            set { _model.AutoHideOnEdge = value; OnPropertyChanged(); Save(); }
        }

        public bool UseShellContextMenu
        {
            get => _model.UseShellContextMenu;
            set { _model.UseShellContextMenu = value; OnPropertyChanged(); Save(); }
        }

        public bool IsSvgButtonContainer
        {
            get => _model.IsSvgButtonContainer;
            set
            {
                _model.IsSvgButtonContainer = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNormalContainer));
                Save();
            }
        }

        public bool IsNormalContainer => !_model.IsSvgButtonContainer;

        public bool HideAddSvgButton
        {
            get => _model.HideAddSvgButton;
            set { _model.HideAddSvgButton = value; OnPropertyChanged(); Save(); }
        }

        private bool _isTitleHovered;

        public bool IsTitleHovered
        {
            get => _isTitleHovered;
            set { _isTitleHovered = value; OnPropertyChanged(); }
        }

        public bool TitleHoverEffect
        {
            get => _model.TitleHoverEffect;
            set { _model.TitleHoverEffect = value; OnPropertyChanged(); Save(); }
        }

        public bool IsVisible
        {
            get => _model.IsVisible;
            set { _model.IsVisible = value; OnPropertyChanged(); Save(); }
        }

        public string? FolderPortalPath
        {
            get => _model.FolderPortalPath;
            set { _model.FolderPortalPath = value; OnPropertyChanged(); Save(); }
        }

        public bool IsEditing
        {
            get => _isEditing;
            set { _isEditing = value; OnPropertyChanged(); }
        }

        public bool IsHovered
        {
            get => _isHovered;
            set
            {
                if (_isHovered == value) return;
                _isHovered = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsVisuallyCollapsed));

                // Opacity transition: fade to active when hovered, idle when not
                double targetOpacity = value ? ActiveTargetOpacity : IdleTargetOpacity;
                // Only animate if the target is different from current
                if (Math.Abs(targetOpacity - _currentOpacity) > 0.005)
                    StartOpacityAnimation(targetOpacity);

                if (_model.AutoHide)
                {
                    if (value)
                    {
                        _autoHideTimer?.Stop();
                        if (Height < _fullHeight || _isAnimatingHeight)
                        {
                            StartHeightAnimation(_fullHeight, ShowDurMs);
                        }
                    }
                    else
                    {
                        StartHeightAnimation(MIN_AUTO_HIDE_HEIGHT, HideDurMs);
                    }
                }
            }
        }

        // --- NEW PROPERTIES ---

        public bool ShowShortcutArrow
        {
            get => _model.ShowShortcutArrow;
            set
            {
                _model.ShowShortcutArrow = value;
                OnPropertyChanged();
                Save();
            }
        }

        public int HeaderIconSize
        {
            get => _model.HeaderIconSize;
            set
            {
                _model.HeaderIconSize = Math.Clamp(value, 6, 16);
                OnPropertyChanged();
                Save();
            }
        }

        public int ShortcutIconSize
        {
            get => _model.ShortcutIconSize;
            set
            {
                _model.ShortcutIconSize = Math.Clamp(value, 24, 64);
                OnPropertyChanged();
                Save();
            }
        }

        public int SvgImageSize
        {
            get => _model.SvgImageSize;
            set
            {
                _model.SvgImageSize = Math.Clamp(value, 16, 96);
                OnPropertyChanged();
                Save();
            }
        }

        public int SvgButtonSize
        {
            get => _model.SvgButtonSize;
            set
            {
                _model.SvgButtonSize = Math.Clamp(value, 24, 128);
                OnPropertyChanged();
                Save();
            }
        }

        public bool SvgButtonShowBg
        {
            get => _model.SvgButtonShowBg;
            set
            {
                _model.SvgButtonShowBg = value;
                OnPropertyChanged();
                Save();
            }
        }

        public int BodyOpacity
        {
            get => _model.BodyOpacity;
            set
            {
                _model.BodyOpacity = Math.Clamp(value, 0, 100);
                OnPropertyChanged();
                OnPropertyChanged(nameof(BodyOpacityFactor));
                OnPropertyChanged(nameof(BodyColorWithOpacity));
                Save();
            }
        }

        public double BodyOpacityFactor => BodyOpacity / 100.0;

        public int AnimationSpeedMs
        {
            get => _model.AnimationSpeedMs;
            set
            {
                _model.AnimationSpeedMs = Math.Clamp(value, 100, 1000);
                OnPropertyChanged();
                Save();
            }
        }

        public string ViewMode
        {
            get => _model.ViewMode;
            set
            {
                _model.ViewMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDetailsView));
                Save();
            }
        }

        public bool IsDetailsView => _model.ViewMode == "Details";

        public string? ContainerThemeName
        {
            get => _model.ContainerThemeName;
            set
            {
                _model.ContainerThemeName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsThemeActive));
                if (!string.IsNullOrEmpty(value) && value != "Custom" && value != "Theme")
                    ApplyThemePreset(value);
                RefreshAllBindings();
                Save();
            }
        }

        public bool IsThemeActive => ContainerThemeName != "Custom";

        public static string[] ContainerThemeNames
        {
            get
            {
                var list = new List<string> { "Theme", "Custom" };
                list.AddRange(ThemeService.Presets.Select(p => p.Name));
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
        }

        public string PasswordHash => _model.PasswordHash;
        public bool IsPasswordLocked
        {
            get => _model.IsPasswordLocked;
            set
            {
                _model.IsPasswordLocked = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasPassword));
                OnPropertyChanged(nameof(CanLockPrivateBox));
            }
        }

        public bool HasPassword => !string.IsNullOrEmpty(_model.PasswordHash);
        public bool CanLockPrivateBox => HasPassword && !_model.IsPasswordLocked;

        public int PrivateBoxAutoLockSeconds
        {
            get => _model.PrivateBoxAutoLockSeconds;
            set
            {
                _model.PrivateBoxAutoLockSeconds = Math.Max(0, value);
                OnPropertyChanged();
                Save();
            }
        }

        private string? _unlockPassword;

        public void SetUnlockPassword(string password) => _unlockPassword = password;
        public void ClearUnlockPassword() => _unlockPassword = null;

        public void LockPrivateBox()
        {
            if (!HasPassword || IsPasswordLocked) return;

            // Encrypt shortcuts if we have the password stored from unlock
            if (!string.IsNullOrEmpty(_unlockPassword))
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(
                    _model.Shortcuts.ToList(), Newtonsoft.Json.Formatting.None);
                _model.EncryptedShortcuts = Services.EncryptionService.Encrypt(json, _unlockPassword);
            }

            _model.Shortcuts.Clear();
            _unlockPassword = null;
            IsPasswordLocked = true;
            Save();
        }

        public void NotifyPasswordChanged()
        {
            OnPropertyChanged(nameof(PasswordHash));
            OnPropertyChanged(nameof(HasPassword));
        }

        // Inline title editing
        public bool IsEditingTitle
        {
            get => _isEditingTitle;
            set { _isEditingTitle = value; OnPropertyChanged(); }
        }
        private bool _isEditingTitle;

        public string EditingTitle
        {
            get => _editingTitle;
            set { _editingTitle = value; OnPropertyChanged(); }
        }
        private string _editingTitle = string.Empty;

        public void BeginEditTitle()
        {
            EditingTitle = Name;
            IsEditingTitle = true;
        }

        public void CommitEditTitle()
        {
            if (!string.IsNullOrWhiteSpace(EditingTitle))
                Name = EditingTitle;
            IsEditingTitle = false;
        }

        public void CancelEditTitle()
        {
            IsEditingTitle = false;
        }

        public void ApplyThemePreset(string themeName)
        {
            var preset = ThemeService.Presets.FirstOrDefault(p =>
                p.Name.Equals(themeName, StringComparison.OrdinalIgnoreCase));
            if (preset == null) return;

            _model.ContainerThemeName = themeName;
            HeaderColor = (Color)ColorConverter.ConvertFromString(preset.HeaderColor);
            BodyColor = (Color)ColorConverter.ConvertFromString(preset.BodyColor);
            TitleColor = (Color)ColorConverter.ConvertFromString(preset.TitleColor);
            LabelsColor = (Color)ColorConverter.ConvertFromString(preset.LabelsColor);
            GradientEndColor = null;
        }

        // --- END NEW ---

        public ObservableCollection<ShortcutItem> Shortcuts => _model.Shortcuts;
        public ShortcutItem? SelectedShortcut { get; set; }
        public ObservableCollection<ShortcutItem> SelectedShortcuts { get; } = new();
        public bool IsMultiSelecting => SelectedShortcuts.Count > 1;

        // Commands
        public ICommand DeleteCommand { get; }
        public ICommand ToggleLockCommand { get; }
        public ICommand ToggleAutoHideCommand { get; }
        public ICommand EditCommand { get; }
        public ICommand DeleteShortcutCommand { get; }
        public ICommand UndoLastDeleteCommand { get; }
        public ICommand ChangeFolderPortalPathCommand { get; }
        public ICommand RecenterCommand { get; }
        public ICommand ResizeToIconMultiplesCommand { get; }

        // iTop-like header commands
        public ICommand ToggleCollapseCommand { get; }
        public ICommand CreateShortcutCommand { get; }
        public ICommand RenameCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand SetAsDefaultCommand { get; }
        public ICommand SetIconSizeCommand { get; }
        public ICommand SetAnimationSpeedCommand { get; }
        public ICommand ToggleOptionCommand { get; }
        public ICommand SortNowCommand { get; }

        // Search hybrid
        public bool IsSearchActive
        {
            get => _isSearchActive;
            set { _isSearchActive = value; OnPropertyChanged(); }
        }
        private bool _isSearchActive;

        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (_searchQuery == value) return;
                _searchQuery = value;
                OnPropertyChanged();
                if (!string.IsNullOrEmpty(value))
                {
                    FilterPattern = value;
                    FilterEnabled = true;
                }
                else
                {
                    FilterPattern = string.Empty;
                    FilterEnabled = false;
                }
            }
        }
        private string _searchQuery = string.Empty;

        public event Action<string?>? FolderPortalPathChanged;
        public event Action? RequestRecenter;
        public event Action? RequestClose;
        public event Action? RequestEdit;
        public event Action? RequestCreateShortcut;
        public event Action? PositionChanged;

        public ContainerViewModel(ContainerModel model)
        {
            _model = model;
            double loadedHeight = model.Height;

            // Legacy check: old containers only had Opacity (0-100), not IdleOpacity/ActiveOpacity.
            // When IdleOpacity and ActiveOpacity are both 0 (unset) and Opacity is < 100, use Opacity for both.
            if (model.IdleOpacity < 1 && model.ActiveOpacity < 1 && model.Opacity > 0 && model.Opacity < 100)
            {
                _idleOpacityPercent = model.Opacity;
                _activeOpacityPercent = model.Opacity;
            }
            else
            {
                _idleOpacityPercent = model.IdleOpacity > 0 ? model.IdleOpacity : 29;
                _activeOpacityPercent = model.ActiveOpacity > 0 ? model.ActiveOpacity : 41;
            }
            _currentOpacity = _idleOpacityPercent / 100.0;
            _fullHeight = Math.Max(MIN_AUTO_HIDE_HEIGHT + 60, model.FullHeight);
            if (loadedHeight > _fullHeight && loadedHeight > MIN_AUTO_HIDE_HEIGHT + 20)
                _fullHeight = loadedHeight;
            _clipHeight = _fullHeight;

            if (model.AutoHide)
            {
                _suppressSave = true;
                Height = MIN_AUTO_HIDE_HEIGHT;
                _suppressSave = false;
            }

            DeleteCommand = new RelayCommand(() => RequestClose?.Invoke());
            ToggleLockCommand = new RelayCommand(() => IsLocked = !IsLocked);
            ToggleAutoHideCommand = new RelayCommand(() => AutoHide = !AutoHide);
            EditCommand = new RelayCommand(() => RequestEdit?.Invoke());
            DeleteShortcutCommand = new RelayCommand(DeleteSelectedShortcut);
            UndoLastDeleteCommand = new RelayCommand(UndoLastDelete);
            ChangeFolderPortalPathCommand = new RelayCommand(() => FolderPortalPathChanged?.Invoke(_model.FolderPortalPath));
            RecenterCommand = new RelayCommand(() => RequestRecenter?.Invoke());
            ResizeToIconMultiplesCommand = new RelayCommand(ResizeToIconMultiples);

            ToggleCollapseCommand = new RelayCommand(() =>
            {
                if (_model.AutoHide)
                {
                    AutoHide = false;
                }
                else
                {
                    _fullHeight = Math.Max(100, Height);
                    _model.FullHeight = _fullHeight;
                    StartHeightAnimation(MIN_AUTO_HIDE_HEIGHT, HideDurMs);
                    _model.AutoHide = true;
                    OnPropertyChanged(nameof(AutoHide));
                    Save();
                }
            });

            CreateShortcutCommand = new RelayCommand(() => RequestCreateShortcut?.Invoke());
            RenameCommand = new RelayCommand(() => BeginEditTitle());
            RefreshCommand = new RelayCommand(() =>
            {
                RefreshHeader();
                OnPropertyChanged(nameof(FilterEnabled));
            });
            SetAsDefaultCommand = new RelayCommand(() => Services.ContainerManager.Instance.SaveDefaults(_model));
            SetIconSizeCommand = new RelayCommand<object>(param =>
            {
                if (param is string s && int.TryParse(s, out int size))
                    ShortcutIconSize = size;
            });
            SetAnimationSpeedCommand = new RelayCommand<object>(param =>
            {
                if (param is string s && int.TryParse(s, out int speed))
                    AnimationSpeedMs = speed;
            });
            ToggleOptionCommand = new RelayCommand<string>(prop =>
            {
                switch (prop)
                {
                    case "AutoHide": AutoHide = !AutoHide; break;
                    case "IsLocked": IsLocked = !IsLocked; break;
                    case "ShowBorder": ShowBorder = !ShowBorder; break;
                    case "RoundedCorners": RoundedCornersEnabled = !RoundedCornersEnabled; break;
                    case "TitleHoverEffect": TitleHoverEffect = !TitleHoverEffect; break;
                    case "ShowCounter": ShowCounter = !ShowCounter; break;
                    case "IsSvgButtonContainer": IsSvgButtonContainer = !IsSvgButtonContainer; break;
                    case "FilterAll": FilterEnabled = false; FilterType = "All"; break;
                    case "FilterPrograms": FilterEnabled = true; FilterType = "Programs"; break;
                    case "FilterDocuments": FilterEnabled = true; FilterType = "Documents"; break;
                    case "FilterFolders": FilterEnabled = true; FilterType = "Folders"; break;
                    case "IconsView": ViewMode = "Icons"; break;
                    case "DetailsView": ViewMode = "Details"; break;
                }
            });
            SortNowCommand = new RelayCommand(() =>
            {
                Services.ContainerManager.Instance.SortAllShortcuts(false);
                RefreshHeader();
            });

            _model.Shortcuts.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HeaderText));
            };
        }

        private double ShowDurMs => AnimationSpeedMs;
        private double HideDurMs => AnimationSpeedMs;

        private EventHandler? _renderingHandler;
        private EventHandler? _opacityRenderingHandler;
        private DateTime _heightAnimStart;
        private double _heightAnimFrom;
        private double _heightAnimTo;
        private double _heightAnimDuration;
        private bool _isAnimatingHeight;
        private DateTime _opacityAnimStart;
        private double _opacityAnimFrom;
        private double _opacityAnimTo;

        private void StartHeightAnimation(double to, double durationMs)
        {
            double currentClip = _isAnimatingHeight ? _clipHeight : Height;
            StopHeightAnimation();
            _heightAnimFrom = currentClip;
            _heightAnimTo = to;
            _heightAnimDuration = durationMs;
            _heightAnimStart = DateTime.UtcNow;
            _suppressSave = true;
            _isAnimatingHeight = true;

            double fullH = Math.Max(_fullHeight, Math.Max(_heightAnimFrom, _heightAnimTo));
            _model.Height = fullH;
            OnPropertyChanged(nameof(Height));

            _renderingHandler = (_, _) =>
            {
                double elapsed = (DateTime.UtcNow - _heightAnimStart).TotalMilliseconds;
                double t = Math.Min(1.0, elapsed / _heightAnimDuration);
                double eased = t * t * (3.0 - 2.0 * t);
                ClipHeight = _heightAnimFrom + (_heightAnimTo - _heightAnimFrom) * eased;

                if (t >= 1.0)
                {
                    CompositionTarget.Rendering -= _renderingHandler;
                    _renderingHandler = null;
                    _suppressSave = false;
                    _isAnimatingHeight = false;
                    if (_heightAnimTo > _fullHeight)
                    {
                        _fullHeight = _heightAnimTo;
                        _model.FullHeight = _heightAnimTo;
                    }
                    Height = _heightAnimTo;
                    ClipHeight = _heightAnimTo;
                }
            };
            CompositionTarget.Rendering += _renderingHandler;
        }

        public void StopHeightAnimation()
        {
            if (_renderingHandler != null)
            {
                CompositionTarget.Rendering -= _renderingHandler;
                _renderingHandler = null;
            }
            _isAnimatingHeight = false;
            _suppressSave = false;
        }

        private void StartOpacityAnimation(double to)
        {
            StopOpacityAnimation();
            _opacityAnimFrom = _currentOpacity;
            _opacityAnimTo = to;
            _opacityAnimStart = DateTime.UtcNow;

            _opacityRenderingHandler = (_, _) =>
            {
                double elapsed = (DateTime.UtcNow - _opacityAnimStart).TotalMilliseconds;
                double dur = 150.0;
                double t = Math.Min(1.0, elapsed / dur);
                double eased = t * t * (3.0 - 2.0 * t);
                CurrentOpacity = _opacityAnimFrom + (_opacityAnimTo - _opacityAnimFrom) * eased;

                if (t >= 1.0)
                {
                    CompositionTarget.Rendering -= _opacityRenderingHandler;
                    _opacityRenderingHandler = null;
                    CurrentOpacity = _opacityAnimTo;
                }
            };
            CompositionTarget.Rendering += _opacityRenderingHandler;
        }

        private void StopOpacityAnimation()
        {
            if (_opacityRenderingHandler != null)
            {
                CompositionTarget.Rendering -= _opacityRenderingHandler;
                _opacityRenderingHandler = null;
            }
        }

        public void NotifyResizeStarted()
        {
            StopHeightAnimation();
            _autoHideTimer?.Stop();
        }

        private void EnsureTimer()
        {
            if (_autoHideTimer != null) return;
            _autoHideTimer = new DispatcherTimer();
            _autoHideTimer.Tick += OnAutoHideTimerTick;
        }

        private void OnAutoHideTimerTick(object? sender, EventArgs e)
        {
            _autoHideTimer!.Stop();
            if (!_isHovered)
            {
                if (Height > _fullHeight)
                {
                    _fullHeight = Math.Max(100, Height);
                    _model.FullHeight = _fullHeight;
                }
                StartHeightAnimation(MIN_AUTO_HIDE_HEIGHT, HideDurMs);
            }
        }

        public void Show()
        {
            if (_fullHeight > 0)
                StartHeightAnimation(_fullHeight, ShowDurMs);
        }

        public void RestoreFullHeight()
        {
            if (Height <= MIN_AUTO_HIDE_HEIGHT + 10)
            {
                _model.Height = _fullHeight;
                OnPropertyChanged(nameof(Height));
                ClipHeight = _fullHeight;
            }
        }

        public void NotifyPositionChanged()
        {
            PositionChanged?.Invoke();
        }

        internal static (ShortcutItem? Item, int Index, ContainerViewModel? Source)? LastDeleted { get; set; }

        private void DeleteSelectedShortcut()
        {
            if (SelectedShortcut != null)
            {
                var deleted = SelectedShortcut;
                LastDeleted = (deleted, Shortcuts.IndexOf(deleted), this);
                Shortcuts.Remove(deleted);
                SelectedShortcut = null;
                Save();

                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    if (LastDeleted?.Source == this && LastDeleted?.Item == deleted)
                        LastDeleted = null;
                };
                timer.Start();
            }
        }

        public static void UndoLastDelete()
        {
            if (LastDeleted is var (item, idx, src) && item != null && src != null)
            {
                src.Shortcuts.Insert(Math.Clamp(idx, 0, src.Shortcuts.Count), item);
                src.Save();
                LastDeleted = null;
            }
        }

        public void Save()
        {
            ContainerManager.Instance.UpdateContainer(_model);
        }

        public void RefreshHeader()
        {
            OnPropertyChanged(nameof(HeaderText));
        }

        public void ResizeToIconMultiples()
        {
            const double cellSize = 60;
            const double minCellsX = 4;
            const double minCellsY = 3;

            int cellsX = Math.Max((int)Math.Round(Width / cellSize), (int)minCellsX);
            int cellsY = Math.Max((int)Math.Round(Height / cellSize), (int)minCellsY);

            _model.Width = cellsX * cellSize;
            _model.Height = cellsY * cellSize;
            _suppressSave = false;
            ClipHeight = _model.Height;
            OnPropertyChanged(nameof(Width));
            OnPropertyChanged(nameof(Height));
            Save();

            if (_model.FullHeight < _model.Height)
            {
                _model.FullHeight = _model.Height;
                _fullHeight = _model.Height;
            }
        }

        public void RefreshAllBindings()
        {
            // Reload opacity values from model into backing fields (ApplyModelTo changes model directly)
            _idleOpacityPercent = Math.Clamp(_model.IdleOpacity > 0 ? _model.IdleOpacity : 29, 0, 100);
            _activeOpacityPercent = Math.Clamp(_model.ActiveOpacity > 0 ? _model.ActiveOpacity : 41, 0, 100);
            // Start opacity animation to current target (live preview when slider changes)
            double targetOpacity = _isHovered ? ActiveTargetOpacity : IdleTargetOpacity;
            if (Math.Abs(targetOpacity - _currentOpacity) > 0.005)
                StartOpacityAnimation(targetOpacity);
            else
                CurrentOpacity = targetOpacity;
            _fullHeight = Math.Max(MIN_AUTO_HIDE_HEIGHT + 60, _model.FullHeight);
            // Handle AutoHide change (ApplyModelTo bypasses the ViewModel setter)
            if (_model.AutoHide)
            {
                EnsureTimer();
            }
            else
            {
                _autoHideTimer?.Stop();
                if (Height < _fullHeight || _isAnimatingHeight)
                    StartHeightAnimation(_fullHeight, ShowDurMs);
            }
            OnPropertyChanged("");
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
