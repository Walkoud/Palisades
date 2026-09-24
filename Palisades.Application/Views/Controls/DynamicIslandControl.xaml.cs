using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Palisades.Services;
using WinForms = System.Windows.Forms;
using Palisades.ViewModels;

namespace Palisades.Views.Controls
{
    public partial class DynamicIslandControl : UserControl
    {
        public DynamicIslandViewModel IslandVm => DynamicIslandViewModel.Instance;

        /// <summary>False inside the taskbar bar window (the bar drags as a whole).</summary>
        public bool EnableFreeDrag { get; set; } = true;
        /// <summary>True inside the taskbar bar window (card grows upward).</summary>
        public bool ForceExpandUp { get; set; }
        /// <summary>True for the taskbar bar host (owns the shared views when pinned).</summary>
        public bool IsBarHost { get; set; }

        // Free placement : glisser le fond de la pilule, ou ALT + glisser depuis n'importe où
        private bool _dragArmed;
        private bool _dragging;
        private Point _dragStartScreen;
        private double _dragStartLeft;
        private double _dragStartTop;

        // Survol : ouvre après délai, referme au départ (seulement si ouvert par survol)
        private readonly DispatcherTimer _hoverOpenTimer;
        private readonly DispatcherTimer _hoverCloseTimer;
        private bool _hoverOpened;
        private bool _animating;
        private DispatcherTimer _hoverPoll = null!;

        // Easings partagés + gelés : zéro alloc par frame d'anim
        private static readonly CubicEase EaseOut;
        private static readonly CubicEase EaseIn;

        static DynamicIslandControl()
        {
            EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            EaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };
            try { EaseOut.Freeze(); EaseIn.Freeze(); } catch { }
        }

        public DynamicIslandControl()
        {
            InitializeComponent();
            DataContext = IslandVm;
            IslandVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DynamicIslandViewModel.State))
                    AnimateExpand();
                else if (e.PropertyName == nameof(DynamicIslandViewModel.CurrentWidget))
                {
                    AnimateWidgetSwitch();
                    LogSnapshot("select");
                }
            };
            DynamicIslandService.Instance.Changed += () => Dispatcher.Invoke(() =>
            {
                ApplyExpandDirection();
                ClearFixedWidth();
                ApplyTimingSettings();
                SyncWidgetHost();
            });
            ExpandedPanel.SizeChanged += OnExpandedSizeChanged;
            // Pastille centrée FIXE : en alignement Left/Right, un changement de
            // largeur du contenu décalerait la pastille (en Center c'est stable).
            // On compense la marge pour garder le CENTRE (pas de boucle sondage).
            SizeChanged += (_, e) =>
            {
                try
                {
                    if (IsBarHost) return; // la barre gère sa fenêtre elle-même
                    if (!e.WidthChanged || e.PreviousSize.Width <= 0 || ActualWidth <= 0) return;
                    double dW = e.PreviousSize.Width - ActualWidth;
                    if (Math.Abs(dW) < 2.0) return;
                    if (HorizontalAlignment == HorizontalAlignment.Left)
                        Margin = new Thickness(Margin.Left + dW / 2, Margin.Top, Margin.Right, Margin.Bottom);
                    else if (HorizontalAlignment == HorizontalAlignment.Right)
                        Margin = new Thickness(Margin.Left, Margin.Top, Margin.Right + dW / 2, Margin.Bottom);
                }
                catch { }
            };
            Loaded += (_, _) =>
            {
                ApplyExpandDirection();
                ClearFixedWidth();
                ApplyTimingSettings();
                SyncWidgetHost();
                // Replié = Collapsed = taille 0 (les vues restent chargées quand même)
                ExpandedPanel.Visibility = IslandVm.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
                // Warmup : un vrai passage layout (Visible + measure) en fond pour que
                // la 1re ouverture n'ait rien à mesurer sur le thread UI (fini le freeze)
                Dispatcher.BeginInvoke(new Action(WarmupLayout),
                    DispatcherPriority.Background);
            };

            _hoverOpenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _hoverOpenTimer.Tick += (_, _) =>
            {
                _hoverOpenTimer.Stop();
                var svc = DynamicIslandService.Instance;
                bool flap = HoverFlapGuard();
                HLog($"OPEN-TICK hover={svc.ExpandOnHover} over={_hoverInside} state={IslandVm.State} drag={_dragging} armed={_dragArmed} anim={_animating} alt={IsAltHeld()} flapOk={flap}");
                if (!flap) return;
                // Seulement depuis l'état replié, jamais en drag/anim/ALT : pas de boucle.
                // _hoverInside (sonde géo) et non IsMouseOver : sur l'overlay
                // click-through, IsMouseOver se périme dès que la géo bouge.
                if (svc.ExpandOnHover
                    && _hoverInside && IslandVm.State == IslandState.Compact
                    && !_dragging && !_dragArmed && !_animating && !IsAltHeld())
                {
                    _hoverOpened = true;
                    IslandVm.State = IslandState.Expanded;
                }
            };
            _hoverCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _hoverCloseTimer.Tick += (_, _) =>
            {
                _hoverCloseTimer.Stop();
                bool flap = HoverFlapGuard();
                HLog($"CLOSE-TICK hoverOpened={_hoverOpened} over={_hoverInside} state={IslandVm.State} anim={_animating} flapOk={flap}");
                if (!flap) return;
                if (_hoverOpened && !_hoverInside && IslandVm.State == IslandState.Expanded && !_animating)
                {
                    // _hoverOpened RESTE true (= pilotage survol) : si la souris
                    // revient pendant la fermeture, FinishClose rouvre.
                    IslandVm.State = IslandState.Compact;
                }
            };

            // Sondage : l'état de survol est réévalué périodiquement sur la
            // position live (avec hystérésis), pas sur les events de bordure
            // qui sont faussés dès que les bornes bougent (resize, shift, anim).
            _hoverPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _hoverPoll.Tick += (_, _) => HoverPollTick();
            _hoverPoll.Start();
        }

        private bool _hoverInside;

        /// <summary>Présence souris évaluée sur géométrie live + hystérésis 8px :
        /// les bornes qui bougent sous un curseur fixe ne génèrent aucun flip.</summary>
        private void HoverPollTick()
        {
            try
            {
                if (Visibility != Visibility.Visible) return;
                if (_pillMenuOpen) return;
                if (_dragging || _dragArmed) return;
                double w = ActualWidth, h = ActualHeight;
                if (w <= 0 || h <= 0) return;
                Point origin;
                try
                {
                    origin = PointToScreen(new Point(0, 0));
                    // PointToScreen ne voit pas le RenderTransform d'un descendant :
                    // en expand-up, RootShift (GPU) sort le visuel de la boîte layout.
                    // Sans ce report, la zone panneau compte comme "dehors" -> referme.
                    origin.X += RootShift.X;
                    origin.Y += RootShift.Y;
                }
                catch { return; }
                var dpi = VisualTreeHelper.GetDpi(this);
                double dx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
                double dy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
                System.Drawing.Point mp;
                try { mp = WinForms.Control.MousePosition; }
                catch { return; }
                double mx = mp.X / dx, my = mp.Y / dy;
                bool inside = mx >= origin.X && mx <= origin.X + w
                    && my >= origin.Y && my <= origin.Y + h;
                bool outside = mx < origin.X - 8 || mx > origin.X + w + 8
                    || my < origin.Y - 8 || my > origin.Y + h + 8;
                // Occultation : une vraie fenêtre par-dessus avec curseur fixe ne
                // génère ni event ni changement géo -> sans ce test l'îlot reste
                // ouvert sous l'app ("se ferme plus") ou ne s'ouvre pas au
                // désoccultage ("s'ouvre pas"). Même processus = dedans (menus…).
                bool blocked = false;
                if (inside)
                {
                    try { blocked = IsOccludedByForeignWindow(mp.X, mp.Y); } catch { blocked = false; }
                }
                string geo = $"rect=({origin.X:0},{origin.Y:0},{w:0}x{h:0}) mouse=({mx:0},{my:0}) occ={blocked} shift={RootShift.Y:0} panelH={ExpandedPanel.ActualHeight:0}";
                if (inside && !blocked)
                {
                    if (!_hoverInside)
                    {
                        _hoverInside = true;
                        HLog($"HOVER-IN state={IslandVm.State} anim={_animating} {geo}");
                        _hoverCloseTimer.Stop();
                        TryStartOpen();
                    }
                }
                else if (outside || blocked)
                {
                    if (_hoverInside)
                    {
                        _hoverInside = false;
                        HLog($"HOVER-OUT hoverOpened={_hoverOpened} state={IslandVm.State} anim={_animating} {geo}");
                        _hoverOpenTimer.Stop();
                        TryStartClose();
                    }
                }
                // Bande morte 0-8px non occultée : garde l'état (pas de flip sur micro-jitter)
            }
            catch { }
        }

        private void TryStartOpen()
        {
            try
            {
                var svc = DynamicIslandService.Instance;
                if (svc.ExpandOnHover
                    && IslandVm.State == IslandState.Compact
                    && !_dragging && !_dragArmed && !_animating && !IsAltHeld())
                {
                    HLog($"TIMER-OPEN start ({svc.HoverOpenDelayMs}ms)");
                    _hoverOpenTimer.Start();
                }
            }
            catch { }
        }

        private void TryStartClose()
        {
            try
            {
                if (_hoverOpened && IslandVm.State == IslandState.Expanded && !_animating)
                {
                    HLog($"TIMER-CLOSE start ({DynamicIslandService.Instance.HoverCloseDelayMs}ms)");
                    _hoverCloseTimer.Start();
                }
            }
            catch { }
        }

        private static bool IsAltHeld()
            => (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT pt);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOSIZE_ = 0x0001, SWP_NOMOVE_ = 0x0002, SWP_NOACTIVATE_ = 0x0010;

        /// <summary>Le ContextMenu vit dans un popup séparé : quand l'îlot est épinglé
        /// (fenêtre Topmost), le menu passait DERRIÈRE. On force le popup topmost à
        /// l'ouverture.</summary>
        public static void EnsureMenuTopmost(ContextMenu menu)
        {
            try
            {
                menu.Opened += (_, _) =>
                {
                    try
                    {
                        var src = System.Windows.PresentationSource.FromVisual(menu)
                            as System.Windows.Interop.HwndSource;
                        if (src != null)
                            SetWindowPos(src.Handle, HWND_TOPMOST, 0, 0, 0, 0,
                                SWP_NOMOVE_ | SWP_NOSIZE_ | SWP_NOACTIVATE_);
                    }
                    catch { }
                };
            }
            catch { }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        /// <summary>True si une fenêtre d'un AUTRE processus recouvre le point
        /// (pixels écran). Nos propres fenêtres/popups = dedans.</summary>
        private static bool IsOccludedByForeignWindow(int x, int y)
        {
            try
            {
                var h = WindowFromPoint(new POINT { X = x, Y = y });
                if (h == IntPtr.Zero) return true;
                uint pid = 0;
                GetWindowThreadProcessId(h, out pid);
                if (pid == 0) return true;
                try
                {
                    using var me = System.Diagnostics.Process.GetCurrentProcess();
                    return pid != (uint)me.Id;
                }
                catch { return true; }
            }
            catch { return true; }
        }

        // Coupe-circuit anti-boucle : rafale de bascules RAPPROCHÉES (< 500 ms
        // d'intervalle) = events fantômes qui s'auto-entretiennent -> pause 10 s.
        // L'usage humain normal (rythme lent) ne déclenche jamais.
        private int _hoverStreak;
        private DateTime _hoverLastFlip = DateTime.MinValue;
        private DateTime _hoverCalmUntil = DateTime.MinValue;

        /// <summary>Trace DIAG du survol : %LocalAppData%/Palisades/island_hover.log.
        /// Chaque décision loggée avec ses gardes (pourquoi ça ouvre / pas).</summary>
        private static readonly string HoverLogPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Palisades", "island_hover.log");

        private static void HLog(string msg)
        {
            try
            {
                var f = new System.IO.FileInfo(HoverLogPath);
                if (f.Exists && f.Length > 300 * 1024)
                    f.Delete();
                System.IO.File.AppendAllText(HoverLogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            }
            catch { }
        }

        private bool HoverFlapGuard()
        {
            try
            {
                var now = DateTime.UtcNow;
                if (now < _hoverCalmUntil) return false;
                bool rapid = (now - _hoverLastFlip).TotalMilliseconds < 500;
                _hoverLastFlip = now;
                _hoverStreak = rapid ? _hoverStreak + 1 : 1;
                if (_hoverStreak >= 8)
                {
                    _hoverStreak = 0;
                    _hoverCalmUntil = now.AddSeconds(10);
                    HLog("FLAP-GUARD automation paused 10s");
                    try { App.Log("[Island] hover flap guard: automation paused 10s"); } catch { }
                    return false;
                }
                return true;
            }
            catch { return true; }
        }

        private bool _pillMenuOpen;

        // NOTE : plus de logique hover dans Enter/Leave (events de bordure
        // faussés par les bornes qui bougent). Tout passe par HoverPollTick.

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            base.OnPreviewKeyUp(e);
            // ALT relâché en survol : relance l'ouverture auto
            if (e.Key is Key.LeftAlt or Key.RightAlt or Key.System
                && DynamicIslandService.Instance.ExpandOnHover && _hoverInside
                && IslandVm.State != IslandState.Expanded && !_dragging && !_hoverOpened)
            {
                _hoverOpenTimer.Stop();
                _hoverOpenTimer.Start();
            }
        }

        /// <summary>Grip du panneau : glisser vertical = hauteur du widget (savée).
        /// Expand-up : le grip est le bord HAUT -> tirer vers le haut agrandit
        /// (VerticalChange négatif), donc on inverse le signe.</summary>
        private void ResizeGrip_Drag(object sender, DragDeltaEventArgs e)
        {
            var type = IslandVm.CurrentWidget?.GadgetType;
            if (string.IsNullOrEmpty(type)) return;
            double delta = EffectiveExpandUp ? -e.VerticalChange : e.VerticalChange;
            DynamicIslandService.Instance.SetWidgetHeight(type, IslandVm.CurrentWidgetHeight + delta);
        }

        /// <summary>Clic droit pilule : accès direct pin + sens + survol.
        /// Clic droit sur un widget déplié : réglages du widget (comme le desktop).</summary>
        protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseRightButtonUp(e);
            try
            {
                var svc = DynamicIslandService.Instance;
                var tr = TranslationService.Instance;
                var menu = new ContextMenu();

                var widget = IslandVm.CurrentWidget;
                if (widget != null
                    && IsInsideExpandedPanel(e.OriginalSource as DependencyObject)
                    && !string.IsNullOrEmpty(widget.GadgetType))
                {
                    // --- Menu réglages widget (partagé desktop / îlot) ---
                    string type = widget.GadgetType;
                    WidgetSettingsMenu.Build(menu, type, GadgetTypeDefaults.Instance.Get(type),
                        widget.ExpandedView,
                        json => GadgetTypeDefaults.Instance.Remember(type, json));
                    menu.Items.Add(new Separator());
                    var remove = new MenuItem { Header = tr["Db_Island_RemoveWidget"] ?? "Remove from island" };
                    remove.Click += (_, _) =>
                    {
                        try { DynamicIslandService.Instance.SetPinned(type, false); } catch { }
                    };
                    menu.Items.Add(remove);
                    var editW = new MenuItem { Header = tr["Widget_Ctx_EditProperties"] ?? "Edit Properties..." };
                    editW.Click += (_, _) => OpenIslandWidgetDashboard(type);
                    menu.Items.Add(editW);
                    ClosePillMenuOnOutside(menu);
                    EnsureMenuTopmost(menu);
                    HLog($"MENU-OPEN widget {type}");
                    menu.IsOpen = true;
                    e.Handled = true;
                    return;
                }

                var edit = new MenuItem { Header = tr["Widget_Ctx_EditProperties"] ?? "Edit Properties..." };
                edit.Click += (_, _) => OpenIslandDashboard();
                menu.Items.Add(edit);
                menu.Items.Add(new Separator());
                AddPillCheck(menu, tr["Db_Island_PinTaskbar"] ?? "Pin to taskbar", svc.PinToTaskbar,
                    v => svc.PinToTaskbar = v);
                AddPillCheck(menu, tr["Db_Island_ExpandUp"] ?? "Expand upwards", svc.ExpandUp,
                    v => svc.ExpandUp = v);
                AddPillCheck(menu, tr["Db_Island_ExpandOnHover"] ?? "Expand on hover", svc.ExpandOnHover,
                    v => svc.ExpandOnHover = v);
                ClosePillMenuOnOutside(menu);
                EnsureMenuTopmost(menu);
                HLog("MENU-OPEN pastille");
                menu.IsOpen = true;
                e.Handled = true;
            }
            catch { }
        }

        private int _openMenuDepth;
        /// <summary>True tant qu'un menu de l'îlot est ouvert. La barre épinglée s'en
        /// sert pour suspendre sa réaffirmation Topmost (sinon elle repasse au-dessus).</summary>
        public bool IsMenuOpen => _openMenuDepth > 0;

        /// <summary>Menu ouvert = hover ignoré, réévalué à la fermeture.</summary>
        private void ClosePillMenuOnOutside(ContextMenu menu)
        {
            _pillMenuOpen = true;
            _openMenuDepth++;
            menu.Closed += (_, _) =>
            {
                _openMenuDepth = Math.Max(0, _openMenuDepth - 1);
                _pillMenuOpen = false;
                try
                {
                    if (!_hoverOpened || !DynamicIslandService.Instance.ExpandOnHover) return;
                    if (IslandVm.State == IslandState.Expanded && !_hoverInside)
                        _hoverCloseTimer.Start();
                }
                catch { }
            };
        }

        /// <summary>Ouvre le dashboard Palisades sur la page Dynamic Island.</summary>
        private static void OpenIslandDashboard()
        {
            try
            {
                if (Application.Current is Palisades.App app)
                {
                    var win = app.GetDashboardWindow();
                    if (win != null)
                    {
                        win.ShowIslandProperties();
                        if (win.WindowState == WindowState.Minimized)
                            win.WindowState = WindowState.Normal;
                        win.Activate();
                    }
                }
            }
            catch { }
        }

        /// <summary>Ouvre le dashboard et sélectionne le widget îlot pour personnalisation.</summary>
        private static void OpenIslandWidgetDashboard(string gadgetType)
        {
            try
            {
                if (Application.Current is Palisades.App app)
                {
                    var win = app.GetDashboardWindow();
                    if (win != null)
                    {
                        win.ShowIslandWidgetProperties(gadgetType);
                        if (win.WindowState == WindowState.Minimized)
                            win.WindowState = WindowState.Normal;
                        win.Activate();
                    }
                }
            }
            catch { }
        }

        private static void AddPillCheck(ContextMenu menu, string header, bool isChecked, Action<bool> set)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
            item.Click += (_, _) => { try { HLog($"MENU-CLICK [{header}] {isChecked}->{!isChecked}"); set(!isChecked); } catch { } };
            menu.Items.Add(item);
        }

        private bool EffectiveExpandUp => ForceExpandUp || DynamicIslandService.Instance.ExpandUp;

        /// <summary>L'îlot s'ouvre vers le haut ou vers le bas (réordonne le panneau
        /// + contenu miroir : sélecteur collé à la pastille, grip au bord externe).
        /// Seuls scroller/grip bougent (légers) : WidgetHost et ses vues lourdes
        /// (radio en cours !) ne sont jamais détachés.</summary>
        public void ApplyExpandDirection()
        {
            try
            {
                bool up = EffectiveExpandUp;
                int want = up ? 0 : 1;
                int have = MainStack.Children.IndexOf(ExpandedPanel);
                if (have != want && have >= 0)
                {
                    MainStack.Children.Remove(ExpandedPanel);
                    MainStack.Children.Insert(Math.Min(want, MainStack.Children.Count), ExpandedPanel);
                    HLog($"DIRECTION panel {(up ? "haut" : "bas")}");
                }
                // Ordre voulu : bas=[scroller,header,media,host,grip],
                // haut=[grip,header,media,host,scroller]
                bool scrollerFirst = ExpandedStack.Children.IndexOf(WidgetScroller) == 0;
                if (up == scrollerFirst)
                {
                    HLog($"DIRECTION contenu miroir {(up ? "haut" : "bas")}");
                    ExpandedStack.Children.Remove(WidgetScroller);
                    ExpandedStack.Children.Remove(ResizeGrip);
                    if (up)
                    {
                        ExpandedStack.Children.Insert(0, ResizeGrip);
                        ExpandedStack.Children.Add(WidgetScroller);
                    }
                    else
                    {
                        ExpandedStack.Children.Insert(0, WidgetScroller);
                        ExpandedStack.Children.Add(ResizeGrip);
                    }
                }
            }
            catch { }
        }

        private int _expandGen;

        private void ApplyTimingSettings()
        {
            try
            {
                var svc = DynamicIslandService.Instance;
                _hoverOpenTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, svc.HoverOpenDelayMs));
                _hoverCloseTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(100, svc.HoverCloseDelayMs));
            }
            catch { }
        }

        /// <summary>Un visuel = un seul parent : quand l'îlot est épinglé, l'hôte
        /// overlay détache ses vues et la barre les adopte (aucun conflit).</summary>
        private void SyncWidgetHost()
        {
            try
            {
                var svc = DynamicIslandService.Instance;
                bool detached = !IsBarHost && svc.Enabled && svc.PinToTaskbar;
                WidgetHost.ItemsSource = detached
                    ? null
                    : IslandVm.Widgets;
                HLog($"HOST bar={IsBarHost} detached={detached} views={IslandVm.Widgets.Count}");
            }
            catch { }
        }

        /// <summary>Warmup : crée les vues + un vrai passage layout une fois, pour que
        /// la 1re ouverture n'ait rien de lourd à faire sur le thread UI.</summary>
        private int _warmupTries;

        private void WarmupLayout()
        {
            try
            {
                foreach (var w in IslandVm.Widgets)
                {
                    try
                    {
                        var v = w.ExpandedView;
                        v.Measure(new Size(IslandVm.CompactMaxWidth, IslandVm.CurrentWidgetHeight));
                    }
                    catch { }
                }
                if (!IslandVm.IsExpanded && ExpandedPanel.Visibility != Visibility.Visible)
                {
                    ExpandedPanel.Visibility = Visibility.Visible;
                    ExpandedPanel.UpdateLayout();
                    ExpandedPanel.Visibility = Visibility.Collapsed;
                }
                HLog($"WARMUP views={IslandVm.Widgets.Count} panelH={ExpandedPanel.ActualHeight:0}");
                // Contenu pas prêt (0px) : réessaie en fond, 3 fois max
                if (!IslandVm.IsExpanded && ExpandedPanel.ActualHeight <= 1 && _warmupTries < 3)
                {
                    _warmupTries++;
                    Dispatcher.BeginInvoke(new Action(WarmupLayout), DispatcherPriority.Background);
                }
            }
            catch { }
        }

        /// <summary>Pastille fixe en expand-up : le shift suit la hauteur réelle
        /// du panneau (switch widget, resize grip) une fois l'anim terminée.</summary>
        private void OnExpandedSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Suivi continu SANS exception anim : le contenu arrive en async
            // (pochettes, textes) et change la hauteur après l'ouverture.
            // Si le shift ne suit pas, la pastille saute en vers-le-haut.
            // SizeChanged part pendant le layout, avant le rendu = aucun saut visible.
            try
            {
                if (IsBarHost) return;
                if (!EffectiveExpandUp) return;
                // Suit aussi pendant l'anim de fermeture (State déjà Compact) :
                // le contenu async tasse encore la hauteur, shift périmé = saut.
                if (!IslandVm.IsExpanded && !_animating) return;
                SnapUpShift();
            }
            catch { }
        }

        /// <summary>Recale le shift sur la hauteur réelle du panneau (pastille fixe).
        /// Instantané : appelé au même tick que le changement layout.</summary>
        private void SnapUpShift()
        {
            try
            {
                if (IsBarHost || !EffectiveExpandUp) return;
                if (!IslandVm.IsExpanded && !_animating) return;
                // Hauteur + marges du panneau : tout ce qui pousse la pastille
                double want = -(Math.Max(0, ExpandedPanel.ActualHeight)
                    + ExpandedPanel.Margin.Top + ExpandedPanel.Margin.Bottom);
                if (Math.Abs(RootShift.Y - want) > 0.5)
                {
                    RootShift.BeginAnimation(TranslateTransform.YProperty, null);
                    RootShift.Y = want;
                    HLog($"SHIFT y={want:0} (panelH={ExpandedPanel.ActualHeight:0})");
                    RefreshIslandHitRect();
                }
            }
            catch { }
        }

        private void RefreshIslandHitRect()
        {
            try
            {
                if (Window.GetWindow(this) is Views.DesktopOverlayWindow o)
                    o.CacheIslandRect();
            }
            catch { }
        }

        /// <summary>Bornes visuelles réelles du panneau déplié (inclut le shift
        /// GPU expand-up via RenderTransform) dans le repère de la fenêtre hôte.
        /// Le hook souris overlay s'en sert : sans ça, les clics dans la zone
        /// dépliée hors rect layout sont avalés comme clics bureau.</summary>
        public Rect GetVisualHitRect()
        {
            try
            {
                var win = Window.GetWindow(this);
                if (win == null || ExpandedPanel.Visibility != Visibility.Visible) return Rect.Empty;
                var tl = ExpandedPanel.TranslatePoint(new Point(0, 0), win);
                return new Rect(tl.X, tl.Y,
                    Math.Max(0, ExpandedPanel.ActualWidth),
                    Math.Max(0, ExpandedPanel.ActualHeight));
            }
            catch { return Rect.Empty; }
        }

        /// <summary>Ouverture façon iOS : dépliage vers hauteur réelle + slide + fade.
        /// Fermeture : repli + fade puis collapse (toutes durées réglables).</summary>
        private void AnimateExpand()
        {
            // Hôte masqué (îlot épinglé : c'est la barre qui anime) : inerte,
            // sinon double automation sur le VM partagé + double logs.
            if (Visibility != Visibility.Visible)
            {
                HLog($"ANIM skip (hôte masqué) state={IslandVm.State}");
                return;
            }
            ClearFixedWidth();
            int gen = ++_expandGen;
            HLog($"ANIM state={IslandVm.State} gen={gen}");
            if (IslandVm.IsExpanded) PlayOpenAnim(gen);
            else PlayCloseAnim(gen);
        }

        private int _lastWidgetIndex = -1;

        /// <summary>Transition entre widgets : fade + slide latéral (sens du scroll)
        /// sur le host de la vue widget. Pas de layout animé : opacité + transform.</summary>
        private void AnimateWidgetSwitch()
        {
            try
            {
                int idx = IslandVm.SelectedIndex;
                int dir = idx >= _lastWidgetIndex ? 1 : -1;
                _lastWidgetIndex = idx;
                if (Visibility != Visibility.Visible) return; // hôte masqué (barre)
                if (IslandVm.State != IslandState.Expanded) return;

                if (WidgetHost.RenderTransform is not TranslateTransform tt)
                {
                    tt = new TranslateTransform();
                    WidgetHost.RenderTransform = tt;
                }
                int ms = Math.Clamp(DynamicIslandService.Instance.ExpandDurationMs / 2, 120, 260);
                WidgetHost.BeginAnimation(OpacityProperty, null);
                WidgetHost.Opacity = 0;
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
                WidgetHost.BeginAnimation(OpacityProperty, fade);
                tt.BeginAnimation(TranslateTransform.XProperty, null);
                tt.X = dir * 24;
                var slide = new DoubleAnimation(dir * 24, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
                tt.BeginAnimation(TranslateTransform.XProperty, slide);

                // Hauteur fluide dans les DEUX sens : on anime la hauteur réelle du
                // host (MaxHeight n'est qu'un plafond -> en grand->petit le contenu
                // rétrécissait instantanément). Cible = hauteur naturelle du nouveau
                // widget, plafonnée par son réglage.
                double fromH = WidgetHost.ActualHeight;
                double toH = IslandVm.CurrentWidgetHeight;
                try
                {
                    var v = IslandVm.CurrentWidget?.ExpandedView;
                    if (v != null)
                    {
                        double w = WidgetHost.ActualWidth > 1 ? WidgetHost.ActualWidth : 300;
                        v.Measure(new Size(w, double.PositiveInfinity));
                        if (v.DesiredSize.Height > 1)
                            toH = Math.Min(v.DesiredSize.Height, IslandVm.CurrentWidgetHeight);
                    }
                }
                catch { }
                // La barre épinglée est une fenêtre SizeToContent : animer la hauteur
                // = resize de fenêtre par frame => la barre bouge. Là on snappe.
                if (!IsBarHost && fromH > 1 && Math.Abs(fromH - toH) > 1)
                {
                    WidgetHost.ClipToBounds = true;
                    var hAnim = new DoubleAnimation(fromH, toH, TimeSpan.FromMilliseconds(ms))
                    {
                        EasingFunction = EaseOut
                    };
                    hAnim.Completed += (_, _) =>
                    {
                        try { WidgetHost.BeginAnimation(FrameworkElement.HeightProperty, null); } catch { }
                    };
                    WidgetHost.BeginAnimation(FrameworkElement.HeightProperty, hAnim);
                }
            }
            catch { }
        }

        /// <summary>Resync après (dé)pin : l'hôte masqué n'anime pas, son panneau
        /// peut être désynchro du VM partagé. No-op si déjà synchro (ne tue
        /// jamais une anim en cours).</summary>
        public void SyncPanelToVm()
        {
            try
            {
                bool wantExpanded = IslandVm.IsExpanded;
                bool haveExpanded = ExpandedPanel.Visibility == Visibility.Visible;
                if (wantExpanded == haveExpanded) return;
                Dispatcher.Invoke(() =>
                {
                    ResetPanelTransforms();
                    ExpandedPanel.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
                    ExpandedPanel.ClearValue(FrameworkElement.MaxHeightProperty);
                    ExpandedPanel.Visibility = wantExpanded ? Visibility.Visible : Visibility.Collapsed;
                    if (wantExpanded && Visibility == Visibility.Visible)
                    {
                        ExpandedPanel.UpdateLayout();
                        SnapUpShift();
                    }
                    RefreshIslandHitRect();
                });
                HLog($"RESYNC panel -> {(wantExpanded ? "Visible" : "Collapsed")}");
            }
            catch { }
        }

        private void PlayOpenAnim(int gen)
        {
            try
            {
                var svc = DynamicIslandService.Instance;
                bool up = EffectiveExpandUp;
                _animating = true;
                try { IslandVm.IsClosing = false; } catch { }
                _hoverOpenTimer.Stop();
                ExpandedPanel.RenderTransformOrigin = new Point(0.5, up ? 1 : 0);
                // Visible AVANT l'anim : le contenu (déjà pré-chargé) est là dès la 1re frame,
                // sans anim propre — seul le conteneur bouge
                ExpandedPanel.Visibility = Visibility.Visible;
                ResetPanelTransforms();
                // Largeur gelée sur la pastille repliée : le contenu large écartait
                // l'îlot à chaque ouverture (344<->503) et le placement oscillait.
                // (Un binding MaxWidth<-CompactRow.ActualWidth ne marche pas : dans
                // un StackPanel la rangée s'étire déjà à la largeur finale.)
                // Moins les chrome (marges panneau + padding racine) sinon la
                // racine dépasse quand même de 16px.
                double chrome = ExpandedPanel.Margin.Left + ExpandedPanel.Margin.Right
                    + IslandRoot.Padding.Left + IslandRoot.Padding.Right
                    + IslandRoot.BorderThickness.Left + IslandRoot.BorderThickness.Right;
                ExpandedPanel.MaxWidth = Math.Max(180, IslandRoot.ActualWidth - chrome);
                HLog($"OPEN-START up={up} bar={IsBarHost} expandMs={svc.ExpandDurationMs} fadeMs={svc.ExpandFadeMs} capW={ExpandedPanel.MaxWidth:0}");
                // Ombre constante pendant les anims : le on/off créait un pop
                // visible (mesuré à op=0.10 en pleine fermeture = 2e temps).

                // 100 % GPU (transform + opacité), zéro layout par frame, partout :
                // le layout animé par frame faisait scintiller la barre (resize
                // fenêtre + repasse layout à chaque frame). La fenêtre transparente
                // ne montre rien au resize, seul le contenu fade/slide.
                // Expand-up : shift INSTANTANÉ (jamais animé) sinon la pastille
                // descend d'abord puis remonte = elle "bouge".
                if (up && !IsBarHost)
                {
                    ExpandedPanel.UpdateLayout();
                    if (ExpandedPanel.ActualHeight <= 1)
                    {
                        // Contenu pas encore mesuré : repli de sécu, le suivi
                        // SizeChanged recalera dès la mesure réelle
                        RootShift.BeginAnimation(TranslateTransform.YProperty, null);
                        RootShift.Y = -320;
                    }
                    SnapUpShift();
                    RefreshIslandHitRect();
                }
                var grow = new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(svc.ExpandDurationMs))
                {
                    EasingFunction = EaseOut
                };
                    grow.Completed += (_, _) =>
                    {
                        HLog($"OPEN-DONE gen={gen} cur={_expandGen}");
                        if (gen == _expandGen)
                        {
                            _animating = false;
                            // La hauteur a pu changer pendant l'anim (contenu async) :
                            // recale une fois, la pastille ne bouge pas
                            SnapUpShift();
                            // Leave raté pendant l'ouverture (guard _animating) :
                            // la souris est déjà partie -> referme
                            if (_hoverOpened && !_hoverInside && IslandVm.State == IslandState.Expanded)
                                _hoverCloseTimer.Start();
                        }
                        RefreshIslandHitRect();
                        LogSnapshot("open");
                    };
                ExpandScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

                var slide = new DoubleAnimation(up ? 14 : -14, 0, TimeSpan.FromMilliseconds(svc.ExpandDurationMs))
                {
                    EasingFunction = EaseOut
                };
                ExpandSlide.BeginAnimation(TranslateTransform.YProperty, slide);

                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(svc.ExpandFadeMs));
                ExpandedPanel.BeginAnimation(OpacityProperty, fade);
            }
            catch (Exception ex)
            {
                try
                {
                    _animating = false;
                    ExpandedPanel.Visibility = Visibility.Visible;
                    System.Diagnostics.Debug.WriteLine("[Island] PlayOpenAnim: " + ex.Message);
                }
                catch { }
            }
        }

        private void ResetPanelTransforms()
        {
            try
            {
                ExpandScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                ExpandScale.ScaleY = 1;
                ExpandedPanel.BeginAnimation(OpacityProperty, null);
                ExpandedPanel.Opacity = 1;
                ExpandSlide.BeginAnimation(TranslateTransform.YProperty, null);
                ExpandSlide.Y = 0;
                RootShift.BeginAnimation(TranslateTransform.YProperty, null);
                RootShift.Y = 0;
                IslandRoot.Clip = null;
            }
            catch { }
        }

        private void PlayCloseAnim(int gen)
        {
            try
            {
                var svc = DynamicIslandService.Instance;
                if (ExpandedPanel.Visibility != Visibility.Visible)
                    return;
                _animating = true;
                // Fige la hauteur du panneau pendant le repli (résumé NP + média
                // restent visibles) : le clip cible la pastille, pas une zone vide.
                try { IslandVm.IsClosing = true; } catch { }
                _hoverCloseTimer.Stop();

                // C'est la RACINE noire (IslandRoot) qui se replie, pas le panneau
                // intérieur : sinon le fond noir restait grand puis se snapait au
                // collapse. Le clip ne déclenche aucun layout = 100 % fluide, et
                // le contenu reste figé (ni slide ni scale vers la pastille).
                // Shift gardé tel quel pendant le repli (pastille fixe), reset pile
                // au collapse dans FinishClose (même tick = aucun saut).
                bool up = EffectiveExpandUp;
                double fullW = Math.Max(1, IslandRoot.ActualWidth);
                double fullH = Math.Max(1, IslandRoot.ActualHeight);
                HLog($"CLOSE-START up={up} bar={IsBarHost} root={fullW:0}x{fullH:0} panelH={ExpandedPanel.ActualHeight:0}");
                double panelSpace = Math.Max(0, ExpandedPanel.ActualHeight
                    + ExpandedPanel.Margin.Top + ExpandedPanel.Margin.Bottom);
                double pillH = Math.Max(1, fullH - panelSpace);
                int foldMs = Math.Max(svc.ExpandDurationMs, svc.ExpandFadeMs);
                var from = new Rect(0, 0, fullW, fullH);
                var to = up ? new Rect(0, fullH - pillH, fullW, pillH) : new Rect(0, 0, fullW, pillH);
                var clip = new RectangleGeometry(from);
                // Garde l'arrondi utilisateur pendant le repli (sinon coins carrés)
                try
                {
                    double r = DynamicIslandService.Instance.CornerRadius;
                    clip.RadiusX = r;
                    clip.RadiusY = r;
                }
                catch { }
                IslandRoot.Clip = clip;
                var fold = new RectAnimation(from, to, TimeSpan.FromMilliseconds(foldMs))
                {
                    EasingFunction = EaseOut
                };
                fold.Completed += (_, _) => FinishClose(gen);
                clip.BeginAnimation(RectangleGeometry.RectProperty, fold);

                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(svc.ExpandFadeMs));
                ExpandedPanel.BeginAnimation(OpacityProperty, fade);
            }
            catch (Exception ex)
            {
                try
                {
                    _animating = false;
                    ExpandedPanel.Visibility = Visibility.Collapsed;
                    System.Diagnostics.Debug.WriteLine("[Island] PlayCloseAnim: " + ex.Message);
                }
                catch { }
            }
        }

        /// <summary>Effondrement synchrone (drop barre) : tue les anims, retire le
        /// panneau du layout DANS LE MÊME tick. Un fondu seul ne suffit pas :
        /// le contenu reste mesuré et le dégel SizeToContent remesure grand
        /// (= ballon). État final identique à FinishClose.</summary>
        public void CollapseInstant()
        {
            try
            {
                HLog("COLLAPSE-INSTANT (drop)");
                _animating = false;
                try { IslandVm.IsClosing = false; } catch { }
                ResetPanelTransforms();
                ExpandedPanel.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
                ExpandedPanel.ClearValue(FrameworkElement.MaxHeightProperty);
                ExpandedPanel.ClearValue(FrameworkElement.MaxWidthProperty);
                ExpandedPanel.Visibility = Visibility.Collapsed;
                RefreshIslandHitRect();
            }
            catch { }
        }

        private void FinishClose(int gen)
        {
            RefreshIslandHitRect();
            HLog($"FINISH-CLOSE gen={gen} cur={_expandGen} over={_hoverInside} hoverOpened={_hoverOpened}");
            if (gen != _expandGen) return;
            // Enter raté pendant la fermeture : la souris est (re)venue alors
            // que c'était piloté par survol -> rouvre au lieu de collapse.
            // Clic manuel : _hoverOpened est false -> reste fermé.
            var svc = DynamicIslandService.Instance;
            if (_hoverOpened && _hoverInside && IslandVm.State == IslandState.Compact
                && svc.ExpandOnHover && !IsAltHeld() && !_dragging && !_dragArmed)
            {
                _animating = false;
                IslandVm.State = IslandState.Expanded;
                return;
            }
                    try
                    {
                        try { IslandVm.IsClosing = false; } catch { }
                        ResetPanelTransforms();
                        ExpandedPanel.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
                        ExpandedPanel.ClearValue(FrameworkElement.MaxHeightProperty);
                        ExpandedPanel.ClearValue(FrameworkElement.MaxWidthProperty);
                        // Collapsed = taille 0 (vues toujours chargées, pas de lag suivant)
                        ExpandedPanel.Visibility = Visibility.Collapsed;
                        LogSnapshot("close");
                    }
                    catch { }
                    finally { _animating = false; }
        }

        private void ClearFixedWidth()
        {
            IslandRoot.BeginAnimation(WidthProperty, null);
            double w = IslandVm.CompactMinWidth;
            if (w > 0)
            {
                // Longueur = largeur FIXE : le contenu (noms longs) ne fait plus
                // grandir l'îlot.
                IslandRoot.Width = w;
                IslandRoot.MaxWidth = w;
            }
            else
            {
                IslandRoot.Width = double.NaN;
                IslandRoot.MaxWidth = IslandVm.CompactMaxWidth;
            }
        }

        /// <summary>Molette verticale -> scroll horizontal (sans Shift).</summary>
        private void WidgetScroller_Wheel(object sender, MouseWheelEventArgs e)
        {
            var sv = (ScrollViewer)sender;
            double next = sv.HorizontalOffset - Math.Sign(e.Delta) * 48;
            sv.ScrollToHorizontalOffset(Math.Max(0, next));
            e.Handled = true;

            // Pagine entre widgets au scroll franc
            if (Math.Abs(e.Delta) >= 100)
            {
                if (e.Delta < 0) IslandVm.NextWidgetCommand.Execute(null);
                else IslandVm.PrevWidgetCommand.Execute(null);
            }
        }

        private void WidgetCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.Tag is IslandWidget w)
            {
                HLog($"CARD-CLICK {w.GadgetType} clicks={e.ClickCount}");
                IslandVm.SelectWidgetCommand.Execute(w);
                HLog($"CARD-SELECTED idx={IslandVm.SelectedIndex}");
                if (e.ClickCount == 2)
                    IslandVm.ToggleExpandCommand.Execute(null);
            }
            else
            {
                HLog($"CARD-CLICK IGNORED src={ElDesc((sender as DependencyObject) ?? e.OriginalSource as DependencyObject)}");
            }
        }

        /// <summary>Bouton chevron (tout à droite) : toggle manuel. Coupe le pilotage
        /// survol sinon FinishClose rouvre car la souris est encore dessus.</summary>
        private void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _hoverOpened = false;
            _hoverOpenTimer.Stop();
            _hoverCloseTimer.Stop();
            HLog("TOGGLE-BTN manual (hover drive off)");
        }

        private void CompactRow_Click(object sender, MouseButtonEventArgs e)
        {
            // Clic zone vide : expand/collapse. Les boutons et cartes gèrent déjà leurs clics.
            if (e.OriginalSource is TextBlock or System.Windows.Shapes.Ellipse)
            {
                HLog($"PILL-CLICK src={ElDesc(e.OriginalSource as DependencyObject)}");
                // Toggle pilule = pilotage manuel : plus de refermeture survol
                _hoverOpened = false;
                IslandVm.ToggleExpandCommand.Execute(null);
                HLog($"PILL-TOGGLED state={IslandVm.State}");
            }
        }

        private void SeekSlider_Released(object sender, MouseButtonEventArgs e)
        {
            if (sender is Slider s)
            {
                HLog($"SEEK {s.Value:0}s");
                IslandVm.SeekCommand.Execute(s.Value);
            }
        }

        private void ResizeGrip_Done(object sender, DragCompletedEventArgs e)
        {
            try
            {
                HLog($"GRIP height={IslandVm.CurrentWidgetHeight:0} widget={IslandVm.CurrentWidget?.GadgetType}");
            }
            catch { }
        }

        // --- Free placement : glisser le fond, ou ALT + glisser depuis n'importe où ---

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonDown(e);
            HLog($"DOWN @{e.GetPosition(this)} src={ElDesc(e.OriginalSource as DependencyObject)} alt={IsAltHeld()} clicks={e.ClickCount}");
            if (!EnableFreeDrag) return;
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
            {
                BeginDrag(e);
                e.Handled = true;
                return;
            }
            // Clic sur fond de la pastille uniquement : arme un drag, simple clic = expand.
            // Cartes widgets, slider, grip, boutons : jamais de drag (sinon clic mangé).
            // Zone dépliée : jamais de drag (vues plugins custom = clics avalés sinon).
            if (e.OriginalSource is Border
                && !IsInteractiveElement(e.OriginalSource as DependencyObject)
                && !IsInsideExpandedPanel(e.OriginalSource as DependencyObject))
            {
                _dragArmed = true;
                _dragStartScreen = PointToScreen(e.GetPosition(this));
                CaptureActualPos();
            }
        }

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);
            if (!EnableFreeDrag) { _dragArmed = false; return; }
            if (_dragging)
            {
                MoveDragVisual(e);
                return;
            }
            if (_dragArmed)
            {
                var cur = PointToScreen(e.GetPosition(this));
                if ((cur - _dragStartScreen).Length > 4)
                {
                    _dragArmed = false;
                    _dragging = true;
                    CaptureMouse();
                }
            }
        }

        protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonUp(e);
            _dragArmed = false;
            if (!_dragging)
            {
                HLog($"UP src={ElDesc(e.OriginalSource as DependencyObject)} (click-through)");
                return;
            }
            _dragging = false;
            ReleaseMouseCapture();
            HLog($"DRAG-END persist=({Margin.Left:0},{Margin.Top:0})");
            // Persiste la position visuelle de la pastille (= layout top : le shift
            // expand-up s'annule avec l'offset panneau dans la pastille).
            var svc = DynamicIslandService.Instance;
            svc.SetCustomPosition(Math.Max(0, Margin.Left), Math.Max(0, Margin.Top));
            svc.Placement = "Custom";
            e.Handled = true;
        }

        private void BeginDrag(MouseButtonEventArgs e)
        {
            HLog($"DRAG-BEGIN alt={IsAltHeld()} @{e.GetPosition(this)} src={ElDesc(e.OriginalSource as DependencyObject)}");
            _dragArmed = false;
            _dragging = true;
            _dragStartScreen = PointToScreen(e.GetPosition(this));
            CaptureActualPos();
            CaptureMouse();
        }

        private void CaptureActualPos()
        {
            try
            {
                var win = Window.GetWindow(this);
                var p = win != null ? TranslatePoint(new Point(0, 0), win) : new Point(Margin.Left, Margin.Top);
                // TranslatePoint sur `this` NE voit PAS le RootShift (transform d'un
                // descendant) : p = layout top. Or la pastille visuelle = layout top
                // dans les 2 sens (up: offset panneau + shift s'annulent). Donc direct.
                _dragStartLeft = p.X;
                _dragStartTop = p.Y;
            }
            catch { _dragStartLeft = Margin.Left; _dragStartTop = Margin.Top; }
        }

        private void MoveDragVisual(MouseEventArgs e)
        {
            var cur = PointToScreen(e.GetPosition(this));
            double dx = (cur.X - _dragStartScreen.X) / DpiScaleX();
            double dy = (cur.Y - _dragStartScreen.Y) / DpiScaleX();
            // Visuel direct (pas de Notify pendant le drag : évite rebuild + save à chaque pixel)
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Top;
            Margin = new Thickness(Math.Max(0, _dragStartLeft + dx), Math.Max(0, _dragStartTop + dy), 0, 0);
        }

        /// <summary>True si l'élément est dans la carte dépliée (WidgetHost, sélecteur,
        /// vues plugins). Le drag libre ne s'arme jamais là : les contrôles custom
        /// (Border + MouseDown) seraient avalés.</summary>
        private bool IsInsideExpandedPanel(DependencyObject? elt)
        {
            try
            {
                while (elt != null)
                {
                    if (ReferenceEquals(elt, ExpandedPanel)) return true;
                    elt = System.Windows.Media.VisualTreeHelper.GetParent(elt);
                }
            }
            catch { }
            return false;
        }

        /// <summary>True si l'élément (ou un ancêtre) est interactif :
        /// bouton, carte widget (Tag), slider, grip, scroller. Le drag libre
        /// ne s'arme jamais dessus pour ne pas manger les clics.</summary>
        private static bool IsInteractiveElement(DependencyObject? elt)
        {
            try
            {
                while (elt != null)
                {
                    if (elt is Button or Thumb or Slider or ScrollViewer) return true;
                    if (elt is Border b && b.Tag is IslandWidget) return true;
                    if (elt is UserControl) return false;
                    elt = System.Windows.Media.VisualTreeHelper.GetParent(elt);
                }
            }
            catch { }
            return false;
        }

        private double DpiScaleX()
        {
            try
            {
                var src = PresentationSource.FromVisual(this);
                return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            }
            catch { return 1.0; }
        }

        /// <summary>Description compacte d'une source d'event (qui a été cliqué).</summary>
        private static string ElDesc(object? o)
        {
            try
            {
                if (o == null) return "null";
                if (o is FrameworkElement fe)
                {
                    string extra = "";
                    if (!string.IsNullOrEmpty(fe.Name)) extra += "#" + fe.Name;
                    if (fe.Tag is IslandWidget iw) extra += "[card:" + iw.GadgetType + "]";
                    else if (fe.Tag != null) extra += "[tag]";
                    if (fe is Button b) extra += "[btn:" + (b.Content?.ToString() ?? "") + "]";
                    if (fe is TextBlock t) extra += "[txt:" + ((t.Text?.Length ?? 0) > 24 ? t.Text!.Substring(0, 24) + "…" : t.Text) + "]";
                    if (fe is Thumb) extra += "[grip]";
                    if (fe is Slider) extra += "[seek]";
                    return o.GetType().Name + extra;
                }
                return o.GetType().Name;
            }
            catch { return "?"; }
        }

        /// <summary>Photo complète de l'îlot : position, tailles, contenu, état.</summary>
        private void LogSnapshot(string why)
        {
            try
            {
                double pwx = double.NaN, pwy = double.NaN, scx = 0, scy = 0;
                try
                {
                    var win = Window.GetWindow(this);
                    if (win != null)
                    {
                        var p = CompactRow.TranslatePoint(new Point(0, 0), win);
                        pwx = p.X; pwy = p.Y;
                    }
                    var s = PointToScreen(new Point(0, 0));
                    scx = s.X; scy = s.Y;
                }
                catch { }
                var cur = IslandVm.CurrentWidget;
                Palisades.Services.IslandDiag.Log($"SNAP-{why} state={IslandVm.State} pillWin=({pwx:0},{pwy:0}) scr=({scx:0},{scy:0}) root={IslandRoot.ActualWidth:0}x{IslandRoot.ActualHeight:0} panel={ExpandedPanel.ActualWidth:0}x{ExpandedPanel.ActualHeight:0}/{ExpandedPanel.Visibility} widget={cur?.GadgetType} np=[{IslandVm.NpTitle}]/[{IslandVm.NpArtist}]/{IslandVm.NpPlaying}");
            }
            catch { }
        }
    }
}
