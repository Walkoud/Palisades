using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Palisades.Plugins
{
    public class SystemMonitorPlugin : IPlugin
    {
        public string Name => "System Resource Monitor";
        public string Id => "com.palisades.plugin.sysmon";
        public string Version => "1.0.0";
        public string Author => "Palisades Team";
        public string Description => "Displays live system-wide CPU and RAM utilization statistics.";

        public void OnLoad(PluginContext context)
        {
            context.RegisterGadget(
                gadgetType: "SystemMonitor",
                name: "System Monitor",
                viewFactory: () => new SystemMonitorView(),
                defaultWidth: 260,
                defaultHeight: 140
            );
        }

        public void OnUnload()
        {
        }
    }

    public class SystemMonitorView : Border, ICustomizableGadgetView
    {
        private DispatcherTimer? _timer;
        private readonly ProgressBar _cpuProgress;
        private readonly TextBlock _cpuText;
        private readonly ProgressBar _ramProgress;
        private readonly TextBlock _ramText;
        private readonly StackPanel _cpuSection;
        private readonly StackPanel _ramSection;
        private double _refreshInterval = 1.5;

        private class SysMonSettings
        {
            public bool ShowCpu { get; set; } = true;
            public bool ShowRam { get; set; } = true;
            public double Interval { get; set; } = 1.5;
        }

        // DLL Imports for CPU and RAM monitoring
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;

            public ulong ToTicks()
            {
                return ((ulong)dwHighDateTime << 32) | dwLowDateTime;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] ref MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public void Init()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        private ulong _lastIdleTime;
        private ulong _lastKernelTime;
        private ulong _lastUserTime;
        private bool _isFirstSample = true;

        public SystemMonitorView()
        {
            Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF));
            CornerRadius = new CornerRadius(8);
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF));
            BorderThickness = new Thickness(1);
            Padding = new Thickness(12);

            var stack = new StackPanel();

            // CPU section
            _cpuSection = new StackPanel();
            var cpuHeader = new Grid();
            cpuHeader.Children.Add(new TextBlock { Text = "CPU Utilization", Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold });
            _cpuText = new TextBlock { Text = "0%", Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC)), FontSize = 12, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right };
            cpuHeader.Children.Add(_cpuText);
            _cpuSection.Children.Add(cpuHeader);

            _cpuProgress = new ProgressBar
            {
                Height = 8,
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x02, 0x84, 0xC7)), // Ocean Blue
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 4, 0, 16),
                Minimum = 0,
                Maximum = 100
            };
            _cpuSection.Children.Add(_cpuProgress);
            stack.Children.Add(_cpuSection);

            // RAM section
            _ramSection = new StackPanel();
            var ramHeader = new Grid();
            ramHeader.Children.Add(new TextBlock { Text = "Memory Utilization", Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold });
            _ramText = new TextBlock { Text = "0%", Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x84, 0xFC)), FontSize = 12, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right };
            ramHeader.Children.Add(_ramText);
            _ramSection.Children.Add(ramHeader);

            _ramProgress = new ProgressBar
            {
                Height = 8,
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x93, 0x33, 0xEA)), // Dark Purple
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 4, 0, 0),
                Minimum = 0,
                Maximum = 100
            };
            _ramSection.Children.Add(_ramProgress);
            stack.Children.Add(_ramSection);

            Child = stack;

            Loaded += SystemMonitorView_Loaded;
            Unloaded += SystemMonitorView_Unloaded;
        }

        public void ApplyCustomSettings(string customData)
        {
            try
            {
                if (!string.IsNullOrEmpty(customData))
                {
                    var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<SysMonSettings>(customData);
                    if (settings != null)
                    {
                        _cpuSection.Visibility = settings.ShowCpu ? Visibility.Visible : Visibility.Collapsed;
                        _ramSection.Visibility = settings.ShowRam ? Visibility.Visible : Visibility.Collapsed;

                        _refreshInterval = Math.Clamp(settings.Interval, 0.1, 10.0);
                        if (_timer != null)
                        {
                            _timer.Interval = TimeSpan.FromSeconds(_refreshInterval);
                        }
                    }
                }
            }
            catch { }
        }

        private void SystemMonitorView_Loaded(object sender, RoutedEventArgs e)
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(_refreshInterval)
            };
            _timer.Tick += (s, ev) => UpdateStats();
            _timer.Start();
            UpdateStats();
        }

        private void SystemMonitorView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;
            }
        }

        private void UpdateStats()
        {
            // 1. CPU calculation
            if (GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime))
            {
                ulong currentIdle = idleTime.ToTicks();
                ulong currentKernel = kernelTime.ToTicks();
                ulong currentUser = userTime.ToTicks();

                if (!_isFirstSample)
                {
                    ulong idleDiff = currentIdle - _lastIdleTime;
                    ulong kernelDiff = currentKernel - _lastKernelTime;
                    ulong userDiff = currentUser - _lastUserTime;

                    ulong totalSysDiff = kernelDiff + userDiff;

                    if (totalSysDiff > 0)
                    {
                        double cpuLoad = (double)(totalSysDiff - idleDiff) * 100.0 / (double)totalSysDiff;
                        cpuLoad = Math.Clamp(cpuLoad, 0.0, 100.0);

                        _cpuProgress.Value = cpuLoad;
                        _cpuText.Text = $"{(int)cpuLoad}%";
                    }
                }
                else
                {
                    _isFirstSample = false;
                }

                _lastIdleTime = currentIdle;
                _lastKernelTime = currentKernel;
                _lastUserTime = currentUser;
            }

            // 2. RAM calculation
            var memStatus = new MEMORYSTATUSEX();
            memStatus.Init();
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                uint memLoad = memStatus.dwMemoryLoad;
                double totalGb = (double)memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                double usedGb = totalGb * memLoad / 100.0;

                _ramProgress.Value = memLoad;
                _ramText.Text = $"{usedGb:F1} GB / {totalGb:F1} GB ({memLoad}%)";
            }
        }
    }
}
