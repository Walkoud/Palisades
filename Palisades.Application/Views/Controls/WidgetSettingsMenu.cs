using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Palisades.Services;
using Palisades.ViewModels;
using RadioSettings = Palisades.Plugins.RadioSettings;

namespace Palisades.Views.Controls
{
    /// <summary>Widget settings context menu shared by the desktop widget wrapper and
    /// the Dynamic Island. Reads/writes a gadget's CustomData JSON through delegates
    /// so each host persists to its own store (desktop item vs GadgetTypeDefaults).</summary>
    internal static class WidgetSettingsMenu
    {
        /// <summary>Builds the type-specific "settings" items into <paramref name="host"/>.
        /// Returns true if anything was added.</summary>
        public static bool Build(ItemsControl host, string gadgetType, string data, UIElement? liveView, Action<string> save)
        {
            var tr = TranslationService.Instance;
            if (string.IsNullOrEmpty(gadgetType)) return false;

            if (Eq(gadgetType, "Clock"))
            {
                AddSeparator(host);
                var it = new MenuItem { Header = tr["Widget_Ctx_ClockSettings"] };
                BuildClock(it, data, save);
                host.Items.Add(it);
                return true;
            }
            if (Eq(gadgetType, "SystemMonitor"))
            {
                AddSeparator(host);
                var it = new MenuItem { Header = tr["Widget_Ctx_MonitorSettings"] };
                BuildSysMon(it, data, save);
                host.Items.Add(it);
                return true;
            }
            if (Eq(gadgetType, "NowPlaying"))
            {
                AddSeparator(host);
                if (liveView is Palisades.Plugins.NowPlayingView npView)
                {
                    var sourceItem = new MenuItem { Header = tr["Widget_Ctx_NpSource"] };
                    BuildNowPlayingSource(npView, sourceItem, data, save);
                    host.Items.Add(sourceItem);
                }
                var it = new MenuItem { Header = tr["Widget_Ctx_NpSettings"] };
                BuildNowPlaying(it, data, save);
                host.Items.Add(it);
                return true;
            }
            if (Eq(gadgetType, "Radio"))
            {
                AddSeparator(host);
                var it = new MenuItem { Header = tr["Widget_Ctx_RadioSettings"] };
                BuildRadio(it, data, save);
                host.Items.Add(it);
                return true;
            }
            if (Eq(gadgetType, "Football"))
            {
                AddSeparator(host);
                if (liveView is Palisades.Plugins.FootballView fbView)
                {
                    var refreshItem = new MenuItem { Header = tr["Widget_Ctx_FootballRefresh"] };
                    refreshItem.Click += (_, _) => fbView.RefreshNowAsync();
                    host.Items.Add(refreshItem);

                    var favItem = new MenuItem { Header = tr["Widget_Ctx_FootballFavorites"] };
                    favItem.Click += (_, _) =>
                    {
                        try { new Palisades.Plugins.FootballTeamSearchWindow(fbView).Show(); } catch { }
                    };
                    host.Items.Add(favItem);
                }
                return true;
            }
            return false;
        }

        private static bool Eq(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
        private static void AddSeparator(ItemsControl m) { if (m.Items.Count > 0 && m.Items[m.Items.Count - 1] is not Separator) m.Items.Add(new Separator()); }

        private static T Load<T>(string data, Func<T> fallback) where T : class
        {
            try { if (!string.IsNullOrEmpty(data)) return Newtonsoft.Json.JsonConvert.DeserializeObject<T>(data) ?? fallback(); }
            catch { }
            return fallback();
        }

        // ---------------- Clock ----------------
        private static void BuildClock(MenuItem parent, string data, Action<string> save)
        {
            var tr = TranslationService.Instance;
            var s = Load(data, () => new MainViewModel.ClockSettings());

            var sec = new MenuItem { Header = tr["Widget_Ctx_ShowSeconds"], IsCheckable = true, IsChecked = s.ShowSeconds };
            sec.Click += (_, _) => { s.ShowSeconds = !s.ShowSeconds; save(Newtonsoft.Json.JsonConvert.SerializeObject(s)); };
            parent.Items.Add(sec);

            var fmt = new MenuItem { Header = tr["Widget_Ctx_24Hour"], IsCheckable = true, IsChecked = s.Is24Hour };
            fmt.Click += (_, _) => { s.Is24Hour = !s.Is24Hour; save(Newtonsoft.Json.JsonConvert.SerializeObject(s)); };
            parent.Items.Add(fmt);

            var colorMenu = new MenuItem { Header = tr["Widget_Ctx_ClockColor"] };
            string[] colors = { tr["Widget_Ctx_Col_IceBlue"], tr["Widget_Ctx_Col_White"], tr["Widget_Ctx_Col_Matrix"], tr["Widget_Ctx_Col_Amber"], tr["Widget_Ctx_Col_Cyber"] };
            string[] hex = { "#7DD3FC", "#FFFFFF", "#4AF626", "#FFB000", "#FF3E3E" };
            for (int i = 0; i < colors.Length; i++)
            {
                string code = hex[i];
                var it = new MenuItem { Header = colors[i], IsCheckable = true, IsChecked = s.Color.Equals(code, StringComparison.OrdinalIgnoreCase) };
                it.Click += (_, _) => { s.Color = code; save(Newtonsoft.Json.JsonConvert.SerializeObject(s)); };
                colorMenu.Items.Add(it);
            }
            parent.Items.Add(colorMenu);

            var sizeMenu = new MenuItem { Header = tr["Widget_Ctx_FontSize"] };
            double[] sizes = { 24, 36, 48, 64 };
            string[] names = { tr["Widget_Ctx_Size_Small"], tr["Widget_Ctx_Size_Medium"], tr["Widget_Ctx_Size_Large"], tr["Widget_Ctx_Size_Huge"] };
            for (int i = 0; i < sizes.Length; i++)
            {
                double sz = sizes[i];
                var it = new MenuItem { Header = names[i], IsCheckable = true, IsChecked = Math.Abs(s.FontSize - sz) < 0.1 };
                it.Click += (_, _) => { s.FontSize = sz; save(Newtonsoft.Json.JsonConvert.SerializeObject(s)); };
                sizeMenu.Items.Add(it);
            }
            parent.Items.Add(sizeMenu);
        }

        // ---------------- System Monitor ----------------
        private static void BuildSysMon(MenuItem parent, string data, Action<string> save)
        {
            var tr = TranslationService.Instance;
            var s = Load(data, () => new MainViewModel.SysMonSettings());

            var cpu = new MenuItem { Header = tr["Widget_Ctx_ShowCpu"], IsCheckable = true, IsChecked = s.ShowCpu };
            cpu.Click += (_, _) => { s.ShowCpu = !s.ShowCpu; save(Newtonsoft.Json.JsonConvert.SerializeObject(s)); };
            parent.Items.Add(cpu);

            var ram = new MenuItem { Header = tr["Widget_Ctx_ShowRam"], IsCheckable = true, IsChecked = s.ShowRam };
            ram.Click += (_, _) => { s.ShowRam = !s.ShowRam; save(Newtonsoft.Json.JsonConvert.SerializeObject(s)); };
            parent.Items.Add(ram);

            var rateMenu = new MenuItem { Header = tr["Widget_Ctx_RefreshRate"] };
            double[] rates = { 0.5, 1.0, 1.5, 2.0, 5.0 };
            string[] names = { tr["Widget_Ctx_Rate_Fast"], tr["Widget_Ctx_Rate_Normal"], tr["Widget_Ctx_Rate_Medium"], tr["Widget_Ctx_Rate_Slow"], tr["Widget_Ctx_Rate_VerySlow"] };
            for (int i = 0; i < rates.Length; i++)
            {
                double r = rates[i];
                var it = new MenuItem { Header = names[i], IsCheckable = true, IsChecked = Math.Abs(s.Interval - r) < 0.1 };
                it.Click += (_, _) => { s.Interval = r; save(Newtonsoft.Json.JsonConvert.SerializeObject(s)); };
                rateMenu.Items.Add(it);
            }
            parent.Items.Add(rateMenu);
        }

        // ---------------- Now Playing ----------------
        private static void BuildNowPlayingSource(Palisades.Plugins.NowPlayingView view, MenuItem parent, string data, Action<string> save)
        {
            var tr = TranslationService.Instance;

            var auto = new MenuItem { Header = tr["Widget_Ctx_NpSourceAuto"], IsCheckable = true, IsChecked = !view.IsManualSource };
            auto.Click += (_, _) => PinSource(view, null, data, save);
            parent.Items.Add(auto);

            if (view.SourceSessions.Count > 0)
            {
                parent.Items.Add(new Separator());
                bool anyChecked = false;
                foreach (var session in view.SourceSessions)
                {
                    bool isPlaying = false;
                    try { isPlaying = session.GetPlaybackInfo().PlaybackStatus == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; } catch { }
                    bool isChecked = view.IsManualSource && ReferenceEquals(session, view.ActiveSession);
                    anyChecked |= isChecked;
                    var item = new MenuItem { Header = (isPlaying ? "\u25B6 " : "") + view.SourceDisplayName(session), IsCheckable = true, IsChecked = isChecked };
                    item.Click += (_, _) => PinSource(view, session, data, save);
                    parent.Items.Add(item);
                }
                if (view.IsManualSource && !anyChecked)
                {
                    parent.Items.Add(new Separator());
                    parent.Items.Add(new MenuItem { Header = "\u25B6 " + view.PinnedSourceName, IsCheckable = true, IsChecked = true, IsEnabled = false });
                }
            }
            else if (view.IsManualSource)
            {
                parent.Items.Add(new Separator());
                parent.Items.Add(new MenuItem { Header = "\u25B6 " + view.PinnedSourceName, IsCheckable = true, IsChecked = true, IsEnabled = false });
            }
        }

        private static void PinSource(Palisades.Plugins.NowPlayingView view, Windows.Media.Control.GlobalSystemMediaTransportControlsSession? session, string data, Action<string> save)
        {
            var settings = Load(data, () => new MainViewModel.NowPlayingSettings());
            try { settings.ForcedSourceAppId = session?.SourceAppUserModelId ?? ""; }
            catch { settings.ForcedSourceAppId = ""; }
            save(Newtonsoft.Json.JsonConvert.SerializeObject(settings));
        }

        private static void BuildNowPlaying(MenuItem parent, string data, Action<string> save)
        {
            var tr = TranslationService.Instance;
            var s = Load(data, () => new MainViewModel.NowPlayingSettings());
            void Persist() => save(Newtonsoft.Json.JsonConvert.SerializeObject(s));

            var layoutMenu = new MenuItem { Header = tr["Widget_Ctx_NpLayout"] };
            string[] layouts = { "Classic", "Compact", "Fluent", "Taskbar", "TaskbarSlim" };
            string[] layoutKeys = { "Widget_Ctx_NpLayout_Classic", "Widget_Ctx_NpLayout_Compact", "Widget_Ctx_NpLayout_Fluent", "Widget_Ctx_NpLayout_Taskbar", "Widget_Ctx_NpLayout_TaskbarSlim" };
            for (int i = 0; i < layouts.Length; i++)
            {
                string l = layouts[i];
                var it = new MenuItem { Header = tr[layoutKeys[i]], IsCheckable = true, IsChecked = s.Layout.Equals(l, StringComparison.OrdinalIgnoreCase) };
                it.Click += (_, _) => { s.Layout = l; Persist(); };
                layoutMenu.Items.Add(it);
            }
            parent.Items.Add(layoutMenu);

            Check(parent, tr["Widget_Ctx_NpShowSeekBar"], s.ShowSeekBar, v => { s.ShowSeekBar = v; Persist(); });
            Check(parent, tr["Widget_Ctx_NpShowControls"], s.ShowControls, v => { s.ShowControls = v; Persist(); });
            Check(parent, tr["Widget_Ctx_NpShowAppLabel"], s.ShowAppLabel, v => { s.ShowAppLabel = v; Persist(); });
            Check(parent, tr["Widget_Ctx_NpShowCover"], s.ShowCover, v => { s.ShowCover = v; Persist(); });
            Check(parent, tr["Widget_Ctx_NpDarkMode"], s.DarkMode, v => { s.DarkMode = v; Persist(); });

            ColorMenu(parent, tr["Widget_Ctx_NpAccentColor"], s.AccentColor, code => { s.AccentColor = code; Persist(); },
                new[] { "#FF7DD3FC", "#FFFFFFFF", "#FF4AF626", "#FFFFB000", "#FFFF3E3E", "#FFFF71CE", "#FFA855F7", "#FF2DD4BF", "#FFFACC15", "#FFFB923C", "#FFFB7185", "#FFA3E635" });
            ColorMenu(parent, tr["Widget_Ctx_NpButtonsColor"], s.ButtonsColor, code => { s.ButtonsColor = code; Persist(); },
                new[] { "#A0FFFFFF", "#FF7DD3FC", "#FFFFFFFF", "#FF4AF626", "#FFFFB000", "#FFFF3E3E", "#FFFF71CE", "#FFA855F7", "#FF2DD4BF", "#FFFACC15", "#FFFB923C", "#FFFB7185", "#FFA3E635" },
                defaultLabelKey: "Widget_Ctx_NpButtons_Default");
            ColorMenu(parent, tr["Widget_Ctx_NpTextColor"], s.TextColor, code => { s.TextColor = code; Persist(); },
                new[] { "#FFF0F0F0", "#FF7DD3FC", "#FFFFFFFF", "#FF4AF626", "#FFFFB000", "#FFFF3E3E", "#FFFF71CE", "#FFA855F7", "#FF2DD4BF", "#FFFACC15", "#FFFB923C", "#FFFB7185", "#FFA3E635" },
                defaultLabelKey: "Widget_Ctx_NpButtons_Default");
        }

        // ---------------- Radio ----------------
        private static void BuildRadio(MenuItem parent, string data, Action<string> save)
        {
            var tr = TranslationService.Instance;
            var s = Load(data, () => new RadioSettings());
            void Persist() => save(Newtonsoft.Json.JsonConvert.SerializeObject(s));

            Check(parent, tr["Db_RadioNowPlaying"] ?? "Show in Now Playing", s.ShowInNowPlaying, v => { s.ShowInNowPlaying = v; Persist(); });
            Check(parent, tr["Db_RadioDiscord"] ?? "Show on Discord", s.ShowOnDiscord, v => { s.ShowOnDiscord = v; Persist(); });

            var artMenu = new MenuItem { Header = tr["Db_RadioArtwork"] ?? "Artwork" };
            void AddArt(string label, string val)
            {
                var it = new MenuItem { Header = label, IsCheckable = true, IsChecked = string.Equals(s.ArtworkSource, val, StringComparison.OrdinalIgnoreCase) };
                it.Click += (_, _) => { s.ArtworkSource = val; Persist(); };
                artMenu.Items.Add(it);
            }
            AddArt(tr["Db_RadioArtwork_Favicon"] ?? "Station logo", "favicon");
            AddArt(tr["Db_RadioArtwork_Itunes"] ?? "iTunes cover", "itunes");
            parent.Items.Add(artMenu);

            ColorMenu(parent, tr["Db_RadioAccent"] ?? "Accent color", s.AccentColor, code => { s.AccentColor = code; Persist(); },
                new[] { "#7DD3FC", "#FFFFFF", "#4AF626", "#FFB000", "#FF3E3E", "#FF71CE", "#A855F7", "#2DD4BF", "#FACC15", "#FB923C", "#FB7185", "#A3E635" });
        }

        // ---------------- helpers ----------------
        private static void Check(MenuItem parent, string header, bool value, Action<bool> set)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = value };
            item.Click += (_, _) => set(!value);
            parent.Items.Add(item);
        }

        private static void ColorMenu(MenuItem parent, string header, string current, Action<string> set, string[] hex, string? defaultLabelKey = null)
        {
            var tr = TranslationService.Instance;
            string[] names = { tr["Widget_Ctx_Col_IceBlue"], tr["Widget_Ctx_Col_White"], tr["Widget_Ctx_Col_Matrix"], tr["Widget_Ctx_Col_Amber"], tr["Widget_Ctx_Col_Cyber"], tr["Widget_Ctx_Col_KawaiiPink"], tr["Widget_Ctx_Col_Purple"], tr["Widget_Ctx_Col_Teal"], tr["Widget_Ctx_Col_Gold"], tr["Widget_Ctx_Col_Orange"], tr["Widget_Ctx_Col_Rose"], tr["Widget_Ctx_Col_Lime"] };
            var menu = new MenuItem { Header = header };
            for (int i = 0; i < hex.Length && i < names.Length; i++)
            {
                string code = hex[i];
                string label = (i == 0 && defaultLabelKey != null) ? (tr[defaultLabelKey] ?? names[i]) : names[i];
                var it = new MenuItem { Header = label, IsCheckable = true, IsChecked = string.Equals(current, code, StringComparison.OrdinalIgnoreCase) };
                it.Click += (_, _) => set(code);
                menu.Items.Add(it);
            }
            parent.Items.Add(menu);
        }
    }
}

