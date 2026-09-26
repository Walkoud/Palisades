using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Palisades.Converters;
using Palisades.Models;
using Palisades.Services;
using Newtonsoft.Json;

namespace Palisades.Plugins
{
    public sealed class ContainerPortalSettings
    {
        public string ContainerId { get; set; } = "";
        /// <summary>Grid | Compact | List | Details</summary>
        public string Style { get; set; } = "Grid";
        public double IconSize { get; set; } = 36;
        public int Columns { get; set; } = 0; // 0 = auto (wrap)
        public double Gap { get; set; } = 8;
        public bool ShowLabels { get; set; } = true;
        public double BgOpacity { get; set; } = 0.0;
    }

    /// <summary>Dynamic Island widget: lightweight portal showing a desktop container's
    /// shortcuts (no overlay chrome). Several display styles, customizable.</summary>
    public sealed class ContainerPortalView : Border, ICustomizableGadgetView
    {
        private readonly string _containerId;
        private ContainerPortalSettings _s = new();
        private readonly PathToImageConverter _icon = new() { ShowArrow = false };
        private readonly PathToImageConverter _iconArrow = new() { ShowArrow = true };
        private Panel _panel = new WrapPanel();
        private readonly ScrollViewer _scroll;
        private ContainerModel? _model;
        private readonly List<ShortcutItem> _hooked = new();

        public ContainerPortalView(string containerId)
        {
            _containerId = containerId ?? "";
            Background = Brushes.Transparent;
            BorderThickness = new Thickness(0);

            _scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(2)
            };
            Child = _scroll;

            Rebuild();
        }

        public void ApplyCustomSettings(string customData)
        {
            try
            {
                var s = string.IsNullOrEmpty(customData)
                    ? new ContainerPortalSettings()
                    : JsonConvert.DeserializeObject<ContainerPortalSettings>(customData) ?? new ContainerPortalSettings();
                if (!string.IsNullOrEmpty(s.ContainerId) && !string.Equals(s.ContainerId, _containerId, StringComparison.OrdinalIgnoreCase))
                    s.ContainerId = _containerId;
                _s = s;
                ResolveContainer();
                Rebuild();
            }
            catch { }
        }

        private void ResolveContainer()
        {
            // Détache les hooks précédents
            if (_model != null)
            {
                try { _model.Shortcuts.CollectionChanged -= OnShortcutsChanged; } catch { }
                _model = null;
            }
            try { _model = ContainerManager.Instance.GetContainer(_containerId); } catch { }
            if (_model != null)
            {
                try { _model.Shortcuts.CollectionChanged += OnShortcutsChanged; } catch { }
            }
        }

        private void OnShortcutsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            try { Dispatcher.InvokeAsync(Rebuild); } catch { }
        }

        private void Rebuild()
        {
            try
            {
                byte a = (byte)Math.Round(Math.Clamp(_s.BgOpacity, 0, 1) * 255);
                Background = new SolidColorBrush(Color.FromArgb(a, 0, 0, 0));
                if (a == 0) Background = Brushes.Transparent;

                bool vertical = IsVertical(_s.Style);
                if (vertical)
                {
                    _panel = new StackPanel { Orientation = Orientation.Vertical };
                }
                else if (_s.Columns > 0)
                {
                    _panel = new UniformGrid { Columns = Math.Clamp(_s.Columns, 1, 12) };
                }
                else
                {
                    _panel = new WrapPanel { Orientation = Orientation.Horizontal };
                }
                _scroll.Content = _panel;

                var shortcuts = _model?.Shortcuts;
                if (shortcuts == null) return;
                foreach (var s in shortcuts)
                    _panel.Children.Add(CreateItem(s));
            }
            catch { }
        }

        private static bool IsVertical(string style)
            => style.Equals("List", StringComparison.OrdinalIgnoreCase)
               || style.Equals("Details", StringComparison.OrdinalIgnoreCase);

        private FrameworkElement CreateItem(ShortcutItem s)
        {
            bool vertical = IsVertical(_s.Style);
            bool compact = _s.Style.Equals("Compact", StringComparison.OrdinalIgnoreCase);
            bool details = _s.Style.Equals("Details", StringComparison.OrdinalIgnoreCase);
            double iconSize = compact ? Math.Max(16, _s.IconSize * 0.7) : _s.IconSize;

            var item = new Border
            {
                Margin = new Thickness(_s.Gap / 2),
                Padding = new Thickness(4, 3, 4, 3),
                CornerRadius = new CornerRadius(6),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                ToolTip = string.IsNullOrEmpty(s.TargetPath) ? s.DisplayName : s.TargetPath
            };
            item.MouseEnter += (_, _) => item.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            item.MouseLeftButtonUp += (_, _) => Launch(s);

            var img = new Image
            {
                Width = iconSize,
                Height = iconSize,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            try
            {
                var src = (s.IconPath ?? s.TargetPath) != null
                    ? _iconArrow.Convert(s.IconPath ?? s.TargetPath, typeof(BitmapSource), s.ShortcutPath, CultureInfo.CurrentCulture) as BitmapSource
                    : null;
                img.Source = src;
            }
            catch { }

            var label = new TextBlock
            {
                Text = s.DisplayName,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = vertical ? TextAlignment.Left : TextAlignment.Center,
                HorizontalAlignment = vertical ? HorizontalAlignment.Left : HorizontalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                Margin = vertical ? new Thickness(8, 0, 0, 0) : new Thickness(0, 3, 0, 0),
                MaxWidth = vertical ? 220 : Math.Max(48, _s.IconSize + 28)
            };

            if (vertical)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(img);
                if (_s.ShowLabels || details)
                {
                    var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    texts.Children.Add(label);
                    if (details)
                    {
                        texts.Children.Add(new TextBlock
                        {
                            Text = s.DisplayType ?? "",
                            FontSize = 9,
                            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8E, 0x96)),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            MaxWidth = 220
                        });
                    }
                    row.Children.Add(texts);
                }
                item.Child = row;
            }
            else
            {
                var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                stack.Children.Add(img);
                if (_s.ShowLabels && !compact)
                    stack.Children.Add(label);
                item.Child = stack;
            }
            return item;
        }

        private static void Launch(ShortcutItem s)
        {
            try
            {
                if (s.IsUrl && !string.IsNullOrEmpty(s.UrlTarget))
                {
                    Process.Start(new ProcessStartInfo { FileName = s.UrlTarget, UseShellExecute = true });
                    return;
                }
                string path = !string.IsNullOrEmpty(s.ShortcutPath) ? s.ShortcutPath : s.TargetPath;
                if (string.IsNullOrEmpty(path)) return;
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = s.Arguments ?? "",
                    WorkingDirectory = s.WorkingDirectory ?? "",
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
