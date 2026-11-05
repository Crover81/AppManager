using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace AppManager
{
    // ====================================================================== Pagina "Log": pulizia dei log del PC e registro attività di AppManager

    public sealed class LogEntryViewModel
    {
        internal readonly LogEntry Entry;
        internal LogEntryViewModel(LogEntry e) { Entry = e; }

        public string TimeText { get { return Entry.Time.ToString("d MMM HH:mm:ss", Fmt.It); } }
        public string Level { get { return Entry.Level; } }
        public string LevelLabel
        {
            get
            {
                switch (Entry.Level)
                {
                    case "ERRORE": return L.T("Errore");
                    case "DETTAGLI": return L.T("Dettagli");
                    case "AVVISO": return L.T("Avviso");
                    default: return L.T("Info");
                }
            }
        }

        public string LevelColor
        {
            get
            {
                switch (Entry.Level)
                {
                    case "ERRORE": case "DETTAGLI": return "#EF4F56";
                    case "AVVISO": return "#F5B94B";
                    default: return "#5B8CFF";
                }
            }
        }

        /// <summary>Prima riga (le tracce degli errori continuano nella descrizione completa).</summary>
        public string Summary
        {
            get
            {
                string m = Entry.Message ?? "";
                int nl = m.IndexOf('\n');
                return nl < 0 ? m : m.Substring(0, nl) + "  …";
            }
        }

        public string Message { get { return Entry.Message; } }
        public bool IsMultiline { get { return (Entry.Message ?? "").IndexOf('\n') >= 0; } }
    }

    public sealed class LogsPageViewModel : ObservableObject
    {
        private string tab = "pc";

        // --- log del PC
        private bool isScanning, hasScanned, onlyOld = true, useRecycleBin;
        private string progressText, summary, selectionText;
        private CancellationTokenSource scanCts;

        // --- registro di AppManager
        private List<LogEntryViewModel> allEntries = new List<LogEntryViewModel>();
        private string levelFilter = "all", searchText = "", logInfo;
        private bool isLoadingLog;

        public LogsPageViewModel()
        {
            Groups = new ObservableCollection<ItemGroupViewModel>();
            Entries = new ObservableCollection<LogEntryViewModel>();
            ScanCommand = new RelayCommand(() => { var t = ScanAsync(); }, () => !isScanning);
            CleanCommand = new RelayCommand(Clean, () => !isScanning && Groups.Any(g => g.CheckedCount > 0));
            SelectAllCommand = new RelayCommand(() => { foreach (var g in Groups) g.IsChecked = true; });
            SelectNoneCommand = new RelayCommand(() => { foreach (var g in Groups) g.IsChecked = false; });
            ExpandAllCommand = new RelayCommand(() => { bool any = Groups.Any(g => !g.IsExpanded); foreach (var g in Groups) g.IsExpanded = any; });
            RefreshCommand = new RelayCommand(() =>
            {
                if (tab == "app") { var t = LoadLogAsync(); }
                else if (!isScanning) { var t = ScanAsync(); }
            });
            ClearLogCommand = new RelayCommand(ClearLog);
            ExportLogCommand = new RelayCommand(ExportLog);
            OpenLogFolderCommand = new RelayCommand(() => ShellActions.Reveal(Log.FilePath));
        }

        /// <summary>Scheda: "pc" (log e diagnostica del PC) o "app" (registro di AppManager).</summary>
        public string Tab
        {
            get { return tab; }
            set
            {
                if (value == null || !Set(ref tab, value)) return;
                Raise("IsPcTab", "IsAppTab");
                if (tab == "app") { var t = LoadLogAsync(); }
            }
        }

        public bool IsPcTab { get { return tab == "pc"; } }
        public bool IsAppTab { get { return tab == "app"; } }

        internal void OnShown()
        {
            if (tab == "app") { var t = LoadLogAsync(); }
        }

        /// <summary>Dall'avviso di log e cache: si apre direttamente l'analisi.</summary>
        internal void StartPcScan()
        {
            Tab = "pc";
            if (!isScanning) { var t = ScanAsync(); }
        }

        // ------------------------------------------------------------------ Log e diagnostica del PC

        public ObservableCollection<ItemGroupViewModel> Groups { get; private set; }
        public ICommand ScanCommand { get; private set; }
        public ICommand CleanCommand { get; private set; }
        public ICommand SelectAllCommand { get; private set; }
        public ICommand SelectNoneCommand { get; private set; }
        public ICommand ExpandAllCommand { get; private set; }
        public ICommand RefreshCommand { get; private set; }

        public bool IsScanning { get { return isScanning; } private set { if (Set(ref isScanning, value)) Raise("ShowIntro", "HasResults", "IsClean"); } }
        public bool HasScanned { get { return hasScanned; } private set { if (Set(ref hasScanned, value)) Raise("ShowIntro", "HasResults", "IsClean"); } }
        public bool ShowIntro { get { return !hasScanned && !isScanning; } }
        public bool HasResults { get { return hasScanned && !isScanning && Groups.Count > 0; } }
        public bool IsClean { get { return hasScanned && !isScanning && Groups.Count == 0; } }
        public string ProgressText { get { return progressText; } private set { Set(ref progressText, value); } }
        public string Summary { get { return summary; } private set { Set(ref summary, value); } }
        public string SelectionText { get { return selectionText; } private set { Set(ref selectionText, value); } }
        public bool UseRecycleBin { get { return useRecycleBin; } set { Set(ref useRecycleBin, value); } }
        public bool ShowAdminHint { get { return !Native.IsElevated; } }

        /// <summary>Solo i file più vecchi di 7 giorni: i log recenti possono servire se un gioco o programma ha appena dato problemi.</summary>
        public bool OnlyOld
        {
            get { return onlyOld; }
            set { if (Set(ref onlyOld, value) && hasScanned && !isScanning) { var t = ScanAsync(); } }
        }

        private async Task ScanAsync()
        {
            if (isScanning) return;
            if (scanCts != null) scanCts.Cancel();
            scanCts = new CancellationTokenSource();
            var ct = scanCts.Token;
            IsScanning = true;
            ProgressText = L.T("Preparazione…");
            int days = onlyOld ? 7 : 0;
            var prog = new Progress<string>(s => ProgressText = s);
            List<LeftoverItem> items;
            int locked = 0;
            try
            {
                items = await Task.Run(() =>
                {
                    int l;
                    var r = LogJanitor.Find(days, ((IProgress<string>)prog).Report, ct, out l);
                    locked = l;
                    return r;
                }, ct);
            }
            catch (OperationCanceledException) { IsScanning = false; return; }
            catch (Exception ex)
            {
                Log.Error("Ricerca dei log", ex);
                IsScanning = false;
                MainViewModel.Current.Message(L.T("Analisi non riuscita"), ex.Message);
                return;
            }
            Groups.Clear();
            foreach (var grp in items.GroupBy(i => i.GroupKey).OrderByDescending(g => g.Sum(i => Math.Max(0, i.Size))))
            {
                var first = grp.First();
                var vm = new ItemGroupViewModel(first.GroupName, GlyphFor(grp.Key),
                    grp.OrderByDescending(i => i.Size).Select(i => new ItemViewModel(i, i.Confidence >= Confidence.Medium)));
                vm.Source = first.GroupSource;
                vm.GroupKey = grp.Key;
                if (AppSettings.Current.BackgroundMonitor && AppSettings.Current.CleanableAlert)
                    vm.EnableAlertToggle(() => { RefreshAlerts(); var m = MainViewModel.Current; if (m != null) m.Settings.RaiseCleanable(); });
                vm.IsExpanded = grp.Count() <= 6;
                vm.Changed = UpdateSelection;
                Groups.Add(vm);
            }
            long total = items.Sum(i => Math.Max(0, i.Size));
            Summary = items.Count == 0 ? "" : Fmt.Count(items.Count, "elemento", "elementi") + " · " + Fmt.Size(total) + (locked > 0 ? " · " + Fmt.Count(locked, "file in uso saltato", "file in uso saltati") : "");
            HasScanned = true;
            IsScanning = false;
            UpdateSelection();
        }

        /// <summary>Una categoria accesa o spenta può cambiare più gruppi insieme.</summary>
        internal void RefreshAlerts()
        {
            foreach (var g in Groups) g.RefreshAlert();
        }

        private static string GlyphFor(string key)
        {
            switch (key)
            {
                case "log:wer": return "";
                case "log:dump": return "";
                case "log:unity": case "log:unreal": return "";
                case "log:temp": return "";
                case "log:tempfiles": return "\uE8B7";
                case "log:shader": return "\uE7F4";
                case "log:windows": return "\uE770";
                case "log:wucache": case "log:docache": return "\uE895";
                case "log:driver": return "\uE7F4";
                case "log:driverpkg": return "\uE7B8";
                case "log:browser": return "\uE774";
                default: return "";
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
            var selected = Groups.SelectMany(g => g.Items).Where(i => i.IsChecked).Select(i => i.Item).ToList();
            if (selected.Count == 0) return;
            int left = Groups.Sum(g => g.Items.Count(i => !i.IsChecked));
            string msg = L.F("Da eliminare: {0} ({1}).", Fmt.Count(selected.Count, "file o cartella di log", "file e cartelle di log"), Fmt.Size(selected.Sum(i => Math.Max(0, i.Size)))) + " ";
            msg += useRecycleBin ? L.T("I file vanno nel Cestino.")
                                 : L.T("Sono file che servono solo per la diagnosi: vengono eliminati definitivamente.");
            if (selected.Any(i => i.RequiresAdmin) && !Native.IsElevated)
                msg += L.T("\n\nAlcuni elementi richiedono i permessi di amministratore e verranno saltati.");
            MainViewModel.Current.Confirm(L.T("Eliminare i log selezionati?"), msg, L.T("Elimina"), true, () =>
            {
                var vm = new CleanupDialogViewModel(selected, false, useRecycleBin, left, AppKind.Program, L.T("Pulizia dei log"));
                vm.Closed = changed =>
                {
                    MainViewModel.Current.Dialog = null;
                    if (changed) { var t = ScanAsync(); }
                };
                MainViewModel.Current.Dialog = vm;
            });
        }

        // ------------------------------------------------------------------ Registro di AppManager

        public ObservableCollection<LogEntryViewModel> Entries { get; private set; }
        public ICommand ClearLogCommand { get; private set; }
        public ICommand ExportLogCommand { get; private set; }
        public ICommand OpenLogFolderCommand { get; private set; }
        public bool IsLoadingLog { get { return isLoadingLog; } private set { if (Set(ref isLoadingLog, value)) Raise("HasNoEntries"); } }
        public bool HasNoEntries { get { return !isLoadingLog && Entries.Count == 0; } }
        public string LogInfo { get { return logInfo; } private set { Set(ref logInfo, value); } }

        /// <summary>Filtro per livello: all, problems (avvisi ed errori), errors.</summary>
        public string LevelFilter { get { return levelFilter; } set { if (value != null && Set(ref levelFilter, value)) ApplyLogFilter(); } }
        public string LogSearch { get { return searchText; } set { if (Set(ref searchText, value ?? "")) ApplyLogFilter(); } }

        /// <summary>Giorni di conservazione del registro (0 = nessun limite).</summary>
        public int RetentionDays
        {
            get { return AppSettings.Current.LogRetentionDays; }
            set
            {
                if (value == AppSettings.Current.LogRetentionDays) return;
                AppSettings.Current.LogRetentionDays = value;
                AppSettings.Current.Save();
                Raise("RetentionDays", "RetentionKey");
                Log.Maintain(value);
                var t = LoadLogAsync();
            }
        }

        /// <summary>Per i pulsanti a pillola: "7", "30", "90", "0".</summary>
        public string RetentionKey
        {
            get { return RetentionDays.ToString(); }
            set { int d; if (int.TryParse(value, out d)) RetentionDays = d; }
        }

        private async Task LoadLogAsync()
        {
            IsLoadingLog = true;
            List<LogEntry> entries;
            try { entries = await Task.Run(() => { Log.Flush(); return Log.ReadEntries(5000); }); }
            catch (Exception ex) { Log.Error("Lettura del registro", ex); entries = new List<LogEntry>(); }
            allEntries = entries.Select(e => new LogEntryViewModel(e)).ToList();
            int errors = entries.Count(e => e.Level == "ERRORE"), warnings = entries.Count(e => e.Level == "AVVISO");
            LogInfo = Fmt.Size(Log.SizeBytes) + " · " + Fmt.Count(entries.Count, "voce", "voci") + (errors > 0 ? " · " + Fmt.Count(errors, "errore", "errori") : "") + (warnings > 0 ? " · " + Fmt.Count(warnings, "avviso", "avvisi") : "") + (entries.Count >= 5000 ? L.T(" (sono mostrate le 5000 più recenti)") : "");
            ApplyLogFilter();
            IsLoadingLog = false;
        }

        private void ApplyLogFilter()
        {
            string q = (searchText ?? "").Trim();
            Entries.Clear();
            foreach (var e in allEntries)
            {
                if (levelFilter == "errors" && e.Level != "ERRORE" && e.Level != "DETTAGLI") continue;
                if (levelFilter == "problems" && e.Level == "INFO") continue;
                if (q.Length > 0 && (e.Message ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                Entries.Add(e);
            }
            Raise("HasNoEntries");
        }

        private void ClearLog()
        {
            MainViewModel.Current.Confirm(L.T("Svuotare il registro attività?"),
                L.T("Tutte le voci del registro di AppManager vengono cancellate."),
                L.T("Svuota"), true, () =>
                {
                    try
                    {
                        Log.Clear();
                        MainViewModel.Current.ShowToast(L.T("Registro attività svuotato."));
                    }
                    catch (Exception ex) { MainViewModel.Current.Message(L.T("Impossibile svuotare il registro"), ex.Message); }
                    var t = LoadLogAsync();
                });
        }

        private void ExportLog()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = L.T("Esporta il registro attività"),
                FileName = "AppManager registro " + DateTime.Now.ToString("yyyy-MM-dd HH.mm") + ".txt",
                Filter = L.T("File di testo (*.txt)|*.txt"),
                DefaultExt = ".txt",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            };
            if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
            try
            {
                Log.Export(dlg.FileName);
                MainViewModel.Current.ShowToast(L.T("Registro esportato."));
                ShellActions.Reveal(dlg.FileName);
            }
            catch (Exception ex) { MainViewModel.Current.Message(L.T("Esportazione non riuscita"), ex.Message); }
        }
    }
}
