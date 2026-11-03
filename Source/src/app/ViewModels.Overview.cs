using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AppManager
{
    // ====================================================================== Pagina "Panoramica"

    public sealed class OverviewPageViewModel : ObservableObject
    {
        private string programsText, programsDetail, gamesText, gamesDetail, backupsText, backupsDetail, resourcesText, resourcesDetail;
        private bool comparedApps, isLoadingDisks = true;
        private int refreshGen;

        public OverviewPageViewModel()
        {
            uptimeTimer.Tick += (s, e) => OnUptimeTick();
            sizesTimer.Tick += (s, e) =>
            {
                sizesTimer.Stop();
                if (MainViewModel.Current != null) UpdateLibraryTiles(MainViewModel.Current);
            };
            Disks = new ObservableCollection<DiskViewModel>();
            TopApps = new ObservableCollection<TopAppViewModel>();
            Added = new ObservableCollection<string>();
            Removed = new ObservableCollection<string>();
            GoCommand = new RelayCommand(p => MainViewModel.Current.Navigate(p as string));
            ExportCommand = new RelayCommand(Export, () => MainViewModel.Current != null && MainViewModel.Current.Programs.HasLoaded);
            DismissChangesCommand = new RelayCommand(() => { Added.Clear(); Removed.Clear(); RaiseChanges(); });
        }

        public ObservableCollection<DiskViewModel> Disks { get; private set; }
        public ObservableCollection<TopAppViewModel> TopApps { get; private set; }
        public ObservableCollection<string> Added { get; private set; }
        public ObservableCollection<string> Removed { get; private set; }
        public ICommand GoCommand { get; private set; }
        public ICommand ExportCommand { get; private set; }
        public ICommand DismissChangesCommand { get; private set; }

        public string PcName { get { return ThisPc.Name; } }
        public string PcAge { get { return ThisPc.WindowsAgeText; } }

        // ------------------------------------------------------------------ Tempo di accensione

        private readonly System.Windows.Threading.DispatcherTimer uptimeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        private DateTime? powerOn;
        private DateTime lastTick;
        private int rereads;
        private string pcUptime;

        /// <summary>"Acceso da 1 ora, 16 minuti"; null finché la lettura non è finita.</summary>
        public string PcUptime { get { return pcUptime; } private set { Set(ref pcUptime, value); } }

        /// <summary>L'orologio gira solo mentre la pagina è aperta.</summary>
        internal void SetActive(bool active)
        {
            if (!active) { uptimeTimer.Stop(); return; }
            lastTick = DateTime.Now;
            ReadPowerOn();
            uptimeTimer.Start();
        }

        private void OnUptimeTick()
        {
            // un salto dell'orologio vuol dire che il PC è stato sospeso: si rilegge, due volte perché Windows annota la ripresa con qualche secondo di ritardo
            if ((DateTime.Now - lastTick).TotalSeconds > 90) rereads = 2;
            lastTick = DateTime.Now;
            if (rereads > 0) { rereads--; ReadPowerOn(); }
            else ShowUptime();
        }

        private void ReadPowerOn()
        {
            Task.Run(() => ThisPc.LastPowerOn()).ContinueWith(t =>
            {
                if (t.IsFaulted) return;
                powerOn = t.Result;
                ShowUptime();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void ShowUptime()
        {
            if (powerOn.HasValue) PcUptime = L.F("Acceso da {0}", ThisPc.Uptime(DateTime.Now - powerOn.Value));
        }

        public string ProgramsText { get { return programsText ?? "…"; } private set { Set(ref programsText, value); } }
        public string ProgramsDetail { get { return programsDetail; } private set { Set(ref programsDetail, value); } }
        public string GamesText { get { return gamesText ?? "…"; } private set { Set(ref gamesText, value); } }
        public string GamesDetail { get { return gamesDetail; } private set { Set(ref gamesDetail, value); } }
        public string BackupsText { get { return backupsText ?? "…"; } private set { Set(ref backupsText, value); } }
        public string BackupsDetail { get { return backupsDetail; } private set { Set(ref backupsDetail, value); } }
        public string ResourcesText { get { return resourcesText ?? "…"; } private set { Set(ref resourcesText, value); } }
        public string ResourcesDetail { get { return resourcesDetail; } private set { Set(ref resourcesDetail, value); } }

        public bool HasChanges { get { return Added.Count > 0 || Removed.Count > 0; } }
        public bool HasAdded { get { return Added.Count > 0; } }
        public bool HasRemoved { get { return Removed.Count > 0; } }
        public bool HasTopApps { get { return TopApps.Count > 0; } }

        /// <summary>Lettura dei dischi in corso e niente ancora da mostrare: un aggiornamento non fa sparire i dischi già visibili.</summary>
        public bool IsLoadingDisks { get { return isLoadingDisks; } private set { Set(ref isLoadingDisks, value); } }

        /// <summary>Giochi e programmi non ancora pronti: al posto della classifica si vede il caricamento.</summary>
        public bool IsLoadingTopApps
        {
            get
            {
                var main = MainViewModel.Current;
                return TopApps.Count == 0 && (main == null || main.Games.IsLoading || main.Programs.IsLoading);
            }
        }

        public bool ShowTopSection { get { return HasTopApps || IsLoadingTopApps; } }
        public string AddedTitle { get { return L.F("Installati dall'ultimo avvio ({0})", Added.Count); } }
        public string RemovedTitle { get { return L.F("Non più presenti ({0})", Removed.Count); } }

        private void RaiseChanges()
        {
            Raise("HasChanges", "HasAdded", "HasRemoved", "AddedTitle", "RemovedTitle");
        }

        /// <summary>Giochi e programmi sono pronti: si confrontano (una volta sola) con quelli visti all'ultimo avvio.</summary>
        internal void OnLibrariesLoaded()
        {
            var main = MainViewModel.Current;
            if (main == null || main.Games.IsLoading || main.Programs.IsLoading) return;
            if (!comparedApps && main.Games.DetectionSucceeded && main.Programs.DetectionSucceeded)
            {
                comparedApps = true;
                var current = new Dictionary<string, string>();
                foreach (var e in main.AllDetected().Where(x => !x.IsComponent && !string.IsNullOrEmpty(x.Id)))
                    current[e.Id] = e.Name;
                List<string> added, removed;
                ThisPc.CompareApps(current, out added, out removed);
                foreach (var a in added.Take(12)) Added.Add(a);
                foreach (var r in removed.Take(12)) Removed.Add(r);
                RaiseChanges();
            }
            Refresh();
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>Rilegge i numeri della pagina. Dischi e cartella dati si misurano in background: un disco lento non deve bloccare la finestra.</summary>
        internal void Refresh()
        {
            var main = MainViewModel.Current;
            if (main == null) return;
            int gen = ++refreshGen;
            UpdateLibraryTiles(main);
            IsLoadingDisks = Disks.Count == 0;
            Task.Run(() =>
            {
                var disks = ReadDisks();
                long dataSize = PathUtil.Size(AppPaths.DataDir);
                int coverFiles;
                long coverBytes;
                CoverService.CacheStats(out coverFiles, out coverBytes);
                var dispatcher = Application.Current == null ? null : Application.Current.Dispatcher;
                if (dispatcher == null) return;
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != refreshGen) return;
                    Disks.Clear();
                    foreach (var d in disks) Disks.Add(d);
                    IsLoadingDisks = false;
                    ResourcesText = Fmt.Size(dataSize);
                    ResourcesDetail = coverBytes > 0 ? L.F("immagini: {0}", Fmt.Size(coverBytes)) : null;
                }));
            });
            BackupCatalog.GetAsync().ContinueWith(t =>
            {
                if (t.IsFaulted) return;
                var list = t.Result;
                var dispatcher = Application.Current == null ? null : Application.Current.Dispatcher;
                if (dispatcher == null) return;
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != refreshGen) return;
                    BackupsText = list.Count.ToString(L.Culture);
                    BackupsDetail = list.Count == 0 ? L.T("nessun backup") : Fmt.Size(list.Sum(b => b.Size));
                }));
            });
        }

        private readonly System.Windows.Threading.DispatcherTimer sizesTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        /// <summary>
        /// Una dimensione calcolata in background è arrivata: totali e classifica si aggiornano. Le dimensioni arrivano
        /// a raffica, quindi si ricalcola al massimo una volta al secondo, e solo con la pagina aperta (all'apertura si rilegge tutto).
        /// </summary>
        internal void OnSizesChanged()
        {
            if (uptimeTimer.IsEnabled && !sizesTimer.IsEnabled) sizesTimer.Start();
        }

        private void UpdateLibraryTiles(MainViewModel main)
        {
            if (main.Programs.HasLoaded && !main.Programs.IsLoading)
            {
                var programs = main.Programs.Items.Where(e => !e.Entry.IsComponent).ToList();
                ProgramsText = programs.Count.ToString(L.Culture);
                ProgramsDetail = Fmt.Size(programs.Where(e => e.SizeBytes > 0).Sum(e => e.SizeBytes));
            }
            if (main.Games.HasLoaded && !main.Games.IsLoading)
            {
                GamesText = main.Games.Items.Count.ToString(L.Culture);
                GamesDetail = Fmt.Size(main.Games.Items.Where(e => e.SizeBytes > 0).Sum(e => e.SizeBytes));
            }
            var top = main.Games.Items.Concat(main.Programs.Items).Where(e => !e.Entry.IsComponent && e.SizeBytes > 0)
                .OrderByDescending(e => e.SizeBytes).Take(7).ToList();
            long max = top.Count == 0 ? 1 : top[0].SizeBytes;
            TopApps.Clear();
            foreach (var e in top) TopApps.Add(new TopAppViewModel(e, (double)e.SizeBytes / max));
            Raise("HasTopApps", "IsLoadingTopApps", "ShowTopSection");
        }

        private static List<DiskViewModel> ReadDisks()
        {
            var list = new List<DiskViewModel>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if ((d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) || !d.IsReady || d.TotalSize <= 0) continue;
                    list.Add(new DiskViewModel(d.Name, d.VolumeLabel, d.TotalSize, d.TotalFreeSpace, d.DriveType == DriveType.Removable));
                }
                catch { /* unità che non risponde: non si mostra */ }
            }
            return list;
        }

        private void Export()
        {
            var main = MainViewModel.Current;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = L.T("Esporta l'elenco di giochi e programmi"),
                FileName = "AppManager - " + ThisPc.Name + " - " + DateTime.Now.ToString("yyyy-MM-dd") + ".csv",
                Filter = L.T("File CSV") + " (*.csv)|*.csv",
                DefaultExt = ".csv",
            };
            if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, AppExport.ToCsv(main.AllDetected()), new System.Text.UTF8Encoding(true));
                main.ShowToast(L.F("Elenco esportato: {0}", Path.GetFileName(dlg.FileName)));
            }
            catch (Exception ex)
            {
                Log.Error("Esportazione dell'elenco", ex);
                main.Message(L.T("Esportazione non riuscita"), ex.Message);
            }
        }
    }

    public sealed class DiskViewModel
    {
        internal DiskViewModel(string root, string label, long total, long free, bool removable)
        {
            string letter = root.TrimEnd('\\');
            Title = string.IsNullOrWhiteSpace(label) ? letter : label + " (" + letter + ")";
            Glyph = removable ? "" : "";
            Used = total <= 0 ? 0 : (double)(total - free) / total;
            Detail = L.F("{0} liberi su {1}", Fmt.Size(free), Fmt.Size(total));
            IsLow = Used >= 0.9;
        }

        public string Title { get; private set; }
        public string Glyph { get; private set; }
        public string Detail { get; private set; }
        public double Used { get; private set; }
        public bool IsLow { get; private set; }
    }

    public sealed class TopAppViewModel
    {
        internal TopAppViewModel(EntryViewModel e, double share)
        {
            Name = e.Name;
            SizeText = e.SizeText;
            Share = share;
            KindLabel = e.IsGame ? L.T("Gioco") : L.T("Programma");
            KindColor = e.IsGame ? "#8B7CFF" : "#5B8CFF";
            OpenCommand = new RelayCommand(() => MainViewModel.Current.ShowInLibrary(e));
        }

        public string Name { get; private set; }
        public string SizeText { get; private set; }
        public double Share { get; private set; }
        public string KindLabel { get; private set; }
        public string KindColor { get; private set; }
        public ICommand OpenCommand { get; private set; }
    }

    // ====================================================================== Pagina "Avvio automatico"

    public sealed class StartupPageViewModel : ObservableObject
    {
        private bool isLoading = true, hasLoaded;
        private string search = "", error;

        public StartupPageViewModel()
        {
            Items = new ObservableCollection<StartupItemViewModel>();
            View = System.Windows.Data.CollectionViewSource.GetDefaultView(Items);
            View.Filter = o =>
            {
                var i = (StartupItemViewModel)o;
                return search.Length == 0 || i.Name.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0
                    || (i.Command ?? "").IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0;
            };
            RefreshCommand = new RelayCommand(Load);
            OpenTaskManagerCommand = new RelayCommand(() => System.Diagnostics.Process.Start("taskmgr.exe", "/0 /startup"));
        }

        public ObservableCollection<StartupItemViewModel> Items { get; private set; }
        public System.ComponentModel.ICollectionView View { get; private set; }
        public ICommand RefreshCommand { get; private set; }
        public ICommand OpenTaskManagerCommand { get; private set; }

        public bool IsLoading { get { return isLoading; } private set { if (Set(ref isLoading, value)) Raise("IsEmpty", "IsFilteredEmpty", "ShowList"); } }
        public bool ShowList { get { return !isLoading && Items.Count > 0; } }
        public bool IsEmpty { get { return !isLoading && Items.Count == 0; } }
        public bool IsFilteredEmpty { get { return !isLoading && Items.Count > 0 && View.IsEmpty; } }
        public bool HasError { get { return error != null; } }
        public string Error { get { return error; } private set { if (Set(ref error, value)) Raise("HasError"); } }
        public bool ShowAdminHint { get { return !Native.IsElevated && Items.Any(i => i.RequiresAdmin); } }

        public string Summary
        {
            get
            {
                if (isLoading) return "";
                int on = Items.Count(i => i.IsEnabled);
                return Fmt.Count(Items.Count, "programma", "programmi") + " · " + Fmt.Count(on, "attivo", "attivi");
            }
        }

        public string SearchText
        {
            get { return search; }
            set { if (Set(ref search, value ?? "")) { View.Refresh(); Raise("IsFilteredEmpty"); } }
        }

        internal void OnShown()
        {
            if (!hasLoaded) Load();
        }

        internal void ItemChanged()
        {
            Raise("Summary");
        }

        private void Load()
        {
            hasLoaded = true;
            IsLoading = true;
            Error = null;
            Task.Run(() => StartupManager.List()).ContinueWith(t =>
            {
                Items.Clear();
                if (t.IsFaulted)
                {
                    Log.Error("Elenco dell'avvio automatico", t.Exception.GetBaseException());
                    Error = t.Exception.GetBaseException().Message;
                }
                else foreach (var e in t.Result) Items.Add(new StartupItemViewModel(e, this));
                IsLoading = false;
                Raise("Summary", "ShowAdminHint", "ShowList", "IsEmpty", "IsFilteredEmpty");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    public sealed class StartupItemViewModel : ObservableObject
    {
        private readonly StartupEntry entry;
        private readonly StartupPageViewModel page;
        private ImageSource icon;
        private bool iconRequested;
        private double iconLuminance = -1;

        internal StartupItemViewModel(StartupEntry e, StartupPageViewModel page)
        {
            entry = e;
            this.page = page;
            RevealCommand = new RelayCommand(() => ShellActions.Reveal(entry.Exe ?? entry.Command), () => entry.Exe != null);
        }

        public string Name { get { return entry.Name; } }
        public string Command { get { return entry.Command; } }
        public string SourceText { get { return L.T(entry.SourceText); } }
        public bool RequiresAdmin { get { return entry.RequiresAdmin; } }
        public bool TargetMissing { get { return entry.TargetMissing; } }
        public string Initials { get { return NameUtil.Initials(entry.Name); } }
        public ICommand RevealCommand { get; private set; }

        public ImageSource Icon
        {
            get
            {
                if (!iconRequested)
                {
                    iconRequested = true;
                    LoadIcon();
                }
                return icon;
            }
        }

        public bool HasIcon { get { return icon != null; } }
        public string IconBackdrop { get { return Images.BackdropFor(iconLuminance); } }

        private async void LoadIcon()
        {
            string exe = entry.Exe;
            if (exe == null || !File.Exists(exe)) return;
            try
            {
                var img = await Task.Run(() => Native.ExtractIcon(exe, 32));
                iconLuminance = await Task.Run(() => Images.VisibleLuminance(img));
                icon = img;
            }
            catch (Exception ex) { Log.Error(L.F("Icona di {0}", entry.Name), ex); }
            Raise("Icon", "HasIcon", "IconBackdrop");
        }

        /// <summary>Interruttore: scrive lo stato come Gestione attività. La voce resta, quindi si può sempre riattivare.</summary>
        public bool IsEnabled
        {
            get { return entry.IsEnabled; }
            set
            {
                if (value == entry.IsEnabled) return;
                if (entry.RequiresAdmin && !Native.IsElevated)
                {
                    MainViewModel.Current.RequireAdmin(L.T("cambiare l'avvio automatico per tutti gli utenti"));
                    Raise("IsEnabled");
                    return;
                }
                try
                {
                    StartupManager.SetEnabled(entry, value);
                    MainViewModel.Current.ShowToast(L.F(value ? "{0} partirà con Windows" : "{0} non partirà più con Windows", entry.Name));
                }
                catch (Exception ex)
                {
                    Log.Error(L.F("Avvio automatico di {0}", entry.Name), ex);
                    MainViewModel.Current.Message(L.T("Operazione non riuscita"), ex.Message);
                }
                Raise("IsEnabled");
                page.ItemChanged();
            }
        }
    }

    // ====================================================================== Un altro PC conosciuto (Impostazioni)

    public sealed class OtherPcViewModel
    {
        internal OtherPcViewModel(PcProfile p, SettingsPageViewModel page)
        {
            Name = string.IsNullOrEmpty(p.Name) ? L.T("PC senza nome") : p.Name;
            Detail = L.F("Ultimo avvio il {0}", Fmt.Date(p.LastSeenDate));
            ForgetCommand = new RelayCommand(() => page.ForgetPc(p.Id, Name));
        }

        public string Name { get; private set; }
        public string Detail { get; private set; }
        public ICommand ForgetCommand { get; private set; }
    }
}
