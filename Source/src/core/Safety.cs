using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AppManager
{
    /// <summary>
    /// Ultima linea di difesa: qualunque cosa venga proposta dagli scanner, qui si decide
    /// se è davvero sicuro eliminarla. Nel dubbio la risposta è no.
    /// </summary>
    internal static class Safety
    {
        private static readonly object Sync = new object();
        private static List<string> protectedDirs;
        private static List<string> deniedTrees;
        private static List<string> ownTrees;
        private static readonly Dictionary<string, List<string>> installedDirs = new Dictionary<string, List<string>>();

        /// <summary>
        /// Registra le cartelle dei giochi o dei programmi installati (<paramref name="group"/> = "games" o "programs"):
        /// non si elimina mai una cartella che ne contiene una.
        /// </summary>
        public static void SetInstalledDirs(string group, IEnumerable<string> dirs)
        {
            lock (Sync)
            {
                installedDirs[group] = dirs.Where(d => !string.IsNullOrEmpty(d)).ToList();
                protectedDirs = null;
            }
        }

        private static void Build()
        {
            var p = new List<string>();
            Action<string> add = s => { string n = PathUtil.Normalize(s); if (n != null) p.Add(n); };

            foreach (var drive in DriveInfo.GetDrives()) add(drive.RootDirectory.FullName);
            string users = Path.GetDirectoryName(KnownDirs.UserProfile);
            foreach (var s in new[]
            {
                KnownDirs.Windows, KnownDirs.ProgramFiles, KnownDirs.ProgramFilesX86, KnownDirs.ProgramData, users,
                KnownDirs.UserProfile, Path.Combine(users ?? "", "Public"), KnownDirs.Documents, KnownDirs.Desktop, KnownDirs.PublicDesktop,
                KnownDirs.Pictures, KnownDirs.Videos, KnownDirs.Captures, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Path.Combine(KnownDirs.UserProfile, "Downloads"), KnownDirs.Downloads, Path.Combine(KnownDirs.UserProfile, "OneDrive"),
                KnownDirs.SavedGames, KnownDirs.MyGames, Path.Combine(KnownDirs.MyGames, "UnrealEngine3"),
                Path.Combine(KnownDirs.UserProfile, "AppData"), KnownDirs.RoamingAppData, KnownDirs.LocalAppData, KnownDirs.LocalLow,
                KnownDirs.Temp, KnownDirs.StartMenu, KnownDirs.CommonStartMenu, Path.GetDirectoryName(KnownDirs.StartMenu),
                Path.GetDirectoryName(KnownDirs.CommonStartMenu), Path.Combine(KnownDirs.StartMenu, "Startup"), Path.Combine(KnownDirs.CommonStartMenu, "Startup"),
                KnownDirs.PublicDocuments, KnownDirs.CrashDumps, KnownDirs.VirtualStore,
                Path.Combine(KnownDirs.VirtualStore, "Program Files"), Path.Combine(KnownDirs.VirtualStore, "Program Files (x86)"),
                Path.Combine(KnownDirs.VirtualStore, "ProgramData"), Path.Combine(KnownDirs.LocalAppData, "Packages"),
                KnownDirs.LocalPrograms, Path.Combine(KnownDirs.LocalPrograms, "Common"), Path.Combine(KnownDirs.LocalLow, "Unity"),
                Path.Combine(KnownDirs.Videos, "NVIDIA"), Path.Combine(KnownDirs.Videos, "Radeon ReLive"),
                Path.Combine(KnownDirs.Pictures, "Screenshots"), Path.Combine(KnownDirs.LocalAppData, "UnrealEngine"),
                Path.Combine(KnownDirs.LocalAppData, "CrashReportClient"), Path.Combine(KnownDirs.RoamingAppData, "Goldberg SteamEmu Saves"),
                Path.Combine(KnownDirs.PublicDocuments, "Steam"), Path.Combine(KnownDirs.RoamingAppData, "Steam"),
                Path.Combine(KnownDirs.ProgramData, "Steam"),
            }) add(s);
            // le cartelle comuni degli emulatori di Steam contengono i salvataggi di più giochi
            foreach (var s in Traces.AppIdSaveRoots().Concat(Traces.NamedEmuSaveRoots())) add(s);

            string steam = SteamInfo.SteamPath;
            if (steam != null)
            {
                add(steam);
                add(Path.Combine(steam, "userdata"));
                add(Path.Combine(steam, "steam", "games"));
                add(Path.Combine(steam, "appcache"));
                foreach (var sa in SteamInfo.SteamAppsDirs)
                {
                    add(Path.GetDirectoryName(sa));
                    add(sa);
                    foreach (var sub in new[] { "common", "shadercache", "workshop", @"workshop\content", @"workshop\downloads", @"workshop\temp", "downloading", "temp", "compatdata", "sourcemods" })
                        add(Path.Combine(sa, sub));
                }
                foreach (var ud in SteamInfo.UserDataDirs)
                {
                    add(ud);
                    foreach (var sub in new[] { "760", @"760\remote", "config", "gamerecordings", @"gamerecordings\clips", @"gamerecordings\video", @"gamerecordings\timelines" })
                        add(Path.Combine(ud, sub));
                }
            }
            foreach (var sm in new[] { KnownDirs.StartMenu, KnownDirs.CommonStartMenu })
                foreach (var name in StartMenuSystemFolders) add(Path.Combine(sm, name));
            foreach (var x in XboxInfo.GameRoots()) add(x);
            foreach (var lib in InstalledApps.PortableRoots(AppKind.Game, AppSettings.Current)) add(lib);
            foreach (var lib in InstalledApps.PortableRoots(AppKind.Program, AppSettings.Current)) add(lib);
            add(EpicInfo.ManifestsDir);
            add(Path.GetDirectoryName(EpicInfo.LauncherInstalledDat));
            protectedDirs = p.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var d = new List<string>();
            foreach (var s in new[]
            {
                KnownDirs.Windows, Path.Combine(KnownDirs.ProgramFiles, "WindowsApps"), Path.Combine(KnownDirs.ProgramFiles, "ModifiableWindowsApps"),
                Path.Combine(KnownDirs.ProgramFiles, "Common Files"), Path.Combine(KnownDirs.ProgramFilesX86, "Common Files"),
                Path.Combine(KnownDirs.ProgramFiles, "Windows Defender"), Path.Combine(KnownDirs.ProgramFiles, "Windows Defender Advanced Threat Protection"),
                Path.Combine(KnownDirs.ProgramFiles, "WindowsPowerShell"), Path.Combine(KnownDirs.ProgramFilesX86, "WindowsPowerShell"),
                Path.Combine(KnownDirs.ProgramFiles, "Windows NT"), Path.Combine(KnownDirs.ProgramFilesX86, "Windows NT"),
                Path.Combine(KnownDirs.ProgramFiles, "Internet Explorer"), Path.Combine(KnownDirs.ProgramFilesX86, "Internet Explorer"),
                Path.Combine(KnownDirs.ProgramFiles, "dotnet"), Path.Combine(KnownDirs.ProgramFilesX86, "dotnet"),
                Path.Combine(KnownDirs.ProgramFilesX86, "Microsoft"), Path.Combine(KnownDirs.ProgramFiles, "Microsoft Update Health Tools"),
                Path.Combine(KnownDirs.ProgramData, "Microsoft"), Path.Combine(KnownDirs.ProgramData, "Package Cache"),
                Path.Combine(KnownDirs.LocalAppData, "Microsoft"), Path.Combine(KnownDirs.RoamingAppData, "Microsoft"),
                Path.Combine(KnownDirs.LocalAppData, "Package Cache"), Path.Combine(KnownDirs.LocalAppData, "Packages"),
            })
            {
                string n = PathUtil.Normalize(s);
                if (n != null) d.Add(n);
            }
            // componenti di Windows installati in Programmi (Windows Media Player, Windows Photo Viewer, Windows Mail...)
            foreach (var pf in new[] { KnownDirs.ProgramFiles, KnownDirs.ProgramFilesX86 })
                foreach (var dir in PathUtil.Dirs(pf))
                    if (Path.GetFileName(dir).StartsWith("Windows", StringComparison.OrdinalIgnoreCase)) d.Add(dir);
            foreach (var drive in DriveInfo.GetDrives())
            {
                d.Add(Path.Combine(drive.RootDirectory.FullName, "System Volume Information"));
                d.Add(Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin"));
            }
            deniedTrees = d;

            // i dati di AppManager (impostazioni, registro, copertine, backup, salvataggi messi da parte)
            var own = new List<string>();
            var settings = AppSettings.Current;
            foreach (var s in AppPaths.OwnDirs().Concat(new[] { settings.BackupFolder, settings.SaveVaultFolder }))
            {
                string n = PathUtil.Normalize(s);
                if (n != null) own.Add(n);
            }
            ownTrees = own.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>True se il percorso è (o contiene) una cartella di AppManager stesso.</summary>
        public static bool IsOwnData(string path)
        {
            string n = PathUtil.Normalize(path);
            if (n == null) return false;
            if (InRecycleStaging(n)) return true;
            lock (Sync)
            {
                if (protectedDirs == null) Build();
                return ownTrees.Any(t => PathUtil.IsUnder(n, t, true) || PathUtil.IsUnder(t, n));
            }
        }

        /// <summary>Eccezioni puntuali dentro le aree negate (menu Start, report di errore di Windows, pacchetti dello Store).</summary>
        private static bool IsAllowedException(string path)
        {
            // collegamenti e sottocartelle del menu Start (le cartelle principali restano protette)
            if (PathUtil.IsUnder(path, KnownDirs.StartMenu) || PathUtil.IsUnder(path, KnownDirs.CommonStartMenu)) return true;
            foreach (var baseDir in new[] { KnownDirs.ProgramData, KnownDirs.LocalAppData })
            {
                foreach (var sub in new[] { @"Microsoft\Windows\WER\ReportArchive", @"Microsoft\Windows\WER\ReportQueue" })
                {
                    string root = Path.Combine(baseDir, sub);
                    if (PathUtil.IsUnder(path, root) && Path.GetDirectoryName(path).Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            // dump di arresto anomalo di Windows (pulizia dei log): solo file .dmp, mai le cartelle
            if (Path.GetExtension(path).Equals(".dmp", StringComparison.OrdinalIgnoreCase) && File.Exists(PathUtil.Long(path)))
            {
                string win = KnownDirs.Windows;
                if (PathUtil.Same(Path.GetDirectoryName(path), Path.Combine(win, "Minidump")) || PathUtil.Same(path, Path.Combine(win, "MEMORY.DMP"))
                    || PathUtil.IsUnder(path, Path.Combine(win, "LiveKernelReports"))) return true;
            }
            if (IsSystemCacheOrLog(path)) return true;
            string packages = Path.Combine(KnownDirs.LocalAppData, "Packages");
            return PathUtil.IsUnder(path, packages) && string.Equals(Path.GetDirectoryName(path), packages, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Cartelle di Windows il cui contenuto diretto è fatto solo di file temporanei o scaricati di nuovo da Windows se servono.</summary>
        internal static string[] SystemCacheRoots()
        {
            string win = KnownDirs.Windows;
            return new[]
            {
                Path.Combine(win, "Temp"),
                Path.Combine(win, @"SoftwareDistribution\Download"),
                Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"),
            };
        }

        /// <summary>Cartelle dei log di manutenzione, aggiornamento e installazione di Windows.</summary>
        internal static string[] SystemLogDirs()
        {
            string logs = Path.Combine(KnownDirs.Windows, "Logs");
            return new[]
            {
                Path.Combine(logs, "CBS"), Path.Combine(logs, "DISM"), Path.Combine(logs, "WindowsUpdate"), Path.Combine(logs, "waasmedic"),
                Path.Combine(logs, "NetSetup"), Path.Combine(logs, "MoSetup"), Path.Combine(KnownDirs.Windows, "Panther"),
            };
        }

        internal static readonly string[] SystemLogExtensions = { ".log", ".etl", ".cab" };
        internal static readonly string[] BrowserCacheNames = { "Cache", "Code Cache", "GPUCache" };

        /// <summary>
        /// Le sole eccezioni per la pulizia di log e cache dentro le aree di sistema: il contenuto diretto delle cartelle temporanee di
        /// Windows, i file di log (mai le cartelle) di Windows, le cache di Edge. Mai le cartelle stesse né ciò che sta accanto.
        /// </summary>
        private static bool IsSystemCacheOrLog(string path)
        {
            string parent = Path.GetDirectoryName(path);
            if (parent == null) return false;
            foreach (var root in SystemCacheRoots())
                if (PathUtil.Same(parent, root)) return true;
            if (SystemLogExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) && File.Exists(PathUtil.Long(path)))
                foreach (var dir in SystemLogDirs())
                    if (PathUtil.Same(parent, dir)) return true;
            // Edge sta in AppData\Local\Microsoft: solo User Data\<profilo>\Cache, Code Cache e GPUCache (e ciò che contengono)
            string edge = Path.Combine(KnownDirs.LocalAppData, @"Microsoft\Edge\User Data");
            if (PathUtil.IsUnder(path, edge))
            {
                var parts = path.Substring(edge.Length + 1).Split('\\');
                if (parts.Length >= 2 && BrowserCacheNames.Contains(parts[1], StringComparer.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static bool IsDeletableDirectory(string path)
        {
            return IsDeletablePath(path);
        }

        /// <summary>Cartelle di sistema nella radice delle unità (oltre a quelle nascoste di sistema e a quelle con "$").</summary>
        private static readonly HashSet<string> RootSystemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Recovery", "Boot", "EFI", "Config.Msi", "PerfLogs", "Documents and Settings", "Windows.old", "OneDriveTemp", "inetpub",
            "System Volume Information", "Users", "Program Files", "Program Files (x86)", "ProgramData", "Windows",
        };

        /// <summary>Cartelle del menu Start create da Windows.</summary>
        private static readonly string[] StartMenuSystemFolders =
        {
            "Accessibility", "Accessories", "Administrative Tools", "Maintenance", "Startup", "System Tools", "Windows PowerShell",
            "Windows Tools", "Windows Accessories", "Windows Administrative Tools", "Windows Ease of Access", "Windows System",
        };

        /// <summary>True se il file/cartella può essere eliminato senza rischi per il sistema, per i giochi o per i programmi.</summary>
        public static bool IsDeletablePath(string path)
        {
            string n = PathUtil.Normalize(path);
            if (n == null || !IsDeletableLexical(n)) return false;
            // un collegamento (junction, link simbolico) in una cartella superiore può portare altrove, anche in un'area protetta:
            // vale anche la posizione reale
            string parent = Path.GetDirectoryName(n);
            string realParent = parent == null ? null : Native.FinalPath(parent);
            if (realParent != null && !PathUtil.Same(realParent, parent))
            {
                if (realParent.StartsWith(@"\\"))
                {
                    // unità di rete: normale per le lettere di unità mappate, sospetto per un'unità locale
                    try { if (new DriveInfo(n.Substring(0, 1)).DriveType != DriveType.Network) return false; }
                    catch { return false; }
                }
                else
                {
                    string real = PathUtil.Normalize(Path.Combine(realParent, Path.GetFileName(n)));
                    if (real == null || !IsDeletableLexical(real)) return false;
                }
            }
            return true;
        }

        private static bool IsDeletableLexical(string n)
        {
            if (n.Length <= 3 || !Path.IsPathRooted(n) || n.StartsWith(@"\\") || n.Contains(@"\\")) return false;
            string parent = Path.GetDirectoryName(n);
            string leaf = Path.GetFileName(n);
            if (string.IsNullOrWhiteSpace(leaf) || leaf.Trim('.').Length == 0) return false;
            // radice dell'unità: cartelle di sistema, nascoste di sistema o con "$" (es. $Recycle.Bin, $WinREAgent)
            if (parent != null && parent.Length <= 3)
            {
                if (leaf.StartsWith("$") || RootSystemNames.Contains(leaf) || IsHiddenSystem(n)) return false;
            }
            // cartelle «AppManager - …» nella radice delle unità: file raccolti per il Cestino (se il Cestino le ha rifiutate restano lì)
            if (InRecycleStaging(n)) return false;
            // direttamente nel profilo dell'utente ci sono solo cose sue (progetti, cartelle sincronizzate come Dropbox, NTUSER.DAT...):
            // si accettano solo le cartelle "nascoste" dei programmi, alla Unix (".vscode", ".gradle"...)
            if (PathUtil.Same(parent, KnownDirs.UserProfile) && !leaf.StartsWith(".")) return false;
            lock (Sync)
            {
                if (protectedDirs == null) Build();
                foreach (var p in protectedDirs)
                    if (PathUtil.Same(n, p) || PathUtil.IsUnder(p, n)) return false;
                foreach (var group in installedDirs.Values)
                    foreach (var g in group)
                        if (PathUtil.IsUnder(g, n)) return false;
                foreach (var t in deniedTrees)
                    if (PathUtil.IsUnder(n, t, true) && !IsAllowedException(n)) return false;
                foreach (var t in ownTrees)
                    if (PathUtil.IsUnder(n, t, true) || PathUtil.IsUnder(t, n)) return false;
            }
            return true;
        }

        /// <summary>
        /// True se il percorso è (o è dentro) una cartella di raccolta per il Cestino: «X:\AppManager - …» nella radice di un'unità
        /// o, se la radice non era scrivibile, nella cartella dell'utente.
        /// </summary>
        private static bool InRecycleStaging(string n)
        {
            return FirstSegmentIsStaging(n, Path.GetPathRoot(n))
                || (PathUtil.IsUnder(n, KnownDirs.UserProfile) && FirstSegmentIsStaging(n, KnownDirs.UserProfile.TrimEnd('\\') + "\\"));
        }

        private static bool FirstSegmentIsStaging(string n, string parent)
        {
            if (string.IsNullOrEmpty(parent) || n.Length <= parent.Length || !n.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = n.Substring(parent.Length);
            int slash = rest.IndexOf('\\');
            string first = slash < 0 ? rest : rest.Substring(0, slash);
            return first.StartsWith(RecycleStaging.Prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHiddenSystem(string path)
        {
            try
            {
                var a = File.GetAttributes(path);
                return (a & FileAttributes.Hidden) != 0 && (a & FileAttributes.System) != 0;
            }
            catch { return false; }
        }

        public static void Invalidate()
        {
            lock (Sync) protectedDirs = null;
        }

        /// <summary>True se il percorso appartiene a Windows (o a una sua area riservata).</summary>
        public static bool IsSystemPath(string path)
        {
            string n = PathUtil.Normalize(path);
            if (n == null) return false;
            lock (Sync)
            {
                if (protectedDirs == null) Build();
                return deniedTrees.Any(t => PathUtil.IsUnder(n, t, true));
            }
        }

        // ------------------------------------------------------------------ Registro

        private static readonly string[] DeniedKeyTrees =
        {
            @"HKCU\Software\Microsoft", @"HKCU\Software\Classes", @"HKCU\Software\Policies", @"HKCU\System", @"HKCU\Control Panel",
            @"HKCU\Environment", @"HKLM\SOFTWARE\Microsoft", @"HKLM\SOFTWARE\WOW6432Node\Microsoft", @"HKLM\SOFTWARE\Classes",
            @"HKLM\SOFTWARE\WOW6432Node\Classes", @"HKLM\SOFTWARE\Policies", @"HKLM\SOFTWARE\WOW6432Node\Policies", @"HKLM\SYSTEM",
            @"HKLM\HARDWARE", @"HKLM\SAM", @"HKLM\SECURITY", @"HKLM\BCD00000000", @"HKLM\SOFTWARE\Clients", @"HKLM\SOFTWARE\WOW6432Node\Clients",
            @"HKCU\Software\Clients", @"HKLM\SOFTWARE\RegisteredApplications", @"HKCU\Software\RegisteredApplications",
        };

        private static readonly HashSet<string> ProtectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"HKCU\Software", @"HKLM\SOFTWARE", @"HKCU\Software\WOW6432Node", @"HKLM\SOFTWARE\WOW6432Node",
            @"HKCU\Software\Valve", @"HKCU\Software\Valve\Steam", @"HKCU\Software\Valve\Steam\Apps",
            @"HKLM\SOFTWARE\Valve", @"HKLM\SOFTWARE\WOW6432Node\Valve", @"HKLM\SOFTWARE\WOW6432Node\Valve\Steam",
            @"HKCU\Software\Epic Games", @"HKLM\SOFTWARE\Epic Games", @"HKLM\SOFTWARE\WOW6432Node\Epic Games",
            @"HKCU\Software\Ubisoft", @"HKLM\SOFTWARE\WOW6432Node\Ubisoft", @"HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher",
            @"HKLM\SOFTWARE\WOW6432Node\GOG.com", @"HKLM\SOFTWARE\WOW6432Node\GOG.com\Games", @"HKCU\Software\GOG.com",
            @"HKCU\Software\Electronic Arts", @"HKLM\SOFTWARE\Electronic Arts", @"HKLM\SOFTWARE\WOW6432Node\Electronic Arts",
            @"HKCU\Software\Blizzard Entertainment", @"HKLM\SOFTWARE\WOW6432Node\Blizzard Entertainment",
            @"HKCU\Software\Rockstar Games", @"HKLM\SOFTWARE\WOW6432Node\Rockstar Games", @"HKCU\Software\Unity Technologies",
            @"HKCU\Software\Unity", @"HKCU\Software\Google", @"HKCU\Software\Mozilla", @"HKCU\Software\NVIDIA Corporation",
            @"HKLM\SOFTWARE\NVIDIA Corporation", @"HKCU\Software\Intel", @"HKLM\SOFTWARE\Intel", @"HKCU\Software\AMD", @"HKLM\SOFTWARE\AMD",
            @"HKCU\Software\JavaSoft", @"HKLM\SOFTWARE\JavaSoft", @"HKCU\Software\Python", @"HKLM\SOFTWARE\Python",
            @"HKCU\Software\Classes", @"HKCU\Software\RegisteredApplications", @"HKLM\SOFTWARE\RegisteredApplications",
            @"HKLM\SOFTWARE\Google", @"HKLM\SOFTWARE\WOW6432Node\Google", @"HKLM\SOFTWARE\Mozilla", @"HKLM\SOFTWARE\WOW6432Node\Mozilla",
            @"HKCU\Software\Adobe", @"HKLM\SOFTWARE\Adobe", @"HKLM\SOFTWARE\WOW6432Node\Adobe", @"HKCU\Software\Apple Inc.",
            @"HKLM\SOFTWARE\Apple Inc.", @"HKCU\Software\Oracle", @"HKLM\SOFTWARE\Oracle", @"HKLM\SOFTWARE\WOW6432Node\Oracle",
        };

        private static readonly Regex[] AllowedKeyPatterns =
        {
            new Regex(@"^HK(CU|LM)\\SOFTWARE\\(WOW6432Node\\)?Microsoft\\Windows\\CurrentVersion\\Uninstall\\[^\\]+$", RegexOptions.IgnoreCase),
            new Regex(@"^HK(CU|LM)\\SOFTWARE\\(WOW6432Node\\)?Microsoft\\Windows\\CurrentVersion\\App Paths\\[^\\]+\.exe$", RegexOptions.IgnoreCase),
            new Regex(@"^HKCU\\System\\GameConfigStore\\Children\\[^\\]+$", RegexOptions.IgnoreCase),
        };

        // chiavi consentite solo dopo una verifica del contenuto (non devono riguardare componenti di Windows)
        private static readonly Regex ApplicationKey = new Regex(@"^HK(CU|LM)\\SOFTWARE\\(WOW6432Node\\)?Classes\\Applications\\[^\\]+\.exe$", RegexOptions.IgnoreCase);
        private static readonly Regex NotificationKey = new Regex(@"^HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Notifications\\Settings\\[^\\]+$", RegexOptions.IgnoreCase);
        private static readonly Regex ProgIdKey = new Regex(@"^HK(CU|LM)\\SOFTWARE\\(WOW6432Node\\)?Classes\\([^\\]+)$", RegexOptions.IgnoreCase);
        private static readonly Regex ClsidKey = new Regex(@"^HK(CU|LM)\\SOFTWARE\\(WOW6432Node\\)?Classes\\CLSID\\(\{[0-9A-Fa-f\-]{36}\})$", RegexOptions.IgnoreCase);
        private static readonly Regex ShellExKey = new Regex(
            @"^HK(CU|LM)\\SOFTWARE\\Classes\\(\*|Directory|Directory\\Background|Folder|Drive|AllFilesystemObjects|DesktopBackground|lnkfile|exefile)\\shellex\\" +
            @"(ContextMenuHandlers|DragDropHandlers|PropertySheetHandlers|CopyHookHandlers|IconOverlayHandlers)\\[^\\]+$", RegexOptions.IgnoreCase);
        private static readonly Regex OverlayKey = new Regex(
            @"^HKLM\\SOFTWARE\\(WOW6432Node\\)?Microsoft\\Windows\\CurrentVersion\\Explorer\\ShellIconOverlayIdentifiers\\[^\\]+$", RegexOptions.IgnoreCase);
        private static readonly Regex ClientKey = new Regex(@"^HK(CU|LM)\\SOFTWARE\\(WOW6432Node\\)?Clients\\[^\\]+\\[^\\]+$", RegexOptions.IgnoreCase);
        private static readonly Regex VerbKey = new Regex(
            @"^HK(CU|LM)\\SOFTWARE\\Classes\\(\*|Directory|Directory\\Background|Folder|Drive|AllFilesystemObjects|DesktopBackground|LibraryFolder\\background|" +
            @"SystemFileAssociations\\[^\\]+)\\shell\\[^\\]+$", RegexOptions.IgnoreCase);

        /// <summary>Chiavi in cui è consentito eliminare singoli valori (tracce legate al percorso dell'eseguibile, avvio automatico...).</summary>
        private static readonly HashSet<string> ValueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
            @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
            @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store",
            @"HKCU\Software\Microsoft\DirectX\UserGpuPreferences",
            @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppSwitched",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\ShowJumpView",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppBadgeUpdated",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppLaunch",
            @"HKLM\SOFTWARE\RegisteredApplications", @"HKCU\Software\RegisteredApplications", @"HKLM\SOFTWARE\WOW6432Node\RegisteredApplications",
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved",
            @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved",
        };

        private static readonly Regex[] ValueKeyPatterns =
        {
            new Regex(@"^HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\UserAssist\\\{[^\\]+\}\\Count$", RegexOptions.IgnoreCase),
            new Regex(@"^HK(CU|LM)\\SOFTWARE\\Classes\\\.[^\\]+\\OpenWithProgids$", RegexOptions.IgnoreCase),
            new Regex(@"^HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\\.[^\\]+\\OpenWithProgids$", RegexOptions.IgnoreCase),
        };

        public static bool IsDeletableRegistryKey(string fullPath)
        {
            string s = RegUtil.ShortName(fullPath);
            if (s == null || !(s.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase) || s.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase))) return false;
            s = s.TrimEnd('\\');
            // "Software\\Microsoft" verrebbe aperto da .NET come "Software\Microsoft": i controlli devono vedere il percorso vero
            if (s.Contains(@"\\")) return false;
            if (ProtectedKeys.Contains(s)) return false;
            if (s.Split('\\').Length < 3) return false; // almeno HKxx\Software\Nome
            foreach (var re in AllowedKeyPatterns) if (re.IsMatch(s)) return true;
            if (ApplicationKey.IsMatch(s)) return CommandsAreThirdParty(s);
            if (NotificationKey.IsMatch(s)) return IsProgramNotificationKey(s);
            if (ClsidKey.IsMatch(s)) return IsThirdPartyClsid(s);
            if (ShellExKey.IsMatch(s) || OverlayKey.IsMatch(s)) return IsThirdPartyHandler(s);
            if (ClientKey.IsMatch(s)) return CommandsAreThirdParty(s);
            if (VerbKey.IsMatch(s)) return VerbIsThirdParty(s);
            var progId = ProgIdKey.Match(s);
            if (progId.Success) return IsThirdPartyProgId(s, progId.Groups[3].Value);
            foreach (var t in DeniedKeyTrees)
                if (s.Equals(t, StringComparison.OrdinalIgnoreCase) || s.StartsWith(t + "\\", StringComparison.OrdinalIgnoreCase)) return false;
            return s.StartsWith(@"HKCU\Software\", StringComparison.OrdinalIgnoreCase) || s.StartsWith(@"HKLM\SOFTWARE\", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly Regex SystemProgIdName = new Regex(
            @"^(\..*|\*|CLSID|Interface|TypeLib|AppID|Wow6432Node|Installer|Local Settings|MIME|SystemFileAssociations|PackagedCom|ActivatableClasses|" +
            @"Extensions|DirectShow|Media Type|MediaFoundation|Record|Component Categories|FileType|Applications|AllFilesystemObjects|Directory|Drive|" +
            @"Folder|DesktopBackground|Network|Printers|exefile|lnkfile|batfile|cmdfile|comfile|piffile|scrfile|htmlfile|mhtmlfile|https?|ftp|mailto|" +
            @"InternetShortcut|Unknown|CompressedFolder|txtfile|regfile|inffile|inifile|jsfile|JSEFile|VBSFile|VBEFile|WSFFile|WSHFile|Msi\..*|" +
            @"cplfile|dllfile|sysfile|ocxfile|themefile|AppX.*|Microsoft\..*|Windows\..*|ms-.*|search-ms|shell|\{.*\})$",
            RegexOptions.IgnoreCase);

        /// <summary>Un tipo di file (ProgID) di un programma: niente nomi di sistema e nessun comando verso Windows.</summary>
        private static bool IsThirdPartyProgId(string keyPath, string name)
        {
            if (SystemProgIdName.IsMatch(name)) return false;
            return CommandsAreThirdParty(keyPath);
        }

        /// <summary>True se tutti i comandi (shell\*\command) della chiave avviano file fuori da Windows.</summary>
        private static bool CommandsAreThirdParty(string keyPath)
        {
            using (var k = RegUtil.Open(keyPath))
            {
                if (k == null) return true; // già eliminata
                using (var shell = k.OpenSubKey("shell"))
                {
                    foreach (var verb in RegUtil.SubKeys(shell))
                    {
                        using (var cmd = shell.OpenSubKey(verb + "\\command"))
                        {
                            string target = cmd == null ? null : Traces.TargetOfCommand(RegUtil.GetString(cmd, ""));
                            if (target != null && IsSystemPath(target)) return false;
                        }
                    }
                }
                using (var icon = k.OpenSubKey("DefaultIcon"))
                {
                    string ic = icon == null ? null : RegUtil.GetString(icon, "");
                    string path = ic == null ? null : PathUtil.Normalize(ic.Split(',')[0]);
                    if (path != null && IsSystemPath(path)) return false;
                }
            }
            return true;
        }

        /// <summary>Voce di menu contestuale di un programma: il comando avvia un file fuori da Windows.</summary>
        private static bool VerbIsThirdParty(string keyPath)
        {
            using (var cmd = RegUtil.Open(keyPath + "\\command"))
            {
                if (cmd == null) return !RegUtil.KeyExists(keyPath); // già eliminata; senza comando non si sa di chi sia
                string target = Traces.TargetOfCommand(RegUtil.GetString(cmd, ""));
                return target != null && !IsSystemPath(target);
            }
        }

        /// <summary>Un componente COM di un programma: il file che lo implementa non è di Windows.</summary>
        private static bool IsThirdPartyClsid(string keyPath)
        {
            using (var k = RegUtil.Open(keyPath))
            {
                if (k == null) return true;
                foreach (var server in new[] { "InprocServer32", "LocalServer32" })
                {
                    using (var sk = k.OpenSubKey(server))
                    {
                        if (sk == null) continue;
                        string raw = RegUtil.GetString(sk, "");
                        string path = server == "LocalServer32" ? Traces.TargetOfCommand(raw) : PathUtil.Normalize(raw);
                        if (path == null || IsSystemPath(path)) return false;
                        return true;
                    }
                }
            }
            return false; // senza file associato non si sa di chi sia
        }

        /// <summary>Voce di menu o sovrimpressione di Esplora risorse che attiva un componente COM di un programma (o inesistente).</summary>
        private static bool IsThirdPartyHandler(string keyPath)
        {
            string clsid;
            using (var k = RegUtil.Open(keyPath))
            {
                if (k == null) return true;
                clsid = RegUtil.GetString(k, "");
            }
            string name = keyPath.Substring(keyPath.LastIndexOf('\\') + 1);
            if (string.IsNullOrWhiteSpace(clsid) || !clsid.Trim().StartsWith("{")) clsid = name.StartsWith("{") ? name : null;
            if (clsid == null) return false;
            clsid = clsid.Trim();
            foreach (var root in new[] { @"HKCU\Software\Classes\CLSID\", @"HKLM\SOFTWARE\Classes\CLSID\", @"HKLM\SOFTWARE\WOW6432Node\Classes\CLSID\" })
                if (RegUtil.KeyExists(root + clsid) && !IsThirdPartyClsid(root + clsid)) return false;
            return true; // componente di un programma, oppure non più registrato
        }

        /// <summary>Impostazioni delle notifiche di un programma: l'ID dell'app deve essere il percorso di un eseguibile fuori da Windows.</summary>
        private static bool IsProgramNotificationKey(string keyPath)
        {
            string aumid = KnownDirs.ExpandKnownFolderId(keyPath.Substring(keyPath.LastIndexOf('\\') + 1));
            return aumid.Length > 3 && aumid[1] == ':' && aumid.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) > 0 && !IsSystemPath(aumid);
        }

        public static bool IsDeletableRegistryValue(string keyPath, string valueName)
        {
            if (valueName == null || keyPath == null) return false;
            string s = RegUtil.ShortName(keyPath).TrimEnd('\\');
            if (s.Contains(@"\\")) return false;
            if (Traces.RunKeys.Contains(s, StringComparer.OrdinalIgnoreCase)) return IsThirdPartyRunValue(s, valueName);
            if (Traces.StartupApprovedKeys.Contains(s, StringComparer.OrdinalIgnoreCase)) return valueName.Length > 0;
            if (ValueKeys.Contains(s) || ValueKeyPatterns.Any(re => re.IsMatch(s))) return valueName.Length > 0 || ValueKeys.Contains(s);
            return valueName.Length > 0 && IsDeletableRegistryKey(keyPath);
        }

        /// <summary>Una voce di avvio automatico di un programma (non di Windows).</summary>
        private static bool IsThirdPartyRunValue(string keyPath, string valueName)
        {
            if (valueName.Length == 0) return false;
            using (var k = RegUtil.Open(keyPath))
            {
                if (k == null) return true;
                string target = Traces.TargetOfCommand(RegUtil.GetString(k, valueName));
                return target == null || !IsSystemPath(target);
            }
        }

        // ------------------------------------------------------------------ Attività, servizi, PATH

        /// <summary>
        /// Le attività di Windows (\Microsoft\...) non si toccano mai, e nemmeno quelle che avviano programmi di Windows
        /// (alcune stanno nella cartella principale, es. CreateExplorerShellUnelevatedTask).
        /// </summary>
        public static bool IsDeletableTask(string taskPath)
        {
            if (string.IsNullOrEmpty(taskPath) || !taskPath.StartsWith("\\") || taskPath.Length < 2 || taskPath.EndsWith("\\")) return false;
            if (taskPath.StartsWith("\\Microsoft\\", StringComparison.OrdinalIgnoreCase)) return false;
            var targets = Traces.TaskTargets(taskPath);
            if (targets == null) return true; // già eliminata (o non leggibile: l'eliminazione lo segnalerà)
            return !targets.Any(t => PathUtil.Normalize(t) == null || IsSystemPath(t));
        }

        private static readonly HashSet<string> ProtectedServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WinDefend", "MDCoreSvc", "WdNisSvc", "Sense", "SecurityHealthService", "TrustedInstaller", "wuauserv", "WMPNetworkSvc",
            "edgeupdate", "edgeupdatem", "MicrosoftEdgeElevationService", "GamingServices", "GamingServicesNet", "ClickToRunSvc",
        };

        /// <summary>Solo servizi di programmi (con un proprio eseguibile fuori da Windows); mai driver o servizi di sistema.</summary>
        public static bool IsDeletableService(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOf('\\') >= 0 || ProtectedServices.Contains(name)) return false;
            using (var k = RegUtil.Open(Traces.ServicesRoot + "\\" + name))
            {
                if (k == null) return true; // già eliminato
                object type = k.GetValue("Type");
                if (!(type is int) || ((int)type & 0x30) == 0) return false;
                string image = RegUtil.GetString(k, "ImagePath");
                string exe = PathUtil.Normalize(PathUtil.ExeFromCommand(image));
                if (exe == null || IsSystemPath(exe) || Path.GetFileName(exe).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase)) return false;
                if (PathUtil.IsUnder(exe, KnownDirs.ProgramFiles) && exe.Substring(KnownDirs.ProgramFiles.Length + 1).StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
        }

        /// <summary>Una cartella nella variabile PATH: mai quelle di Windows o delle app di sistema.</summary>
        public static bool IsDeletablePathEntry(string keyPath, string valueName, string entry)
        {
            if (!PathUtil.Same(RegUtil.ShortName(keyPath), Traces.UserEnvironment) && !PathUtil.Same(RegUtil.ShortName(keyPath), Traces.SystemEnvironment)) return false;
            if (!string.Equals(valueName, "Path", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(entry)) return false;
            if (Regex.IsMatch(entry, @"%(SystemRoot|windir|SystemDrive)%", RegexOptions.IgnoreCase)) return false;
            string expanded = PathUtil.Normalize(entry);
            if (expanded == null) return false;
            if (IsSystemPath(expanded)) return false;
            return !PathUtil.IsUnder(expanded, Path.Combine(KnownDirs.LocalAppData, "Microsoft"), true);
        }

        /// <summary>Verifica complessiva di un elemento residuo.</summary>
        public static bool IsDeletable(LeftoverItem item)
        {
            switch (item.Kind)
            {
                case ItemKind.Directory:
                case ItemKind.File:
                    return IsDeletablePath(item.Path);
                case ItemKind.RegistryKey:
                    return IsDeletableRegistryKey(item.Path);
                case ItemKind.RegistryValue:
                    return IsDeletableRegistryValue(item.Path, item.ValueName);
                case ItemKind.FirewallRule:
                    return !string.IsNullOrEmpty(item.Path) && item.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !IsSystemPath(item.Path);
                case ItemKind.ScheduledTask:
                    return IsDeletableTask(item.Path);
                case ItemKind.Service:
                    return IsDeletableService(item.Path);
                case ItemKind.PathEntry:
                    return IsDeletablePathEntry(item.Path, item.ValueName, item.Data);
            }
            return false;
        }
    }
}
