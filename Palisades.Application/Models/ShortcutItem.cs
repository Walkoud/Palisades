using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using Palisades.Services;

namespace Palisades.Models
{
    public class ShortcutItem : INotifyPropertyChanged
    {
        [JsonIgnore]
        public static bool ShowFileExtensions { get; set; } = true;

        private string _name = string.Empty;
        private string _targetPath = string.Empty;
        private string _arguments = string.Empty;
        private string _iconPath = string.Empty;
        private string? _shortcutPath;
        private int _iconIndex;
        private string _workingDirectory = string.Empty;
        private bool _isUrl;
        private string _urlTarget = string.Empty;
        private string? _svgContent;
        private string? _hotkey;

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        public string TargetPath
        {
            get => _targetPath;
            set
            {
                if (_targetPath != value)
                {
                    _targetPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayName));
                    OnPropertyChanged(nameof(DisplayType));
                }
            }
        }

        public string Arguments
        {
            get => _arguments;
            set
            {
                if (_arguments != value)
                {
                    _arguments = value;
                    OnPropertyChanged();
                }
            }
        }

        public string IconPath
        {
            get => _iconPath;
            set
            {
                if (_iconPath != value)
                {
                    _iconPath = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>Original .lnk/.url file path (for shortcut arrow overlay).</summary>
        public string? ShortcutPath
        {
            get => _shortcutPath;
            set
            {
                if (_shortcutPath != value)
                {
                    _shortcutPath = value;
                    OnPropertyChanged();
                }
            }
        }

        public int IconIndex
        {
            get => _iconIndex;
            set
            {
                if (_iconIndex != value)
                {
                    _iconIndex = value;
                    OnPropertyChanged();
                }
            }
        }

        public string WorkingDirectory
        {
            get => _workingDirectory;
            set
            {
                if (_workingDirectory != value)
                {
                    _workingDirectory = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsUrl
        {
            get => _isUrl;
            set
            {
                if (_isUrl != value)
                {
                    _isUrl = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayType));
                }
            }
        }

        public string UrlTarget
        {
            get => _urlTarget;
            set
            {
                if (_urlTarget != value)
                {
                    _urlTarget = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        public string? SvgContent
        {
            get => _svgContent;
            set
            {
                if (_svgContent != value)
                {
                    _svgContent = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        public string? Hotkey
        {
            get => _hotkey;
            set
            {
                if (_hotkey != value)
                {
                    _hotkey = value;
                    OnPropertyChanged();
                }
            }
        }

        [JsonIgnore]
        public string DisplayType
        {
            get
            {
                if (IsUrl) return TranslationService.Instance["Shortcut_DisplayType_WebLink"];
                if (string.IsNullOrEmpty(TargetPath)) return TranslationService.Instance["Shortcut_DisplayType_Unknown"];
                try
                {
                    if (File.GetAttributes(TargetPath).HasFlag(FileAttributes.Directory))
                        return TranslationService.Instance["Shortcut_DisplayType_Folder"];
                }
                catch { }
                string ext = Path.GetExtension(TargetPath)?.ToLowerInvariant() ?? "";
                if (ext == ".exe" || ext == ".msi" || ext == ".bat" || ext == ".cmd" || ext == ".com")
                    return TranslationService.Instance["Shortcut_DisplayType_Program"];
                if (ext == ".url" || ext == ".lnk") return TranslationService.Instance["Shortcut_DisplayType_Shortcut"];
                return TranslationService.Instance["Shortcut_DisplayType_File"];
            }
        }

        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Name))
                {
                    if (ShowFileExtensions && !string.IsNullOrEmpty(ShortcutPath))
                    {
                        var ext = Path.GetExtension(ShortcutPath);
                        // For .lnk files, show the target's extension instead
                        if (string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(TargetPath))
                            ext = Path.GetExtension(TargetPath);

                        if (!string.IsNullOrEmpty(ext) && !Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                            return Name + ext;
                    }
                    return Name;
                }
                if (!string.IsNullOrEmpty(TargetPath))
                    return Path.GetFileNameWithoutExtension(TargetPath);
                return TranslationService.Instance["Shortcut_FallbackName"];
            }
        }

        public void NotifyDisplayNameChanged()
        {
            OnPropertyChanged(nameof(DisplayName));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // Native IShellLink COM interface — no external package needed
        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [Guid("0000010B-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            void IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        private const uint SLGP_RAWPATH = 0x00000004;

        public static ShortcutItem? FromLnk(string lnkPath)
        {
            try
            {
                if (!File.Exists(lnkPath))
                    return null;

                var link = (IShellLinkW)new ShellLink();
                var persist = (IPersistFile)link;
                persist.Load(lnkPath, 0); // STGM_READ

                var file = new StringBuilder(260);

                // Resolve and get the path
                link.GetPath(file, file.Capacity, IntPtr.Zero, SLGP_RAWPATH);

                var targetPath = file.ToString();

                // Get working directory
                var workDir = new StringBuilder(260);
                link.GetWorkingDirectory(workDir, workDir.Capacity);

                // Get arguments
                var args = new StringBuilder(260);
                link.GetArguments(args, args.Capacity);

                // Get icon location
                var iconPath = new StringBuilder(260);
                int iconIndex = 0;
                link.GetIconLocation(iconPath, iconPath.Capacity, out iconIndex);

                var item = new ShortcutItem
                {
                    Name = Path.GetFileNameWithoutExtension(lnkPath),
                    TargetPath = targetPath,
                    Arguments = args.ToString().TrimEnd('\0'),
                    WorkingDirectory = workDir.ToString().TrimEnd('\0'),
                    ShortcutPath = lnkPath,
                    IconPath = lnkPath,
                    IconIndex = iconIndex
                };

                return item;
            }
            catch
            {
                return null;
            }
        }

        public static ShortcutItem? FromUrl(string urlPath)
        {
            try
            {
                if (!File.Exists(urlPath))
                    return null;

                string[] lines = File.ReadAllLines(urlPath);
                string? targetUrl = null;
                foreach (var line in lines)
                {
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                    {
                        targetUrl = line[4..].Trim();
                        break;
                    }
                }

                if (targetUrl == null) return null;

                return new ShortcutItem
                {
                    Name = Path.GetFileNameWithoutExtension(urlPath),
                    IsUrl = true,
                    UrlTarget = targetUrl,
                    ShortcutPath = urlPath,
                    IconPath = FindBrowserIcon()
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Resolve a .lnk file's target path without creating a ShortcutItem instance.
        /// </summary>
        public static string? GetLnkTargetPath(string lnkPath)
        {
            try
            {
                if (!File.Exists(lnkPath))
                    return null;

                var link = (IShellLinkW)new ShellLink();
                var persist = (IPersistFile)link;
                persist.Load(lnkPath, 0);

                var file = new StringBuilder(260);
                link.GetPath(file, file.Capacity, IntPtr.Zero, SLGP_RAWPATH);

                var path = file.ToString();
                return string.IsNullOrEmpty(path) ? null : path;
            }
            catch
            {
                return null;
            }
        }

        private static string FindBrowserIcon()
        {
            string[] browsers =
            {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files\Mozilla Firefox\firefox.exe",
                @"C:\Program Files (x86)\Mozilla Firefox\firefox.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Windows\System32\msedge.exe",
                @"%USERPROFILE%\AppData\Local\Microsoft\WindowsApps\msedge.exe",
                @"C:\Program Files\Internet Explorer\iexplore.exe"
            };

            foreach (var browser in browsers)
            {
                var expanded = Environment.ExpandEnvironmentVariables(browser);
                if (File.Exists(expanded))
                    return expanded;
            }

            return string.Empty;
        }
    }
}
