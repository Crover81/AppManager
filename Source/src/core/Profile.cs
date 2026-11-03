using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AppManager
{
    /// <summary>Costruisce l'impronta (AppProfile) di un gioco o programma leggendo la sua cartella di installazione.</summary>
    internal static class ProfileBuilder
    {
        private static readonly ConcurrentDictionary<string, AppProfile> Cache = new ConcurrentDictionary<string, AppProfile>();

        private static readonly Regex ExeSuffix = new Regex(
            @"([-_](Win64|Win32|WinGDK|WinStore)[-_]Shipping|[-_]Shipping|[-_ ]?x64|[-_ ]?x86|[-_]dx1[12]|[-_]vulkan|[-_](BE|EAC)|[-_]64|[-_]Steam|[-_]GOG)$",
            RegexOptions.IgnoreCase);

        private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Engine", "_CommonRedist", "_Redist", "Redist", "redistributables", "DirectX", "EasyAntiCheat", "EasyAntiCheat_EOS", "BattlEye",
            "Support", "__Installer", "Installers", "vcredist", "dotnet", "PhysX", "_Offline Unlocker", "_Windows 7 Fix", "MonoBleedingEdge",
        };

        private static readonly Regex GenericExe = new Regex(
            @"^(unins\d*|uninstall.*|setup.*|.*redist.*|dxsetup|dxwebsetup|vc_?redist.*|.*crash.*|.*helper|quicksfv|.*unlocker.*|" +
            @"easyanticheat.*|start_protected_game|launcher|gamelaunchhelper|bootstrap.*|loader|run|runme|updater?|.*installer|" +
            @"epicwebhelper|cefsubprocess|benchmark|config(tool)?|settings|editor|server|dedicatedserver|touchup|cleanup|dotnet.*|oalinst|" +
            @"elevate|maintenancetool|notification_helper|squirrel|createdump|.*reporter|update|app|main|start|client)$",
            RegexOptions.IgnoreCase);

        public static void ClearCache() { Cache.Clear(); }

        public static AppProfile Get(AppEntry g)
        {
            return Cache.GetOrAdd(g.Id, id => Build(g));
        }

        public static void Forget(AppEntry g)
        {
            AppProfile removed;
            if (g != null) Cache.TryRemove(g.Id, out removed);
        }

        public static AppProfile Build(AppEntry g)
        {
            var p = new AppProfile { Entry = g, InstallDir = g.InstallDir };
            string title = g.IsGame ? L.T("nome del gioco") : L.T("nome del programma");
            AddName(p, g.Name, title, true);
            if (g.IsGame) AddName(p, NameUtil.StripEdition(g.Name), title, true);
            else AddName(p, NameUtil.BaseProgramName(g.Name), title, true);
            foreach (var d in g.Dirs) AddName(p, Path.GetFileName(d), L.T("nome della cartella di installazione"), true);
            foreach (var part in Regex.Split(g.Name ?? "", @"\s*(?::|\s-\s|\s–\s)\s*"))
                if (!string.Equals(part, g.Name, StringComparison.OrdinalIgnoreCase)) AddName(p, part, L.T("parte del titolo"), false);
            if (!string.IsNullOrEmpty(g.PackageName))
            {
                string pkg = g.PackageName;
                int dot = pkg.LastIndexOf('.');
                AddName(p, dot > 0 ? pkg.Substring(dot + 1) : pkg, g.IsGame ? L.T("nome del pacchetto Xbox") : L.T("nome del pacchetto dello Store"), true);
            }
            if (g.Launcher == LauncherKind.Steam && !string.IsNullOrEmpty(g.SteamAppId)) p.SteamAppIds.Add(g.SteamAppId);
            AddPublisher(p, g.Publisher);

            if (g.IsSingleFile && g.MainExe != null)
            {
                p.ExePaths.Add(g.MainExe);
                p.ExeNames.Add(Path.GetFileName(g.MainExe));
                AddExeName(p, Path.GetFileNameWithoutExtension(g.MainExe));
            }
            foreach (var dir in g.Dirs.Where(Directory.Exists).ToList())
            {
                try { ReadInstallDir(p, dir); }
                catch (Exception ex) { Log.Error(L.F("Analisi cartella {0}", dir), ex); }
            }
            if (g.IsGame && !string.IsNullOrEmpty(g.InstallDir) && Directory.Exists(g.InstallDir))
            {
                foreach (var id in SteamInfo.AppIdsInFolder(g.InstallDir)) p.SteamAppIds.Add(id);
                p.Engine = GameMarkers.Detect(g.InstallDir);
            }
            if (!g.IsGame && g.MainExe != null)
            {
                var vi = InstalledApps.VersionInfo(g.MainExe);
                if (vi != null)
                {
                    AddPublisher(p, vi.CompanyName);
                    AddName(p, vi.ProductName, L.T("prodotto indicato nell'eseguibile"), false);
                }
            }
            if (p.UnityCompany != null) AddPublisher(p, p.UnityCompany);
            return p;
        }

        public static void AddName(AppProfile p, string name, string reason, bool strong)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            string key = NameUtil.Norm(name);
            if (NameUtil.IsGeneric(key, p.Entry.Kind)) return;
            if (strong)
            {
                p.NameKeys.Add(key);
                p.WeakKeys.Remove(key);
                if (!p.Names.ContainsKey(name)) p.Names[name] = reason;
            }
            else if (!p.NameKeys.Contains(key) && key.Length >= 6)
            {
                p.WeakKeys.Add(key);
            }
        }

        /// <summary>Nome dell'eseguibile principale: per i programmi vale anche se corto (es. "vlc", "obs").</summary>
        private static void AddExeName(AppProfile p, string baseName)
        {
            string stripped = baseName;
            for (int i = 0; i < 2; i++) stripped = ExeSuffix.Replace(stripped, "");
            AddName(p, stripped, L.T("nome dell'eseguibile"), true);
            string key = NameUtil.Norm(stripped);
            if (!p.IsGame && key.Length == 3 && !NameUtil.IsGenericWord(key, p.Entry.Kind) && NameUtil.Norm(p.Entry.Name).StartsWith(key))
            {
                p.ShortKeys.Add(key);
                if (!p.Names.ContainsKey(stripped)) p.Names[stripped] = L.T("nome dell'eseguibile");
            }
        }

        public static void AddPublisher(AppProfile p, string publisher)
        {
            if (string.IsNullOrWhiteSpace(publisher)) return;
            string full = NameUtil.Norm(publisher);
            string trimmed = NameUtil.Norm(Regex.Replace(publisher,
                @"[\s,\.]+(inc|ltd|llc|gmbh|s\.?a|s\.?r\.?l|s\.?p\.?a|co|corp|corporation|limited|ab|as|e\.?u|s\.?r\.?o|k\.?k|b\.?v|oy|pty|plc|ag)\.?\s*$", "",
                RegexOptions.IgnoreCase));
            foreach (var k in new[] { full, trimmed })
                if (!NameUtil.IsGeneric(k) && !p.PublisherKeys.Contains(k)) p.PublisherKeys.Add(k);
        }

        private static void FindUnrealProjects(AppProfile p, string dir, bool hasEngineDir, int depth)
        {
            foreach (var sub in PathUtil.Dirs(dir))
            {
                string name = Path.GetFileName(sub);
                if (name.Equals("Engine", StringComparison.OrdinalIgnoreCase) || SkipDirs.Contains(name) || name.StartsWith("_")) continue;
                bool ue4 = Directory.Exists(Path.Combine(sub, "Binaries")) && (hasEngineDir || Directory.Exists(Path.Combine(sub, "Content")));
                bool ue3 = PathUtil.Dirs(sub).Any(x => Path.GetFileName(x).StartsWith("Cooked", StringComparison.OrdinalIgnoreCase));
                if (ue4 || ue3)
                {
                    if (!p.UnrealProjects.Contains(name, StringComparer.OrdinalIgnoreCase)) p.UnrealProjects.Add(name);
                    AddName(p, name, L.T("nome del progetto Unreal Engine"), true);
                }
                else if (depth == 0 && Directory.Exists(Path.Combine(sub, "Engine")))
                {
                    FindUnrealProjects(p, sub, true, 1);
                }
            }
        }

        private static void ReadInstallDir(AppProfile p, string dir)
        {
            if (p.IsGame)
            {
                bool hasEngineDir = Directory.Exists(Path.Combine(dir, "Engine"));

                // Unity: <Gioco>_Data\app.info contiene "Azienda" e "Prodotto" (usati per LocalLow e registro)
                var dataDirs = PathUtil.Dirs(dir).Where(d => d.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var d in dataDirs)
                {
                    string info = Path.Combine(d, "app.info");
                    if (!File.Exists(info)) continue;
                    try
                    {
                        var lines = File.ReadAllLines(info).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
                        if (lines.Length >= 2)
                        {
                            p.UnityCompany = lines[0];
                            p.UnityProduct = lines[1];
                            AddName(p, lines[1], L.T("nome del prodotto Unity"), true);
                        }
                    }
                    catch { }
                    string exeBase = Path.GetFileName(d);
                    AddName(p, exeBase.Substring(0, exeBase.Length - 5), L.T("nome dell'eseguibile Unity"), true);
                    break;
                }

                // Unreal Engine: cartelle progetto con Binaries/Content (UE4/5) o Cooked* (UE3).
                // Si guarda anche un livello sotto, per le raccolte (es. una trilogia con tre giochi UE).
                FindUnrealProjects(p, dir, hasEngineDir, 0);
            }

            // Eseguibili (fino a 4 livelli), esclusi redistribuibili, anti-cheat e strumenti del motore
            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(dir, 0));
            int budget = 4000;
            string mainExe = p.Entry.MainExe;
            while (queue.Count > 0 && budget > 0 && p.ExePaths.Count < 300)
            {
                var cur = queue.Dequeue();
                foreach (var f in PathUtil.Files(cur.Key, "*.exe"))
                {
                    budget--;
                    string baseName = Path.GetFileNameWithoutExtension(f);
                    bool isMain = mainExe != null && PathUtil.Same(f, mainExe);
                    if (GenericExe.IsMatch(baseName) && !isMain) continue;
                    p.ExePaths.Add(f);
                    p.ExeNames.Add(Path.GetFileName(f));
                    // solo gli eseguibili "principali" diventano nomi di confronto (non gli strumenti nelle sottocartelle)
                    string parent = Path.GetFileName(cur.Key);
                    bool main = isMain || cur.Value == 0 || baseName.EndsWith("-Shipping", StringComparison.OrdinalIgnoreCase)
                        || ((parent.Equals("Win64", StringComparison.OrdinalIgnoreCase) || parent.Equals("Win32", StringComparison.OrdinalIgnoreCase))
                            && Path.GetFileName(Path.GetDirectoryName(cur.Key) ?? "").Equals("Binaries", StringComparison.OrdinalIgnoreCase));
                    if (!main) continue;
                    if (p.IsGame || isMain) AddExeName(p, baseName);
                    else AddName(p, ExeSuffix.Replace(baseName, ""), L.T("nome dell'eseguibile"), false);
                }
                if (cur.Value >= 4) continue;
                foreach (var d in PathUtil.Dirs(cur.Key))
                {
                    budget--;
                    if (SkipDirs.Contains(Path.GetFileName(d)) || PathUtil.IsReparsePoint(d)) continue;
                    queue.Enqueue(new KeyValuePair<string, int>(d, cur.Value + 1));
                }
            }
        }
    }
}
