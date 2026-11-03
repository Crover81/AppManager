using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AppManager
{
    /// <summary>
    /// Sorveglianza in background: icona nell'area di notifica, avvio con Windows, avvisi.
    /// Il lavoro vero (cosa è cambiato, quali residui restano) lo fa <see cref="InstallWatcher"/>.
    /// </summary>
    internal static class BackgroundService
    {
        private static TrayIcon tray;
        private static NotifyPopup popup;
        private static readonly List<HistoryEvent> pending = new List<HistoryEvent>();
        private static DispatcherTimer retry;
        private static bool initialized;

        public static bool IsActive { get { return tray != null; } }

        private static string ExePath { get { return System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName; } }

        /// <summary>All'avvio del programma (mai nella modalità di verifica grafica).</summary>
        public static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            InstallWatcher.Recorded += (e, notify) => OnUi(() => OnRecorded(e, notify));
            InstallWatcher.StatusChanged += () => OnUi(RaiseState);
            MonitorHistory.Changed += () => OnUi(() => { var vm = MainViewModel.Current; if (vm != null) vm.Monitor.Reload(); });
            if (!AppSettings.Current.BackgroundMonitor) return;
            Activate();
            // il programma è portable: se è stato spostato, l'avvio con Windows deve seguirlo
            string exe = ExePath;
            Task.Run(() => { if (!BackgroundStartup.IsRegisteredFor(exe)) BackgroundStartup.Register(exe); });
        }

        /// <summary>Attiva o disattiva la sorveglianza. Restituisce null, o il motivo per cui l'avvio con Windows non è stato accettato.</summary>
        public static string SetEnabled(bool on)
        {
            var s = AppSettings.Current;
            if (on)
            {
                string err = BackgroundStartup.Register(ExePath);
                if (err != null) return err;
                s.BackgroundMonitor = true;
                s.Save();
                if (!AppSettings.SaveDisabled) Activate();
            }
            else
            {
                s.BackgroundMonitor = false;
                s.Save();
                Deactivate();
                // riattivandola si riparte da ciò che c'è in quel momento, senza segnalare cambiamenti avvenuti a sorveglianza spenta
                if (!AppSettings.SaveDisabled) Task.Run(() => { BackgroundStartup.Unregister(); WatchSnapshot.Delete(); });
            }
            RaiseState();
            return null;
        }

        private static void Activate()
        {
            if (tray == null) tray = new TrayIcon();
            InstallWatcher.Start();
        }

        private static void Deactivate()
        {
            InstallWatcher.Stop();
            if (tray != null) { tray.Dispose(); tray = null; }
        }

        /// <summary>Alla chiusura di AppManager: l'icona va tolta subito, o resterebbe nell'area di notifica fino al passaggio del mouse.</summary>
        public static void Shutdown()
        {
            InstallWatcher.Stop();
            if (tray != null) { tray.Dispose(); tray = null; }
            if (popup != null) popup.CloseNow();
        }

        private static void RaiseState()
        {
            var vm = MainViewModel.Current;
            if (vm == null) return;
            vm.Settings.RaiseBackground();
            vm.Monitor.RaiseState();
        }

        private static void OnUi(Action a)
        {
            var app = Application.Current;
            if (app == null) return;
            if (app.Dispatcher.CheckAccess()) a();
            else app.Dispatcher.BeginInvoke(a);
        }

        // ------------------------------------------------------------------ Avvisi

        private static void OnRecorded(HistoryEvent e, bool notify)
        {
            if (!notify) return;
            pending.RemoveAll(p => p.Id == e.Id);
            pending.Add(e);
            ShowPending();
        }

        /// <summary>Con un gioco a schermo intero, una presentazione o lo schermo bloccato l'avviso aspetta.</summary>
        private static void ShowPending()
        {
            // una voce rivista o ignorata nel frattempo non va più segnalata
            pending.RemoveAll(p => { var now = MonitorHistory.Get(p.Id); return now == null || now.Status != HistoryEvent.Leftovers; });
            if (pending.Count == 0) return;
            if (!Native.CanShowNotification)
            {
                if (retry == null)
                {
                    retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                    retry.Tick += (s, a) => { retry.Stop(); retry = null; ShowPending(); };
                    retry.Start();
                }
                return;
            }
            // un avviso ancora aperto non viene sostituito: raccoglie anche le nuove disinstallazioni
            var list = popup != null && popup.IsOpen ? shown.Where(s => !pending.Any(p => p.Id == s.Id)).ToList() : new List<HistoryEvent>();
            list.AddRange(pending);
            pending.Clear();
            shown = list;
            Show(BuildNotice(list));
        }

        /// <summary>Le disinstallazioni nell'avviso aperto (vuoto se non c'è un avviso o se è solo informativo).</summary>
        private static List<HistoryEvent> shown = new List<HistoryEvent>();

        internal static NotifyViewModel BuildNotice(List<HistoryEvent> list)
        {
            var vm = new NotifyViewModel();
            if (list.Count == 1)
            {
                var e = list[0];
                vm.Title = L.F("Sono rimasti residui di {0}", e.Name);
                vm.Message = LeftoverSummary(e);
                vm.PrimaryText = L.T("Vedi e rimuovi");
                vm.OnPrimary = () => App.ShowMainWindow("monitor", e.Id);
                vm.OnIgnore = () => MonitorHistory.Update(e.Id, x => x.Status = HistoryEvent.Ignored);
            }
            else
            {
                int n = list.Sum(e => e.ItemCount);
                vm.Title = L.F("Residui dopo {0}", Fmt.Count(list.Count, "disinstallazione", "disinstallazioni"));
                vm.Message = L.F("{0}: {1} in tutto.", JoinNames(list.Select(e => e.Name).ToList()), Fmt.Count(n, "elemento", "elementi"));
                vm.PrimaryText = L.T("Vedi la cronologia");
                vm.OnPrimary = () => App.ShowMainWindow("monitor", null);
                vm.OnIgnore = () => { foreach (var e in list) MonitorHistory.Update(e.Id, x => x.Status = HistoryEvent.Ignored); };
            }
            return vm;
        }

        /// <summary>«Rimasti: 12 elementi (35 MB), anche nel registro di sistema.»</summary>
        internal static string LeftoverSummary(HistoryEvent e)
        {
            string size = e.ItemSize > 0 ? " (" + Fmt.Size(e.ItemSize) + ")" : "";
            return e.HasRegistryItems
                ? L.F("Rimasti: {0}{1}, anche nel registro di sistema.", Fmt.Count(e.ItemCount, "elemento", "elementi"), size)
                : L.F("Rimasti: {0}{1}.", Fmt.Count(e.ItemCount, "elemento", "elementi"), size);
        }

        private static string JoinNames(List<string> names)
        {
            if (names.Count <= 1) return names.FirstOrDefault() ?? "";
            var shown = names.Take(3).ToList();
            string head = string.Join(", ", shown.Take(shown.Count - 1));
            string text = L.F("{0} e {1}", head, shown.Last());
            return names.Count > 3 ? L.F("{0} e altre {1}", string.Join(", ", shown), names.Count - 3) : text;
        }

        /// <summary>Un avviso solo informativo (es. la prima volta che la finestra si chiude restando nell'area di notifica).</summary>
        public static void ShowInfo(string title, string message)
        {
            // un avviso di residui ancora aperto è più importante: non si copre
            if (popup != null && popup.IsOpen && shown.Count > 0) return;
            Show(new NotifyViewModel { Title = title, Message = message });
            shown = new List<HistoryEvent>();
        }

        internal static void Show(NotifyViewModel vm)
        {
            if (popup == null) popup = new NotifyPopup { Closed = () => shown = new List<HistoryEvent>() };
            popup.Show(vm);
        }

        /// <summary>Solo per le prove grafiche: la finestra dell'avviso già aperta.</summary>
        internal static Window ShowForTest(NotifyViewModel vm)
        {
            if (vm != null) Show(vm);
            return popup == null ? null : popup.Window;
        }

        /// <summary>Solo per le prove grafiche: come se la sorveglianza avesse appena trovato residui.</summary>
        internal static void RecordedForTest(HistoryEvent e)
        {
            OnRecorded(e, true);
        }
    }

    // ====================================================================== Icona nell'area di notifica

    internal sealed class TrayIcon : IDisposable
    {
        private readonly System.Windows.Forms.NotifyIcon icon;

        public TrayIcon()
        {
            icon = new System.Windows.Forms.NotifyIcon { Icon = LoadIcon(), Text = L.T("AppManager: sorveglianza attiva"), Visible = true };
            icon.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left) App.ShowMainWindow(null, null);
                else if (e.Button == System.Windows.Forms.MouseButtons.Right) ShowMenu();
            };
        }

        private static System.Drawing.Icon LoadIcon()
        {
            try
            {
                using (var s = typeof(TrayIcon).Assembly.GetManifestResourceStream("AppManager.ui.app.ico"))
                    if (s != null) return new System.Drawing.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
            catch (Exception ex) { Log.Warn("Icona dell'area di notifica: {0}", ex.Message); }
            return System.Drawing.SystemIcons.Application;
        }

        private static void ShowMenu()
        {
            var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
            Action<string, Action> add = (text, action) =>
            {
                var item = new MenuItem { Header = text };
                item.Click += (s, e) => action();
                menu.Items.Add(item);
            };
            add(L.T("Apri AppManager"), () => App.ShowMainWindow(null, null));
            add(L.T("Cronologia delle disinstallazioni"), () => App.ShowMainWindow("monitor", null));
            add(L.T("Controlla adesso"), InstallWatcher.CheckNow);
            var sep = new Separator();
            var style = Application.Current.TryFindResource("MenuSeparator") as Style;
            if (style != null) sep.Style = style;
            menu.Items.Add(sep);
            add(L.T("Disattiva la sorveglianza"), () => BackgroundService.SetEnabled(false));
            add(L.T("Chiudi AppManager"), App.ExitFromTray);
            menu.Opened += (s, e) =>
            {
                // senza il primo piano il menu non si chiuderebbe cliccando altrove
                var src = PresentationSource.FromVisual(menu) as HwndSource;
                if (src != null) Native.Foreground(src.Handle);
            };
            menu.IsOpen = true;
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
        }
    }

    // ====================================================================== Avviso a comparsa

    /// <summary>
    /// Avviso subito sopra la barra delle applicazioni, sopra orologio e icone di notifica. Non ruba il fuoco, sparisce da solo
    /// dopo 20 secondi (resta finché il mouse ci è sopra) e segue il tema dell'app. Se è già aperto si aggiorna, non si duplica.
    /// </summary>
    internal sealed class NotifyPopup
    {
        private DispatcherTimer timer;
        public Window Window { get; private set; }
        public bool IsOpen { get { return Window != null; } }
        internal Action Closed;

        public void Show(NotifyViewModel vm)
        {
            if (Window != null)
            {
                // stesso avviso, contenuto nuovo: niente sparizione e ricomparsa, il conto alla rovescia riparte
                Window.BeginAnimation(UIElement.OpacityProperty, null);
                Window.Opacity = 1;
                Window.DataContext = vm;
                vm.Dismiss = Dismiss;
                timer.Stop();
                timer.Interval = TimeSpan.FromSeconds(20);
                if (!Window.IsMouseOver) timer.Start();
                return;
            }
            var w = (Window)App.LoadXaml("Notify.xaml");
            // la prima finestra creata diventerebbe la finestra principale del programma: non deve esserlo
            if (Application.Current.MainWindow == w) Application.Current.MainWindow = null;
            w.DataContext = vm;
            vm.Dismiss = Dismiss;
            w.SourceInitialized += (s, e) => Native.MakeNotificationWindow(new WindowInteropHelper(w).Handle);
            w.Opacity = 0;
            w.Loaded += (s, e) =>
            {
                bool top = Place(w);
                w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
                var slide = w.FindName("Slide") as TranslateTransform;
                if (slide != null) slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(top ? -14 : 14, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            };
            // un testo più lungo cambia l'altezza: l'avviso resta attaccato alla barra
            w.SizeChanged += (s, e) => { if (w.IsLoaded) Place(w); };
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            timer.Tick += (s, e) => Dismiss();
            w.MouseEnter += (s, e) => timer.Stop();
            w.MouseLeave += (s, e) => { timer.Interval = TimeSpan.FromSeconds(6); timer.Start(); };
            Window = w;
            w.Show();
            timer.Start();
        }

        /// <summary>
        /// Sopra l'area di orologio, volume e rete, senza mai coprire la barra delle applicazioni: si usa solo l'area di lavoro,
        /// che la esclude. Con la barra in alto l'avviso sta sotto di essa; con la barra a sinistra, l'orologio è in basso a sinistra.
        /// Restituisce true se la barra è in alto.
        /// </summary>
        private static bool Place(Window w)
        {
            var area = SystemParameters.WorkArea;
            bool barTop = area.Top > 0, barLeft = area.Left > 0;
            w.Left = barLeft ? area.Left : area.Right - w.ActualWidth;
            w.Top = barTop ? area.Top : area.Bottom - w.ActualHeight;
            return barTop;
        }

        private void Dismiss()
        {
            var w = Window;
            if (w == null) return;
            if (timer != null) timer.Stop();
            var fade = new DoubleAnimation(w.Opacity, 0, TimeSpan.FromMilliseconds(160));
            fade.Completed += (s, e) => { if (Window == w) CloseNow(); else w.Close(); };
            w.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        public void CloseNow()
        {
            if (timer != null) { timer.Stop(); timer = null; }
            var w = Window;
            Window = null;
            if (w != null) try { w.Close(); } catch { }
            if (w != null && Closed != null) Closed();
        }
    }
}
