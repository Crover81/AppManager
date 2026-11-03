using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AppManager
{
    // ====================================================================== Pagina Sorveglianza

    public sealed class MonitorPageViewModel : ObservableObject
    {
        public ObservableCollection<HistoryEventViewModel> Events { get; private set; }

        public MonitorPageViewModel()
        {
            Events = new ObservableCollection<HistoryEventViewModel>();
            ToggleCommand = new RelayCommand(Toggle);
            CheckNowCommand = new RelayCommand(() => { InstallWatcher.CheckNow(); MainViewModel.Current.ShowToast(L.T("Controllo avviato: se trovo cambiamenti compaiono qui.")); }, () => InstallWatcher.IsRunning);
            ClearCommand = new RelayCommand(Clear, () => Events.Count > 0);
        }

        public ICommand ToggleCommand { get; private set; }
        public ICommand CheckNowCommand { get; private set; }
        public ICommand ClearCommand { get; private set; }

        public bool IsEnabled { get { return AppSettings.Current.BackgroundMonitor; } }
        public string StateTitle { get { return IsEnabled ? L.T("Sorveglianza attiva") : L.T("Sorveglianza disattivata"); } }
        public string StateText
        {
            get
            {
                return IsEnabled
                    ? L.T("AppManager si avvia con Windows e resta nell'area di notifica. Dopo ogni disinstallazione cerca i residui e ti avvisa se ne trova.")
                    : L.T("Attivala per sapere cosa resta sul PC dopo ogni disinstallazione, anche quando la finestra di AppManager è chiusa.");
            }
        }

        public string StatusLine
        {
            get
            {
                if (!IsEnabled || !InstallWatcher.IsRunning) return null;
                string s = InstallWatcher.Status;
                var last = InstallWatcher.LastCheck;
                if (last.HasValue) s = (s == null ? "" : s + " · ") + L.F("ultimo controllo alle {0}", last.Value.ToString("HH:mm"));
                return s;
            }
        }

        public bool HasStatusLine { get { return !string.IsNullOrEmpty(StatusLine); } }
        public string ToggleText { get { return IsEnabled ? L.T("Disattiva") : L.T("Attiva"); } }
        public bool HasEvents { get { return Events.Count > 0; } }
        public string Scope { get { return L.T("Controlla i programmi installati e i giochi di Steam ed Epic. Le app di Microsoft Store non sono incluse."); } }

        internal void RaiseState()
        {
            Raise("IsEnabled", "StateTitle", "StateText", "StatusLine", "HasStatusLine", "ToggleText");
            CommandManager.InvalidateRequerySuggested();
        }

        internal void Reload()
        {
            var list = MonitorHistory.All();
            Events.Clear();
            foreach (var e in list) Events.Add(new HistoryEventViewModel(e, this));
            Raise("HasEvents");
            RaiseState();
        }

        private void Toggle()
        {
            string err = BackgroundService.SetEnabled(!IsEnabled);
            if (err != null)
                MainViewModel.Current.Message(L.T("Avvio con Windows non attivato"), L.F("Windows non ha accettato la richiesta: {0}", err));
            RaiseState();
        }

        private void Clear()
        {
            MainViewModel.Current.Confirm(L.T("Svuotare la cronologia?"),
                L.T("Le voci della cronologia vengono tolte. I residui sul PC restano dove sono: li ritrovi nelle pagine Residui."),
                L.T("Svuota"), true, MonitorHistory.Clear);
        }

        /// <summary>Apre l'elenco dei residui ancora presenti di una disinstallazione, per scegliere cosa eliminare.</summary>
        internal async void OpenReview(string id)
        {
            var main = MainViewModel.Current;
            var ev = MonitorHistory.Get(id);
            if (main == null || ev == null) return;
            List<LeftoverItem> items;
            try
            {
                items = await Task.Run(() =>
                {
                    var left = MonitorHistory.Recheck(id);
                    foreach (var i in left.Where(x => x.IsFileSystem))
                    {
                        int count;
                        DateTime newest;
                        i.Size = PathUtil.Size(i.Path, out count, out newest);
                        i.FileCount = count;
                    }
                    return left;
                });
            }
            catch (Exception ex)
            {
                Log.Error("Lettura dei residui dalla cronologia", ex);
                main.Message(L.T("Residui non letti"), ex.Message);
                return;
            }
            if (items.Count == 0)
            {
                main.Message(L.T("Non è rimasto nulla"), L.F("I residui di {0} non ci sono più: sono già stati eliminati.", ev.Name));
                return;
            }
            var entry = new AppEntry
            {
                Id = ev.AppId ?? "history:" + ev.Id, Kind = ev.IsGame ? AppKind.Game : AppKind.Program, Name = ev.Name, Publisher = ev.Publisher,
                CoverImage = ev.Image, Launcher = LauncherKind.Other,
            };
            var dlg = new CleanupDialogViewModel(entry, items, id);
            dlg.Closed = changed =>
            {
                if (main.Dialog == dlg) main.Dialog = null;
                if (changed) Task.Run(() => MonitorHistory.Recheck(id));
            };
            main.Dialog = dlg;
        }

        // ------------------------------------------------------------------ Dati finti per le prove grafiche

        internal static List<HistoryEvent> DemoEvents()
        {
            var now = DateTime.Now;
            Func<int, string, string, bool, string, List<StoredItem>, HistoryEvent> ev = (minutes, type, name, game, status, items) => new HistoryEvent
            {
                Id = Guid.NewGuid().ToString("N"), When = now.AddMinutes(-minutes).ToString("o"), Type = type, Name = name, IsGame = game,
                Source = game ? "steam" : "registry", Status = status, Items = items ?? new List<StoredItem>(), Version = "2.4.1", OldVersion = "2.3.0",
            };
            var some = new List<StoredItem>
            {
                new StoredItem { Kind = "Directory", Category = "Settings", Path = @"C:\Users\Demo\AppData\Roaming\MediaStudio", Confidence = 2, Size = 12500000 },
                new StoredItem { Kind = "RegistryKey", Category = "Registry", Path = @"HKCU\Software\MediaStudio", Confidence = 2 },
                new StoredItem { Kind = "Directory", Category = "Cache", Path = @"C:\Users\Demo\AppData\Local\MediaStudio", Confidence = 1, Size = 23000000 },
            };
            // nomi inventati, uguali in ogni lingua; uno lungo per la prova di impaginazione
            return new List<HistoryEvent>
            {
                ev(3, HistoryEvent.Uninstalled, "MediaStudio Pro Video Converter Suite Ultimate Edition 2026", false, HistoryEvent.Leftovers, some),
                ev(1, HistoryEvent.Uninstalled, "Speed Rally", true, HistoryEvent.Checking, null),
                ev(80, HistoryEvent.Uninstalled, "Space Colony", true, HistoryEvent.Clean, null),
                ev(60 * 26, HistoryEvent.Installed, "NoteWriter", false, null, null),
                ev(60 * 30, HistoryEvent.Updated, "TunePlayer", false, null, null),
                ev(60 * 24 * 5, HistoryEvent.Uninstalled, "PhotoFrame", false, HistoryEvent.Ignored, some.Take(1).ToList()),
                ev(60 * 24 * 9, HistoryEvent.Uninstalled, "Visual C++ Redistributable", false, HistoryEvent.Skipped, null),
            };
        }
    }

    public sealed class HistoryEventViewModel
    {
        internal readonly HistoryEvent Event;
        private readonly MonitorPageViewModel owner;

        internal HistoryEventViewModel(HistoryEvent e, MonitorPageViewModel owner)
        {
            Event = e;
            this.owner = owner;
            ReviewCommand = new RelayCommand(() => owner.OpenReview(Event.Id));
        }

        public ICommand ReviewCommand { get; private set; }
        public string Name { get { return Event.Name; } }

        private bool IsUninstall { get { return Event.Type == HistoryEvent.Uninstalled; } }

        public string Glyph
        {
            get { return Event.Type == HistoryEvent.Installed ? "\uE896" : Event.Type == HistoryEvent.Updated ? "\uE895" : "\uE74D"; }
        }

        public string Color
        {
            get
            {
                if (!IsUninstall) return "#5B8CFF";
                switch (Event.Status)
                {
                    case HistoryEvent.Leftovers: return "#F5B94B";
                    case HistoryEvent.Clean:
                    case HistoryEvent.Cleaned: return "#3DD68C";
                    case HistoryEvent.Checking: return "#5B8CFF";
                    default: return "#9AA4B2";
                }
            }
        }

        public string Subtitle
        {
            get
            {
                string type = Event.Type == HistoryEvent.Installed ? L.T("Installato") : Event.Type == HistoryEvent.Updated ? L.T("Aggiornato") : L.T("Disinstallato");
                var parts = new List<string> { type, When(Event.WhenDate) };
                string kind = Event.IsGame ? L.T("Gioco") : L.T("Programma");
                if (Event.Source == "steam") kind = L.F("{0} di Steam", kind);
                else if (Event.Source == "epic") kind = L.F("{0} di Epic Games", kind);
                parts.Add(kind);
                if (Event.ByAppManager) parts.Add(L.T("con AppManager"));
                return string.Join(" · ", parts);
            }
        }

        private static string When(DateTime d)
        {
            if (d == DateTime.MinValue) return "";
            if (d.Date == DateTime.Today) return L.F("oggi alle {0}", d.ToString("HH:mm"));
            if (d.Date == DateTime.Today.AddDays(-1)) return L.F("ieri alle {0}", d.ToString("HH:mm"));
            return L.F("{0} alle {1}", Fmt.Date(d), d.ToString("HH:mm"));
        }

        public string Detail
        {
            get
            {
                if (Event.Type == HistoryEvent.Installed)
                    return string.IsNullOrEmpty(Event.Version) ? null : L.F("Versione {0}", Event.Version);
                if (Event.Type == HistoryEvent.Updated)
                    return !string.IsNullOrEmpty(Event.OldVersion) && !string.IsNullOrEmpty(Event.Version) ? L.F("Dalla versione {0} alla {1}", Event.OldVersion, Event.Version) : null;
                switch (Event.Status)
                {
                    case HistoryEvent.Checking: return L.T("Cerco i residui…");
                    case HistoryEvent.Clean: return L.T("Rimosso completamente: nessun residuo.");
                    case HistoryEvent.Cleaned: return L.T("I residui sono stati eliminati.");
                    case HistoryEvent.Ignored: return BackgroundService.LeftoverSummary(Event) + " " + L.T("Avviso ignorato.");
                    case HistoryEvent.Skipped: return L.T("Componente di Windows o runtime: residui non cercati, i suoi file servono ad altri programmi.");
                    case HistoryEvent.Leftovers: return BackgroundService.LeftoverSummary(Event);
                    default: return null;
                }
            }
        }

        public bool HasDetail { get { return !string.IsNullOrEmpty(Detail); } }

        public string Badge
        {
            get
            {
                if (!IsUninstall) return null;
                switch (Event.Status)
                {
                    case HistoryEvent.Leftovers: return L.T("Da rivedere");
                    case HistoryEvent.Ignored: return L.T("Ignorato");
                    case HistoryEvent.Clean:
                    case HistoryEvent.Cleaned: return L.T("Pulito");
                    case HistoryEvent.Checking: return L.T("In corso");
                    default: return null;
                }
            }
        }

        public bool HasBadge { get { return Badge != null; } }
        public bool CanReview { get { return IsUninstall && (Event.Status == HistoryEvent.Leftovers || Event.Status == HistoryEvent.Ignored) && Event.ItemCount > 0; } }
    }

    // ====================================================================== Avviso a comparsa

    public sealed class NotifyViewModel
    {
        public NotifyViewModel()
        {
            PrimaryCommand = new RelayCommand(() => { Close(); if (OnPrimary != null) OnPrimary(); });
            IgnoreCommand = new RelayCommand(() => { Close(); if (OnIgnore != null) OnIgnore(); });
            CloseCommand = new RelayCommand(Close);
        }

        public string Title { get; set; }
        public string Message { get; set; }
        public string PrimaryText { get; set; }
        public bool HasActions { get { return PrimaryText != null; } }
        public ICommand PrimaryCommand { get; private set; }
        public ICommand IgnoreCommand { get; private set; }
        public ICommand CloseCommand { get; private set; }

        internal Action OnPrimary, OnIgnore, Dismiss;

        private void Close()
        {
            if (Dismiss != null) Dismiss();
        }
    }
}
