using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AppManager
{
    /// <summary>
    /// Rilevamento dei programmi: voci di "App installate", app di Microsoft Store, applicazioni di Steam,
    /// programmi portable (collegamenti e cartelle dei portable) e programmi aggiunti a mano.
    /// I giochi già rilevati vengono esclusi.
    /// </summary>
    internal static class ProgramDetector
    {
        public static List<AppEntry> DetectAll(AppSettings settings, List<AppEntry> games, Action<string> progress)
        {
            var list = new List<AppEntry>();
            games = games ?? new List<AppEntry>();
            Run(progress, L.T("programmi installati"), () => AddRegistryPrograms(list, games));
            Run(progress, L.T("app di Microsoft Store"), () => AddStoreApps(list, games));
            Run(progress, L.T("applicazioni di Steam"), () => list.AddRange(SteamInfo.DetectApps(AppKind.Program, settings.ShowSteamTools)
                .Where(a => !games.Any(g => g.Id == a.Id))));
            Run(progress, L.T("collegamenti"), () => AddShortcutPrograms(list, games));
            Run(progress, L.T("cartelle dei programmi portable"), () => AddFolderPrograms(list, games, settings));
            Run(progress, L.T("programmi aggiunti a mano"), () => AddManualPrograms(list, games, settings));
            foreach (var p in list)
            {
                p.FoundBy.Add(p.Via);
                try { Complete(p); }
                catch (Exception ex) { Log.Error("Completamento " + p.Name, ex); }
            }
            MarkContainers(list, games);
            return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static void Run(Action<string> progress, string what, Action action)
        {
            if (progress != null) progress(L.F("Cerco programmi: {0}…", what));
            try { action(); }
            catch (Exception ex) { Log.Error("Rilevamento " + what, ex); }
        }

        // ------------------------------------------------------------------ App installate (registro)

        private static readonly Regex UpdateName = new Regex(@"(\(KB\d{6,}\)|^KB\d{6,}|^Security Update for|^Update for |^Hotfix for )", RegexOptions.IgnoreCase);
        private static readonly Regex MsiProductCode = new Regex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$");

        private static void AddRegistryPrograms(List<AppEntry> list, List<AppEntry> games)
        {
            var gameKeys = new HashSet<string>(games.Where(g => g.UninstallKey != null).Select(g => g.UninstallKey), StringComparer.OrdinalIgnoreCase);
            foreach (var e in InstalledApps.ReadUninstallEntries())
            {
                if (string.IsNullOrWhiteSpace(e.DisplayName) || e.IsSystemOrUpdate || UpdateName.IsMatch(e.DisplayName)) continue;
                if (InstalledApps.SteamAppKey.IsMatch(e.KeyName) || gameKeys.Contains(e.KeyPath)) continue;
                string uninstall = e.UninstallString;
                if (string.IsNullOrWhiteSpace(uninstall) && e.IsMsi && MsiProductCode.IsMatch(e.KeyName)) uninstall = "MsiExec.exe /X" + e.KeyName;
                if (string.IsNullOrWhiteSpace(uninstall)) continue; // come "App installate": senza comando di disinstallazione non si elenca
                string dir = InstalledApps.EntryInstallDir(e);
                // redistribuibili e strumenti registrati dentro la cartella di un gioco
                if (dir != null && games.Any(g => g.Dirs.Any(d => PathUtil.IsUnder(dir, d, true)))) continue;

                string name = NameUtil.CleanProgramName(e.DisplayName, e.DisplayVersion);
                // la stessa voce registrata due volte (32 e 64 bit, utente e computer)
                if (list.Any(p => p.Via == FoundVia.Installed && NameUtil.Norm(p.Name) == NameUtil.Norm(name)
                                  && (PathUtil.Same(p.InstallDir, dir) || string.Equals(p.UninstallString, uninstall, StringComparison.OrdinalIgnoreCase))))
                    continue;

                var p2 = new AppEntry
                {
                    Id = "reg:" + e.KeyPath.ToLowerInvariant(),
                    Kind = AppKind.Program,
                    Name = name,
                    Launcher = LauncherKind.Other,
                    InstallDir = dir,
                    Publisher = e.Publisher,
                    Version = e.DisplayVersion,
                    InstallDate = e.InstallDate,
                    UninstallKey = e.KeyPath,
                    UninstallString = uninstall,
                    QuietUninstallString = e.QuietUninstallString,
                    IconPath = e.DisplayIcon,
                    Source = L.F("App installate di Windows{0}", (e.IsMsi ? " (Windows Installer)" : "")),
                    Via = FoundVia.Installed,
                };
                if (e.EstimatedSizeKB > 0) p2.SizeBytes = e.EstimatedSizeKB * 1024;
                string icon = PathUtil.Normalize(PathUtil.ExeFromCommand(e.DisplayIcon == null ? null : e.DisplayIcon.Split(',')[0]));
                if (icon != null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(icon) && !InstalledApps.IsAuxiliaryExe(icon)
                    && !InstalledApps.IsInstallerCache(Path.GetDirectoryName(icon)))
                    p2.MainExe = icon;
                Components.Classify(p2);
                list.Add(p2);
            }
        }

        // ------------------------------------------------------------------ Microsoft Store

        private static void AddStoreApps(List<AppEntry> list, List<AppEntry> games)
        {
            var xboxRoots = XboxInfo.GameRoots();
            foreach (var app in StoreApps.Query())
            {
                if (games.Any(g => g.PackageFamily != null && string.Equals(g.PackageFamily, app.PackageFamily, StringComparison.OrdinalIgnoreCase))) continue;
                if (xboxRoots.Any(r => PathUtil.IsUnder(app.InstallDir, r))) continue;
                if (File.Exists(Path.Combine(app.InstallDir ?? "", "MicrosoftGame.config"))) continue; // giochi del Game Pass
                list.Add(app);
            }
        }

        // ------------------------------------------------------------------ Collegamenti: programmi senza voce di disinstallazione

        /// <summary>Cartelle di Programmi che appartengono a Windows (i loro collegamenti non sono programmi installati dall'utente).</summary>
        private static readonly Regex WindowsFolders = new Regex(
            @"^(windows .*|windowsapps|modifiablewindowsapps|windowspowershell|internet explorer|common files|microsoft update health tools|microsoft|" +
            @"dotnet|reference assemblies|msbuild|uninstall information|microsoft\.net|windows kits|windows sdk)$", RegexOptions.IgnoreCase);

        private static readonly Regex UninstallShortcut = new Regex(@"(uninstall|disinstall|rimuovi|remove|deinstall|désinstaller|desinstalar)", RegexOptions.IgnoreCase);

        private static void AddShortcutPrograms(List<AppEntry> list, List<AppEntry> games)
        {
            var installed = list.Where(p => p.Via == FoundVia.Installed).ToList();
            foreach (var lnk in InstalledApps.ShortcutFiles())
            {
                if (!lnk.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
                string lnkName = Path.GetFileNameWithoutExtension(lnk);
                if (UninstallShortcut.IsMatch(lnkName)) continue;
                string target, args;
                if (!Native.ReadShortcut(lnk, out target, out args)) continue;
                target = PathUtil.Normalize(target);
                if (target == null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
                if (PathUtil.IsUnder(target, KnownDirs.Windows) || InstalledApps.IsAuxiliaryExe(target)) continue;
                if (InstalledApps.Owns(games, target)) continue;
                var known = list.FirstOrDefault(x => InstalledApps.Owns(new[] { x }, target));
                if (known != null)
                {
                    known.FoundBy.Add(FoundVia.Shortcut);
                    // il collegamento indica l'eseguibile principale meglio di qualunque ricerca nella cartella
                    if (known.MainExe == null || (IsMainShortcut(lnkName, args, known) && !PathUtil.Same(known.MainExe, target)))
                        known.MainExe = target;
                    continue;
                }
                if (IsWindowsComponent(target)) continue;

                string root = InstalledApps.GuessRoot(target);
                // voce di "App installate" dello stesso programma che non indica la cartella (tipico dei pacchetti MSI)
                var owner = MatchInstalled(installed, lnk, target, root);
                if (owner != null)
                {
                    owner.FoundBy.Add(FoundVia.Shortcut);
                    if (owner.InstallDir == null && IsOwnFolder(root, target)) owner.InstallDir = PathUtil.RealCase(root);
                    else if (IsOwnFolder(root, target) && !owner.Dirs.Any(d => PathUtil.IsUnder(root, d, true))) owner.ExtraDirs.Add(PathUtil.RealCase(root));
                    if (owner.MainExe == null || IsMainShortcut(lnkName, args, owner)) owner.MainExe = target;
                    continue;
                }
                if (games.Any(g => g.MainExe != null && PathUtil.Same(g.MainExe, target))) continue;

                bool single = !IsOwnFolder(root, target);
                var vi = InstalledApps.VersionInfo(target);
                // un collegamento con argomenti apre qualcosa di preciso (es. un gioco in un emulatore): il suo nome non è quello del programma
                string candidateName = string.IsNullOrWhiteSpace(args) ? lnkName : Path.GetFileNameWithoutExtension(target);
                var p = new AppEntry
                {
                    Id = single ? "file:" + target.ToLowerInvariant() : "dir:" + root.ToLowerInvariant(),
                    Kind = AppKind.Program,
                    Name = NiceName(candidateName, vi, !string.IsNullOrWhiteSpace(args)),
                    Launcher = LauncherKind.Other,
                    InstallDir = single ? null : PathUtil.RealCase(root),
                    IsSingleFile = single,
                    MainExe = target,
                    Publisher = vi == null ? null : Clean(vi.CompanyName),
                    Version = vi == null ? null : Clean(vi.ProductVersion),
                    Source = InstalledApps.ShortcutSource(lnk) + (UnderProgramFolders(target) ? L.T(" (nessuna voce in App installate)") : ""),
                    Via = FoundVia.Shortcut,
                };
                list.Add(p);
            }
        }

        /// <summary>Componenti di Windows in Programmi (Windows Media Player, Windows Kits...).</summary>
        private static bool IsWindowsComponent(string exe)
        {
            foreach (var pf in new[] { KnownDirs.ProgramFiles, KnownDirs.ProgramFilesX86 })
            {
                if (!PathUtil.IsUnder(exe, pf)) continue;
                string first = exe.Substring(pf.Length + 1).Split('\\')[0];
                if (WindowsFolders.IsMatch(first)) return true;
                var vi = InstalledApps.VersionInfo(exe);
                if (vi != null && (vi.CompanyName ?? "").StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                    && (vi.ProductName ?? "").IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static bool UnderProgramFolders(string path)
        {
            return PathUtil.IsUnder(path, KnownDirs.ProgramFiles) || PathUtil.IsUnder(path, KnownDirs.ProgramFilesX86)
                || PathUtil.IsUnder(path, KnownDirs.LocalPrograms) || PathUtil.IsUnder(path, KnownDirs.ProgramData);
        }

        /// <summary>Cartelle che raccolgono file di programmi diversi: un eseguibile lì dentro è un programma "a file singolo".</summary>
        private static readonly Regex SharedFolderName = new Regex(@"^(tools?|utility|utilities|utilità|programmi|programs|apps?|portable|software|bin|downloads?|scaricati)$", RegexOptions.IgnoreCase);

        /// <summary>True se la cartella è propria del programma (e quindi può essere eliminata insieme a lui).</summary>
        private static bool IsOwnFolder(string root, string exe)
        {
            if (root == null || !InstalledApps.IsSpecificDir(root) || !Safety.IsDeletableDirectory(root)) return false;
            if (SharedFolderName.IsMatch(Path.GetFileName(root))) return false;
            foreach (var shared in new[] { KnownDirs.Desktop, KnownDirs.PublicDesktop, KnownDirs.Documents, KnownDirs.Downloads, KnownDirs.UserProfile })
                if (PathUtil.Same(root, shared)) return false;
            if (InstalledApps.PortableRoots(AppKind.Program, AppSettings.Current).Any(r => PathUtil.Same(r, root))) return false;
            // un eseguibile in mezzo a documenti o a un progetto: la cartella è dell'utente, del programma c'è solo il file
            if (InstalledApps.LooksLikeUserFolder(root)) return false;
            // una raccolta di strumenti diversi (es. D:\Utility con tanti eseguibili di prodotti diversi)
            if (DistinctProducts(root, exe) >= 3) return false;
            return true;
        }

        /// <summary>Quanti prodotti diversi da quello dell'eseguibile ci sono tra gli eseguibili nella cartella (al primo livello).</summary>
        private static int DistinctProducts(string root, string exe)
        {
            Func<string, string> productOf = f =>
            {
                var vi = InstalledApps.VersionInfo(f);
                string p = vi == null ? null : NameUtil.Norm(vi.ProductName);
                return string.IsNullOrEmpty(p) ? NameUtil.Norm(Path.GetFileNameWithoutExtension(f)) : p;
            };
            string own = productOf(exe);
            return PathUtil.Files(root, "*.exe").Where(f => !InstalledApps.IsAuxiliaryExe(f) && !PathUtil.Same(f, exe)).Take(40)
                .Select(productOf).Where(p => p.Length > 0 && p != own).Distinct().Count();
        }

        /// <summary>
        /// Cerca la voce di "App installate" a cui appartiene un collegamento: per nome del collegamento, della sua cartella
        /// nel menu Start, della cartella del programma o del prodotto indicato nell'eseguibile (con l'editore).
        /// </summary>
        private static AppEntry MatchInstalled(List<AppEntry> installed, string lnk, string target, string root)
        {
            var vi = InstalledApps.VersionInfo(target);
            string product = vi == null ? "" : NameUtil.Norm(vi.ProductName);
            string company = vi == null ? "" : CompanyKey(vi.CompanyName);
            string parent = Path.GetDirectoryName(lnk);
            bool inSubfolder = InstalledApps.IsInStartMenu(lnk) && !PathUtil.Same(parent, KnownDirs.StartMenu) && !PathUtil.Same(parent, KnownDirs.CommonStartMenu);
            var keys = new List<string> { NameUtil.Norm(Path.GetFileNameWithoutExtension(lnk)) };
            if (inSubfolder) keys.Add(NameUtil.Norm(Path.GetFileName(parent)));
            if (root != null) keys.Add(NameUtil.Norm(Path.GetFileName(root)));
            if (product.Length > 0) keys.Add(product);

            foreach (var key in keys)
            {
                if (key.Length < 4) continue;
                var matches = installed.Where(p =>
                {
                    string n = NameUtil.Norm(p.Name), b = NameUtil.Norm(NameUtil.BaseProgramName(p.Name));
                    return n == key || n.StartsWith(key) || (b.Length >= 4 && key.StartsWith(b));
                }).ToList();
                if (matches.Count == 1) return matches[0];
                if (matches.Count > 1 && company.Length > 0)
                {
                    var byCompany = matches.Where(p => CompanyKey(p.Publisher) == company).ToList();
                    if (byCompany.Count == 1) return byCompany[0];
                }
            }
            // stesso editore e un solo programma di quell'editore senza cartella nota
            if (company.Length >= 4)
            {
                var same = installed.Where(p => CompanyKey(p.Publisher) == company && (p.InstallDir == null || PathUtil.IsUnder(target, p.InstallDir))).ToList();
                if (same.Count == 1 && product.Length >= 4 && NameUtil.Norm(same[0].Name).Contains(product)) return same[0];
            }
            return null;
        }

        private static string CompanyKey(string company)
        {
            if (string.IsNullOrWhiteSpace(company)) return "";
            return NameUtil.Norm(Regex.Replace(company, @"[\s,\.]+(inc|ltd|llc|gmbh|s\.?a|s\.?r\.?l|s\.?p\.?a|co|corp|corporation|limited|ab|as|e\.?u|s\.?r\.?o|b\.?v|oy|pty|plc|ag)\.?\s*$", "", RegexOptions.IgnoreCase));
        }

        private static string Clean(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        /// <summary>
        /// Nome leggibile: quello del collegamento (o della cartella) oppure, se è generico o se si preferisce il prodotto,
        /// il nome del prodotto dichiarato dall'eseguibile.
        /// </summary>
        private static string NiceName(string candidate, FileVersionInfo vi, bool preferProduct = false)
        {
            string name = NameUtil.CleanProgramName(candidate, null);
            string product = vi == null ? null : Clean(vi.ProductName);
            if (product != null && product.Length >= 3 && !NameUtil.IsGeneric(NameUtil.Norm(product), AppKind.Program)
                && (preferProduct || NameUtil.IsGeneric(NameUtil.Norm(name), AppKind.Program) || name.Length < 3))
                return product;
            return SplitCamelPortable(name);
        }

        /// <summary>
        /// True se il collegamento è quello "principale" del programma: senza argomenti e con il nome del programma
        /// (es. "PuTTY" e non "PuTTYgen" o "Pageant").
        /// </summary>
        private static bool IsMainShortcut(string lnkName, string args, AppEntry p)
        {
            if (!string.IsNullOrWhiteSpace(args)) return false;
            string k = NameUtil.Norm(lnkName);
            return k.Length >= 3 && (k == NameUtil.Norm(p.Name) || k == NameUtil.Norm(NameUtil.BaseProgramName(p.Name)));
        }

        /// <summary>"FirefoxPortable" → "Firefox Portable" (nomi dei pacchetti PortableApps.com).</summary>
        private static string SplitCamelPortable(string name)
        {
            return Regex.Replace(name, @"([a-z0-9])Portable$", "$1 Portable");
        }

        // ------------------------------------------------------------------ Cartelle dei programmi portable

        public static List<string> PortableRoots(AppSettings settings)
        {
            return InstalledApps.PortableRoots(AppKind.Program, settings);
        }

        private static readonly HashSet<string> PlatformFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CommonFiles", "PortableApps.com", "Documents", "Data", "Settings", ".git",
        };

        private static void AddFolderPrograms(List<AppEntry> list, List<AppEntry> games, AppSettings settings)
        {
            // un eseguibile lasciato nella cartella dei giochi (es. un launcher) non è un gioco, ma è un programma
            foreach (var root in InstalledApps.PortableRoots(AppKind.Game, new AppSettings { LibraryFolders = new List<string>(), ProgramFolders = new List<string>() }))
                AddSingleExePrograms(list, games, root);
            foreach (var root in PortableRoots(settings))
            {
                foreach (var dir in PathUtil.Dirs(root))
                {
                    if (PlatformFolders.Contains(Path.GetFileName(dir)) || PathUtil.IsReparsePoint(dir)) continue;
                    if (InstalledApps.IsCovered(games, dir)) continue;
                    if (InstalledApps.IsCovered(list, dir))
                    {
                        InstalledApps.MarkFound(list, dir, FoundVia.Folder);
                        continue;
                    }
                    if (!Safety.IsDeletableDirectory(dir)) continue;
                    if (GameMarkers.IsStrong(dir)) continue; // un gioco messo tra i programmi: lo gestisce la sezione Giochi
                    string exe = InstalledApps.FindMainExe(dir, Path.GetFileName(dir));
                    if (exe == null) continue;
                    var vi = InstalledApps.VersionInfo(exe);
                    list.Add(new AppEntry
                    {
                        Id = "dir:" + dir.ToLowerInvariant(),
                        Kind = AppKind.Program,
                        Name = NiceName(Path.GetFileName(dir), vi),
                        Launcher = LauncherKind.Other,
                        InstallDir = dir,
                        MainExe = exe,
                        Publisher = vi == null ? null : Clean(vi.CompanyName),
                        Version = vi == null ? null : Clean(vi.ProductVersion),
                        Source = L.F("Cartella dei programmi portable ({0})", root),
                        Via = FoundVia.Folder,
                    });
                }
                AddSingleExePrograms(list, games, root);
            }
            AddUnregisteredPrograms(list, games);
        }

        /// <summary>Programmi di un solo file direttamente nella cartella indicata.</summary>
        private static void AddSingleExePrograms(List<AppEntry> list, List<AppEntry> games, string root)
        {
            foreach (var exe in PathUtil.Files(root, "*.exe"))
            {
                if (InstalledApps.IsAuxiliaryExe(exe) || InstalledApps.Owns(games, exe)) continue;
                var owner = list.FirstOrDefault(x => InstalledApps.Owns(new[] { x }, exe));
                if (owner != null) { owner.FoundBy.Add(FoundVia.Folder); continue; }
                var vi = InstalledApps.VersionInfo(exe);
                list.Add(new AppEntry
                {
                    Id = "file:" + exe.ToLowerInvariant(),
                    Kind = AppKind.Program,
                    Name = NiceName(Path.GetFileNameWithoutExtension(exe), vi),
                    Launcher = LauncherKind.Other,
                    MainExe = exe,
                    IsSingleFile = true,
                    Publisher = vi == null ? null : Clean(vi.CompanyName),
                    Version = vi == null ? null : Clean(vi.ProductVersion),
                    Source = L.F("Cartella dei programmi portable ({0})", root),
                    Via = FoundVia.Folder,
                });
            }
        }

        /// <summary>
        /// Programmi portable fuori dalle cartelle dedicate. Un programma che sta in un progetto o tra documenti è il solo eseguibile:
        /// la cartella è dell'utente e non va mai eliminata con lui.
        /// </summary>
        private static void AddUnregisteredPrograms(List<AppEntry> list, List<AppEntry> games)
        {
            foreach (var f in InstalledApps.UnregisteredApps(false).Where(x => !x.IsGame))
            {
                if (InstalledApps.IsCovered(games, f.Dir)) continue;
                if (InstalledApps.IsCovered(list, f.Dir)) { InstalledApps.MarkFound(list, f.Dir, FoundVia.Folder); continue; }
                if (list.Any(x => x.IsSingleFile && PathUtil.Same(x.MainExe, f.Exe))) continue;
                bool single = f.IsSingleFile || !InstalledApps.IsSpecificDir(f.Dir) || !Safety.IsDeletableDirectory(f.Dir) || InstalledApps.LooksLikeUserFolder(f.Dir);
                var vi = InstalledApps.VersionInfo(f.Exe);
                list.Add(new AppEntry
                {
                    Id = (single ? "file:" + f.Exe : "dir:" + f.Dir).ToLowerInvariant(),
                    Kind = AppKind.Program,
                    Name = NiceName(NameUtil.CleanFolderTitle(Path.GetFileName(f.Dir)), vi),
                    Launcher = LauncherKind.Other,
                    InstallDir = single ? null : f.Dir,
                    MainExe = f.Exe,
                    IsSingleFile = single,
                    Publisher = vi == null ? null : Clean(vi.CompanyName),
                    Version = vi == null ? null : Clean(vi.ProductVersion),
                    Source = f.Source,
                    Via = FoundVia.Folder,
                });
            }
        }

        // ------------------------------------------------------------------ Aggiunti a mano

        private static void AddManualPrograms(List<AppEntry> list, List<AppEntry> games, AppSettings settings)
        {
            foreach (var m in settings.ManualPrograms)
            {
                string path = PathUtil.Normalize(m.Path);
                if (path == null) continue;
                bool file = File.Exists(path);
                if (!file && !Directory.Exists(path)) continue;
                var existing = list.FirstOrDefault(p => PathUtil.Same(p.InstallDir, path) || PathUtil.Same(p.MainExe, path));
                if (existing != null) { existing.FoundBy.Add(FoundVia.Manual); continue; }
                list.Add(new AppEntry
                {
                    Id = "manual:" + path.ToLowerInvariant(),
                    Kind = AppKind.Program,
                    Name = string.IsNullOrWhiteSpace(m.Name) ? Path.GetFileNameWithoutExtension(path) : m.Name,
                    Launcher = LauncherKind.Other,
                    InstallDir = file ? null : path,
                    MainExe = file ? path : null,
                    IsSingleFile = file,
                    IsManual = true,
                    Source = L.T("Aggiunto manualmente"),
                    Via = FoundVia.Manual,
                });
            }
        }

        // ------------------------------------------------------------------ Completamento

        private static void Complete(AppEntry p)
        {
            if (p.Launcher != LauncherKind.Store && (p.MainExe == null || !File.Exists(p.MainExe)) && p.InstallDir != null)
                p.MainExe = InstalledApps.FindMainExe(p.InstallDir, NameUtil.BaseProgramName(p.Name));
            // icona: quella dichiarata, a meno che non sia del programma di disinstallazione o dell'installer
            string iconFile = string.IsNullOrEmpty(p.IconPath) ? null : PathUtil.Normalize(p.IconPath.Split(',')[0].Trim('"'));
            bool poorIcon = iconFile == null || !File.Exists(iconFile) || InstalledApps.IsInstallerCache(Path.GetDirectoryName(iconFile) ?? "")
                || (iconFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && InstalledApps.IsAuxiliaryExe(iconFile));
            if (poorIcon && p.MainExe != null && File.Exists(p.MainExe)) p.IconPath = p.MainExe;
            else if (iconFile == null || !File.Exists(iconFile)) p.IconPath = null;
            if (p.Publisher == null && p.MainExe != null)
            {
                var vi = InstalledApps.VersionInfo(p.MainExe);
                if (vi != null) p.Publisher = Clean(vi.CompanyName);
            }
            if (!p.InstallDate.HasValue)
            {
                try
                {
                    if (p.InstallDir != null && Directory.Exists(p.InstallDir)) p.InstallDate = Directory.GetCreationTime(p.InstallDir);
                    else if (p.MainExe != null && File.Exists(p.MainExe)) p.InstallDate = File.GetCreationTime(p.MainExe);
                }
                catch { }
            }
            if (p.IsSingleFile && p.SizeBytes < 0 && p.MainExe != null)
            {
                try { p.SizeBytes = new FileInfo(p.MainExe).Length; } catch { }
            }
        }

        /// <summary>
        /// Una cartella che contiene quelle di altri programmi o giochi (es. Steam con steamapps) non va mai proposta per intero;
        /// due voci con la stessa cartella la condividono.
        /// </summary>
        private static void MarkContainers(List<AppEntry> list, List<AppEntry> games)
        {
            var all = list.Concat(games).ToList();
            foreach (var p in list)
            {
                if (p.InstallDir == null) continue;
                p.ContainsOthers = all.Any(o => o != p && o.Dirs.Any(d => PathUtil.IsUnder(d, p.InstallDir) || PathUtil.Same(d, p.InstallDir)));
            }
        }
    }

    // ====================================================================== Componenti (runtime, driver, app di Windows)

    internal static class Components
    {
        private static readonly Regex DriverNames = new Regex(
            @"(\bdrivers?\b|\bdriver grafico|audio controller|usb.*controller|chipset|bus driver|\bpnp\b|firmware|bluetooth.*(stack|driver))",
            RegexOptions.IgnoreCase);

        private static readonly Regex RuntimeNames = new Regex(
            @"(visual c\+\+|redistributable|\bredist\b|\.net (framework|runtime|desktop runtime|core)|desktop runtime|asp\.net|\bruntime\b|" +
            @"directx|webview2|windows (software development kit|sdk|assessment and deployment kit|app runtime|driver kit)|\bsdk\b|\badk\b|" +
            @"application compatibility|update health tools|gameinput|physx|openal|vulkan|\bjava\b|\bjdk\b|\bjre\b|openjdk|" +
            @"microsoft (edge )?update|vs_|msxml|sql server .*(native client|compact|localdb|clr types|t-sql)|report viewer|" +
            @"access database engine|office .*(runtime|shared)|\bcodec(s)? (runtime|redist)|windows installer|powershell \d|" +
            @"microsoft (edge )?webview|teams machine-wide installer|windows pc health check)",
            RegexOptions.IgnoreCase);

        /// <summary>Programmi di Windows che si aggiornano con il sistema e non vanno disinstallati come gli altri.</summary>
        private static readonly Regex WindowsApps = new Regex(@"^(microsoft edge|microsoft onedrive setup|windows .*)$", RegexOptions.IgnoreCase);

        public static void Classify(AppEntry p)
        {
            string name = p.Name ?? "";
            if (DriverNames.IsMatch(name)) Mark(p, "Driver");
            else if (RuntimeNames.IsMatch(name)) Mark(p, L.T("Componente"));
            else if (WindowsApps.IsMatch(name) && (p.Publisher ?? "").StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) Mark(p, L.T("App di Windows"));
        }

        public static void Mark(AppEntry p, string label)
        {
            p.IsComponent = true;
            p.ComponentLabel = label;
        }
    }

    // ====================================================================== Microsoft Store (pacchetti MSIX/AppX)

    internal static class StoreApps
    {
        /// <summary>Editore delle app di Microsoft (le app incluse in Windows).</summary>
        private const string MicrosoftPublisherId = "8wekyb3d8bbwe";

        /// <summary>
        /// Pacchetti installati per l'utente, esclusi quelli di sistema, i framework e quelli non rimovibili.
        /// Si usa Get-AppxPackage: l'API documentata di Windows, utilizzabile senza SDK.
        /// </summary>
        public static List<AppEntry> Query()
        {
            var result = new List<AppEntry>();
            string script = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; " +
                "Get-AppxPackage -PackageTypeFilter Main | Where-Object { $_.SignatureKind -ne 'System' -and -not $_.IsFramework -and -not $_.NonRemovable } | " +
                "ForEach-Object { $_.PackageFullName + '|' + $_.PackageFamilyName + '|' + $_.Name + '|' + $_.InstallLocation + '|' + $_.SignatureKind + '|' + $_.Version }";
            string output = RunPowerShell(script, 60000);
            if (output == null) return result;
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split('|');
                if (f.Length < 6 || string.IsNullOrWhiteSpace(f[3])) continue;
                try
                {
                    var app = FromManifest(f[0], f[1], f[2], f[3].Trim(), f[4], f[5]);
                    if (app != null) result.Add(app);
                }
                catch (Exception ex) { Log.Error("Pacchetto " + f[0], ex); }
            }
            return result;
        }

        private static AppEntry FromManifest(string fullName, string family, string name, string dir, string signature, string version)
        {
            string manifest = Path.Combine(dir, "AppxManifest.xml");
            if (!File.Exists(manifest)) return null;
            var doc = XDocument.Load(manifest);
            Func<XElement, string, XElement> child = (e, n) => e == null ? null : e.Elements().FirstOrDefault(x => x.Name.LocalName == n);
            var props = child(doc.Root, "Properties");
            string display = Value(child(props, "DisplayName"));
            string publisher = Value(child(props, "PublisherDisplayName"));
            string logo = Value(child(props, "Logo"));

            // solo i pacchetti con almeno un'app visibile nel menu Start (esclusi codec, estensioni e servizi)
            XElement visible = null;
            var apps = child(doc.Root, "Applications");
            if (apps != null)
            {
                foreach (var a in apps.Elements().Where(x => x.Name.LocalName == "Application"))
                {
                    var ve = a.Elements().FirstOrDefault(x => x.Name.LocalName == "VisualElements");
                    if (ve == null || string.Equals((string)ve.Attribute("AppListEntry"), "none", StringComparison.OrdinalIgnoreCase)) continue;
                    visible = a;
                    break;
                }
            }
            if (visible == null) return null;
            var visual = visible.Elements().First(x => x.Name.LocalName == "VisualElements");

            display = Resolve(display, fullName, name);
            if (string.IsNullOrWhiteSpace(display) || display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                display = Resolve((string)visual.Attribute("DisplayName"), fullName, name);
            if (string.IsNullOrWhiteSpace(display) || display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) display = name;
            publisher = Resolve(publisher, fullName, name);
            if (publisher != null && publisher.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) publisher = null;

            string exe = (string)visible.Attribute("Executable");
            string icon = StoreLogo(dir, (string)visual.Attribute("Square44x44Logo")) ?? StoreLogo(dir, (string)visual.Attribute("Square150x150Logo")) ?? StoreLogo(dir, logo);
            var app = new AppEntry
            {
                Id = "store:" + family.ToLowerInvariant(),
                Kind = AppKind.Program,
                Name = display.Trim(),
                Launcher = LauncherKind.Store,
                InstallDir = dir,
                Publisher = publisher,
                Version = version,
                PackageName = name,
                PackageFamily = family,
                PackageFullName = fullName,
                MainExe = string.IsNullOrEmpty(exe) ? null : Path.Combine(dir, exe),
                IconPath = icon,
                Source = signature == "Developer" ? L.T("App installata fuori dallo Store (pacchetto MSIX)") : "Microsoft Store",
                Via = FoundVia.Store,
            };
            try { app.InstallDate = Directory.GetCreationTime(dir); } catch { }
            // app di Microsoft incluse in Windows o installate da Windows (Clipchamp, Solitaire, Teams...)
            if (family.EndsWith("_" + MicrosoftPublisherId, StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                || (publisher ?? "").StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase))
                Components.Mark(app, L.T("App di Windows"));
            return app;
        }

        private static string Value(XElement e)
        {
            return e == null ? null : e.Value.Trim();
        }

        /// <summary>Risolve i testi "ms-resource:" del manifest nella lingua di Windows.</summary>
        private static string Resolve(string value, string fullName, string name)
        {
            if (string.IsNullOrEmpty(value) || !value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return value;
            string key = value.Substring("ms-resource:".Length);
            var candidates = new List<string>();
            if (key.StartsWith("//")) candidates.Add(value);
            else
            {
                string k = key.TrimStart('/');
                if (k.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase)) candidates.Add("ms-resource://" + name + "/" + k);
                else
                {
                    candidates.Add("ms-resource://" + name + "/Resources/" + k);
                    candidates.Add("ms-resource://" + name + "/" + k);
                }
            }
            foreach (var c in candidates)
            {
                string s = Native.LoadIndirectString("@{" + fullName + "?" + c + "}");
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            return value;
        }

        /// <summary>Il logo migliore tra le varianti di un'immagine del pacchetto (dimensione, scala, "unplated").</summary>
        public static string StoreLogo(string dir, string rel)
        {
            if (string.IsNullOrEmpty(rel)) return null;
            string full = Path.Combine(dir, rel);
            string folder = Path.GetDirectoryName(full);
            string baseName = Path.GetFileNameWithoutExtension(full);
            string ext = Path.GetExtension(full);
            string best = null;
            int bestScore = -1;
            foreach (var f in PathUtil.Files(folder, baseName + "*" + ext))
            {
                string q = Path.GetFileNameWithoutExtension(f).Substring(baseName.Length).ToLowerInvariant();
                if (q.Contains("contrast-") || q.Contains("theme-light")) continue;
                int size = 44;
                var m = Regex.Match(q, @"targetsize-(\d+)");
                if (m.Success) size = int.Parse(m.Groups[1].Value);
                else
                {
                    m = Regex.Match(q, @"scale-(\d+)");
                    if (m.Success) size = 44 * int.Parse(m.Groups[1].Value) / 100;
                }
                // grande è meglio; le varianti "unplated" sono pensate per stare su qualunque sfondo, quelle "light" per i temi chiari
                int score = Math.Min(size, 256) * 2 + (q.Contains("unplated") ? 100 : 0) - (q.Contains("light") ? 150 : 0);
                if (score > bestScore) { bestScore = score; best = f; }
            }
            return best ?? (File.Exists(full) ? full : null);
        }

        /// <summary>Esegue uno script PowerShell nascosto e ne restituisce l'output (null in caso di errore).</summary>
        public static string RunPowerShell(string script, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
                {
                    CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    string output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return null; }
                    if (p.ExitCode != 0) Log.Info("PowerShell: codice {0}: {1}", p.ExitCode, err.Result.Trim());
                    return output;
                }
            }
            catch (Exception ex)
            {
                Log.Error("PowerShell", ex);
                return null;
            }
        }
    }
}
