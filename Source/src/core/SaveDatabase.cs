using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AppManager
{
    /// <summary>Una cartella di dati di un gioco indicata dal database online.</summary>
    internal sealed class OnlineSaveFolder
    {
        public string Path;
        public bool IsConfig;
        public bool NameMatches;    // il nome della cartella richiama il gioco, o è abbastanza specifica da non essere condivisa
    }

    /// <summary>
    /// Salvataggi dentro la cartella del gioco: è dove li tengono molte copie portable e repack (…\Saved\SaveGames, …\Saves, *.sav).
    /// Eliminando la cartella si perdono, perciò è importante saperlo.
    /// </summary>
    internal static class LocalSaves
    {
        private static readonly Regex SaveDir = new Regex(@"^(saves?|save[ _-]?games?|saved[ _-]?games?|save[ _-]?data)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SkipDir = new Regex(@"^(engine|binaries|_commonredist|_redist|redist|directx|localization|movies|paks|content)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public static readonly HashSet<string> SaveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".sav", ".save", ".savegame", ".sl2", ".sgd", ".dsv", ".srm" };

        /// <summary>Le cartelle e i file di salvataggio; una cartella con eseguibili o librerie non è una cartella di salvataggi.</summary>
        public static List<string> Find(string gameDir)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir)) return found;
            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(gameDir, 0));
            int visited = 0;
            while (queue.Count > 0 && visited++ < 1500 && found.Count < 40)
            {
                var cur = queue.Dequeue();
                if (cur.Value <= 1)
                    foreach (var f in PathUtil.Files(cur.Key))
                        if (SaveExtensions.Contains(Path.GetExtension(f)) && found.Count < 40) found.Add(f);
                if (cur.Value >= 5) continue;
                foreach (var d in PathUtil.Dirs(cur.Key))
                {
                    string name = Path.GetFileName(d);
                    if (PathUtil.IsReparsePoint(d) || SkipDir.IsMatch(name)) continue;
                    if (!SaveDir.IsMatch(name)) { queue.Enqueue(new KeyValuePair<string, int>(d, cur.Value + 1)); continue; }
                    var files = PathUtil.FilesNoLinks(d).Take(500).ToList();
                    if (files.Count > 0 && !files.Any(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) found.Add(d);
                }
            }
            return found.Where(f => !found.Any(o => o != f && PathUtil.IsUnder(f, o))).ToList();
        }
    }

    /// <summary>
    /// Dove un gioco tiene salvataggi e impostazioni, secondo PCGamingWiki. Serve per i casi che dal nome non si possono capire
    /// (Assetto Corsa EVO → Giochi salvati\ACE). Viene inviato solo il titolo del gioco; le risposte restano in cache.
    /// Le cartelle indicate valgono solo se esistono e stanno nelle cartelle dati dell'utente.
    /// </summary>
    internal static class SaveDatabase
    {
        private const int KeepDays = 30, RetryMissDays = 7;
        private static readonly object Sync = new object();
        private static Dictionary<string, object> cache;

        /// <summary>Le prove non devono dipendere dalla rete.</summary>
        internal static bool TestsDisabled;

        private static string CacheFile { get { return Path.Combine(AppPaths.DataDir, "savedb.json"); } }

        private static bool Enabled { get { return !TestsDisabled && AppSettings.Current.SearchSavesOnline && !PcResources.IsLowSpace; } }

        /// <summary>Le cartelle del gioco che esistono su questo PC. Può interrogare la rete: da chiamare in background.</summary>
        public static List<OnlineSaveFolder> For(AppEntry g)
        {
            if (!Enabled) return new List<OnlineSaveFolder>();
            var raw = Cached(g, true);
            return raw == null ? new List<OnlineSaveFolder>() : Resolve(raw, g);
        }

        /// <summary>Solo ciò che è già in cache, senza rete: serve a non scambiare per residui i dati dei giochi installati.</summary>
        public static List<string> KnownPaths(AppEntry g)
        {
            var raw = Cached(g, false);
            return raw == null ? new List<string>() : Resolve(raw, g).Select(f => f.Path).ToList();
        }

        /// <summary>
        /// AppID di Steam del titolo, anche se la copia non è di Steam: gli emulatori e le crack salvano in cartelle con quel numero
        /// (…\Goldberg SteamEmu Saves\1234) e spesso nella cartella del gioco non è scritto da nessuna parte.
        /// </summary>
        public static List<string> AppIdsFor(AppEntry g)
        {
            if (!Enabled) return new List<string>();
            var raw = Cached(g, true);
            var list = raw == null ? null : Json.Child(raw, "AppIds") as object[];
            return list == null ? new List<string>() : list.OfType<string>().ToList();
        }

        /// <summary>Le cartelle dentro quella del gioco indicate dal database ({{p|game}}\Saves).</summary>
        public static List<string> InGamePaths(AppEntry g)
        {
            var result = new List<string>();
            if (!Enabled || string.IsNullOrEmpty(g.InstallDir)) return result;
            var raw = Cached(g, true);
            var list = raw == null ? null : Json.Child(raw, "Saves") as object[];
            if (list == null) return result;
            foreach (var o in list)
            {
                string path = ExpandInGame(o as string, g.InstallDir);
                if (path != null && !result.Any(r => PathUtil.Same(r, path))) result.Add(path);
            }
            return result;
        }

        internal static List<string> ExtractAppIds(string wikitext)
        {
            var ids = new List<string>();
            foreach (Match m in Regex.Matches(wikitext, @"\|\s*steam appid(?: side)?\s*=\s*([\d,\s]+)", RegexOptions.IgnoreCase))
                foreach (var id in m.Groups[1].Value.Split(new[] { ',', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    if (id.Length >= 2 && !ids.Contains(id)) ids.Add(id);
            return ids;
        }

        internal static string ExpandInGame(string cell, string gameDir)
        {
            if (string.IsNullOrWhiteSpace(cell) || string.IsNullOrEmpty(gameDir)) return null;
            var m = Regex.Match(cell.Trim(), @"^\{\{p\|game\}\}(.*)$", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string rest = CutVariablePart(m.Groups[1].Value);
            if (rest.Length == 0 || rest.Contains("..") || rest.IndexOf(':') >= 0) return null;
            string path = PathUtil.Normalize(Path.Combine(gameDir, rest));
            return path != null && PathUtil.IsUnder(path, gameDir) && Directory.Exists(path) ? path : null;
        }

        private static string CutVariablePart(string rest)
        {
            rest = rest.Replace('/', '\\');
            int cut = rest.IndexOfAny(new[] { '<', '{', '*', '?' });
            if (cut >= 0) rest = rest.Substring(0, rest.LastIndexOf('\\', Math.Max(0, cut - 1)) + 1);
            return rest.Trim().Trim('\\');
        }

        private static IDictionary<string, object> Cached(AppEntry g, bool allowNetwork)
        {
            string key = NameUtil.Norm(g.Name);
            lock (Sync)
            {
                object found;
                if (Cache.TryGetValue(key, out found))
                {
                    var d = found as IDictionary<string, object>;
                    DateTime when;
                    bool hasData = d != null && (Json.Child(d, "Saves") as object[] ?? new object[0]).Length + (Json.Child(d, "Config") as object[] ?? new object[0]).Length > 0;
                    if (d != null && DateTime.TryParse(Json.Str(d, "When"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out when)
                        && (DateTime.Now - when).TotalDays <= (hasData ? KeepDays : RetryMissDays))
                        return d;
                }
            }
            if (!allowNetwork) return null;
            var names = new List<string>();
            string known = CoverService.KnownTitle(g);
            if (!string.IsNullOrWhiteSpace(known)) names.Add(known);
            names.Add(g.Name);
            string text = null;
            try
            {
                string title = CoverService.PcGamingWikiTitle(names);
                if (title != null) text = CoverService.PcGamingWikiText(title);
            }
            catch (Exception ex) { Log.Warn("Ricerca online dei salvataggi di {0}: {1}", g.Name, ex.Message); }
            var saves = new List<string>();
            var config = new List<string>();
            var ids = new List<string>();
            if (text != null)
            {
                Extract(text, "saves", saves);
                Extract(text, "config", config);
                ids = ExtractAppIds(text);
            }
            // nessuna risposta (rete assente o sito irraggiungibile) non vuol dire che il gioco non è sul database: si riprova al prossimo avvio
            if (text == null && CoverService.LastDownloadFailed) return null;
            var entry = new Dictionary<string, object> { { "When", DateTime.Now.ToString("o") }, { "Saves", saves.ToArray() }, { "Config", config.ToArray() }, { "AppIds", ids.ToArray() } };
            lock (Sync)
            {
                Cache[key] = entry;
                if (!AppSettings.SaveDisabled)
                {
                    try { PathUtil.WriteAllTextAtomic(CacheFile, Json.Serialize(Cache)); }
                    catch (Exception ex) { Log.Warn("Salvataggio della cache dei salvataggi: {0}", ex.Message); }
                }
            }
            return Json.Parse(Json.Serialize(entry)) as IDictionary<string, object>;
        }

        private static Dictionary<string, object> Cache
        {
            get
            {
                if (cache == null)
                {
                    try { cache = File.Exists(CacheFile) ? Json.Parse(File.ReadAllText(CacheFile)) as Dictionary<string, object> : null; }
                    catch { cache = null; }
                    if (cache == null) cache = new Dictionary<string, object>();
                }
                return cache;
            }
        }

        private static List<OnlineSaveFolder> Resolve(IDictionary<string, object> raw, AppEntry g)
        {
            var result = new List<OnlineSaveFolder>();
            foreach (var pair in new[] { Tuple.Create("Saves", false), Tuple.Create("Config", true) })
            {
                var list = Json.Child(raw, pair.Item1) as object[];
                if (list == null) continue;
                foreach (var o in list)
                {
                    string path = Expand(o as string);
                    if (path == null || result.Any(r => PathUtil.Same(r.Path, path))) continue;
                    result.Add(new OnlineSaveFolder { Path = path, IsConfig = pair.Item2, NameMatches = IsSpecific(path, g) });
                }
            }
            return result;
        }

        /// <summary>Le celle di {{Game data/saves|Windows|…}}: una riga per percorso, senza note e riferimenti.</summary>
        internal static void Extract(string wikitext, string kind, List<string> into)
        {
            string marker = "{{Game data/" + kind + "|Windows|";
            int at = 0;
            while ((at = wikitext.IndexOf(marker, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int start = at + marker.Length, depth = 2, i = start;
                for (; i < wikitext.Length - 1 && depth > 0; i++)
                {
                    if (wikitext[i] == '{' && wikitext[i + 1] == '{') { depth += 2; i++; }
                    else if (wikitext[i] == '}' && wikitext[i + 1] == '}') { depth -= 2; i++; }
                }
                if (depth != 0) return;
                string cell = wikitext.Substring(start, i - 2 - start);
                at = i;
                cell = Regex.Replace(cell, @"<ref[^>]*?/>|<ref.*?</ref>|\{\{note\|.*?\}\}|\{\{cn.*?\}\}", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                foreach (var part in Regex.Split(cell, @"<br\s*/?>|\n", RegexOptions.IgnoreCase))
                {
                    string p = part.Trim();
                    if (p.Length > 0 && !into.Contains(p)) into.Add(p);
                }
            }
        }

        /// <summary>
        /// Da "{{p|userprofile\Documents}}\Editore\Gioco\&lt;user-id&gt;\" al percorso reale, fermandosi prima delle parti variabili.
        /// Null se non è nelle cartelle dati dell'utente, se non esiste o se coincide con una cartella comune.
        /// </summary>
        internal static string Expand(string cell)
        {
            if (string.IsNullOrWhiteSpace(cell)) return null;
            var m = Regex.Match(cell.Trim(), @"^\{\{p\|([^}]+)\}\}(.*)$", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string root;
            switch (m.Groups[1].Value.Trim().Replace('/', '\\').ToLowerInvariant())
            {
                case "userprofile": root = KnownDirs.UserProfile; break;
                case "userprofile\\documents": root = KnownDirs.Documents; break;
                case "userprofile\\saved games": root = KnownDirs.SavedGames; break;
                case "userprofile\\appdata\\locallow": root = KnownDirs.LocalLow; break;
                case "appdata": root = KnownDirs.RoamingAppData; break;
                case "localappdata": root = KnownDirs.LocalAppData; break;
                case "programdata": case "allusers": root = KnownDirs.ProgramData; break;
                case "public": root = KnownDirs.PublicDocuments == null ? null : Path.GetDirectoryName(KnownDirs.PublicDocuments); break;
                default: return null; // cartella del gioco, di Steam, registro: già gestiti in altro modo
            }
            if (root == null) return null;
            // prima parte variabile: <user-id>, {{p|uid}}, *.sav
            string rest = CutVariablePart(m.Groups[2].Value);
            if (rest.Length == 0 || rest.Contains("..") || rest.IndexOf(':') >= 0) return null;
            string path = PathUtil.Normalize(Path.Combine(root, rest));
            if (path == null || !Directory.Exists(path)) return null;
            foreach (var common in new[] { KnownDirs.UserProfile, KnownDirs.Documents, KnownDirs.SavedGames, KnownDirs.MyGames, KnownDirs.LocalAppData, KnownDirs.RoamingAppData, KnownDirs.LocalLow, KnownDirs.ProgramData, KnownDirs.PublicDocuments })
                if (PathUtil.Same(path, common)) return null;
            if (!new[] { KnownDirs.Documents, KnownDirs.SavedGames, KnownDirs.LocalAppData, KnownDirs.RoamingAppData, KnownDirs.LocalLow, KnownDirs.ProgramData, KnownDirs.PublicDocuments }.Any(r => PathUtil.IsUnder(path, r))) return null;
            return path;
        }

        /// <summary>Una cartella di primo livello con un nome estraneo può essere dell'editore e contenere altri giochi: si propone come «Da verificare».</summary>
        private static bool IsSpecific(string path, AppEntry g)
        {
            string name = NameUtil.Norm(Path.GetFileName(path));
            string title = NameUtil.Norm(g.Name);
            if (name.Length > 0 && (title.Contains(name) || name.Contains(title))) return true;
            foreach (var word in Regex.Split(g.Name, @"[^\p{L}\p{N}]+").Where(w => w.Length >= 4))
                if (name.Contains(NameUtil.Norm(word))) return true;
            string parent = Path.GetDirectoryName(path);
            return !new[] { KnownDirs.Documents, KnownDirs.SavedGames, KnownDirs.LocalAppData, KnownDirs.RoamingAppData, KnownDirs.LocalLow, KnownDirs.ProgramData, KnownDirs.MyGames }.Any(r => PathUtil.Same(parent, r));
        }
    }
}
