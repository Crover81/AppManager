using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AppManager
{
    /// <summary>Riconosce una cartella di gioco dalle "impronte" dei motori e delle librerie tipiche dei videogiochi.</summary>
    internal static class GameMarkers
    {
        private static readonly Dictionary<string, string> FileMarkers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "unityplayer.dll", "Unity" }, { "gameassembly.dll", "Unity" }, { "unitycrashhandler64.exe", "Unity" }, { "unitycrashhandler32.exe", "Unity" },
            { "steam_api.dll", "Steamworks" }, { "steam_api64.dll", "Steamworks" }, { "steam_appid.txt", "Steamworks" },
            // emulatori delle API di Steam e crack: dicono che è un gioco anche quando il motore non si riconosce
            { "steam_emu.ini", "Crack" }, { "codex.ini", "Crack" }, { "rune.ini", "Crack" }, { "empress.ini", "Crack" }, { "tenoke.ini", "Crack" }, { "flt.ini", "Crack" },
            { "plaza.ini", "Crack" }, { "cpy.ini", "Crack" }, { "rld.ini", "Crack" }, { "skidrow.ini", "Crack" }, { "ali213.ini", "Crack" }, { "3dmgame.ini", "Crack" },
            { "3dmgame.dll", "Crack" }, { "onlinefix.ini", "Crack" }, { "onlinefix64.dll", "Crack" }, { "cream_api.ini", "Crack" }, { "smartsteamemu.ini", "Crack" },
            { "coldclientloader.ini", "Crack" }, { "steamclient_loader.exe", "Crack" }, { "steamclient_loader_x64.exe", "Crack" }, { "hlm.ini", "Crack" }, { "lumaemu.ini", "Crack" },
            { "unsteam.ini", "Crack" }, { "uplay_emu.ini", "Crack" },
            // altri motori
            { "crysystem.dll", "CryEngine" }, { "cryrenderd3d11.dll", "CryEngine" }, { "engine2.dll", "Source" }, { "mss32.dll", "Miles" }, { "mss64.dll", "Miles" },
            { "love.dll", "LOVE" }, { "lime.ndll", "Haxe" }, { "rpg_rt.exe", "RPG Maker" }, { "microsoft.xna.framework.dll", "XNA" }, { "fna.dll", "XNA" },
            { "monogame.framework.dll", "XNA" }, { "re_chunk_000.pak", "RE Engine" }, { "idtechlauncher.exe", "id Tech" }, { "initfs_win32", "Frostbite" }, { "cri_ware_unity.dll", "Unity" },
            { "ue4prereqsetup_x64.exe", "Unreal Engine" }, { "ue3redist.exe", "Unreal Engine" },
            { "galaxy.dll", "GOG Galaxy" }, { "galaxy64.dll", "GOG Galaxy" }, { "goggame.sdb", "GOG" },
            { "eossdk-win64-shipping.dll", "Epic Online Services" }, { "eossdk-win32-shipping.dll", "Epic Online Services" },
            { "uplay_r1_loader.dll", "Ubisoft" }, { "uplay_r1_loader64.dll", "Ubisoft" }, { "upc_r2_loader64.dll", "Ubisoft" }, { "uplay_r2_loader64.dll", "Ubisoft" },
            { "nvngx_dlss.dll", "DLSS" }, { "nvngx_dlssg.dll", "DLSS" }, { "sl.interposer.dll", "DLSS" }, { "amd_fidelityfx_dx12.dll", "FSR" },
            { "ffx_fsr2_api_x64.dll", "FSR" }, { "amd_ags_x64.dll", "AMD AGS" }, { "libxess.dll", "XeSS" },
            { "bink2w64.dll", "Bink" }, { "binkw64.dll", "Bink" }, { "binkw32.dll", "Bink" }, { "bink2w32.dll", "Bink" },
            { "fmod.dll", "FMOD" }, { "fmod64.dll", "FMOD" }, { "fmodex.dll", "FMOD" }, { "fmodex64.dll", "FMOD" }, { "fmodstudio.dll", "FMOD" },
            { "fmodstudio64.dll", "FMOD" }, { "aksoundengine.dll", "Wwise" }, { "physx3_x64.dll", "PhysX" }, { "physx_64.dll", "PhysX" },
            { "physxloader.dll", "PhysX" }, { "physx3common_x64.dll", "PhysX" }, { "xinput1_3.dll", "XInput" }, { "d3dx9_43.dll", "DirectX 9" },
            { "openvr_api.dll", "OpenVR" }, { "discord_game_sdk.dll", "Discord SDK" }, { "data.win", "GameMaker" },
            { "game.rgss3a", "RPG Maker" }, { "game.rgss2a", "RPG Maker" }, { "game.rgssad", "RPG Maker" }, { "microsoftgame.config", "Xbox GDK" },
            { "hl2.exe", "Source" }, { "dxsetup.exe", "DirectX" }, { "easyanticheat_eos_setup.exe", "EasyAntiCheat" },
            { "start_protected_game.exe", "EasyAntiCheat" },
        };

        private static readonly Dictionary<string, string> DirMarkers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "easyanticheat", "EasyAntiCheat" }, { "easyanticheat_eos", "EasyAntiCheat" }, { "battleye", "BattlEye" },
            { "_commonredist", "Redist" }, { "_redist", "Redist" }, { "renpy", "Ren'Py" }, { "monobleedingedge", "Unity" }, { "steam_settings", "Crack" },
        };

        private static readonly Dictionary<string, string> ExtMarkers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".forge", "Ubisoft Anvil" }, { ".rpf", "Rockstar RAGE" }, { ".bdt", "FromSoftware" }, { ".vpk", "Source" }, { ".rpa", "Ren'Py" },
            { ".upk", "Unreal Engine" }, { ".utoc", "Unreal Engine" }, { ".ucas", "Unreal Engine" }, { ".ba2", "Creation Engine" }, { ".bsa", "Creation Engine" },
            { ".esm", "Creation Engine" }, { ".ttarch2", "Telltale" }, { ".cpk", "CRI" }, { ".psarc", "PSARC" }, { ".cas", "Frostbite" },
        };

        private static readonly string[] EnginePriority =
        {
            "Unreal Engine", "Unity", "Godot", "GameMaker", "RPG Maker", "Ren'Py", "Source", "Ubisoft Anvil", "Rockstar RAGE", "FromSoftware", "Xbox GDK"
        };

        private static readonly HashSet<string> WeakFileMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "xinput1_3.dll", "d3dx9_43.dll", "dxsetup.exe", "steam_appid.txt", "steam_emu.ini", "data.win",
        };

        /// <summary>Librerie che usano anche programmi che non sono giochi: da sole non bastano fuori dalle cartelle dedicate ai giochi.</summary>
        private static readonly HashSet<string> WeakLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Steamworks", "Epic Online Services", "DLSS", "FSR", "XeSS", "AMD AGS", "XInput", "DirectX 9", "DirectX", "Redist", "OpenVR", "Discord SDK",
        };

        private static readonly HashSet<string> TechnicalDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bin", "bin64", "bin32", "x64", "x86", "win64", "win32", "binaries", "exe", "retail", "_retail_", "game", "system", "program", "engine", "data", "content", "paks" };

        /// <summary>
        /// True se ci sono prove sicure che la cartella è proprio un gioco (motore o crack, non solo una libreria come Steamworks).
        /// Si guarda solo lì e nelle sue sottocartelle tecniche: una cartella che contiene dei giochi non è un gioco.
        /// </summary>
        public static bool IsStrong(string dir)
        {
            return Collect(dir, 1, d => TechnicalDirs.Contains(Path.GetFileName(d))).Any(l => !WeakLabels.Contains(l));
        }

        /// <summary>True se il nome di file (es. caricato da un eseguibile) è tipico dei videogiochi.</summary>
        public static bool IsMarkerFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            if (FileMarkers.ContainsKey(fileName)) return !WeakFileMarkers.Contains(fileName);
            return fileName.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Restituisce il motore/tecnologia riconosciuta, oppure null se la cartella non sembra un gioco.</summary>
        public static string Detect(string dir, int maxDepth = 2)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            var found = Collect(dir, maxDepth, null);
            if (found.Count == 0) return null;
            foreach (var e in EnginePriority) if (found.Contains(e)) return e;
            return L.T("Gioco");
        }

        private static HashSet<string> Collect(string dir, int maxDepth, Func<string, bool> descend)
        {
            var found = new HashSet<string>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return found;
            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(dir, 0));
            int budget = 6000;
            while (queue.Count > 0 && budget > 0)
            {
                var cur = queue.Dequeue();
                string[] files = PathUtil.Files(cur.Key);
                budget -= files.Length;
                var exeNames = new HashSet<string>(files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    .Select(f => Path.GetFileNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    string name = Path.GetFileName(f);
                    string label;
                    if (FileMarkers.TryGetValue(name, out label)) found.Add(label);
                    string ext = Path.GetExtension(name);
                    if (ExtMarkers.TryGetValue(ext, out label)) found.Add(label);
                    if (name.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase)) found.Add("Unreal Engine");
                    if (ext.Equals(".pak", StringComparison.OrdinalIgnoreCase) && cur.Key.EndsWith("\\Paks", StringComparison.OrdinalIgnoreCase)) found.Add("Unreal Engine");
                    if (ext.Equals(".pck", StringComparison.OrdinalIgnoreCase) && exeNames.Contains(Path.GetFileNameWithoutExtension(name))) found.Add("Godot");
                    if (name.StartsWith("goggame-", StringComparison.OrdinalIgnoreCase) && ext.Equals(".info", StringComparison.OrdinalIgnoreCase)) found.Add("GOG");
                    if (name.StartsWith("rgss", StringComparison.OrdinalIgnoreCase) && ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) found.Add("RPG Maker");
                    if (name.StartsWith("oo2core_", StringComparison.OrdinalIgnoreCase) && ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) found.Add("Oodle");
                }
                string[] dirs = PathUtil.Dirs(cur.Key);
                budget -= dirs.Length;
                foreach (var d in dirs)
                {
                    string name = Path.GetFileName(d);
                    string label;
                    if (DirMarkers.TryGetValue(name, out label)) found.Add(label);
                    if (name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)
                        && (File.Exists(Path.Combine(d, "app.info")) || File.Exists(Path.Combine(d, "globalgamemanagers"))))
                        found.Add("Unity");
                    if (name.Equals("Engine", StringComparison.OrdinalIgnoreCase)
                        && (Directory.Exists(Path.Combine(d, "Binaries")) || Directory.Exists(Path.Combine(d, "Content")))
                        && cur.Value <= 1)
                        found.Add("Unreal Engine");
                    if (name.Equals("www", StringComparison.OrdinalIgnoreCase)
                        && (File.Exists(Path.Combine(d, "js", "rpg_core.js")) || File.Exists(Path.Combine(d, "js", "rmmz_core.js"))))
                        found.Add("RPG Maker");
                    if (cur.Value < maxDepth && !PathUtil.IsReparsePoint(d) && (descend == null || descend(d))) queue.Enqueue(new KeyValuePair<string, int>(d, cur.Value + 1));
                }
            }
            return found;
        }
    }

    /// <summary>Voce di disinstallazione letta dal registro (quelle che Windows mostra in "App installate").</summary>
    internal sealed class UninstallEntry
    {
        public string KeyPath, KeyName, DisplayName, Publisher, InstallLocation, UninstallString, QuietUninstallString, DisplayIcon, DisplayVersion;
        public DateTime? InstallDate;
        public long EstimatedSizeKB;
        public bool IsSystemOrUpdate;
        public bool IsMsi;
        public bool NoRemove;
    }

    /// <summary>Strumenti comuni ai giochi e ai programmi: voci di disinstallazione, collegamenti, cartelle.</summary>
    internal static class InstalledApps
    {
        public static readonly string[] UninstallRoots =
        {
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        /// <summary>Voci "Steam App 123": giochi e applicazioni di Steam, gestiti leggendo Steam.</summary>
        public static readonly Regex SteamAppKey = new Regex(@"^Steam App \d+$", RegexOptions.IgnoreCase);

        public static List<UninstallEntry> ReadUninstallEntries()
        {
            var list = new List<UninstallEntry>();
            foreach (var root in UninstallRoots)
            {
                bool wow = RegUtil.IsWow32(root);
                using (var rk = RegUtil.Open(root))
                {
                    if (rk == null) continue;
                    foreach (var name in RegUtil.SubKeys(rk))
                    {
                        try
                        {
                            using (var k = rk.OpenSubKey(name))
                            {
                                if (k == null) continue;
                                var e = new UninstallEntry
                                {
                                    KeyPath = root + "\\" + name,
                                    KeyName = name,
                                    DisplayName = RegUtil.GetString(k, "DisplayName"),
                                    Publisher = RegUtil.GetString(k, "Publisher"),
                                    // percorsi con le variabili viste dal programma (a 32 bit: %ProgramFiles% = Program Files (x86))
                                    InstallLocation = RegUtil.GetPath(k, "InstallLocation", wow),
                                    UninstallString = RegUtil.GetPath(k, "UninstallString", wow),
                                    QuietUninstallString = RegUtil.GetPath(k, "QuietUninstallString", wow),
                                    DisplayIcon = RegUtil.GetPath(k, "DisplayIcon", wow),
                                    DisplayVersion = RegUtil.GetString(k, "DisplayVersion"),
                                };
                                object sc = k.GetValue("SystemComponent");
                                string release = RegUtil.GetString(k, "ReleaseType") ?? "";
                                e.IsSystemOrUpdate = (sc is int && (int)sc == 1) || !string.IsNullOrEmpty(RegUtil.GetString(k, "ParentKeyName"))
                                    || release.IndexOf("update", StringComparison.OrdinalIgnoreCase) >= 0 || release.IndexOf("hotfix", StringComparison.OrdinalIgnoreCase) >= 0;
                                object msi = k.GetValue("WindowsInstaller");
                                e.IsMsi = msi is int && (int)msi == 1;
                                object noRemove = k.GetValue("NoRemove");
                                e.NoRemove = noRemove is int && (int)noRemove == 1;
                                object size = k.GetValue("EstimatedSize");
                                if (size is int) e.EstimatedSizeKB = (int)size;
                                DateTime date;
                                string ds = RegUtil.GetString(k, "InstallDate");
                                if (!string.IsNullOrEmpty(ds) && DateTime.TryParseExact(ds.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                                    e.InstallDate = date;
                                list.Add(e);
                            }
                        }
                        catch { }
                    }
                }
            }
            return list;
        }

        /// <summary>Deduce la cartella di installazione di una voce del registro.</summary>
        public static string EntryInstallDir(UninstallEntry e)
        {
            string dir = PathUtil.Normalize(e.InstallLocation);
            if (dir != null && Directory.Exists(dir) && IsSpecificDir(dir) && !IsInstallerCache(dir)) return PathUtil.RealCase(dir);
            // dall'icona o dal programma di disinstallazione, risalendo le cartelle tecniche ("Programma\bin\app.exe" → "Programma")
            foreach (var candidate in new[] { e.DisplayIcon, e.UninstallString })
            {
                string exe = PathUtil.ExeFromCommand(candidate == null ? null : candidate.Split(',')[0]);
                exe = PathUtil.Normalize(exe);
                if (exe == null || !File.Exists(exe)) continue;
                string d = GuessRoot(exe);
                if (d == null || !IsSpecificDir(d) || IsInstallerCache(d)) continue;
                return PathUtil.RealCase(d);
            }
            return null;
        }

        /// <summary>Cartelle in cui gli installer tengono una copia di sé stessi (non sono la cartella del programma).</summary>
        public static bool IsInstallerCache(string dir)
        {
            return PathUtil.IsUnder(dir, KnownDirs.ProgramData, true) || PathUtil.IsUnder(dir, Path.Combine(KnownDirs.LocalAppData, "Package Cache"), true)
                || PathUtil.IsUnder(dir, KnownDirs.Temp, true) || PathUtil.IsUnder(dir, Path.Combine(KnownDirs.Windows, "Installer"), true)
                || dir.IndexOf("\\Package Cache\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>True se la cartella può essere quella di un singolo programma (non una cartella di sistema o condivisa).</summary>
        public static bool IsSpecificDir(string dir)
        {
            if (dir == null || dir.Length <= 3 || PathUtil.IsUnder(dir, KnownDirs.Windows, true)) return false;
            foreach (var shared in new[]
            {
                KnownDirs.ProgramFiles, KnownDirs.ProgramFilesX86, KnownDirs.ProgramData, KnownDirs.LocalAppData, KnownDirs.RoamingAppData,
                KnownDirs.LocalPrograms, KnownDirs.UserProfile, KnownDirs.Documents, KnownDirs.Desktop, KnownDirs.Downloads, KnownDirs.LocalLow,
                Path.Combine(KnownDirs.ProgramFiles, "Common Files"), Path.Combine(KnownDirs.ProgramFilesX86, "Common Files"),
                Path.Combine(KnownDirs.ProgramFiles, "WindowsApps"),
            })
                if (PathUtil.Same(dir, shared)) return false;
            return true;
        }

        private static readonly HashSet<string> DocumentExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".pdf", ".psd", ".ai", ".indd", ".cr2", ".cr3",
            ".nef", ".arw", ".dng", ".raf", ".heic", ".kdbx", ".pst", ".ost", ".eml", ".msg", ".vsdx", ".pub", ".one", ".epub", ".mobi",
        };

        private static readonly Regex ProjectFile = new Regex(
            @"(\.(sln|csproj|vbproj|vcxproj|fsproj|pyproj)|^(cmakelists\.txt|cargo\.toml|go\.mod|pom\.xml|build\.gradle(\.kts)?|package\.json|makefile|setup\.py|pyproject\.toml|requirements\.txt|composer\.json|\.gitignore))$",
            RegexOptions.IgnoreCase);

        private static readonly HashSet<string> DocSubfolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "doc", "docs", "documentation", "manual", "manuals", "help", "licenses", "license", "legal", "readme", "samples", "examples",
            "templates", "guide", "guides", "tutorials", "3rdparty", "third_party", "thirdparty",
        };

        /// <summary>
        /// True se nella cartella ci sono anche cose dell'utente (documenti, foto, un progetto di sviluppo con il suo repository):
        /// una cartella così non va mai eliminata per intero insieme a un programma.
        /// </summary>
        public static bool LooksLikeUserFolder(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                foreach (var m in new[] { ".git", ".svn", ".hg" })
                    if (Directory.Exists(Path.Combine(dir, m)) || File.Exists(Path.Combine(dir, m))) return true;
                int docs = 0, seen = 0;
                var queue = new Queue<KeyValuePair<string, int>>();
                queue.Enqueue(new KeyValuePair<string, int>(dir, 0));
                while (queue.Count > 0 && seen < 3000)
                {
                    var cur = queue.Dequeue();
                    foreach (var f in PathUtil.Files(cur.Key))
                    {
                        seen++;
                        if (cur.Value == 0 && ProjectFile.IsMatch(Path.GetFileName(f))) return true;
                        if (DocumentExts.Contains(Path.GetExtension(f)) && ++docs >= 5) return true;
                    }
                    if (cur.Value >= 2) continue;
                    foreach (var d in PathUtil.Dirs(cur.Key))
                        if (!DocSubfolders.Contains(Path.GetFileName(d)) && !PathUtil.IsReparsePoint(d)) queue.Enqueue(new KeyValuePair<string, int>(d, cur.Value + 1));
                }
                return false;
            }
            catch { return true; }
        }

        /// <summary>True se la cartella coincide, contiene o è contenuta nella cartella di un gioco o programma già noto.</summary>
        public static bool IsCovered(IEnumerable<AppEntry> entries, string dir)
        {
            if (dir == null) return false;
            foreach (var g in entries)
            {
                foreach (var d in g.Dirs)
                    if (PathUtil.IsUnder(dir, d, true) || PathUtil.IsUnder(d, dir)) return true;
            }
            return false;
        }

        /// <summary>
        /// Gioco o programma già noto la cui cartella è (o contiene) <paramref name="dir"/>: lo stesso elemento trovato per un'altra strada.
        /// Una cartella che ne contiene diverse (es. una libreria di Steam) non è "lo stesso elemento".
        /// </summary>
        public static AppEntry CoveringEntry(IEnumerable<AppEntry> entries, string dir)
        {
            if (dir == null) return null;
            foreach (var g in entries)
                foreach (var d in g.Dirs)
                    if (PathUtil.IsUnder(dir, d, true)) return g;
            return null;
        }

        /// <summary>Annota che anche <paramref name="via"/> ha trovato l'elemento che contiene la cartella (per i contatori delle Impostazioni).</summary>
        public static void MarkFound(IEnumerable<AppEntry> entries, string dir, FoundVia via)
        {
            var e = CoveringEntry(entries, dir);
            if (e != null) e.FoundBy.Add(via);
        }

        /// <summary>True se il file è dentro la cartella di un gioco o programma già noto (o è il suo eseguibile).</summary>
        public static bool Owns(IEnumerable<AppEntry> entries, string file)
        {
            foreach (var g in entries)
            {
                if (g.MainExe != null && PathUtil.Same(g.MainExe, file)) return true;
                foreach (var d in g.Dirs)
                    if (PathUtil.IsUnder(file, d)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ Collegamenti sul desktop e nel menu Start

        public static List<string> ShortcutFiles()
        {
            var files = new List<string>();
            foreach (var root in new[] { KnownDirs.Desktop, KnownDirs.PublicDesktop, KnownDirs.StartMenu, KnownDirs.CommonStartMenu })
                CollectShortcuts(root, 0, files);
            return files;
        }

        private static void CollectShortcuts(string dir, int depth, List<string> files)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            foreach (var f in PathUtil.Files(dir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".lnk" || ext == ".url") files.Add(f);
            }
            if (depth < 4)
                foreach (var d in PathUtil.Dirs(dir))
                    if (!PathUtil.IsReparsePoint(d)) CollectShortcuts(d, depth + 1, files);
        }

        public static bool IsInStartMenu(string lnk)
        {
            return PathUtil.IsUnder(lnk, KnownDirs.StartMenu) || PathUtil.IsUnder(lnk, KnownDirs.CommonStartMenu);
        }

        public static string ShortcutSource(string lnk)
        {
            return L.F("Collegamento {0}", IsInStartMenu(lnk) ? L.T("nel menu Start") : L.T("sul desktop"));
        }

        private static readonly HashSet<string> GenericBinDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bin", "bin64", "bin32", "x64", "x86", "win64", "win32", "binaries", "exe", "retail", "_retail_", "sources", "game", "system", "app", "cef", "program" };

        /// <summary>Risale dall'eseguibile alla cartella principale del gioco o programma.</summary>
        public static string GuessRoot(string exe)
        {
            string dir = Path.GetDirectoryName(exe);
            if (dir == null) return null;
            var parts = dir.Split('\\');
            for (int i = parts.Length - 1; i >= 2; i--)
            {
                if (!parts[i].Equals("Binaries", StringComparison.OrdinalIgnoreCase)) continue;
                string project = string.Join("\\", parts, 0, i);
                string parent = Path.GetDirectoryName(project);
                if (parent != null && parent.Length > 3 && Directory.Exists(Path.Combine(parent, "Engine"))) return parent;
                return project;
            }
            for (int i = 0; i < 2 && GenericBinDirs.Contains(Path.GetFileName(dir)); i++)
            {
                string up = Path.GetDirectoryName(dir);
                if (up == null || !IsSpecificDir(up)) break;
                dir = up;
            }
            return dir;
        }

        // ------------------------------------------------------------------ Eseguibile principale

        private static readonly Regex NotMainExe = new Regex(
            @"^(unins\d*|.*uninstall.*|.*setup.*|.*redist.*|dxwebsetup|vc_?redist.*|.*crash.*|unitycrashhandler.*|.*updater?|" +
            @"easyanticheat.*|start_protected_game|.*helper|quicksfv|.*unlocker.*|dotnet.*|oalinst|physx.*|uplay.*|.*installer|" +
            @".*trainer.*|.*crack.*|keygen.*|.*patcher|steamclient_loader.*|" +
            @"maintenancetool|elevate|notification_helper|squirrel|createdump|.*reporter)$",
            RegexOptions.IgnoreCase);

        /// <summary>True se l'eseguibile è un programma di installazione, aggiornamento o supporto (non l'applicazione).</summary>
        public static bool IsAuxiliaryExe(string exe)
        {
            return NotMainExe.IsMatch(Path.GetFileNameWithoutExtension(exe));
        }

        public static string FindMainExe(string dir, string name)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            string nName = NameUtil.Norm(name), nDir = NameUtil.Norm(Path.GetFileName(dir));
            var rootExes = PathUtil.Files(dir, "*.exe").Where(f => !IsAuxiliaryExe(f)).ToList();
            var match = rootExes.FirstOrDefault(f =>
            {
                string n = NameUtil.Norm(Path.GetFileNameWithoutExtension(f));
                return n.Length > 2 && (n == nName || n == nDir || nName.StartsWith(n) || n.StartsWith(nDir));
            });
            if (match != null) return match;
            if (rootExes.Count > 0) return rootExes.OrderByDescending(f => new FileInfo(f).Length).First();
            try
            {
                return PathUtil.FilesNoLinks(dir)
                    .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !IsAuxiliaryExe(f))
                    .Take(200).OrderByDescending(f => f.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>Informazioni di versione di un eseguibile (null se non leggibili).</summary>
        public static FileVersionInfo VersionInfo(string exe)
        {
            try { return exe != null && File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe) : null; }
            catch { return null; }
        }

        private static readonly Regex GameRootName = new Regex(@"^(games?|giochi|gaming|gog games|amazon games|riot games|steamunlocked|repacks?|fitgirl.*|dodi.*)(\s.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ProgramRootName = new Regex(@"^(portableapps|portable( apps)?|app portable|programs?( portable| utili)?|programmi( .*)?|utility|utilit[aà]|tools|strumenti)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AppNameNoise = new Regex(@"[\s._\-]*\b(x64|x86|win64|win32|windows|64bit|32bit|amd64|portable|v?\d+(\.\d+)+[a-z]?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Forma confrontabile di un nome di cartella o eseguibile: senza versione, architettura e sigle ("Foo_1.2_x64-CODEX" → "foo").</summary>
        private static string AppKey(string name)
        {
            return NameUtil.Norm(AppNameNoise.Replace(NameUtil.CleanFolderTitle(name) ?? "", ""));
        }

        /// <summary>L'eseguibile che porta il nome della sua cartella (Shotcut\shotcut.exe, Tenkaichi 3\Tenkaichi3Decomp.exe); null se non c'è.</summary>
        public static string ExeNamedLikeFolder(string dir)
        {
            string folder = AppKey(Path.GetFileName(dir));
            if (folder.Length < 3) return null;
            string best = null;
            int bestScore = 0;
            foreach (var exe in PathUtil.Files(dir, "*.exe"))
            {
                if (IsAuxiliaryExe(exe)) continue;
                string name = AppKey(Path.GetFileNameWithoutExtension(exe));
                if (name.Length > 3 && (name.EndsWith("64") || name.EndsWith("32"))) name = name.Substring(0, name.Length - 2);
                if (name.Length < 3 || NameUtil.IsGenericWord(name, AppKind.Game)) continue;
                int score = 0;
                if (name == folder) score = 1000;
                else if (name.Length >= 4 && folder.Length >= 4 && (name.Contains(folder) || folder.Contains(name)) && Math.Min(name.Length, folder.Length) * 2 >= Math.Max(name.Length, folder.Length))
                    score = Math.Min(name.Length, folder.Length);
                if (score > bestScore) { bestScore = score; best = exe; }
            }
            return best;
        }

        // ------------------------------------------------------------------ Giochi e programmi non registrati da nessuna parte

        /// <summary>Cartella trovata senza che nulla l'abbia registrata: una copia portable, un gioco piratato o spostato a mano.</summary>
        internal sealed class FoundApp
        {
            public string Dir, Exe, Name, Source;
            public bool IsGame, IsSingleFile, FromShortcut;
        }

        private const int ScanMaxDirs = 6000, ScanMaxMs = 4000, ScanMaxDepth = 4;
        private static readonly Regex ScanSkip = new Regex(
            @"^(\$.*|system volume information|windows|windows\.old|users|programdata|program files.*|recovery|perflogs|boot|efi|msocache|config\.msi|onedrivetemp|" +
            @"appdata|steamapps|steamlibrary|windowsapps|xboxgames|node_modules|__pycache__|site-packages|\.?venv|\.git|\.svn|\.hg|\.vs|my games|portableapps\.com|" +
            @"ue_\d.*|unity( hub)?|unreal engine|epic games launcher|launcher|android|android studio|sdk|.*[-_ ]sdk|godot.*|cryengine.*|blender.*|python\d*|jdk.*|jre.*|msys\d*|cygwin\d*|mingw.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly object FoundSync = new object();
        private static List<FoundApp> foundApps;
        private static DateTime foundAt;

        /// <summary>Tutto ciò che si trova fuori dai canali ufficiali. Giochi e programmi si rilevano uno dopo l'altro: la ricerca vale per entrambi.</summary>
        public static List<FoundApp> UnregisteredApps(bool fresh)
        {
            lock (FoundSync)
            {
                if (fresh || foundApps == null || (DateTime.UtcNow - foundAt).TotalSeconds > 60)
                {
                    var list = new List<FoundApp>();
                    try { list.AddRange(ScanDisks(ScanRoots())); }
                    catch (Exception ex) { Log.Error("Ricerca sui dischi", ex); }
                    try { list.AddRange(FromSteamShortcuts()); }
                    catch (Exception ex) { Log.Error("Giochi aggiunti a Steam", ex); }
                    try { list.AddRange(FromWindowsTraces()); }
                    catch (Exception ex) { Log.Error("Tracce di Windows", ex); }
                    foundApps = list;
                    foundAt = DateTime.UtcNow;
                }
                return foundApps.Where(f => f.IsSingleFile ? File.Exists(f.Exe) : Directory.Exists(f.Dir)).ToList();
            }
        }

        private static List<Tuple<string, bool>> ScanRoots()
        {
            var roots = new List<Tuple<string, bool>>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if ((drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable) && drive.IsReady) roots.Add(Tuple.Create(drive.RootDirectory.FullName, false));
                }
                catch { }
            }
            foreach (var d in new[] { KnownDirs.Desktop, KnownDirs.Downloads, KnownDirs.Documents, KnownDirs.Videos, KnownDirs.PublicDesktop })
                if (Directory.Exists(d)) roots.Add(Tuple.Create(d, false));
            // in Programmi ci sono soprattutto programmi registrati: lì si cercano solo i giochi
            foreach (var d in new[] { KnownDirs.ProgramFiles, KnownDirs.ProgramFilesX86 })
                if (Directory.Exists(d)) roots.Add(Tuple.Create(d, true));
            return roots;
        }

        /// <summary>
        /// Esplora i dischi dall'alto verso il basso, con limiti di cartelle e di tempo, e si ferma alla prima cartella che è un'applicazione.
        /// I progetti di sviluppo non vengono mai presi per intero: si propone al massimo il loro eseguibile.
        /// </summary>
        internal static List<FoundApp> ScanDisks(IEnumerable<Tuple<string, bool>> roots)
        {
            var found = new List<FoundApp>();
            var entry = System.Reflection.Assembly.GetEntryAssembly();
            string own = entry == null ? null : Path.GetDirectoryName(entry.Location);
            var queue = new Queue<Tuple<string, int, bool>>();
            foreach (var r in roots) queue.Enqueue(Tuple.Create(r.Item1, 0, r.Item2));
            int visited = 0;
            var clock = Stopwatch.StartNew();
            while (queue.Count > 0)
            {
                if (visited++ >= ScanMaxDirs || clock.ElapsedMilliseconds > ScanMaxMs)
                {
                    Log.Info("Ricerca sui dischi interrotta dopo {0} cartelle: {1} non esplorate", visited, queue.Count);
                    break;
                }
                var cur = queue.Dequeue();
                string dir = cur.Item1;
                string[] dirs = PathUtil.Dirs(dir);
                if (cur.Item2 > 0)
                {
                    if (own != null && PathUtil.IsUnder(own, dir, true)) continue;
                    string name = Path.GetFileName(dir);
                    string named = ExeNamedLikeFolder(dir);
                    bool project = dirs.Any(d => { string n = Path.GetFileName(d); return ProjectDirs.Contains(n); })
                                   || PathUtil.Files(dir).Any(f => ProjectFile.IsMatch(Path.GetFileName(f)));
                    if (project)
                    {
                        if (named != null && !cur.Item3) found.Add(new FoundApp { Exe = named, Dir = dir, IsSingleFile = true, Source = L.F("Cartella su disco ({0})", dir) });
                        continue;
                    }
                    string where = L.F("Cartella su disco ({0})", Path.GetDirectoryName(dir));
                    if (GameMarkers.IsStrong(dir) && FindMainExe(dir, name) != null)
                    {
                        found.Add(new FoundApp { Dir = dir, Exe = named, IsGame = true, Source = where });
                        continue;
                    }
                    if (named != null)
                    {
                        bool game = NameUtil.GameReleaseTags.IsMatch(name) || CoverService.IsKnownGame(AppKey(name));
                        if (game || !cur.Item3) found.Add(new FoundApp { Dir = dir, Exe = named, IsGame = game, Source = where });
                        if (game || !cur.Item3) continue;
                    }
                }
                if (cur.Item2 >= (cur.Item3 ? 3 : ScanMaxDepth)) continue;
                foreach (var d in dirs)
                {
                    string n = Path.GetFileName(d);
                    if (ScanSkip.IsMatch(n) || PathUtil.IsReparsePoint(d)) continue;
                    if (cur.Item3 && Traces.SkipTopLevel.Contains(NameUtil.Norm(n))) continue;
                    try { if ((File.GetAttributes(d) & FileAttributes.Hidden) != 0) continue; }
                    catch { continue; }
                    queue.Enqueue(Tuple.Create(d, cur.Item2 + 1, cur.Item3));
                }
            }
            return found;
        }

        private static readonly HashSet<string> ProjectDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", ".svn", ".hg", "node_modules", ".venv", "venv" };

        /// <summary>La cartella sta dentro un progetto di sviluppo: è un suo strumento o una sua compilazione, non un'applicazione a sé.</summary>
        internal static bool IsInsideProject(string dir)
        {
            string cur = Path.GetDirectoryName(dir);
            for (int i = 0; i < 4 && cur != null && cur.Length > 3 && IsSpecificDir(cur); i++, cur = Path.GetDirectoryName(cur))
            {
                if (ProjectDirs.Any(m => Directory.Exists(Path.Combine(cur, m)))) return true;
                if (PathUtil.Files(cur).Any(f => ProjectFile.IsMatch(Path.GetFileName(f)))) return true;
            }
            return false;
        }

        /// <summary>Giochi aggiunti a Steam a mano (spesso è così che si avviano le copie piratate): stanno in userdata\…\config\shortcuts.vdf.</summary>
        private static List<FoundApp> FromSteamShortcuts()
        {
            var list = new List<FoundApp>();
            foreach (var ud in SteamInfo.UserDataDirs)
            {
                string file = Path.Combine(ud, "config", "shortcuts.vdf");
                if (!File.Exists(file)) continue;
                foreach (var s in ParseSteamShortcuts(File.ReadAllBytes(file)))
                {
                    string exe = PathUtil.Normalize(s.Value);
                    if (exe == null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe) || PathUtil.IsUnder(exe, KnownDirs.Windows)) continue;
                    string root = GuessRoot(exe);
                    if (root == null || !IsSpecificDir(root)) continue;
                    list.Add(new FoundApp { Dir = root, Exe = exe, Name = s.Key, IsGame = true, FromShortcut = true, Source = L.T("Aggiunto a Steam come gioco esterno") });
                }
            }
            return list;
        }

        /// <summary>Le coppie nome ed eseguibile di un file shortcuts.vdf (formato binario di Steam).</summary>
        internal static List<KeyValuePair<string, string>> ParseSteamShortcuts(byte[] data)
        {
            var list = new List<KeyValuePair<string, string>>();
            string text = System.Text.Encoding.UTF8.GetString(data);
            string name = null;
            foreach (Match m in Regex.Matches(text, "\u0001(appname|exe)\u0000\"?([^\u0000\"]*)\"?\u0000", RegexOptions.IgnoreCase))
            {
                if (m.Groups[1].Value.Equals("appname", StringComparison.OrdinalIgnoreCase)) name = m.Groups[2].Value;
                else if (name != null) { list.Add(new KeyValuePair<string, string>(name, m.Groups[2].Value)); name = null; }
            }
            return list;
        }

        /// <summary>
        /// Eseguibili che esistono e che Windows ricorda di aver avviato: trovano le applicazioni ovunque siano, anche oltre i limiti della ricerca sui dischi.
        /// Un gioco dev'essere riconoscibile come tale; un programma deve avere il nome della sua cartella ed essere fuori dalle cartelle di installazione.
        /// </summary>
        private static List<FoundApp> FromWindowsTraces()
        {
            var list = new List<FoundApp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entry = System.Reflection.Assembly.GetEntryAssembly();
            string own = entry == null ? null : Path.GetDirectoryName(entry.Location);
            foreach (var t in Traces.ExePathValues().Concat(Traces.GameConfigStore()))
            {
                string exe = PathUtil.Normalize(t.ExePath);
                if (exe == null || !seen.Add(exe) || !File.Exists(exe) || IsAuxiliaryExe(exe)) continue;
                string root = GuessRoot(exe);
                if (root == null || !seen.Add("d:" + root) || !IsSpecificDir(root) || (own != null && PathUtil.IsUnder(own, root, true))) continue;
                if (new[] { KnownDirs.Windows, KnownDirs.Temp, KnownDirs.LocalAppData, KnownDirs.RoamingAppData, KnownDirs.ProgramData }.Any(s => PathUtil.IsUnder(root, s, true))) continue;
                if (LooksLikeUserFolder(root) || IsInsideProject(root)) continue;
                bool gameBar =t.KeyPath.IndexOf("GameConfigStore", StringComparison.OrdinalIgnoreCase) >= 0;
                if (GameMarkers.IsStrong(root) || (gameBar && GameMarkers.Detect(root) != null))
                    list.Add(new FoundApp { Dir = root, Exe = exe, IsGame = true, Source = L.T("Avviato su questo PC (tracce di Windows)") });
                else if (ExeNamedLikeFolder(root) != null && !PathUtil.IsUnder(root, KnownDirs.ProgramFiles, true) && !PathUtil.IsUnder(root, KnownDirs.ProgramFilesX86, true))
                    list.Add(new FoundApp { Dir = root, Exe = ExeNamedLikeFolder(root), Source = L.T("Avviato su questo PC (tracce di Windows)") });
            }
            return list;
        }

        /// <summary>Radici delle cartelle "portable" (una sottocartella per gioco o programma): trovate da sole più quelle scelte dall'utente.</summary>
        public static List<string> PortableRoots(AppKind kind, AppSettings settings)
        {
            var pattern = kind == AppKind.Game ? GameRootName : ProgramRootName;
            var roots = new List<string>();
            Action<string> check = parent =>
            {
                foreach (var p in PathUtil.Dirs(parent))
                    if (pattern.IsMatch(Path.GetFileName(p)) && !PathUtil.IsReparsePoint(p) && !roots.Any(r => PathUtil.Same(r, p))) roots.Add(PathUtil.RealCase(p));
            };
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady) check(drive.RootDirectory.FullName);
                }
                catch { }
            }
            check(KnownDirs.Desktop);
            foreach (var p in settings.PortableFolders(kind))
            {
                string n = PathUtil.Normalize(p);
                if (n != null && Directory.Exists(n) && !roots.Any(r => PathUtil.Same(r, n))) roots.Add(n);
            }
            return roots;
        }
    }

    /// <summary>Rilevamento dei giochi.</summary>
    internal static class GameDetector
    {
        private static readonly Regex LauncherNames = new Regex(
            @"^(steam|epic games launcher|gog galaxy.*|ubisoft connect|uplay|ea app|ea desktop|origin|battle\.net|rockstar games (launcher|social club)|" +
            @"amazon games|xbox.*|epic online services|discord|overwolf|nvidia.*|geforce.*|playnite|heroic.*|itch|legendary|galaxy|" +
            @"directx.*|microsoft .*|vulkan.*|easyanticheat.*|easy anti-cheat.*|battleye.*|wallpaper engine|rpcs3|cheat engine.*)$",
            RegexOptions.IgnoreCase);

        public static List<AppEntry> DetectAll(AppSettings settings, Action<string> progress)
        {
            var games = new List<AppEntry>();
            Run(progress, "Steam", () => games.AddRange(SteamInfo.DetectApps(AppKind.Game, settings.ShowSteamTools)));
            Run(progress, "Epic Games", () => AddUnique(games, EpicInfo.DetectGames()));
            Run(progress, "Xbox", () => AddUnique(games, XboxInfo.DetectGames()));
            Run(progress, L.T("programmi installati"), () => AddRegistryGames(games));
            Run(progress, L.T("cartelle dei giochi"), () => AddLibraryFolderGames(games, settings));
            Run(progress, L.T("collegamenti"), () => AddShortcutGames(games));
            Run(progress, L.T("copie portable sui dischi"), () => AddUnregisteredGames(games));
            Run(progress, L.T("giochi aggiunti a mano"), () => AddManualGames(games, settings));
            Run(progress, L.T("collegamenti dei launcher"), () => MarkLauncherShortcuts(games));
            foreach (var g in games)
            {
                g.FoundBy.Add(g.Via);
                try { Complete(g); }
                catch (Exception ex) { Log.Error("Completamento " + g.Name, ex); }
            }
            return games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static void Run(Action<string> progress, string what, Action action)
        {
            if (progress != null) progress(L.F("Cerco giochi: {0}…", what));
            try { action(); }
            catch (Exception ex) { Log.Error("Rilevamento " + what, ex); }
        }

        private static void AddUnique(List<AppEntry> games, IEnumerable<AppEntry> found)
        {
            foreach (var g in found)
                if (!InstalledApps.IsCovered(games, g.InstallDir)) games.Add(g);
        }

        // ------------------------------------------------------------------ Registro (voci di disinstallazione)

        private static LauncherKind Classify(UninstallEntry e)
        {
            string all = ((e.UninstallString ?? "") + "|" + (e.QuietUninstallString ?? "") + "|" + (e.DisplayIcon ?? "")).ToLowerInvariant();
            string pub = (e.Publisher ?? "").ToLowerInvariant();
            if (e.KeyName.StartsWith("Uplay Install", StringComparison.OrdinalIgnoreCase) || all.Contains("uplay://uninstall") || all.Contains("upc.exe"))
                return LauncherKind.Ubisoft;
            if (all.Contains("blizzard uninstaller") || all.Contains("battle.net\\agent")) return LauncherKind.BattleNet;
            if (all.Contains("eainstaller") || all.Contains("ea desktop") || all.Contains("\\origin\\") || (pub.Contains("electronic arts") && all.Contains("cleanup.exe")))
                return LauncherKind.EA;
            if (all.Contains("rockstar games\\launcher")) return LauncherKind.Rockstar;
            if (all.Contains("amazon game remover")) return LauncherKind.Amazon;
            if (pub.Contains("gog.com") || Regex.IsMatch(e.KeyName, @"^\d{8,}_is1$")) return LauncherKind.GOG;
            return LauncherKind.Other;
        }

        private static void AddRegistryGames(List<AppEntry> games)
        {
            foreach (var e in InstalledApps.ReadUninstallEntries())
            {
                if (string.IsNullOrWhiteSpace(e.DisplayName) || e.IsSystemOrUpdate) continue;
                if (InstalledApps.SteamAppKey.IsMatch(e.KeyName)) continue;
                string cleanName = NameUtil.CleanDisplayName(e.DisplayName);
                if (LauncherNames.IsMatch(cleanName.Trim())) continue;
                string dir = InstalledApps.EntryInstallDir(e);
                if (dir == null) continue;

                // la cartella appartiene già a un gioco rilevato: collega solo la voce di disinstallazione
                var existing = games.FirstOrDefault(g => PathUtil.Same(g.InstallDir, dir));
                if (existing != null)
                {
                    if (existing.UninstallKey == null) existing.UninstallKey = e.KeyPath;
                    existing.FoundBy.Add(Classify(e) == LauncherKind.Other ? FoundVia.Installed : FoundVia.Launcher);
                    continue;
                }
                if (InstalledApps.IsCovered(games, dir)) continue;

                var kind = Classify(e);
                string engine = kind == LauncherKind.Other ? GameMarkers.Detect(dir) : "launcher";
                if (engine == null) continue;

                var game = new AppEntry
                {
                    Id = "reg:" + e.KeyPath.ToLowerInvariant(),
                    Kind = AppKind.Game,
                    Name = cleanName,
                    Launcher = kind,
                    InstallDir = dir,
                    Publisher = e.Publisher,
                    Version = e.DisplayVersion,
                    InstallDate = e.InstallDate,
                    UninstallKey = e.KeyPath,
                    UninstallString = e.UninstallString,
                    QuietUninstallString = e.QuietUninstallString,
                    IconPath = e.DisplayIcon,
                    Source = kind == LauncherKind.Other ? L.T("Programmi installati") : Launchers.Label(kind),
                    Via = kind == LauncherKind.Other ? FoundVia.Installed : FoundVia.Launcher,
                };
                if (e.EstimatedSizeKB > 0) game.SizeBytes = e.EstimatedSizeKB * 1024;
                string icon = PathUtil.Normalize(PathUtil.ExeFromCommand(e.DisplayIcon == null ? null : e.DisplayIcon.Split(',')[0]));
                if (icon != null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(icon)
                    && !Path.GetFileName(icon).StartsWith("unins", StringComparison.OrdinalIgnoreCase))
                    game.MainExe = icon;
                games.Add(game);
            }
        }

        // ------------------------------------------------------------------ Cartelle dei giochi (anche portable)

        public static List<string> LibraryRoots(AppSettings settings)
        {
            return InstalledApps.PortableRoots(AppKind.Game, settings);
        }

        private static void AddLibraryFolderGames(List<AppEntry> games, AppSettings settings)
        {
            foreach (var root in LibraryRoots(settings))
            {
                foreach (var dir in PathUtil.Dirs(root))
                {
                    if (InstalledApps.IsCovered(games, dir))
                    {
                        // gioco già trovato in altro modo (es. un repack installato in C:\Games): lo trova anche la cartella
                        InstalledApps.MarkFound(games, dir, FoundVia.Folder);
                        continue;
                    }
                    // in una cartella dedicata ai giochi basta un eseguibile con il nome della cartella, anche senza un motore riconoscibile
                    string namedExe = InstalledApps.ExeNamedLikeFolder(dir);
                    if (GameMarkers.Detect(dir) == null && namedExe == null) continue;
                    games.Add(new AppEntry
                    {
                        Id = "dir:" + dir.ToLowerInvariant(),
                        Kind = AppKind.Game,
                        Name = NameUtil.CleanFolderTitle(Path.GetFileName(dir)),
                        Launcher = LauncherKind.Other,
                        InstallDir = dir,
                        MainExe = namedExe,
                        Source = L.F("Cartella dei giochi portable ({0})", root),
                        Via = FoundVia.Folder,
                    });
                }
            }
        }

        /// <summary>Copie portable, piratate o spostate a mano: non le ha registrate nessuno, ma ci sono.</summary>
        private static void AddUnregisteredGames(List<AppEntry> games)
        {
            var registered = new HashSet<string>(InstalledApps.ReadUninstallEntries().Select(InstalledApps.EntryInstallDir).Where(d => d != null), StringComparer.OrdinalIgnoreCase);
            foreach (var f in InstalledApps.UnregisteredApps(true).Where(x => x.IsGame))
            {
                if (InstalledApps.IsCovered(games, f.Dir)) { InstalledApps.MarkFound(games, f.Dir, f.FromShortcut ? FoundVia.Shortcut : FoundVia.Folder); continue; }
                string title = f.Name ?? NameUtil.CleanFolderTitle(Path.GetFileName(f.Dir));
                if (registered.Contains(f.Dir) || LauncherNames.IsMatch(title) || LauncherNames.IsMatch(Path.GetFileName(f.Dir)) || !Safety.IsDeletableDirectory(f.Dir)) continue;
                // aggiunto a Steam ma è un programma qualunque (es. un browser): serve una prova, se è in una cartella di installazione
                if (f.FromShortcut && GameMarkers.Detect(f.Dir) == null && (PathUtil.IsUnder(f.Dir, KnownDirs.ProgramFiles) || PathUtil.IsUnder(f.Dir, KnownDirs.ProgramFilesX86) || PathUtil.IsUnder(f.Dir, KnownDirs.LocalAppData))) continue;
                games.Add(new AppEntry
                {
                    Id = "dir:" + f.Dir.ToLowerInvariant(),
                    Kind = AppKind.Game,
                    Name = title,
                    Launcher = LauncherKind.Other,
                    InstallDir = f.Dir,
                    MainExe = f.Exe,
                    Source = f.Source,
                    Via = f.FromShortcut ? FoundVia.Shortcut : FoundVia.Folder,
                });
            }
        }

        // ------------------------------------------------------------------ Collegamenti sul desktop e nel menu Start

        private static void AddShortcutGames(List<AppEntry> games)
        {
            foreach (var lnk in InstalledApps.ShortcutFiles())
            {
                if (!lnk.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
                string target, args;
                if (!Native.ReadShortcut(lnk, out target, out args)) continue;
                target = PathUtil.Normalize(target);
                if (target == null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
                if (PathUtil.IsUnder(target, KnownDirs.Windows)) continue;
                string root = InstalledApps.GuessRoot(target);
                if (root == null || root.Length <= 3) continue;
                var known = InstalledApps.CoveringEntry(games, root);
                if (known != null)
                {
                    // collegamento a un gioco già trovato (es. di Steam): non ne aggiunge un altro, ma conta per i collegamenti
                    known.FoundBy.Add(FoundVia.Shortcut);
                    continue;
                }
                if (!Safety.IsDeletableDirectory(root)) continue;
                if (InstalledApps.IsCovered(games, root)) continue;
                if (LauncherNames.IsMatch(Path.GetFileNameWithoutExtension(lnk))) continue;
                if (GameMarkers.Detect(root) == null) continue;
                games.Add(new AppEntry
                {
                    Id = "dir:" + root.ToLowerInvariant(),
                    Kind = AppKind.Game,
                    Name = NameUtil.CleanDisplayName(Path.GetFileNameWithoutExtension(lnk)),
                    Launcher = LauncherKind.Other,
                    InstallDir = root,
                    MainExe = target,
                    Source = InstalledApps.ShortcutSource(lnk),
                    Via = FoundVia.Shortcut,
                });
            }
        }

        // ------------------------------------------------------------------ Giochi aggiunti a mano

        private static void AddManualGames(List<AppEntry> games, AppSettings settings)
        {
            foreach (var m in settings.ManualGames)
            {
                string dir = PathUtil.Normalize(m.Path);
                if (dir == null || !Directory.Exists(dir)) continue;
                var existing = games.FirstOrDefault(g => PathUtil.Same(g.InstallDir, dir));
                if (existing != null) { existing.FoundBy.Add(FoundVia.Manual); continue; }
                games.Add(new AppEntry
                {
                    Id = "manual:" + dir.ToLowerInvariant(),
                    Kind = AppKind.Game,
                    Name = string.IsNullOrWhiteSpace(m.Name) ? Path.GetFileName(dir) : m.Name,
                    Launcher = LauncherKind.Other,
                    InstallDir = dir,
                    IsManual = true,
                    Source = L.T("Aggiunto manualmente"),
                    Via = FoundVia.Manual,
                });
            }
        }

        /// <summary>
        /// Collegamenti .url dei launcher (steam://rungameid/…, com.epicgames.launcher://…) sul desktop e nel menu Start:
        /// non aggiungono giochi (sono già nella libreria del launcher), ma contano tra quelli trovati dai collegamenti.
        /// </summary>
        private static void MarkLauncherShortcuts(List<AppEntry> games)
        {
            foreach (var s in Traces.Shortcuts())
            {
                if (string.IsNullOrEmpty(s.Url)) continue;
                string sid = Traces.SteamIdFromUrl(s.Url);
                bool epic = s.Url.StartsWith("com.epicgames.launcher://", StringComparison.OrdinalIgnoreCase);
                foreach (var g in games)
                {
                    if ((sid != null && g.SteamAppId == sid)
                        || (epic && g.EpicAppName != null && s.Url.IndexOf(g.EpicAppName, StringComparison.OrdinalIgnoreCase) >= 0))
                        g.FoundBy.Add(FoundVia.Shortcut);
                }
            }
        }

        // ------------------------------------------------------------------ Completamento (eseguibile e copertina)

        private static void Complete(AppEntry g)
        {
            if (g.MainExe == null || !File.Exists(g.MainExe)) g.MainExe = InstalledApps.FindMainExe(g.InstallDir, g.Name);
            if (g.CoverImage == null && g.Launcher != LauncherKind.Steam)
            {
                foreach (var id in SteamInfo.AppIdsInFolder(g.InstallDir))
                {
                    g.CoverImage = SteamInfo.FindImage(id, ArtKind.Portrait);
                    g.HeaderImage = SteamInfo.FindImage(id, ArtKind.Header);
                    g.HeroImage = SteamInfo.FindImage(id, ArtKind.Hero);
                    g.LogoImage = SteamInfo.FindImage(id, ArtKind.Logo);
                    if (g.CoverImage != null || g.HeaderImage != null) break;
                }
            }
            // copertina inclusa nella cartella del gioco (molti repack ne contengono una)
            if (g.CoverImage == null) g.CoverImage = CoverService.FindLocalCover(g.InstallDir);
        }
    }
}
