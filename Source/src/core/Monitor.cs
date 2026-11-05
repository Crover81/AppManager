using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace AppManager
{
    // ====================================================================== Istantanea di ciò che è installato

    /// <summary>Un'applicazione come la vede la sorveglianza: quanto basta per riconoscerla e cercarne i residui quando sparisce.</summary>
    internal sealed class WatchedApp
    {
        public string Key { get; set; }           // reg:percorso della voce, steam:appid, epic:nome
        public string AppId { get; set; }         // Id dell'AppEntry (per i backup e le immagini), se noto
        public string Name { get; set; }
        public string Publisher { get; set; }
        public string Version { get; set; }
        public string InstallDir { get; set; }
        public string UninstallKey { get; set; }
        public string SteamAppId { get; set; }
        public string Image { get; set; }
        public string Source { get; set; }        // registry, steam, epic
        public bool IsGame { get; set; }
        public bool IsComponent { get; set; }
    }

    /// <summary>Cosa è cambiato tra due istantanee.</summary>
    internal sealed class WatchDiff
    {
        public readonly List<WatchedApp> Removed = new List<WatchedApp>();
        public readonly List<WatchedApp> Added = new List<WatchedApp>();
        public readonly List<Tuple<WatchedApp, WatchedApp>> Updated = new List<Tuple<WatchedApp, WatchedApp>>();
        public bool IsEmpty { get { return Removed.Count + Added.Count + Updated.Count == 0; } }

        /// <summary>
        /// Una voce sparita e una comparsa con lo stesso prodotto sono un aggiornamento (molti installer MSI cambiano voce a ogni versione),
        /// non una disinstallazione: lì non si cercano residui.
        /// </summary>
        public static WatchDiff Compare(Dictionary<string, WatchedApp> before, Dictionary<string, WatchedApp> after)
        {
            var d = new WatchDiff();
            d.Removed.AddRange(before.Where(kv => !after.ContainsKey(kv.Key)).Select(kv => kv.Value));
            d.Added.AddRange(after.Where(kv => !before.ContainsKey(kv.Key)).Select(kv => kv.Value));
            foreach (var r in d.Removed.ToList())
            {
                var a = d.Added.FirstOrDefault(x => SameProduct(r, x));
                if (a == null) continue;
                d.Removed.Remove(r);
                d.Added.Remove(a);
                d.Updated.Add(Tuple.Create(r, a));
            }
            return d;
        }

        internal static bool SameProduct(WatchedApp a, WatchedApp b)
        {
            if (a.Source != b.Source) return false;
            string na = NameUtil.Norm(NameUtil.BaseProgramName(a.Name)), nb = NameUtil.Norm(NameUtil.BaseProgramName(b.Name));
            if (na.Length < 3 || na != nb) return false;
            string pa = NameUtil.Norm(a.Publisher), pb = NameUtil.Norm(b.Publisher);
            return pa.Length == 0 || pb.Length == 0 || pa == pb;
        }
    }

    internal static class WatchSnapshot
    {
        private static string FileName { get { return Path.Combine(AppPaths.DataDir, "pc", ThisPc.Id + ".watch"); } }

        /// <summary>Programmi installati (voci di disinstallazione), giochi e strumenti di Steam, giochi di Epic.</summary>
        public static Dictionary<string, WatchedApp> Take(IList<AppEntry> known)
        {
            var map = new Dictionary<string, WatchedApp>(StringComparer.OrdinalIgnoreCase);
            known = known ?? new List<AppEntry>();
            foreach (var e in InstalledApps.ReadUninstallEntries())
            {
                if (string.IsNullOrWhiteSpace(e.DisplayName) || e.IsSystemOrUpdate || InstalledApps.SteamAppKey.IsMatch(e.KeyName)) continue;
                var k = known.FirstOrDefault(x => x.UninstallKey != null && x.UninstallKey.Equals(e.KeyPath, StringComparison.OrdinalIgnoreCase));
                map["reg:" + e.KeyPath.ToLowerInvariant()] = new WatchedApp
                {
                    Key = "reg:" + e.KeyPath.ToLowerInvariant(),
                    AppId = k == null ? null : k.Id,
                    Name = k != null ? k.Name : NameUtil.CleanProgramName(e.DisplayName, e.DisplayVersion),
                    Publisher = e.Publisher,
                    Version = e.DisplayVersion,
                    InstallDir = k != null && k.InstallDir != null ? k.InstallDir : PathUtil.Normalize(e.InstallLocation),
                    UninstallKey = e.KeyPath,
                    Image = k == null ? null : k.CoverImage,
                    Source = "registry",
                    IsGame = k != null && k.IsGame,
                    IsComponent = k != null && k.IsComponent,
                };
            }
            foreach (var a in SteamInfo.DetectApps(AppKind.Game, true).Concat(SteamInfo.DetectApps(AppKind.Program, true)))
            {
                map["steam:" + a.SteamAppId] = new WatchedApp
                {
                    Key = "steam:" + a.SteamAppId, AppId = a.Id, Name = a.Name, Publisher = a.Publisher, InstallDir = a.InstallDir,
                    UninstallKey = a.UninstallKey, SteamAppId = a.SteamAppId, Image = a.CoverImage, Source = "steam", IsGame = a.IsGame,
                };
            }
            foreach (var a in EpicInfo.DetectGames())
            {
                if (string.IsNullOrEmpty(a.EpicAppName)) continue;
                map["epic:" + a.EpicAppName.ToLowerInvariant()] = new WatchedApp
                {
                    Key = "epic:" + a.EpicAppName.ToLowerInvariant(), AppId = a.Id, Name = a.Name, Publisher = a.Publisher, InstallDir = a.InstallDir,
                    Image = a.CoverImage, Source = "epic", IsGame = true,
                };
            }
            return map;
        }

        public static Dictionary<string, WatchedApp> Load()
        {
            try
            {
                if (!File.Exists(FileName)) return null;
                var list = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<List<WatchedApp>>(File.ReadAllText(FileName));
                if (list == null) return null;
                var map = new Dictionary<string, WatchedApp>(StringComparer.OrdinalIgnoreCase);
                foreach (var w in list) if (w != null && w.Key != null) map[w.Key] = w;
                return map;
            }
            catch (Exception ex) { Log.Warn("Istantanea della sorveglianza illeggibile: {0}", ex.Message); return null; }
        }

        public static void Save(Dictionary<string, WatchedApp> map)
        {
            if (AppSettings.SaveDisabled) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FileName));
                PathUtil.WriteAllTextAtomic(FileName, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(map.Values.ToList()));
            }
            catch (Exception ex) { Log.Error("Salvataggio dell'istantanea della sorveglianza", ex); }
        }

        public static void Delete()
        {
            try { if (File.Exists(FileName)) File.Delete(FileName); }
            catch { }
        }
    }

    // ====================================================================== Cronologia

    /// <summary>Un residuo salvato nella cronologia (i campi di <see cref="LeftoverItem"/> che servono a ritrovarlo ed eliminarlo).</summary>
    internal sealed class StoredItem
    {
        public string Kind { get; set; }
        public string Category { get; set; }
        public string Path { get; set; }
        public string ValueName { get; set; }
        public string Data { get; set; }
        public string Reason { get; set; }
        public int Confidence { get; set; }
        public long Size { get; set; }
        public int FileCount { get; set; }
        public bool NeedsAdmin { get; set; }

        public static StoredItem From(LeftoverItem i)
        {
            return new StoredItem
            {
                Kind = i.Kind.ToString(), Category = i.Category.ToString(), Path = i.Path, ValueName = i.ValueName, Data = i.Data, Reason = i.Reason,
                Confidence = (int)i.Confidence, Size = Math.Max(0, i.Size), FileCount = i.FileCount, NeedsAdmin = i.NeedsAdmin,
            };
        }

        public LeftoverItem ToItem()
        {
            ItemKind kind;
            ItemCategory cat;
            if (!Enum.TryParse(Kind, out kind) || !Enum.TryParse(Category, out cat)) return null;
            return new LeftoverItem
            {
                Kind = kind, Category = cat, Path = Path, ValueName = ValueName, Data = Data, Reason = Reason,
                Confidence = (AppManager.Confidence)Math.Max(0, Math.Min(2, Confidence)), Size = Size, FileCount = FileCount, NeedsAdmin = NeedsAdmin,
            };
        }
    }

    /// <summary>Un'installazione, un aggiornamento o una disinstallazione, con ciò che è rimasto.</summary>
    internal sealed class HistoryEvent
    {
        public const string Installed = "installed", Updated = "updated", Uninstalled = "uninstalled";
        // stato di una disinstallazione
        public const string Checking = "checking", Clean = "clean", Leftovers = "leftovers", Cleaned = "cleaned", Ignored = "ignored", Skipped = "skipped";

        public string Id { get; set; }
        public string When { get; set; }
        public string Type { get; set; }
        public string AppId { get; set; }
        public string Name { get; set; }
        public string Publisher { get; set; }
        public string Version { get; set; }
        public string OldVersion { get; set; }
        public bool IsGame { get; set; }
        public string Source { get; set; }        // registry, steam, epic
        public bool ByAppManager { get; set; }
        public string Image { get; set; }
        public string Status { get; set; }
        public List<StoredItem> Items { get; set; }

        [ScriptIgnore]
        public DateTime WhenDate
        {
            get
            {
                DateTime d;
                return DateTime.TryParse(When, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out d) ? d : DateTime.MinValue;
            }
        }

        [ScriptIgnore] public int ItemCount { get { return Items == null ? 0 : Items.Count; } }
        [ScriptIgnore] public long ItemSize { get { return Items == null ? 0 : Items.Sum(i => i.Size); } }
        [ScriptIgnore] public bool HasRegistryItems { get { return Items != null && Items.Any(i => i.Kind == "RegistryKey" || i.Kind == "RegistryValue"); } }
        /// <summary>Vale un avviso: almeno un residuo «Certo» o «Probabile».</summary>
        [ScriptIgnore]
        public bool WorthNotifying { get { return Items != null && Items.Any(i => i.Confidence >= (int)AppManager.Confidence.Medium); } }

        public static HistoryEvent For(WatchedApp w, string type)
        {
            return new HistoryEvent
            {
                Id = Guid.NewGuid().ToString("N"), When = DateTime.Now.ToString("o"), Type = type, AppId = w.AppId, Name = w.Name, Publisher = w.Publisher,
                Version = w.Version, IsGame = w.IsGame, Source = w.Source, Image = w.Image, Items = new List<StoredItem>(),
            };
        }
    }

    /// <summary>La cronologia di questo PC, in Dati\pc. Al massimo <see cref="MaxEvents"/> voci, le più recenti.</summary>
    internal static class MonitorHistory
    {
        public const int MaxEvents = 300;
        private static readonly object Sync = new object();
        private static List<HistoryEvent> events;

        /// <summary>La cronologia è cambiata (chiamato dal thread che l'ha cambiata).</summary>
        public static event Action Changed;

        private static string FileName { get { return Path.Combine(AppPaths.DataDir, "pc", ThisPc.Id + ".history"); } }

        /// <summary>Solo per le prove grafiche: la cronologia resta in memoria.</summary>
        internal static void UseInMemory(List<HistoryEvent> list)
        {
            lock (Sync) events = list;
        }

        private static List<HistoryEvent> Events
        {
            get
            {
                if (events != null) return events;
                try
                {
                    if (File.Exists(FileName))
                        events = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<List<HistoryEvent>>(File.ReadAllText(FileName));
                }
                catch (Exception ex) { Log.Warn("Cronologia della sorveglianza illeggibile: {0}", ex.Message); }
                if (events == null) events = new List<HistoryEvent>();
                events.RemoveAll(e => e == null || e.Id == null);
                // un controllo interrotto (PC spento a metà) non resta «in corso» per sempre
                foreach (var e in events.Where(x => x.Status == HistoryEvent.Checking)) e.Status = e.ItemCount > 0 ? HistoryEvent.Leftovers : HistoryEvent.Skipped;
                return events;
            }
        }

        public static List<HistoryEvent> All()
        {
            lock (Sync) return Events.OrderByDescending(e => e.WhenDate).ToList();
        }

        public static HistoryEvent Get(string id)
        {
            lock (Sync) return Events.FirstOrDefault(e => e.Id == id);
        }

        public static void Add(HistoryEvent e)
        {
            lock (Sync)
            {
                Events.Insert(0, e);
                if (Events.Count > MaxEvents) Events.RemoveRange(MaxEvents, Events.Count - MaxEvents);
                Write();
            }
            Raise();
        }

        /// <summary>Applica una modifica a una voce e la salva.</summary>
        public static void Update(string id, Action<HistoryEvent> change)
        {
            lock (Sync)
            {
                var e = Events.FirstOrDefault(x => x.Id == id);
                if (e == null) return;
                change(e);
                Write();
            }
            Raise();
        }

        public static void SetItems(string id, IEnumerable<LeftoverItem> items)
        {
            var stored = items.Select(StoredItem.From).ToList();
            Update(id, e =>
            {
                e.Items = stored;
                e.Status = stored.Count == 0 ? (e.Status == HistoryEvent.Leftovers ? HistoryEvent.Cleaned : HistoryEvent.Clean) : HistoryEvent.Leftovers;
            });
        }

        public static void Clear()
        {
            lock (Sync)
            {
                Events.Clear();
                Write();
            }
            Raise();
        }

        /// <summary>
        /// Rilegge cosa resta di una disinstallazione: i residui già eliminati spariscono dall'elenco.
        /// Restituisce quelli ancora presenti.
        /// </summary>
        public static List<LeftoverItem> Recheck(string id)
        {
            var e = Get(id);
            if (e == null) return new List<LeftoverItem>();
            var left = (e.Items ?? new List<StoredItem>()).Select(i => i.ToItem()).Where(i => i != null && LeftoverCheck.Exists(i)).ToList();
            var keep = new HashSet<string>(left.Select(i => i.Key));
            Update(id, x =>
            {
                x.Items = (x.Items ?? new List<StoredItem>()).Where(s => { var it = s.ToItem(); return it != null && keep.Contains(it.Key); }).ToList();
                if (x.Items.Count == 0 && (x.Status == HistoryEvent.Leftovers || x.Status == HistoryEvent.Ignored)) x.Status = HistoryEvent.Cleaned;
            });
            return left;
        }

        /// <summary>Una disinstallazione fatta con AppManager: si annota con i residui che ha trovato.</summary>
        public static string RecordUninstallByApp(AppEntry g, IEnumerable<LeftoverItem> items)
        {
            var e = new HistoryEvent
            {
                Id = Guid.NewGuid().ToString("N"), When = DateTime.Now.ToString("o"), Type = HistoryEvent.Uninstalled, AppId = g.Id, Name = g.Name,
                Publisher = g.Publisher, Version = g.Version, IsGame = g.IsGame, Source = g.Launcher == LauncherKind.Steam ? "steam" : g.Launcher == LauncherKind.Epic ? "epic" : "registry",
                ByAppManager = true, Image = g.CoverImage, Items = items.Select(StoredItem.From).ToList(),
            };
            e.Status = e.Items.Count == 0 ? HistoryEvent.Clean : HistoryEvent.Leftovers;
            Add(e);
            return e.Id;
        }

        private static void Write()
        {
            if (AppSettings.SaveDisabled) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FileName));
                PathUtil.WriteAllTextAtomic(FileName, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(events));
            }
            catch (Exception ex) { Log.Error("Salvataggio della cronologia della sorveglianza", ex); }
        }

        private static void Raise()
        {
            var h = Changed;
            if (h != null) try { h(); } catch (Exception ex) { Log.Error("Aggiornamento della cronologia", ex); }
        }
    }

    /// <summary>Se un residuo c'è ancora (per i tipi che non si possono controllare in modo economico: sì).</summary>
    internal static class LeftoverCheck
    {
        public static bool Exists(LeftoverItem i)
        {
            try
            {
                switch (i.Kind)
                {
                    case ItemKind.Directory: return Directory.Exists(PathUtil.Long(i.Path));
                    case ItemKind.File: return File.Exists(PathUtil.Long(i.Path));
                    case ItemKind.RegistryKey: return RegUtil.KeyExists(i.Path);
                    case ItemKind.RegistryValue: return RegUtil.ValueExists(i.Path, i.ValueName);
                    case ItemKind.Service: return RegUtil.KeyExists(@"HKLM\SYSTEM\CurrentControlSet\Services\" + i.Path);
                    case ItemKind.ScheduledTask: return File.Exists(Path.Combine(KnownDirs.Windows, "System32", "Tasks", (i.Path ?? "").TrimStart('\\')));
                    default: return true;
                }
            }
            catch { return true; }
        }
    }

    // ====================================================================== Avvio con Windows

    /// <summary>
    /// Avvio di AppManager con Windows, nell'area di notifica. Da amministratore un'attività pianificata «con i privilegi più elevati»
    /// (Windows non avvia all'accesso un programma che chiede l'UAC dalla chiave Run); senza, la chiave Run dell'utente.
    /// </summary>
    internal static class BackgroundStartup
    {
        public const string TaskName = "AppManager";
        public const string Argument = "--background";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        internal static string BuildTaskXml(string exe, string user, bool highest)
        {
            Func<string, string> x = SecurityElement.Escape;
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Author>AppManager</Author><Description>" + x(L.T("Avvia AppManager nell'area di notifica per sorvegliare installazioni e disinstallazioni.")) + "</Description></RegistrationInfo>\r\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + x(user) + "</UserId><Delay>PT20S</Delay></LogonTrigger></Triggers>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + x(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>" + (highest ? "HighestAvailable" : "LeastPrivilege") + "</RunLevel></Principal></Principals>\r\n" +
                "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable>" +
                "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>" +
                "<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle>" +
                "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + x(exe) + "</Command><Arguments>" + Argument + "</Arguments><WorkingDirectory>" + x(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions>\r\n" +
                "</Task>\r\n";
        }

        /// <summary>Registra l'avvio con Windows per questo eseguibile. Restituisce null o il motivo dell'errore.</summary>
        public static string Register(string exe)
        {
            if (AppSettings.SaveDisabled) return null;
            try
            {
                if (Native.IsElevated)
                {
                    RemoveRunValue();
                    string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                    string file = Path.Combine(KnownDirs.Temp, "am_task_" + Guid.NewGuid().ToString("N") + ".xml");
                    File.WriteAllText(file, BuildTaskXml(exe, user, true), Encoding.Unicode);
                    try
                    {
                        string output;
                        int code = Schtasks("/Create /TN \"" + TaskName + "\" /XML \"" + file + "\" /F", out output);
                        if (code != 0) return output.Trim().Length > 0 ? output.Trim() : L.F("schtasks ha risposto con il codice {0}", code);
                    }
                    finally { try { File.Delete(file); } catch { } }
                }
                else
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                        k.SetValue(TaskName, "\"" + exe + "\" " + Argument);
                }
                Log.Info("Avvio con Windows attivato ({0})", Native.IsElevated ? L.T("attività pianificata") : L.T("chiave Run"));
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("Registrazione dell'avvio con Windows", ex);
                return ex.Message;
            }
        }

        public static void Unregister()
        {
            if (AppSettings.SaveDisabled) return;
            try
            {
                RemoveRunValue();
                string output;
                if (RegisteredTaskCommand() != null) Schtasks("/Delete /TN \"" + TaskName + "\" /F", out output);
                Log.Info("Avvio con Windows disattivato");
            }
            catch (Exception ex) { Log.Error("Rimozione dell'avvio con Windows", ex); }
        }

        /// <summary>True se l'avvio con Windows punta a questo eseguibile (il programma è portable: può essere stato spostato).</summary>
        public static bool IsRegisteredFor(string exe)
        {
            string cmd = Native.IsElevated ? RegisteredTaskCommand() : RunValue();
            return cmd != null && cmd.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string RunValue()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k == null ? null : k.GetValue(TaskName) as string;
        }

        private static void RemoveRunValue()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                if (k != null && k.GetValue(TaskName) != null) k.DeleteValue(TaskName, false);
        }

        /// <summary>Il programma avviato dall'attività «AppManager», se c'è.</summary>
        private static string RegisteredTaskCommand()
        {
            string file = Path.Combine(KnownDirs.Windows, "System32", "Tasks", TaskName);
            try
            {
                if (!File.Exists(file)) return null;
                var m = Regex.Match(File.ReadAllText(file), @"<Command>(.*?)</Command>", RegexOptions.Singleline);
                return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : "";
            }
            catch { return ""; }
        }

        private static int Schtasks(string args, out string output)
        {
            var psi = new ProcessStartInfo(Path.Combine(KnownDirs.Windows, "System32", "schtasks.exe"), args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                string outText = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } output = L.T("schtasks non ha risposto"); return -1; }
                output = err.Length > 0 ? err : outText;
                return p.ExitCode;
            }
        }
    }

    // ====================================================================== Sorveglianza

    /// <summary>
    /// Segue in background le voci di disinstallazione e le librerie di Steam ed Epic. Quando un'applicazione sparisce cerca i residui,
    /// li annota nella cronologia e avvisa. Non elimina mai nulla: decide sempre l'utente.
    /// </summary>
    internal static class InstallWatcher
    {
        /// <summary>Una voce è stata aggiunta o completata nella cronologia; il secondo argomento dice se vale un avviso.</summary>
        public static event Action<HistoryEvent, bool> Recorded;
        public static event Action StatusChanged;

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, DateTime> expected = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static Thread thread;
        private static ManualResetEvent stopEvent;
        private static readonly AutoResetEvent pokeEvent = new AutoResetEvent(false);
        private static List<AppEntry> known = new List<AppEntry>();
        private static string status;
        private static DateTime? lastCheck;

        public static bool IsRunning { get { lock (Sync) return thread != null; } }
        public static DateTime? LastCheck { get { return lastCheck; } }
        public static string Status { get { return status; } }

        public static void Start()
        {
            lock (Sync)
            {
                if (thread != null) return;
                stopEvent = new ManualResetEvent(false);
                var stop = stopEvent;
                thread = new Thread(() => Loop(stop)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Sorveglianza" };
                thread.Start();
            }
            Log.Info("Sorveglianza in background avviata");
        }

        public static void Stop()
        {
            lock (Sync)
            {
                if (thread == null) return;
                stopEvent.Set();
                thread = null;
            }
            SetStatus(null);
            Log.Info("Sorveglianza in background fermata");
        }

        public static void CheckNow() { pokeEvent.Set(); }

        /// <summary>La disinstallazione la sta facendo AppManager: i residui li cerca già la sua finestra, non serve un secondo avviso.</summary>
        public static void ExpectUninstall(AppEntry e)
        {
            lock (Sync) foreach (var k in KeysOf(e)) expected[k] = DateTime.Now;
        }

        private static IEnumerable<string> KeysOf(AppEntry e)
        {
            if (!string.IsNullOrEmpty(e.UninstallKey)) yield return "reg:" + e.UninstallKey.ToLowerInvariant();
            if (!string.IsNullOrEmpty(e.SteamAppId)) yield return "steam:" + e.SteamAppId;
            if (!string.IsNullOrEmpty(e.EpicAppName)) yield return "epic:" + e.EpicAppName.ToLowerInvariant();
        }

        private static bool WasExpected(WatchedApp w)
        {
            lock (Sync)
            {
                DateTime t;
                if (!expected.TryGetValue(w.Key, out t)) return false;
                expected.Remove(w.Key);
                return (DateTime.Now - t).TotalHours < 6;
            }
        }

        private static void SetStatus(string s)
        {
            status = s;
            var h = StatusChanged;
            if (h != null) try { h(); } catch { }
        }

        private static void Loop(ManualResetEvent stop)
        {
            var notifier = new RegistryNotifier();
            var watchers = new List<FileSystemWatcher>();
            try
            {
                // all'accesso a Windows c'è già abbastanza da fare: si parte con calma
                SetStatus(L.T("In avvio…"));
                if (stop.WaitOne(TimeSpan.FromSeconds(15))) return;
                RefreshKnown(stop);
                var current = WatchSnapshot.Take(known);
                var previous = WatchSnapshot.Load();
                // le disinstallazioni fatte mentre AppManager era chiuso si controllano adesso
                if (previous == null) WatchSnapshot.Save(current);
                else Process(previous, stop);
                foreach (var dir in SteamInfo.SteamAppsDirs.Concat(new[] { EpicInfo.ManifestsDir }))
                {
                    try
                    {
                        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                        var w = new FileSystemWatcher(dir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
                        w.Created += (s, e) => pokeEvent.Set();
                        w.Deleted += (s, e) => pokeEvent.Set();
                        w.Renamed += (s, e) => pokeEvent.Set();
                        w.EnableRaisingEvents = true;
                        watchers.Add(w);
                    }
                    catch (Exception ex) { Log.Warn("Sorveglianza di {0}: {1}", dir, ex.Message); }
                }
                notifier.Arm();
                // controllo ricostruito anche senza la memoria del suo stato: precalcolo delle impronte dei programmi, per i residui
                WarmProfiles(stop);
                var started = DateTime.Now;
                var lastSweep = DateTime.Now;
                while (true)
                {
                    SetStatus(L.T("In ascolto"));
                    int i = WaitHandle.WaitAny(new WaitHandle[] { stop, pokeEvent, notifier.Event }, TimeSpan.FromMinutes(5));
                    if (i == 0) return;
                    if (i == WaitHandle.WaitTimeout)
                    {
                        try { CleanableCheck.RunIfDue(started, CancellationToken.None); }
                        catch (Exception ex) { Log.Error("Controllo di log e cache", ex); }
                        // controllo di sicurezza ogni 20 minuti, per ciò che sfugge agli avvisi (es. una libreria di Steam su un disco riattaccato)
                        if (DateTime.Now - lastSweep < TimeSpan.FromMinutes(20)) continue;
                    }
                    lastSweep = DateTime.Now;
                    if (i == 2) notifier.Arm();
                    if (!Settle(stop, notifier)) return;
                    var prev = WatchSnapshot.Load() ?? WatchSnapshot.Take(known);
                    Process(prev, stop);
                }
            }
            catch (Exception ex) { Log.Error("Sorveglianza in background", ex); }
            finally
            {
                notifier.Dispose();
                foreach (var w in watchers) try { w.Dispose(); } catch { }
                stop.Dispose();
            }
        }

        /// <summary>
        /// Un programma di disinstallazione fa molte modifiche e può impiegare minuti: si aspetta che finisca
        /// (3 secondi senza modifiche e nessun programma di disinstallazione aperto, al massimo 15 minuti).
        /// </summary>
        private static bool Settle(ManualResetEvent stop, RegistryNotifier notifier)
        {
            SetStatus(L.T("Attendo la fine dell'installazione o della disinstallazione…"));
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromMinutes(15))
            {
                int i = WaitHandle.WaitAny(new WaitHandle[] { stop, pokeEvent, notifier.Event }, TimeSpan.FromSeconds(3));
                if (i == 0) return false;
                if (i == 2) { notifier.Arm(); continue; }
                if (i == 1) continue;
                if (!UninstallerRunning()) return true;
            }
            return true;
        }

        private static readonly Regex UninstallerName = new Regex(@"^(unins\d*|uninst.*|uninstall.*|au_|un_a|_iu14d2n|.*uninstaller|setup|install)$", RegexOptions.IgnoreCase);

        private static bool UninstallerRunning()
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try { if (UninstallerName.IsMatch(p.ProcessName)) return true; }
                catch { }
                finally { p.Dispose(); }
            }
            return false;
        }

        private static void Process(Dictionary<string, WatchedApp> previous, ManualResetEvent stop)
        {
            SetStatus(L.T("Controllo delle modifiche…"));
            var now = WatchSnapshot.Take(known);
            var diff = WatchDiff.Compare(previous, now);
            if (diff.Removed.Count > 0)
            {
                // una voce può sparire per un attimo durante un aggiornamento: si controlla di nuovo poco dopo
                if (stop.WaitOne(TimeSpan.FromSeconds(3))) return;
                now = WatchSnapshot.Take(known);
                diff = WatchDiff.Compare(previous, now);
            }
            lastCheck = DateTime.Now;
            if (diff.IsEmpty) { WatchSnapshot.Save(now); return; }
            var before = known;
            // prima i residui di ciò che è stato tolto: l'avviso non aspetta la rilettura di tutti i giochi e programmi
            // (per riconoscere ciò che è di altri basta l'elenco che c'era già)
            foreach (var r in diff.Removed)
            {
                if (stop.WaitOne(0)) return;
                try { HandleRemoval(r, before, stop); }
                catch (Exception ex) { Log.Error(L.F("Controllo dei residui di {0}", r.Name), ex); }
            }
            RefreshKnown(stop);
            // l'impronta di ciò che è appena stato installato o aggiornato va presa ora, finché i suoi file ci sono
            foreach (var a in diff.Added.Concat(diff.Updated.Select(u => u.Item2)))
            {
                var k = Match(known, a);
                if (k == null) continue;
                try { ProfileBuilder.Forget(k); ProfileBuilder.Get(k); }
                catch { }
            }
            foreach (var a in diff.Added)
            {
                var e = HistoryEvent.For(Enrich(a), HistoryEvent.Installed);
                MonitorHistory.Add(e);
                Log.Info("Sorveglianza: installato {0}", a.Name);
                Raise(e, false);
            }
            foreach (var u in diff.Updated)
            {
                var e = HistoryEvent.For(Enrich(u.Item2), HistoryEvent.Updated);
                e.OldVersion = u.Item1.Version;
                MonitorHistory.Add(e);
                Raise(e, false);
            }
            WatchSnapshot.Save(now);
        }

        private static WatchedApp Enrich(WatchedApp w)
        {
            var k = Match(known, w);
            if (k != null)
            {
                w.AppId = k.Id;
                w.IsGame = k.IsGame;
                if (w.Image == null) w.Image = k.CoverImage;
            }
            return w;
        }

        private static AppEntry Match(IEnumerable<AppEntry> list, WatchedApp w)
        {
            foreach (var e in list)
            {
                if (w.AppId != null && e.Id == w.AppId) return e;
                if (w.UninstallKey != null && w.UninstallKey.Equals(e.UninstallKey, StringComparison.OrdinalIgnoreCase)) return e;
                if (w.SteamAppId != null && w.SteamAppId == e.SteamAppId) return e;
            }
            return null;
        }

        /// <summary>
        /// Alcuni programmi di disinstallazione tolgono la voce prima di aver finito di cancellare i file: si aspetta che la cartella
        /// sparisca o smetta di cambiare (al massimo 2 minuti), così l'avviso arriva a disinstallazione davvero finita.
        /// </summary>
        private static void WaitUntilGone(string dir, ManualResetEvent stop)
        {
            if (string.IsNullOrEmpty(dir) || dir.Length <= 3) return;
            var clock = Stopwatch.StartNew();
            var stable = Stopwatch.StartNew();
            string last = null;
            while (clock.Elapsed < TimeSpan.FromMinutes(2) && Directory.Exists(dir))
            {
                string now;
                try { now = Directory.GetFileSystemEntries(dir).Length + "|" + Directory.GetLastWriteTimeUtc(dir).Ticks; }
                catch { return; }
                if (now != last) { last = now; stable.Restart(); }
                else if (stable.Elapsed > TimeSpan.FromSeconds(10)) return; // non cambia più: resta sul disco, sarà un residuo
                if (stop.WaitOne(TimeSpan.FromSeconds(2))) return;
            }
        }

        private static void HandleRemoval(WatchedApp w, List<AppEntry> before, ManualResetEvent stop)
        {
            if (WasExpected(w)) return; // la cronologia l'ha già scritta la finestra di disinstallazione di AppManager
            var entry = Match(before, w) ?? new AppEntry
            {
                Id = w.AppId ?? w.Key, Kind = w.IsGame ? AppKind.Game : AppKind.Program, Name = w.Name, Publisher = w.Publisher, Version = w.Version,
                InstallDir = w.InstallDir, UninstallKey = w.UninstallKey, SteamAppId = w.SteamAppId,
                Launcher = w.Source == "steam" ? LauncherKind.Steam : w.Source == "epic" ? LauncherKind.Epic : LauncherKind.Other,
            };
            var ev = HistoryEvent.For(w, HistoryEvent.Uninstalled);
            ev.AppId = entry.Id;
            ev.IsGame = entry.IsGame;
            if (ev.Image == null) ev.Image = entry.CoverImage;
            Log.Info("Sorveglianza: disinstallato {0}", w.Name);
            if (entry.IsComponent || w.IsComponent)
            {
                // runtime e componenti di Windows: i loro file condivisi non vanno proposti come residui
                ev.Status = HistoryEvent.Skipped;
                MonitorHistory.Add(ev);
                Raise(ev, false);
                return;
            }
            ev.Status = HistoryEvent.Checking;
            MonitorHistory.Add(ev);
            Raise(ev, false);
            SetStatus(L.F("Attendo che {0} sia rimosso del tutto…", w.Name));
            WaitUntilGone(entry.InstallDir ?? w.InstallDir, stop);
            SetStatus(L.F("Cerco i residui di {0}…", w.Name));
            var profile = ProfileBuilder.Get(entry);
            var others = known.Where(x => x.Id != entry.Id && Match(new[] { x }, w) == null).Select(ProfileBuilder.Get).ToList();
            var items = LeftoverScanner.Scan(profile, others, true, null, CancellationToken.None);
            ProfileBuilder.Forget(entry);
            MonitorHistory.SetItems(ev.Id, items);
            var done = MonitorHistory.Get(ev.Id);
            Log.Info("Sorveglianza: {0} residui di {1}", items.Count, w.Name);
            if (done != null) Raise(done, done.WorthNotifying);
        }

        private static void Raise(HistoryEvent e, bool notify)
        {
            var h = Recorded;
            if (h != null) try { h(e, notify); } catch (Exception ex) { Log.Error("Avviso della sorveglianza", ex); }
        }

        /// <summary>Elenco completo di giochi e programmi: serve a riconoscere ciò che appartiene ancora ad altre applicazioni.</summary>
        private static void RefreshKnown(ManualResetEvent stop)
        {
            if (stop.WaitOne(0)) return;
            SetStatus(L.T("Leggo giochi e programmi installati…"));
            try
            {
                var games = GameDetector.DetectAll(AppSettings.Current, null);
                var programs = ProgramDetector.DetectAll(AppSettings.Current, games, null);
                known = games.Concat(programs).ToList();
            }
            catch (Exception ex) { Log.Error("Sorveglianza: rilevamento delle applicazioni", ex); }
        }

        /// <summary>L'impronta (eseguibili, cartelle, nomi) va presa finché l'applicazione è installata: dopo non si può più.</summary>
        private static void WarmProfiles(ManualResetEvent stop)
        {
            foreach (var e in known.ToList())
            {
                if (stop.WaitOne(0)) return;
                try { ProfileBuilder.Get(e); }
                catch { }
            }
        }
    }

    /// <summary>Avviso di Windows quando cambia una delle chiavi «Uninstall» (nessun controllo periodico: zero lavoro quando non succede nulla).</summary>
    internal sealed class RegistryNotifier : IDisposable
    {
        public readonly AutoResetEvent Event = new AutoResetEvent(false);
        private readonly List<RegistryKey> keys = new List<RegistryKey>();

        public RegistryNotifier()
        {
            foreach (var path in InstalledApps.UninstallRoots)
            {
                try
                {
                    var k = RegUtil.Open(path);
                    if (k != null) keys.Add(k);
                }
                catch { }
            }
        }

        /// <summary>Ogni avviso vale una volta sola: va richiesto di nuovo dopo averlo ricevuto.</summary>
        public void Arm()
        {
            foreach (var k in keys)
            {
                try { Native.RegNotifyChangeKeyValue(k.Handle, true, Native.RegNotifyName | Native.RegNotifyLastSet, Event.SafeWaitHandle, true); }
                catch (Exception ex) { Log.Warn("Avviso del registro: {0}", ex.Message); }
            }
        }

        public void Dispose()
        {
            foreach (var k in keys) try { k.Dispose(); } catch { }
            Event.Dispose();
        }
    }
}
