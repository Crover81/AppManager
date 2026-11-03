using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace AppManager
{
    // ====================================================================== Log

    /// <summary>Una voce del registro attività (le righe di dettaglio di un errore fanno parte della voce precedente).</summary>
    internal sealed class LogEntry
    {
        public DateTime Time;
        public string Level;     // INFO, AVVISO, ERRORE, DETTAGLI
        public string Message;
    }

    /// <summary>
    /// Registro attività (log.txt nella cartella dati). Le righe identiche consecutive vengono compattate
    /// ("ripetuto N volte"), le voci più vecchie dei giorni di conservazione vengono tolte e il file non supera
    /// <see cref="MaxBytes"/>: oltre, si tolgono le voci più vecchie (non c'è più un log.txt.old da 4 MB).
    /// Non legge mai le impostazioni: il loro caricamento scrive a sua volta nel registro.
    /// </summary>
    internal static class Log
    {
        private static readonly object Sync = new object();
        private const long MaxBytes = 2 * 1024 * 1024;
        private const long TrimTo = 1536 * 1024;
        private static readonly Regex EntryStart = new Regex(@"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) \[([A-Z]+)\] ?(.*)$", RegexOptions.Compiled);

        // compattazione delle ripetizioni: l'ultima riga scritta e quante volte è arrivata di nuovo
        private static string lastLevel, lastMessage;
        private static int repeats;
        private static DateTime lastRepeat;

        public static string FilePath { get { return Path.Combine(AppPaths.DataDir, "log.txt"); } }

        public static void Info(string format, params object[] args)
        {
            Write("INFO", args.Length == 0 ? L.T(format) : string.Format(L.T(format), args));
        }

        /// <summary>Qualcosa non è andato come previsto ma l'operazione è proseguita.</summary>
        public static void Warn(string format, params object[] args)
        {
            Write("AVVISO", args.Length == 0 ? L.T(format) : string.Format(L.T(format), args));
        }

        public static void Error(string context, Exception ex)
        {
            Write("ERRORE", L.T(context) + ": " + (ex == null ? "" : ex.GetType().Name + " - " + ex.Message));
            // per gli errori imprevisti dell'interfaccia serve anche la traccia completa
            if (ex != null && context.StartsWith("Errore")) Write("DETTAGLI", ex.ToString());
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Sync)
                {
                    var now = DateTime.Now;
                    if (level == lastLevel && message == lastMessage && level != "DETTAGLI")
                    {
                        repeats++;
                        lastRepeat = now;
                        return;
                    }
                    Directory.CreateDirectory(AppPaths.DataDir);
                    var sb = new StringBuilder();
                    AppendRepeats(sb);
                    sb.AppendFormat("{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}\r\n", now, level, message);
                    File.AppendAllText(FilePath, sb.ToString(), Encoding.UTF8);
                    lastLevel = level;
                    lastMessage = message;
                    var fi = new FileInfo(FilePath);
                    if (fi.Length > MaxBytes) Rewrite(DateTime.MinValue, TrimTo);
                }
            }
            catch { }
        }

        private static void AppendRepeats(StringBuilder sb)
        {
            if (repeats == 0) return;
            sb.AppendFormat("{0:yyyy-MM-dd HH:mm:ss} [{1}] ↑ {2}\r\n", lastRepeat, lastLevel,
                repeats == 1 ? L.T("ripetuto ancora una volta") : L.F("ripetuto altre {0} volte", repeats));
            repeats = 0;
        }

        /// <summary>Scrive la nota sulle ripetizioni in sospeso (da chiamare alla chiusura).</summary>
        public static void Flush()
        {
            try
            {
                lock (Sync)
                {
                    if (repeats == 0) return;
                    var sb = new StringBuilder();
                    AppendRepeats(sb);
                    File.AppendAllText(FilePath, sb.ToString(), Encoding.UTF8);
                    lastMessage = null;
                }
            }
            catch { }
        }

        /// <summary>
        /// Manutenzione all'avvio: toglie le voci più vecchie di <paramref name="retentionDays"/> giorni (0 = nessun limite),
        /// riporta il file sotto la dimensione massima e cancella il vecchio log.txt.old delle versioni precedenti.
        /// </summary>
        public static void Maintain(int retentionDays)
        {
            try
            {
                lock (Sync)
                {
                    foreach (var old in new[] { FilePath + ".old", Path.Combine(AppPaths.DataDir, "log.old") })
                        if (File.Exists(old)) File.Delete(old);
                    if (!File.Exists(FilePath)) return;
                    var cutoff = retentionDays > 0 ? DateTime.Now.AddDays(-retentionDays) : DateTime.MinValue;
                    Rewrite(cutoff, MaxBytes);
                }
            }
            catch (Exception ex) { Error("Manutenzione del registro attività", ex); }
        }

        /// <summary>Riscrive il file togliendo le voci più vecchie di <paramref name="cutoff"/> e, se serve, le più vecchie oltre <paramref name="maxBytes"/>.</summary>
        private static void Rewrite(DateTime cutoff, long maxBytes)
        {
            var lines = File.ReadAllLines(FilePath, Encoding.UTF8);
            // inizio di ogni voce (le righe di dettaglio seguono la voce a cui appartengono)
            var starts = new List<int>();
            var times = new List<DateTime>();
            for (int i = 0; i < lines.Length; i++)
            {
                var m = EntryStart.Match(lines[i]);
                DateTime t;
                if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                {
                    starts.Add(i);
                    times.Add(t);
                }
            }
            int first = 0;
            while (first < starts.Count && times[first] < cutoff) first++;
            int fromLine = first < starts.Count ? starts[first] : lines.Length;
            long size = 0;
            for (int i = fromLine; i < lines.Length; i++) size += Encoding.UTF8.GetByteCount(lines[i]) + 2;
            while (size > maxBytes && first + 1 < starts.Count)
            {
                for (int i = starts[first]; i < starts[first + 1]; i++) size -= Encoding.UTF8.GetByteCount(lines[i]) + 2;
                first++;
                fromLine = starts[first];
            }
            if (fromLine == 0) return;
            var keep = new StringBuilder();
            for (int i = fromLine; i < lines.Length; i++) keep.Append(lines[i]).Append("\r\n");
            PathUtil.WriteAllTextAtomic(FilePath, keep.ToString());
        }

        /// <summary>Legge le voci del registro, dalla più recente.</summary>
        public static List<LogEntry> ReadEntries(int max)
        {
            var result = new List<LogEntry>();
            string[] lines;
            try
            {
                lock (Sync)
                {
                    if (!File.Exists(FilePath)) return result;
                    using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var r = new StreamReader(fs, Encoding.UTF8))
                        lines = r.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                }
            }
            catch (Exception ex)
            {
                Error("Lettura del registro attività", ex);
                return result;
            }
            LogEntry cur = null;
            foreach (var line in lines)
            {
                var m = EntryStart.Match(line.TrimStart('\uFEFF'));
                DateTime t;
                if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                {
                    cur = new LogEntry { Time = t, Level = m.Groups[2].Value, Message = m.Groups[3].Value };
                    result.Add(cur);
                }
                else if (cur != null && line.Length > 0)
                {
                    cur.Message += "\n" + line;
                }
            }
            result.Reverse();
            if (result.Count > max) result.RemoveRange(max, result.Count - max);
            return result;
        }

        public static long SizeBytes
        {
            get
            {
                try { var fi = new FileInfo(FilePath); return fi.Exists ? fi.Length : 0; }
                catch { return 0; }
            }
        }

        /// <summary>Svuota il registro lasciando una riga che dice quando è stato svuotato.</summary>
        public static void Clear()
        {
            lock (Sync)
            {
                repeats = 0;
                lastMessage = null;
                PathUtil.WriteAllTextAtomic(FilePath, string.Format("{0:yyyy-MM-dd HH:mm:ss} [INFO] Registro attività svuotato\r\n", DateTime.Now));
            }
        }

        /// <summary>Copia il registro in <paramref name="target"/> (per inviarlo o conservarlo).</summary>
        public static void Export(string target)
        {
            Flush();
            lock (Sync) File.Copy(FilePath, target, true);
        }
    }

    // ====================================================================== Percorsi noti

    internal static class AppPaths
    {
        private static string dataDir;
        private static bool portable;
        private const string LegacyName = "GameManager";

        /// <summary>
        /// Cartella di impostazioni, registro attività e cache: sempre "Dati" accanto al programma.
        /// Solo se lì non si può scrivere (chiavetta protetta, cartella di sola lettura) si usa quella del PC.
        /// </summary>
        public static string DataDir
        {
            get
            {
                if (dataDir == null) Resolve();
                return dataDir;
            }
        }

        /// <summary>Falso solo se la cartella accanto al programma non è scrivibile.</summary>
        public static bool IsPortable
        {
            get
            {
                if (dataDir == null) Resolve();
                return portable;
            }
        }

        private static bool IsTestDataDir { get { return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPMANAGER_DATA")); } }

        private static void Resolve()
        {
            // solo per i test automatici: una cartella dati separata, per non toccare quella vera
            string testDir = Environment.GetEnvironmentVariable("APPMANAGER_DATA");
            if (!string.IsNullOrEmpty(testDir))
            {
                try
                {
                    Directory.CreateDirectory(testDir);
                    dataDir = testDir;
                    portable = true;
                    return;
                }
                catch { }
            }
            try
            {
                var entry = System.Reflection.Assembly.GetEntryAssembly();
                string exeDir = entry == null ? null : Path.GetDirectoryName(entry.Location);
                if (exeDir != null)
                {
                    string dir = Path.Combine(exeDir, "Dati");
                    Directory.CreateDirectory(dir);
                    string probe = Path.Combine(dir, ".scrittura");
                    File.WriteAllText(probe, "");
                    File.Delete(probe);
                    dataDir = dir;
                    portable = true;
                    return;
                }
            }
            catch
            {
                // chiavetta protetta da scrittura o piena: si usa la cartella del PC
            }
            dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppManager");
            portable = false;
        }

        /// <summary>Cartelle dati delle versioni precedenti (non portable), rimaste sul PC.</summary>
        public static IEnumerable<string> LegacyDataDirs()
        {
            if (IsTestDataDir) yield break;
            string localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(localRoot, "AppManager");
            yield return Path.Combine(localRoot, LegacyName);
        }

        public static string SettingsFile { get { return Path.Combine(DataDir, "settings.json"); } }

        private static string Documents { get { return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); } }

        public static string DefaultBackupDir
        {
            get { return Path.Combine(Documents, "AppManager Backup"); }
        }

        /// <summary>Cartella dei backup usata da GameManager: i backup lì dentro restano visibili e ripristinabili.</summary>
        public static string LegacyBackupDir
        {
            get { return Path.Combine(Documents, LegacyName + " Backup"); }
        }

        /// <summary>Deposito predefinito dei salvataggi dei giochi (istantanee e profili).</summary>
        public static string DefaultSaveVaultDir
        {
            get { return Path.Combine(Documents, "AppManager Salvataggi"); }
        }

        /// <summary>
        /// Cartelle di AppManager stesso (dati, cartella del PC anche in modalità portable, backup, salvataggi):
        /// non sono mai residui da proporre, nemmeno quando AppManager non risulta "installato".
        /// </summary>
        public static IEnumerable<string> OwnDirs()
        {
            string localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return DataDir;
            yield return Path.Combine(localRoot, "AppManager");
            yield return Path.Combine(localRoot, LegacyName);
            yield return DefaultBackupDir;
            yield return LegacyBackupDir;
            yield return DefaultSaveVaultDir;
        }

        /// <summary>
        /// Se le impostazioni puntano ancora alla cartella predefinita di GameManager, passa a quella di AppManager
        /// (spostando i backup già fatti). Restituisce true se le impostazioni sono cambiate.
        /// </summary>
        public static bool MigrateBackupFolder(AppSettings s)
        {
            if (!PathUtil.Same(s.BackupFolder, LegacyBackupDir)) return false;
            try
            {
                if (Directory.Exists(LegacyBackupDir) && !Directory.Exists(DefaultBackupDir)) Directory.Move(LegacyBackupDir, DefaultBackupDir);
                if (Directory.Exists(LegacyBackupDir)) return false; // non spostabile ora: si continua a usarla
            }
            catch (Exception ex)
            {
                Log.Error("Spostamento della cartella dei backup", ex);
                return false;
            }
            s.BackupFolder = DefaultBackupDir;
            return true;
        }
    }

    /// <summary>Cartelle di sistema dove i giochi lasciano dati. Calcolate una sola volta.</summary>
    internal static class KnownDirs
    {
        public static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        public static readonly string RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static readonly string LocalLow = Native.GetKnownFolder(Native.FolderLocalAppDataLow) ?? Path.Combine(UserProfile, "AppData", "LocalLow");
        public static readonly string SavedGames = Native.GetKnownFolder(Native.FolderSavedGames) ?? Path.Combine(UserProfile, "Saved Games");
        public static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        public static readonly string PublicDocuments = Native.GetKnownFolder(Native.FolderPublicDocuments) ?? @"C:\Users\Public\Documents";
        public static readonly string MyGames = Path.Combine(Documents, "My Games");
        public static readonly string Temp = Path.GetTempPath().TrimEnd('\\');
        public static readonly string Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        public static readonly string PublicDesktop = Native.GetKnownFolder(Native.FolderPublicDesktop) ?? @"C:\Users\Public\Desktop";
        public static readonly string StartMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        public static readonly string CommonStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        public static readonly string Videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        public static readonly string Pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        public static readonly string Captures = Native.GetKnownFolder(Native.FolderCaptures) ?? Path.Combine(Videos, "Captures");
        public static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        public static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        public static readonly string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        public static readonly string CrashDumps = Path.Combine(LocalAppData, "CrashDumps");
        public static readonly string VirtualStore = Path.Combine(LocalAppData, "VirtualStore");
        public static readonly string LocalPrograms = Path.Combine(LocalAppData, "Programs");
        public static readonly string Downloads = Native.GetKnownFolder(Native.FolderDownloads) ?? Path.Combine(UserProfile, "Downloads");
        public static readonly string System32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

        /// <summary>
        /// Converte i percorsi scritti con l'ID di una cartella nota, come fa Windows negli ID delle app
        /// ("{6D809377-...}\VideoLAN\VLC\vlc.exe" → "C:\Program Files\VideoLAN\VLC\vlc.exe").
        /// </summary>
        public static string ExpandKnownFolderId(string s)
        {
            if (string.IsNullOrEmpty(s) || s[0] != '{') return s;
            int end = s.IndexOf('}');
            if (end < 0) return s;
            string root;
            switch (s.Substring(1, end - 1).ToUpperInvariant())
            {
                case "6D809377-6AF0-444B-8957-A3773F02200E": root = ProgramFiles; break;
                case "7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E": root = ProgramFilesX86; break;
                case "F38BF404-1D43-42F2-9305-67DE0B28FC23": root = Windows; break;
                case "1AC14E77-02E7-4E5D-B744-2EB1AE5198B7": root = System32; break;
                case "D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27": root = Path.Combine(Windows, "SysWOW64"); break;
                case "F1B32785-6FBA-4FCF-9D55-7B8E7F157091": root = LocalAppData; break;
                case "3EB685DB-65F9-4CF6-A03A-E3EF65729F3D": root = RoamingAppData; break;
                case "62AB5D82-FDC1-4DC3-A9DD-070D1D495D97": root = ProgramData; break;
                case "5E6C858F-0E22-4760-9AFE-EA3317B67173": root = UserProfile; break;
                case "374DE290-123F-4565-9164-39C4925E467B": root = Downloads; break;
                case "B4BFCC3A-DB2C-424C-B029-7FE99A87C641": root = Desktop; break;
                case "FDD39AD0-238F-46AF-ADB4-6C85480369C7": root = Documents; break;
                default: return s;
            }
            return root + s.Substring(end + 1);
        }
    }

    // ====================================================================== Percorsi

    internal static class PathUtil
    {
        /// <summary>
        /// Espande le variabili d'ambiente. Con <paramref name="wow32"/> usa i valori visti dai programmi a 32 bit
        /// (%ProgramFiles% = "Program Files (x86)"), altrimenti quelli normali.
        /// </summary>
        public static string Expand(string s, bool wow32)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('%') < 0) return s;
            if (wow32)
            {
                string pf86 = KnownDirs.ProgramFilesX86, cf86 = Environment.GetEnvironmentVariable("CommonProgramFiles(x86)");
                s = Regex.Replace(s, @"%ProgramFiles%", m => pf86, RegexOptions.IgnoreCase);
                if (cf86 != null) s = Regex.Replace(s, @"%CommonProgramFiles%", m => cf86, RegexOptions.IgnoreCase);
            }
            return Environment.ExpandEnvironmentVariables(s);
        }

        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).Replace('/', '\\');
                // solo percorsi assoluti con lettera di unità: un nome relativo (es. "MsiExec.exe")
                // verrebbe risolto sulla cartella corrente, con risultati sbagliati
                if (path.Length < 3 || path[1] != ':' || path[2] != '\\') return null;
                path = Path.GetFullPath(path);
                // i nomi brevi 8.3 ("C:\PROGRA~1") vanno riportati al nome vero, altrimenti sfuggirebbero alle protezioni
                if (path.IndexOf('~') >= 0) path = Native.GetLongPath(path);
                if (path.Length > 3) path = path.TrimEnd('\\');
                return path;
            }
            catch { return null; }
        }

        public static bool Same(string a, string b)
        {
            return a != null && b != null && string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True se <paramref name="path"/> è dentro <paramref name="root"/> (o coincide, se orSame).</summary>
        public static bool IsUnder(string path, string root, bool orSame = false)
        {
            if (path == null || root == null) return false;
            path = path.TrimEnd('\\');
            root = root.TrimEnd('\\');
            if (path.Length == root.Length) return orSame && string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
            return path.Length > root.Length && path[root.Length] == '\\'
                && path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Forma "\\?\C:\..." per i percorsi lunghi: senza questa forma Windows rifiuta i percorsi oltre i 260 caratteri
        /// (a meno che il supporto ai percorsi lunghi sia attivo nel sistema). I percorsi brevi restano come sono.
        /// </summary>
        public static string Long(string path)
        {
            if (path == null || path.Length < 240 || path.StartsWith(@"\\")) return path;
            return path.Length > 2 && path[1] == ':' && path[2] == '\\' ? @"\\?\" + path : path;
        }

        /// <summary>Toglie il prefisso "\\?\" aggiunto da <see cref="Long"/> (per confronti e messaggi).</summary>
        public static string Plain(string path)
        {
            return path != null && path.Length > 6 && path.StartsWith(@"\\?\") && path[5] == ':' ? path.Substring(4) : path;
        }

        public static bool Exists(string path)
        {
            return !string.IsNullOrEmpty(path) && (Directory.Exists(Long(path)) || File.Exists(Long(path)));
        }

        public static string[] Dirs(string path)
        {
            try
            {
                string p = Long(path);
                return Directory.Exists(p) ? Directory.GetDirectories(p).Select(Plain).ToArray() : new string[0];
            }
            catch { return new string[0]; }
        }

        public static string[] Files(string path, string pattern = "*")
        {
            try
            {
                string p = Long(path);
                return Directory.Exists(p) ? Directory.GetFiles(p, pattern).Select(Plain).ToArray() : new string[0];
            }
            catch { return new string[0]; }
        }

        /// <summary>
        /// True se il percorso è un collegamento (junction, link simbolico) verso un'altra posizione.
        /// I reparse point di OneDrive e simili sono cartelle vere: per loro la risposta è false.
        /// </summary>
        public static bool IsReparsePoint(string path)
        {
            try
            {
                if ((File.GetAttributes(Long(path)) & FileAttributes.ReparsePoint) == 0) return false;
            }
            catch { return false; }
            return Native.IsLink(path);
        }

        /// <summary>
        /// Tutti i file di una cartella e delle sue sottocartelle SENZA entrare nei collegamenti (junction e link simbolici):
        /// un collegamento può puntare ovunque, anche a cartelle di sistema o di altri programmi.
        /// </summary>
        public static IEnumerable<string> FilesNoLinks(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(Long(root)) || IsReparsePoint(root)) yield break;
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                foreach (var f in Files(dir)) yield return f;
                foreach (var d in Dirs(dir))
                    if (!IsReparsePoint(d)) stack.Push(d);
            }
        }

        /// <summary>Sottocartelle (a ogni livello) senza entrare nei collegamenti; i collegamenti stessi sono inclusi in <paramref name="links"/>.</summary>
        public static List<string> DirsNoLinks(string root, List<string> links = null)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(Long(root)) || IsReparsePoint(root)) return result;
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                foreach (var d in Dirs(dir))
                {
                    if (IsReparsePoint(d)) { if (links != null) links.Add(d); continue; }
                    result.Add(d);
                    stack.Push(d);
                }
            }
            return result;
        }

        /// <summary>Dimensione ricorsiva di un file o cartella (ignora link simbolici/junction).</summary>
        public static long Size(string path, CancellationToken ct = default(CancellationToken))
        {
            int files;
            return Size(path, out files, ct);
        }

        public static long Size(string path, out int fileCount, CancellationToken ct = default(CancellationToken))
        {
            DateTime newest;
            return Size(path, out fileCount, out newest, ct);
        }

        /// <summary>Dimensione, numero di file e data del file modificato più di recente.</summary>
        public static long Size(string path, out int fileCount, out DateTime newest, CancellationToken ct = default(CancellationToken))
        {
            fileCount = 0;
            newest = DateTime.MinValue;
            try
            {
                if (File.Exists(Long(path)))
                {
                    var fi = new FileInfo(Long(path));
                    fileCount = 1;
                    newest = fi.LastWriteTime;
                    return fi.Length;
                }
                if (!Directory.Exists(Long(path))) return 0;
                newest = Directory.GetLastWriteTime(Long(path));
            }
            catch { return 0; }
            long total = 0;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(Long(path)));
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) break;
                var dir = stack.Pop();
                IEnumerable<FileSystemInfo> entries;
                try { entries = dir.EnumerateFileSystemInfos(); }
                catch { continue; }
                try
                {
                    foreach (var e in entries)
                    {
                        if ((e.Attributes & FileAttributes.Directory) != 0)
                        {
                            if ((e.Attributes & FileAttributes.ReparsePoint) == 0 || !Native.IsLink(e.FullName)) stack.Push(new DirectoryInfo(Long(Plain(e.FullName))));
                        }
                        else
                        {
                            total += ((FileInfo)e).Length;
                            fileCount++;
                            if (e.LastWriteTime > newest) newest = e.LastWriteTime;
                        }
                    }
                }
                catch { }
            }
            return total;
        }

        /// <summary>Dimensione di una cartella senza le sottocartelle indicate (es. i giochi dentro la cartella di Steam).</summary>
        public static long SizeExcluding(string path, ICollection<string> excluded, CancellationToken ct = default(CancellationToken))
        {
            if (excluded == null || excluded.Count == 0) return Size(path, ct);
            long total = 0;
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) break;
                string dir = stack.Pop();
                foreach (var f in Files(dir))
                {
                    try { total += new FileInfo(Long(f)).Length; } catch { }
                }
                foreach (var d in Dirs(dir))
                {
                    if (IsReparsePoint(d) || excluded.Any(x => Same(x, d))) continue;
                    // le cartelle che contengono quelle escluse si esplorano, le altre si misurano intere
                    if (excluded.Any(x => IsUnder(x, d))) stack.Push(d);
                    else total += Size(d, ct);
                }
            }
            return total;
        }

        /// <summary>
        /// Scrive un file di testo in modo atomico: prima un file temporaneo, poi la sostituzione.
        /// Se il PC si spegne a metà resta la versione precedente, mai un file troncato.
        /// </summary>
        public static void WriteAllTextAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            var bytes = new UTF8Encoding(false).GetBytes(text);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                fs.Write(bytes, 0, bytes.Length);
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null, true); return; }
                catch (IOException) { }                     // es. unità FAT32 di una chiavetta: si sostituisce in due passi
                catch (PlatformNotSupportedException) { }
                File.Copy(tmp, path, true);
                File.Delete(tmp);
            }
            else File.Move(tmp, path);
        }

        public static DateTime? LastWrite(string path)
        {
            try
            {
                if (Directory.Exists(Long(path))) return Directory.GetLastWriteTime(Long(path));
                if (File.Exists(Long(path))) return File.GetLastWriteTime(Long(path));
            }
            catch { }
            return null;
        }

        /// <summary>Estrae il percorso dell'eseguibile da una riga di comando (es. UninstallString).</summary>
        public static string ExeFromCommand(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                return end > 1 ? cmd.Substring(1, end - 1) : null;
            }
            int idx = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return cmd.Substring(0, idx + 4);
            int sp = cmd.IndexOf(' ');
            return sp > 0 ? cmd.Substring(0, sp) : cmd;
        }

        /// <summary>Restituisce il percorso con le maiuscole reali presenti su disco.</summary>
        public static string RealCase(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Exists(path)) return path;
                var parts = path.Split('\\');
                string cur = parts[0].ToUpperInvariant() + "\\";
                for (int i = 1; i < parts.Length; i++)
                {
                    if (parts[i].Length == 0) continue;
                    string match = Directory.EnumerateFileSystemEntries(cur, parts[i]).FirstOrDefault();
                    cur = match ?? Path.Combine(cur, parts[i]);
                }
                return cur;
            }
            catch { return path; }
        }

        public static string SafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }
    }

    // ====================================================================== Nomi

    internal static class NameUtil
    {
        private static readonly Regex[] JunkPatterns =
        {
            new Regex(@"\s*[\[\(][^\]\)]*(repack|elamigos|fitgirl|dodi|multi\d*|build|\bv\d)[^\]\)]*[\]\)]", RegexOptions.IgnoreCase),
            new Regex(@"\s+-?\s*(MULTi\d+|ElAmigos|FitGirl|DODI|KaOs|xatab|Repack)\b.*$", RegexOptions.IgnoreCase),
            new Regex(@"\s+(versione|version|ver\.|v)\s*\d[\w\.\-]*.*$", RegexOptions.IgnoreCase),
            new Regex(@"\s+build\s*\d+.*$", RegexOptions.IgnoreCase),
        };

        private static readonly Regex EditionPattern = new Regex(
            @"\b(the\s+)?(definitive|complete|deluxe|ultimate|gold|goty|game of the year|enhanced|anniversary|remastered|special|premium|standard|digital deluxe|director'?s cut)(\s+edition)?\s*$",
            RegexOptions.IgnoreCase);

        /// <summary>
        /// Nomi troppo generici per essere usati nel confronto con le cartelle: parole tecniche, cartelle di sistema
        /// e grandi editori che raccolgono molti prodotti.
        /// </summary>
        private static readonly HashSet<string> Generic = new HashSet<string>(new[]
        {
            "game", "games", "launcher", "gamelauncher", "setup", "install", "installer", "uninstall", "unins000", "unins001",
            "bin", "bin64", "binaries", "win64", "win32", "x64", "x86", "engine", "data", "content", "redist", "commonredist",
            "directx", "support", "tools", "crashreportclient", "unitycrashhandler64", "unitycrashhandler32", "crashpadhandler",
            "crashpadhandler64", "crashhandler", "crashreporter", "vcredist", "dxsetup", "dxwebsetup", "start", "play", "client",
            "server", "config", "settings", "saves", "save", "savegames", "savedgames", "profiles", "logs", "cache", "temp",
            "tmp", "local", "roaming", "locallow", "microsoft", "windows", "nvidia", "nvidiacorporation", "amd", "intel",
            "mygames", "documents", "desktop", "programfiles", "programfilesx86", "common", "commonfiles", "shared", "app",
            "application", "applications", "main", "default", "user", "users", "public", "sdk", "plugins", "mods", "mod", "patch",
            "update", "updater", "manual", "readme", "prerequisites", "redistributables", "packages", "programs", "crashdumps",
            "d3dscache", "screenshots", "captures", "videos", "pictures", "music", "downloads", "appdata", "system", "system32",
            "syswow64", "home", "mozilla", "google", "apple", "adobe", "oracle", "sun", "classes", "policies", "wow6432node",
            "software", "crash", "crashes", "log", "dumps", "shadercache", "pipelinecache", "dxcache", "glcache", "webcache",
            "htmlcache", "gpucache", "codecache", "win", "windowsnoeditor", "cookedpc", "cookedpcconsole", "movies", "sound",
            "sounds", "audio", "video", "localization", "paks", "pak", "startgame", "gamestart", "startup", "uploader",
            "downloader", "datacleaner", "reporter", "monitor", "service", "services", "agent", "overlay", "browser", "webhelper",
            "renderer", "worker", "daemon", "host", "console", "terminal", "resources", "assets", "lib", "libs", "include",
            "docs", "doc", "help", "license", "licenses", "locales", "locale", "lang", "languages", "images", "icons", "themes",
            "skins", "fonts", "extensions", "addons", "components", "modules", "scripts", "backup", "backups", "cookies",
            "history", "storage", "database", "db", "state", "session", "sessions", "userdata", "profile", "portable",
        });

        /// <summary>
        /// Launcher, runtime e programmi diffusi: generici quando si cerca un GIOCO (le loro cartelle non sono dati del gioco),
        /// ma validi come nome di un programma (le cartelle di Steam o di Discord sono i dati di quel programma).
        /// </summary>
        private static readonly HashSet<string> SharedApps = new HashSet<string>(new[]
        {
            "steam", "epic", "epicgames", "epicgameslauncher", "ubisoft", "uplay", "ea", "origin", "eadesktop",
            "electronicarts", "battlenet", "blizzard", "blizzardentertainment", "gog", "gogcom", "goggalaxy", "galaxy", "xbox",
            "unity", "unitytechnologies", "unrealengine", "unrealengine3", "unreal", "eac", "easyanticheat", "easyanticheateos",
            "battleye", "anticheat", "dotnet", "java", "python", "qt", "cef", "chromium", "electron", "node", "nvidiaapp",
            "javasoft", "valve", "rockstargames", "socialclub", "amazongames", "amazon", "discord", "overwolf", "obs",
            "obsstudio", "msi", "afterburner", "rivatuner", "wallpaperengine",
            // nomi di piattaforme con cui Windows ricorda molti programmi diversi
            "nodejs", "javaw", "pythonw", "javaplatformsebinary", "openjdkplatformbinary", "electronapp", "windowshostprocess",
            "microsoftedgewebview2", "webview2", "nwjs", "autohotkey", "powershell", "windowspowershell", "cmd",
        });

        /// <summary>True se il nome (già normalizzato) è troppo generico per un gioco.</summary>
        public static bool IsGeneric(string normalized)
        {
            return IsGeneric(normalized, AppKind.Game);
        }

        /// <summary>True se il nome (già normalizzato) è troppo generico per un gioco o per un programma.</summary>
        public static bool IsGeneric(string normalized, AppKind kind)
        {
            if (string.IsNullOrEmpty(normalized) || normalized.Length < 3 || Regex.IsMatch(normalized, @"^\d+$")) return true;
            return IsGenericWord(normalized, kind);
        }

        /// <summary>True se la parola (già normalizzata) è nell'elenco dei nomi generici, qualunque sia la lunghezza.</summary>
        public static bool IsGenericWord(string normalized, AppKind kind)
        {
            return Generic.Contains(normalized) || (kind == AppKind.Game && SharedApps.Contains(normalized));
        }

        /// <summary>Forma confrontabile: minuscolo, senza accenti, solo lettere e cifre, "&amp;" diventa "and".</summary>
        public static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("&", "and");
            string d = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (char c in d)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>Ripulisce un nome visualizzato (versioni, sigle dei repack, simboli ™ ®).</summary>
        public static string CleanDisplayName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            string s = name.Replace("™", "").Replace("®", "").Replace("©", "").Trim();
            foreach (var re in JunkPatterns) s = re.Replace(s, "");
            s = Regex.Replace(s, @"\s{2,}", " ").Trim(' ', '-', '_', ':');
            return s.Length == 0 ? name.Trim() : s;
        }

        /// <summary>
        /// Nome di un programma come lo mostra Windows, senza simboli ™ ® © e senza la versione in coda se è quella dichiarata.
        /// (Più prudente di CleanDisplayName, pensata per i repack: "Visual C++ v14 Redistributable" deve restare intero.)
        /// </summary>
        public static string CleanProgramName(string name, string version)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            string s = Regex.Replace(name.Replace("™", "").Replace("®", "").Replace("©", ""), @"\s{2,}", " ").Trim();
            return StripVersion(s.Length == 0 ? name.Trim() : s, version);
        }

        /// <summary>Nome di un programma senza la versione in coda, se coincide con quella dichiarata ("LocalSend versione 1.17.0" → "LocalSend").</summary>
        public static string StripVersion(string name, string version)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            string s = name.Trim();
            if (!string.IsNullOrWhiteSpace(version))
                s = Regex.Replace(s, @"[\s\-_,]*(\b(versione|version|ver\.?|v)\s*)?" + Regex.Escape(version.Trim()) + @"\s*$", "", RegexOptions.IgnoreCase).Trim();
            return s.Length >= 2 ? s : name.Trim();
        }

        private static readonly Regex ArchSuffix = new Regex(@"\s*[\(\[]?\b(x64|x86|amd64|arm64|64[\s\-]?bit|32[\s\-]?bit)\b[\)\]]?", RegexOptions.IgnoreCase);
        private static readonly Regex TrailingVersion = new Regex(@"\s+(release\s+|versione\s+|version\s+|ver\.?\s*|v)?\d+(\.\d+)*[a-z]?\b.*$", RegexOptions.IgnoreCase);

        /// <summary>Nome "di base" di un programma, senza versione e architettura ("Python 3.12.10 (64-bit)" → "Python").</summary>
        public static string BaseProgramName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            string s = ArchSuffix.Replace(name, "").Trim();
            string cut = TrailingVersion.Replace(s, "").Trim(' ', '-', '_', ',', ':');
            return cut.Length >= 2 ? cut : s;
        }

        /// <summary>Sigle di repack, crack e trainer: una cartella che le contiene è di un gioco.</summary>
        public static readonly Regex GameReleaseTags = new Regex(
            @"(repack|fitgirl|dodi|elamigos|\bkaos\b|xatab|kr0n0s|insaneramzes|\bfling\b|trainer|steamrip|online-?fix|\bmulti\d+\b|" +
            @"[-_.](rune|codex|empress|tenoke|skidrow|plaza|hoodlum|darksiders|tinyiso|anomaly|p2p|flt|razor1911|reloaded|cpy|gog)\b|" +
            @"full[-_ ]?game|game[-_ ]?data)", RegexOptions.IgnoreCase);

        // parole di contorno nei nomi delle cartelle scaricate; i numeri semplici restano (sono i seguiti: "The Sims 4")
        private static readonly Regex TailNoise = new Regex(
            @"^(download|downloads|archive|win|win32|win64|windows|x64|x86|x86_64|amd64|\d+-?bits?|setup|installer|portable|full|repack|multi\d*|eng|en|ita|rar|zip|7z|exe|" +
            @"v\d+([.\-]\d+)*[a-z]?|\d+([.\-]\d+)+[a-z]?|\d{5,})$", RegexOptions.IgnoreCase);
        private static readonly Regex HeadNoise = new Regex(@"^(download|downloads|setup|installer)$", RegexOptions.IgnoreCase);
        private static readonly Regex TagSuffix = new Regex(
            @"[-_ ]+(rune|codex|empress|tenoke|skidrow|plaza|hoodlum|darksiders|tinyiso|anomaly|p2p|flt|elamigos|fitgirl|dodi|kr0n0s|insaneramzes|fling|" +
            @"x86_64|x64|x86|amd64|win|win64|win32|windows|portable|v?\d+(\.\d+)+[a-z]?)$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Titolo leggibile ricavato dal nome di una cartella scaricata o estratta
        /// ("AC.Black.Flag.Resynced-Kr0n0s" → "AC Black Flag Resynced", "Crimson_Desert_Update_from_v1.00_to_v1.01-ElAmigos.rar" → "Crimson Desert",
        /// "Forza.Horizon.6-InsaneRamZes" → "Forza Horizon 6").
        /// </summary>
        public static string CleanFolderTitle(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return folder;
            string s = Regex.Replace(folder, @"\.(rar|zip|7z)$", "", RegexOptions.IgnoreCase);
            s = CleanDisplayName(s);
            string prev, untagged = s;
            do { prev = s; s = TagSuffix.Replace(s, "").TrimEnd(' ', '-', '_'); } while (s != prev && s.Length > 0);
            bool hadTag = s != untagged; // "Elden.Ring-CODEX" è un nome di release; "Node.js" no
            // nomi "tecnici" con trattini bassi, punti o trattini al posto degli spazi (i punti tra due cifre sono versioni e restano)
            if (s.IndexOf(' ') < 0)
            {
                s = s.Replace('_', ' ');
                if (hadTag || s.Count(c => c == '.') >= 2 || s.IndexOf(' ') > 0) s = Regex.Replace(s, @"(?<!\d)\.|\.(?!\d)", " ");
                if (s.IndexOf(' ') < 0 && s.Count(c => c == '-') >= 2) s = s.Replace('-', ' ');
            }
            // "Gioco Update from v1 to v2", "Gioco Update", "gioco full game": conta solo il titolo
            s = Regex.Replace(s, @"\s+update(\s+(from|v?\d).*)?$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+full[\s\-_]*game$", "", RegexOptions.IgnoreCase);
            do { prev = s; s = TagSuffix.Replace(s, "").TrimEnd(' ', '-', '_'); } while (s != prev && s.Length > 0);
            var words = Regex.Split(s, @"\s+").Where(w => w.Length > 0).ToList();
            while (words.Count > 1 && TailNoise.IsMatch(words[words.Count - 1])) words.RemoveAt(words.Count - 1);
            while (words.Count > 1 && HeadNoise.IsMatch(words[0])) words.RemoveAt(0);
            string result = string.Join(" ", words).Trim(' ', '-', '_');
            return result.Length >= 2 ? result : folder;
        }

        /// <summary>Nome senza suffissi di edizione ("Definitive Edition", "GOTY"...).</summary>
        public static string StripEdition(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            string s = EditionPattern.Replace(name, "").Trim(' ', '-', ':', '–');
            return s.Length >= 3 ? s : name;
        }

        public static string Initials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var words = Regex.Split(name, @"[\s\-_:]+").Where(w => w.Length > 0 && char.IsLetterOrDigit(w[0])).ToList();
            if (words.Count == 0) return name.Substring(0, 1).ToUpperInvariant();
            if (words.Count == 1) return words[0].Substring(0, Math.Min(2, words[0].Length)).ToUpperInvariant();
            return (words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpperInvariant();
        }
    }

    // ====================================================================== Formattazione

    internal static class Fmt
    {
        /// <summary>Numeri e date nella lingua dell'interfaccia.</summary>
        public static CultureInfo It { get { return L.Culture; } }

        public static string Size(long bytes)
        {
            if (bytes < 0) return "—";
            if (bytes < 1024) return bytes + " " + L.T("B");
            string[] units = { "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = -1;
            do { v /= 1024; u++; } while (v >= 1024 && u < units.Length - 1);
            return v.ToString(v >= 100 ? "0" : v >= 10 ? "0.#" : "0.##", It) + " " + L.T(units[u]);
        }

        public static string Date(DateTime? d)
        {
            return d.HasValue ? d.Value.ToString("d MMM yyyy", It) : "";
        }

        public static string Count(int n, string singular, string plural)
        {
            return n + " " + L.Plural(n, singular, plural);
        }
    }

    // ====================================================================== Registro

    internal static class RegUtil
    {
        /// <summary>Apre una chiave a partire da un percorso completo (HKEY_CURRENT_USER\... o HKCU\...), vista a 64 bit.</summary>
        public static RegistryKey Open(string fullPath, bool writable = false)
        {
            string sub;
            RegistryHive hive;
            if (!Split(fullPath, out hive, out sub)) return null;
            try
            {
                var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                if (string.IsNullOrEmpty(sub)) return root;
                return root.OpenSubKey(sub, writable);
            }
            catch { return null; }
        }

        public static bool Split(string fullPath, out RegistryHive hive, out string sub)
        {
            hive = RegistryHive.CurrentUser;
            sub = null;
            if (string.IsNullOrEmpty(fullPath)) return false;
            int slash = fullPath.IndexOf('\\');
            string h = slash < 0 ? fullPath : fullPath.Substring(0, slash);
            sub = slash < 0 ? "" : fullPath.Substring(slash + 1);
            switch (h.ToUpperInvariant())
            {
                case "HKEY_CURRENT_USER": case "HKCU": hive = RegistryHive.CurrentUser; return true;
                case "HKEY_LOCAL_MACHINE": case "HKLM": hive = RegistryHive.LocalMachine; return true;
                case "HKEY_USERS": case "HKU": hive = RegistryHive.Users; return true;
                case "HKEY_CLASSES_ROOT": case "HKCR": hive = RegistryHive.ClassesRoot; return true;
            }
            return false;
        }

        /// <summary>Forma lunga, usata nei file .reg.</summary>
        public static string LongName(string fullPath)
        {
            RegistryHive hive;
            string sub;
            if (!Split(fullPath, out hive, out sub)) return fullPath;
            string h = hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER"
                : hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE"
                : hive == RegistryHive.Users ? "HKEY_USERS" : "HKEY_CLASSES_ROOT";
            return string.IsNullOrEmpty(sub) ? h : h + "\\" + sub;
        }

        /// <summary>Forma corta, per la visualizzazione.</summary>
        public static string ShortName(string fullPath)
        {
            RegistryHive hive;
            string sub;
            if (!Split(fullPath, out hive, out sub)) return fullPath;
            string h = hive == RegistryHive.CurrentUser ? "HKCU" : hive == RegistryHive.LocalMachine ? "HKLM"
                : hive == RegistryHive.Users ? "HKU" : "HKCR";
            return string.IsNullOrEmpty(sub) ? h : h + "\\" + sub;
        }

        public static bool KeyExists(string fullPath)
        {
            using (var k = Open(fullPath)) return k != null;
        }

        public static bool ValueExists(string keyPath, string valueName)
        {
            using (var k = Open(keyPath))
            {
                if (k == null) return false;
                try { return k.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase); }
                catch { return false; }
            }
        }

        public static string GetString(RegistryKey key, string name)
        {
            try
            {
                var v = key.GetValue(name);
                return v == null ? null : v.ToString();
            }
            catch { return null; }
        }

        public static string[] SubKeys(RegistryKey key)
        {
            try { return key == null ? new string[0] : key.GetSubKeyNames(); }
            catch { return new string[0]; }
        }

        /// <summary>True se la chiave appartiene alla vista a 32 bit del registro (programmi a 32 bit su Windows a 64 bit).</summary>
        public static bool IsWow32(string fullPath)
        {
            return fullPath != null && fullPath.IndexOf(@"\WOW6432Node\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Legge un percorso da un valore del registro. Nelle chiavi a 32 bit le variabili come %ProgramFiles%
        /// valgono "Program Files (x86)", come le vede il programma che le ha scritte.
        /// </summary>
        public static string GetPath(RegistryKey key, string name, bool wow32)
        {
            try
            {
                var v = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                return v == null ? null : PathUtil.Expand(v, wow32);
            }
            catch { return null; }
        }

        /// <summary>
        /// Apre una chiave di HKLM\SOFTWARE cercandola anche nella vista a 32 bit (WOW6432Node): i programmi a 32 bit
        /// scrivono lì anche quando il riferimento (es. RegisteredApplications) indica solo "SOFTWARE\...".
        /// </summary>
        public static RegistryKey OpenAnyView(string fullPath)
        {
            var k = Open(fullPath);
            if (k != null) return k;
            string s = ShortName(fullPath);
            if (s != null && s.StartsWith(@"HKLM\SOFTWARE\", StringComparison.OrdinalIgnoreCase) && !IsWow32(s))
                return Open(@"HKLM\SOFTWARE\WOW6432Node\" + s.Substring(@"HKLM\SOFTWARE\".Length));
            return null;
        }

        public static string[] ValueNames(RegistryKey key)
        {
            try { return key == null ? new string[0] : key.GetValueNames(); }
            catch { return new string[0]; }
        }
    }

    // ====================================================================== JSON

    internal static class Json
    {
        private static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
        }

        public static object Parse(string text)
        {
            return Serializer().DeserializeObject(text);
        }

        public static T Deserialize<T>(string text)
        {
            return Serializer().Deserialize<T>(text);
        }

        public static string Serialize(object o)
        {
            return Serializer().Serialize(o);
        }

        /// <summary>Sotto-oggetto JSON (null se assente).</summary>
        public static object Child(object obj, string key)
        {
            var d = obj as IDictionary<string, object>;
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        /// <summary>Legge un campo stringa da un oggetto JSON generico.</summary>
        public static string Str(object obj, string key)
        {
            var d = obj as IDictionary<string, object>;
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }
    }
}
