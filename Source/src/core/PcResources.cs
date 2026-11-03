using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace AppManager
{
    /// <summary>
    /// Le risorse che AppManager usa e scarica: spazio nella cartella Dati, file temporanei, cartelle lasciate sul PC, memoria.
    /// Si tengono in ordine da sole: il programma è portable e non deve accumulare nulla, né sulla chiavetta né sul PC.
    /// </summary>
    internal static class PcResources
    {
        /// <summary>Sotto questo spazio libero sull'unità dei dati non si scaricano più immagini.</summary>
        public const long LowSpaceBytes = 64L * 1024 * 1024;

        private const int TempMaxAgeHours = 24;

        private static DateTime lowSpaceChecked;
        private static bool lowSpace;

        /// <summary>Spazio libero sull'unità della cartella Dati (-1 se non si può leggere).</summary>
        public static long FreeSpace()
        {
            try
            {
                string root = Path.GetPathRoot(AppPaths.DataDir);
                return string.IsNullOrEmpty(root) || root.StartsWith(@"\\") ? -1 : new DriveInfo(root).AvailableFreeSpace;
            }
            catch { return -1; }
        }

        /// <summary>L'unità dei dati è quasi piena (controllo al massimo una volta al minuto).</summary>
        public static bool IsLowSpace
        {
            get
            {
                if ((DateTime.Now - lowSpaceChecked).TotalSeconds > 60)
                {
                    long free = FreeSpace();
                    lowSpace = free >= 0 && free < LowSpaceBytes;
                    lowSpaceChecked = DateTime.Now;
                }
                return lowSpace;
            }
        }

        /// <summary>Limite effettivo per le immagini: quello scelto o, in automatico, il 15% dello spazio disponibile (da 50 MB a 1 GB).</summary>
        public static long CoverBudget(long currentCacheBytes)
        {
            int chosen = AppSettings.Current.CoverBudgetMb;
            if (chosen > 0) return chosen * 1024L * 1024;
            long free = FreeSpace();
            if (free < 0) return 1024L * 1024 * 1024;
            long auto = (free + currentCacheBytes) / 100 * 15;
            return Math.Max(50L * 1024 * 1024, Math.Min(1024L * 1024 * 1024, auto));
        }

        /// <summary>Manutenzione che non richiede l'elenco dei giochi: da eseguire in background all'avvio.</summary>
        public static void StartupMaintenance()
        {
            try
            {
                int forgotten = ThisPc.ForgetExpired();
                if (forgotten > 0) Log.Info("Dimenticati {0} PC non più visti da oltre {1} giorni", forgotten, ThisPc.ForgetAfterDays);
                int temp = CleanTempLeftovers(Path.GetTempPath());
                int data = CleanDataLeftovers(AppPaths.DataDir);
                if (temp + data > 0) Log.Info("Pulizia automatica: tolti {0} file temporanei rimasti da operazioni interrotte", temp + data);
            }
            catch (Exception ex) { Log.Error("Manutenzione automatica", ex); }
        }

        /// <summary>File temporanei di AppManager rimasti dopo un'interruzione (solo quelli vecchi: uno recente può essere in uso).</summary>
        internal static int CleanTempLeftovers(string tempDir)
        {
            int n = 0;
            foreach (var pattern in new[] { "am_task_*.xml", "am_restore_*.reg" })
                foreach (var f in PathUtil.Files(tempDir, pattern))
                    if (DeleteIfOld(f)) n++;
            return n;
        }

        /// <summary>Scritture atomiche interrotte (*.tmp) e copie di impostazioni danneggiate ormai inutili, nella cartella Dati.</summary>
        internal static int CleanDataLeftovers(string dataDir)
        {
            int n = 0;
            foreach (var f in PathUtil.Files(dataDir, "*.tmp")) if (DeleteIfOld(f)) n++;
            foreach (var f in PathUtil.Files(Path.Combine(dataDir, "pc"), "*.tmp")) if (DeleteIfOld(f)) n++;
            foreach (var f in PathUtil.Files(Path.Combine(dataDir, "covers"), "*.tmp")) if (DeleteIfOld(f)) n++;
            string damaged = Path.Combine(dataDir, "settings.json.danneggiato");
            try
            {
                if (File.Exists(damaged) && (DateTime.Now - File.GetLastWriteTime(damaged)).TotalDays > 30) { File.Delete(damaged); n++; }
            }
            catch { }
            return n;
        }

        private static bool DeleteIfOld(string file)
        {
            try
            {
                if ((DateTime.Now - File.GetLastWriteTime(file)).TotalHours < TempMaxAgeHours) return false;
                File.Delete(file);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Alla chiusura: le cartelle predefinite di backup e salvataggi che sono rimaste vuote non restano sul PC.
        /// Si tolgono solo cartelle senza nemmeno un file; quelle scelte a mano dall'utente restano dove sono.
        /// </summary>
        public static void RemoveEmptyFoldersOnExit()
        {
            var s = AppSettings.Current;
            foreach (var pair in new[] { Tuple.Create(s.BackupFolder, AppPaths.DefaultBackupDir), Tuple.Create(s.SaveVaultFolder, AppPaths.DefaultSaveVaultDir) })
                if (PathUtil.Same(pair.Item1, pair.Item2)) RemoveIfEmpty(pair.Item2);
        }

        /// <summary>Toglie la cartella e le sue sottocartelle se in tutto l'albero non c'è nessun file. I collegamenti non si seguono.</summary>
        internal static bool RemoveIfEmpty(string dir)
        {
            try
            {
                if (!Directory.Exists(dir) || PathUtil.IsReparsePoint(dir)) return false;
                bool empty = true;
                foreach (var d in PathUtil.Dirs(dir)) if (!RemoveIfEmpty(d)) empty = false;
                if (!empty || Directory.EnumerateFileSystemEntries(dir).Any()) return false;
                Directory.Delete(dir, false);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Porta nella cartella Dati ciò che le versioni precedenti (non portable) avevano sul PC, poi toglie quella cartella.
        /// Si eliminano solo i file che AppManager stesso ha creato; una cartella con altro dentro resta.
        /// </summary>
        internal static void AdoptLegacyData(string legacyDir)
        {
            if (!AppPaths.IsPortable || !Directory.Exists(legacyDir) || PathUtil.Same(legacyDir, AppPaths.DataDir)) return;
            try
            {
                string target = AppPaths.SettingsFile;
                string oldSettings = Path.Combine(legacyDir, "settings.json");
                if (File.Exists(oldSettings))
                {
                    if (!File.Exists(target)) File.Copy(oldSettings, target);
                    else MergeLegacySettings(oldSettings, target);
                }
                MergeCovers(Path.Combine(legacyDir, "covers"), Path.Combine(AppPaths.DataDir, "covers"));
                foreach (var name in new[] { "settings.json", "settings.json.danneggiato", "log.txt", "log.txt.old", "log.old" })
                {
                    string f = Path.Combine(legacyDir, name);
                    if (File.Exists(f)) File.Delete(f);
                }
                RemoveIfEmpty(legacyDir);
                Log.Info("Dati della versione precedente spostati in {0}", AppPaths.DataDir);
            }
            catch (Exception ex) { Log.Error("Spostamento dei dati della versione precedente", ex); }
        }

        /// <summary>Elementi nascosti, aggiunti a mano e chiave di SteamGridDB presenti solo nella vecchia cartella non vanno persi.</summary>
        private static void MergeLegacySettings(string oldFile, string newFile)
        {
            var oldS = Json.Deserialize<AppSettings>(File.ReadAllText(oldFile));
            var cur = Json.Deserialize<AppSettings>(File.ReadAllText(newFile));
            if (oldS == null || cur == null) return;
            if (string.IsNullOrWhiteSpace(cur.SteamGridDbKey)) cur.SteamGridDbKey = oldS.SteamGridDbKey;
            cur.LibraryFolders = Union(cur.LibraryFolders, oldS.LibraryFolders);
            cur.ProgramFolders = Union(cur.ProgramFolders, oldS.ProgramFolders);
            cur.HiddenGames = Union(cur.HiddenGames, oldS.HiddenGames);
            cur.ManualGames = Union(cur.ManualGames, oldS.ManualGames);
            cur.HiddenPrograms = Union(cur.HiddenPrograms, oldS.HiddenPrograms);
            cur.ManualPrograms = Union(cur.ManualPrograms, oldS.ManualPrograms);
            PathUtil.WriteAllTextAtomic(newFile, Json.Serialize(cur));
        }

        private static List<string> Union(List<string> a, List<string> b)
        {
            return (a ?? new List<string>()).Concat(b ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<SavedEntry> Union(List<SavedEntry> a, List<SavedEntry> b)
        {
            return (a ?? new List<SavedEntry>()).Concat(b ?? new List<SavedEntry>()).Where(x => x != null && !string.IsNullOrWhiteSpace(x.Path))
                .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        }

        private static void MergeCovers(string from, string to)
        {
            if (!Directory.Exists(from)) return;
            Directory.CreateDirectory(to);
            var oldIndex = ReadIndex(Path.Combine(from, "index.json"));
            var newIndex = ReadIndex(Path.Combine(to, "index.json"));
            foreach (var e in oldIndex) if (!newIndex.ContainsKey(e.Key)) newIndex[e.Key] = e.Value;
            foreach (var f in PathUtil.Files(from))
            {
                string name = Path.GetFileName(f);
                if (name.Equals("index.json", StringComparison.OrdinalIgnoreCase)) continue;
                string dest = Path.Combine(to, name);
                if (File.Exists(dest)) File.Delete(f);
                else File.Move(f, dest);
            }
            PathUtil.WriteAllTextAtomic(Path.Combine(to, "index.json"), Json.Serialize(newIndex));
            File.Delete(Path.Combine(from, "index.json"));
            RemoveIfEmpty(from);
        }

        private static Dictionary<string, object> ReadIndex(string file)
        {
            try { return (File.Exists(file) ? Json.Parse(File.ReadAllText(file)) as Dictionary<string, object> : null) ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }

        // ------------------------------------------------------------------ Memoria

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

        /// <summary>Con la finestra ridotta a icona il programma non ha bisogno di tenere in memoria immagini e interfaccia.</summary>
        public static void TrimMemory()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                using (var p = System.Diagnostics.Process.GetCurrentProcess())
                    SetProcessWorkingSetSize(p.Handle, new IntPtr(-1), new IntPtr(-1));
            }
            catch { }
        }
    }
}
