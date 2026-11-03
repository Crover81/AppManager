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
                AddFiles(s, Path.Combine(steam, "logs"), LogExtensions, "log:steam", L.T("Log di Steam"), "Launcher", L.T("Registro di Steam: Steam ne crea di nuovi da solo"), false, false);
                AddFiles(s, Path.Combine(steam, "dumps"), new[] { ".dmp", ".mdmp" }, "log:steam", L.T("Log di Steam"), "Launcher", L.T("Dump di un arresto anomalo di Steam"), false, false);
            }
            AddFiles(s, Path.Combine(local, @"EpicGamesLauncher\Saved\Logs"), LogExtensions, "log:epic", L.T("Log di Epic Games"), "Launcher", L.T("Registro dell'Epic Games Launcher"), false, false);
            AddChildDirs(s, Path.Combine(local, @"EpicGamesLauncher\Saved\Crashes"), "log:epic", L.T("Log di Epic Games"), "Launcher", L.T("Rapporto di un arresto anomalo dell'Epic Games Launcher"), false);
            AddFiles(s, Path.Combine(local, @"Electronic Arts\EA Desktop\Logs"), LogExtensions, "log:ea", L.T("Log della EA app"), "Launcher", L.T("Registro della EA app"), false, false);
            AddFiles(s, Path.Combine(local, @"Ubisoft Game Launcher\logs"), LogExtensions, "log:ubi", L.T("Log di Ubisoft Connect"), "Launcher", L.T("Registro di Ubisoft Connect"), false, false);
            AddFiles(s, Path.Combine(programData, @"GOG.com\Galaxy\logs"), LogExtensions, "log:gog", L.T("Log di GOG Galaxy"), "Launcher", L.T("Registro di GOG Galaxy"), false, false);
            AddFiles(s, Path.Combine(local, @"Battle.net\Logs"), LogExtensions, "log:bnet", L.T("Log di Battle.net"), "Launcher", L.T("Registro di Battle.net"), false, false);

            report(L.T("Log dei giochi Unity…"));
            foreach (var company in PathUtil.Dirs(KnownDirs.LocalLow))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var gameDir in PathUtil.Dirs(company))
                {
                    foreach (var name in new[] { "Player.log", "Player-prev.log", "output_log.txt" })
                        AddFile(s, Path.Combine(gameDir, name), "log:unity", L.T("Log dei giochi Unity"), "Giochi",
                            L.F("Registro della sessione di gioco di {0}: il gioco lo riscrive a ogni avvio", Path.GetFileName(gameDir)), false);
                    AddChildDirs(s, Path.Combine(gameDir, "Crashes"), "log:unity", L.T("Log dei giochi Unity"), "Giochi",
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
                AddFiles(s, Path.Combine(saved, "Logs"), LogExtensions, "log:unreal", L.T("Log dei giochi Unreal Engine"), "Giochi",
                    L.F("Registro di {0}: il gioco ne crea di nuovi a ogni avvio", game), false, false);
                AddChildDirs(s, Path.Combine(saved, "Crashes"), "log:unreal", L.T("Log dei giochi Unreal Engine"), "Giochi",
                    L.F("Rapporto di un arresto anomalo di {0}", game), false);
            }

            report(L.T("Log temporanei…"));
            AddFiles(s, KnownDirs.Temp, new[] { ".log", ".dmp", ".etl" }, "log:temp", L.T("Log temporanei"), "Windows",
                L.T("Log lasciato nella cartella dei file temporanei da un programma o da un'installazione"), false, false);

            locked = s.Locked;
            return s.Items;
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
                if (!extensions.Contains(ext)) continue;
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
