using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Palisades.Helpers;
using Palisades.Models;
using Palisades.Services;

namespace Palisades.Views
{
    public partial class SvgButtonEditWindow : Window
    {
        public string ButtonName => NameTextBox.Text.Trim();
        public string TargetPath => TargetPathTextBox.Text.Trim();
        public string TargetArguments => ArgumentsTextBox.Text.Trim();
        public string? Hotkey => string.IsNullOrWhiteSpace(HotkeyTextBox.Text) ? null : HotkeyTextBox.Text.Trim();
        public string? SvgContent => string.IsNullOrWhiteSpace(SvgTextBox.Text) ? null : SvgTextBox.Text.Trim();

        public SvgButtonEditWindow(ShortcutItem? existingItem = null)
        {
            InitializeComponent();

            if (existingItem != null)
            {
                NameTextBox.Text = existingItem.Name;
                TargetPathTextBox.Text = existingItem.IsUrl ? existingItem.UrlTarget : existingItem.TargetPath;
                ArgumentsTextBox.Text = existingItem.Arguments;
                HotkeyTextBox.Text = existingItem.Hotkey ?? string.Empty;
                SvgTextBox.Text = existingItem.SvgContent ?? string.Empty;
            }

            UpdatePreview();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void BrowseFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Shortcut Target File",
                Filter = "All Files (*.*)|*.*|Programs (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd"
            };

            if (dialog.ShowDialog(this) == true)
            {
                TargetPathTextBox.Text = dialog.FileName;
                if (string.IsNullOrWhiteSpace(NameTextBox.Text))
                {
                    NameTextBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
                }
            }
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Shortcut Target Folder",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                TargetPathTextBox.Text = dialog.SelectedPath;
                if (string.IsNullOrWhiteSpace(NameTextBox.Text))
                {
                    NameTextBox.Text = Path.GetFileName(dialog.SelectedPath);
                }
            }
        }

        private void HotkeyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;

            // Ignore raw modifier presses
            if (e.Key == Key.System || e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
                e.Key == Key.LeftAlt || e.Key == Key.RightAlt || e.Key == Key.LeftShift || e.Key == Key.RightShift ||
                e.Key == Key.LWin || e.Key == Key.RWin)
            {
                return;
            }

            var key = e.Key;
            
            // Allow clearing using Escape or Delete/Backspace
            if (key == Key.Escape || key == Key.Back || key == Key.Delete)
            {
                HotkeyTextBox.Text = string.Empty;
                return;
            }

            var modifiers = Keyboard.Modifiers;
            var sb = new StringBuilder();
            if (modifiers.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
            if (modifiers.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
            if (modifiers.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
            if (modifiers.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");

            sb.Append(key.ToString());
            HotkeyTextBox.Text = sb.ToString();
        }

        private void ImportSvg_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import SVG File",
                Filter = "Vector Image (*.svg)|*.svg"
            };

            if (dialog.ShowDialog(this) == true)
            {
                try
                {
                    string content = File.ReadAllText(dialog.FileName);
                    SvgTextBox.Text = content;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, string.Format(TranslationService.Instance["Msg_FailedLoadSvg"], ex.Message), TranslationService.Instance["Msg_Error"], MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ClearSvg_Click(object sender, RoutedEventArgs e)
        {
            SvgTextBox.Text = string.Empty;
        }

        private void SvgTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (string.IsNullOrWhiteSpace(SvgTextBox.Text))
            {
                SvgPreviewImage.Source = null;
                return;
            }

            // Render live preview with white foreground color
            var drawing = SvgRenderer.RenderSvg(SvgTextBox.Text, Brushes.White);
            SvgPreviewImage.Source = drawing;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TargetPathTextBox.Text))
            {
                MessageBox.Show(this, TranslationService.Instance["Msg_TargetPathRequired"], TranslationService.Instance["Msg_ValidationError"], MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(NameTextBox.Text))
            {
                MessageBox.Show(this, TranslationService.Instance["Msg_NameRequired"], TranslationService.Instance["Msg_ValidationError"], MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void PresetBrowser_Click(object sender, RoutedEventArgs e)
        {
            SvgTextBox.Text = @"<svg viewBox=""0 0 24 24""><circle cx=""12"" cy=""12"" r=""10"" stroke=""currentColor"" stroke-width=""2"" fill=""none""/><path d=""M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"" stroke=""currentColor"" stroke-width=""2"" fill=""none""/><path d=""M2 12h20"" stroke=""currentColor"" stroke-width=""2""/></svg>";
        }

        private void PresetFolder_Click(object sender, RoutedEventArgs e)
        {
            SvgTextBox.Text = @"<svg viewBox=""0 0 24 24""><path d=""M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"" fill=""currentColor""/></svg>";
        }

        private void PresetEditor_Click(object sender, RoutedEventArgs e)
        {
            SvgTextBox.Text = @"<svg viewBox=""0 0 24 24""><path d=""M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"" fill=""currentColor""/><path d=""M14 2v6h6"" fill=""none"" stroke=""currentColor"" stroke-width=""2""/></svg>";
        }

        private void PresetGame_Click(object sender, RoutedEventArgs e)
        {
            SvgTextBox.Text = @"<svg viewBox=""0 0 24 24""><rect x=""2"" y=""6"" width=""20"" height=""12"" rx=""3"" fill=""currentColor""/><circle cx=""17"" cy=""12"" r=""1.5"" fill=""#1E1E1E""/><circle cx=""19"" cy=""10"" r=""1.5"" fill=""#1E1E1E""/><path d=""M6 12h4M8 10v4"" stroke=""#1E1E1E"" stroke-width=""2"" stroke-linecap=""round""/></svg>";
        }

        private void PresetTerminal_Click(object sender, RoutedEventArgs e)
        {
            SvgTextBox.Text = @"<svg viewBox=""0 0 24 24""><rect x=""2"" y=""3"" width=""20"" height=""18"" rx=""2"" fill=""currentColor""/><path d=""M7 8l4 4-4 4M13 16h5"" stroke=""#1E1E1E"" stroke-width=""2"" stroke-linecap=""round"" fill=""none""/></svg>";
        }

        private void PresetMusic_Click(object sender, RoutedEventArgs e)
        {
            SvgTextBox.Text = @"<svg viewBox=""0 0 24 24""><path d=""M9 18V5l12-2v13"" stroke=""currentColor"" stroke-width=""2"" fill=""none""/><circle cx=""6"" cy=""18"" r=""3"" fill=""currentColor""/><circle cx=""18"" cy=""16"" r=""3"" fill=""currentColor""/></svg>";
        }

        private void PresetMonitor_Click(object sender, RoutedEventArgs e)
        {
            SvgTextBox.Text = @"<svg viewBox=""0 0 24 24""><rect x=""2"" y=""3"" width=""20"" height=""14"" rx=""2"" stroke=""currentColor"" stroke-width=""2"" fill=""none""/><path d=""M6 21h12M12 17v4"" stroke=""currentColor"" stroke-width=""2""/><path d=""M6 10l3-4 4 5 5-3"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" fill=""none""/></svg>";
        }

        private void PresetSettings_Click(object sender, RoutedEventArgs e)
        {
            SvgTextBox.Text = @"<svg viewBox=""0 0 24 24""><circle cx=""12"" cy=""12"" r=""3"" fill=""currentColor""/><path d=""M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"" stroke=""currentColor"" stroke-width=""1"" fill=""none""/></svg>";
        }
    }
}
