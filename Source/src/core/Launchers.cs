using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace AppManager
{
    // ====================================================================== Steam

    internal static class SteamInfo
    {
        private static bool loaded;
        private static string steamPath;
        private static readonly List<string> steamApps = new List<string>();
        private static readonly List<string> userDirs = new List<string>();
        private static readonly HashSet<string> libraryAppIds = new HashSet<string>();
        private static Dictionary<uint, SteamAppInfo> appInfo;
        private static readonly object Sync = new object();

        /// <summary>App di sistema di Steam da non considerare giochi.</summary>
        public static readonly HashSet<string> SystemAppIds = new HashSet<string>
        {
            "7", "760", "228980", "241100", "1070560", "1391110", "1628350", "1493710", "2180100", "1887720",
            "1580130", "1420170", "1113280", "1245040", "961940", "858280", "930400", "1054830",
        };

        public static void Reset()
        {
            lock (Sync) { loaded = false; appInfo = null; gameNames = null; steamApps.Clear(); userDirs.Clear(); libraryAppIds.Clear(); steamPath = null; }
        }

        private static void EnsureLoaded()
        {
            lock (Sync)
            {
                if (loaded) return;
                loaded = true;
                try { Load(); }
                catch (Exception ex) { Log.Error("Rilevamento Steam", ex); }
            }
        }

        private static void Load()
        {
            string p = null;
            using (var k = RegUtil.Open(@"HKCU\Software\Valve\Steam"))
                if (k != null) p = RegUtil.GetString(k, "SteamPath");
            if (string.IsNullOrEmpty(p))
                using (var k = RegUtil.Open(@"HKLM\SOFTWARE\WOW6432Node\Valve\Steam"))
                    if (k != null) p = RegUtil.GetString(k, "InstallPath");
            p = PathUtil.RealCase(PathUtil.Normalize(p));
            if (p == null || !Directory.Exists(p)) return;
            steamPath = p;

            var libs = new List<string> { p };
            var vdf = VdfNode.ParseFile(Path.Combine(p, "steamapps", "libraryfolders.vdf"));
            var root = vdf == null ? null : (vdf.Node("libraryfolders") ?? vdf.Node("LibraryFolders"));
            if (root != null)
            {
                foreach (var kv in root.Items)
                {
                    var node = kv.Value as VdfNode;
                    var apps = node == null ? null : node.Node("apps");
                    if (apps != null) foreach (var id in apps.Items.Keys) libraryAppIds.Add(id);
                    string lib = node != null ? node.Str("path") : kv.Value as string;
                    if (!string.IsNullOrEmpty(lib) && (node != null || kv.Key.All(char.IsDigit)))
                    {
                        lib = PathUtil.RealCase(PathUtil.Normalize(lib));
                        if (lib != null && !libs.Any(l => PathUtil.Same(l, lib))) libs.Add(lib);
                    }
                }
            }
            foreach (var lib in libs)
            {
                string sa = Path.Combine(lib, "steamapps");
                if (Directory.Exists(sa)) steamApps.Add(sa);
            }
            foreach (var d in PathUtil.Dirs(Path.Combine(p, "userdata")))
                if (Path.GetFileName(d).All(char.IsDigit) && Path.GetFileName(d) != "0") userDirs.Add(d);
        }

        public static string SteamPath { get { EnsureLoaded(); return steamPath; } }
        public static List<string> SteamAppsDirs { get { EnsureLoaded(); return steamApps; } }
        public static List<string> UserDataDirs { get { EnsureLoaded(); return userDirs; } }

        /// <summary>
        /// AppID installati secondo Steam: manifest presenti più l'elenco in libraryfolders.vdf
        /// (che include anche le librerie su dischi scollegati).
        /// </summary>
        public static HashSet<string> InstalledAppIds()
        {
            EnsureLoaded();
            var set = new HashSet<string>(libraryAppIds);
            foreach (var sa in steamApps)
            {
                foreach (var acf in PathUtil.Files(sa, "appmanifest_*.acf"))
                {
                    string n = Path.GetFileNameWithoutExtension(acf);
                    set.Add(n.Substring("appmanifest_".Length));
                }
            }
            return set;
        }

        /// <summary>Nomi delle cartelle in steamapps\common appartenenti a giochi installati.</summary>
        public static HashSet<string> InstalledDirNames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sa in SteamAppsDirs)
            {
                foreach (var acf in PathUtil.Files(sa, "appmanifest_*.acf"))
                {
                    var node = VdfNode.ParseFile(acf);
                    var st = node == null ? null : node.Node("AppState");
                    string dir = st == null ? null : st.Str("installdir");
                    if (!string.IsNullOrEmpty(dir)) set.Add(dir);
                }
            }
            return set;
        }

        public static Dictionary<uint, SteamAppInfo> AppInfo
        {
            get
            {
                EnsureLoaded();
                lock (Sync)
                {
                    if (appInfo == null)
                    {
                        appInfo = steamPath == null ? new Dictionary<uint, SteamAppInfo>()
                            : SteamAppInfoReader.Read(Path.Combine(steamPath, "appcache", "appinfo.vdf"));
                    }
                    return appInfo;
                }
            }
        }

        private static HashSet<string> gameNames;

        /// <summary>Nomi (normalizzati) di tutti i giochi che Steam conosce su questo PC: servono a riconoscere i giochi tra i residui.</summary>
        public static HashSet<string> GameNameKeys()
        {
            lock (Sync)
            {
                if (gameNames != null) return gameNames;
            }
            var set = new HashSet<string>();
            foreach (var info in AppInfo.Values)
            {
                if (string.IsNullOrEmpty(info.Name) || info.Type == null) continue;
                string t = info.Type.ToLowerInvariant();
                if (t != "game" && t != "demo") continue;
                string k = NameUtil.Norm(info.Name);
                if (k.Length >= 4) set.Add(k);
            }
            lock (Sync) gameNames = set;
            return set;
        }

        public static string AppName(string appId)
        {
            uint id;
            SteamAppInfo info;
            if (uint.TryParse(appId, out id) && AppInfo.TryGetValue(id, out info) && !string.IsNullOrEmpty(info.Name))
                return info.Name;
            return null;
        }

        /// <summary>Cerca una copertina nella cache locale di Steam (nessun download).</summary>
        public static string FindImage(string appId, bool portrait)
        {
            return FindImage(appId, portrait ? ArtKind.Portrait : ArtKind.Header);
        }

        /// <summary>Cerca un'immagine del gioco nella cache locale di Steam (nessun download).</summary>
        public static string FindImage(string appId, ArtKind kind)
        {
            if (string.IsNullOrEmpty(appId) || SteamPath == null) return null;
            string cache = Path.Combine(SteamPath, "appcache", "librarycache");
            string[] names;
            switch (kind)
            {
                case ArtKind.Portrait: names = new[] { "library_600x900.jpg", "library_600x900_2x.jpg", "library_capsule.jpg", "library_capsule_2x.jpg" }; break;
                case ArtKind.Hero: names = new[] { "library_hero.jpg", "library_hero_2x.jpg" }; break;
                case ArtKind.Logo: names = new[] { "logo.png", "logo_2x.png" }; break;
                default: names = new[] { "library_header.jpg", "header.jpg", "library_header_2x.jpg" }; break;
            }
            string dir = Path.Combine(cache, appId);
            if (Directory.Exists(dir))
            {
                foreach (var name in names)
                {
                    try
                    {
                        var found = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
                        if (found != null) return found;
                    }
                    catch { }
                }
            }
            foreach (var name in names)
            {
                string flat = Path.Combine(cache, appId + "_" + name);
                if (File.Exists(flat)) return flat;
            }
            return null;
        }

        /// <summary>
        /// Giochi (AppKind.Game) oppure applicazioni come Wallpaper Engine o Blender (AppKind.Program) installati con Steam.
        /// Gli strumenti (SteamVR, server dedicati, Proton...) compaiono tra i programmi solo se richiesto.
        /// </summary>
        public static List<AppEntry> DetectApps(AppKind kind, bool showTools)
        {
            var apps = new List<AppEntry>();
            foreach (var sa in SteamAppsDirs)
            {
                foreach (var acf in PathUtil.Files(sa, "appmanifest_*.acf"))
                {
                    var node = VdfNode.ParseFile(acf);
                    var st = node == null ? null : node.Node("AppState");
                    if (st == null) continue;
                    string appId = st.Str("appid");
                    string installDir = st.Str("installdir");
                    if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(installDir)) continue;
                    if (SystemAppIds.Contains(appId)) continue;
                    string type = AppType(appId);
                    bool isProgram = type == "application" || type == "tool" || type == "config";
                    if (isProgram != (kind == AppKind.Program)) continue;
                    if (type == "config" || (type == "tool" && !showTools)) continue;
                    var g = new AppEntry
                    {
                        Id = "steam:" + appId,
                        Kind = kind,
                        Name = NameUtil.CleanDisplayName(st.Str("name") ?? installDir),
                        Launcher = LauncherKind.Steam,
                        InstallDir = Path.Combine(sa, "common", installDir),
                        SteamAppId = appId,
                        SteamLibrary = sa,
                        SteamManifestPath = acf,
                        Source = L.T("Libreria Steam"),
                        Via = FoundVia.Launcher,
                    };
                    long size;
                    if (long.TryParse(st.Str("SizeOnDisk"), out size) && size > 0) g.SizeBytes = size;
                    long updated;
                    if (long.TryParse(st.Str("LastUpdated"), out updated) && updated > 0)
                        g.InstallDate = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(updated).ToLocalTime();
                    g.CoverImage = FindImage(appId, ArtKind.Portrait);
                    g.HeaderImage = FindImage(appId, ArtKind.Header);
                    g.HeroImage = FindImage(appId, ArtKind.Hero);
                    g.LogoImage = FindImage(appId, ArtKind.Logo);
                    g.UninstallKey = FindUninstallKey("Steam App " + appId);
                    if (g.UninstallKey != null)
                        using (var k = RegUtil.Open(g.UninstallKey))
                            if (k != null)
                            {
                                g.Publisher = RegUtil.GetString(k, "Publisher");
                                g.IconPath = RegUtil.GetString(k, "DisplayIcon");
                            }
                    if (kind == AppKind.Program && type == "tool") g.Source = L.T("Libreria Steam (strumento)");
                    apps.Add(g);
                }
            }
            return apps;
        }

        /// <summary>Tipo dell'app secondo Steam ("game", "application", "tool"...), in minuscolo; null se sconosciuto.</summary>
        private static string AppType(string appId)
        {
            uint id;
            SteamAppInfo info;
            if (!uint.TryParse(appId, out id) || !AppInfo.TryGetValue(id, out info) || info.Type == null) return null;
            return info.Type.ToLowerInvariant();
        }

        public static string FindUninstallKey(string keyName)
        {
            foreach (var root in new[] { @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                string full = root + "\\" + keyName;
                if (RegUtil.KeyExists(full)) return full;
            }
            return null;
        }

        /// <summary>File in cui giochi ed emulatori delle API di Steam dichiarano l'AppID.</summary>
        private static readonly HashSet<string> AppIdFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "steam_appid.txt", "steam_emu.ini", "cream_api.ini", "onlinefix.ini", "smartsteamemu.ini", "coldclientloader.ini",
            "codex.ini", "empress.ini", "rune.ini", "ali213.ini", "3dmgame.ini", "hlm.ini", "steam_api.ini", "steamclient.ini",
            "configs.app.ini", "configs.main.ini", "configs.user.ini", "valve.ini", "steam_settings.ini",
        };

        private static readonly System.Text.RegularExpressions.Regex AppIdLine = new System.Text.RegularExpressions.Regex(
            @"^\s*(?:real)?app_?id\s*=\s*(\d{2,10})\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // anche i commenti del tipo "Game data is stored at ...\Steam\RUNE\1902960"
        private static readonly System.Text.RegularExpressions.Regex AppIdPath = new System.Text.RegularExpressions.Regex(
            @"\\(?:RUNE|CODEX|EMPRESS|SKIDROW|OnlineFix|Goldberg SteamEmu Saves|GSE Saves|SmartSteamEmu)\\(\d{2,10})\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// AppID Steam dichiarati nei file del gioco (steam_appid.txt, configurazioni degli emulatori delle API di Steam).
        /// Si cerca in profondità: nei giochi Unreal la configurazione sta in Engine\Binaries\ThirdParty\Steamworks\...\Win64.
        /// </summary>
        public static IEnumerable<string> AppIdsInFolder(string dir, int maxDepth = 7)
        {
            var ids = new HashSet<string>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return ids;
            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(dir, 0));
            int visited = 0;
            while (queue.Count > 0 && visited < 3000)
            {
                var cur = queue.Dequeue();
                visited++;
                foreach (var f in PathUtil.Files(cur.Key))
                {
                    string name = Path.GetFileName(f);
                    bool known = AppIdFiles.Contains(name);
                    bool steamIni = (name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
                                    && name.IndexOf("steam", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!known && !steamIni) continue;
                    try
                    {
                        if (new FileInfo(f).Length > 200000) continue;
                        if (name.Equals("steam_appid.txt", StringComparison.OrdinalIgnoreCase))
                        {
                            string t = File.ReadAllText(f).Trim();
                            if (t.Length > 1 && t.Length < 12 && t.All(char.IsDigit)) ids.Add(t);
                            continue;
                        }
                        foreach (var line in File.ReadLines(f))
                        {
                            var m = AppIdLine.Match(line);
                            if (m.Success) ids.Add(m.Groups[1].Value);
                            var p = AppIdPath.Match(line);
                            if (p.Success) ids.Add(p.Groups[1].Value);
                        }
                    }
                    catch { }
                }
                if (cur.Value < maxDepth)
                    foreach (var d in PathUtil.Dirs(cur.Key))
                        if (!PathUtil.IsReparsePoint(d)) queue.Enqueue(new KeyValuePair<string, int>(d, cur.Value + 1));
            }
            return ids;
        }
    }

    // ====================================================================== Epic Games

    internal static class EpicInfo
    {
        public static string ManifestsDir
        {
            get
            {
                using (var k = RegUtil.Open(@"HKLM\SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher"))
                {
                    string p = k == null ? null : RegUtil.GetString(k, "AppDataPath");
                    if (!string.IsNullOrEmpty(p)) return Path.Combine(p, "Manifests");
                }
                return Path.Combine(KnownDirs.ProgramData, @"Epic\EpicGamesLauncher\Data\Manifests");
            }
        }

        public static string LauncherInstalledDat
        {
            get { return Path.Combine(KnownDirs.ProgramData, @"Epic\UnrealEngineLauncher\LauncherInstalled.dat"); }
        }

        public static List<AppEntry> DetectGames()
        {
            var games = new List<AppEntry>();
            foreach (var item in PathUtil.Files(ManifestsDir, "*.item"))
            {
                try
                {
                    var obj = Json.Parse(File.ReadAllText(item)) as IDictionary<string, object>;
                    if (obj == null) continue;
                    string appName = Json.Str(obj, "AppName");
                    string main = Json.Str(obj, "MainGameAppName");
                    string dir = PathUtil.Normalize(Json.Str(obj, "InstallLocation"));
                    if (string.IsNullOrEmpty(appName) || dir == null) continue;
                    if (!string.IsNullOrEmpty(main) && !string.Equals(main, appName, StringComparison.OrdinalIgnoreCase)) continue; // DLC
                    object cats;
                    if (obj.TryGetValue("AppCategories", out cats) && cats is object[])
                    {
                        var list = ((object[])cats).Select(c => Convert.ToString(c).ToLowerInvariant()).ToList();
                        if (list.Contains("engines") || list.Contains("plugins") || list.Contains("digitalextras")) continue;
                    }
                    var g = new AppEntry
                    {
                        Id = "epic:" + appName,
                        Kind = AppKind.Game,
                        Name = NameUtil.CleanDisplayName(Json.Str(obj, "DisplayName") ?? appName),
                        Launcher = LauncherKind.Epic,
                        InstallDir = dir,
                        EpicAppName = appName,
                        EpicManifestPath = item,
                        Source = "Epic Games Launcher",
                        Via = FoundVia.Launcher,
                    };
                    long size;
                    if (long.TryParse(Json.Str(obj, "InstallSize"), out size) && size > 0) g.SizeBytes = size;
                    string exe = Json.Str(obj, "LaunchExecutable");
                    if (!string.IsNullOrEmpty(exe)) g.MainExe = Path.Combine(dir, exe);
                    games.Add(g);
                }
                catch (Exception ex) { Log.Error("Manifest Epic " + item, ex); }
            }
            return games;
        }

        public static Process[] LauncherProcesses()
        {
            return Process.GetProcessesByName("EpicGamesLauncher");
        }

        /// <summary>Rimuove il gioco dagli elenchi del launcher Epic (manifest e LauncherInstalled.dat).</summary>
        public static void Unregister(AppEntry g)
        {
            if (!string.IsNullOrEmpty(g.EpicManifestPath) && File.Exists(g.EpicManifestPath))
                File.Delete(g.EpicManifestPath);
            string dat = LauncherInstalledDat;
            if (!File.Exists(dat)) return;
            var obj = Json.Parse(File.ReadAllText(dat)) as Dictionary<string, object>;
            object listObj;
            if (obj == null || !obj.TryGetValue("InstallationList", out listObj) || !(listObj is object[])) return;
            var list = ((object[])listObj).ToList();
            int removed = list.RemoveAll(e => string.Equals(Json.Str(e, "AppName"), g.EpicAppName, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return;
            File.Copy(dat, dat + ".bak", true);
            obj["InstallationList"] = list.ToArray();
            File.WriteAllText(dat, Json.Serialize(obj));
        }
    }

    // ====================================================================== Xbox / PC Game Pass

    internal static class XboxInfo
    {
        /// <summary>Cartelle "XboxGames" indicate dai file .GamingRoot nella radice delle unità.</summary>
        public static List<string> GameRoots()
        {
            var roots = new List<string>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    string marker = Path.Combine(drive.RootDirectory.FullName, ".GamingRoot");
                    if (!File.Exists(marker)) continue;
                    var bytes = File.ReadAllBytes(marker);
                    if (bytes.Length < 10 || bytes[0] != 'R' || bytes[1] != 'G' || bytes[2] != 'B' || bytes[3] != 'X') continue;
                    string text = Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8);
                    foreach (var rel in text.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string full = Path.Combine(drive.RootDirectory.FullName, rel);
                        if (Directory.Exists(full)) roots.Add(full);
                    }
                }
                catch { }
            }
            return roots;
        }

        public static List<AppEntry> DetectGames()
        {
            var games = new List<AppEntry>();
            foreach (var root in GameRoots())
            {
                foreach (var dir in PathUtil.Dirs(root))
                {
                    try
                    {
                        string content = Path.Combine(dir, "Content");
                        string manifest = Path.Combine(content, "appxmanifest.xml");
                        if (!File.Exists(manifest)) continue;
                        var doc = XDocument.Load(manifest);
                        var identity = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity");
                        if (identity == null) continue;
                        string idName = (string)identity.Attribute("Name");
                        string publisher = (string)identity.Attribute("Publisher");
                        string display = doc.Descendants().Where(e => e.Name.LocalName == "DisplayName").Select(e => e.Value).FirstOrDefault();
                        string exe = null, logo = null;
                        string config = Path.Combine(content, "MicrosoftGame.config");
                        if (File.Exists(config))
                        {
                            var cfg = XDocument.Load(config);
                            var visuals = cfg.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals");
                            if (visuals != null)
                            {
                                string dn = (string)visuals.Attribute("DefaultDisplayName");
                                if (IsUsableName(dn) && !IsUsableName(display)) display = dn;
                                logo = (string)visuals.Attribute("Square150x150Logo") ?? (string)visuals.Attribute("StoreLogo");
                            }
                            var ex = cfg.Descendants().FirstOrDefault(e => e.Name.LocalName == "Executable");
                            if (ex != null) exe = (string)ex.Attribute("Name");
                        }
                        if (!IsUsableName(display)) display = Path.GetFileName(dir);
                        var g = new AppEntry
                        {
                            Id = "xbox:" + idName,
                            Kind = AppKind.Game,
                            Name = NameUtil.CleanDisplayName(display),
                            Launcher = LauncherKind.Xbox,
                            InstallDir = dir,
                            Publisher = doc.Descendants().Where(e => e.Name.LocalName == "PublisherDisplayName").Select(e => e.Value).FirstOrDefault(),
                            PackageName = idName,
                            PackageFamily = string.IsNullOrEmpty(publisher) ? null : idName + "_" + PublisherId(publisher),
                            Source = "App Xbox",
                            Via = FoundVia.Launcher,
                        };
                        if (!string.IsNullOrEmpty(exe) && File.Exists(Path.Combine(content, exe))) g.MainExe = Path.Combine(content, exe);
                        if (!string.IsNullOrEmpty(logo)) g.CoverImage = ResolveScaledAsset(content, logo);
                        games.Add(g);
                    }
                    catch (Exception ex) { Log.Error(L.F("Gioco Xbox {0}", dir), ex); }
                }
            }
            return games;
        }

        private static bool IsUsableName(string s)
        {
            return !string.IsNullOrWhiteSpace(s) && !s.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Trova un asset MSIX considerando i qualificatori di scala (Logo.scale-100.png...).</summary>
        public static string ResolveScaledAsset(string content, string rel)
        {
            string full = Path.Combine(content, rel);
            if (File.Exists(full)) return full;
            string dir = Path.GetDirectoryName(full);
            string pattern = Path.GetFileNameWithoutExtension(full) + ".*" + Path.GetExtension(full);
            return PathUtil.Files(dir, pattern).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
        }

        /// <summary>PublisherId del Package Family Name: base32 dei primi 8 byte dello SHA-256 del publisher.</summary>
        public static string PublisherId(string publisher)
        {
            const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(Encoding.Unicode.GetBytes(publisher));
            var bits = new StringBuilder();
            for (int i = 0; i < 8; i++) bits.Append(Convert.ToString(hash[i], 2).PadLeft(8, '0'));
            bits.Append('0');
            var sb = new StringBuilder();
            for (int i = 0; i < 65; i += 5) sb.Append(alphabet[Convert.ToInt32(bits.ToString(i, 5), 2)]);
            return sb.ToString();
        }
    }
}
