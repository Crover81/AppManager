using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AppManager
{
    /// <summary>
    /// Ciò che AppManager ricorda di un PC. Il programma è portable: la stessa cartella Dati gira su PC diversi,
    /// quindi cartelle, elementi nascosti e giochi noti valgono per un PC solo e stanno qui, non nelle impostazioni generali.
    /// </summary>
    internal sealed class PcProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string FirstSeen { get; set; }
        public string LastSeen { get; set; }

        public string BackupFolder { get; set; }
        public string SaveVaultFolder { get; set; }
        public List<string> LibraryFolders { get; set; }
        public List<string> ProgramFolders { get; set; }
        public List<SavedEntry> HiddenGames { get; set; }
        public List<SavedEntry> ManualGames { get; set; }
        public List<SavedEntry> HiddenPrograms { get; set; }
        public List<SavedEntry> ManualPrograms { get; set; }
        public List<SaveLocation> SaveLocations { get; set; }
        public List<string> SavesWithSettings { get; set; }
        public bool BackgroundMonitor { get; set; }

        /// <summary>Giochi di questo PC (chiave delle immagini → ultima volta che è stato visto): le loro immagini non si toccano.</summary>
        public Dictionary<string, string> GameKeys { get; set; }

        /// <summary>Giochi e programmi visti all'ultimo avvio (Id → nome): servono a dire cosa è cambiato da allora.</summary>
        public Dictionary<string, string> Apps { get; set; }

        public DateTime? LastSeenDate { get { return ParseDate(LastSeen); } }
        public DateTime? FirstSeenDate { get { return ParseDate(FirstSeen); } }

        private static DateTime? ParseDate(string s)
        {
            DateTime d;
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d) ? d : (DateTime?)null;
        }
    }

    /// <summary>Il PC su cui il programma sta girando: lo riconosce, sa se è nuovo e ne conserva il profilo.</summary>
    internal static class ThisPc
    {
        /// <summary>Un PC non visto da così tanti giorni viene dimenticato, insieme alle immagini che servivano solo a lui.</summary>
        public const int ForgetAfterDays = 180;

        /// <summary>Un gioco sparito resta "noto" per questi giorni: un disco staccato o una reinstallazione non fanno riscaricare le immagini.</summary>
        public const int GameGraceDays = 14;

        private static readonly object Sync = new object();
        private static string id;
        private static PcProfile profile;

        /// <summary>Solo per le prove: cartella dei profili e identità sono quelle indicate qui.</summary>
        internal static string IdOverride;

        private static string Dir { get { return Path.Combine(AppPaths.DataDir, "pc"); } }
        private static string FileOf(string pcId) { return Path.Combine(Dir, pcId + ".json"); }

        /// <summary>Impronta anonima del PC: deriva dall'ID di Windows, che non viene mai salvato né inviato.</summary>
        public static string Id
        {
            get
            {
                if (IdOverride != null) return IdOverride;
                if (id != null) return id;
                string env = Environment.GetEnvironmentVariable("APPMANAGER_DATA") == null ? null : Environment.GetEnvironmentVariable("APPMANAGER_PC");
                if (!string.IsNullOrEmpty(env)) return id = env;
                string guid = null;
                try
                {
                    using (var k = RegUtil.Open(@"HKLM\SOFTWARE\Microsoft\Cryptography")) guid = RegUtil.GetString(k, "MachineGuid");
                }
                catch (Exception ex) { Log.Error("Lettura dell'identità del PC", ex); }
                string source = string.IsNullOrWhiteSpace(guid) ? "nome:" + Environment.MachineName : guid.Trim().ToLowerInvariant();
                using (var sha = SHA256.Create())
                    id = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("AppManager|" + source))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
                return id;
            }
        }

        public static string Name { get { return Environment.MachineName; } }

        /// <summary>Vero se AppManager non aveva mai girato su questo PC.</summary>
        public static bool IsNew { get; private set; }

        /// <summary>
        /// Data di installazione di Windows. Un aggiornamento maggiore azzera quella corrente e conserva la vecchia in
        /// SYSTEM\Setup\Source OS: vale la più antica. Una reinstallazione da zero, invece, ricomincia il conto.
        /// </summary>
        public static DateTime? WindowsInstalled
        {
            get
            {
                DateTime? oldest = null;
                try
                {
                    using (var k = RegUtil.Open(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion")) oldest = Older(oldest, k);
                    using (var setup = RegUtil.Open(@"HKLM\SYSTEM\Setup"))
                        foreach (var name in RegUtil.SubKeys(setup))
                            if (name.StartsWith("Source OS", StringComparison.OrdinalIgnoreCase))
                                using (var k = setup.OpenSubKey(name)) oldest = Older(oldest, k);
                }
                catch (Exception ex) { Log.Error("Lettura della data di installazione di Windows", ex); }
                return oldest;
            }
        }

        private static DateTime? Older(DateTime? current, Microsoft.Win32.RegistryKey key)
        {
            object v = key == null ? null : key.GetValue("InstallDate");
            if (!(v is int) || (int)v == 0) return current;
            var date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds((uint)(int)v).ToLocalTime();
            return !current.HasValue || date < current.Value ? date : current;
        }

        /// <summary>Durata leggibile: "3 anni, 2 mesi", "6 mesi", "10 giorni".</summary>
        internal static string Age(DateTime from, DateTime to)
        {
            int months = (to.Year - from.Year) * 12 + to.Month - from.Month;
            if (to.Day < from.Day) months--;
            if (months >= 12)
            {
                string years = Fmt.Count(months / 12, "anno", "anni");
                return months % 12 == 0 ? years : years + ", " + Fmt.Count(months % 12, "mese", "mesi");
            }
            if (months >= 1) return Fmt.Count(months, "mese", "mesi");
            int days = (int)(to.Date - from.Date).TotalDays;
            return days < 1 ? L.T("meno di un giorno") : Fmt.Count(days, "giorno", "giorni");
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        /// <summary>
        /// Quando il PC è stato acceso l'ultima volta. Il contatore di sistema da solo non basta: con l'avvio rapido di Windows
        /// non si azzera allo spegnimento e conta anche la sospensione. Vale il più recente tra contatore, evento di avvio
        /// (registrato anche con l'avvio rapido) ed evento di ripresa dalla sospensione. Legge il registro eventi: da chiamare in background.
        /// </summary>
        public static DateTime LastPowerOn()
        {
            var now = DateTime.Now;
            var start = now.AddMilliseconds(-(double)GetTickCount64());
            try
            {
                const string xpath = "*[System[(Provider[@Name='Microsoft-Windows-Kernel-Boot'] and EventID=27) or (Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1)]]";
                var query = new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName, xpath) { ReverseDirection = true };
                using (var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query))
                using (var last = reader.ReadEvent())
                {
                    if (last != null && last.TimeCreated.HasValue && last.TimeCreated.Value > start && last.TimeCreated.Value <= now) start = last.TimeCreated.Value;
                }
            }
            catch (Exception ex) { Log.Warn("Registro eventi di sistema non leggibile: {0}", ex.Message); }
            return start;
        }

        /// <summary>Durata leggibile dell'accensione: "3 giorni, 4 ore", "1 ora, 16 minuti", "5 minuti".</summary>
        internal static string Uptime(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return L.T("meno di un minuto");
            if (t.TotalHours < 1) return Fmt.Count((int)t.TotalMinutes, "minuto", "minuti");
            if (t.TotalDays < 1)
            {
                string hours = Fmt.Count((int)t.TotalHours, "ora", "ore");
                return t.Minutes == 0 ? hours : hours + ", " + Fmt.Count(t.Minutes, "minuto", "minuti");
            }
            string days = Fmt.Count((int)t.TotalDays, "giorno", "giorni");
            return t.Hours == 0 ? days : days + ", " + Fmt.Count(t.Hours, "ora", "ore");
        }

        /// <summary>"Windows installato da 6 mesi (18 apr 2026)"; null se la data non si può leggere.</summary>
        public static string WindowsAgeText
        {
            get
            {
                var installed = WindowsInstalled;
                return installed.HasValue ? L.F("Windows installato da {0} ({1})", Age(installed.Value, DateTime.Now), Fmt.Date(installed)) : null;
            }
        }

        /// <summary>
        /// Abbina le impostazioni appena lette a questo PC. Un PC conosciuto ritrova le sue cartelle e i suoi elementi nascosti;
        /// uno nuovo parte pulito, senza percorsi che valgono per un altro PC.
        /// </summary>
        internal static void Bind(AppSettings s, bool settingsFileExisted)
        {
            lock (Sync)
            {
                string now = DateTime.Now.ToString("o");
                var known = Read(Id);
                bool adopt = known == null && settingsFileExisted && (s.LastPcId == null || s.LastPcId == Id);
                IsNew = known == null && !adopt;
                if (known != null)
                {
                    profile = known;
                    ApplyTo(known, s);
                }
                else
                {
                    // le impostazioni della versione precedente erano di un solo PC: questo è quello a cui appartenevano
                    profile = new PcProfile { Id = Id, FirstSeen = adopt ? FileDate(AppPaths.SettingsFile) ?? now : now };
                    if (!adopt) ResetPerPc(s);
                }
                profile.Id = Id;
                profile.Name = Name;
                profile.LastSeen = now;
                if (profile.GameKeys == null) profile.GameKeys = new Dictionary<string, string>();
                if (profile.Apps == null) profile.Apps = new Dictionary<string, string>();
                s.LastPcId = Id;
                CopyFrom(s, profile);
                if (IsNew) Log.Info("Nuovo PC riconosciuto: {0}", Name);
            }
        }

        private static string FileDate(string file)
        {
            try { return File.Exists(file) ? File.GetCreationTime(file).ToString("o") : null; }
            catch { return null; }
        }

        private static void ResetPerPc(AppSettings s)
        {
            s.BackupFolder = AppPaths.DefaultBackupDir;
            s.SaveVaultFolder = AppPaths.DefaultSaveVaultDir;
            s.LibraryFolders = new List<string>();
            s.ProgramFolders = new List<string>();
            s.HiddenGames = new List<SavedEntry>();
            s.ManualGames = new List<SavedEntry>();
            s.HiddenPrograms = new List<SavedEntry>();
            s.ManualPrograms = new List<SavedEntry>();
            s.SaveLocations = new List<SaveLocation>();
            s.SavesWithSettings = new List<string>();
            s.BackgroundMonitor = false;
        }

        private static void ApplyTo(PcProfile p, AppSettings s)
        {
            s.BackupFolder = UsableFolder(p.BackupFolder, AppPaths.DefaultBackupDir);
            s.SaveVaultFolder = UsableFolder(p.SaveVaultFolder, AppPaths.DefaultSaveVaultDir);
            s.LibraryFolders = p.LibraryFolders ?? new List<string>();
            s.ProgramFolders = p.ProgramFolders ?? new List<string>();
            s.HiddenGames = p.HiddenGames ?? new List<SavedEntry>();
            s.ManualGames = p.ManualGames ?? new List<SavedEntry>();
            s.HiddenPrograms = p.HiddenPrograms ?? new List<SavedEntry>();
            s.ManualPrograms = p.ManualPrograms ?? new List<SavedEntry>();
            s.SaveLocations = p.SaveLocations ?? new List<SaveLocation>();
            s.SavesWithSettings = p.SavesWithSettings ?? new List<string>();
            s.BackgroundMonitor = p.BackgroundMonitor;
        }

        /// <summary>Una cartella su un disco che ora non c'è (o in un profilo utente diverso) torna a quella predefinita.</summary>
        private static string UsableFolder(string folder, string fallback)
        {
            if (string.IsNullOrEmpty(folder) || PathUtil.Normalize(folder) == null) return fallback;
            try
            {
                if (Directory.Exists(folder)) return folder;
                string parent = Path.GetDirectoryName(folder);
                return parent != null && Directory.Exists(parent) ? folder : fallback;
            }
            catch { return fallback; }
        }

        private static void CopyFrom(AppSettings s, PcProfile p)
        {
            p.BackupFolder = s.BackupFolder;
            p.SaveVaultFolder = s.SaveVaultFolder;
            p.LibraryFolders = s.LibraryFolders;
            p.ProgramFolders = s.ProgramFolders;
            p.HiddenGames = s.HiddenGames;
            p.ManualGames = s.ManualGames;
            p.HiddenPrograms = s.HiddenPrograms;
            p.ManualPrograms = s.ManualPrograms;
            p.SaveLocations = s.SaveLocations;
            p.SavesWithSettings = s.SavesWithSettings;
            p.BackgroundMonitor = s.BackgroundMonitor;
        }

        /// <summary>Salva il profilo con i valori attuali delle impostazioni (chiamato da AppSettings.Save).</summary>
        internal static void Save(AppSettings s)
        {
            lock (Sync)
            {
                if (profile == null) return;
                CopyFrom(s, profile);
                Write();
            }
        }

        private static void Write()
        {
            if (AppSettings.SaveDisabled) return;
            try
            {
                Directory.CreateDirectory(Dir);
                PathUtil.WriteAllTextAtomic(FileOf(profile.Id), Json.Serialize(profile));
            }
            catch (Exception ex) { Log.Error("Salvataggio del profilo del PC", ex); }
        }

        private static PcProfile Read(string pcId)
        {
            string file = FileOf(pcId);
            try { return File.Exists(file) ? Json.Deserialize<PcProfile>(File.ReadAllText(file)) : null; }
            catch (Exception ex)
            {
                Log.Error("Lettura del profilo del PC", ex);
                return null;
            }
        }

        /// <summary>Gli altri PC su cui questa copia di AppManager ha girato, dal più recente.</summary>
        public static List<PcProfile> Others()
        {
            var list = new List<PcProfile>();
            foreach (var f in PathUtil.Files(Dir, "*.json"))
            {
                string pcId = Path.GetFileNameWithoutExtension(f);
                if (pcId == Id) continue;
                var p = Read(pcId);
                if (p != null) { p.Id = pcId; list.Add(p); }
            }
            return list.OrderByDescending(p => p.LastSeenDate ?? DateTime.MinValue).ToList();
        }

        /// <summary>Dimentica un altro PC: le immagini che servivano solo a lui si liberano alla prossima pulizia.</summary>
        public static void Forget(string pcId)
        {
            if (pcId == Id) return;
            try
            {
                string file = FileOf(pcId);
                if (File.Exists(file)) File.Delete(file);
                // cronologia e istantanea della sorveglianza di quel PC
                foreach (var ext in new[] { ".history", ".watch" })
                    if (File.Exists(Path.Combine(Dir, pcId + ext))) File.Delete(Path.Combine(Dir, pcId + ext));
                Log.Info("Profilo di un altro PC dimenticato");
            }
            catch (Exception ex) { Log.Error("Rimozione del profilo di un altro PC", ex); }
        }

        /// <summary>Toglie i profili dei PC che non si vedono da troppo tempo. Restituisce quanti.</summary>
        public static int ForgetExpired()
        {
            int n = 0;
            var limit = DateTime.Now.AddDays(-ForgetAfterDays);
            foreach (var p in Others())
            {
                if (p.LastSeenDate.HasValue && p.LastSeenDate.Value >= limit) continue;
                Forget(p.Id);
                n++;
            }
            return n;
        }

        /// <summary>Annota i giochi visti ora su questo PC; dimentica quelli spariti da più di <see cref="GameGraceDays"/> giorni.</summary>
        public static void NoteGames(IEnumerable<string> keys)
        {
            lock (Sync)
            {
                if (profile == null) return;
                string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                foreach (var k in keys) profile.GameKeys[k] = today;
                var limit = DateTime.Now.AddDays(-GameGraceDays);
                foreach (var k in profile.GameKeys.Keys.ToList())
                {
                    DateTime seen;
                    if (!DateTime.TryParseExact(profile.GameKeys[k], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out seen) || seen < limit)
                        profile.GameKeys.Remove(k);
                }
                Write();
            }
        }

        /// <summary>I giochi noti su questo PC (quelli visti di recente).</summary>
        public static HashSet<string> MyGameKeys()
        {
            lock (Sync) return profile == null ? new HashSet<string>() : new HashSet<string>(profile.GameKeys.Keys);
        }

        /// <summary>Per ogni gioco noto su un altro PC, quando quel PC è stato visto l'ultima volta.</summary>
        public static Dictionary<string, DateTime> OtherPcGameKeys()
        {
            var map = new Dictionary<string, DateTime>();
            foreach (var p in Others())
            {
                if (p.GameKeys == null) continue;
                var seen = p.LastSeenDate ?? DateTime.MinValue;
                foreach (var k in p.GameKeys.Keys)
                {
                    DateTime old;
                    if (!map.TryGetValue(k, out old) || old < seen) map[k] = seen;
                }
            }
            return map;
        }

        /// <summary>
        /// Confronta giochi e programmi attuali con quelli visti all'ultimo avvio e ricorda i nuovi.
        /// Al primo avvio su un PC non c'è nulla con cui confrontare: gli elenchi restano vuoti.
        /// </summary>
        public static void CompareApps(IDictionary<string, string> current, out List<string> added, out List<string> removed)
        {
            lock (Sync)
            {
                added = new List<string>();
                removed = new List<string>();
                if (profile == null) return;
                if (profile.Apps.Count > 0)
                {
                    added = current.Where(a => !profile.Apps.ContainsKey(a.Key)).Select(a => a.Value).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
                    removed = profile.Apps.Where(a => !current.ContainsKey(a.Key)).Select(a => a.Value).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
                }
                profile.Apps = new Dictionary<string, string>(current);
                Write();
            }
        }

        /// <summary>Solo per le prove: si riparte come se il programma fosse stato appena aperto.</summary>
        internal static void ResetForTests()
        {
            lock (Sync)
            {
                profile = null;
                IsNew = false;
            }
        }
    }
}
