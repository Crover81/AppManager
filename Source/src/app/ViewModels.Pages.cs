using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AppManager
{
    // ====================================================================== Pagine "Residui" (giochi e programmi)

    public sealed class OrphansPageViewModel : ObservableObject
    {
        internal readonly AppKind Kind;
        private bool isScanning, hasScanned, backupEnabled, useRecycleBin;
        private string progressText, summary, selectionText;
        private long recoverable;

        internal OrphansPageViewModel(AppKind kind)
        {
            Kind = kind;
            Groups = new ObservableCollection<ItemGroupViewModel>();
            backupEnabled = AppSettings.Current.BackupBeforeClean;
            useRecycleBin = AppSettings.Current.UseRecycleBin;
            ScanCommand = new RelayCommand(() => { var t = MainViewModel.Current.ScanOrphansAsync(); }, () => !isScanning);
            CleanCommand = new RelayCommand(Clean, () => !isScanning && Groups.Any(g => g.CheckedCount > 0));
            SelectAllCommand = new RelayCommand(() => { foreach (var g in Groups) g.IsChecked = true; });
            SelectNoneCommand = new RelayCommand(() => { foreach (var g in Groups) g.IsChecked = false; });
            ExpandAllCommand = new RelayCommand(() => { bool any = Groups.Any(g => !g.IsExpanded); foreach (var g in Groups) g.IsExpanded = any; });
        }

        private bool Games { get { return Kind == AppKind.Game; } }

        public string Title { get { return Games ? L.T("Residui di giochi disinstallati") : L.T("Residui di programmi disinstallati"); } }

        public string Subtitle
        {
            get
            {
                return Games ? L.T("Salvataggi, cartelle, voci di registro e cache lasciati da giochi non più presenti.")
                             : L.T("Cartelle, impostazioni e voci di sistema lasciate da programmi non più presenti.");
            }
        }

        public string IntroTitle { get { return Games ? L.T("Residui dei giochi già disinstallati") : L.T("Residui dei programmi già disinstallati"); } }

        public string IntroText
        {
            get
            {
                return Games
                    ? L.T("Cerca dati di Steam, cartelle di giochi Unity e Unreal, salvataggi, collegamenti non validi e cache degli shader di giochi eliminati. Niente viene eliminato senza conferma.")
                    : L.T("Cerca cartelle rimaste, voci di avvio automatico, attività, servizi, tipi di file, collegamenti e voci di «App installate» di programmi eliminati. Niente viene eliminato senza conferma.");
            }
        }

        public string IntroGlyph { get { return Games ? "" : ""; } }
        public string CleanText { get { return Games ? L.T("Nessuna traccia di giochi disinstallati.") : L.T("Nessuna traccia di programmi disinstallati."); } }

        public string AdminHint
        {
            get
            {
                return Games ? L.T("Come amministratore, l'analisi usa anche il Prefetch di Windows e trova più dati dei giochi eliminati.")
                             : L.T("Come amministratore, l'analisi usa anche il Prefetch di Windows e puoi eliminare servizi e voci di sistema.");
            }
        }

        public string SharedScanNote
        {
            get { return Games ? L.T("L'analisi aggiorna anche i residui dei programmi.") : L.T("L'analisi aggiorna anche i residui dei giochi."); }
        }

        public ObservableCollection<ItemGroupViewModel> Groups { get; private set; }
        public ICommand ScanCommand { get; private set; }
        public ICommand CleanCommand { get; private set; }
        public ICommand SelectAllCommand { get; private set; }
        public ICommand SelectNoneCommand { get; private set; }
        public ICommand ExpandAllCommand { get; private set; }

        public bool IsScanning { get { return isScanning; } private set { if (Set(ref isScanning, value)) Raise("ShowIntro", "HasResults", "IsClean"); } }
        public bool HasScanned { get { return hasScanned; } private set { if (Set(ref hasScanned, value)) Raise("ShowIntro", "HasResults", "IsClean"); } }
        public bool ShowIntro { get { return !hasScanned && !isScanning; } }
        public bool HasResults { get { return hasScanned && !isScanning && Groups.Count > 0; } }
        public bool IsClean { get { return hasScanned && !isScanning && Groups.Count == 0; } }
        public string ProgressText { get { return progressText; } internal set { Set(ref progressText, value); } }
        public string Summary { get { return summary; } private set { Set(ref summary, value); } }
        public string SelectionText { get { return selectionText; } private set { Set(ref selectionText, value); } }
        public bool BackupEnabled { get { return backupEnabled; } set { Set(ref backupEnabled, value); } }
        public bool UseRecycleBin { get { return useRecycleBin; } set { Set(ref useRecycleBin, value); } }
        public bool ShowAdminHint { get { return !Native.IsElevated; } }

        /// <summary>Caselle «Backup prima di eliminare» e «Usa il Cestino» come nelle Impostazioni.</summary>
        internal void ApplyDeletionDefaults()
        {
            BackupEnabled = AppSettings.Current.BackupBeforeClean;
            UseRecycleBin = AppSettings.Current.UseRecycleBin;
        }

        internal void BeginScan()
        {
            IsScanning = true;
            ProgressText = L.T("Preparazione…");
        }

        internal void EndScan()
        {
            IsScanning = false;
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>Mostra i risultati dell'analisi (già filtrati per questa sezione).</summary>
        internal async Task ShowResultsAsync(List<LeftoverItem> items)
        {
            Groups.Clear();
            var grouped = items.GroupBy(i => i.GroupKey)
                .OrderBy(g => g.Key.StartsWith("x:") ? 1 : 0)
                .ThenBy(g => g.All(i => !i.IsFileSystem) ? 1 : 0)
                .ThenByDescending(g => g.Sum(i => Math.Max(0, i.Size)));
            var images = new List<Tuple<ItemGroupViewModel, string>>();
            foreach (var grp in grouped)
            {
                var first = grp.First();
                var vm = new ItemGroupViewModel(L.T(first.GroupName), GlyphFor(first.GroupSource),
                    grp.OrderBy(i => Array.IndexOf(Categories.Order, i.Category)).Select(i => new ItemViewModel(i, i.Confidence >= Confidence.Medium)));
                vm.Source = L.T(first.GroupSource);
                vm.GroupKey = grp.Key;
                vm.Changed = UpdateSelection;
                if (grp.Key.StartsWith("s:") && first.GroupName.Contains(grp.Key.Substring(2)))
                {
                    vm.Link = "https://steamdb.info/app/" + grp.Key.Substring(2) + "/";
                    vm.LinkText = L.T("Che gioco è?");
                }
                Groups.Add(vm);
                if (first.GroupImage != null) images.Add(Tuple.Create(vm, first.GroupImage));
            }
            recoverable = items.Where(i => i.Confidence >= Confidence.Medium).Sum(i => Math.Max(0, i.Size));
            UpdateSummary();
            HasScanned = true;
            IsScanning = false;
            UpdateSelection();
            foreach (var t in images) t.Item1.Image = await Images.LoadAsync(t.Item2, 90);
            if (Games) { var e = EnrichOnlineAsync(); }
        }

        private void UpdateSummary()
        {
            int count = Groups.Count(g => g.GroupKey != null && !g.GroupKey.StartsWith("x:"));
            Summary = Groups.Count == 0 ? "" : (count > 0 ? (Games ? L.F("Residui di giochi non più installati: {0}", count) : L.F("Residui di programmi non più installati: {0}", count)) : L.T("Tracce non più valide"))
                + (recoverable > 0 ? L.F(" · {0} recuperabili", Fmt.Size(recoverable)) : "");
        }

        /// <summary>Dà un nome (e una copertina) ai residui identificati solo dall'AppID di Steam, e li unisce al gruppo dello stesso gioco.</summary>
        private async Task EnrichOnlineAsync()
        {
            if (!CoverService.Enabled) return;
            foreach (var g in Groups.Where(x => x.GroupKey != null && x.GroupKey.StartsWith("s:") && (x.Link != null || !x.HasImage)).ToList())
            {
                string appId = g.GroupKey.Substring(2);
                CoverResult info = null;
                try { info = await Task.Run(() => CoverService.ForAppId(appId)); }
                catch (Exception ex) { Log.Error(L.F("Nome del gioco {0}", appId), ex); }
                if (info == null || !Groups.Contains(g)) continue;
                // l'AppID appartiene a un gioco installato (es. salvataggi di un repack che non dichiara l'AppID): non è un residuo
                var installedTwin = string.IsNullOrEmpty(info.Name) ? null
                    : MainViewModel.Current.Games.Detected.FirstOrDefault(x => CoverService.MatchScore(x.Name, info.Name) >= 0.8);
                if (installedTwin != null)
                {
                    Log.Info("Residui: AppID {0} ({1}) appartiene a \"{2}\", installato: tolto dall'elenco", appId, info.Name, installedTwin.Name);
                    Groups.Remove(g);
                    UpdateSummary();
                    UpdateSelection();
                    continue;
                }
                if (g.Link != null && !string.IsNullOrEmpty(info.Name))
                {
                    g.Title = info.Name;
                    g.Link = null;
                }
                if (!g.HasImage && (info.Portrait ?? info.Header) != null)
                    g.Image = await Images.LoadAsync(info.Portrait ?? info.Header, 90);
                // stesso gioco trovato anche per nome (es. salvataggi in "Documenti\My Games\SnowRunner")?
                string key = NameUtil.Norm(g.Title);
                var twin = Groups.FirstOrDefault(x => x != g && NameUtil.Norm(Regex.Replace(x.Title, @"\s*\(.*\)\s*$", "")) == key);
                if (twin != null)
                {
                    // stesso gioco disinstallato: se il suo gruppo è tutto selezionato, lo sono anche questi elementi
                    if (twin.IsChecked == true) foreach (var i in g.Items) i.SetCheckedSilently(true);
                    twin.Absorb(g);
                    Groups.Remove(g);
                    UpdateSummary();
                    UpdateSelection();
                }
            }
            // copertine per i giochi riconosciuti solo dal nome delle loro cartelle
            foreach (var g in Groups.Where(x => !x.HasImage && x.GroupKey != null && x.GroupKey.StartsWith("n:")).ToList())
            {
                string name = Regex.Replace(g.Title, @"\s*\(.*\)\s*$", "");
                CoverResult info = null;
                try { info = await Task.Run(() => CoverService.ForName(name)); }
                catch (Exception ex) { Log.Error(L.F("Copertina di {0}", name), ex); }
                if (info != null && Groups.Contains(g) && (info.Portrait ?? info.Header) != null)
                    g.Image = await Images.LoadAsync(info.Portrait ?? info.Header, 90);
            }
        }

        private string GlyphFor(string source)
        {
            switch (source)
            {
                case "Driver video": return "";
                case "Collegamenti": return "";
                case "Registro": return "";
                case "Salvataggi": return "";
                case "Avvio automatico": return "";
                case "Attività pianificate": return "";
                case "Servizi": return "";
                case "Variabili d'ambiente": return "";
                case "Microsoft Store": return "";
                default: return Games ? "" : "";
            }
        }

        private void UpdateSelection()
        {
            int count = Groups.Sum(g => g.CheckedCount);
            long size = Groups.Sum(g => g.CheckedSize);
            SelectionText = count == 0 ? L.T("Nessun elemento selezionato") : Fmt.Count(count, "elemento selezionato", "elementi selezionati") + (size > 0 ? " · " + Fmt.Size(size) : "");
            CommandManager.InvalidateRequerySuggested();
        }

        private void Clean()
        {
            var all = Groups.SelectMany(g => g.Items).ToList();
            var selected = all.Where(i => i.IsChecked).Select(i => i.Item).ToList();
            var unselected = all.Where(i => !i.IsChecked).Select(i => i.Item).ToList();
            if (selected.Count == 0) return;
            string msg = L.F("Da eliminare: {0}.", Fmt.Count(selected.Count, "elemento", "elementi"));
            msg += useRecycleBin ? L.T(" File e cartelle vanno nel Cestino; le cache si eliminano subito.")
                                 : L.T(" File e cartelle saranno eliminati definitivamente.");
            if (backupEnabled) msg += L.T(" Prima viene creato un backup.");
            if (selected.Any(i => i.RequiresAdmin) && !Native.IsElevated)
                msg += L.T("\n\nAlcuni elementi richiedono i permessi di amministratore e verranno saltati.");
            string option = null;
            if (unselected.Count > 0)
            {
                msg += "\n\n" + L.F("Elementi non selezionati che restano sul PC: {0}.", unselected.Count);
                option = L.T("Elimina anche gli elementi non selezionati");
            }
            MainViewModel.Current.Confirm(L.T("Eliminare i residui selezionati?"), msg, L.T("Elimina"), true, null, option, includeAll =>
            {
                var items = includeAll ? selected.Concat(unselected).ToList() : selected;
                int left = includeAll ? 0 : unselected.Count;
                var vm = new CleanupDialogViewModel(items, backupEnabled, useRecycleBin, left, Kind);
                vm.Closed = changed =>
                {
                    MainViewModel.Current.Dialog = null;
                    if (changed) { var t = MainViewModel.Current.ScanOrphansAsync(); }
                };
                MainViewModel.Current.Dialog = vm;
            });
        }
    }

    // ====================================================================== Pagina "Impostazioni"

    public sealed class SettingsPageViewModel : ObservableObject
    {
        public SettingsPageViewModel()
        {
            LibraryFolders = new ObservableCollection<string>();
            AutoLibraryFolders = new ObservableCollection<string>();
            ProgramFolders = new ObservableCollection<string>();
            AutoProgramFolders = new ObservableCollection<string>();
            HiddenEntries = new ObservableCollection<HiddenEntryViewModel>();
            NetRules = new ObservableCollection<NetRuleViewModel>();
            RefreshNetRulesCommand = new RelayCommand(LoadNetRules);
            ChangeBackupFolderCommand = new RelayCommand(ChangeBackupFolder);
            OpenSaveVaultCommand = new RelayCommand(() =>
            {
                try
                {
                    Directory.CreateDirectory(SaveVault.Root);
                    ShellActions.OpenFolder(SaveVault.Root);
                }
                catch (Exception ex) { MainViewModel.Current.Message(L.T("Cartella non disponibile"), ex.Message); }
            });
            AddLibraryFolderCommand = new RelayCommand(() => AddFolder(AppKind.Game));
            RemoveLibraryFolderCommand = new RelayCommand(p => RemoveFolder(AppKind.Game, p as string));
            AddProgramFolderCommand = new RelayCommand(() => AddFolder(AppKind.Program));
            RemoveProgramFolderCommand = new RelayCommand(p => RemoveFolder(AppKind.Program, p as string));
            OpenLogCommand = new RelayCommand(() => MainViewModel.Current.Navigate("logs"));
            ClearCoversCommand = new RelayCommand(() =>
            {
                CoverService.ClearCache();
                UpdateCacheText();
                MainViewModel.Current.ShowToast(L.T("Immagini scaricate eliminate: verranno cercate di nuovo."));
                var t = MainViewModel.Current.Games.LoadAsync();
            });
            SaveSgdbKeyCommand = new RelayCommand(() => { var t = SaveSgdbKeyAsync(); });
            RemoveSgdbKeyCommand = new RelayCommand(() =>
            {
                S.SteamGridDbKey = null;
                S.Save();
                SetSgdbStatus(L.T("Chiave rimossa: SteamGridDB non verrà più usato."), "#9AA4B2");
                Raise("HasSgdbKey", "SgdbKeyMasked");
            });
            OpenSgdbHelpCommand = new RelayCommand(() => ShellActions.OpenUrl("https://www.steamgriddb.com/profile/preferences/api"));
            RescanCommand = new RelayCommand(() => { var t = MainViewModel.Current.Games.LoadAsync(); MainViewModel.Current.Navigate("games"); });
            RescanProgramsCommand = new RelayCommand(() => { var t = MainViewModel.Current.Programs.LoadAsync(); MainViewModel.Current.Navigate("programs"); });
            OtherPcs = new ObservableCollection<OtherPcViewModel>();
            FreeSpaceCommand = new RelayCommand(() => { var t = FreeSpaceAsync(); }, () => !isFreeing);
        }

        // ------------------------------------------------------------------ Aspetto e lingua

        public string ThemeKey
        {
            get { return S.Theme; }
            set { if (value != null && value != S.Theme) MainViewModel.Current.SetTheme(value); }
        }

        internal void RaiseTheme() { Raise("ThemeKey"); }

        public string LanguageKey
        {
            get { return S.Language; }
            set
            {
                if (value == null || value == S.Language) return;
                string before = L.Resolve(S.Language);
                S.Language = value;
                S.Save();
                Raise("LanguageKey");
                if (L.Resolve(value) == before) return;
                MainViewModel.Current.Confirm(L.T("Riavviare AppManager?"), L.T("La nuova lingua vale dal prossimo avvio del programma."),
                    L.T("Riavvia adesso"), false, () => MainViewModel.Current.RestartApp());
            }
        }

        // ------------------------------------------------------------------ Questo PC e risorse

        private string resourcesText;
        private bool isFreeing;

        public string ThisPcTitle { get { return L.F("Questo PC: {0}", ThisPc.Name); } }

        public string ThisPcDetail { get { return ThisPc.WindowsAgeText; } }

        public ObservableCollection<OtherPcViewModel> OtherPcs { get; private set; }
        public bool HasOtherPcs { get { return OtherPcs.Count > 0; } }
        public string ResourcesText { get { return resourcesText ?? "…"; } private set { Set(ref resourcesText, value); } }
        public ICommand FreeSpaceCommand { get; private set; }

        public string CoverBudgetKey
        {
            get { return S.CoverBudgetMb.ToString(); }
            set
            {
                int n;
                if (!int.TryParse(value, out n) || n == S.CoverBudgetMb) return;
                S.CoverBudgetMb = n;
                S.Save();
                Raise("CoverBudgetKey");
                var t = FreeSpaceAsync(true);
            }
        }

        private void LoadOtherPcs()
        {
            OtherPcs.Clear();
            foreach (var p in ThisPc.Others()) OtherPcs.Add(new OtherPcViewModel(p, this));
            Raise("HasOtherPcs");
        }

        internal void ForgetPc(string id, string name)
        {
            MainViewModel.Current.Confirm(L.F("Dimenticare {0}?", name),
                L.T("Vengono eliminate le sue impostazioni e le immagini usate solo da lui. Se ci torni, sarà come un PC nuovo."),
                L.T("Dimentica"), true, () =>
                {
                    ThisPc.Forget(id);
                    LoadOtherPcs();
                    var t = FreeSpaceAsync(true);
                });
        }

        /// <summary>Misura lo spazio usato nella cartella Dati, in background.</summary>
        private void UpdateResourcesText()
        {
            Task.Run(() =>
            {
                long total = PathUtil.Size(AppPaths.DataDir);
                int files;
                long covers;
                CoverService.CacheStats(out files, out covers);
                long free = PcResources.FreeSpace();
                string text = L.F("{0} in tutto: {1} di immagini, {2} di registro attività e impostazioni.", Fmt.Size(total), Fmt.Size(covers), Fmt.Size(Math.Max(0, total - covers)));
                if (free >= 0) text += " " + L.F("Sull'unità restano liberi {0}.", Fmt.Size(free));
                if (PcResources.IsLowSpace) text += " " + L.T("Spazio quasi esaurito: finché non se ne libera non si scaricano nuove immagini.");
                Application.Current.Dispatcher.BeginInvoke(new Action(() => ResourcesText = text));
            });
        }

        private async Task FreeSpaceAsync(bool quiet = false)
        {
            if (isFreeing) return;
            isFreeing = true;
            CommandManager.InvalidateRequerySuggested();
            try
            {
                long before = PathUtil.Size(AppPaths.DataDir);
                var games = MainViewModel.Current.Games.HasLoaded && !MainViewModel.Current.Games.IsLoading ? MainViewModel.Current.Games.Detected : null;
                long after = await Task.Run(() =>
                {
                    PcResources.StartupMaintenance();
                    if (games != null) CoverService.FreeSpaceNow(games);
                    return PathUtil.Size(AppPaths.DataDir);
                });
                PcResources.TrimMemory();
                long freed = before - after;
                if (freed > 0) MainViewModel.Current.ShowToast(L.F("Liberati {0}", Fmt.Size(freed)));
                else if (!quiet) MainViewModel.Current.ShowToast(L.T("Niente da liberare: è già tutto in ordine."));
                UpdateCacheText();
                UpdateResourcesText();
            }
            catch (Exception ex)
            {
                Log.Error("Pulizia delle risorse", ex);
                MainViewModel.Current.Message(L.T("Operazione non riuscita"), ex.Message);
            }
            finally
            {
                isFreeing = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private static AppSettings S { get { return AppSettings.Current; } }

        public bool UseRecycleBin { get { return S.UseRecycleBin; } set { S.UseRecycleBin = value; S.Save(); DeletionChanged("UseRecycleBin", "DeletePermanently"); } }
        public bool DeletePermanently { get { return !S.UseRecycleBin; } set { UseRecycleBin = !value; } }
        public bool BackupBeforeClean { get { return S.BackupBeforeClean; } set { S.BackupBeforeClean = value; S.Save(); DeletionChanged("BackupBeforeClean"); } }

        /// <summary>Le scelte di partenza valgono subito anche nelle pagine Residui (prima si vedevano solo dopo un riavvio).</summary>
        private void DeletionChanged(params string[] names)
        {
            Raise(names);
            Raise("DeletionSummary", "DeletionColor", "DeletionGlyph");
            if (MainViewModel.Current != null) MainViewModel.Current.ApplyDeletionDefaults();
        }

        /// <summary>Cosa si può recuperare con le scelte attuali.</summary>
        public string DeletionSummary
        {
            get
            {
                if (S.UseRecycleBin && S.BackupBeforeClean)
                    return L.T("Puoi tornare indietro in due modi: dal Cestino (file e cartelle) e dal backup (salvataggi, impostazioni e voci di sistema).");
                if (S.UseRecycleBin)
                    return L.T("Puoi recuperare tutto dal Cestino finché non lo svuoti. Nessun backup.");
                if (S.BackupBeforeClean)
                    return L.T("I file vengono eliminati subito. Nel backup restano solo salvataggi, impostazioni e voci di sistema, non le cartelle dei programmi.");
                return L.T("Attenzione: ciò che elimini non si può recuperare.");
            }
        }

        public string DeletionColor { get { return S.UseRecycleBin || S.BackupBeforeClean ? (S.UseRecycleBin && S.BackupBeforeClean ? "#3DD68C" : "#9AA4B2") : "#F5B94B"; } }
        public string DeletionGlyph { get { return S.UseRecycleBin || S.BackupBeforeClean ? "\uE73E" : "\uE7BA"; } }
        public bool UsePrefetch { get { return S.UsePrefetch; } set { S.UsePrefetch = value; S.Save(); PrefetchIndex.Reset(); Raise("UsePrefetch"); } }

        public bool BackgroundMonitor
        {
            get { return S.BackgroundMonitor; }
            set
            {
                if (value != S.BackgroundMonitor)
                {
                    string err = BackgroundService.SetEnabled(value);
                    if (err != null) MainViewModel.Current.Message(L.T("Avvio con Windows non attivato"), L.F("Windows non ha accettato la richiesta: {0}", err));
                }
                Raise("BackgroundMonitor");
            }
        }

        private ICommand openMonitor;
        public ICommand OpenMonitorCommand { get { return openMonitor ?? (openMonitor = new RelayCommand(() => MainViewModel.Current.Navigate("monitor"))); } }

        internal void RaiseBackground() { Raise("BackgroundMonitor", "CleanableAlert"); }

        public bool CleanableAlert { get { return S.CleanableAlert; } set { S.CleanableAlert = value; S.Save(); Raise("CleanableAlert"); } }

        public string CleanableThresholdKey
        {
            get { return S.CleanableThresholdMb.ToString(); }
            set { int mb; if (int.TryParse(value, out mb) && mb > 0) { S.CleanableThresholdMb = mb; S.Save(); Raise("CleanableThresholdKey"); } }
        }

        public string Threshold500 { get { return Fmt.Size(500L * 1024 * 1024); } }
        public string Threshold1024 { get { return Fmt.Size(1024L * 1024 * 1024); } }
        public string Threshold2048 { get { return Fmt.Size(2048L * 1024 * 1024); } }
        public string Threshold5120 { get { return Fmt.Size(5120L * 1024 * 1024); } }

        public bool HasCleanableExclusions { get { return S.CleanableExcluded != null && S.CleanableExcluded.Count > 0; } }

        // i gruppi di una categoria si vedono sotto il suo interruttore: qui solo quelli senza categoria
        private List<string> UncategorizedExclusions
        {
            get { return (S.CleanableExcluded ?? new List<string>()).Where(k => CleanableCheck.CategoryOf(k) == null).ToList(); }
        }
        public bool HasUncategorizedExclusions { get { return UncategorizedExclusions.Count > 0; } }
        public string CleanableExclusionsText
        {
            get { var list = UncategorizedExclusions; return list.Count > 0 ? L.F("Esclusi dall'avviso: {0}.", string.Join(", ", list.Select(LogJanitor.GroupName))) : null; }
        }

        private ICommand clearExclusions;
        public ICommand ClearCleanableExclusionsCommand
        {
            get
            {
                return clearExclusions ?? (clearExclusions = new RelayCommand(() =>
                {
                    CleanableCheck.ClearExclusions();
                    RaiseCleanable();
                    var m = MainViewModel.Current;
                    if (m != null && m.Logs != null) m.Logs.RefreshAlerts();
                }));
            }
        }

        internal void RaiseCleanable()
        {
            Raise("HasCleanableExclusions", "HasUncategorizedExclusions", "CleanableExclusionsText", "CleanableAlert");
            if (cleanableCategories != null) foreach (var c in cleanableCategories) c.Refresh();
        }

        private List<CleanableCategoryViewModel> cleanableCategories;
        /// <summary>Gli interruttori delle categorie: cosa conta l'avviso di log e cache.</summary>
        public List<CleanableCategoryViewModel> CleanableCategories
        {
            get
            {
                if (cleanableCategories == null)
                {
                    CleanableCheck.Normalize();
                    cleanableCategories = CleanableCheck.Categories().Select(c => new CleanableCategoryViewModel(c, () =>
                    {
                        RaiseCleanable();
                        var m = MainViewModel.Current;
                        if (m != null && m.Logs != null) m.Logs.RefreshAlerts();
                    })).ToList();
                }
                return cleanableCategories;
            }
        }

        public bool ShowSteamTools
        {
            get { return S.ShowSteamTools; }
            set { S.ShowSteamTools = value; S.Save(); Raise("ShowSteamTools"); var t = MainViewModel.Current.Programs.LoadAsync(); }
        }

        public bool DownloadCovers { get { return S.DownloadCovers; } set { S.DownloadCovers = value; S.Save(); Raise("DownloadCovers"); } }
        public bool SearchSavesOnline { get { return S.SearchSavesOnline; } set { S.SearchSavesOnline = value; S.Save(); Raise("SearchSavesOnline"); } }
        public bool IsPortable { get { return AppPaths.IsPortable; } }
        public string BackupFolder { get { return S.BackupFolder; } }
        public string SaveVaultFolder { get { return SaveVault.Root; } }

        /// <summary>Backup da tenere per applicazione, come testo per i pulsanti a pillola ("0" = tutti).</summary>
        public string BackupsToKeepKey
        {
            get { return S.BackupsToKeep.ToString(); }
            set
            {
                int n;
                if (!int.TryParse(value, out n) || n == S.BackupsToKeep) return;
                S.BackupsToKeep = n;
                S.Save();
                Raise("BackupsToKeepKey");
            }
        }
        public bool IsAdmin { get { return Native.IsElevated; } }
        public bool PrefetchReadable { get { return PrefetchIndex.IsAvailable; } }
        public string Version { get { return "AppManager " + typeof(SettingsPageViewModel).Assembly.GetName().Version.ToString(2); } }
        public string DataFolder { get { return AppPaths.DataDir; } }
        public string DataFolderText { get { return L.F("Impostazioni, registro e immagini sono in {0}", AppPaths.DataDir); } }

        public ObservableCollection<string> LibraryFolders { get; private set; }
        public ObservableCollection<string> AutoLibraryFolders { get; private set; }
        public ObservableCollection<string> ProgramFolders { get; private set; }
        public ObservableCollection<string> AutoProgramFolders { get; private set; }
        public bool HasAutoLibraryFolders { get { return AutoLibraryFolders.Count > 0; } }
        public bool HasAutoProgramFolders { get { return AutoProgramFolders.Count > 0; } }

        // Quanti giochi e programmi trova ciascun metodo. Un elemento può essere trovato da più metodi (per esempio un gioco di Steam
        // con un collegamento sul desktop): il numero li conta tutti, "già trovati prima" indica quelli che un metodo precedente aveva già trovato.
        private readonly Dictionary<string, string> counts = new Dictionary<string, string>();
        private string Count(string key) { string v; return counts.TryGetValue(key, out v) ? v : "…"; }
        private string Also(string key) { string v; return counts.TryGetValue(key + "+", out v) ? v : null; }
        public string CountLauncher { get { return Count("g-Launcher"); } }
        public string CountInstalled { get { return Count("g-Installed"); } }
        public string CountShortcut { get { return Count("g-Shortcut"); } }
        public string CountFolder { get { return Count("g-Folder"); } }
        public string CountManual { get { return Count("g-Manual"); } }
        public string CountProgInstalled { get { return Count("p-Installed"); } }
        public string CountProgStore { get { return Count("p-Store"); } }
        public string CountProgLauncher { get { return Count("p-Launcher"); } }
        public string CountProgShortcut { get { return Count("p-Shortcut"); } }
        public string CountProgFolder { get { return Count("p-Folder"); } }
        public string CountProgManual { get { return Count("p-Manual"); } }
        public string CountLauncherAlso { get { return Also("g-Launcher"); } }
        public string CountInstalledAlso { get { return Also("g-Installed"); } }
        public string CountShortcutAlso { get { return Also("g-Shortcut"); } }
        public string CountFolderAlso { get { return Also("g-Folder"); } }
        public string CountManualAlso { get { return Also("g-Manual"); } }
        public string CountProgInstalledAlso { get { return Also("p-Installed"); } }
        public string CountProgStoreAlso { get { return Also("p-Store"); } }
        public string CountProgLauncherAlso { get { return Also("p-Launcher"); } }
        public string CountProgShortcutAlso { get { return Also("p-Shortcut"); } }
        public string CountProgFolderAlso { get { return Also("p-Folder"); } }
        public string CountProgManualAlso { get { return Also("p-Manual"); } }

        internal void RefreshSources()
        {
            var main = MainViewModel.Current;
            if (main == null) return;
            var games = main.Games.IsLoading ? null : main.Games.Detected;
            var programs = main.Programs.IsLoading || !main.Programs.HasLoaded ? null : main.Programs.Detected;
            counts.Clear();
            foreach (FoundVia via in Enum.GetValues(typeof(FoundVia)))
            {
                if (games != null) SetCount("g-" + via, games, via, "gioco", "giochi");
                if (programs != null) SetCount("p-" + via, programs, via, "programma", "programmi");
            }
            var names = new List<string>();
            foreach (var k in new[] { "Launcher", "Installed", "Shortcut", "Folder", "Manual", "ProgInstalled", "ProgStore", "ProgLauncher", "ProgShortcut", "ProgFolder", "ProgManual" })
            {
                names.Add("Count" + k);
                names.Add("Count" + k + "Also");
            }
            names.Add("HasAutoLibraryFolders");
            names.Add("HasAutoProgramFolders");
            Raise(names.ToArray());
        }

        private void SetCount(string key, List<AppEntry> list, FoundVia via, string one, string many)
        {
            int total = list.Count(e => e.Via == via || e.FoundBy.Contains(via));
            int before = list.Count(e => e.Via != via && e.FoundBy.Contains(via));
            counts[key] = Fmt.Count(total, one, many);
            if (before > 0) counts[key + "+"] = L.F("già trovati prima: {0}", before);
        }

        public ObservableCollection<HiddenEntryViewModel> HiddenEntries { get; private set; }
        public bool HasHiddenEntries { get { return HiddenEntries.Count > 0; } }

        public ObservableCollection<NetRuleViewModel> NetRules { get; private set; }
        public bool HasNetRules { get { return NetRules.Count > 0; } }
        public ICommand RefreshNetRulesCommand { get; private set; }

        internal void LoadNetRules()
        {
            // l'enumerazione delle regole del firewall è lenta: la facciamo in background per non bloccare la pagina
            Task.Run(() =>
            {
                List<FirewallRuleInfo> rules;
                try { rules = Firewall.List(); }
                catch (Exception ex) { Log.Error("Elenco regole di rete", ex); rules = new List<FirewallRuleInfo>(); }
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    NetRules.Clear();
                    foreach (var r in rules) NetRules.Add(new NetRuleViewModel(r, this));
                    Raise("HasNetRules");
                }));
            });
        }

        internal void RemoveNetRule(NetRuleViewModel r)
        {
            if (!Native.IsElevated) { MainViewModel.Current.RequireAdmin(L.T("togliere una regola di blocco")); return; }
            try { Firewall.RemoveByName(r.RuleName); NetRules.Remove(r); Raise("HasNetRules"); }
            catch (Exception ex) { Log.Error("Rimozione regola di rete", ex); MainViewModel.Current.Message(L.T("Operazione non riuscita"), ex.Message); }
        }

        public ICommand ChangeBackupFolderCommand { get; private set; }
        public ICommand OpenSaveVaultCommand { get; private set; }
        public ICommand AddLibraryFolderCommand { get; private set; }
        public ICommand RemoveLibraryFolderCommand { get; private set; }
        public ICommand AddProgramFolderCommand { get; private set; }
        public ICommand RemoveProgramFolderCommand { get; private set; }
        public ICommand OpenLogCommand { get; private set; }
        public ICommand RescanCommand { get; private set; }
        public ICommand RescanProgramsCommand { get; private set; }
        public ICommand ClearCoversCommand { get; private set; }
        public ICommand SaveSgdbKeyCommand { get; private set; }
        public ICommand RemoveSgdbKeyCommand { get; private set; }
        public ICommand OpenSgdbHelpCommand { get; private set; }

        // ------------------------------------------------------------------ Immagini dei giochi

        private string sgdbInput, sgdbStatus, sgdbStatusColor, cacheText;

        public string SgdbKeyInput { get { return sgdbInput; } set { Set(ref sgdbInput, value); } }
        public string SgdbStatus { get { return sgdbStatus; } private set { Set(ref sgdbStatus, value); } }
        public string SgdbStatusColor { get { return sgdbStatusColor; } private set { Set(ref sgdbStatusColor, value); } }
        public bool HasSgdbKey { get { return !string.IsNullOrWhiteSpace(S.SteamGridDbKey); } }
        public string CacheText { get { return cacheText; } private set { Set(ref cacheText, value); } }

        public string SgdbKeyMasked
        {
            get
            {
                string k = S.SteamGridDbKey ?? "";
                return k.Length < 4 ? "" : L.F("Chiave salvata ••••••••{0}", k.Substring(k.Length - 4));
            }
        }

        private void SetSgdbStatus(string text, string color)
        {
            SgdbStatusColor = color;
            SgdbStatus = text;
        }

        private async Task SaveSgdbKeyAsync()
        {
            string key = (sgdbInput ?? "").Trim();
            if (key.Length == 0)
            {
                SetSgdbStatus(L.T("Incolla prima la chiave nella casella."), "#F5B94B");
                return;
            }
            SetSgdbStatus(L.T("Verifica della chiave in corso…"), "#9AA4B2");
            bool? ok = await Task.Run(() => CoverService.TestSteamGridDbKey(key));
            if (ok == false)
            {
                SetSgdbStatus(L.T("SteamGridDB ha rifiutato la chiave: controlla di averla copiata per intero."), "#EF4F56");
                return;
            }
            S.SteamGridDbKey = key;
            S.Save();
            SgdbKeyInput = "";
            Raise("HasSgdbKey", "SgdbKeyMasked");
            SetSgdbStatus(ok == true
                ? L.T("Chiave valida: le immagini mancanti verranno cercate anche su SteamGridDB.")
                : L.T("Chiave salvata, ma ora SteamGridDB non risponde: verrà usata appena possibile."), ok == true ? "#3DD68C" : "#F5B94B");
            // le ricerche fatte senza chiave vanno ripetute
            CoverService.ForgetIncomplete();
            var t = MainViewModel.Current.Games.LoadAsync();
        }

        private void UpdateCacheText()
        {
            int files;
            long bytes;
            CoverService.CacheStats(out files, out bytes);
            CacheText = files == 0 ? L.T("Nessuna immagine scaricata.")
                : L.F("{0} · {1}", Fmt.Count(files, "immagine", "immagini"), Fmt.Size(bytes));
        }

        internal void Reload()
        {
            LibraryFolders.Clear();
            foreach (var f in S.LibraryFolders) LibraryFolders.Add(f);
            ProgramFolders.Clear();
            foreach (var f in S.ProgramFolders) ProgramFolders.Add(f);
            var auto = new AppSettings { LibraryFolders = new List<string>(), ProgramFolders = new List<string>() };
            AutoLibraryFolders.Clear();
            foreach (var f in InstalledApps.PortableRoots(AppKind.Game, auto)) AutoLibraryFolders.Add(f);
            AutoProgramFolders.Clear();
            foreach (var f in InstalledApps.PortableRoots(AppKind.Program, auto)) AutoProgramFolders.Add(f);
            RefreshSources();
            HiddenEntries.Clear();
            foreach (var h in S.HiddenGames) HiddenEntries.Add(new HiddenEntryViewModel(h, AppKind.Game, this));
            foreach (var h in S.HiddenPrograms) HiddenEntries.Add(new HiddenEntryViewModel(h, AppKind.Program, this));
            LoadNetRules();
            UpdateCacheText();
            LoadOtherPcs();
            UpdateResourcesText();
            Raise("ThemeKey", "LanguageKey", "CoverBudgetKey", "HasHiddenEntries", "BackupFolder", "UseRecycleBin", "DeletePermanently", "BackupBeforeClean", "UsePrefetch", "ShowSteamTools", "DownloadCovers",
                "HasSgdbKey", "SgdbKeyMasked", "PrefetchReadable");
        }

        private IntPtr Owner()
        {
            var w = Application.Current.MainWindow;
            return w == null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(w).Handle;
        }

        private void ChangeBackupFolder()
        {
            string dir = Native.PickFolder(Owner(), L.T("Cartella in cui salvare i backup"), S.BackupFolder);
            if (dir == null) return;
            if (!Directory.Exists(dir) || PathUtil.Normalize(dir) == null)
            {
                MainViewModel.Current.Message(L.T("Cartella non valida"), L.T("Scegli una cartella su un disco del PC."));
                return;
            }
            if (Safety.IsSystemPath(dir) || dir.Length <= 3)
            {
                MainViewModel.Current.Message(L.T("Cartella non valida"), L.T("I backup non possono stare in una cartella di Windows o nella radice di un disco. Scegli un'altra cartella."));
                return;
            }
            S.BackupFolder = dir;
            S.Save();
            // la nuova cartella dei backup non deve mai essere proposta come residuo
            Safety.Invalidate();
            Raise("BackupFolder");
        }

        private void AddFolder(AppKind kind)
        {
            string title = kind == AppKind.Game ? L.T("Cartella che contiene i tuoi giochi (una sottocartella per gioco)") : L.T("Cartella che contiene i tuoi programmi portable (una sottocartella per programma)");
            string dir = Native.PickFolder(Owner(), title, null);
            var list = S.PortableFolders(kind);
            if (dir == null || list.Any(f => PathUtil.Same(f, dir))) return;
            if (PathUtil.Normalize(dir) == null || dir.Length <= 3 || PathUtil.IsUnder(dir, KnownDirs.Windows, true))
            {
                MainViewModel.Current.Message(L.T("Cartella non valida"), L.T("Scegli una cartella specifica (non un disco intero o una cartella di Windows)."));
                return;
            }
            list.Add(dir);
            S.Save();
            (kind == AppKind.Game ? LibraryFolders : ProgramFolders).Add(dir);
            Safety.Invalidate();
            MainViewModel.Current.ShowToast(kind == AppKind.Game ? L.T("Cartella aggiunta: premi «Aggiorna» nella pagina dei giochi.") : L.T("Cartella aggiunta: premi «Aggiorna» nella pagina dei programmi."));
        }

        private void RemoveFolder(AppKind kind, string dir)
        {
            if (dir == null) return;
            S.PortableFolders(kind).RemoveAll(f => PathUtil.Same(f, dir));
            S.Save();
            (kind == AppKind.Game ? LibraryFolders : ProgramFolders).Remove(dir);
            Safety.Invalidate();
        }

        internal void Unhide(HiddenEntryViewModel h)
        {
            S.Hidden(h.Kind).RemoveAll(x => x.Path == h.Hidden.Path);
            S.Save();
            HiddenEntries.Remove(h);
            Raise("HasHiddenEntries");
            var t = h.Kind == AppKind.Game ? MainViewModel.Current.Games.LoadAsync() : MainViewModel.Current.Programs.LoadAsync();
        }
    }

    public sealed class HiddenEntryViewModel
    {
        internal readonly SavedEntry Hidden;
        internal readonly AppKind Kind;

        internal HiddenEntryViewModel(SavedEntry h, AppKind kind, SettingsPageViewModel page)
        {
            Hidden = h;
            Kind = kind;
            UnhideCommand = new RelayCommand(() => page.Unhide(this));
        }

        public string Name { get { return Hidden.Name; } }
        public string KindLabel { get { return Kind == AppKind.Game ? L.T("Gioco") : L.T("Programma"); } }
        public ICommand UnhideCommand { get; private set; }
    }

    /// <summary>Una regola di blocco creata da AppManager (per l'elenco nelle Impostazioni).</summary>
    public sealed class NetRuleViewModel
    {
        internal readonly string RuleName;

        internal NetRuleViewModel(FirewallRuleInfo r, SettingsPageViewModel page)
        {
            RuleName = r.Name;
            Target = r.Remote != null ? L.F("Verso {0}", r.Remote) : (r.Program != null ? System.IO.Path.GetFileName(r.Program) : L.T("Tutte le connessioni"));
            string what = r.Program != null && r.Remote != null ? System.IO.Path.GetFileName(r.Program) + " → " + r.Remote
                : r.Program != null ? r.Program : (r.Remote != null ? L.F("Qualsiasi programma → {0}", r.Remote) : "");
            Detail = (r.Outbound ? L.T("In uscita") : L.T("In entrata")) + (what.Length > 0 ? "  ·  " + what : "");
            RemoveCommand = new RelayCommand(() => page.RemoveNetRule(this));
        }

        public string Target { get; private set; }
        public string Detail { get; private set; }
        public ICommand RemoveCommand { get; private set; }
    }

    /// <summary>Un interruttore delle Impostazioni: la categoria (Windows, browser…) conta per l'avviso di log e cache.</summary>
    public sealed class CleanableCategoryViewModel : ObservableObject
    {
        private readonly CleanableCategory category;
        private readonly string key;
        private readonly Action changed;

        internal CleanableCategoryViewModel(CleanableCategory c, Action changed)
        {
            category = c;
            key = c.Key;
            Name = c.Name;
            Detail = c.Detail;
            this.changed = changed;
        }

        public string Name { get; private set; }
        public string Detail { get; private set; }

        /// <summary>Gruppi esclusi uno per uno dalla pagina Log: l'interruttore resta acceso per gli altri.</summary>
        public string ExcludedText
        {
            get
            {
                var list = CleanableCheck.ExcludedIn(category);
                if (list.Count == 0) return null;
                string names = string.Join(", ", list.Select(LogJanitor.GroupName));
                return list.Count == 1 ? L.F("Escluso dalla pagina Log: {0}", names) : L.F("Esclusi dalla pagina Log: {0}", names);
            }
        }
        public bool HasExcluded { get { return CleanableCheck.ExcludedIn(category).Count > 0; } }

        public bool IsOn
        {
            get { return CleanableCheck.IsCategoryOn(key); }
            set
            {
                if (value == IsOn) return;
                CleanableCheck.SetCategoryOn(key, value);
                Raise("IsOn");
                if (changed != null) changed();
            }
        }

        internal void Refresh() { Raise("IsOn", "ExcludedText", "HasExcluded"); }
    }
}
