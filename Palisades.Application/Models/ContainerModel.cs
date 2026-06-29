using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Newtonsoft.Json;

namespace Palisades.Models
{
    public class ContainerModel
    {
        public string Identifier { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "New Container";
        public double X { get; set; } = 100;
        public double Y { get; set; } = 100;
        public double Width { get; set; } = 500;
        public double Height { get; set; } = 400;
        /// <summary>Opacity from 0 (transparent) to 100 (fully opaque).</summary>
        public double Opacity { get; set; } = 100.0;
        /// <summary>Opacity when not hovered (0-100). Default 29.</summary>
        public double IdleOpacity { get; set; } = 29.0;
        /// <summary>Opacity when hovered (0-100). Default 41.</summary>
        public double ActiveOpacity { get; set; } = 41.0;
        public bool AutoHide { get; set; } = false;
        public int AutoHideDelayMs { get; set; } = 500;
        public bool IsLocked { get; set; }
        public bool ShowTitle { get; set; } = true;
        public string FilterPattern { get; set; } = string.Empty;
        public bool FilterEnabled { get; set; }
        public string FilterType { get; set; } = "All"; // All, Programs, Documents, Folders, Custom
        public string? FolderPortalPath { get; set; }
        public string HeaderColor { get; set; } = "#FF202020";
        public string BodyColor { get; set; } = "#FF181818";
        public string TitleColor { get; set; } = "#FFFFFFFF";
        public string LabelsColor { get; set; } = "#FFFFFFFF";
        public string? GradientEndColor { get; set; }
        public double GradientAngle { get; set; } = 0;
        public bool HeaderGradientEnabled { get; set; } = true;
        public bool BodyGradientEnabled { get; set; } = true;
        public int CornerRadius { get; set; } = 12;
        public int BodyOpacity { get; set; } = 100;
        public bool IsExpanded { get; set; } = true;
        public double CollapsedHeight { get; set; } = 52;
        public int SortOrder { get; set; }
        public bool IsVisible { get; set; } = true;
        /// <summary>True = double-click to open, False = single-click to open.</summary>
        public bool OpenOnDoubleClick { get; set; } = true;
        public double FullHeight { get; set; } = 400.0;
        public string TitleFontFamily { get; set; } = "Segoe UI";
        public double TitleFontSize { get; set; } = 11;
        public string TitleAlignment { get; set; } = "Center";
        public bool ShowBorder { get; set; } = false;
        public bool RoundedCorners { get; set; } = true;
        public bool AutoHideOnEdge { get; set; } = false;
        public bool UseShellContextMenu { get; set; } = false;
        public bool TitleHoverEffect { get; set; } = true;
        public bool IsSvgButtonContainer { get; set; } = false;
        public bool HideAddSvgButton { get; set; } = false;
        public int SvgImageSize { get; set; } = 48;
        public int SvgButtonSize { get; set; } = 72;
        public bool SvgButtonShowBg { get; set; } = true;

        /// <summary>Positions saved per screen configuration (for multi-monitor memory).</summary>
        public Dictionary<string, PositionSnapshot> ResolutionPositions { get; set; } = new();

        /// <summary>Categories this container accepts for auto-sort (e.g. "Documents", "Images").</summary>
        public List<string> AutoSortCategories { get; set; } = new();

        /// <summary>Show icon count in the title bar.</summary>
        public bool ShowCounter { get; set; }

        /// <summary>Keep original shortcuts on the desktop after sorting (default: remove them).</summary>
        public bool KeepOriginalsAfterSort { get; set; }

        /// <summary>True if this container has ever been managed by auto-sort categories.</summary>
        public bool IsAutoSortManaged { get; set; }

        /// <summary>Globally enable auto-sort of new desktop shortcuts into containers.</summary>
        public bool IsAutoSortEnabled { get; set; } = true;

        /// <summary>Target container identifier for auto-sort (null/empty = use category matching).</summary>
        public string? AutoSortTargetIdentifier { get; set; }

        /// <summary>Create automatic snapshots on display settings changes (default: true).</summary>
        public bool AutoSnapshotEnabled { get; set; } = true;

        /// <summary>Show shortcut overlay arrow on icons (default: true).</summary>
        public bool ShowShortcutArrow { get; set; } = true;

        /// <summary>Show resize handles at edges and corners (default: true).</summary>
        public bool ShowResizeHandle { get; set; } = true;

        /// <summary>Show Recycle Bin shortcut on desktop overlay (default: false).</summary>
        public bool ShowRecycleBin { get; set; } = false;

        /// <summary>Header icon size (hamburger/chevron base grid size, 6-16).</summary>
        public int HeaderIconSize { get; set; } = 9;

        /// <summary>Shortcut icon grid size (24-64).</summary>
        public int ShortcutIconSize { get; set; } = 36;

        /// <summary>Allow shortcut names to wrap into 2 lines (default: false).</summary>
        public bool TwoLineShortcuts { get; set; } = false;

        /// <summary>Container-level theme preset name, null = inherit global.</summary>
        public string? ContainerThemeName { get; set; } = "Theme";

        /// <summary>Curtain animation speed in milliseconds (100-1000).</summary>
        public int AnimationSpeedMs { get; set; } = 400;

        /// <summary>Enable curtain mode (vertical strip that opens left-to-right).</summary>
        public bool IsCurtainMode { get; set; }
        /// <summary>Header text rendering: "Vertical", "Stacked", or "Hidden".</summary>
        public string CurtainHeaderMode { get; set; } = "Vertical";
        /// <summary>Full open width in pixels when curtain is expanded.</summary>
        public double CurtainOpenWidth { get; set; } = 300;
        /// <summary>Full open height in pixels when curtain is expanded.</summary>
        public double CurtainOpenHeight { get; set; } = 300;
        /// <summary>Shortcut icon size in curtain mode (16-48).</summary>
        public int CurtainShortcutIconSize { get; set; } = 50;
        /// <summary>Curtain open direction: "LeftToRight" or "RightToLeft".</summary>
        public string CurtainDirection { get; set; } = "LeftToRight";

        // Private Box (AES-256)
        /// <summary>SHA256 hash of the container password (empty = no password).</summary>
        public string PasswordHash { get; set; } = string.Empty;
        /// <summary>AES-256 encrypted JSON of shortcuts when locked.</summary>
        public string? EncryptedShortcuts { get; set; }
        /// <summary>Whether the container is currently locked.</summary>
        public bool IsPasswordLocked { get; set; }
        /// <summary>Auto-lock private box after N seconds (0 = disabled).</summary>
        public int PrivateBoxAutoLockSeconds { get; set; }

        /// <summary>"Icons" or "Details" view mode for shortcuts.</summary>
        public string ViewMode { get; set; } = "Icons";

        public ObservableCollection<ShortcutItem> Shortcuts { get; set; } = new();
    }

    public class PositionSnapshot
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double FullHeight { get; set; }
    }
}
