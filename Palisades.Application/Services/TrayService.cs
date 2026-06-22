using System;
using System.Windows.Forms;

namespace Palisades.Services
{
    public class TrayService : IDisposable
    {
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _menu;
        private bool _disposed;

        public event Action? ShowMainWindowRequested;
        public event Action? CreateContainerRequested;
        public event Action? ToggleContainersRequested;
        public event Action? ExitRequested;
        public event Action? ToggleDesktopIconsRequested;
        public event Action? InstallContextMenuRequested;

        public TrayService()
        {
            _trayIcon = new NotifyIcon();
            _menu = new ContextMenuStrip();

            UpdateMenuText();

            TranslationService.Instance.LanguageChanged += UpdateMenuText;

            _trayIcon.ContextMenuStrip = _menu;
            _trayIcon.Text = "Palisades";
            _trayIcon.Visible = true;

            _trayIcon.DoubleClick += (_, _) => ShowMainWindowRequested?.Invoke();
        }

        private void UpdateMenuText()
        {
            var t = TranslationService.Instance;
            _menu.Items.Clear();
            _menu.Items.Add(t["Tray_OpenPalisades"], null, (_, _) => ShowMainWindowRequested?.Invoke());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(t["Tray_NewContainer"], null, (_, _) => CreateContainerRequested?.Invoke());
            _menu.Items.Add(t["Tray_ShowHideAll"], null, (_, _) => ToggleContainersRequested?.Invoke());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(t["Tray_ToggleDesktopIcons"], null, (_, _) => ToggleDesktopIconsRequested?.Invoke());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(t["Tray_InstallContextMenu"], null, (_, _) => InstallContextMenuRequested?.Invoke());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(t["Tray_Exit"], null, (_, _) => ExitRequested?.Invoke());
        }

        public void SetIcon(string iconPath)
        {
            try
            {
                _trayIcon.Icon = new System.Drawing.Icon(iconPath);
            }
            catch
            {
            }
        }

        public void ShowNotification(string title, string text)
        {
            _trayIcon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                TranslationService.Instance.LanguageChanged -= UpdateMenuText;
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _menu.Dispose();
                _disposed = true;
            }
        }
    }
}
