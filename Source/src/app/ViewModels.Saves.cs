using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AppManager
{
    /// <summary>
    /// Dialogo che può riaprirsi dopo una conferma o un messaggio: MainViewModel.Confirm/Message lo rimettono
    /// al suo posto invece di chiudere tutto.
    /// </summary>
    internal interface IReturnableDialog { }

    // ====================================================================== Salvataggi di un gioco: backup, profili, nuova partita

    public sealed class SaveLocationViewModel : ObservableObject
    {
        internal readonly SaveLocationInfo Info;

        internal SaveLocationViewModel(SaveLocationInfo info, SavesDialogViewModel owner)
        {
            Info = info;
            RevealCommand = new RelayCommand(() => ShellActions.Reveal(info.Path));
            RemoveCommand = new RelayCommand(() => owner.RemoveLocation(this), () => !owner.IsBusy);
        }

        public string Path { get { return Info.Path; } }
        public string KindLabel { get { return Info.Category == ItemCategory.Settings ? L.T("Impostazioni") : L.T("Salvataggi"); } }
        public string KindColor { get { return Info.Category == ItemCategory.Settings ? "#5B8CFF" : "#3DD68C"; } }

        public string Detail
        {
            get
            {
                var parts = new List<string>();
                if (!Info.Exists) parts.Add(L.T("vuota"));
                else
                {
                    parts.Add(Info.Size > 0 ? Fmt.Size(Info.Size) : L.T("vuota"));
                    if (Info.FileCount > 0) parts.Add(Fmt.Count(Info.FileCount, "file", "file"));
                    if (Info.LastWrite.HasValue) parts.Add(L.F("modificati il {0}", Fmt.Date(Info.LastWrite)));
                }
                if (Info.Manual) parts.Add(L.T("aggiunta a mano"));
                else if (Info.Confidence < Confidence.High) parts.Add(L.T("da verificare"));
                return string.Join(" · ", parts);
            }
        }

        public string Reason { get { return Info.Reason; } }
        public ICommand RevealCommand { get; private set; }
        public ICommand RemoveCommand { get; private set; }
    }

    public sealed class SaveProfileViewModel : ObservableObject
    {
        internal readonly SaveProfile Profile;
        private readonly SavesDialogViewModel owner;
        private bool isRenaming;
        private string editName;
        private string sizeText;

        internal SaveProfileViewModel(SaveProfile p, bool active, SavesDialogViewModel owner)
        {
            Profile = p;
            IsActive = active;
            this.owner = owner;
            UseCommand = new RelayCommand(() => owner.SwitchTo(this), () => !owner.IsBusy && !IsActive);
            RenameCommand = new RelayCommand(() => { EditName = Profile.Name; IsRenaming = true; }, () => !owner.IsBusy);
            ConfirmRenameCommand = new RelayCommand(() => { owner.Rename(this, EditName); IsRenaming = false; });
            CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
            DeleteCommand = new RelayCommand(() => owner.DeleteProfile(this), () => !owner.IsBusy && !IsActive);
        }

        public string Name { get { return Profile.Name; } }
        public bool IsActive { get; private set; }
        public string Initial { get { return string.IsNullOrEmpty(Profile.Name) ? "?" : Profile.Name.Substring(0, 1).ToUpperInvariant(); } }

        public string StatusText
        {
            get
            {
                if (IsActive) return L.T("In uso: i suoi file sono nelle cartelle del gioco");
                var parts = new List<string> { L.T("Messo da parte") };
                if (sizeText != null) parts.Add(sizeText);
                var used = Profile.LastUsedDate;
                if (used > DateTime.MinValue) parts.Add(L.F("usato l'ultima volta il {0}", used.ToString("d MMM yyyy", Fmt.It)));
                return string.Join(" · ", parts);
            }
        }

        internal void SetSize(long bytes)
        {
            sizeText = bytes > 0 ? Fmt.Size(bytes) : L.T("vuoto (nuova partita)");
            Raise("StatusText");
        }

        public bool IsRenaming { get { return isRenaming; } set { Set(ref isRenaming, value); } }
        public string EditName { get { return editName; } set { Set(ref editName, value); } }

        public ICommand UseCommand { get; private set; }
        public ICommand RenameCommand { get; private set; }
        public ICommand ConfirmRenameCommand { get; private set; }
        public ICommand CancelRenameCommand { get; private set; }
        public ICommand DeleteCommand { get; private set; }
    }

    public sealed class SaveBackupViewModel : ObservableObject
    {
        internal readonly BackupInfo Info;

        internal SaveBackupViewModel(BackupInfo info, SavesDialogViewModel owner)
        {
            Info = info;
            RestoreCommand = new RelayCommand(() => owner.RestoreBackup(this), () => !owner.IsBusy);
            RevealCommand = new RelayCommand(() => ShellActions.Reveal(info.ZipPath));
        }

        public string DateText { get { return Info.Created.ToString("dddd d MMMM yyyy, HH:mm", Fmt.It); } }
        public string SizeText { get { return Fmt.Size(Info.Size); } }
        public string ReasonLabel { get { return Info.Reason == SaveVault.BeforeRestoreReason ? L.T("Automatico, prima di un ripristino") : L.T("Backup dei salvataggi"); } }
        public ICommand RestoreCommand { get; private set; }
        public ICommand RevealCommand { get; private set; }
    }

    public sealed class SavesDialogViewModel : ObservableObject, IDialogKeys, IReturnableDialog
    {
        private readonly EntryViewModel entryVm;
        private readonly AppEntry game;
        private SaveVaultState state;
        private bool isBusy, isLoading, includeSettings, nameEdited;
        private string progressText, newProfileName, warning;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private bool changed;
        private int loadVersion;

        internal SavesDialogViewModel(EntryViewModel e)
        {
            entryVm = e;
            game = e.Entry;
            Locations = new ObservableCollection<SaveLocationViewModel>();
            Profiles = new ObservableCollection<SaveProfileViewModel>();
            Backups = new ObservableCollection<SaveBackupViewModel>();
            CloseCommand = new RelayCommand(Close, () => !isBusy);
            AddLocationCommand = new RelayCommand(AddLocation, () => !isBusy);
            BackupCommand = new RelayCommand(() => { var t = BackupNowAsync(); }, () => !isBusy && !isLoading && Locations.Count > 0);
            NewGameCommand = new RelayCommand(NewGame, () => !isBusy && !isLoading && Locations.Count > 0);
            RefreshCommand = new RelayCommand(() => { var t = LoadAsync(); }, () => !isBusy && !isLoading);
            AllBackupsCommand = new RelayCommand(() => { Close(); MainViewModel.Current.Navigate("backups"); }, () => !isBusy);
            OpenVaultCommand = new RelayCommand(() =>
            {
                try
                {
                    string dir = SaveVault.GameDir(game);
                    ShellActions.OpenFolder(Directory.Exists(dir) ? dir : SaveVault.Root);
                }
                catch (Exception ex) { MainViewModel.Current.Message(L.T("Cartella non disponibile"), ex.Message); }
            });
            var t0 = LoadAsync();
        }

        internal Action<bool> Closed;

        public string Title { get { return L.F("Salvataggi di {0}", game.Name); } }
        public string Subtitle { get { return L.T("Backup, profili per più giocatori o impostazioni diverse, nuova partita senza perdere quella vecchia."); } }
        public ImageSource Cover { get { return entryVm.Thumbnail; } }
        public bool HasCover { get { return Cover != null; } }
        public bool HasPortrait { get { return HasCover && Images.IsPortrait(Cover); } }
        public bool HasSquareIcon { get { return HasCover && !HasPortrait; } }

        public ObservableCollection<SaveLocationViewModel> Locations { get; private set; }
        public ObservableCollection<SaveProfileViewModel> Profiles { get; private set; }
        public ObservableCollection<SaveBackupViewModel> Backups { get; private set; }

        public bool IsBusy { get { return isBusy; } private set { if (Set(ref isBusy, value)) { Raise("ShowContent"); CommandManager.InvalidateRequerySuggested(); } } }
        public bool IsLoading { get { return isLoading; } private set { if (Set(ref isLoading, value)) { Raise("ShowContent", "HasNoLocations"); CommandManager.InvalidateRequerySuggested(); } } }
        public bool ShowContent { get { return !isLoading; } }
        public string ProgressText { get { return progressText; } private set { Set(ref progressText, value); } }
        public bool HasNoLocations { get { return !isLoading && Locations.Count == 0; } }
        public bool HasProfiles { get { return Profiles.Count > 1; } }
        public bool HasBackups { get { return Backups.Count > 0; } }
        public string Warning { get { return warning; } private set { if (Set(ref warning, value)) Raise("HasWarning"); } }
        public bool HasWarning { get { return !string.IsNullOrEmpty(warning); } }

        /// <summary>Nome del nuovo profilo: proposto da solo («Profilo N» libero) finché non lo scrivi tu.</summary>
        public string NewProfileName { get { return newProfileName; } set { if (Set(ref newProfileName, value)) nameEdited = true; } }

        private string SuggestedName()
        {
            return SaveVault.FreeProfileName(state == null ? new string[0] : state.Profiles.Select(p => p.Name));
        }

        public string ActiveProfileText
        {
            get
            {
                var a = state == null ? null : state.Active;
                return a == null ? L.F("Nessun profilo: i salvataggi attuali diventeranno il profilo «{0}» al primo cambio.", L.T("Principale")) : L.F("Profilo in uso: {0}", a.Name);
            }
        }

        /// <summary>Includere anche le impostazioni nei backup e nei profili (così ogni profilo ha le sue).</summary>
        public bool IncludeSettings
        {
            get { return includeSettings; }
            set
            {
                if (isBusy || !Set(ref includeSettings, value)) return;
                // la preferenza è nelle impostazioni: attivarla non crea cartelle nel deposito
                SaveVault.SetIncludesSettings(game, value);
                var t = LoadAsync();
            }
        }

        public ICommand CloseCommand { get; private set; }
        public ICommand AddLocationCommand { get; private set; }
        public ICommand BackupCommand { get; private set; }
        public ICommand NewGameCommand { get; private set; }
        public ICommand RefreshCommand { get; private set; }
        public ICommand AllBackupsCommand { get; private set; }
        public ICommand OpenVaultCommand { get; private set; }

        private List<AppEntry> Others()
        {
            return MainViewModel.Current.AllDetected().Where(x => x.Id != game.Id).ToList();
        }

        // ------------------------------------------------------------------ Caricamento

        private async Task LoadAsync()
        {
            int version = ++loadVersion;
            IsLoading = true;
            ProgressText = L.F("Cerco i salvataggi di {0}…", game.Name);
            var others = Others();
            var prog = new Progress<string>(s => ProgressText = s);
            bool withSettings = SaveVault.IncludesSettings(game);
            try
            {
                var result = await Task.Run(() =>
                {
                    var st = SaveVault.Load(game);
                    // deposito rimasto senza profili messi da parte (versione precedente): non serve, si toglie
                    SaveVault.RemoveIfUnused(game, st);
                    var locs = SaveVault.FindLocations(game, others, withSettings || st.IncludeSettings, ((IProgress<string>)prog).Report, cts.Token);
                    var backups = Cleaner.ListBackups(AppSettings.Current.BackupFolder)
                        .Where(b => (b.AppId == game.Id || string.Equals(b.AppName, game.Name, StringComparison.OrdinalIgnoreCase))
                                    && (b.Reason == SaveVault.SavesReason || b.Reason == SaveVault.BeforeRestoreReason))
                        .OrderByDescending(b => b.Created).Take(20).ToList();
                    var sizes = st.Profiles.ToDictionary(p => p.Id, p => p.Id == st.ActiveProfile ? 0 : SaveVault.StoredSize(game, p));
                    string running = string.Join(", ", UninstallSession.RunningFrom(game));
                    return new LoadResult { State = st, Locations = locs, Backups = backups, Sizes = sizes, Running = running };
                }, cts.Token);
                // un caricamento più recente (es. cambio dell'opzione impostazioni) ha la precedenza
                if (version == loadVersion) Apply(result);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error(L.F("Salvataggi di {0}", game.Name), ex);
                if (version == loadVersion) Warning = L.F("Analisi non riuscita: {0}", ex.Message);
            }
            finally
            {
                if (version == loadVersion) IsLoading = false;
            }
        }

        private sealed class LoadResult
        {
            public SaveVaultState State;
            public List<SaveLocationInfo> Locations;
            public List<BackupInfo> Backups;
            public Dictionary<string, long> Sizes;
            public string Running;
        }

        private void Apply(LoadResult r)
        {
            state = r.State;
            // la preferenza dei depositi della versione 2.1 passa nelle impostazioni
            if (state.IncludeSettings && !SaveVault.IncludesSettings(game)) SaveVault.SetIncludesSettings(game, true);
            includeSettings = SaveVault.IncludesSettings(game);
            Raise("IncludeSettings", "ActiveProfileText");
            Locations.Clear();
            foreach (var l in r.Locations) Locations.Add(new SaveLocationViewModel(l, this));
            Profiles.Clear();
            foreach (var p in state.Profiles.OrderByDescending(p => p.Id == state.ActiveProfile).ThenBy(p => p.CreatedDate))
            {
                var vm = new SaveProfileViewModel(p, p.Id == state.ActiveProfile, this);
                long size;
                if (!vm.IsActive && r.Sizes.TryGetValue(p.Id, out size)) vm.SetSize(size);
                Profiles.Add(vm);
            }
            Backups.Clear();
            foreach (var b in r.Backups) Backups.Add(new SaveBackupViewModel(b, this));
            // nome proposto sempre libero: dopo aver eliminato «Profilo 2» torna a essere proposto «Profilo 2»
            if (!nameEdited || string.IsNullOrWhiteSpace(newProfileName))
            {
                newProfileName = SuggestedName();
                nameEdited = false;
                Raise("NewProfileName");
            }

            var notes = new List<string>();
            if (r.Running.Length > 0) notes.Add(L.F("Il gioco è aperto ({0}): chiudilo prima di cambiare profilo o ripristinare un backup.", r.Running));
            string steam = SteamInfo.SteamPath;
            if (steam != null && r.Locations.Any(l => PathUtil.IsUnder(l.Path, Path.Combine(steam, "userdata"))))
                notes.Add(L.T("Alcuni salvataggi sono sincronizzati con Steam Cloud. Cambia profilo con Steam chiuso o disattiva Steam Cloud per questo gioco: Steam potrebbe riscaricare i salvataggi dell'altro profilo."));
            Warning = notes.Count == 0 ? null : string.Join("\n", notes);
            Raise("HasNoLocations", "HasProfiles", "HasBackups");
            CommandManager.InvalidateRequerySuggested();
        }

        // ------------------------------------------------------------------ Posizioni

        private void AddLocation()
        {
            var w = Application.Current.MainWindow;
            var owner = w == null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(w).Handle;
            string dir = Native.PickFolder(owner, L.F("Cartella dei salvataggi di {0}", game.Name), null);
            if (dir == null) return;
            string problem = SaveVault.CheckUsable(dir);
            if (problem != null)
            {
                MainViewModel.Current.Message(L.T("Cartella non utilizzabile"), problem);
                return;
            }
            if (!string.IsNullOrEmpty(game.InstallDir) && PathUtil.IsUnder(game.InstallDir, dir, true))
            {
                MainViewModel.Current.Message(L.T("Cartella non utilizzabile"), L.T("È la cartella del gioco (o la contiene): scegli solo la sottocartella con i salvataggi."));
                return;
            }
            SaveVault.AddManualLocation(game, dir);
            var t = LoadAsync();
        }

        internal void RemoveLocation(SaveLocationViewModel l)
        {
            MainViewModel.Current.Confirm(L.T("Non gestire più questa cartella?"),
                L.F("{0}\n\nAppManager non la includerà più nei backup e nei profili di {1}. I file restano dove sono.", l.Path, game.Name),
                L.T("Non gestire"), false, () =>
                {
                    SaveVault.RemoveLocation(game, l.Info);
                    var t = LoadAsync();
                });
        }

        // ------------------------------------------------------------------ Backup

        private byte[] Image() { return Images.ToPng(entryVm.Thumbnail, 200); }

        private async Task BackupNowAsync()
        {
            IsBusy = true;
            ProgressText = L.T("Creo il backup dei salvataggi…");
            var locs = Locations.Select(l => l.Info).ToList();
            var img = Image();
            try
            {
                string zip = await Task.Run(() => SaveVault.Backup(game, locs, SaveVault.SavesReason, img, CancellationToken.None));
                BackupCatalog.Invalidate();
                MainViewModel.Current.ShowToast(L.T("Backup dei salvataggi creato: lo trovi anche nella sezione Backup."));
                Log.Info("Salvataggi di {0}: backup {1}", game.Name, zip);
            }
            catch (Exception ex)
            {
                Log.Error(L.F("Backup dei salvataggi di {0}", game.Name), ex);
                MainViewModel.Current.Message(L.T("Backup non riuscito"), ex.Message);
            }
            finally { IsBusy = false; }
            await LoadAsync();
        }

        internal void RestoreBackup(SaveBackupViewModel b)
        {
            MainViewModel.Current.Confirm(L.F("Ripristinare i salvataggi del {0}?", b.Info.Created.ToString("d MMMM yyyy, HH:mm", Fmt.It)),
                L.T("Prima viene creato un backup automatico dei salvataggi attuali, poi vengono sostituiti con quelli scelti."), L.T("Ripristina"), false, () =>
                {
                    var img = Image();
                    var t = RunAsync(L.T("Ripristino dei salvataggi…"), () =>
                    {
                        var r = SaveVault.RestoreBackup(game, b.Info.ZipPath, img, (s, d) => Report(s));
                        return r.Errors.Count == 0 ? null : string.Join("\n", r.Errors.Take(8));
                    }, L.T("Salvataggi ripristinati."));
                });
        }

        // ------------------------------------------------------------------ Profili

        private void NewGame()
        {
            string name = (NewProfileName ?? "").Trim();
            if (name.Length == 0) name = SuggestedName();
            if (Profiles.Any(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            {
                MainViewModel.Current.Message(L.T("Nome già usato"), L.F("Esiste già un profilo chiamato «{0}»: scegli un altro nome.", name));
                return;
            }
            var active = state == null ? null : state.Active;
            MainViewModel.Current.Confirm(L.T("Iniziare una nuova partita?"),
                L.F("I salvataggi attuali passano nel profilo «{0}» e le cartelle del gioco restano vuote: {1} riparte da zero con il nuovo profilo «{2}».\n\nPuoi tornare al profilo precedente quando vuoi.", (active == null ? L.T("Principale") : active.Name), game.Name, name),
                L.T("Nuova partita"), false, () =>
                {
                    var locs = Locations.Select(l => l.Info).ToList();
                    var st = state;
                    var t = RunAsync(L.T("Metto da parte i salvataggi attuali…"), () =>
                    {
                        SaveVault.Switch(game, st, locs, null, name, Report);
                        return null;
                    }, L.F("Nuova partita pronta: profilo «{0}» in uso.", name));
                    nameEdited = false;   // al termine viene proposto il prossimo nome libero
                });
        }

        internal void SwitchTo(SaveProfileViewModel p)
        {
            var active = state == null ? null : state.Active;
            MainViewModel.Current.Confirm(L.F("Usare il profilo «{0}»?", p.Name),
                L.F("I salvataggi {0}attuali vengono messi da parte nel profilo «{1}», poi quelli di «{2}» tornano nelle cartelle del gioco.", (includeSettings ? L.T("e le impostazioni ") : ""), (active == null ? L.T("Principale") : active.Name), p.Name),
                L.T("Usa questo profilo"), false, () =>
                {
                    var locs = Locations.Select(l => l.Info).ToList();
                    var st = state;
                    var t = RunAsync(L.T("Cambio di profilo…"), () =>
                    {
                        SaveVault.Switch(game, st, locs, p.Profile.Id, null, Report);
                        return null;
                    }, L.F("Profilo «{0}» in uso.", p.Name));
                });
        }

        internal void Rename(SaveProfileViewModel p, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name == p.Name) return;
            if (Profiles.Any(x => x != p && string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            {
                MainViewModel.Current.Message(L.T("Nome già usato"), L.F("Esiste già un profilo chiamato «{0}».", name));
                return;
            }
            try
            {
                SaveVault.RenameProfile(game, state, p.Profile.Id, name);
                changed = true;
                var t = LoadAsync();
            }
            catch (Exception ex) { MainViewModel.Current.Message(L.T("Rinomina non riuscita"), ex.Message); }
        }

        internal void DeleteProfile(SaveProfileViewModel p)
        {
            MainViewModel.Current.Confirm(L.F("Eliminare il profilo «{0}»?", p.Name),
                L.T("I suoi salvataggi messi da parte vanno nel Cestino. Il profilo in uso non viene toccato."), L.T("Elimina profilo"), true, () =>
                {
                    var st = state;
                    var t = RunAsync(L.T("Elimino il profilo…"), () => { SaveVault.DeleteProfile(game, st, p.Profile.Id); return null; }, L.F("Profilo «{0}» eliminato.", p.Name));
                });
        }

        private void Report(string s)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() => ProgressText = s));
        }

        /// <summary>Esegue un'operazione in background con il dialogo bloccato; la funzione restituisce eventuali avvisi (null = tutto bene).</summary>
        private async Task RunAsync(string text, Func<string> work, string done)
        {
            IsBusy = true;
            ProgressText = text;
            string problems = null;
            bool ok = false;
            try
            {
                problems = await Task.Run(work);
                ok = true;
                changed = true;
            }
            catch (Exception ex)
            {
                Log.Error(L.F("Salvataggi di {0}", game.Name), ex);
                MainViewModel.Current.Message(L.T("Operazione non riuscita"), ex.Message);
            }
            finally { IsBusy = false; }
            BackupCatalog.Invalidate();   // ripristini e cambi di profilo possono creare backup automatici
            if (ok)
            {
                if (problems != null) MainViewModel.Current.Message(L.T("Completato con alcuni errori"), problems);
                else MainViewModel.Current.ShowToast(done);
            }
            entryVm.RefreshSaveProfile();
            await LoadAsync();
        }

        // ------------------------------------------------------------------ Chiusura e tastiera

        private void Close()
        {
            if (isBusy) return;
            cts.Cancel();
            if (Closed != null) Closed(changed);
        }

        bool IDialogKeys.OnEscape()
        {
            if (isBusy) return false;
            Close();
            return true;
        }

        bool IDialogKeys.OnEnter() { return false; }
    }
}
