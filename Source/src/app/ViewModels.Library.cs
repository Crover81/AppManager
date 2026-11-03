using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace AppManager
{
    public sealed class FilterChip : ObservableObject
    {
        private bool isSelected;
        internal Action<FilterChip> Selected;
        public string Key { get; set; }
        public string Label { get; set; }
        public int Count { get; set; }
        public string Color { get; set; }

        public bool IsSelected
        {
            get { return isSelected; }
            set { if (Set(ref isSelected, value) && value && Selected != null) Selected(this); }
        }
    }

    // ====================================================================== Un gioco o programma nell'elenco

    public abstract class EntryViewModel : ObservableObject
    {
        internal readonly AppEntry Entry;

        internal EntryViewModel(AppEntry e)
        {
            Entry = e;
        }

        public string Name { get { return Entry.Name; } }
        public string Source { get { return Entry.Source; } }

        /// <summary>Gli altri metodi che hanno trovato lo stesso gioco o programma ("Trovato anche da: collegamento, …"), null se nessuno.</summary>
        public string AlsoFoundText
        {
            get
            {
                var others = Entry.FoundBy.Where(v => v != Entry.Via).OrderBy(v => (int)v).Select(ViaLabel).ToList();
                return others.Count == 0 ? null : L.F("Trovato anche da: {0}", string.Join(", ", others));
            }
        }

        private static string ViaLabel(FoundVia v)
        {
            switch (v)
            {
                case FoundVia.Launcher: return L.T("launcher");
                case FoundVia.Installed: return L.T("programmi installati");
                case FoundVia.Store: return "Microsoft Store";
                case FoundVia.Shortcut: return L.T("collegamento");
                case FoundVia.Folder: return L.T("cartella dei portable");
                default: return L.T("aggiunto a mano");
            }
        }
        public bool IsGame { get { return Entry.IsGame; } }

        /// <summary>Cartella (o eseguibile, per i programmi di un solo file).</summary>
        public string InstallDir { get { return Entry.IsSingleFile ? Entry.MainExe : Entry.InstallDir; } }
        public bool HasInstallDir { get { return !string.IsNullOrEmpty(InstallDir); } }
        public string Publisher { get { return string.IsNullOrWhiteSpace(Entry.Publisher) ? null : Entry.Publisher.Trim(); } }
        public string Version { get { return string.IsNullOrWhiteSpace(Entry.Version) ? null : Entry.Version.Trim(); } }
        public DateTime? InstallDate { get { return Entry.InstallDate; } }
        public string InstallDateText { get { return Entry.InstallDate.HasValue ? Fmt.Date(Entry.InstallDate) : null; } }
        public string Initials { get { return NameUtil.Initials(Entry.Name); } }
        public long SizeBytes { get { return Entry.SizeBytes; } }
        public string SizeText { get { return Entry.SizeBytes >= 0 ? Fmt.Size(Entry.SizeBytes) : "…"; } }
        public string LauncherLabel { get { return Launchers.Label(Entry.Launcher); } }
        public string LauncherColor { get { return Launchers.Color(Entry.Launcher); } }

        public string UninstallInfo
        {
            get
            {
                switch (Entry.Launcher)
                {
                    case LauncherKind.Steam: return L.T("Disinstallazione tramite Steam");
                    case LauncherKind.Epic: return L.T("Rimozione diretta dei file (Epic Games)");
                    case LauncherKind.Xbox: return L.T("Disinstallazione tramite Windows");
                    case LauncherKind.Store: return L.T("Disinstallazione tramite Windows (app di Microsoft Store)");
                }
                if (Entry.HasUninstaller)
                {
                    if ((Entry.UninstallString ?? "").IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0) return L.T("Disinstallazione con Windows Installer");
                    if ((Entry.UninstallString ?? "").StartsWith("winget", StringComparison.OrdinalIgnoreCase)) return L.T("Disinstallazione con winget (Gestione pacchetti di Windows)");
                    return L.T("Con il suo programma di disinstallazione");
                }
                if (Entry.IsSingleFile) return L.T("Programma portable: l'eseguibile viene eliminato");
                return Entry.IsGame ? L.T("Gioco portable: la cartella viene eliminata") : L.T("Programma portable: la cartella viene eliminata");
            }
        }

        // ------------------------------------------------------------------ Blocco dell'accesso a Internet (Windows Firewall)

        private bool? internetBlocked;
        private int netGen;
        private bool netBusy;

        /// <summary>C'è almeno un eseguibile da poter bloccare.</summary>
        public bool CanBlockInternet { get { return Entry.MainExe != null || (!Entry.IsSingleFile && !string.IsNullOrEmpty(Entry.InstallDir)); } }

        /// <summary>Interruttore "Blocca l'accesso a Internet": crea (o toglie) le regole di Windows Firewall per gli eseguibili.</summary>
        public bool IsInternetBlocked
        {
            get
            {
                if (internetBlocked == null)
                {
                    // la lettura del firewall (centinaia di regole) va fatta in background, o l'interfaccia si blocca un attimo
                    internetBlocked = false;
                    string dir = Entry.IsSingleFile ? null : Entry.InstallDir;
                    string exe = Entry.MainExe;
                    int gen = ++netGen;
                    Task.Run(() =>
                    {
                        bool b;
                        try { b = Firewall.AnyBlockedUnder(dir, exe); } catch { b = false; }
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            // ignora il risultato se nel frattempo l'utente ha usato l'interruttore
                            if (gen == netGen && internetBlocked != b) { internetBlocked = b; Raise("IsInternetBlocked"); }
                        }));
                    });
                }
                return internetBlocked.Value;
            }
            set { ToggleInternet(value); }
        }

        private async void ToggleInternet(bool block)
        {
            if (block == IsInternetBlocked || netBusy) { Raise("IsInternetBlocked"); return; }
            netGen++; // una scelta dell'utente ha la precedenza su una lettura in background ancora in corso
            if (!Native.IsElevated)
            {
                MainViewModel.Current.RequireAdmin(L.T("bloccare l'accesso a Internet"));
                Raise("IsInternetBlocked");
                return;
            }
            // la ricerca degli eseguibili in un gioco da decine di GB bloccava la finestra: si fa in background
            netBusy = true;
            internetBlocked = block;
            Raise("IsInternetBlocked", "IsNetworkBusy");
            string name = Entry.Name, dir = Entry.IsSingleFile ? null : Entry.InstallDir, exe = Entry.MainExe;
            try
            {
                await Task.Run(() =>
                {
                    if (block)
                    {
                        var exes = NetworkExes().ToList();
                        if (exes.Count == 0) throw new InvalidOperationException(L.T("Non ho trovato eseguibili da bloccare nella cartella."));
                        Firewall.BlockPrograms(exes, name);
                    }
                    else Firewall.UnblockUnder(dir, exe);
                });
                MainViewModel.Current.ShowToast(block ? L.F("Internet bloccato per {0}", name) : L.F("Internet sbloccato per {0}", name));
            }
            catch (Exception ex)
            {
                Log.Error("Blocco rete " + name, ex);
                internetBlocked = null;
                MainViewModel.Current.Message(L.T("Operazione non riuscita"), ex.Message);
            }
            finally
            {
                netBusy = false;
                Raise("IsInternetBlocked", "IsNetworkBusy");
            }
        }

        /// <summary>Blocco o sblocco in corso: l'interruttore resta fermo finché non finisce.</summary>
        public bool IsNetworkBusy { get { return netBusy; } }

        // ------------------------------------------------------------------ Profilo di salvataggio in uso (solo giochi)

        private string saveProfileText;
        private bool saveProfileLoaded;

        /// <summary>"In uso: Mario · 3 profili" se il gioco ha profili di salvataggio, altrimenti null.</summary>
        public string SaveProfileText
        {
            get
            {
                if (!saveProfileLoaded && Entry.IsGame)
                {
                    saveProfileLoaded = true;
                    var e = Entry;
                    Task.Run(() =>
                    {
                        string text = null;
                        try
                        {
                            var st = SaveVault.Load(e);
                            if (st.Active != null && st.Profiles.Count > 1)
                                text = L.F("In uso: {0} · {1}", st.Active.Name, Fmt.Count(st.Profiles.Count, "profilo", "profili"));
                        }
                        catch { }
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (text == saveProfileText) return;
                            saveProfileText = text;
                            Raise("SaveProfileText", "HasSaveProfile");
                        }));
                    });
                }
                return saveProfileText;
            }
        }

        public bool HasSaveProfile { get { return SaveProfileText != null; } }

        internal void RefreshSaveProfile()
        {
            saveProfileLoaded = false;
            Raise("SaveProfileText", "HasSaveProfile");
        }

        // ------------------------------------------------------------------ Backup dell'applicazione

        private string backupText;
        private bool backupLoaded;

        /// <summary>"3 backup · ultimo il 28 set 2026" se ci sono backup di questo gioco o programma, altrimenti null.</summary>
        public string BackupText
        {
            get
            {
                if (!backupLoaded)
                {
                    backupLoaded = true;
                    var e = Entry;
                    BackupCatalog.GetAsync().ContinueWith(t =>
                    {
                        string text = null;
                        if (!t.IsFaulted)
                        {
                            var list = BackupCatalog.For(t.Result, e).ToList();
                            if (list.Count > 0)
                                text = L.F("{0} · ultimo il {1}", Fmt.Count(list.Count, "backup", "backup"), list.Max(b => b.Created).ToString("d MMM yyyy", Fmt.It));
                        }
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (text == backupText) return;
                            backupText = text;
                            Raise("BackupText", "HasBackups");
                        }));
                    });
                }
                return backupText;
            }
        }

        public bool HasBackups { get { return BackupText != null; } }

        internal void RefreshBackupText()
        {
            backupLoaded = false;
            Raise("BackupText", "HasBackups");
        }

        /// <summary>Gli eseguibili del gioco o programma (quello principale più quelli nella sua cartella), fino a un limite.</summary>
        internal IEnumerable<string> NetworkExes()
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> add = e =>
            {
                if (!string.IsNullOrEmpty(e) && e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && seen.Add(e)) result.Add(e);
            };
            add(Entry.MainExe);
            foreach (var d in Entry.Dirs)
            {
                foreach (var f in PathUtil.FilesNoLinks(d))
                {
                    if (result.Count >= 40) return result;
                    if (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) add(f);
                }
            }
            return result;
        }

        // ------------------------------------------------------------------ Avvio

        /// <summary>Cosa aprire per avviare il gioco o programma (null se non si sa come).</summary>
        internal string LaunchTarget
        {
            get
            {
                if (Entry.Launcher == LauncherKind.Steam && !string.IsNullOrEmpty(Entry.SteamAppId)) return "steam://rungameid/" + Entry.SteamAppId;
                if (Entry.Launcher == LauncherKind.Epic && !string.IsNullOrEmpty(Entry.EpicAppName)) return "com.epicgames.launcher://apps/" + Uri.EscapeDataString(Entry.EpicAppName) + "?action=launch&silent=true";
                if (!string.IsNullOrEmpty(Entry.PackageFamily))
                {
                    string id = PackageAppId();
                    return id == null ? null : "shell:AppsFolder\\" + Entry.PackageFamily + "!" + id;
                }
                return !string.IsNullOrEmpty(Entry.MainExe) && File.Exists(Entry.MainExe) ? Entry.MainExe : null;
            }
        }

        /// <summary>L'ID dell'applicazione dentro il pacchetto, dal suo manifesto.</summary>
        private string PackageAppId()
        {
            try
            {
                string manifest = string.IsNullOrEmpty(Entry.InstallDir) ? null : Path.Combine(Entry.InstallDir, "AppxManifest.xml");
                if (manifest == null || !File.Exists(manifest)) return null;
                var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(manifest), "<Application\\s[^>]*\\bId=\"([^\"]+)\"");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        public bool CanLaunch { get { return LaunchTarget != null; } }

        /// <summary>Immagine per le finestre di dialogo: copertina del gioco o icona del programma.</summary>
        public abstract ImageSource Thumbnail { get; }

        internal virtual void LoadDetails() { }

        /// <summary>Libera le immagini grandi del pannello: si ricaricano se l'elemento viene selezionato di nuovo.</summary>
        internal virtual void ReleaseDetails() { }

        internal void RaiseSize() { Raise("SizeBytes", "SizeText"); }
    }

    // ====================================================================== Pagina con l'elenco (base comune di Giochi e Programmi)

    public abstract class LibraryPageViewModel : ObservableObject
    {
        internal List<AppEntry> Detected = new List<AppEntry>();
        internal bool DetectionSucceeded;
        internal bool HasLoaded;
        private string searchText = "";
        protected string filterKey = "all";
        private string sortMode = "name";
        private EntryViewModel selected;
        private bool isLoading, loadAgain;
        private string loadingText;
        private string summary;
        private CancellationTokenSource sizeCts;

        // le immagini grandi restano in memoria solo per gli ultimi elementi guardati: scorrere tutta la libreria non deve riempire la RAM
        private const int RecentDetails = 4;
        private readonly List<EntryViewModel> recent = new List<EntryViewModel>();

        protected LibraryPageViewModel()
        {
            Items = new ObservableCollection<EntryViewModel>();
            Filters = new ObservableCollection<FilterChip>();
            View = CollectionViewSource.GetDefaultView(Items);
            View.Filter = o => Matches((EntryViewModel)o);
            ApplySort();
            RefreshCommand = new RelayCommand(() => { var t = LoadAsync(); });
            AddCommand = new RelayCommand(Add);
            UninstallCommand = new RelayCommand(p => OpenCleanup(p as EntryViewModel ?? selected, CleanupDialogViewModel.Mode.Uninstall), p => (p as EntryViewModel ?? selected) != null);
            AnalyzeCommand = new RelayCommand(p => OpenCleanup(p as EntryViewModel ?? selected, CleanupDialogViewModel.Mode.Analyze), p => (p as EntryViewModel ?? selected) != null);
            ShowBackupsCommand = new RelayCommand(p => MainViewModel.Current.ShowBackupsFor(p as EntryViewModel ?? selected), p => (p as EntryViewModel ?? selected) != null);
            OpenFolderCommand = new RelayCommand(p =>
            {
                var g = p as EntryViewModel ?? selected;
                if (g == null) return;
                if (g.Entry.IsSingleFile) ShellActions.Reveal(g.Entry.MainExe);
                else ShellActions.OpenFolder(g.InstallDir);
            }, p => { var g = p as EntryViewModel ?? selected; return g != null && g.HasInstallDir; });
            HideCommand = new RelayCommand(p => Hide(p as EntryViewModel ?? selected), p => (p as EntryViewModel ?? selected) != null);
            LaunchCommand = new RelayCommand(p => Launch(p as EntryViewModel ?? selected), p => { var g = p as EntryViewModel ?? selected; return g != null && g.CanLaunch; });
        }

        internal abstract AppKind Kind { get; }

        /// <summary>Rilevamento (eseguito in background).</summary>
        internal abstract List<AppEntry> Detect(Action<string> progress);

        internal abstract EntryViewModel CreateItem(AppEntry e);

        /// <summary>Filtri "a pillola" in base agli elementi caricati.</summary>
        internal abstract void BuildFilterChips();

        internal abstract bool MatchesFilter(EntryViewModel e);

        internal abstract string SummaryFor(IList<EntryViewModel> visible, long total);

        /// <summary>Chiamato dopo ogni caricamento (es. per aggiornare le pagine collegate).</summary>
        internal virtual void OnLoaded() { }

        public ObservableCollection<EntryViewModel> Items { get; private set; }
        public ICollectionView View { get; private set; }
        public ObservableCollection<FilterChip> Filters { get; private set; }

        public ICommand RefreshCommand { get; private set; }
        public ICommand AddCommand { get; private set; }
        public ICommand UninstallCommand { get; private set; }
        public ICommand AnalyzeCommand { get; private set; }
        public ICommand ShowBackupsCommand { get; private set; }
        public ICommand OpenFolderCommand { get; private set; }
        public ICommand HideCommand { get; private set; }
        public ICommand LaunchCommand { get; private set; }

        public string SearchText
        {
            get { return searchText; }
            set { if (Set(ref searchText, value ?? "")) { View.Refresh(); Raise("IsFilteredEmpty"); } }
        }

        public string SortMode
        {
            get { return sortMode; }
            set { if (Set(ref sortMode, value)) ApplySort(); }
        }

        public EntryViewModel Selected
        {
            get { return selected; }
            set
            {
                var previous = selected;
                if (Set(ref selected, value))
                {
                    Raise("HasSelection", "ShowDetails");
                    if (value != null) value.LoadDetails();
                    RememberRecent(previous, value);
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool HasSelection { get { return selected != null; } }

        private void RememberRecent(EntryViewModel previous, EntryViewModel current)
        {
            if (current != null) recent.Remove(current);
            if (previous != null && previous != current)
            {
                recent.Remove(previous);
                recent.Add(previous);
            }
            while (recent.Count > RecentDetails)
            {
                recent[0].ReleaseDetails();
                recent.RemoveAt(0);
            }
        }

        private void Launch(EntryViewModel g)
        {
            string target = g == null ? null : g.LaunchTarget;
            if (target == null) return;
            try
            {
                ShellActions.Launch(target);
                MainViewModel.Current.ShowToast(L.F("Avvio di {0}…", g.Name));
            }
            catch (Exception ex)
            {
                Log.Error(L.F("Avvio di {0}", g.Name), ex);
                MainViewModel.Current.Message(L.T("Avvio non riuscito"), ex.Message);
            }
        }

        /// <summary>
        /// Il pannello a destra si vede solo a caricamento finito: durante una ricarica sparisce (i dati sarebbero quelli
        /// vecchi) e la rotellina resta al centro, come all'avvio. Alla fine torna selezionato lo stesso elemento.
        /// </summary>
        public bool ShowDetails { get { return selected != null && !IsLoading; } }

        /// <summary>
        /// In caricamento anche prima della prima ricerca: all'avvio i programmi aspettano l'elenco dei giochi, e intanto la pagina
        /// non deve mostrare «Nessun programma trovato», i filtri o «Ordina per», ma la rotellina al centro.
        /// </summary>
        public bool IsLoading { get { return isLoading || !HasLoaded; } }

        private void SetLoading(bool value)
        {
            if (isLoading == value) return;
            isLoading = value;
            Raise("IsLoading", "IsEmpty", "IsFilteredEmpty", "ShowDetails");
        }

        public string LoadingText
        {
            get { return loadingText ?? (Kind == AppKind.Game ? L.T("Cerco i giochi installati…") : L.T("Cerco i programmi installati…")); }
            private set { Set(ref loadingText, value); }
        }
        public string Summary { get { return summary; } private set { Set(ref summary, value); } }
        public bool IsEmpty { get { return !IsLoading && Items.Count == 0; } }
        public bool IsFilteredEmpty { get { return !IsLoading && Items.Count > 0 && View.IsEmpty; } }

        private bool Matches(EntryViewModel e)
        {
            if (!MatchesFilter(e)) return false;
            if (searchText.Length == 0) return true;
            return e.Name.IndexOf(searchText, StringComparison.CurrentCultureIgnoreCase) >= 0
                || (e.InstallDir ?? "").IndexOf(searchText, StringComparison.CurrentCultureIgnoreCase) >= 0
                || (e.Publisher ?? "").IndexOf(searchText, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void ApplySort()
        {
            using (View.DeferRefresh())
            {
                View.SortDescriptions.Clear();
                if (sortMode == "size") View.SortDescriptions.Add(new SortDescription("SizeBytes", ListSortDirection.Descending));
                else if (sortMode == "date") View.SortDescriptions.Add(new SortDescription("InstallDate", ListSortDirection.Descending));
                View.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
            }
        }

        /// <summary>Riapplica filtri e riepilogo (es. dopo aver cambiato un'opzione di visualizzazione).</summary>
        protected void RefreshView()
        {
            BuildFilterChips();
            View.Refresh();
            UpdateSummary();
            Raise("IsFilteredEmpty");
            if (selected == null || !Matches(selected)) Selected = View.Cast<EntryViewModel>().FirstOrDefault();
        }

        internal async Task LoadAsync()
        {
            // un aggiornamento richiesto mentre ne è in corso un altro viene ripetuto alla fine (i dati potrebbero essere cambiati)
            // il controllo usa lo stato vero: con IsLoading (vero anche prima della prima ricerca) la prima ricerca non partirebbe mai
            if (isLoading) { loadAgain = true; return; }
            SetLoading(true);
            string previous = selected != null ? selected.Entry.Id : null;
            try
            {
                do
                {
                    loadAgain = false;
                    LoadingText = Kind == AppKind.Game ? L.T("Cerco i giochi installati…") : L.T("Cerco i programmi installati…");
                    if (sizeCts != null) sizeCts.Cancel();
                    var progress = new Progress<string>(s => LoadingText = s);
                    List<AppEntry> found;
                    try
                    {
                        found = await Task.Run(() => Detect(((IProgress<string>)progress).Report));
                        DetectionSucceeded = true;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Rilevamento " + (Kind == AppKind.Game ? "giochi" : "programmi"), ex);
                        found = new List<AppEntry>();
                        DetectionSucceeded = false;
                    }
                    Detected = found;
                    var hidden = new HashSet<string>(AppSettings.Current.Hidden(Kind).Select(h => h.Path), StringComparer.OrdinalIgnoreCase);
                    Items.Clear();
                    foreach (var e in found.Where(x => !hidden.Contains(x.Id))) Items.Add(CreateItem(e));
                    BuildFilterChips();
                    HasLoaded = true;
                } while (loadAgain);
            }
            finally
            {
                HasLoaded = true;   // anche se qualcosa è andato storto: meglio la pagina vuota che la rotellina per sempre
                SetLoading(false);
            }
            UpdateSummary();
            Selected = Items.FirstOrDefault(g => g.Entry.Id == previous) ?? View.Cast<EntryViewModel>().FirstOrDefault();
            Raise("IsEmpty", "IsFilteredEmpty");
            ComputeSizesInBackground();
            OnLoaded();
            if (MainViewModel.Current != null) MainViewModel.Current.Settings.RefreshSources();
        }

        protected void SetFilterChips(IEnumerable<FilterChip> chips)
        {
            string keep = filterKey;
            Filters.Clear();
            foreach (var f in chips) Filters.Add(f);
            foreach (var f in Filters) f.Selected = OnFilterSelected;
            var sel = Filters.FirstOrDefault(f => f.Key == keep) ?? Filters.FirstOrDefault();
            if (sel != null)
            {
                filterKey = sel.Key;
                sel.IsSelected = true;
            }
        }

        private void OnFilterSelected(FilterChip chip)
        {
            foreach (var f in Filters) if (f != chip) f.IsSelected = false;
            filterKey = chip.Key;
            View.Refresh();
            Raise("IsFilteredEmpty");
            UpdateSummary();
            if (selected == null || !Matches(selected)) Selected = View.Cast<EntryViewModel>().FirstOrDefault();
        }

        protected void UpdateSummary()
        {
            var visible = Items.Where(MatchesFilter).ToList();
            long total = visible.Where(g => g.SizeBytes > 0).Sum(g => g.SizeBytes);
            Summary = SummaryFor(visible, total);
        }

        /// <summary>Calcola in background la dimensione di chi non la dichiara (portable, repack, app dello Store...).</summary>
        private void ComputeSizesInBackground()
        {
            sizeCts = new CancellationTokenSource();
            var ct = sizeCts.Token;
            var pending = Items.Where(g => g.SizeBytes < 0 && g.HasInstallDir).ToList();
            if (pending.Count == 0) return;
            // una cartella che contiene quelle di altri (es. Steam con i giochi) si misura senza di loro
            var all = MainViewModel.Current == null ? new List<AppEntry>() : MainViewModel.Current.AllDetected();
            var excluded = pending.Where(g => g.Entry.ContainsOthers).ToDictionary(g => g.Entry.Id,
                g => all.Where(o => o != g.Entry).SelectMany(o => o.Dirs).Where(d => PathUtil.IsUnder(d, g.Entry.InstallDir)).ToList());
            Task.Run(() =>
            {
                foreach (var g in pending)
                {
                    if (ct.IsCancellationRequested) return;
                    List<string> skip;
                    long size = g.Entry.IsSingleFile ? PathUtil.Size(g.Entry.MainExe, ct)
                        : excluded.TryGetValue(g.Entry.Id, out skip) ? PathUtil.SizeExcluding(g.Entry.InstallDir, skip, ct)
                        : PathUtil.Size(g.Entry.InstallDir, ct);
                    var vm = g;
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        vm.Entry.SizeBytes = size;
                        vm.RaiseSize();
                        UpdateSummary();
                        if (MainViewModel.Current != null) MainViewModel.Current.Overview.OnSizesChanged();
                    }));
                }
            }, ct);
        }

        private void Add()
        {
            var owner = Application.Current.MainWindow;
            string noun = Kind == AppKind.Game ? L.T("gioco") : L.T("programma");
            string dir = Native.PickFolder(owner == null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(owner).Handle,
                L.F("Scegli la cartella del {0} (anche portable)", noun), null);
            if (dir == null) return;
            if (!Safety.IsDeletableDirectory(dir) || !InstalledApps.IsSpecificDir(dir))
            {
                MainViewModel.Current.Message(L.T("Cartella non valida"), L.F("Questa cartella è di sistema o contiene altri giochi e programmi. Scegli la cartella di un solo {0}.", noun, noun));
                return;
            }
            var s = AppSettings.Current;
            var list = s.Manual(Kind);
            if (!list.Any(m => PathUtil.Same(m.Path, dir)))
            {
                list.Add(new SavedEntry { Name = NameUtil.CleanDisplayName(Path.GetFileName(dir)), Path = dir });
                s.Save();
            }
            MainViewModel.Current.ShowToast(Kind == AppKind.Game ? L.F("Gioco aggiunto: {0}", Path.GetFileName(dir)) : L.F("Programma aggiunto: {0}", Path.GetFileName(dir)));
            var t = LoadAsync();
        }

        private void Hide(EntryViewModel g)
        {
            if (g == null) return;
            string noun = g.IsGame ? L.T("Il gioco") : L.T("Il programma");
            MainViewModel.Current.Confirm(L.F("Nascondere {0}?", g.Name),
                L.F("{0} non comparirà nell'elenco, ma non viene disinstallato. Puoi mostrarlo di nuovo dalle Impostazioni.", noun),
                L.T("Nascondi"), false, () =>
                {
                    var s = AppSettings.Current;
                    s.Hidden(Kind).Add(new SavedEntry { Name = g.Name, Path = g.Entry.Id });
                    s.Manual(Kind).RemoveAll(m => "manual:" + (m.Path ?? "").ToLowerInvariant() == g.Entry.Id);
                    s.Save();
                    Items.Remove(g);
                    BuildFilterChips();
                    UpdateSummary();
                    Selected = View.Cast<EntryViewModel>().FirstOrDefault();
                    MainViewModel.Current.Settings.Reload();
                });
        }

        private void OpenCleanup(EntryViewModel g, CleanupDialogViewModel.Mode mode)
        {
            if (g == null) return;
            var all = MainViewModel.Current.AllDetected();
            var vm = new CleanupDialogViewModel(g, mode, all);
            vm.Closed = changed =>
            {
                MainViewModel.Current.Dialog = null;
                if (changed && mode == CleanupDialogViewModel.Mode.Uninstall)
                {
                    var t = LoadAsync();
                }
            };
            MainViewModel.Current.Dialog = vm;
        }
    }
}
