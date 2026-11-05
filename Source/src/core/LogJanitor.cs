using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AppManager
{
    /// <summary>
    /// Trova i file di log e di diagnostica lasciati sul PC da Windows, dai launcher e dai giochi: segnalazioni di errore,
    /// dump di arresto anomalo, log dei launcher, log e crash di Unity e Unreal, log temporanei.
    /// Mai i registri eventi di Windows (.evtx) né i log che servono al sistema; mai file in uso (vengono saltati).
    /// Ogni elemento passa comunque dai controlli di <see cref="Safety"/>, anche al momento dell'eliminazione.
    /// </summary>
    internal static class LogJanitor
    {
        private static readonly string[] LogExtensions = { ".log", ".txt", ".previous", ".old", ".bak", ".etl", ".dmp", ".mdmp", ".json", ".xml", ".gz", ".zip" };

        private sealed class Scan
        {
            public readonly List<LeftoverItem> Items = new List<LeftoverItem>();
            public readonly HashSet<string> Seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public DateTime Cutoff;
            public int Locked;
            public CancellationToken Ct;
        }

        /// <param name="minAgeDays">solo file più vecchi di tanti giorni (0 = tutti)</param>
        /// <param name="locked">file saltati perché in uso</param>
        public static List<LeftoverItem> Find(int minAgeDays, Action<string> progress, CancellationToken ct, out int locked)
        {
            var s = new Scan { Cutoff = minAgeDays > 0 ? DateTime.Now.AddDays(-minAgeDays) : DateTime.MaxValue, Ct = ct };
            Action<string> report = t => { if (progress != null) progress(t); };
            string local = KnownDirs.LocalAppData, programData = KnownDirs.ProgramData, windows = KnownDirs.Windows;

            report(L.T("Segnalazioni di errore di Windows…"));
            foreach (var sub in new[] { "ReportArchive", "ReportQueue" })
            {
                AddChildDirs(s, Path.Combine(local, @"Microsoft\Windows\WER", sub), "log:wer", L.T("Segnalazioni di errore di Windows"), "Windows",
                    L.T("Rapporto di errore già inviato o in coda: serve solo a Microsoft per la diagnosi"), false);
                AddChildDirs(s, Path.Combine(programData, @"Microsoft\Windows\WER", sub), "log:wer", L.T("Segnalazioni di errore di Windows"), "Windows",
                    L.T("Rapporto di errore di sistema: serve solo a Microsoft per la diagnosi"), true);
            }

            report(L.T("Dump di arresto anomalo…"));
            AddFiles(s, KnownDirs.CrashDumps, new[] { ".dmp", ".mdmp" }, "log:dump", L.T("Dump di arresto anomalo"), "Windows",
                L.T("Copia della memoria di un programma che si è chiuso per errore"), false, false);
            AddFiles(s, Path.Combine(windows, "Minidump"), new[] { ".dmp" }, "log:dump", L.T("Dump di arresto anomalo"), "Windows",
                L.T("Minidump di una schermata blu: serve solo per diagnosticarla"), true, false);
            AddFile(s, Path.Combine(windows, "MEMORY.DMP"), "log:dump", L.T("Dump di arresto anomalo"), "Windows",
                L.T("Dump completo della memoria dopo una schermata blu (può occupare diversi GB)"), true);
            AddFiles(s, Path.Combine(windows, "LiveKernelReports"), new[] { ".dmp" }, "log:dump", L.T("Dump di arresto anomalo"), "Windows",
                L.T("Rapporto del kernel su un blocco del driver video o di un dispositivo"), true, true);

            report(L.T("Log dei launcher…"));
            string steam = SteamInfo.SteamPath;
            if (steam != null)
            {
                AddFiles(s, Path.Combine(steam, "logs"), LogExtensions, "log:steam", L.T("Log di Steam"), L.T("Launcher"), L.T("Registro di Steam: Steam ne crea di nuovi da solo"), false, false);
                AddFiles(s, Path.Combine(steam, "dumps"), new[] { ".dmp", ".mdmp" }, "log:steam", L.T("Log di Steam"), L.T("Launcher"), L.T("Dump di un arresto anomalo di Steam"), false, false);
            }
            AddFiles(s, Path.Combine(local, @"EpicGamesLauncher\Saved\Logs"), LogExtensions, "log:epic", L.T("Log di Epic Games"), L.T("Launcher"), L.T("Registro dell'Epic Games Launcher"), false, false);
            AddChildDirs(s, Path.Combine(local, @"EpicGamesLauncher\Saved\Crashes"), "log:epic", L.T("Log di Epic Games"), L.T("Launcher"), L.T("Rapporto di un arresto anomalo dell'Epic Games Launcher"), false);
            AddFiles(s, Path.Combine(local, @"Electronic Arts\EA Desktop\Logs"), LogExtensions, "log:ea", L.T("Log della EA app"), L.T("Launcher"), L.T("Registro della EA app"), false, false);
            AddFiles(s, Path.Combine(local, @"Ubisoft Game Launcher\logs"), LogExtensions, "log:ubi", L.T("Log di Ubisoft Connect"), L.T("Launcher"), L.T("Registro di Ubisoft Connect"), false, false);
            AddFiles(s, Path.Combine(programData, @"GOG.com\Galaxy\logs"), LogExtensions, "log:gog", L.T("Log di GOG Galaxy"), L.T("Launcher"), L.T("Registro di GOG Galaxy"), false, false);
            AddFiles(s, Path.Combine(local, @"Battle.net\Logs"), LogExtensions, "log:bnet", L.T("Log di Battle.net"), L.T("Launcher"), L.T("Registro di Battle.net"), false, false);

            report(L.T("Log dei giochi Unity…"));
            foreach (var company in PathUtil.Dirs(KnownDirs.LocalLow))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var gameDir in PathUtil.Dirs(company))
                {
                    foreach (var name in new[] { "Player.log", "Player-prev.log", "output_log.txt" })
                        AddFile(s, Path.Combine(gameDir, name), "log:unity", L.T("Log dei giochi Unity"), L.T("Giochi"),
                            L.F("Registro della sessione di gioco di {0}: il gioco lo riscrive a ogni avvio", Path.GetFileName(gameDir)), false);
                    AddChildDirs(s, Path.Combine(gameDir, "Crashes"), "log:unity", L.T("Log dei giochi Unity"), L.T("Giochi"),
                        L.F("Rapporto di un arresto anomalo di {0}", Path.GetFileName(gameDir)), false);
                }
            }

            report(L.T("Log dei giochi Unreal Engine…"));
            foreach (var gameDir in PathUtil.Dirs(local))
            {
                ct.ThrowIfCancellationRequested();
                string saved = Path.Combine(gameDir, "Saved");
                if (!Directory.Exists(saved)) continue;
                string game = Path.GetFileName(gameDir);
                // solo Logs e Crashes: SaveGames e Config sono salvataggi e impostazioni, non si toccano
                AddFiles(s, Path.Combine(saved, "Logs"), LogExtensions, "log:unreal", L.T("Log dei giochi Unreal Engine"), L.T("Giochi"),
                    L.F("Registro di {0}: il gioco ne crea di nuovi a ogni avvio", game), false, false);
                AddChildDirs(s, Path.Combine(saved, "Crashes"), "log:unreal", L.T("Log dei giochi Unreal Engine"), L.T("Giochi"),
                    L.F("Rapporto di un arresto anomalo di {0}", game), false);
            }

            report(L.T("Log temporanei…"));
            AddFiles(s, KnownDirs.Temp, new[] { ".log", ".dmp", ".etl" }, "log:temp", L.T("Log temporanei"), "Windows",
                L.T("Log lasciato nella cartella dei file temporanei da un programma o da un'installazione"), false, false);

            report(L.T("File temporanei…"));
            // anche togliendo «solo file più vecchi di 7 giorni», un file temporaneo di oggi può essere di un'installazione in corso
            var cutoff = s.Cutoff;
            if (s.Cutoff > DateTime.Now.AddDays(-2)) s.Cutoff = DateTime.Now.AddDays(-2);
            AddChildDirs(s, KnownDirs.Temp, "log:tempfiles", L.T("File temporanei"), "Windows", L.T("Cartella temporanea che non viene usata da giorni"), false);
            AddFiles(s, KnownDirs.Temp, null, "log:tempfiles", L.T("File temporanei"), "Windows", L.T("File temporaneo che non viene usato da giorni"), false, false);
            s.Cutoff = cutoff;

            report(L.T("Log di Windows…"));
            foreach (var dir in Safety.SystemLogDirs())
                AddFiles(s, dir, Safety.SystemLogExtensions, "log:windows", L.T("Log di Windows"), "Windows",
                    L.T("Registro di aggiornamenti, manutenzione o installazione di Windows: serve solo per la diagnosi"), true, false);
            // file temporanei di Windows, download di Windows Update e di Ottimizzazione recapito: mai più recenti di 2 giorni
            var keep = s.Cutoff;
            if (s.Cutoff > DateTime.Now.AddDays(-2)) s.Cutoff = DateTime.Now.AddDays(-2);
            var roots = Safety.SystemCacheRoots();
            AddChildDirs(s, roots[0], "log:tempfiles", L.T("File temporanei"), "Windows", L.T("Cartella temporanea che non viene usata da giorni"), true);
            AddFiles(s, roots[0], null, "log:tempfiles", L.T("File temporanei"), "Windows", L.T("File temporaneo che non viene usato da giorni"), true, false);
            report(L.T("Cache di Windows Update…"));
            AddChildDirs(s, roots[1], "log:wucache", L.T("Download di Windows Update"), "Windows", L.T("Aggiornamento già scaricato: se serve ancora, Windows lo scarica di nuovo"), true);
            AddFiles(s, roots[1], null, "log:wucache", L.T("Download di Windows Update"), "Windows", L.T("Aggiornamento già scaricato: se serve ancora, Windows lo scarica di nuovo"), true, false);
            AddChildDirs(s, roots[2], "log:docache", L.T("Cache di Ottimizzazione recapito"), "Windows", L.T("Copia di un aggiornamento da condividere con altri PC: Windows la riscarica se serve"), true);
            AddFiles(s, roots[2], null, "log:docache", L.T("Cache di Ottimizzazione recapito"), "Windows", L.T("Copia di un aggiornamento da condividere con altri PC: Windows la riscarica se serve"), true, false);

            report(L.T("Log e pacchetti dei driver…"));
            // driver installati: i pacchetti estratti servivano solo per installarli
            foreach (var dir in new[] { @"C:\NVIDIA", @"C:\AMD", Path.Combine(programData, @"NVIDIA Corporation\Downloader") })
                AddChildDirs(s, dir, "log:driverpkg", L.T("Pacchetti di installazione dei driver"), L.T("Driver video"), L.T("Driver già installato: questi file servivano solo per installarlo"), true);
            s.Cutoff = keep;
            // log dei driver e dei loro pannelli (mai NGX, Streamline o i dati delle app: lì ci sono componenti che servono)
            foreach (var dir in new[] { Path.Combine(programData, "NVIDIA Corporation"), Path.Combine(local, "NVIDIA Corporation"), Path.Combine(programData, "AMD"), Path.Combine(local, @"AMD\CN") })
                AddFiles(s, dir, new[] { ".log" }, "log:driver", L.T("Log dei driver video"), L.T("Driver video"), L.T("Registro del driver video o del suo pannello di controllo"), true, true);

            report(L.T("Cache dei browser…"));
            foreach (var dir in BrowserCacheDirs())
                AddCacheDir(s, dir, "log:browser", L.T("Cache dei browser"), L.T("Browser"), L.T("Si ricrea da sola: le pagine potrebbero caricarsi più lentamente la prima volta. Password, cronologia e dati dei siti non vengono toccati"), Confidence.Medium);

            report(L.T("Cache degli shader…"));
            foreach (var dir in ShaderCacheDirs())
                AddCacheDir(s, dir, "log:shader", L.T("Cache degli shader dei driver grafici"), L.T("Driver video"), L.T("Si rigenera da sola: i giochi potrebbero caricare più lentamente al primo avvio"), Confidence.Low);

            locked = s.Locked;
            return s.Items;
        }

        /// <summary>Cache degli shader dei driver video: si rigenerano da sole.</summary>
        public static string[] ShaderCacheDirs()
        {
            return new[]
            {
                Path.Combine(KnownDirs.LocalAppData, @"NVIDIA\DXCache"), Path.Combine(KnownDirs.LocalAppData, @"NVIDIA\GLCache"),
                Path.Combine(KnownDirs.LocalLow, @"NVIDIA\PerDriverVersion\DXCache"), Path.Combine(KnownDirs.LocalLow, @"NVIDIA\PerDriverVersion\GLCache"),
                Path.Combine(KnownDirs.LocalLow, @"NVIDIA\DXCache"), Path.Combine(KnownDirs.LocalAppData, @"AMD\DxCache"),
                Path.Combine(KnownDirs.LocalAppData, @"AMD\DxcCache"), Path.Combine(KnownDirs.LocalAppData, @"AMD\VkCache"),
                Path.Combine(KnownDirs.LocalAppData, @"AMD\GLCache"), Path.Combine(KnownDirs.LocalAppData, "D3DSCache"),
                Path.Combine(KnownDirs.LocalLow, @"Intel\ShaderCache"), Path.Combine(KnownDirs.LocalAppData, @"Intel\ShaderCache"),
            };
        }

        /// <summary>Cache dei browser più diffusi, per ogni profilo: solo Cache, Code Cache e GPUCache (Firefox: cache2).</summary>
        public static List<string> BrowserCacheDirs()
        {
            var list = new List<string>();
            string local = KnownDirs.LocalAppData;
            foreach (var userData in new[]
            {
                Path.Combine(local, @"Microsoft\Edge\User Data"), Path.Combine(local, @"Google\Chrome\User Data"),
                Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"), Path.Combine(local, @"Vivaldi\User Data"),
            })
            {
                foreach (var profile in PathUtil.Dirs(userData))
                {
                    string n = Path.GetFileName(profile);
                    if (!n.Equals("Default", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var c in Safety.BrowserCacheNames) list.Add(Path.Combine(profile, c));
                }
            }
            foreach (var opera in new[] { Path.Combine(local, @"Opera Software\Opera Stable"), Path.Combine(local, @"Opera Software\Opera GX Stable") })
                foreach (var c in Safety.BrowserCacheNames) list.Add(Path.Combine(opera, c));
            foreach (var profile in PathUtil.Dirs(Path.Combine(local, @"Mozilla\Firefox\Profiles")))
                list.Add(Path.Combine(profile, "cache2"));
            return list;
        }

        /// <summary>Nome visualizzato di un gruppo (per le esclusioni dagli avvisi).</summary>
        public static string GroupName(string key)
        {
            switch (key)
            {
                case "log:wer": return L.T("Segnalazioni di errore di Windows");
                case "log:dump": return L.T("Dump di arresto anomalo");
                case "log:steam": return L.T("Log di Steam");
                case "log:epic": return L.T("Log di Epic Games");
                case "log:ea": return L.T("Log della EA app");
                case "log:ubi": return L.T("Log di Ubisoft Connect");
                case "log:gog": return L.T("Log di GOG Galaxy");
                case "log:bnet": return L.T("Log di Battle.net");
                case "log:unity": return L.T("Log dei giochi Unity");
                case "log:unreal": return L.T("Log dei giochi Unreal Engine");
                case "log:temp": return L.T("Log temporanei");
                case "log:tempfiles": return L.T("File temporanei");
                case "log:shader": return L.T("Cache degli shader dei driver grafici");
                case "log:windows": return L.T("Log di Windows");
                case "log:wucache": return L.T("Download di Windows Update");
                case "log:docache": return L.T("Cache di Ottimizzazione recapito");
                case "log:driver": return L.T("Log dei driver video");
                case "log:driverpkg": return L.T("Pacchetti di installazione dei driver");
                case "log:browser": return L.T("Cache dei browser");
                default: return key;
            }
        }

        /// <summary>
        /// Una cache: si svuota anche se usata da poco (si rigenera da sola; i file aperti vengono saltati durante la pulizia).
        /// Quelle degli shader restano «Da verificare», perché dopo i giochi impiegano di più al primo avvio.
        /// </summary>
        private static void AddCacheDir(Scan s, string dir, string key, string group, string source, string reason, Confidence confidence)
        {
            try
            {
                if (!Directory.Exists(PathUtil.Long(dir)) || PathUtil.IsReparsePoint(dir) || s.Seen.Contains(dir) || !Safety.IsDeletablePath(dir)) return;
                int count;
                DateTime newest;
                long size = PathUtil.Size(dir, out count, out newest, s.Ct);
                if (size <= 0) return;
                s.Seen.Add(dir);
                s.Items.Add(new LeftoverItem
                {
                    Kind = ItemKind.Directory, Category = ItemCategory.Cache, Path = dir, Size = size, FileCount = count, LastWrite = newest == DateTime.MinValue ? (DateTime?)null : newest,
                    Reason = reason, Confidence = confidence,
                    GroupKey = key, GroupName = group, GroupSource = source, GroupKind = AppKind.Program,
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn("Cache non controllata {0}: {1}", dir, ex.Message); }
        }

        private static bool OldEnough(Scan s, DateTime lastWrite)
        {
            return s.Cutoff == DateTime.MaxValue || lastWrite < s.Cutoff;
        }

        private static void AddFiles(Scan s, string dir, string[] extensions, string key, string group, string source, string reason, bool admin, bool recursive)
        {
            if (!Directory.Exists(PathUtil.Long(dir)) || PathUtil.IsReparsePoint(dir)) return;
            var files = recursive ? PathUtil.FilesNoLinks(dir) : PathUtil.Files(dir);
            foreach (var f in files)
            {
                s.Ct.ThrowIfCancellationRequested();
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (extensions != null && !extensions.Contains(ext)) continue;
                AddFile(s, f, key, group, source, reason, admin);
            }
        }

        private static void AddFile(Scan s, string file, string key, string group, string source, string reason, bool admin)
        {
            try
            {
                var fi = new FileInfo(PathUtil.Long(file));
                if (!fi.Exists) return;
                // i registri eventi di Windows e simili non si toccano mai
                if (fi.Extension.Equals(".evtx", StringComparison.OrdinalIgnoreCase)) return;
                if (!OldEnough(s, fi.LastWriteTime)) return;
                if (s.Seen.Contains(file) || !Safety.IsDeletablePath(file)) return;
                if (IsLocked(fi.FullName)) { s.Locked++; return; }
                s.Seen.Add(file);
                s.Items.Add(new LeftoverItem
                {
                    Kind = ItemKind.File, Category = ItemCategory.Cache, Path = file, Size = fi.Length, FileCount = 1, LastWrite = fi.LastWriteTime,
                    Reason = reason, Confidence = Confidence.High, NeedsAdmin = admin,
                    GroupKey = key, GroupName = group, GroupSource = source, GroupKind = AppKind.Program,
                });
            }
            catch (Exception ex) { Log.Warn("Log non controllato {0}: {1}", file, ex.Message); }
        }

        /// <summary>Sottocartelle (un rapporto di errore o di crash per cartella).</summary>
        private static void AddChildDirs(Scan s, string dir, string key, string group, string source, string reason, bool admin)
        {
            if (!Directory.Exists(PathUtil.Long(dir)) || PathUtil.IsReparsePoint(dir)) return;
            foreach (var d in PathUtil.Dirs(dir))
            {
                s.Ct.ThrowIfCancellationRequested();
                try
                {
                    if (s.Seen.Contains(d) || PathUtil.IsReparsePoint(d) || !Safety.IsDeletablePath(d)) continue;
                    int count;
                    DateTime newest;
                    long size = PathUtil.Size(d, out count, out newest, s.Ct);
                    if (newest == DateTime.MinValue) newest = Directory.GetLastWriteTime(PathUtil.Long(d));
                    if (!OldEnough(s, newest)) continue;
                    s.Seen.Add(d);
                    s.Items.Add(new LeftoverItem
                    {
                        // non "Cache": così anche i rapporti di errore e di crash seguono la casella «Usa il Cestino» della pagina Log
                        Kind = ItemKind.Directory, Category = ItemCategory.Other, Path = d, Size = size, FileCount = count, LastWrite = newest,
                        Reason = reason, Confidence = Confidence.High, NeedsAdmin = admin,
                        GroupKey = key, GroupName = group, GroupSource = source, GroupKind = AppKind.Program,
                    });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Warn("Cartella di log non controllata {0}: {1}", d, ex.Message); }
            }
        }

        /// <summary>True se il file è aperto da un altro programma in scrittura (es. il log di Steam mentre Steam è aperto).</summary>
        private static bool IsLocked(string file)
        {
            try
            {
                using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                return false;
            }
            catch (UnauthorizedAccessException) { return false; }   // serve l'amministratore: lo dirà la pulizia
            catch (IOException) { return true; }
        }
    }
}
