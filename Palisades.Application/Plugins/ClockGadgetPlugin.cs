using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Palisades.Plugins
{
    public class ClockGadgetPlugin : IPlugin
    {
        public string Name => "Digital Clock Gadget";
        public string Id => "com.palisades.plugin.clock";
        public string Version => "1.0.0";
        public string Author => "Palisades Team";
        public string Description => "Adds a clean, glowing digital clock and date widget to your desktop overlay.";

        public void OnLoad(PluginContext context)
        {
            // Register the clock gadget with its default size
            context.RegisterGadget(
                gadgetType: "Clock",
                name: "Digital Clock",
                viewFactory: () => new ClockGadgetView(),
                defaultWidth: 260,
                defaultHeight: 110
            );
        }

        public void OnUnload()
        {
            // No cleanup needed; instances will unload themselves from WPF visual tree
        }
    }

    public class ClockGadgetView : Border, ICustomizableGadgetView
    {
        private readonly TextBlock _timeBlock;
        private readonly TextBlock _dateBlock;
        private DispatcherTimer? _timer;
        private string _timeFormat = "HH:mm:ss";

        private class ClockSettings
        {
            public bool ShowSeconds { get; set; } = true;
            public bool Is24Hour { get; set; } = true;
            public string Color { get; set; } = "#7DD3FC";
            public double FontSize { get; set; } = 36;
        }

        public ClockGadgetView()
        {
            Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF));
            CornerRadius = new CornerRadius(8);
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF));
            BorderThickness = new Thickness(1);

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.8, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });

            _timeBlock = new TextBlock
            {
                Text = "00:00:00",
                FontSize = 36,
                FontWeight = FontWeights.Light,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC)), // Ice blue / Light blue
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 0)
            };
            Grid.SetRow(_timeBlock, 0);

            _dateBlock = new TextBlock
            {
                Text = "Loading date...",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x9C, 0xAE)), // Gray
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 0, 0)
            };
            Grid.SetRow(_dateBlock, 1);

            mainGrid.Children.Add(_timeBlock);
            mainGrid.Children.Add(_dateBlock);
            Child = mainGrid;

            Loaded += ClockGadgetView_Loaded;
            Unloaded += ClockGadgetView_Unloaded;
            
            UpdateTime();
        }

        public void ApplyCustomSettings(string customData)
        {
            try
            {
                if (!string.IsNullOrEmpty(customData))
                {
                    var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<ClockSettings>(customData);
                    if (settings != null)
                    {
                        // Apply color
                        try
                        {
                            var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(settings.Color);
                            _timeBlock.Foreground = new SolidColorBrush(color);
                        }
                        catch { }

                        // Apply font size
                        _timeBlock.FontSize = settings.FontSize;

                        // Apply time format
                        if (settings.Is24Hour)
                        {
                            _timeFormat = settings.ShowSeconds ? "HH:mm:ss" : "HH:mm";
                        }
                        else
                        {
                            _timeFormat = settings.ShowSeconds ? "h:mm:ss tt" : "h:mm tt";
                        }

                        UpdateTime();
                    }
                }
            }
            catch { }
        }

        private void ClockGadgetView_Loaded(object sender, RoutedEventArgs e)
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += (s, ev) => UpdateTime();
            _timer.Start();
        }

        private void ClockGadgetView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;
            }
        }

        private void UpdateTime()
        {
            var now = DateTime.Now;
            _timeBlock.Text = now.ToString(_timeFormat);
            _dateBlock.Text = now.ToString("dddd, d MMMM yyyy");
        }
    }
}
