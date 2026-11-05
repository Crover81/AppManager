using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AppManager
{
    // ====================================================================== Finestra principale

    public sealed class MainViewModel : ObservableObject
    {
        internal static MainViewModel Current { get; private set; }

        public GamesPageViewModel Games { get; private set; }
        public ProgramsPageViewModel Programs { get; private set; }
        public OrphansPageViewModel GameOrphans { get; private set; }
        public OrphansPageViewModel ProgramOrphans { get; private set; }
        public BackupsPageViewModel Backups { get; private set; }
        public SettingsPageViewModel Settings { get; private set; }
        public NetworkPageViewModel Network { get; private set; }
        public LogsPageViewModel Logs { get; private set; }
        public OverviewPageViewModel Overview { get; private set; }
        public StartupPageViewModel Startup { get; private set; }
        public MonitorPageViewModel Monitor { get; private set; }

        private object currentPage;
        private string currentKey = "overview";
        private object dialog;
        private string toast;
        private bool orphanScanRunning;
        private readonly DispatcherTimer toastTimer;

        public MainViewModel()
        {
            Current = this;
            // i programmi vengono rilevati dopo i giochi (li escludono): li avvia la pagina dei giochi al termine
            Programs = new ProgramsPageViewModel();
            GameOrphans = new OrphansPageViewModel(AppKind.Game);
            ProgramOrphans = new OrphansPageViewModel(AppKind.Program);
            Backups = new BackupsPageViewModel();
            Settings = new SettingsPageViewModel();
            Network = new NetworkPageViewModel();
            Logs = new LogsPageViewModel();
            Overview = new OverviewPageViewModel();
            Startup = new StartupPageViewModel();
            Monitor = new MonitorPageViewModel();
            Games = new GamesPageViewModel();
            currentPage = Overview;
            NavigateCommand = new RelayCommand(p => Navigate(p as string));
            RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
            ToggleThemeCommand = new RelayCommand(() => SetTheme(ThemeInfo.IsLight ? "dark" : "light"));
            toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            toastTimer.Tick += (s, e) => { toastTimer.Stop(); Toast = null; };
        }

        /// <summary>Da chiamare a finestra aperta: un PC mai visto viene salutato una volta.</summary>
        internal void OnWindowReady()
        {
            Overview.Refresh();
            Overview.SetActive(currentKey == "overview");
            if (ThisPc.IsNew) ShowToast(L.F("Nuovo PC riconosciuto: {0}. Le sue impostazioni partono da zero.", ThisPc.Name));
        }

        // ------------------------------------------------------------------ Aspetto

        public string ThemeGlyph { get { return ThemeInfo.IsLight ? "\uE708" : "\uE706"; } }
        public string ThemeTip { get { return ThemeInfo.IsLight ? L.T("Passa al tema scuro") : L.T("Passa al tema chiaro"); } }
        public ICommand ToggleThemeCommand { get; private set; }

        internal void SetTheme(string choice)
        {
            AppSettings.Current.Theme = choice;
            AppSettings.Current.Save();
            App.ApplyTheme();
            Raise("ThemeGlyph", "ThemeTip");
            Settings.RaiseTheme();
        }

        /// <summary>Il tema è cambiato da fuori (per esempio in Windows): si aggiornano i testi che ne dipendono.</summary>
        internal void ThemeChanged()
        {
            Raise("ThemeGlyph", "ThemeTip");
        }

        /// <summary>Riavvia il programma (cambio di lingua). Una pulizia o un ripristino in corso hanno la precedenza.</summary>
        internal void RestartApp()
        {
            string work = WorkInProgress;
            if (work != null)
            {
                ShowToast(work);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--restarted") { UseShellExecute = true });
                Application.Current.Shutdown();
            }
            catch (Exception ex) { Log.Error("Riavvio del programma", ex); }
        }

        /// <summary>Apre la libreria giusta con l'elemento selezionato (dalla Panoramica).</summary>
        internal void ShowInLibrary(EntryViewModel e)
        {
            if (e == null) return;
            var library = e.IsGame ? (LibraryPageViewModel)Games : Programs;
            Navigate(e.IsGame ? "games" : "programs");
            if (library.Items.Contains(e)) library.Selected = e;
        }

        /// <summary>Tutti i giochi e i programmi rilevati (servono a riconoscere ciò che appartiene ad altri).</summary>
        internal List<AppEntry> AllDetected()
        {
            return Games.Detected.Concat(Programs.Detected).ToList();
        }

        /// <summary>
        /// Una sola analisi dei residui per entrambe le sezioni: ogni gruppo va nella pagina dei giochi
        /// o in quella dei programmi, così nessun elemento compare due volte.
        /// </summary>
        private bool librariesStale;

        /// <summary>
        /// La sorveglianza ha visto installare o togliere qualcosa: la libreria si rilegge, perché i residui si riconoscono per esclusione
        /// e con un elenco vecchio i dati di un programma appena installato sembrerebbero residui. Con un dialogo aperto si rimanda.
        /// </summary>
        internal void SystemChanged()
        {
            librariesStale = true;
            if (HasDialog || Games.IsLoading || Programs.IsLoading) return;
            librariesStale = false;
            Games.RefreshCommand.Execute(null);
        }

        internal async Task ScanOrphansAsync()
        {
            if (orphanScanRunning) return;
            orphanScanRunning = true;
            if (librariesStale && !Games.IsLoading)
            {
                librariesStale = false;
                Games.RefreshCommand.Execute(null);
                await Task.Delay(300);
            }
            GameOrphans.BeginScan();
            ProgramOrphans.BeginScan();
            Action<string> report = s => { GameOrphans.ProgressText = s; ProgramOrphans.ProgressText = s; };
            try
            {
                report(L.T("Attendo l'elenco dei giochi e dei programmi installati…"));
                // i residui si riconoscono per esclusione: servono gli elenchi completi
                while (Games.IsLoading || Programs.IsLoading || !Games.HasLoaded || !Programs.HasLoaded) await Task.Delay(200);
                if (!Games.DetectionSucceeded || !Programs.DetectionSucceeded)
                {
                    GameOrphans.EndScan();
                    ProgramOrphans.EndScan();
                    Message(L.T("Analisi non disponibile"),
                        L.T("Ricerca dei giochi o dei programmi non riuscita: senza quell'elenco i residui non si possono riconoscere. Premi Aggiorna nella pagina Giochi o Programmi e riprova."));
                    return;
                }
                var games = Games.Detected;
                var programs = Programs.Detected;
                var prog = new Progress<string>(report);
                var items = await Task.Run(() => OrphanScanner.Scan(games, programs, ((IProgress<string>)prog).Report, CancellationToken.None));
                await GameOrphans.ShowResultsAsync(items.Where(i => i.GroupKind == AppKind.Game).ToList());
                await ProgramOrphans.ShowResultsAsync(items.Where(i => i.GroupKind == AppKind.Program).ToList());
            }
            catch (Exception ex)
            {
                Log.Error("Ricerca residui orfani", ex);
                GameOrphans.EndScan();
                ProgramOrphans.EndScan();
                Message(L.T("Analisi non riuscita"), ex.Message);
            }
            finally
            {
                orphanScanRunning = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public object CurrentPage { get { return currentPage; } private set { Set(ref currentPage, value); } }
        public string CurrentKey { get { return currentKey; } set { Navigate(value); } }

        public object Dialog
        {
            get { return dialog; }
            set { if (Set(ref dialog, value)) Raise("HasDialog", "IsInteractive"); }
        }

        public bool HasDialog { get { return dialog != null; } }

        /// <summary>Falso con un dialogo aperto: pagina e barra laterale non devono ricevere clic né tasti (Tab, Spazio, scorciatoie).</summary>
        public bool IsInteractive { get { return dialog == null; } }
        public bool IsAdmin { get { return Native.IsElevated; } }
        public string Toast { get { return toast; } private set { Set(ref toast, value); } }

        public ICommand NavigateCommand { get; private set; }
        public ICommand RestartAsAdminCommand { get; private set; }

        internal void Navigate(string key)
        {
            if (key == null) return;
            if (key == "orphans") key = "game-orphans";
            switch (key)
            {
                case "overview": CurrentPage = Overview; Overview.Refresh(); break;
                case "startup": CurrentPage = Startup; Startup.OnShown(); break;
                case "monitor": CurrentPage = Monitor; Monitor.Reload(); break;
                case "games": CurrentPage = Games; break;
                case "game-orphans": CurrentPage = GameOrphans; GameOrphans.ApplyDeletionDefaults(); break;
                case "programs": CurrentPage = Programs; break;
                case "program-orphans": CurrentPage = ProgramOrphans; ProgramOrphans.ApplyDeletionDefaults(); break;
                case "network": CurrentPage = Network; break;
                case "backups": CurrentPage = Backups; Backups.Refresh(); break;
                case "logs": CurrentPage = Logs; Logs.OnShown(); break;
                case "settings": CurrentPage = Settings; Settings.Reload(); break;
                default: return;
            }
            // il monitor di rete aggiorna in tempo reale solo mentre la sua pagina è aperta
            Network.SetActive(key == "network");
            Overview.SetActive(key == "overview");
            Set(ref currentKey, key, "CurrentKey");
        }

        /// <summary>La finestra è stata nascosta nell'area di notifica: si fermano gli aggiornamenti in tempo reale.</summary>
        internal void Sleep()
        {
            Network.SetActive(false);
            Overview.SetActive(false);
        }

        /// <summary>La finestra è di nuovo visibile: la pagina riprende ad aggiornarsi.</summary>
        internal void Wake()
        {
            Navigate(currentKey);
        }

        /// <summary>Le scelte di eliminazione delle Impostazioni appena cambiate valgono subito anche nelle pagine Residui.</summary>
        internal void ApplyDeletionDefaults()
        {
            GameOrphans.ApplyDeletionDefaults();
            ProgramOrphans.ApplyDeletionDefaults();
        }

        internal void ShowToast(string message)
        {
            Toast = message;
            toastTimer.Stop();
            toastTimer.Start();
        }

        internal void Confirm(string title, string message, string confirmText, bool danger, Action onConfirm, string optionText = null, Action<bool> onConfirmWithOption = null)
        {
            // chiesta da un dialogo che deve restare aperto (es. Salvataggi): alla chiusura si torna lì
            var previous = dialog as IReturnableDialog;
            var vm = new ConfirmDialogViewModel
            {
                Title = title, Message = message, ConfirmText = confirmText, IsDanger = danger, OptionText = optionText,
            };
            vm.OnClose = ok =>
            {
                Dialog = previous;
                if (!ok) return;
                if (onConfirmWithOption != null) onConfirmWithOption(vm.OptionValue);
                else if (onConfirm != null) onConfirm();
            };
            Dialog = vm;
        }

        internal void Message(string title, string message, IEnumerable<string> details = null)
        {
            var previous = dialog as IReturnableDialog;
            var vm = new ConfirmDialogViewModel { Title = title, Message = message, ConfirmText = "OK", CancelText = null };
            if (details != null) foreach (var d in details) vm.Details.Add(d);
            vm.OnClose = ok => Dialog = previous;
            Dialog = vm;
        }

        /// <summary>Descrizione dell'operazione in corso che non conviene interrompere (null se non ce ne sono).</summary>
        internal string WorkInProgress
        {
            get
            {
                var c = dialog as CleanupDialogViewModel;
                if (c != null && c.IsCleaning) return L.T("È in corso una pulizia.");
                if (c != null && c.IsUninstalling) return L.T("È in corso una disinstallazione: al termine AppManager cercherà i residui.");
                if (dialog is BusyDialogViewModel) return L.T("È in corso un ripristino.");
                var s = dialog as SavesDialogViewModel;
                if (s != null && s.IsBusy) return L.F("È in corso un'operazione sui salvataggi ({0}).", s.ProgressText);
                return null;
            }
        }

        /// <summary>Apre la sezione Backup sull'applicazione indicata.</summary>
        internal void ShowBackupsFor(EntryViewModel e)
        {
            if (e == null) return;
            Backups.SelectWhenLoaded(e.Entry);
            Navigate("backups");
        }

        /// <summary>I backup sono cambiati: i pannelli delle librerie rileggono «N backup · ultimo il …».</summary>
        internal void RefreshBackupTexts()
        {
            Action refresh = () => { foreach (var e in Games.Items.Concat(Programs.Items)) e.RefreshBackupText(); };
            var d = Application.Current == null ? null : Application.Current.Dispatcher;
            if (d == null || d.CheckAccess()) refresh();
            else d.BeginInvoke(refresh);
        }

        /// <summary>Apre il gestore dei salvataggi di un gioco (backup, profili, nuova partita).</summary>
        internal void OpenSaves(EntryViewModel e)
        {
            if (e == null || !e.IsGame) return;
            var vm = new SavesDialogViewModel(e);
            vm.Closed = changed => { if (Dialog == vm) Dialog = null; };
            Dialog = vm;
        }

        /// <summary>Chiede all'utente se riavviare come amministratore per un'azione che lo richiede.</summary>
        internal void RequireAdmin(string action)
        {
            Confirm(L.T("Servono i permessi di amministratore"),
                L.F("Per {0} AppManager deve essere in esecuzione come amministratore. Vuoi riavviarlo adesso?", action),
                L.T("Riavvia come amministratore"), false, RestartAsAdmin);
        }

        private void RestartAsAdmin()
        {
            string work = WorkInProgress;
            if (work != null)
            {
                ShowToast(L.F("{0} Attendi che finisca, poi riavvia come amministratore.", work));
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--restarted") { UseShellExecute = true, Verb = "runas" });
                Application.Current.Shutdown();
            }
            catch (System.ComponentModel.Win32Exception) { /* UAC annullato */ }
        }
    }

    // ====================================================================== Finestre di dialogo semplici

    public sealed class ConfirmDialogViewModel : ObservableObject, IDialogKeys
    {
        private bool optionValue;

        public ConfirmDialogViewModel()
        {
            CancelText = L.T("Annulla");
            Details = new ObservableCollection<string>();
            ConfirmCommand = new RelayCommand(() => { if (OnClose != null) OnClose(true); });
            CancelCommand = new RelayCommand(() => { if (OnClose != null) OnClose(false); });
        }

        public string Title { get; set; }
        public string Message { get; set; }
        public string ConfirmText { get; set; }
        public string CancelText { get; set; }
        public bool IsDanger { get; set; }
        public string OptionText { get; set; }
        public bool OptionValue { get { return optionValue; } set { Set(ref optionValue, value); } }
        public ObservableCollection<string> Details { get; private set; }
        public ICommand ConfirmCommand { get; private set; }
        public ICommand CancelCommand { get; private set; }
        internal Action<bool> OnClose;

        bool IDialogKeys.OnEscape()
        {
            // un semplice messaggio (solo "OK") si chiude anche con Esc
            if (CancelText != null) CancelCommand.Execute(null);
            else ConfirmCommand.Execute(null);
            return true;
        }

        bool IDialogKeys.OnEnter()
        {
            if (IsDanger) return false;
            ConfirmCommand.Execute(null);
            return true;
        }
    }

    public sealed class BusyDialogViewModel : ObservableObject
    {
        private string text;
        private double progress;
        public string Title { get; set; }
        public string Text { get { return text; } set { Set(ref text, value); } }
        public double Progress { get { return progress; } set { Set(ref progress, value); } }
    }

    // ====================================================================== Elementi residui (condivisi da pulizia e orfani)

    public sealed class ItemViewModel : ObservableObject
    {
        internal readonly LeftoverItem Item;
        internal Action Changed;
        private bool isChecked;

        internal ItemViewModel(LeftoverItem item, bool check)
        {
            Item = item;
            isChecked = check;
            OpenCommand = new RelayCommand(Open);
        }

        public bool IsChecked
        {
            get { return isChecked; }
            set { if (Set(ref isChecked, value) && Changed != null) Changed(); }
        }

        internal void SetCheckedSilently(bool value)
        {
            if (isChecked == value) return;
            isChecked = value;
            Raise("IsChecked");
        }

        public string DisplayPath { get { return Item.DisplayPath; } }
        public string Reason { get { return Item.Reason; } }
        public string SizeText { get { return Item.Size > 0 ? Fmt.Size(Item.Size) : Item.Size == 0 ? L.T("vuota") : ""; } }

        public string Detail
        {
            get
            {
                var parts = new List<string>();
                if (Item.FileCount > 0) parts.Add(Fmt.Count(Item.FileCount, "file", "file"));
                if (Item.LastWrite.HasValue) parts.Add(L.F("modificato il {0}", Fmt.Date(Item.LastWrite)));
                if (Item.RequiresAdmin && !Native.IsElevated) parts.Add(L.T("richiede amministratore"));
                return string.Join(" · ", parts);
            }
        }

        /// <summary>Elementi che si possono togliere senza conseguenze: cache degli shader e file di log.</summary>
        private bool IsOptional { get { return Item.GroupKey == "x:shader" || (Item.GroupKey != null && Item.GroupKey.StartsWith("log:")); } }

        public string ConfidenceLabel
        {
            get
            {
                if (IsOptional) return L.T("Facoltativo");
                return Item.Confidence == Confidence.High ? L.T("Certo") : Item.Confidence == Confidence.Medium ? L.T("Probabile") : L.T("Da verificare");
            }
        }

        public string ConfidenceColor
        {
            get
            {
                if (IsOptional) return "#9AA4B2";
                return Item.Confidence == Confidence.High ? "#3DD68C" : Item.Confidence == Confidence.Medium ? "#5B8CFF" : "#F5B94B";
            }
        }

        public string Glyph
        {
            get
            {
                switch (Item.Kind)
                {
                    case ItemKind.Directory: return "";
                    case ItemKind.File: return Item.Category == ItemCategory.Shortcuts ? "" : "";
                    case ItemKind.FirewallRule: return "";
                    case ItemKind.ScheduledTask: return "";
                    case ItemKind.Service: return "";
                    case ItemKind.PathEntry: return "";
                    default: return Item.Category == ItemCategory.Startup ? "" : "";
                }
            }
        }

        public string OpenTip
        {
            get
            {
                switch (Item.Kind)
                {
                    case ItemKind.RegistryKey: case ItemKind.RegistryValue: return L.T("Apri nell'Editor del Registro");
                    case ItemKind.FirewallRule: return L.T("Apri Windows Firewall");
                    case ItemKind.ScheduledTask: return L.T("Apri l'Utilità di pianificazione");
                    case ItemKind.Service: return L.T("Apri Servizi");
                    case ItemKind.PathEntry: return L.T("Apri le variabili d'ambiente");
                    default: return L.T("Mostra in Esplora risorse");
                }
            }
        }

        public ICommand OpenCommand { get; private set; }

        private void Open()
        {
            switch (Item.Kind)
            {
                case ItemKind.RegistryKey: case ItemKind.RegistryValue: ShellActions.OpenRegistry(Item.Path); break;
                case ItemKind.FirewallRule: Process.Start("wf.msc"); break;
                case ItemKind.ScheduledTask: Process.Start("taskschd.msc"); break;
                case ItemKind.Service: Process.Start("services.msc"); break;
                case ItemKind.PathEntry: Process.Start("rundll32.exe", "sysdm.cpl,EditEnvironmentVariables"); break;
                default: ShellActions.Reveal(Item.Path); break;
            }
        }
    }

    /// <summary>Gruppo di elementi con casella a tre stati (categoria di un gioco o gioco orfano).</summary>
    public sealed class ItemGroupViewModel : ObservableObject
    {
        private bool isExpanded;
        private ImageSource image;
        internal Action Changed;
        internal string GroupKey;

        internal ItemGroupViewModel(string title, string glyph, IEnumerable<ItemViewModel> items)
        {
            this.title = title;
            Glyph = glyph;
            Items = new ObservableCollection<ItemViewModel>(items);
            foreach (var i in Items) i.Changed = OnItemChanged;
            ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
            OpenLinkCommand = new RelayCommand(() => { if (Link != null) ShellActions.OpenUrl(Link); });
        }

        private string title;

        public string Title { get { return title; } internal set { Set(ref title, value); } }
        public string Glyph { get; private set; }
        public string Source { get; set; }

        /// <summary>Sposta qui gli elementi di un altro gruppo (stesso gioco trovato per due strade).</summary>
        internal void Absorb(ItemGroupViewModel other)
        {
            foreach (var i in other.Items.ToList())
            {
                i.Changed = OnItemChanged;
                Items.Add(i);
            }
            other.Items.Clear();
            if (Image == null) Image = other.Image;
            Raise("CountText", "SizeText", "IsChecked");
            if (Changed != null) Changed();
        }
        private string link;
        public string Link { get { return link; } set { Set(ref link, value); } }
        public string LinkText { get; set; }
        public ObservableCollection<ItemViewModel> Items { get; private set; }
        public ICommand ToggleCommand { get; private set; }
        public ICommand OpenLinkCommand { get; private set; }

        // pagina Log: questo gruppo conta o no per l'avviso di log e cache della sorveglianza
        private string alertText;
        public string AlertText { get { return alertText; } private set { if (Set(ref alertText, value)) Raise("HasAlertToggle"); } }
        public bool HasAlertToggle { get { return alertText != null; } }
        public ICommand AlertCommand { get; private set; }

        internal void EnableAlertToggle(Action changed)
        {
            AlertCommand = new RelayCommand(() =>
            {
                CleanableCheck.SetCounts(GroupKey, !CleanableCheck.Counts(GroupKey));
                RefreshAlert();
                if (changed != null) changed();
            });
            RefreshAlert();
            Raise("AlertCommand");
        }

        /// <summary>Riallinea la scritta (anche quando cambia la categoria nelle Impostazioni).</summary>
        internal void RefreshAlert()
        {
            if (AlertCommand == null) return;
            AlertText = CleanableCheck.Counts(GroupKey) ? L.T("Escludi dall'avviso") : L.T("Escluso dall'avviso · includi");
        }

        public bool IsExpanded { get { return isExpanded; } set { Set(ref isExpanded, value); } }

        public ImageSource Image { get { return image; } set { if (Set(ref image, value)) Raise("HasImage"); } }
        public bool HasImage { get { return image != null; } }

        internal long TotalSize { get { return Items.Sum(i => Math.Max(0, i.Item.Size)); } }
        internal long CheckedSize { get { return Items.Where(i => i.IsChecked).Sum(i => Math.Max(0, i.Item.Size)); } }
        internal int CheckedCount { get { return Items.Count(i => i.IsChecked); } }

        public string SizeText { get { long s = TotalSize; return s > 0 ? Fmt.Size(s) : ""; } }

        public string CountText
        {
            get
            {
                int files = Items.Count(i => i.Item.IsFileSystem);
                int reg = Items.Count - files;
                var parts = new List<string>();
                if (files > 0) parts.Add(Fmt.Count(files, "file/cartella", "file e cartelle"));
                if (reg > 0) parts.Add(Fmt.Count(reg, "voce di sistema", "voci di sistema"));
                return string.Join(" · ", parts);
            }
        }

        public bool? IsChecked
        {
            get
            {
                int c = CheckedCount;
                return c == 0 ? false : c == Items.Count ? true : (bool?)null;
            }
            set
            {
                bool v = value ?? false;
                foreach (var i in Items) i.SetCheckedSilently(v);
                OnItemChanged();
            }
        }

        private void OnItemChanged()
        {
            Raise("IsChecked");
            if (Changed != null) Changed();
        }
    }
}
