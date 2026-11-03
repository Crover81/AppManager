using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AppManager
{
    // ====================================================================== Pagina "Rete"

    /// <summary>
    /// Monitor generale delle connessioni: mostra, per ogni programma in esecuzione, verso quali indirizzi
    /// è connesso, e permette di bloccare una singola destinazione o tutto l'accesso a Internet del programma.
    /// La lettura è di sola lettura e avviene in background; il blocco usa Windows Firewall (serve l'amministratore).
    /// </summary>
    public sealed class NetworkPageViewModel : ObservableObject
    {
        private readonly DispatcherTimer timer;
        private readonly Dictionary<int, NetProcessViewModel> byPid = new Dictionary<int, NetProcessViewModel>();
        private readonly Dictionary<int, string> pathCache = new Dictionary<int, string>();
        private readonly Dictionary<int, string> nameCache = new Dictionary<int, string>();
        private static readonly Dictionary<string, ImageSource> IconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private bool active, autoRefresh = true, onlyRemote = true, refreshing, loading;
        private string summary, error, updatedText;

        public NetworkPageViewModel()
        {
            Processes = new ObservableCollection<NetProcessViewModel>();
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, e) => Refresh();
            RefreshCommand = new RelayCommand(Refresh);
            RestartAsAdminCommand = new RelayCommand(() => { if (MainViewModel.Current != null) MainViewModel.Current.RestartAsAdminCommand.Execute(null); });
        }

        public ObservableCollection<NetProcessViewModel> Processes { get; private set; }
        public ICommand RefreshCommand { get; private set; }
        public ICommand RestartAsAdminCommand { get; private set; }

        public bool IsAdmin { get { return Native.IsElevated; } }
        public string Summary { get { return summary; } private set { Set(ref summary, value); } }

        /// <summary>"Aggiornato alle 08:15:32": con l'aggiornamento automatico spento si vede quanto è vecchio l'elenco.</summary>
        public string UpdatedText { get { return updatedText; } private set { Set(ref updatedText, value); } }
        public string Error { get { return error; } private set { if (Set(ref error, value)) Raise("HasError"); } }
        public bool HasError { get { return !string.IsNullOrEmpty(error); } }
        public bool IsLoading { get { return loading; } private set { if (Set(ref loading, value)) Raise("IsEmpty"); } }
        public bool IsEmpty { get { return Processes.Count == 0 && !HasError && !loading; } }

        /// <summary>Aggiornamento automatico ogni due secondi. Se lo spegni, l'elenco resta fermo finché non premi Aggiorna.</summary>
        public bool AutoRefresh
        {
            get { return autoRefresh; }
            set
            {
                if (!Set(ref autoRefresh, value)) return;
                if (active && autoRefresh) { timer.Start(); Refresh(); }
                else timer.Stop();
            }
        }

        /// <summary>Mostra solo i programmi con connessioni verso l'esterno (nasconde chi resta in ascolto o sul PC).</summary>
        public bool OnlyRemote
        {
            get { return onlyRemote; }
            set { if (Set(ref onlyRemote, value)) Refresh(); }
        }

        /// <summary>Attiva o ferma l'aggiornamento quando si entra o si esce dalla pagina.</summary>
        internal void SetActive(bool on)
        {
            active = on;
            if (on)
            {
                if (Processes.Count == 0) IsLoading = true;
                Refresh();
                if (autoRefresh) timer.Start();
            }
            else timer.Stop();
        }

        private void Refresh()
        {
            if (refreshing) return;
            refreshing = true;
            bool onlyRemoteNow = onlyRemote;
            // tutto il lavoro pesante (lettura connessioni, percorsi dei processi, lettura del firewall) fuori dal thread dell'interfaccia
            Task.Run(() =>
            {
                List<Snapshot> snaps = null;
                string err = null;
                try
                {
                    var conns = NetMonitor.Connections();
                    var blocked = Firewall.BlockedProgramSet();
                    var groups = conns
                        .Where(c => c.Pid > 0 && (!onlyRemoteNow || (c.HasRemote && !IsLocal(c))))
                        .GroupBy(c => c.Pid);
                    snaps = new List<Snapshot>();
                    foreach (var g in groups)
                    {
                        string path = PathFor(g.Key);
                        snaps.Add(new Snapshot
                        {
                            Pid = g.Key,
                            Path = path,
                            Name = NameFor(g.Key, path),
                            Icon = IconFor(path),
                            Blocked = path != null && blocked.Contains(path),
                            Conns = g.OrderByDescending(c => c.State == 5).ThenBy(c => c.RemoteText).ToList(),
                        });
                    }
                }
                catch (Exception ex) { Log.Error("Lettura connessioni di rete", ex); err = ex.Message; }
                var e2 = err; var s2 = snaps;
                Application.Current.Dispatcher.BeginInvoke(new Action(() => Apply(s2, e2)));
            });
        }

        private void Apply(List<Snapshot> snaps, string err)
        {
            refreshing = false;
            IsLoading = false;
            if (err != null)
            {
                Error = L.F("Impossibile leggere le connessioni di rete: {0}", err);
                Raise("IsEmpty");
                return;
            }
            Error = null;
            if (snaps == null) return;

            var present = new Dictionary<int, Snapshot>();
            foreach (var s in snaps) present[s.Pid] = s;

            // togli i processi non più presenti, MA tieni quelli bloccati e ancora in esecuzione (per poterli sbloccare)
            foreach (var pid in byPid.Keys.ToList())
            {
                if (present.ContainsKey(pid)) continue;
                var vm = byPid[pid];
                if (vm.IsBlocked && Native.GetProcessPath(pid) != null) { vm.Update(new List<NetConnection>()); continue; }
                Processes.Remove(vm);
                byPid.Remove(pid);
            }
            foreach (var s in snaps)
            {
                NetProcessViewModel vm;
                if (!byPid.TryGetValue(s.Pid, out vm))
                {
                    vm = new NetProcessViewModel(s.Pid, s.Path, s.Name, s.Icon);
                    byPid[s.Pid] = vm;
                    Processes.Add(vm);
                }
                vm.SetBlockedState(s.Blocked);
                vm.Update(s.Conns);
            }
            Sort();
            int conns = snaps.Sum(s => s.Conns.Count);
            Summary = Processes.Count == 0 ? L.T("Nessuna connessione attiva")
                : Fmt.Count(Processes.Count, "programma connesso", "programmi connessi") + " · " + Fmt.Count(conns, "connessione", "connessioni");
            UpdatedText = L.F("Aggiornato alle {0}", DateTime.Now.ToString("HH:mm:ss"));
            Raise("IsEmpty");
        }

        private string PathFor(int pid)
        {
            string path;
            if (pathCache.TryGetValue(pid, out path)) return path;
            path = Native.GetProcessPath(pid);
            pathCache[pid] = path;
            return path;
        }

        /// <summary>
        /// Nome da mostrare: il nome del file dell'eseguibile se leggibile, altrimenti il nome del processo
        /// (che Windows fornisce anche per i processi di sistema), con qualche caso speciale.
        /// </summary>
        private string NameFor(int pid, string path)
        {
            if (path != null) return System.IO.Path.GetFileName(path);
            string cached;
            if (nameCache.TryGetValue(pid, out cached)) return cached;
            string name;
            if (pid == 0) name = L.T("Sistema inattivo");
            else if (pid == 4) name = L.T("Sistema (Windows)");
            else
            {
                try { using (var p = System.Diagnostics.Process.GetProcessById(pid)) name = p.ProcessName + ".exe"; }
                catch { name = L.F("Processo {0}", pid); }
            }
            nameCache[pid] = name;
            return name;
        }

        private static bool IsLocal(NetConnection c)
        {
            return c.Remote != null && (System.Net.IPAddress.IsLoopback(c.Remote) || c.Remote.Equals(System.Net.IPAddress.Any));
        }

        private void Sort()
        {
            var ordered = Processes.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Pid).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                int cur = Processes.IndexOf(ordered[i]);
                if (cur != i) Processes.Move(cur, i);
            }
        }

        private static ImageSource IconFor(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            ImageSource img;
            lock (IconCache)
                if (IconCache.TryGetValue(path, out img)) return img;
            try { img = Native.ExtractIcon(path, 32); } catch { img = null; }
            lock (IconCache) IconCache[path] = img;
            return img;
        }

        private sealed class Snapshot
        {
            public int Pid;
            public string Path;
            public string Name;
            public ImageSource Icon;
            public bool Blocked;
            public List<NetConnection> Conns;
        }
    }

    // ====================================================================== Un programma nel monitor

    public sealed class NetProcessViewModel : ObservableObject
    {
        public int Pid { get; private set; }
        public string Path { get; private set; }
        public string Name { get; private set; }
        public ImageSource Icon { get; private set; }

        private bool expanded, blocked;
        private string remotes;

        internal NetProcessViewModel(int pid, string path, string name, ImageSource icon)
        {
            Pid = pid;
            Path = path;
            Icon = icon;
            Name = name ?? (path != null ? System.IO.Path.GetFileName(path) : "PID " + pid);
            Connections = new ObservableCollection<NetConnectionViewModel>();
            BlockCommand = new RelayCommand(() => SetBlocked(!blocked), () => CanBlock);
            ToggleCommand = new RelayCommand(() => Expanded = !Expanded);
        }

        public ObservableCollection<NetConnectionViewModel> Connections { get; private set; }
        public ICommand BlockCommand { get; private set; }
        public ICommand ToggleCommand { get; private set; }

        public bool CanBlock { get { return !string.IsNullOrEmpty(Path) && Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase); } }
        public bool IsBlocked { get { return blocked; } private set { if (Set(ref blocked, value)) Raise("BlockLabel"); } }
        public string BlockLabel { get { return blocked ? L.T("Sblocca") : L.T("Blocca Internet"); } }

        public string PidText { get { return "PID " + Pid; } }
        public int ConnectionCount { get { return Connections.Count; } }
        public string ConnectionsText { get { return Fmt.Count(Connections.Count, "connessione", "connessioni"); } }
        public string RemotesSummary { get { return remotes; } private set { Set(ref remotes, value); } }

        public bool Expanded { get { return expanded; } set { Set(ref expanded, value); } }

        /// <summary>Allinea l'etichetta al vero stato del firewall (senza fare nulla).</summary>
        internal void SetBlockedState(bool value) { IsBlocked = value; }

        internal void Update(List<NetConnection> conns)
        {
            var keys = conns.Select(Key).ToList();
            for (int i = Connections.Count - 1; i >= 0; i--)
                if (!keys.Contains(Connections[i].Key)) Connections.RemoveAt(i);
            var have = new HashSet<string>(Connections.Select(c => c.Key));
            foreach (var c in conns)
                if (!have.Contains(Key(c))) Connections.Add(new NetConnectionViewModel(this, c));
            var distinct = conns.Where(c => c.HasRemote).Select(c => c.Remote.ToString()).Distinct().ToList();
            RemotesSummary = blocked && distinct.Count == 0 ? L.T("Bloccato · nessuna connessione")
                : distinct.Count == 0 ? L.T("Nessuna destinazione esterna")
                : Fmt.Count(distinct.Count, "destinazione", "destinazioni");
            Raise("ConnectionCount", "ConnectionsText");
        }

        private static string Key(NetConnection c) { return c.RemoteText + "|" + c.LocalPort; }

        internal void SetBlocked(bool block)
        {
            if (!CanBlock) return;
            if (!RequireAdmin()) return;
            try
            {
                if (block) Firewall.BlockProgram(Path, Name);
                else Firewall.UnblockPrograms(new[] { Path });
                IsBlocked = block;
                if (MainViewModel.Current != null)
                    MainViewModel.Current.ShowToast(block ? L.F("Internet bloccato per {0}", Name) : L.F("Internet sbloccato per {0}", Name));
            }
            catch (Exception ex)
            {
                Log.Error(L.F("Blocco rete di {0}", Name), ex);
                if (MainViewModel.Current != null) MainViewModel.Current.Message(L.T("Operazione non riuscita"), ex.Message);
            }
        }

        internal static bool RequireAdmin()
        {
            if (Native.IsElevated) return true;
            if (MainViewModel.Current != null) MainViewModel.Current.RequireAdmin(L.T("bloccare o sbloccare le connessioni"));
            return false;
        }
    }

    // ====================================================================== Una singola connessione

    public sealed class NetConnectionViewModel : ObservableObject
    {
        private readonly NetProcessViewModel owner;
        private readonly NetConnection conn;

        internal NetConnectionViewModel(NetProcessViewModel owner, NetConnection conn)
        {
            this.owner = owner;
            this.conn = conn;
            BlockRemoteCommand = new RelayCommand(BlockRemote, () => conn.HasRemote);
        }

        internal string Key { get { return conn.RemoteText + "|" + conn.LocalPort; } }
        public string RemoteText { get { return conn.HasRemote ? conn.RemoteText : "—"; } }
        public string StateText { get { return conn.StateText; } }
        public bool HasRemote { get { return conn.HasRemote; } }
        public string ProtocolText { get { return conn.IPv6 ? "TCP/IPv6" : "TCP"; } }
        public ICommand BlockRemoteCommand { get; private set; }

        private void BlockRemote()
        {
            if (!conn.HasRemote || !NetProcessViewModel.RequireAdmin()) return;
            string ip = conn.Remote.ToString();
            string exe = owner.CanBlock ? owner.Path : null;
            MainViewModel.Current.Confirm(L.F("Bloccare le connessioni verso {0}?", ip),
                L.F("{0} Puoi togliere il blocco dalle Impostazioni › Regole di rete.", (exe != null ? L.F("«{0}» non potrà più connettersi a {1}.", owner.Name, ip) : L.F("Nessun programma potrà più connettersi a {0}.", ip))),
                L.T("Blocca"), true, () =>
                {
                    try
                    {
                        Firewall.BlockRemote(ip, exe, owner.Name);
                        MainViewModel.Current.ShowToast(L.F("Bloccate le connessioni verso {0}", ip));
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Blocco verso " + ip, ex);
                        MainViewModel.Current.Message(L.T("Operazione non riuscita"), ex.Message);
                    }
                });
        }
    }
}
