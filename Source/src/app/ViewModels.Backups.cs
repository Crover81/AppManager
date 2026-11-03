using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AppManager
{
    // ====================================================================== Elenco dei backup condiviso (pagina Backup e pannelli delle librerie)

    /// <summary>
    /// Backup presenti nella cartella dei backup, letti una volta in background e tenuti finché qualcosa non li cambia
    /// (pulizia, ripristino, eliminazione): aprire decine di zip a ogni selezione rallenterebbe l'interfaccia.
    /// </summary>
    internal static class BackupCatalog
    {
        private static readonly object Sync = new object();
        private static Task<List<BackupInfo>> task;
        private static Task<Dictionary<string, ImageSource>> images;

        public static Task<List<BackupInfo>> GetAsync()
        {
            lock (Sync)
            {
                if (task == null)
                {
                    string folder = AppSettings.Current.BackupFolder;
                    task = Task.Run(() => Cleaner.ListBackups(folder));
                }
                return task;
            }
        }

        /// <summary>Immagine salvata nel backup più recente di ogni applicazione che ne ha una (per le app non più installate).</summary>
        public static Task<Dictionary<string, ImageSource>> GetImagesAsync()
        {
            lock (Sync)
            {
                if (images == null)
                {
                    var list = GetAsync();
                    images = list.ContinueWith(t =>
                    {
                        var imgs = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
                        if (t.IsFaulted) return imgs;
                        foreach (var b in t.Result.Where(x => x.HasImage).OrderByDescending(x => x.Created))
                            if (!imgs.ContainsKey(b.AppName ?? "")) imgs[b.AppName ?? ""] = Images.FromBytes(Cleaner.ReadImage(b.ZipPath), 240);
                        return imgs;
                    }, TaskScheduler.Default);
                }
                return images;
            }
        }

        /// <summary>True se i backup sono già stati letti (aprire la pagina non deve far lampeggiare il caricamento).</summary>
        public static bool IsReady
        {
            get
            {
                lock (Sync) return task != null && task.IsCompleted && images != null && images.IsCompleted;
            }
        }

        /// <summary>I backup sono cambiati: la prossima lettura li rilegge e i pannelli delle librerie si aggiornano.</summary>
        public static void Invalidate()
        {
            lock (Sync) { task = null; images = null; }
            if (MainViewModel.Current != null) MainViewModel.Current.RefreshBackupTexts();
        }

        /// <summary>Backup di un gioco o programma: stesso Id oppure, per quelli di prima, stesso nome.</summary>
        public static IEnumerable<BackupInfo> For(IEnumerable<BackupInfo> all, AppEntry e)
        {
            return all.Where(b => (!string.IsNullOrEmpty(b.AppId) && b.AppId == e.Id) || string.Equals(b.AppName, e.Name, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ====================================================================== Un backup

    public sealed class BackupViewModel : ObservableObject
    {
        internal readonly BackupInfo Info;
        private bool showContents;

        internal BackupViewModel(BackupInfo info, BackupsPageViewModel page)
        {
            Info = info;
            RestoreCommand = new RelayCommand(() => page.Restore(this));
            RevealCommand = new RelayCommand(() => ShellActions.Reveal(info.ZipPath));
            DeleteCommand = new RelayCommand(() => page.Delete(this));
            ToggleContentsCommand = new RelayCommand(() => ShowContents = !ShowContents);
        }

        public string Title { get { return Info.AppName ?? Info.Title; } }
        public string DateText { get { return Info.Created.ToString("dddd d MMMM yyyy, HH:mm", Fmt.It); } }
        public string ShortDateText { get { return Info.Created.ToString("d MMM yyyy, HH:mm", Fmt.It); } }
        public string SizeText { get { return Fmt.Size(Info.Size); } }
        public string ReasonLabel { get { return string.IsNullOrEmpty(Info.Reason) ? L.T("Backup") : L.T(Info.Reason); } }

        public string ReasonColor
        {
            get
            {
                switch (Info.Reason)
                {
                    case "Disinstallazione": return "#EF4F56";
                    case "Pulizia dei dati": return "#5B8CFF";
                    case "Salvataggi": return "#3DD68C";
                    case "Prima del ripristino": return "#F5B94B";
                    default: return "#9AA4B2";
                }
            }
        }

        public string CountText
        {
            get
            {
                int files = Info.ItemCount - Info.RegistryCount;
                var parts = new List<string>();
                if (files > 0) parts.Add(Fmt.Count(files, "file/cartella", "file e cartelle"));
                if (Info.RegistryCount > 0) parts.Add(Fmt.Count(Info.RegistryCount, "voce di sistema", "voci di sistema"));
                return parts.Count == 0 ? L.T("vuoto") : string.Join(" · ", parts);
            }
        }

        /// <summary>Dove tornerà ogni elemento (le prime 40 posizioni).</summary>
        public List<string> Contents
        {
            get
            {
                var list = Info.Paths.Take(40).ToList();
                if (Info.Paths.Count > 40) list.Add(L.F("…e altri {0} elementi", (Info.Paths.Count - 40)));
                return list;
            }
        }

        public bool ShowContents { get { return showContents; } set { Set(ref showContents, value); } }

        public ICommand RestoreCommand { get; private set; }
        public ICommand RevealCommand { get; private set; }
        public ICommand DeleteCommand { get; private set; }
        public ICommand ToggleContentsCommand { get; private set; }
    }

    // ====================================================================== Un'applicazione con i suoi backup

    public sealed class BackupAppViewModel : ObservableObject
    {
        private ImageSource image;

        internal BackupAppViewModel(string name, AppKind? kind, IEnumerable<BackupViewModel> backups, EntryViewModel library, BackupsPageViewModel page)
        {
            Name = name;
            Kind = kind ?? (library != null ? (AppKind?)library.Entry.Kind : null);
            Library = library;
            Backups = new ObservableCollection<BackupViewModel>(backups.OrderByDescending(b => b.Info.Created));
            OpenFolderCommand = new RelayCommand(OpenFolder);
            DeleteAllCommand = new RelayCommand(() => page.DeleteAll(this), () => Backups.Count > 0);
            SavesCommand = new RelayCommand(() => MainViewModel.Current.OpenSaves(Library), () => Library != null && Library.IsGame);
            ShowInLibraryCommand = new RelayCommand(ShowInLibrary, () => Library != null);
            OpenInstallDirCommand = new RelayCommand(() => { if (Library != null && Library.HasInstallDir) ShellActions.OpenFolder(Library.InstallDir); });
            // l'immagine della libreria arriva in background: appena è pronta sostituisce quella salvata nel backup
            if (library != null) library.PropertyChanged += OnLibraryChanged;
        }

        private void OnLibraryChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "Thumbnail") return;
            var img = Library.Thumbnail;
            if (img != null) Image = img;
        }

        /// <summary>Da chiamare quando l'elenco viene ricostruito: niente riferimenti rimasti agli elementi della libreria.</summary>
        internal void Detach()
        {
            if (Library != null) Library.PropertyChanged -= OnLibraryChanged;
        }

        public string Name { get; private set; }
        internal AppKind? Kind;
        /// <summary>Il gioco o programma nella libreria, se è ancora installato.</summary>
        internal readonly EntryViewModel Library;
        public ObservableCollection<BackupViewModel> Backups { get; private set; }

        public ImageSource Image { get { return image; } internal set { if (Set(ref image, value)) Raise("HasImage", "ShowPortrait", "ShowSquare"); } }
        public bool HasImage { get { return image != null; } }
        /// <summary>Copertina verticale; un'icona (anche quella di un gioco senza copertina) si mostra quadrata.</summary>
        public bool ShowPortrait { get { return Images.IsPortrait(image); } }
        public bool ShowSquare { get { return image != null && !ShowPortrait; } }
        public bool IsGame { get { return Kind == AppKind.Game; } }
        public bool IsProgram { get { return Kind != AppKind.Game; } }
        public string KindLabel { get { return Kind == AppKind.Game ? L.T("Gioco") : Kind == AppKind.Program ? L.T("Programma") : L.T("Applicazione"); } }
        public string KindColor { get { return Kind == AppKind.Game ? "#8B7CFF" : Kind == AppKind.Program ? "#5B8CFF" : "#9AA4B2"; } }
        public string Initials { get { return NameUtil.Initials(Name ?? ""); } }

        public bool IsInstalled { get { return Library != null; } }
        public bool CanManageSaves { get { return IsInstalled && IsGame; } }
        public bool CanShowInLibraryOnly { get { return IsInstalled && !IsGame; } }
        public string StatusLabel { get { return IsInstalled ? L.T("Installato") : L.T("Non più installato"); } }
        public string StatusColor { get { return IsInstalled ? "#3DD68C" : "#9AA4B2"; } }
        public string InstallDir { get { return Library == null ? null : Library.InstallDir; } }
        public string Publisher { get { return Library == null ? null : Library.Publisher; } }
        public string Version { get { return Library == null ? null : Library.Version; } }

        internal long TotalSize { get { return Backups.Sum(b => b.Info.Size); } }
        internal DateTime Latest { get { return Backups.Count == 0 ? DateTime.MinValue : Backups.Max(b => b.Info.Created); } }
        public string TotalSizeText { get { return Fmt.Size(TotalSize); } }
        public string CountText { get { return Fmt.Count(Backups.Count, "backup", "backup"); } }
        public string LatestText { get { return Backups.Count == 0 ? "" : Latest.ToString("d MMMM yyyy, HH:mm", Fmt.It); } }
        public string SummaryText { get { return Backups.Count == 0 ? "" : L.F("{0} · ultimo il {1}", CountText, Latest.ToString("d MMM yyyy", Fmt.It)); } }

        public ICommand OpenFolderCommand { get; private set; }
        public ICommand DeleteAllCommand { get; private set; }
        public ICommand SavesCommand { get; private set; }
        public ICommand ShowInLibraryCommand { get; private set; }
        public ICommand OpenInstallDirCommand { get; private set; }

        internal void Changed() { Raise("SummaryText", "CountText", "TotalSizeText", "LatestText"); }

        /// <summary>Cartella dei backup dell'applicazione (quella del backup più recente).</summary>
        private void OpenFolder()
        {
            var last = Backups.OrderByDescending(b => b.Info.Created).FirstOrDefault();
            if (last == null) return;
            string dir = Path.GetDirectoryName(last.Info.ZipPath);
            if (Directory.Exists(dir)) ShellActions.OpenFolder(dir);
        }

        private void ShowInLibrary()
        {
            var main = MainViewModel.Current;
            if (Library == null || main == null) return;
            LibraryPageViewModel page = Library.IsGame ? (LibraryPageViewModel)main.Games : main.Programs;
            main.Navigate(Library.IsGame ? "games" : "programs");
            page.Selected = Library;
        }
    }

    // ====================================================================== Pagina "Backup": un'applicazione per volta, come nelle librerie

    public sealed class BackupsPageViewModel : ObservableObject
    {
        private readonly List<BackupAppViewModel> all = new List<BackupAppViewModel>();
        private BackupAppViewModel selected;
        private bool isLoading;
        private string filter = "all", searchText = "", summary = "";
        private int loadVersion;
        private string pendingId, pendingName;

        public BackupsPageViewModel()
        {
            Apps = new ObservableCollection<BackupAppViewModel>();
            RefreshCommand = new RelayCommand(() => { BackupCatalog.Invalidate(); Refresh(); }, () => !isLoading);
            OpenFolderCommand = new RelayCommand(() =>
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    ShellActions.OpenFolder(Folder);
                }
                catch (Exception ex) { MainViewModel.Current.Message(L.T("Cartella non disponibile"), ex.Message); }
            });
        }

        public ObservableCollection<BackupAppViewModel> Apps { get; private set; }
        public ICommand RefreshCommand { get; private set; }
        public ICommand OpenFolderCommand { get; private set; }
        public string Folder { get { return AppSettings.Current.BackupFolder; } }

        public BackupAppViewModel Selected
        {
            get { return selected; }
            set { if (Set(ref selected, value)) Raise("HasSelection", "ShowDetails"); }
        }

        public bool HasSelection { get { return selected != null; } }

        /// <summary>Durante una ricarica il pannello a destra sparisce e la rotellina resta al centro, come all'avvio.</summary>
        public bool ShowDetails { get { return selected != null && !isLoading; } }
        public bool IsLoading { get { return isLoading; } private set { if (Set(ref isLoading, value)) Raise("IsEmpty", "HasApps", "HasNoMatch", "ShowDetails", "ShowFilters"); } }

        /// <summary>Filtri con i conteggi: solo con i risultati pronti (durante un caricamento i numeri sarebbero quelli vecchi).</summary>
        public bool ShowFilters { get { return !isLoading && all.Count > 0; } }
        public bool IsEmpty { get { return !isLoading && all.Count == 0; } }
        public bool HasApps { get { return !isLoading && Apps.Count > 0; } }
        public bool HasNoMatch { get { return !isLoading && all.Count > 0 && Apps.Count == 0; } }
        public string Summary { get { return summary; } private set { Set(ref summary, value); } }
        public int AllCount { get { return all.Count; } }
        public int GamesCount { get { return all.Count(a => a.Kind == AppKind.Game); } }
        public int ProgramsCount { get { return all.Count(a => a.Kind != AppKind.Game); } }

        /// <summary>Filtro: all, games, programs.</summary>
        public string Filter { get { return filter; } set { if (value != null && Set(ref filter, value)) ApplyFilter(); } }

        public string SearchText { get { return searchText; } set { if (Set(ref searchText, value ?? "")) ApplyFilter(); } }

        /// <summary>Apre la pagina sull'applicazione indicata (dal pannello di un gioco o programma).</summary>
        internal void SelectWhenLoaded(AppEntry e)
        {
            pendingId = e.Id;
            pendingName = e.Name;
            if (!isLoading) TrySelectPending();
        }

        private void TrySelectPending()
        {
            if (pendingId == null && pendingName == null) return;
            var app = all.FirstOrDefault(a => a.Library != null && a.Library.Entry.Id == pendingId)
                      ?? all.FirstOrDefault(a => string.Equals(a.Name, pendingName, StringComparison.OrdinalIgnoreCase));
            pendingId = pendingName = null;
            if (app == null) return;
            if (!Apps.Contains(app))
            {
                filter = "all";
                searchText = "";
                Raise("Filter", "SearchText");
                ApplyFilter();
            }
            Selected = app;
        }

        /// <summary>Rilegge i backup (in background) e li raggruppa per applicazione.</summary>
        internal async void Refresh()
        {
            int version = ++loadVersion;
            // backup già letti: niente caricamento (aprire la pagina farebbe sparire e ricomparire il pannello)
            if (!BackupCatalog.IsReady) IsLoading = true;
            var main = MainViewModel.Current;
            var library = main == null ? new List<EntryViewModel>() : main.Games.Items.Concat(main.Programs.Items).ToList();
            List<BackupInfo> infos;
            Dictionary<string, ImageSource> stored;
            try
            {
                infos = await BackupCatalog.GetAsync();
                stored = await BackupCatalog.GetImagesAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Elenco dei backup", ex);
                infos = new List<BackupInfo>();
                stored = new Dictionary<string, ImageSource>();
            }
            if (version != loadVersion) return;   // nel frattempo è partito un altro aggiornamento

            string keep = selected == null ? null : selected.Name;
            foreach (var old in all) old.Detach();
            all.Clear();
            foreach (var g in infos.GroupBy(b => (b.AppName ?? b.Title ?? "").Trim(), StringComparer.OrdinalIgnoreCase))
            {
                var kind = g.Select(b => b.AppKind).FirstOrDefault(k => k.HasValue);
                string id = g.Select(b => b.AppId).FirstOrDefault(x => !string.IsNullOrEmpty(x));
                string name = g.First().AppName ?? g.Key;
                var live = library.FirstOrDefault(e => id != null && e.Entry.Id == id)
                           ?? library.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) && (!kind.HasValue || e.Entry.Kind == kind.Value));
                var app = new BackupAppViewModel(name, kind, g.Select(b => new BackupViewModel(b, this)), live, this);
                // immagine: quella della libreria se l'app è ancora installata, altrimenti quella salvata nel backup
                ImageSource img = live != null ? live.Thumbnail : null;
                if (img == null) stored.TryGetValue(app.Name, out img);
                app.Image = img;
                all.Add(app);
            }
            all.Sort((a, b) => b.Latest.CompareTo(a.Latest));
            ApplyFilter();
            IsLoading = false;
            if (keep != null && selected == null) Selected = Apps.FirstOrDefault(a => string.Equals(a.Name, keep, StringComparison.OrdinalIgnoreCase));
            if (selected == null) Selected = Apps.FirstOrDefault();
            TrySelectPending();
            Raise("Folder", "AllCount", "GamesCount", "ProgramsCount");
            CommandManager.InvalidateRequerySuggested();
        }

        private void ApplyFilter()
        {
            string q = (searchText ?? "").Trim();
            var current = selected;
            Apps.Clear();
            foreach (var a in all)
            {
                if (filter == "games" && a.Kind != AppKind.Game) continue;
                if (filter == "programs" && a.Kind == AppKind.Game) continue;
                if (q.Length > 0 && a.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                Apps.Add(a);
            }
            // la selezione resta se l'applicazione è ancora nell'elenco filtrato, altrimenti passa alla prima
            Selected = current != null && Apps.Contains(current) ? current : Apps.FirstOrDefault();
            int count = all.Sum(a => a.Backups.Count);
            Summary = count == 0 ? "" : L.F("{0} di {1}", Fmt.Count(count, "backup", "backup"), Fmt.Count(all.Count, "applicazione", "applicazioni")) + " · " + Fmt.Size(all.Sum(a => a.TotalSize));
            Raise("IsEmpty", "HasApps", "HasNoMatch", "AllCount", "GamesCount", "ProgramsCount", "ShowFilters");
        }

        internal void Restore(BackupViewModel b)
        {
            int conflicts = 0;
            try { conflicts = Cleaner.CountConflicts(b.Info.ZipPath); } catch { }
            string msg = L.T("File e voci di sistema tornano nelle posizioni originali.");
            if (conflicts > 0) msg += "\n\n" + L.F("File già presenti sul PC: {0}.", conflicts);
            MainViewModel.Current.Confirm(L.F("Ripristinare il backup di «{0}»?", b.Title), L.F("{0} del {1}.", b.ReasonLabel, b.DateText) + "\n\n" + msg, L.T("Ripristina"), false, null,
                conflicts > 0 ? L.T("Sovrascrivi i file esistenti con quelli del backup") : null,
                overwrite =>
                {
                    var busy = new BusyDialogViewModel { Title = L.T("Ripristino in corso"), Text = L.T("Preparazione…") };
                    MainViewModel.Current.Dialog = busy;
                    var prog = new Progress<Tuple<string, double>>(t => { busy.Text = t.Item1; busy.Progress = t.Item2; });
                    Task.Run(() => Cleaner.Restore(b.Info.ZipPath, overwrite, (s, d) => ((IProgress<Tuple<string, double>>)prog).Report(Tuple.Create(s, d))))
                        .ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                            {
                                MainViewModel.Current.Message(L.T("Ripristino non riuscito"), t.Exception.GetBaseException().Message);
                                return;
                            }
                            var r = t.Result;
                            MainViewModel.Current.Message(r.Errors.Count == 0 ? L.T("Ripristino completato") : L.T("Ripristino completato con errori"),
                                L.F("Ripristinati {0} di «{1}».", Fmt.Count(r.Removed, "elemento", "elementi"), b.Title), r.Notes.Concat(r.Errors));
                        }, TaskScheduler.FromCurrentSynchronizationContext());
                });
        }

        internal void Delete(BackupViewModel b)
        {
            MainViewModel.Current.Confirm(L.F("Eliminare questo backup di «{0}»?", b.Title),
                L.F("{0} del {1} ({2}).\nIl file del backup verrà spostato nel Cestino.", b.ReasonLabel, b.DateText, b.SizeText), L.T("Elimina"), true, () =>
                {
                    try
                    {
                        Cleaner.DeleteBackup(b.Info.ZipPath, Folder);
                        Removed(new[] { b });
                        MainViewModel.Current.ShowToast(L.T("Backup spostato nel Cestino."));
                    }
                    catch (Exception ex) { MainViewModel.Current.Message(L.T("Impossibile eliminare il backup"), ex.Message); }
                });
        }

        internal void DeleteAll(BackupAppViewModel app)
        {
            var list = app.Backups.ToList();
            if (list.Count == 0) return;
            MainViewModel.Current.Confirm(L.F("Eliminare tutti i backup di «{0}»?", app.Name),
                L.F("{0} ({1}) verranno spostati nel Cestino. Non potrai più ripristinarli da AppManager.", Fmt.Count(list.Count, "backup", "backup"), app.TotalSizeText),
                L.T("Elimina tutti"), true, () =>
                {
                    var done = new List<BackupViewModel>();
                    var errors = new List<string>();
                    foreach (var b in list)
                    {
                        try { Cleaner.DeleteBackup(b.Info.ZipPath, Folder); done.Add(b); }
                        catch (Exception ex) { errors.Add(ex.Message + ": " + b.Info.ZipPath); }
                    }
                    Removed(done);
                    if (errors.Count > 0) MainViewModel.Current.Message(L.T("Alcuni backup non sono stati eliminati"), L.T("Controlla che non siano aperti in un altro programma."), errors);
                    else MainViewModel.Current.ShowToast(L.F("Backup di «{0}» spostati nel Cestino.", app.Name));
                });
        }

        private void Removed(IEnumerable<BackupViewModel> removed)
        {
            foreach (var b in removed.ToList())
            {
                var app = all.FirstOrDefault(a => a.Backups.Contains(b));
                if (app == null) continue;
                app.Backups.Remove(b);
                app.Changed();
                if (app.Backups.Count == 0) all.Remove(app);
            }
            ApplyFilter();
            BackupCatalog.Invalidate();
        }
    }
}
