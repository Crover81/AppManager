using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AppManager
{
    /// <summary>
    /// Procedura guidata: conferma → disinstallazione → ricerca residui → revisione → pulizia → riepilogo.
    /// Usata anche per "Dati del gioco/programma" (solo analisi) e per pulire i residui orfani.
    /// </summary>
    public sealed class CleanupDialogViewModel : ObservableObject, IDialogKeys
    {
        internal enum Mode { Uninstall, Analyze, Orphans, Leftovers }

        private readonly Mode mode;
        private readonly AppEntry entry;
        private readonly AppKind kind;
        private readonly List<AppEntry> allEntries;
        private readonly EntryViewModel entryVm;
        private AppProfile profile;
        private UninstallSession session;
        private DispatcherTimer pollTimer;
        private CancellationTokenSource cts = new CancellationTokenSource();
        private int orphanLeft;
        private bool changed;
        private string historyId;    // voce della cronologia della sorveglianza da aggiornare dopo la pulizia

        private string step, status, hint, progressText, warning, resultTitle, resultText, backupPath, selectionText, reviewNote;
        private double progress;
        private bool canContinue, canForce, backupEnabled, useRecycleBin;

        // ------------------------------------------------------------------ Costruzione

        internal CleanupDialogViewModel(EntryViewModel e, Mode mode, List<AppEntry> allEntries)
        {
            this.mode = mode;
            entryVm = e;
            entry = e.Entry;
            kind = entry.Kind;
            this.allEntries = allEntries;
            Init();
            Title = mode == Mode.Uninstall ? L.F("Disinstalla {0}", entry.Name) : L.F("Dati di {0} sul PC", entry.Name);
            Subtitle = mode == Mode.Uninstall ? e.UninstallInfo
                : L.F("{0} Puoi eliminare ciò che selezioni.", (entry.IsGame ? L.T("Salvataggi, impostazioni e tracce trovati nel sistema.") : L.T("Impostazioni, dati e tracce trovati nel sistema.")));
            if (mode == Mode.Uninstall) PrepareConfirm();
            else { var t = ScanAsync(); }
        }

        internal CleanupDialogViewModel(List<LeftoverItem> items, bool backup, bool recycle, int leftUnselected, AppKind kind, string title = null)
        {
            mode = Mode.Orphans;
            this.kind = kind;
            orphanLeft = leftUnselected;
            Init();
            Title = title ?? (kind == AppKind.Game ? L.T("Pulizia dei residui dei giochi") : L.T("Pulizia dei residui dei programmi"));
            Subtitle = Fmt.Count(items.Count, "elemento selezionato", "elementi selezionati");
            backupEnabled = backup;
            useRecycleBin = recycle;
            var t = CleanAsync(items);
        }

        /// <summary>Residui rimasti dopo una disinstallazione, aperti dalla cronologia della sorveglianza.</summary>
        internal CleanupDialogViewModel(AppEntry e, List<LeftoverItem> items, string historyId)
        {
            mode = Mode.Leftovers;
            entry = e;
            kind = e.Kind;
            allEntries = new List<AppEntry>();
            this.historyId = historyId;
            Init();
            Title = L.F("Residui di {0}", e.Name);
            Subtitle = L.T("Ciò che è rimasto dopo la disinstallazione. Scegli cosa eliminare.");
            ShowItems(items);
        }

        private bool AfterUninstall { get { return mode == Mode.Uninstall || mode == Mode.Leftovers; } }

        private void Init()
        {
            Groups = new ObservableCollection<ItemGroupViewModel>();
            Errors = new ObservableCollection<string>();
            backupEnabled = AppSettings.Current.BackupBeforeClean;
            useRecycleBin = AppSettings.Current.UseRecycleBin;
            StartCommand = new RelayCommand(() => { var t = StartAsync(); });
            ContinueCommand = new RelayCommand(() => { if (session != null) session.ForceContinue(); OnUninstallFinished(); });
            ForceCommand = new RelayCommand(() => { StopPolling(); var t = ScanAsync(); });
            CancelCommand = new RelayCommand(Close);
            CloseCommand = new RelayCommand(Close);
            CleanCommand = new RelayCommand(() => { var t = CleanAsync(null); }, () => Groups.Any(g => g.CheckedCount > 0));
            SelectAllCommand = new RelayCommand(() => SetAll(true));
            SelectNoneCommand = new RelayCommand(() => SetAll(false));
            OpenBackupCommand = new RelayCommand(() => { if (backupPath != null) ShellActions.Reveal(backupPath); });
        }

        internal Action<bool> Closed;

        private string Noun { get { return kind == AppKind.Game ? L.T("gioco") : L.T("programma"); } }
        private string TheNoun { get { return kind == AppKind.Game ? L.T("il gioco") : L.T("il programma"); } }

        public string Title { get; private set; }
        public string Subtitle { get; private set; }
        public ImageSource Cover { get { return entryVm == null ? null : entryVm.Thumbnail; } }
        public bool HasCover { get { return Cover != null; } }

        /// <summary>Copertina verticale (giochi) o icona quadrata (programmi).</summary>
        public bool HasPortrait { get { return HasCover && Images.IsPortrait(Cover); } }
        public bool HasSquareIcon { get { return HasCover && !HasPortrait; } }
        public string Initials { get { return entryVm == null ? "" : entryVm.Initials; } }
        public bool IsUninstallMode { get { return mode == Mode.Uninstall; } }

        public ObservableCollection<ItemGroupViewModel> Groups { get; private set; }
        public ObservableCollection<string> Errors { get; private set; }

        public ICommand StartCommand { get; private set; }
        public ICommand ContinueCommand { get; private set; }
        public ICommand ForceCommand { get; private set; }
        public ICommand CancelCommand { get; private set; }
        public ICommand CloseCommand { get; private set; }
        public ICommand CleanCommand { get; private set; }
        public ICommand SelectAllCommand { get; private set; }
        public ICommand SelectNoneCommand { get; private set; }
        public ICommand OpenBackupCommand { get; private set; }

        public string Step
        {
            get { return step; }
            private set { if (Set(ref step, value)) Raise("IsConfirm", "IsUninstalling", "IsScanning", "IsReview", "IsCleaning", "IsDone", "IsBusy"); }
        }

        public bool IsConfirm { get { return step == "confirm"; } }
        public bool IsUninstalling { get { return step == "uninstall"; } }
        public bool IsScanning { get { return step == "scan"; } }
        public bool IsReview { get { return step == "review"; } }
        public bool IsCleaning { get { return step == "clean"; } }
        public bool IsDone { get { return step == "done"; } }
        public bool IsBusy { get { return IsScanning || IsCleaning; } }

        public string Status { get { return status; } private set { Set(ref status, value); } }
        public string Hint { get { return hint; } private set { Set(ref hint, value); } }
        public string Warning { get { return warning; } private set { if (Set(ref warning, value)) Raise("HasWarning"); } }
        public bool HasWarning { get { return !string.IsNullOrEmpty(warning); } }
        public bool CanContinue { get { return canContinue; } private set { Set(ref canContinue, value); } }
        public bool CanForce { get { return canForce; } private set { Set(ref canForce, value); } }
        public string ProgressText { get { return progressText; } private set { Set(ref progressText, value); } }
        public double Progress { get { return progress; } private set { Set(ref progress, value); } }
        public string SelectionText { get { return selectionText; } private set { Set(ref selectionText, value); } }
        public string ReviewNote { get { return reviewNote; } private set { if (Set(ref reviewNote, value)) Raise("HasReviewNote"); } }
        public bool HasReviewNote { get { return !string.IsNullOrEmpty(reviewNote); } }
        public string ResultTitle { get { return resultTitle; } private set { Set(ref resultTitle, value); } }
        public string ResultText { get { return resultText; } private set { Set(ref resultText, value); } }
        public string BackupPath { get { return backupPath; } private set { if (Set(ref backupPath, value)) Raise("HasBackup"); } }
        public bool HasBackup { get { return backupPath != null; } }
        public bool HasErrors { get { return Errors.Count > 0; } }
        public bool HasItems { get { return Groups.Count > 0; } }
        public string CleanButtonText { get { return AfterUninstall ? L.T("Elimina i residui selezionati") : L.T("Elimina i selezionati"); } }
        public string ForceTip { get { return L.F("Elimina la cartella e i residui. Prima vedrai l'elenco.", Noun); } }

        public bool BackupEnabled
        {
            get { return backupEnabled; }
            set { if (Set(ref backupEnabled, value)) UpdateSelection(); }
        }

        public bool UseRecycleBin { get { return useRecycleBin; } set { Set(ref useRecycleBin, value); } }

        public string ConfirmText
        {
            get
            {
                if (entry == null) return null;
                string first;
                switch (entry.Launcher)
                {
                    case LauncherKind.Steam: first = L.F("Steam ti chiederà di confermare e rimuoverà {0}.", TheNoun); break;
                    case LauncherKind.Epic: first = L.T("I file del gioco vengono eliminati definitivamente e il gioco viene tolto dalla libreria di Epic Games."); break;
                    case LauncherKind.Xbox: first = L.T("Windows rimuoverà il pacchetto del gioco, come da Impostazioni > App."); break;
                    case LauncherKind.Store: first = L.T("Windows rimuoverà l'app, come da Impostazioni > App > App installate."); break;
                    default:
                        if (entry.HasUninstaller) first = L.F("Si aprirà il programma di disinstallazione di {0}: segui le sue istruzioni.", entry.Name);
                        else if (entry.IsSingleFile) first = L.T("Programma portable, senza disinstallazione: il suo eseguibile viene proposto per l'eliminazione insieme ai residui.");
                        else first = L.F("{0} è portable, senza disinstallazione: la cartella viene proposta per l'eliminazione insieme ai residui.", (entry.IsGame ? L.T("Il gioco") : L.T("Il programma")));
                        break;
                }
                string what = entry.IsGame ? L.T("salvataggi, impostazioni, screenshot, voci di registro e collegamenti rimasti")
                                           : L.T("impostazioni, dati, voci di registro, avvii automatici, servizi, tipi di file e collegamenti rimasti");
                return L.F("{0}\n\nAl termine AppManager cerca {1} e mostra l'elenco prima di eliminare qualsiasi cosa.", first, what);
            }
        }

        // ------------------------------------------------------------------ Conferma e disinstallazione

        private void PrepareConfirm()
        {
            Step = "confirm";
            var running = UninstallSession.RunningFrom(entry);
            if (running.Length > 0)
                Warning = L.F("{0} sembra in esecuzione ({1}): chiudilo prima di continuare.", (entry.IsGame ? L.T("Il gioco") : L.T("Il programma")), string.Join(", ", running));
            else if (entry.Launcher == LauncherKind.Epic && EpicInfo.LauncherProcesses().Length > 0)
                Warning = L.T("Epic Games Launcher è aperto: verrà chiuso per poter aggiornare la sua libreria.");
            else if (entry.IsComponent)
                Warning = L.F("«{0}» è un componente usato da altri programmi o da Windows ({1}): rimuovilo solo se sai che non serve più.", entry.Name, (entry.ComponentLabel ?? L.T("componente")));
            else if (!Native.IsElevated)
                Warning = L.T("Senza amministratore, alcuni residui di sistema potrebbero restare.");
            if (entry.IsGame)
            {
                // i profili messi da parte sono nel deposito di AppManager: la disinstallazione non li tocca, ma è bene saperlo
                try
                {
                    var st = SaveVault.Load(entry);
                    int stored = st.Profiles.Count(p => p.Id != st.ActiveProfile);
                    if (stored > 0)
                    {
                        string note = L.F("Profili di salvataggio messi da parte: {0}. Restano in {1} e li ritrovi se reinstalli il gioco.", stored, SaveVault.Root);
                        Warning = Warning == null ? note : Warning + "\n\n" + note;
                    }
                }
                catch (Exception ex) { Log.Warn("Profili di salvataggio di {0} non letti: {1}", entry.Name, ex.Message); }
            }
        }

        private async Task StartAsync()
        {
            // la disinstallazione parte solo dalla sua finestra di conferma, mai da un'analisi o dalla modalità di verifica grafica
            if (mode != Mode.Uninstall || AppSettings.SaveDisabled) return;
            var running = UninstallSession.RunningFrom(entry);
            if (running.Length > 0)
            {
                Warning = L.F("{0} è ancora in esecuzione ({1}): chiudilo e riprova.", (entry.IsGame ? L.T("Il gioco") : L.T("Il programma")), string.Join(", ", running));
                return;
            }
            Warning = null;
            Step = "uninstall";
            Status = L.T("Preparazione…");
            Hint = L.F("Raccolgo le informazioni necessarie a trovare i residui.", Noun);
            // l'impronta va calcolata PRIMA della disinstallazione, finché i file esistono
            var e = entry;
            profile = await Task.Run(() =>
            {
                var p = ProfileBuilder.Get(e);
                if (AppSettings.Current.UsePrefetch) { var r = PrefetchIndex.Records; }
                return p;
            });

            if (entry.Launcher == LauncherKind.Epic)
            {
                foreach (var p in EpicInfo.LauncherProcesses())
                {
                    try { p.CloseMainWindow(); if (!p.WaitForExit(3000)) p.Kill(); }
                    catch (Exception ex) { Log.Error("Chiusura Epic Games Launcher", ex); }
                }
            }

            if (!entry.HasUninstaller)
            {
                var t = ScanAsync();
                return;
            }
            // i residui li cerca questa finestra: la sorveglianza in background non deve avvisare una seconda volta
            InstallWatcher.ExpectUninstall(entry);
            session = UninstallSession.Start(entry, cts.Token);
            UpdateFromSession();
            if (session.State == UninstallState.Failed) return;
            changed = true;
            pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            pollTimer.Tick += (s, ev) =>
            {
                UninstallState st;
                try { st = session.Poll(); }
                catch (Exception ex)
                {
                    Log.Error("Controllo disinstallazione", ex);
                    st = UninstallState.Running;
                }
                UpdateFromSession();
                if (st == UninstallState.Completed) OnUninstallFinished();
            };
            pollTimer.Start();
        }

        private void UpdateFromSession()
        {
            if (session == null) return;
            Status = session.Status;
            Hint = session.Hint;
            CanContinue = session.State == UninstallState.WaitingUser;
            CanForce = session.CanForce;
        }

        private void StopPolling()
        {
            if (pollTimer != null) { pollTimer.Stop(); pollTimer = null; }
        }

        private void OnUninstallFinished()
        {
            StopPolling();
            if (session != null) { session.Dispose(); session = null; }
            var t = ScanAsync();
        }

        // ------------------------------------------------------------------ Ricerca dei residui

        private async Task ScanAsync()
        {
            Step = "scan";
            ProgressText = L.T("Cerco i residui…");
            var ct = cts.Token;
            var prog = new Progress<string>(s => ProgressText = s);
            bool after = mode == Mode.Uninstall;
            var e = entry;
            List<LeftoverItem> items;
            try
            {
                items = await Task.Run(() =>
                {
                    var p = profile ?? ProfileBuilder.Get(e);
                    var others = allEntries.Where(x => x.Id != e.Id).Select(ProfileBuilder.Get).ToList();
                    return LeftoverScanner.Scan(p, others, after, ((IProgress<string>)prog).Report, ct);
                }, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Error("Scansione residui", ex);
                ShowDone(L.T("Ricerca non riuscita"), ex.Message);
                return;
            }
            // la cronologia della sorveglianza dice anche cosa è rimasto delle disinstallazioni fatte qui
            if (mode == Mode.Uninstall)
            {
                try { historyId = MonitorHistory.RecordUninstallByApp(entry, items); }
                catch (Exception ex) { Log.Error("Cronologia della disinstallazione", ex); }
            }
            ShowItems(items);
        }

        private void ShowItems(List<LeftoverItem> items)
        {
            Groups.Clear();
            foreach (var cat in Categories.Order)
            {
                var catItems = items.Where(i => i.Category == cat).ToList();
                if (catItems.Count == 0) continue;
                bool check = AfterUninstall;
                var g = new ItemGroupViewModel(L.T(Categories.Label(cat, kind)), Categories.Glyph(cat),
                    catItems.Select(i => new ItemViewModel(i, check && i.Confidence >= Confidence.Medium)));
                g.IsExpanded = cat != ItemCategory.Registry && cat != ItemCategory.Cache && catItems.Count <= 12;
                g.Changed = UpdateSelection;
                Groups.Add(g);
            }
            Raise("HasItems");
            if (Groups.Count == 0)
            {
                ShowDone(AfterUninstall ? L.T("Disinstallazione completata") : L.T("Nessun dato trovato"),
                    AfterUninstall ? L.F("{0} è stato rimosso completamente: non ci sono residui da eliminare.", entry.Name)
                                           : L.F("Non ho trovato dati di questo {0} fuori dalla sua cartella.", Noun));
                return;
            }
            UpdateSelection();
            Step = "review";
        }

        private void SetAll(bool value)
        {
            foreach (var g in Groups) g.IsChecked = value;
        }

        private void UpdateSelection()
        {
            int count = Groups.Sum(g => g.CheckedCount);
            long size = Groups.Sum(g => g.CheckedSize);
            SelectionText = count == 0 ? L.T("Nessun elemento selezionato") : Fmt.Count(count, "elemento selezionato", "elementi selezionati") + (size > 0 ? " · " + Fmt.Size(size) : "");
            var sel = Groups.SelectMany(g => g.Items).Where(i => i.IsChecked).Select(i => i.Item).ToList();
            if (sel.Any(i => i.Category == ItemCategory.Saves) && !backupEnabled)
                ReviewNote = kind == AppKind.Game ? L.T("Stai eliminando salvataggi senza backup. Se pensi di reinstallare il gioco, attiva il backup.")
                                                  : L.T("Stai eliminando documenti o dati senza backup. Se pensi di reinstallare il programma, attiva il backup.");
            else if (sel.Any(i => i.RequiresAdmin) && !Native.IsElevated)
                ReviewNote = L.T("Alcuni elementi richiedono l'amministratore: riavvia AppManager come amministratore per eliminarli.");
            else if (sel.Any(i => i.Confidence == Confidence.Low))
                ReviewNote = L.F("Hai selezionato elementi «Da verificare»: controlla che appartengano davvero a questa applicazione.", Noun);
            else
                ReviewNote = null;
            CommandManager.InvalidateRequerySuggested();
        }

        // ------------------------------------------------------------------ Pulizia

        private async Task CleanAsync(List<LeftoverItem> preset)
        {
            var items = preset ?? Groups.SelectMany(g => g.Items).Where(i => i.IsChecked).Select(i => i.Item).ToList();
            if (items.Count == 0) return;
            Step = "clean";
            Progress = 0;
            ProgressText = L.T("Preparazione…");
            var opt = new CleanOptions
            {
                UseRecycleBin = useRecycleBin,
                Backup = backupEnabled,
                BackupFolder = AppSettings.Current.BackupFolder,
                Title = entry != null ? entry.Name : (kind == AppKind.Game ? "Residui dei giochi " : "Residui dei programmi ") + DateTime.Now.ToString("d MMM yyyy", Fmt.It),
                // con un'app precisa il backup porta il suo nome e la sua immagine; per i residui ogni gruppo ha il suo backup
                AppName = entry != null ? entry.Name : null,
                AppId = entry != null ? entry.Id : null,
                AppKind = kind,
                Reason = AfterUninstall ? "Disinstallazione" : mode == Mode.Analyze ? "Pulizia dei dati" : "Residui",
                ImagePng = entryVm != null ? Images.ToPng(entryVm.Thumbnail, 200) : null,
                ImageFile = entry != null ? entry.CoverImage : null,
            };
            var prog = new Progress<Tuple<string, double>>(t => { ProgressText = t.Item1; Progress = t.Item2; });
            CleanResult res;
            try
            {
                res = await Task.Run(() => Cleaner.Clean(items, opt, (s, d) => ((IProgress<Tuple<string, double>>)prog).Report(Tuple.Create(s, d)), cts.Token));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Error("Pulizia", ex);
                ShowDone(L.T("Pulizia non riuscita"), ex.Message);
                return;
            }
            changed = true;
            if (historyId != null)
            {
                string id = historyId;
                var t = Task.Run(() => MonitorHistory.Recheck(id));
            }
            if (entry != null) ProfileBuilder.Forget(entry);
            if (res.BackupPaths.Count > 0) BackupCatalog.Invalidate();
            foreach (var e in res.Errors.Take(50)) Errors.Add(e);
            if (res.Errors.Count > 50) Errors.Add(L.F("…e altri {0} errori (vedi il registro attività).", (res.Errors.Count - 50)));
            Raise("HasErrors");
            BackupPath = res.BackupPath;
            // nel Cestino ogni applicazione è una cartella «AppManager - nome»: si dice quale cercare
            var binNames = res.RecycleFolders.Select(f => "«" + System.IO.Path.GetFileName(f) + "»").Distinct().ToList();
            string where = binNames.Count == 1 ? L.F(" (nel Cestino, nella cartella {0})", binNames[0])
                : binNames.Count > 1 ? L.F(" (nel Cestino, nelle cartelle {0}{1})", string.Join(", ", binNames.Take(4)), (binNames.Count > 4 ? "…" : ""))
                : useRecycleBin && res.KeptFolders.Count == 0 ? L.T(" (i file sono nel Cestino)") : "";
            string text = res.Removed == 0 ? L.T("Non è stato eliminato nulla.") :
                L.F("Eliminati: {0}{1}{2}.", Fmt.Count(res.Removed, "elemento", "elementi"), res.FreedBytes > 0 ? L.F(", liberati {0}", Fmt.Size(res.FreedBytes)) : "", where);
            if (res.BackupPaths.Count > 1) text += L.F("\nBackup creati: {0}, uno per applicazione. Li trovi nella sezione Backup.", res.BackupPaths.Count);
            else if (res.BackupPath != null) text += L.T("\nBackup creato: puoi ripristinarlo dalla sezione Backup.");
            int left = mode == Mode.Orphans ? orphanLeft : Groups.Sum(g => g.Items.Count(i => !i.IsChecked));
            if (left > 0) text += L.F("\n{0} sul PC, come avevi scelto.", Fmt.Count(left, "elemento non selezionato è rimasto", "elementi non selezionati sono rimasti"));
            if (res.Notes.Count > 0)
                text += "\n\n" + string.Join("\n", res.Notes.Take(8)) + (res.Notes.Count > 8 ? L.F("\n…e altre {0} note nel registro attività.", (res.Notes.Count - 8)) : "");
            foreach (var n in res.Notes) Log.Info("Nota: {0}", n);
            ShowDone(res.Cancelled ? L.T("Pulizia interrotta") : res.Errors.Count == 0 ? L.T("Pulizia completata") : L.T("Pulizia completata con alcuni errori"), text);
        }

        private void ShowDone(string title, string text)
        {
            ResultTitle = title;
            ResultText = text;
            Step = "done";
        }

        bool IDialogKeys.OnEscape()
        {
            // mai durante disinstallazione, analisi o pulizia: si interromperebbe il lavoro a metà
            if (!IsConfirm && !IsReview && !IsDone) return false;
            Close();
            return true;
        }

        bool IDialogKeys.OnEnter()
        {
            if (!IsDone) return false;
            Close();
            return true;
        }

        private void Close()
        {
            StopPolling();
            cts.Cancel();
            if (session != null) { session.Dispose(); session = null; }
            if (Closed != null) Closed(changed);
        }
    }
}
