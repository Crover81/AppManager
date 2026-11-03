using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

[assembly: AssemblyTitle("AppManager")]
[assembly: AssemblyProduct("AppManager")]
[assembly: AssemblyDescription("Gestione e disinstallazione completa di giochi e programmi")]
[assembly: AssemblyVersion("2.3.0.0")]
[assembly: AssemblyFileVersion("2.3.0.0")]
// dichiara .NET Framework 4.8: attiva i comportamenti moderni (TLS scelto da Windows, DPI per monitor in WPF)
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace AppManager
{
    public sealed class App : Application
    {
        private static string[] arguments = new string[0];
        private static Mutex instanceLock;
        private bool closeConfirmed;

        [STAThread]
        public static void Main(string[] args)
        {
            arguments = args;
            // la modalità di verifica grafica non deve lasciare tracce nelle impostazioni vere
            if (Arg("--screenshot") != null)
            {
                AppSettings.SaveDisabled = true;
                L.ForcedCode = Arg("--lang");
            }
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("Errore non gestito", e.ExceptionObject as Exception);
            // una sola copia aperta per cartella dati: due finestre che puliscono o salvano le impostazioni insieme si pestano i piedi
            bool background = args.Contains(BackgroundStartup.Argument);
            if (Arg("--screenshot") == null && !TakeInstanceLock(args.Contains("--restarted")))
            {
                // già aperto, anche solo nell'area di notifica: gli si chiede di mostrare la finestra. Avviato con Windows, non fa nulla
                if (!background && !SignalOtherInstance()) ActivateOtherInstance();
                return;
            }
            // la finestra può chiudersi restando nell'area di notifica: quando il programma finisce lo decide App
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Error("Errore dell'interfaccia", e.Exception);
                e.Handled = true;
                string shot = Arg("--screenshot");
                if (shot != null)
                {
                    // modalità di verifica: l'errore va registrato accanto all'immagine, senza finestre modali
                    File.AppendAllText(shot + ".error.txt", e.Exception + Environment.NewLine);
                    return;
                }
                MessageBox.Show(L.F("Si è verificato un errore imprevisto:\n\n{0}\n\nI dettagli sono nel registro attività.", e.Exception.Message),
                    "AppManager", MessageBoxButton.OK, MessageBoxImage.Warning);
            };
            TaskScheduler.UnobservedTaskException += (s, e) => { Log.Error("Attività in background", e.Exception); e.SetObserved(); };
            app.Startup += (s, e) => app.Start();
            app.Exit += (s, e) =>
            {
                BackgroundService.Shutdown();
                Microsoft.Win32.SystemEvents.UserPreferenceChanged -= app.OnSystemPreferenceChanged;
                if (Arg("--screenshot") == null) PcResources.RemoveEmptyFoldersOnExit();
                Log.Info("Chiusura di AppManager");
                Log.Flush();
            };
            app.Run();
        }

        private void Start()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher));
            // ciò che le versioni precedenti avevano lasciato sul PC si sposta nella cartella Dati, una volta sola
            if (Arg("--screenshot") == null)
                foreach (var legacy in AppPaths.LegacyDataDirs()) PcResources.AdoptLegacyData(legacy);
            if (Arg("--theme") != null) AppSettings.Current.Theme = Arg("--theme");
            // numeri, date e testi dei controlli nella lingua dell'interfaccia
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(L.Culture.IetfLanguageTag)));
            // conservazione del registro attività: via le voci troppo vecchie e il vecchio log.txt.old
            Log.Maintain(AppSettings.Current.LogRetentionDays);
            string version = typeof(App).Assembly.GetName().Version.ToString(2);
            if (Native.IsElevated) Log.Info("AppManager {0} avviato come amministratore", version);
            else Log.Info("AppManager {0} avviato", version);
            bool background = arguments.Contains(BackgroundStartup.Argument) && Arg("--screenshot") == null;
            if (background && !AppSettings.Current.BackgroundMonitor)
            {
                // avvio con Windows rimasto da quando la sorveglianza era attiva: si toglie e basta
                BackgroundStartup.Unregister();
                Shutdown();
                return;
            }
            try
            {
                ThemeInfo.IsLight = ThemeInfo.Resolve(AppSettings.Current.Theme);
                LoadDictionaries();
            }
            catch (Exception ex)
            {
                StartupFailed(ex);
                return;
            }
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
            string shot = Arg("--screenshot");
            // avviato con Windows: solo l'icona nell'area di notifica, la finestra si crea quando serve
            if (!background && !CreateMainWindow()) return;
            if (shot != null)
            {
                RunScreenshotScript(mainWindow, (MainViewModel)mainWindow.DataContext, shot);
                return;
            }
            if (background) Log.Info("Avviato con Windows nell'area di notifica");
            ListenForShowRequests();
            BackgroundService.Initialize();
            Task.Run(() => PcResources.StartupMaintenance());
        }

        private void StartupFailed(Exception ex)
        {
            // senza finestra il programma resterebbe aperto e invisibile: meglio avvisare e chiudere
            Log.Error("Errore all'avvio", ex);
            string shotFile = Arg("--screenshot");
            if (shotFile != null) File.AppendAllText(shotFile + ".error.txt", ex + Environment.NewLine);
            else MessageBox.Show(L.F("AppManager non è riuscito ad avviarsi:\n\n{0}", ex.Message), "AppManager", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }

        // ------------------------------------------------------------------ Finestra principale

        private Window mainWindow;
        private bool exiting;

        /// <summary>La finestra principale: creata all'avvio o, se AppManager è partito con Windows, la prima volta che serve.</summary>
        private bool CreateMainWindow()
        {
            Window window;
            try { window = (Window)LoadXaml("MainWindow.xaml"); }
            catch (Exception ex)
            {
                StartupFailed(ex);
                return false;
            }
            window.SourceInitialized += (s, e) => StyleTitleBar(window);
            // ridotto a icona il programma non ha bisogno di tenere in memoria immagini e interfaccia
            window.StateChanged += (s, e) => { if (window.WindowState == WindowState.Minimized) Task.Run(() => PcResources.TrimMemory()); };
            var vm = new MainViewModel();
            window.DataContext = vm;
            KeyboardShortcuts.Attach(window, vm);
            window.Closing += (s, e) =>
            {
                // con la sorveglianza attiva la finestra si nasconde soltanto: il lavoro in corso continua e AppManager resta nell'area di notifica
                if (BackgroundService.IsActive && !exiting)
                {
                    e.Cancel = true;
                    HideMainWindow(window, vm);
                    return;
                }
                string work = vm.WorkInProgress;
                if (work == null || closeConfirmed) return;
                var answer = MessageBox.Show(window,
                    L.F("{0} Chiudendo ora l'operazione si interrompe a metà.\n\nChiudere comunque?", work),
                    "AppManager", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer == MessageBoxResult.Yes) { closeConfirmed = true; Log.Info("Chiusura durante un'operazione in corso"); }
                else { e.Cancel = true; exiting = false; }
            };
            window.Closed += (s, e) =>
            {
                if (mainWindow != window) return;
                mainWindow = null;
                Shutdown();
            };
            mainWindow = window;
            MainWindow = window;
            window.Show();
            if (Arg("--screenshot") == null) vm.OnWindowReady();
            return true;
        }

        private void HideMainWindow(Window window, MainViewModel vm)
        {
            window.Hide();
            vm.Sleep();
            Task.Run(() => PcResources.TrimMemory());
            var s = AppSettings.Current;
            if (s.TrayHintShown) return;
            s.TrayHintShown = true;
            s.Save();
            BackgroundService.ShowInfo(L.T("AppManager resta attivo"),
                L.T("La sorveglianza continua nell'area di notifica, accanto all'orologio. Per chiudere AppManager usa il menu della sua icona."));
        }

        /// <summary>Mostra la finestra (creandola se serve), su una pagina e, volendo, sui residui di una disinstallazione.</summary>
        internal static void ShowMainWindow(string page, string reviewId)
        {
            var app = Current as App;
            if (app == null) return;
            if (app.mainWindow == null && !app.CreateMainWindow()) return;
            var w = app.mainWindow;
            var vm = w.DataContext as MainViewModel;
            if (!w.IsVisible)
            {
                w.Show();
                if (vm != null) vm.Wake();
            }
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
            Native.BringToFront(new WindowInteropHelper(w).Handle);
            // con un dialogo aperto (es. una pulizia) non si cambia pagina sotto i piedi dell'utente
            if (vm == null || vm.HasDialog) return;
            if (page != null) vm.Navigate(page);
            if (reviewId != null) vm.Monitor.OpenReview(reviewId);
        }

        /// <summary>«Chiudi AppManager» dal menu dell'icona: con un'operazione in corso chiede conferma, come chiudendo la finestra.</summary>
        internal static void ExitFromTray()
        {
            var app = Current as App;
            if (app == null) return;
            var w = app.mainWindow;
            if (w == null) { app.Shutdown(); return; }
            var vm = w.DataContext as MainViewModel;
            if (vm != null && vm.WorkInProgress != null) ShowMainWindow(null, null);
            app.exiting = true;
            w.Close();
        }

        // ------------------------------------------------------------------ Tema

        private void LoadDictionaries()
        {
            Resources.MergedDictionaries.Clear();
            foreach (var name in new[] { ThemeInfo.IsLight ? "Palette.Light.xaml" : "Palette.Dark.xaml", "Theme.xaml", "Shared.xaml", "Overview.xaml", "Pages.xaml", "Dialogs.xaml", "Saves.xaml", "Logs.xaml", "Backups.xaml", "Monitor.xaml" })
                Resources.MergedDictionaries.Add((ResourceDictionary)LoadXaml(name));
        }

        private void StyleTitleBar(Window window)
        {
            var caption = (Color)FindResource("TitleBarColor");
            Native.StyleTitleBar(new WindowInteropHelper(window).Handle, (caption.R << 16) | (caption.G << 8) | caption.B, !ThemeInfo.IsLight);
        }

        /// <summary>Applica il tema scelto senza riavviare: la pagina viene ricreata e torna dov'era.</summary>
        internal static void ApplyTheme()
        {
            var app = Current as App;
            if (app != null) app.Reskin();
        }

        private void Reskin()
        {
            bool light = ThemeInfo.Resolve(AppSettings.Current.Theme);
            if (light == ThemeInfo.IsLight) return;
            var window = mainWindow;
            if (window == null) return;
            double offset = 0;
            var pageHost = window.FindName("PageHost") as DependencyObject;
            var scroll = pageHost == null ? null : FindChild<System.Windows.Controls.ScrollViewer>(pageHost);
            if (scroll != null) offset = scroll.VerticalOffset;
            ThemeInfo.IsLight = light;
            LoadDictionaries();
            // i riferimenti ai colori sono risolti al caricamento: si ricarica il contenuto, la finestra resta la stessa (posizione, dimensioni, ViewModel)
            var fresh = (Window)LoadXaml("MainWindow.xaml");
            var root = fresh.Content;
            fresh.Content = null;
            NameScope.SetNameScope(window, NameScope.GetNameScope(fresh));
            window.Background = fresh.Background;
            window.Foreground = fresh.Foreground;
            window.Content = root;
            fresh.Close();
            StyleTitleBar(window);
            var vm = window.DataContext as MainViewModel;
            if (vm != null) vm.ThemeChanged();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                var host = window.FindName("PageHost") as DependencyObject;
                var sv = host == null ? null : FindChild<System.Windows.Controls.ScrollViewer>(host);
                if (sv != null) sv.ScrollToVerticalOffset(offset);
            }));
        }

        /// <summary>Con «Come Windows», il programma segue subito il tema scelto in Windows.</summary>
        private void OnSystemPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (e.Category != Microsoft.Win32.UserPreferenceCategory.General || AppSettings.Current.Theme != "system") return;
            Dispatcher.BeginInvoke(new Action(ApplyTheme));
        }

        /// <summary>Carica uno XAML incorporato nell'eseguibile (adattando il nome dell'assembly).</summary>
        internal static object LoadXaml(string name)
        {
            var asm = typeof(App).Assembly;
            string text;
            using (var s = asm.GetManifestResourceStream("AppManager.ui." + name))
            using (var r = new StreamReader(s)) text = r.ReadToEnd();
            text = text.Replace("assembly=AppManager\"", "assembly=" + asm.GetName().Name + "\"");
            return XamlReader.Parse(L.TranslateXaml(text));
        }

        private static string Arg(string name)
        {
            int i = Array.IndexOf(arguments, name);
            return i >= 0 && i + 1 < arguments.Length ? arguments[i + 1] : null;
        }

        /// <summary>
        /// Blocco dell'istanza unica, legato alla cartella dati (una copia portable su chiavetta può convivere con quella installata).
        /// Dopo un riavvio come amministratore si aspetta che la copia precedente si chiuda.
        /// </summary>
        private static bool TakeInstanceLock(bool waitPrevious)
        {
            string id = InstanceId();
            try
            {
                bool created;
                instanceLock = new Mutex(true, @"Local\AppManager." + id, out created);
                if (created) return true;
                if (!waitPrevious) return false;
                try { return instanceLock.WaitOne(15000); }
                catch (AbandonedMutexException) { return true; }
            }
            catch (UnauthorizedAccessException)
            {
                return false; // c'è già una copia aperta come amministratore
            }
            catch (Exception ex)
            {
                Log.Error("Blocco istanza unica", ex);
                return true; // nel dubbio si parte
            }
        }

        private static string InstanceId()
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(AppPaths.DataDir.ToLowerInvariant()))).Replace("-", "").Substring(0, 16);
        }

        private static string ShowEventName { get { return @"Local\AppManager." + InstanceId() + ".show"; } }
        private static EventWaitHandle showRequests;

        /// <summary>Chiede alla copia già aperta di mostrare la finestra. Falso se non risponde.</summary>
        private static bool SignalOtherInstance()
        {
            try
            {
                EventWaitHandle h;
                if (!EventWaitHandle.TryOpenExisting(ShowEventName, out h)) return false;
                using (h) h.Set();
                return true;
            }
            catch { return false; }
        }

        /// <summary>Un secondo avvio di AppManager chiede a questa copia di mostrare la finestra (anche se era solo nell'area di notifica).</summary>
        private void ListenForShowRequests()
        {
            try { showRequests = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName); }
            catch (Exception ex)
            {
                Log.Warn("Richieste di apertura da un'altra copia: {0}", ex.Message);
                return;
            }
            var listener = new Thread(() =>
            {
                while (true)
                {
                    try { showRequests.WaitOne(); }
                    catch { return; }
                    Dispatcher.BeginInvoke(new Action(() => ShowMainWindow(null, null)));
                }
            }) { IsBackground = true, Name = "Richieste di apertura" };
            listener.Start();
        }

        /// <summary>Porta in primo piano la finestra della copia già aperta.</summary>
        private static void ActivateOtherInstance()
        {
            var me = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
            {
                try
                {
                    if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                    Native.BringToFront(p.MainWindowHandle);
                    return;
                }
                catch { }
                finally { p.Dispose(); }
            }
            MessageBox.Show(L.T("AppManager è già aperto."), "AppManager", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ------------------------------------------------------------------ Modalità di verifica grafica (solo test)

        /// <summary>
        /// --screenshot file.png [--page games|programs|game-orphans|program-orphans|backups|settings] [--select nome]
        /// [--dialog analyze|uninstall|confirm] [--scan] [--wait secondi] [--components] [--filter chiave]
        /// Esegue i passi indicati, salva un'immagine della finestra e chiude il programma.
        /// </summary>
        private async void RunScreenshotScript(Window window, MainViewModel vm, string file)
        {
            try
            {
                int wait;
                if (!int.TryParse(Arg("--wait") ?? "", out wait)) wait = 8;
                string size = Arg("--size");
                if (size != null)
                {
                    var p = size.Split('x');
                    window.Width = double.Parse(p[0]);
                    window.Height = double.Parse(p[1]);
                }
                if (arguments.Contains("--early"))
                {
                    // immagine della finestra appena aperta, prima che i giochi e i programmi siano stati trovati
                    if (Arg("--page") != null) vm.Navigate(Arg("--page"));
                    await Task.Delay(400);
                    Capture(window, file);
                    Shutdown();
                    return;
                }
                if (arguments.Contains("--demohistory")) MonitorHistory.UseInMemory(MonitorPageViewModel.DemoEvents());
                while (vm.Games.IsLoading || vm.Programs.IsLoading || !vm.Programs.HasLoaded) await Task.Delay(200);
                if (Arg("--layoutcheck") != null)
                {
                    await LayoutCheckAll(window, vm, Arg("--layoutcheck"));
                    Shutdown();
                    return;
                }
                string page = Arg("--page");
                if (page != null) vm.Navigate(page);
                LibraryPageViewModel library = page == "programs" ? (LibraryPageViewModel)vm.Programs : vm.Games;
                if (arguments.Contains("--components")) vm.Programs.ShowComponents = true;
                string filter = Arg("--filter");
                if (filter != null)
                {
                    var chip = library.Filters.FirstOrDefault(f => f.Key == filter);
                    if (chip != null) chip.IsSelected = true;
                }
                string select = Arg("--select");
                if (select != null)
                    library.Selected = library.Items.FirstOrDefault(g => g.Name.IndexOf(select, StringComparison.OrdinalIgnoreCase) >= 0) ?? library.Selected;
                if (arguments.Contains("--scan")) await vm.ScanOrphansAsync();
                string logTab = Arg("--logtab");
                if (logTab != null) vm.Logs.Tab = logTab;
                if (arguments.Contains("--logscan"))
                {
                    vm.Logs.ScanCommand.Execute(null);
                    for (int i = 0; i < 600 && (vm.Logs.IsScanning || !vm.Logs.HasScanned); i++) await Task.Delay(200);
                }
                if (arguments.Contains("--reload") || arguments.Contains("--reloaded"))
                {
                    // --reload: immagine scattata durante una ricarica; --reloaded: appena la ricarica è finita
                    (page == "backups" ? vm.Backups.RefreshCommand : library.RefreshCommand).Execute(null);
                    await Task.Delay(350);
                    if (arguments.Contains("--reloaded"))
                        for (int i = 0; i < 600 && (page == "backups" ? vm.Backups.IsLoading : library.IsLoading); i++) await Task.Delay(100);
                    await Task.Delay(300);
                    Capture(window, file);
                    Shutdown();
                    return;
                }
                string dialog = Arg("--dialog");
                if (dialog == "analyze") library.AnalyzeCommand.Execute(null);
                else if (dialog == "uninstall") library.UninstallCommand.Execute(null);
                else if (dialog == "saves") vm.OpenSaves(library.Selected);
                else if (dialog == "confirm") vm.Confirm("Titolo di prova", "Messaggio di prova per verificare l'aspetto della finestra di conferma.", L.T("Elimina"), true, null, "Opzione di prova");
                string expand = Arg("--expand");
                // cambio di tema a programma aperto: l'immagine mostra la finestra dopo il cambio
                if (arguments.Contains("--swap")) vm.SetTheme(ThemeInfo.IsLight ? "dark" : "light");
                await Task.Delay(TimeSpan.FromSeconds(wait));
                string scroll = Arg("--scroll");
                if (scroll != null)
                {
                    var sv = FindChild<System.Windows.Controls.ScrollViewer>(window);
                    if (sv != null) sv.ScrollToVerticalOffset(double.Parse(scroll));
                    await Task.Delay(500);
                }
                if (expand != null)
                {
                    if (vm.CurrentPage is NetworkPageViewModel)
                        foreach (var p in vm.Network.Processes.Take(int.Parse(expand))) p.Expanded = true;
                    else
                    {
                        var orphans = vm.CurrentPage as OrphansPageViewModel ?? vm.GameOrphans;
                        foreach (var g in orphans.Groups.Take(int.Parse(expand))) g.IsExpanded = true;
                    }
                    await Task.Delay(800);
                }
                string notify = Arg("--notify");
                if (notify != null)
                {
                    // solo l'immagine dell'avviso, con dati finti
                    var demo = MonitorPageViewModel.DemoEvents();
                    Window popup;
                    if (notify == "merge")
                    {
                        // due disinstallazioni ravvicinate: il secondo avviso deve aggiornare il primo, non sostituirlo
                        MonitorHistory.UseInMemory(demo);
                        var first = demo.First(x => x.Status == HistoryEvent.Leftovers);
                        var second = new HistoryEvent { Id = Guid.NewGuid().ToString("N"), When = DateTime.Now.ToString("o"), Type = HistoryEvent.Uninstalled, Name = "Secondo programma", Status = HistoryEvent.Leftovers, Items = first.Items };
                        MonitorHistory.Add(second);
                        BackgroundService.RecordedForTest(first);
                        await Task.Delay(700);
                        BackgroundService.RecordedForTest(second);
                        popup = BackgroundService.ShowForTest(null);
                    }
                    else popup = BackgroundService.ShowForTest(notify == "info"
                        ? new NotifyViewModel { Title = L.T("AppManager resta attivo"), Message = L.T("La sorveglianza continua nell'area di notifica, accanto all'orologio. Per chiudere AppManager usa il menu della sua icona.") }
                        : BackgroundService.BuildNotice(notify == "many" ? demo.Where(x => x.ItemCount > 0).ToList() : demo.Take(1).ToList()));
                    await Task.Delay(900);
                    var area = SystemParameters.WorkArea;
                    File.WriteAllText(file + ".pos.txt", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "avviso {0:0} {1:0} {2:0} {3:0}\r\narea {4:0} {5:0} {6:0} {7:0}\r\nschermo {8:0} {9:0}\r\ntitolo {10}\r\n",
                        popup.Left, popup.Top, popup.Left + popup.ActualWidth, popup.Top + popup.ActualHeight, area.Left, area.Top, area.Right, area.Bottom,
                        SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight, ((NotifyViewModel)popup.DataContext).Title));
                    Capture(popup, file);
                    Shutdown();
                    return;
                }
                Capture(window, file);
            }
            catch (Exception ex)
            {
                File.WriteAllText(file + ".error.txt", ex.ToString());
            }
            Shutdown();
        }

        // ------------------------------------------------------------------ Misura automatica dell'impaginazione (solo test)

        /// <summary>
        /// Apre ogni pagina e ogni finestra e annota testi attaccati ai pulsanti, pulsanti sovrapposti, testi tagliati ed elementi
        /// fuori dalla finestra. Legge soltanto: i dialoghi si fermano alla conferma o all'analisi, nessuna azione viene avviata.
        /// </summary>
        private async Task LayoutCheckAll(Window window, MainViewModel vm, string file)
        {
            var report = new System.Text.StringBuilder();
            MonitorHistory.UseInMemory(MonitorPageViewModel.DemoEvents());
            await vm.ScanOrphansAsync();
            foreach (var page in new[] { "overview", "programs", "games", "program-orphans", "game-orphans", "startup", "monitor", "network", "backups", "logs", "settings" })
            {
                vm.Navigate(page);
                await Task.Delay(page == "backups" || page == "startup" ? 2500 : 1200);
                foreach (var g in (page == "program-orphans" ? vm.ProgramOrphans : vm.GameOrphans).Groups.Take(6)) g.IsExpanded = page.EndsWith("orphans");
                await Task.Delay(300);
                LayoutCheck(window, vm, page, report);
            }
            vm.Navigate("logs");
            vm.Logs.ScanCommand.Execute(null);
            for (int i = 0; i < 300 && (vm.Logs.IsScanning || !vm.Logs.HasScanned); i++) await Task.Delay(200);
            await Task.Delay(500);
            LayoutCheck(window, vm, "logs-risultati", report);
            vm.Logs.Tab = "app";
            await Task.Delay(1000);
            LayoutCheck(window, vm, "logs-app", report);
            await LayoutCheckBackups(window, vm, report);
            vm.Navigate("games");
            await Task.Delay(500);
            vm.Games.AnalyzeCommand.Execute(null);
            var wizard = vm.Dialog as CleanupDialogViewModel;
            for (int i = 0; i < 120 && wizard != null && !wizard.IsReview && !wizard.IsDone; i++) await Task.Delay(500);
            await Task.Delay(500);
            LayoutCheck(window, vm, "dialogo-dati", report);
            vm.Dialog = null;
            vm.Games.UninstallCommand.Execute(null);
            await Task.Delay(800);
            LayoutCheck(window, vm, "dialogo-disinstalla", report);
            vm.Dialog = null;
            vm.OpenSaves(vm.Games.Selected);
            await Task.Delay(4000);
            LayoutCheck(window, vm, "dialogo-salvataggi", report);
            vm.Dialog = null;
            vm.Navigate("settings");
            vm.Confirm(L.T("Riavviare AppManager?"), L.T("La nuova lingua vale dal prossimo avvio del programma."), L.T("Riavvia adesso"), false, null);
            await Task.Delay(500);
            LayoutCheck(window, vm, "dialogo-conferma", report);
            vm.Dialog = null;
            var demo = MonitorPageViewModel.DemoEvents();
            foreach (var notice in new[] { BackgroundService.BuildNotice(demo.Take(1).ToList()), BackgroundService.BuildNotice(demo.Where(x => x.ItemCount > 0).ToList()) })
            {
                var popup = BackgroundService.ShowForTest(notice);
                await Task.Delay(800);
                LayoutCheck(popup, vm, "avviso", report);
            }
            File.WriteAllText(file, report.ToString());
        }

        /// <summary>La pagina Backup con dati finti: la cartella dei backup vale solo in memoria e la cartella temporanea viene tolta.</summary>
        private async Task LayoutCheckBackups(Window window, MainViewModel vm, System.Text.StringBuilder report)
        {
            string dir = Path.Combine(Path.GetTempPath(), "am_layout_" + Guid.NewGuid().ToString("N"));
            string original = AppSettings.Current.BackupFolder;
            try
            {
                string saves = Path.Combine(dir, "Salvataggi");
                Directory.CreateDirectory(saves);
                File.WriteAllText(Path.Combine(saves, "partita.sav"), "prova");
                var g = new AppEntry { Id = "test:impaginazione", Kind = AppKind.Game, Name = "Gioco con un nome piuttosto lungo per la prova di impaginazione", InstallDir = Path.Combine(dir, "Gioco") };
                AppSettings.Current.BackupFolder = Path.Combine(dir, "Backup");
                SaveVault.AddManualLocation(g, saves);
                var locations = SaveVault.FindLocations(g, new System.Collections.Generic.List<AppEntry>(), false, null, CancellationToken.None);
                SaveVault.Backup(g, locations, SaveVault.SavesReason, null, CancellationToken.None);
                SaveVault.Backup(g, locations, SaveVault.BeforeRestoreReason, null, CancellationToken.None);
                BackupCatalog.Invalidate();
                vm.Navigate("backups");
                await Task.Delay(2500);
                LayoutCheck(window, vm, "backup-con-dati", report);
            }
            catch (Exception ex) { report.AppendLine("backup-con-dati | NON PROVATO | " + ex.Message); }
            finally
            {
                AppSettings.Current.BackupFolder = original;
                BackupCatalog.Invalidate();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void LayoutCheck(Window window, MainViewModel vm, string where, System.Text.StringBuilder report)
        {
            var root = (FrameworkElement)window.Content;
            var start = (vm.HasDialog ? window.FindName("DialogHost") : root) as DependencyObject;
            var buttons = new System.Collections.Generic.List<System.Tuple<System.Windows.Controls.Primitives.ButtonBase, Rect, bool>>();
            var texts = new System.Collections.Generic.List<System.Tuple<System.Windows.Controls.TextBlock, Rect>>();
            var seen = new System.Collections.Generic.HashSet<string>();
            Action<DependencyObject, bool> walk = null;
            walk = (d, inButton) =>
            {
                var fe = d as FrameworkElement;
                if (fe != null && (!fe.IsVisible || fe is System.Windows.Controls.Primitives.ScrollBar)) return;
                var button = d as System.Windows.Controls.Primitives.ButtonBase;
                var text = d as System.Windows.Controls.TextBlock;
                if (fe != null && (button != null || text != null) && fe.ActualWidth > 0)
                {
                    Rect rect;
                    if (text != null && !string.IsNullOrWhiteSpace(text.Text))
                    {
                        // è il testo disegnato che conta, non la casella (spesso molto più larga)
                        if (!inButton && VisibleRect(fe, root, TextExtent(text, window), out rect)) texts.Add(Tuple.Create(text, rect));
                        if (IsTrimmed(text, window) && !text.Text.Contains("\\") && !IsInsideListItem(text) && VisibleRect(fe, root, new Rect(0, 0, fe.ActualWidth, fe.ActualHeight), out rect))
                            Note(report, seen, where, "TAGLIATO", Describe(fe));
                        else if (IsClipped(text, root, window) && !text.Text.Contains("\\") && !IsInsideListItem(text) && VisibleRect(fe, root, new Rect(0, 0, fe.ActualWidth, fe.ActualHeight), out rect))
                            Note(report, seen, where, "TAGLIATO", Describe(fe));
                    }
                    else if (button != null)
                    {
                        // un pulsante senza sfondo si vede solo per il suo contenuto: conta quello, non il margine interno
                        bool visibleBox = button is System.Windows.Controls.RadioButton || IsOpaque(button.Background);
                        var local = new Rect(0, 0, fe.ActualWidth, fe.ActualHeight);
                        if (!visibleBox)
                        {
                            var inner = FindVisibleText(button);
                            if (inner != null && inner.ActualWidth > 0)
                                local = inner.TransformToAncestor(button).TransformBounds(TextExtent(inner, window));
                        }
                        if (VisibleRect(fe, root, local, out rect))
                        {
                            buttons.Add(Tuple.Create(button, rect, visibleBox));
                            if (rect.Right > root.ActualWidth + 1) Note(report, seen, where, "FUORI", Describe(fe));
                        }
                    }
                }
                int n = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++) walk(VisualTreeHelper.GetChild(d, i), inButton || button != null);
            };
            if (start != null) walk(start, false);
            foreach (var b in buttons)
            {
                foreach (var t in texts)
                {
                    double overlap = Math.Min(b.Item2.Bottom, t.Item2.Bottom) - Math.Max(b.Item2.Top, t.Item2.Top);
                    double gap = Math.Max(b.Item2.Left - t.Item2.Right, t.Item2.Left - b.Item2.Right);
                    if (overlap > 2 && gap < 8) Note(report, seen, where, gap < 0 ? "SOPRA" : "ATTACCATO", Describe(t.Item1) + "  <->  " + Describe(b.Item1) + "  (" + gap.ToString("0") + " px)");
                }
                foreach (var o in buttons)
                {
                    if (ReferenceEquals(o.Item1, b.Item1) || b.Item2.Left > o.Item2.Left || !(b.Item3 && o.Item3)) continue;
                    double overlap = Math.Min(b.Item2.Bottom, o.Item2.Bottom) - Math.Max(b.Item2.Top, o.Item2.Top);
                    double gap = Math.Max(o.Item2.Left - b.Item2.Right, b.Item2.Left - o.Item2.Right);
                    if (overlap > 2 && gap < 4) Note(report, seen, where, "PULSANTI", Describe(b.Item1) + "  <->  " + Describe(o.Item1) + "  (" + gap.ToString("0") + " px)");
                }
            }
        }

        private static bool IsOpaque(Brush brush)
        {
            var solid = brush as SolidColorBrush;
            return solid != null && solid.Color.A > 0;
        }

        private static System.Windows.Controls.TextBlock FindVisibleText(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var fe = child as FrameworkElement;
                if (fe != null && !fe.IsVisible) continue;
                var text = child as System.Windows.Controls.TextBlock;
                if (text != null && !string.IsNullOrWhiteSpace(text.Text)) return text;
                var found = FindVisibleText(child);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Il rettangolo del testo davvero disegnato, nelle coordinate del TextBlock.</summary>
        private static Rect TextExtent(System.Windows.Controls.TextBlock t, Window window)
        {
            var formatted = new FormattedText(t.Text, System.Globalization.CultureInfo.CurrentCulture, t.FlowDirection,
                new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch), t.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(window).PixelsPerDip);
            if (t.TextWrapping != TextWrapping.NoWrap) formatted.MaxTextWidth = Math.Max(1, t.ActualWidth);
            double width = Math.Min(formatted.Width, t.ActualWidth);
            double x = t.TextAlignment == TextAlignment.Right ? t.ActualWidth - width : t.TextAlignment == TextAlignment.Center ? (t.ActualWidth - width) / 2 : 0;
            return new Rect(x, 0, width, t.ActualHeight);
        }

        /// <summary>Porta il rettangolo in coordinate della finestra, tagliato da ciò che lo contiene. Falso se non si vede (scorrimento).</summary>
        private static bool VisibleRect(FrameworkElement fe, FrameworkElement root, Rect local, out Rect result)
        {
            result = fe.TransformToAncestor(root).TransformBounds(local);
            for (DependencyObject d = VisualTreeHelper.GetParent(fe); d != null && d != root; d = VisualTreeHelper.GetParent(d))
            {
                var sv = d as System.Windows.Controls.ScrollViewer;
                if (sv == null) continue;
                // la zona visibile è quella del contenuto: uno ScrollViewer può avere un margine interno
                var presenter = FindChild<System.Windows.Controls.ScrollContentPresenter>(sv);
                FrameworkElement area = presenter != null ? (FrameworkElement)presenter : sv;
                result.Intersect(area.TransformToAncestor(root).TransformBounds(new Rect(0, 0, area.ActualWidth, area.ActualHeight)));
                if (result.IsEmpty || result.Height < 2 || result.Width < 2) return false;
            }
            return true;
        }

        private static void Note(System.Text.StringBuilder report, System.Collections.Generic.HashSet<string> seen, string where, string kind, string what)
        {
            string line = where + " | " + kind + " | " + what;
            if (seen.Add(line)) report.AppendLine(line);
        }

        private static string Describe(FrameworkElement fe)
        {
            var text = fe as System.Windows.Controls.TextBlock;
            if (text != null) return "\"" + (text.Text.Length > 60 ? text.Text.Substring(0, 60) + "…" : text.Text) + "\"";
            var inner = FindChild<System.Windows.Controls.TextBlock>(fe);
            return "[" + fe.GetType().Name + ": " + (inner != null ? inner.Text : "") + "]";
        }

        private static bool IsInsideListItem(DependencyObject d)
        {
            for (; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is System.Windows.Controls.ListBoxItem) return true;
            return false;
        }

        private static bool IsTrimmed(System.Windows.Controls.TextBlock t, Window window)
        {
            if (t.TextTrimming == TextTrimming.None || t.TextWrapping != TextWrapping.NoWrap) return false;
            var formatted = new FormattedText(t.Text, System.Globalization.CultureInfo.CurrentCulture, t.FlowDirection,
                new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch), t.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(window).PixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace > t.ActualWidth + 1;
        }

        /// <summary>Testo su una riga senza puntini che non ci sta: viene tagliato di netto dal contenitore.</summary>
        private static bool IsClipped(System.Windows.Controls.TextBlock t, FrameworkElement root, Window window)
        {
            if (t.TextWrapping != TextWrapping.NoWrap || t.TextTrimming != TextTrimming.None) return false;
            var formatted = new FormattedText(t.Text, System.Globalization.CultureInfo.CurrentCulture, t.FlowDirection,
                new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch), t.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(window).PixelsPerDip);
            double natural = formatted.Width;
            var clip = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(t);
            if (clip != null && clip.Bounds.Width + 1 < natural) return true;
            Rect visible;
            return VisibleRect(t, root, new Rect(0, 0, natural, t.ActualHeight), out visible) && visible.Width + 1 < natural;
        }

        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var found = child as T ?? FindChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Solo nelle immagini (--anonymize): nome utente e nome del PC diventano generici, così le immagini si possono pubblicare.
        /// Tocca i testi già disegnati, non i dati.
        /// </summary>
        private static void Anonymize(DependencyObject root)
        {
            string user = Environment.UserName, pc = Environment.MachineName;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var t = child as System.Windows.Controls.TextBlock;
                if (t != null && !string.IsNullOrEmpty(t.Text))
                {
                    string s = System.Text.RegularExpressions.Regex.Replace(t.Text, @"(\\Users\\)" + System.Text.RegularExpressions.Regex.Escape(user) + @"(?=\\|$)", "$1User", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    s = System.Text.RegularExpressions.Regex.Replace(s, @"\b" + System.Text.RegularExpressions.Regex.Escape(pc) + @"\b", "MY-PC", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (s != t.Text) t.Text = s;
                }
                Anonymize(child);
            }
        }

        private static void Capture(Window window, string file)
        {
            if (arguments.Contains("--anonymize"))
            {
                Anonymize(window);
                window.UpdateLayout();
            }
            var content = (FrameworkElement)window.Content;
            var dpi = VisualTreeHelper.GetDpi(window);
            var rtb = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rtb.Render(content);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(file)) enc.Save(fs);
        }
    }
}
