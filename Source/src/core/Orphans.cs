using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace AppManager
{
    /// <summary>
    /// Cerca i residui di giochi e programmi che non sono più installati: dati di Steam, cartelle con struttura
    /// Unity/Unreal, salvataggi, tracce di eseguibili cancellati (anche portable, via Prefetch), collegamenti rotti,
    /// avvii automatici, attività, servizi, associazioni di file e voci della variabile PATH che puntano a file spariti.
    /// Ogni gruppo di residui è assegnato alla sezione Giochi o alla sezione Programmi.
    /// </summary>
    internal sealed class OrphanScanner
    {
        private sealed class Group
        {
            public string Key, Name, Source, Image;
            public AppKind Kind;
            public bool CheckRecency = true;
            public bool RenameFromFolder;   // il nome è quello (criptico) di un progetto Unreal: usa la cartella del gioco quando la scopriamo
            public string Company;          // sviluppatore (giochi Unity) o editore (programmi)
            public bool HasEvidence;        // prova che il gioco o programma è stato cancellato (exe sparito, collegamento rotto...)
            public readonly List<LeftoverItem> Items = new List<LeftoverItem>();
            public readonly List<string> Roots = new List<string>();
            public readonly HashSet<string> ExeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // altri nomi con cui cercare i dati
        }

        private readonly List<AppEntry> entries;
        private readonly List<AppProfile> profiles;
        private readonly HashSet<string> claimedKeys = new HashSet<string>();
        private readonly List<string> claimedDirs = new List<string>();
        private readonly HashSet<string> steamInstalled;
        private readonly HashSet<string> installedAppIds = new HashSet<string>();
        private readonly Dictionary<string, Group> groups = new Dictionary<string, Group>();
        private readonly HashSet<string> seen = new HashSet<string>();
        private readonly CancellationToken ct;
        private readonly Action<string> progress;
        private Dictionary<string, Tuple<string, string>> muiNames;

        private OrphanScanner(List<AppEntry> games, List<AppEntry> programs, Action<string> progress, CancellationToken ct)
        {
            entries = games.Concat(programs ?? new List<AppEntry>()).ToList();
            this.progress = progress;
            this.ct = ct;
            profiles = entries.Select(ProfileBuilder.Get).ToList();
            steamInstalled = SteamInfo.InstalledAppIds();
            foreach (var p in profiles)
            {
                claimedKeys.UnionWith(p.NameKeys);
                claimedKeys.UnionWith(p.ShortKeys);
                foreach (var k in p.PublisherKeys) claimedKeys.Add(k);
                foreach (var id in p.SteamAppIds) installedAppIds.Add(id);
                if (p.IsGame)
                {
                    string acr = Acronym(p.Entry.Name);
                    if (acr != null) claimedKeys.Add(acr);
                }
                claimedDirs.AddRange(p.Entry.Dirs);
                if (p.Entry.IsSingleFile && p.Entry.MainExe != null) claimedDirs.Add(p.Entry.MainExe);
            }
            installedAppIds.UnionWith(steamInstalled);
            // AppManager non è mai un residuo di se stesso
            claimedKeys.Add("appmanager");
            claimedKeys.Add("gamemanager");
            // le cartelle che PCGamingWiki indica per i giochi installati sono loro (solo cache, nessuna connessione)
            foreach (var g in entries.Where(e => e.IsGame)) claimedDirs.AddRange(SaveDatabase.KnownPaths(g));
            // AppID trovati online per nome per i giochi installati (es. repack che non dichiarano l'AppID in modo leggibile)
            installedAppIds.UnionWith(CoverService.KnownAppIds(games));
        }

        public static List<LeftoverItem> Scan(List<AppEntry> games, List<AppEntry> programs, Action<string> progress, CancellationToken ct)
        {
            // gli strumenti di Steam nascosti dall'elenco (es. SteamVR) sono comunque installati: i loro dati non sono residui
            var all = new List<AppEntry>(programs ?? new List<AppEntry>());
            try { all.AddRange(SteamInfo.DetectApps(AppKind.Program, true).Where(t => !all.Any(x => x.Id == t.Id))); }
            catch (Exception ex) { Log.Warn("Strumenti di Steam: {0}", ex.Message); }
            var s = new OrphanScanner(games, all, progress, ct);
            s.Step(L.T("programmi in esecuzione e avvii automatici"), s.ClaimActiveFiles);
            s.Step("Steam", s.SteamOrphans);
            s.Step(L.T("cartelle di dati di Unreal Engine e Unity"), s.EngineOrphans);
            s.Step(L.T("salvataggi"), s.SaveFolderOrphans);
            s.Step(L.T("giochi e programmi eliminati (Prefetch)"), s.PrefetchOrphans);
            s.Step(L.T("tracce nel registro"), s.RegistryTraceOrphans);
            s.Step(L.T("cartelle in Documenti"), s.DocumentOrphans);
            s.Step(L.T("collegamenti non validi"), s.ShortcutOrphans);
            s.Step(L.T("voci di disinstallazione non valide"), s.UninstallOrphans);
            s.Step(L.T("avvio automatico"), s.AutostartOrphans);
            s.Step(L.T("attività pianificate"), s.TaskOrphans);
            s.Step(L.T("servizi"), s.ServiceOrphans);
            s.Step(L.T("associazioni dei file e menu di Esplora risorse"), s.ClassOrphans);
            s.Step(L.T("variabile PATH"), s.PathOrphans);
            s.Step(L.T("cartelle rimaste in Programmi"), s.ProgramFilesOrphans);
            s.Step(L.T("dati dei programmi eliminati"), s.ProgramDataOrphans);
            s.Step(L.T("cache degli shader"), s.ShaderCaches);
            return s.Finish();
        }

        private void Step(string what, Action action)
        {
            ct.ThrowIfCancellationRequested();
            if (progress != null) progress(L.F("Cerco residui: {0}…", what));
            try { action(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Error(L.F("Residui orfani ({0})", what), ex); }
        }

        // ------------------------------------------------------------------ Supporto

        /// <summary>Iniziali di un titolo di più parole ("Assetto Corsa EVO" → "ace"): servono solo a NON toccare dati di giochi installati.</summary>
        private static string Acronym(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var words = Regex.Split(name, @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToList();
            if (words.Count < 2) return null;
            string a = string.Concat(words.Select(w => char.IsDigit(w[0]) ? w : w.Substring(0, 1)));
            return a.Length >= 2 ? NameUtil.Norm(a) : null;
        }

        private bool IsClaimedName(string name)
        {
            string k = NameUtil.Norm(name);
            if (k.Length == 0 || claimedKeys.Contains(k)) return true;
            // "SteamVR Room Setup" è di SteamVR: nel dubbio un nome che inizia con quello di un'applicazione installata è suo
            return claimedKeys.Any(c => c.Length >= 6 && k.StartsWith(c, StringComparison.Ordinal));
        }

        private bool IsClaimedPath(string path)
        {
            foreach (var d in claimedDirs)
                if (PathUtil.IsUnder(path, d, true) || PathUtil.IsUnder(d, path)) return true;
            return false;
        }

        /// <summary>
        /// Le cartelle dei programmi in esecuzione, dei servizi e delle attività con un eseguibile esistente sono in uso,
        /// anche se non compaiono tra i giochi e i programmi rilevati: non si propongono mai.
        /// </summary>
        private void ClaimActiveFiles()
        {
            var active = new List<string>();
            foreach (var proc in Process.GetProcesses())
            {
                try { string path = Native.GetProcessPath(proc.Id); if (path != null) active.Add(path); }
                catch { }
                finally { proc.Dispose(); }
            }
            active.AddRange(Traces.Services().Select(s => s.ExePath));
            active.AddRange(Traces.ScheduledTasks().SelectMany(t => t.Exes));
            active.AddRange(Traces.RunEntries().Select(t => t.ExePath));
            foreach (var exe in active.Where(e => e != null && File.Exists(e)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (PathUtil.IsUnder(exe, KnownDirs.Windows)) continue;
                string root = InstalledApps.GuessRoot(exe);
                claimedDirs.Add(root != null && InstalledApps.IsSpecificDir(root) ? root : exe);
            }
        }

        private Group NameGroup(string name, string source, AppKind kind)
        {
            string key = "n:" + NameUtil.Norm(name);
            Group g;
            if (!groups.TryGetValue(key, out g))
            {
                g = new Group { Key = key, Name = NameUtil.CleanDisplayName(name), Source = source, Kind = kind };
                groups[key] = g;
            }
            else if (kind == AppKind.Game) MarkGame(g);
            return g;
        }

        private static void MarkGame(Group g)
        {
            g.Kind = AppKind.Game;
            if (g.Source == L.T("Programma eliminato")) g.Source = L.T("Gioco eliminato");
        }

        /// <summary>Gruppi che raccolgono tracce non attribuibili a un singolo gioco o programma.</summary>
        private Group CatchAll(string key, string name, string source, AppKind kind = AppKind.Program)
        {
            Group g;
            if (!groups.TryGetValue("x:" + key, out g))
            {
                g = new Group { Key = "x:" + key, Name = name, Source = source, Kind = kind, CheckRecency = false, HasEvidence = true };
                groups[g.Key] = g;
            }
            return g;
        }

        private Group SteamGroup(string appId)
        {
            string key = "s:" + appId;
            Group g;
            if (!groups.TryGetValue(key, out g))
            {
                uint id;
                SteamAppInfo info;
                bool known = uint.TryParse(appId, out id) && SteamInfo.AppInfo.TryGetValue(id, out info);
                string type = known && SteamInfo.AppInfo[id].Type != null ? SteamInfo.AppInfo[id].Type.ToLowerInvariant() : null;
                g = new Group
                {
                    Key = key,
                    Name = SteamInfo.AppName(appId) ?? ("App Steam " + appId),
                    Source = "Steam",
                    Image = SteamInfo.FindImage(appId, true) ?? SteamInfo.FindImage(appId, false),
                    CheckRecency = false,
                    Kind = type == "application" || type == "tool" ? AppKind.Program : AppKind.Game,
                };
                if (known && !string.IsNullOrEmpty(SteamInfo.AppInfo[id].InstallDir))
                    foreach (var sa in SteamInfo.SteamAppsDirs) g.Roots.Add(Path.Combine(sa, "common", SteamInfo.AppInfo[id].InstallDir));
                groups[key] = g;
            }
            return g;
        }

        private List<string> containers;

        /// <summary>
        /// Cartella del gioco o programma a cui apparteneva un percorso: la prima cartella sotto una radice nota
        /// (C:\Games\X, steamapps\common\X, Programmi\X, Download\X, D:\X...).
        /// </summary>
        private string AppFolderOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (containers == null)
            {
                var list = new List<string>();
                list.AddRange(GameDetector.LibraryRoots(AppSettings.Current));
                list.AddRange(InstalledApps.PortableRoots(AppKind.Program, AppSettings.Current));
                list.AddRange(SteamInfo.SteamAppsDirs.Select(sa => Path.Combine(sa, "common")));
                list.AddRange(XboxInfo.GameRoots());
                list.AddRange(new[]
                {
                    KnownDirs.ProgramFiles, KnownDirs.ProgramFilesX86, KnownDirs.Downloads, KnownDirs.Desktop,
                    KnownDirs.Documents, KnownDirs.LocalPrograms, Path.Combine(KnownDirs.ProgramFiles, "Epic Games"),
                    Path.Combine(KnownDirs.ProgramFilesX86, @"GOG Galaxy\Games"), Path.Combine(KnownDirs.ProgramFilesX86, @"Ubisoft\Ubisoft Game Launcher\games"),
                    Path.Combine(KnownDirs.ProgramFiles, @"EA Games"), KnownDirs.LocalAppData, KnownDirs.RoamingAppData, KnownDirs.ProgramData,
                });
                foreach (var d in DriveInfo.GetDrives())
                {
                    string r = d.RootDirectory.FullName;
                    list.Add(r.TrimEnd('\\'));
                    foreach (var n in new[] { "Games", "Giochi", "GOG Games", "XboxGames", "Epic Games", @"SteamLibrary\steamapps\common", @"Downloads\Giochi", @"Downloads\Games" })
                        list.Add(Path.Combine(r, n));
                }
                list.Add(Path.Combine(KnownDirs.Downloads, "Giochi"));
                list.Add(Path.Combine(KnownDirs.Downloads, "Games"));
                list.Add(Path.Combine(KnownDirs.Desktop, "Giochi"));
                list.Add(Path.Combine(KnownDirs.Desktop, "Programmi"));
                containers = list.Where(c => !string.IsNullOrEmpty(c)).Select(c => c.TrimEnd('\\'))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(c => c.Length).ToList();
            }
            var system = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Users", "Windows", "ProgramData", "Program Files", "Program Files (x86)", "$Recycle.Bin", "Recovery", "PerfLogs" };
            foreach (var c in containers)
            {
                if (!PathUtil.IsUnder(path, c)) continue;
                string rel = path.Substring(c.Length + 1);
                int slash = rel.IndexOf('\\');
                if (slash <= 0) return null;
                string first = rel.Substring(0, slash);
                if (c.Length <= 2 && system.Contains(first)) return null;
                if (Traces.SkipTopLevel.Contains(NameUtil.Norm(first))) return null;
                return c + "\\" + first;
            }
            return null;
        }

        /// <summary>Trova il gruppo a cui appartiene la cartella di un gioco o programma (per percorso o per nome).</summary>
        private Group GroupForFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            var byRoot = groups.Values.FirstOrDefault(g => !g.Key.StartsWith("x:") && g.Roots.Any(r => PathUtil.Same(r, folder) || PathUtil.IsUnder(folder, r) || PathUtil.IsUnder(r, folder)));
            if (byRoot != null) return byRoot;
            Group byName;
            if (groups.TryGetValue("n:" + NameUtil.Norm(Path.GetFileName(folder)), out byName)) return byName;
            return groups.TryGetValue("n:" + NameUtil.Norm(NameUtil.CleanFolderTitle(Path.GetFileName(folder))), out byName) ? byName : null;
        }

        /// <summary>Nomi di cartelle che non dicono nulla sul programma che contenevano.</summary>
        private static readonly HashSet<string> WeakFolderNames = new HashSet<string>
        {
            "bios", "release", "releases", "dist", "build", "extracted", "newfolder", "nuovacartella", "files", "output", "out", "src", "source",
            "project", "test", "tests", "program", "runtime", "stuff", "misc", "various", "vari", "roba", "download", "downloads",
        };

        /// <summary>
        /// Nome del gruppo per un eseguibile cancellato: il titolo della sua cartella oppure, se la cartella ha un nome
        /// generico ("bios", "dist", "Nuova cartella"...), il nome del programma ricordato da Windows o quello dell'eseguibile.
        /// </summary>
        private string TitleFor(string folder, string exe)
        {
            string name = NameUtil.CleanFolderTitle(Path.GetFileName(folder));
            string k = NameUtil.Norm(name);
            if (k.Length >= 3 && !NameUtil.IsGeneric(k, AppKind.Program) && !WeakFolderNames.Contains(k)) return name;
            if (muiNames == null) muiNames = Traces.MuiCacheNames();
            Tuple<string, string> info;
            if (muiNames.TryGetValue(exe, out info) && !string.IsNullOrWhiteSpace(info.Item1)) return NameUtil.CleanProgramName(info.Item1, null);
            return NameUtil.CleanFolderTitle(Path.GetFileNameWithoutExtension(exe));
        }

        /// <summary>True se il percorso (o il nome della sua cartella) indica un gioco: repack, trainer, eseguibili dei motori di gioco.</summary>
        private bool LooksLikeGame(string exe, string folder)
        {
            return IsUnderGameRoot(exe) || GameMarkers.IsMarkerFileName(Path.GetFileName(exe))
                || NameUtil.GameReleaseTags.IsMatch(folder ?? exe);
        }

        private static readonly string WindowsApps = Path.Combine(KnownDirs.ProgramFiles, "WindowsApps");

        /// <summary>
        /// Trova (o crea, se <paramref name="create"/>) il gruppo di un eseguibile che non esiste più.
        /// Un gruppo nuovo è un gioco se l'eseguibile stava in una cartella di giochi, altrimenti un programma.
        /// </summary>
        private Group GroupForExe(string exe, bool create, bool gameLike = false)
        {
            string folder = AppFolderOf(exe);
            var byFolder = GroupForFolder(folder);

            // gruppo trovato dal nome dell'eseguibile o del progetto Unreal (es. "Sandfall" per Clair Obscur)
            Group byExe;
            string exeBase = Regex.Replace(Path.GetFileNameWithoutExtension(exe), @"([-_](Win64|Win32|WinGDK)[-_]Shipping|[-_]Shipping|[-_]x64|_BE|_EAC)$", "", RegexOptions.IgnoreCase);
            groups.TryGetValue("n:" + NameUtil.Norm(exeBase), out byExe);
            if (byExe == null)
            {
                var m = Regex.Match(exe, @"\\([^\\]+)\\Binaries\\Win(64|32)\\", RegexOptions.IgnoreCase);
                if (m.Success) groups.TryGetValue("n:" + NameUtil.Norm(m.Groups[1].Value), out byExe);
            }
            if (byExe != null && byExe.Key.StartsWith("x:")) byExe = null;

            var g = byFolder ?? byExe;
            if (byFolder != null && byExe != null && byFolder != byExe) g = Merge(byFolder, byExe);
            bool game = gameLike || LooksLikeGame(exe, folder);
            if (g == null && create && folder != null)
            {
                string title = TitleFor(folder, exe);
                if (!IsClaimedName(title) && !IsClaimedName(Path.GetFileName(folder)))
                    g = NameGroup(title, game ? L.T("Gioco eliminato") : L.T("Programma eliminato"), game ? AppKind.Game : AppKind.Program);
            }
            if (g == null || folder == null) return g;
            if (game) MarkGame(g);
            if (!g.Roots.Any(r => PathUtil.Same(r, folder))) g.Roots.Add(folder);
            string nice = NameUtil.CleanFolderTitle(Path.GetFileName(PathUtil.RealCase(folder)));
            if (g.RenameFromFolder && !NameUtil.IsGeneric(NameUtil.Norm(nice)))
            {
                g.Name = NameUtil.CleanDisplayName(nice);
                g.RenameFromFolder = false;
            }
            else if (g.Name == g.Name.ToLowerInvariant() && nice != nice.ToLowerInvariant() && NameUtil.Norm(nice) == NameUtil.Norm(g.Name))
            {
                g.Name = NameUtil.CleanDisplayName(nice);
            }
            return g;
        }

        /// <summary>Unisce due gruppi che si sono rivelati lo stesso gioco o programma. Tiene il nome più "umano".</summary>
        private Group Merge(Group a, Group b)
        {
            // i gruppi Steam hanno nome e copertina ufficiali: diventano il gruppo principale
            Group into = b.Key.StartsWith("s:") && !a.Key.StartsWith("s:") ? b : a;
            Group from = into == a ? b : a;
            into.Items.AddRange(from.Items);
            foreach (var r in from.Roots) if (!into.Roots.Any(x => PathUtil.Same(x, r))) into.Roots.Add(r);
            into.ExeNames.UnionWith(from.ExeNames);
            into.Names.UnionWith(from.Names);
            into.CheckRecency |= from.CheckRecency;
            into.HasEvidence |= from.HasEvidence;
            if (from.Kind == AppKind.Game) MarkGame(into);
            if (into.RenameFromFolder && !from.RenameFromFolder && !into.Key.StartsWith("s:"))
            {
                into.Name = from.Name;
                into.RenameFromFolder = false;
            }
            if (into.Source == L.T("Gioco eliminato") || into.Source == L.T("Programma eliminato")) into.Source = from.Source;
            if (into.Image == null) into.Image = from.Image;
            if (into.Company == null) into.Company = from.Company;
            groups.Remove(from.Key);
            return into;
        }

        private LeftoverItem Add(Group g, ItemKind kind, ItemCategory cat, string path, string reason, Confidence conf, string valueName = null, string data = null)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (kind == ItemKind.Directory && !Directory.Exists(path)) return null;
            if (kind == ItemKind.File && !File.Exists(path)) return null;
            var item = new LeftoverItem { Kind = kind, Category = cat, Path = path, ValueName = valueName, Data = data, Reason = reason, Confidence = conf };
            if (!seen.Add(item.Key)) return null;
            g.Items.Add(item);
            return item;
        }

        private static bool IsLocalFixedPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 3 || path[1] != ':' || path[2] != '\\') return false;
            try
            {
                var d = new DriveInfo(path.Substring(0, 1));
                return d.IsReady && d.DriveType == DriveType.Fixed;
            }
            catch { return false; }
        }

        /// <summary>True se il file non esiste più su un disco locale presente (e non è di Windows): prova che è stato cancellato.</summary>
        private bool IsGone(string path)
        {
            string n = PathUtil.Normalize(path);
            return n != null && IsLocalFixedPath(n) && !PathUtil.Exists(n) && !PathUtil.IsUnder(n, KnownDirs.Windows) && !IsClaimedPath(n);
        }

        /// <summary>Radici tipiche dei giochi: un eseguibile scomparso da qui era quasi certamente un gioco.</summary>
        private List<string> gameRoots;

        private bool IsUnderGameRoot(string path)
        {
            if (gameRoots == null)
            {
                gameRoots = new List<string>();
                gameRoots.AddRange(GameDetector.LibraryRoots(AppSettings.Current));
                gameRoots.AddRange(SteamInfo.SteamAppsDirs.Select(sa => Path.Combine(sa, "common")));
                gameRoots.AddRange(XboxInfo.GameRoots());
                gameRoots.Add(Path.Combine(KnownDirs.Desktop, "Giochi"));
                gameRoots.Add(Path.Combine(KnownDirs.Downloads, "Giochi"));
                gameRoots.Add(Path.Combine(KnownDirs.Downloads, "Games"));
                foreach (var drive in DriveInfo.GetDrives())
                    foreach (var n in new[] { "Games", "Giochi", "GOG Games", "SteamLibrary", "Program Files\\Epic Games", "Program Files (x86)\\GOG Galaxy\\Games" })
                        gameRoots.Add(Path.Combine(drive.RootDirectory.FullName, n));
            }
            return gameRoots.Any(r => PathUtil.IsUnder(path, r));
        }

        /// <summary>Gruppo di un eseguibile cancellato, oppure quello generico indicato (tracce senza una cartella riconoscibile).</summary>
        private Group GroupOrCatchAll(string exe, bool gameLike, string catchKey, string catchName, string catchSource)
        {
            // versioni precedenti delle app dello Store: le app ci sono ancora, restano solo tracce di vecchie cartelle
            if (PathUtil.IsUnder(exe, WindowsApps))
                return CatchAll("store-old", L.T("Tracce di versioni precedenti delle app dello Store"), "Microsoft Store");
            var g = GroupForExe(exe, true, gameLike);
            if (g != null) return g;
            bool game = gameLike || IsUnderGameRoot(exe);
            return game ? CatchAll(catchKey + "-giochi", catchName.Replace("programmi", "giochi"), catchSource, AppKind.Game)
                        : CatchAll(catchKey, catchName, catchSource);
        }

        /// <summary>Ricorda nome e produttore di un eseguibile cancellato (da MuiCache) per trovare i dati del programma.</summary>
        private void AddExeNames(Group g, string exe)
        {
            g.ExeNames.Add(Path.GetFileName(exe));
            if (muiNames == null) muiNames = Traces.MuiCacheNames();
            Tuple<string, string> info;
            if (!muiNames.TryGetValue(exe, out info)) return;
            if (!string.IsNullOrWhiteSpace(info.Item1)) g.Names.Add(info.Item1);
            if (!string.IsNullOrWhiteSpace(info.Item2) && g.Company == null) g.Company = info.Item2;
        }

        // ------------------------------------------------------------------ Steam

        private bool SteamInstalled(string id)
        {
            return steamInstalled.Contains(id) || SteamInfo.SystemAppIds.Contains(id);
        }

        private void SteamOrphans()
        {
            var installedDirs = SteamInfo.InstalledDirNames();
            var infoByDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in SteamInfo.AppInfo)
                if (!string.IsNullOrEmpty(kv.Value.InstallDir) && !infoByDir.ContainsKey(kv.Value.InstallDir))
                    infoByDir[kv.Value.InstallDir] = kv.Key.ToString();
            var skipCommon = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Steamworks Shared", "Steam Controller Configs", "SteamVR" };

            foreach (var sa in SteamInfo.SteamAppsDirs)
            {
                foreach (var dir in PathUtil.Dirs(Path.Combine(sa, "common")))
                {
                    string name = Path.GetFileName(dir);
                    if (installedDirs.Contains(name) || skipCommon.Contains(name) || IsClaimedPath(dir)) continue;
                    string id = SteamInfo.AppIdsInFolder(dir, 1).FirstOrDefault();
                    if (id == null) infoByDir.TryGetValue(name, out id);
                    if (id != null && SteamInstalled(id)) continue;
                    var g = id != null ? SteamGroup(id) : NameGroup(name, "Steam", AppKind.Game);
                    g.Roots.Add(dir);
                    Add(g, ItemKind.Directory, ItemCategory.AppFolder, dir, "Cartella rimasta in steamapps\\common di un'app non più installata", Confidence.High);
                }
                foreach (var sub in new[] { "shadercache", @"workshop\content", @"workshop\downloads", @"workshop\temp", "downloading", "temp" })
                {
                    foreach (var dir in PathUtil.Dirs(Path.Combine(sa, sub)))
                    {
                        string id = Path.GetFileName(dir);
                        if (!id.All(char.IsDigit) || SteamInstalled(id)) continue;
                        bool workshop = sub == @"workshop\content";
                        Add(SteamGroup(id), ItemKind.Directory, workshop ? ItemCategory.Other : ItemCategory.Cache, dir,
                            workshop ? L.T("Contenuti del Workshop (mod) di un'app non installata") : sub == "shadercache" ? L.T("Cache degli shader di un'app non installata") : L.T("Download incompleto di Steam"),
                            Confidence.High);
                    }
                }
                foreach (var f in PathUtil.Files(Path.Combine(sa, "workshop"), "appworkshop_*.acf"))
                {
                    string id = Path.GetFileNameWithoutExtension(f).Substring("appworkshop_".Length);
                    if (!SteamInstalled(id)) Add(SteamGroup(id), ItemKind.File, ItemCategory.Other, f, L.T("Elenco Workshop di un'app non installata"), Confidence.High);
                }
            }
            foreach (var ud in SteamInfo.UserDataDirs)
            {
                foreach (var dir in PathUtil.Dirs(ud))
                {
                    string id = Path.GetFileName(dir);
                    if (!id.All(char.IsDigit) || SteamInstalled(id)) continue;
                    Add(SteamGroup(id), ItemKind.Directory, ItemCategory.Saves, dir, L.T("Salvataggi locali di Steam Cloud di un gioco non installato (restano nel cloud di Steam)"), Confidence.Medium);
                }
                foreach (var dir in PathUtil.Dirs(Path.Combine(ud, "760", "remote")))
                {
                    string id = Path.GetFileName(dir);
                    if (id.All(char.IsDigit) && !SteamInstalled(id))
                        Add(SteamGroup(id), ItemKind.Directory, ItemCategory.Media, dir, L.T("Screenshot di Steam di un gioco non installato"), Confidence.Medium);
                }
                foreach (var sub in PathUtil.Dirs(Path.Combine(ud, "gamerecordings")))
                {
                    foreach (var entry in PathUtil.Dirs(sub).Concat(PathUtil.Files(sub)))
                    {
                        var m = Regex.Match(Path.GetFileNameWithoutExtension(entry), @"_(\d{2,10})_");
                        if (m.Success && !SteamInstalled(m.Groups[1].Value))
                            Add(SteamGroup(m.Groups[1].Value), Directory.Exists(entry) ? ItemKind.Directory : ItemKind.File, ItemCategory.Media, entry,
                                L.T("Registrazione di Steam di un gioco non installato"), Confidence.Medium);
                    }
                }
            }
            foreach (var root in InstalledApps.UninstallRoots.Where(r => r.StartsWith("HKLM")))
            {
                using (var rk = RegUtil.Open(root))
                {
                    foreach (var name in RegUtil.SubKeys(rk))
                    {
                        var m = Regex.Match(name, @"^Steam App (\d+)$");
                        if (m.Success && !SteamInstalled(m.Groups[1].Value))
                            Add(SteamGroup(m.Groups[1].Value), ItemKind.RegistryKey, ItemCategory.Registry, root + "\\" + name, L.T("Voce di disinstallazione rimasta nel registro"), Confidence.High);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ Unreal Engine e Unity

        private static readonly HashSet<string> UnrealShared = new HashSet<string>(new[]
        {
            "unrealengine", "crashreportclient", "epicgameslauncher", "unrealenginelauncher", "epiconlineservicesuihelper",
            "unrealbuildtool", "unrealheadertool", "epicgames", "unrealeditor",
        });

        private void EngineOrphans()
        {
            // Unreal Engine: %LOCALAPPDATA%\<Progetto>\Saved\...
            foreach (var dir in PathUtil.Dirs(KnownDirs.LocalAppData))
            {
                ct.ThrowIfCancellationRequested();
                string name = Path.GetFileName(dir);
                string k = NameUtil.Norm(name);
                if (UnrealShared.Contains(k) || Traces.SkipTopLevel.Contains(k)) continue;
                string saved = Path.Combine(dir, "Saved");
                if (!Directory.Exists(saved)) continue;
                bool saves = Directory.Exists(Path.Combine(saved, "SaveGames"));
                if (!saves && !Directory.Exists(Path.Combine(saved, "Config")) && !Directory.Exists(Path.Combine(saved, "Logs"))) continue;
                if (IsClaimedName(name) || IsClaimedPath(dir)) continue;
                var g = NameGroup(name, "Unreal Engine", AppKind.Game);
                if (g.Items.Count == 0) g.RenameFromFolder = true;
                Add(g, ItemKind.Directory, saves ? ItemCategory.Saves : ItemCategory.Settings, dir,
                    L.F("Dati di un gioco Unreal Engine ({0}) che non risulta installato", name), Confidence.Medium);
            }

            // Unity: %USERPROFILE%\AppData\LocalLow\<Azienda>\<Prodotto> (+ PlayerPrefs nel registro)
            var skipCompanies = new HashSet<string>(new[] { "microsoft", "nvidia", "intel", "amd", "sun", "oracle", "adobe", "mozilla", "google", "temp", "unity", "apple", "javasoft", "unitytechnologies" });
            foreach (var company in PathUtil.Dirs(KnownDirs.LocalLow))
            {
                string cName = Path.GetFileName(company);
                if (skipCompanies.Contains(NameUtil.Norm(cName))) continue;
                foreach (var product in PathUtil.Dirs(company))
                {
                    string pName = Path.GetFileName(product);
                    if (IsClaimedName(pName) || IsClaimedPath(product) || IsInstalledUnity(cName, pName)) continue;
                    bool unity = File.Exists(Path.Combine(product, "Player.log")) || File.Exists(Path.Combine(product, "Player-prev.log"))
                        || File.Exists(Path.Combine(product, "output_log.txt")) || Directory.Exists(Path.Combine(product, "Unity"));
                    var g = NameGroup(pName, "Unity", AppKind.Game);
                    g.Company = cName;
                    if (g.Name == NameUtil.CleanDisplayName(pName) && !g.Name.Contains("(")) g.Name = pName + " (" + cName + ")";
                    Add(g, ItemKind.Directory, ItemCategory.Saves, product,
                        unity ? L.T("Dati di un gioco Unity (salvataggi, impostazioni, log) che non risulta installato")
                              : "Dati in AppData\\LocalLow di un'applicazione non installata (probabilmente un gioco Unity)",
                        unity ? Confidence.Medium : Confidence.Low);
                    string reg = @"HKCU\Software\" + cName + "\\" + pName;
                    if (RegUtil.KeyExists(reg)) Add(g, ItemKind.RegistryKey, ItemCategory.Registry, reg, L.T("Preferenze del gioco Unity (PlayerPrefs)"), Confidence.Medium);
                    Add(g, ItemKind.Directory, ItemCategory.Cache, Path.Combine(KnownDirs.Temp, cName, pName), L.T("File temporanei del gioco Unity"), Confidence.Medium);
                    Add(g, ItemKind.Directory, ItemCategory.Cache, Path.Combine(KnownDirs.LocalLow, "Unity", cName + "_" + pName), L.T("Dati diagnostici di Unity"), Confidence.Medium);
                }
            }

            // PlayerPrefs di Unity rimasti solo nel registro (valori con suffisso _h + hash)
            using (var sw = RegUtil.Open(@"HKCU\Software"))
            {
                foreach (var c in RegUtil.SubKeys(sw))
                {
                    if (!Safety.IsDeletableRegistryKey(@"HKCU\Software\" + c + @"\x")) continue;
                    using (var ck = OpenSub(sw, c))
                    {
                        foreach (var pName in RegUtil.SubKeys(ck))
                        {
                            using (var pk = OpenSub(ck, pName))
                            {
                                var values = RegUtil.ValueNames(pk);
                                if (values.Length == 0 || !values.Any(v => Regex.IsMatch(v, @"_h\d{6,}$"))) continue;
                                if (IsClaimedName(pName) || IsInstalledUnity(c, pName)) continue;
                                var g = NameGroup(pName, "Unity", AppKind.Game);
                                g.Company = c;
                                if (!g.Name.Contains("(")) g.Name = pName + " (" + c + ")";
                                Add(g, ItemKind.RegistryKey, ItemCategory.Registry, @"HKCU\Software\" + c + "\\" + pName, L.T("Preferenze di un gioco Unity (PlayerPrefs) non installato"), Confidence.Medium);
                            }
                        }
                    }
                }
            }
        }

        private static Microsoft.Win32.RegistryKey OpenSub(Microsoft.Win32.RegistryKey parent, string name)
        {
            try { return parent == null ? null : parent.OpenSubKey(name); }
            catch { return null; }
        }

        private bool IsInstalledUnity(string company, string product)
        {
            return profiles.Any(p => p.UnityProduct != null && NameUtil.Norm(p.UnityProduct) == NameUtil.Norm(product)
                && (p.UnityCompany == null || NameUtil.Norm(p.UnityCompany) == NameUtil.Norm(company)));
        }

        // ------------------------------------------------------------------ Cartelle di salvataggio

        private void SaveFolderOrphans()
        {
            var ue3 = Path.Combine(KnownDirs.MyGames, "UnrealEngine3");
            foreach (var root in new[] { KnownDirs.MyGames, ue3, KnownDirs.SavedGames })
            {
                foreach (var dir in PathUtil.Dirs(root))
                {
                    string name = Path.GetFileName(dir);
                    if (PathUtil.Same(dir, ue3)) continue;
                    if (PathUtil.Same(root, ue3) && (name.Equals("Binaries", StringComparison.OrdinalIgnoreCase) || name.Equals("Engine", StringComparison.OrdinalIgnoreCase))) continue;
                    if (IsClaimedName(name) || IsClaimedPath(dir)) continue;
                    string where = PathUtil.Same(root, KnownDirs.SavedGames) ? L.T("Giochi salvati") : PathUtil.Same(root, ue3) ? @"My Games\UnrealEngine3" : L.T(@"Documenti\My Games");
                    Add(NameGroup(name, "Salvataggi", AppKind.Game), ItemKind.Directory, ItemCategory.Saves, dir,
                        L.F("Salvataggi in {0} di un gioco che non risulta installato", where), Confidence.Medium);
                }
            }
            // emulatori che salvano con un nome scelto dal crack: GTA_DEF è di "GTA: The Trilogy" se è installato
            var firstWords = new HashSet<string>(entries.Where(e => e.IsGame).Select(e => FirstWord(e.Name)).Where(w => w.Length >= 3), StringComparer.OrdinalIgnoreCase);
            foreach (var root in Traces.NamedEmuSaveRoots())
            {
                foreach (var dir in PathUtil.Dirs(root))
                {
                    string name = Path.GetFileName(dir);
                    if (IsClaimedName(name) || IsClaimedPath(dir) || firstWords.Contains(FirstWord(name))) continue;
                    Add(NameGroup(name, "Salvataggi", AppKind.Game), ItemKind.Directory, ItemCategory.Saves, dir,
                        L.T("Salvataggi di un emulatore usato da una copia non ufficiale, di un gioco che non risulta installato"), Confidence.Medium);
                }
            }
            foreach (var root in Traces.AppIdSaveRoots())
            {
                foreach (var dir in PathUtil.Dirs(root))
                {
                    string id = Path.GetFileName(dir);
                    if (!id.All(char.IsDigit) || installedAppIds.Contains(id)) continue;
                    var g = SteamGroup(id);
                    g.CheckRecency = true;
                    g.Kind = AppKind.Game;
                    if (g.Name.StartsWith("App Steam")) g.Name = L.F("Gioco con AppID {0}", id);
                    Add(g, ItemKind.Directory, ItemCategory.Saves, dir, L.F("Salvataggi legati all'AppID {0} di un gioco non installato", id), Confidence.Medium);
                }
            }
        }

        // ------------------------------------------------------------------ Cartelle in Documenti di giochi non più presenti

        private static readonly HashSet<string> PersonalExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".odt", ".ods", ".rtf" };
        private static readonly Regex GameDataName = new Regex(@"\b(game ?data|savegames?|saved games?|saves)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Documenti è dell'utente: una sua cartella si propone solo con un indizio (file di salvataggio, nome da dati di gioco, stessa sigla
        /// di un programma eliminato), mai se contiene documenti, e sempre «Da verificare»: non viene selezionata da sola.
        /// </summary>
        private void DocumentOrphans()
        {
            var deleted = groups.Values.Where(g => g.Source == L.T("Programma eliminato") || g.Source == L.T("Gioco eliminato")).ToList();
            var firstWords = new HashSet<string>(deleted.Select(g => FirstWord(g.Name)).Where(w => w.Length >= 3), StringComparer.OrdinalIgnoreCase);
            var exeNames = deleted.SelectMany(g => g.ExeNames).Select(Path.GetFileNameWithoutExtension).ToList();
            foreach (var dir in PathUtil.Dirs(KnownDirs.Documents))
            {
                string name = Path.GetFileName(dir);
                if (PathUtil.Same(dir, KnownDirs.MyGames) || name.StartsWith("My ", StringComparison.OrdinalIgnoreCase) || Traces.PublisherFolder.IsMatch(name)
                    || Traces.AppIdSaveRoots().Any(r => PathUtil.Same(r, dir))) continue;
                if (IsClaimedName(name) || IsClaimedPath(dir) || claimedDirs.Any(c => PathUtil.IsUnder(c, dir, true)) || PathUtil.IsReparsePoint(dir)) continue;
                var files = PathUtil.FilesNoLinks(dir).Take(400).ToList();
                if (files.Count == 0 || files.Any(f => PersonalExtensions.Contains(Path.GetExtension(f))) || Directory.Exists(Path.Combine(dir, ".git"))) continue;
                string word = FirstWord(name);
                bool related = word.Length >= 3 && (firstWords.Contains(word) || exeNames.Any(e => e.StartsWith(word, StringComparison.OrdinalIgnoreCase)));
                if (!related && !GameDataName.IsMatch(name) && !files.Any(f => LocalSaves.SaveExtensions.Contains(Path.GetExtension(f)))) continue;
                Add(NameGroup(name, "Salvataggi", AppKind.Game), ItemKind.Directory, ItemCategory.Saves, dir,
                    L.T("Cartella in Documenti: sembra di un gioco non più installato. Controlla il contenuto prima di eliminarla"), Confidence.Low);
            }
        }

        private static string FirstWord(string name)
        {
            var m = Regex.Match(name ?? "", @"[\p{L}\p{N}]+");
            return m.Success ? m.Value : "";
        }

        // ------------------------------------------------------------------ Prefetch: eseguibili di giochi e programmi cancellati

        private void PrefetchOrphans()
        {
            if (!AppSettings.Current.UsePrefetch || !PrefetchIndex.IsAvailable) return;
            var records = PrefetchIndex.Records;
            var deleted = records.Where(r => r.ExePath != null && !r.ExeExists && IsLocalFixedPath(r.ExePath)
                                          && !PathUtil.IsUnder(r.ExePath, KnownDirs.Windows) && !PathUtil.IsUnder(r.ExePath, KnownDirs.Temp))
                                 .GroupBy(r => AppFolderOf(r.ExePath) ?? InstalledApps.GuessRoot(r.ExePath) ?? r.ExePath, StringComparer.OrdinalIgnoreCase);
            foreach (var grp in deleted)
            {
                string root = PathUtil.RealCase(grp.Key);
                if (IsClaimedPath(root)) continue;
                var list = grp.ToList();
                bool game = list.Any(r => r.MarkerHits.Count > 0) || IsUnderGameRoot(list[0].ExePath);
                if (game && Directory.Exists(root) && GameMarkers.Detect(root) != null) continue; // il gioco c'è ancora (non rilevato): non toccare
                if (!game)
                {
                    // programmi: solo cartelle di programmi vere, niente installatori scaricati e poi cancellati
                    if (AppFolderOf(list[0].ExePath) == null || Directory.Exists(root)) continue;
                    list = list.Where(r => !InstalledApps.IsAuxiliaryExe(r.ExePath)).ToList();
                    if (list.Count == 0) continue;
                }
                var g = GroupForExe(list[0].ExePath, true, game);
                if (g == null) continue;
                if (!g.Roots.Any(r => PathUtil.Same(r, root))) g.Roots.Add(root);
                foreach (var r in list) AddExeNames(g, r.ExePath);
                g.HasEvidence = true; // Windows ricorda l'eseguibile, che però non esiste più
                string noun = g.Kind == AppKind.Game ? L.T("un gioco eliminato") : L.T("un programma eliminato");
                var owner = g;
                foreach (var dir in PrefetchIndex.ExclusiveAnchors(list, n => OwnsFolderName(owner, n)))
                {
                    if (IsClaimedPath(dir) || IsClaimedName(Path.GetFileName(dir))) continue;
                    bool local = PathUtil.IsUnder(dir, KnownDirs.LocalAppData) || PathUtil.IsUnder(dir, KnownDirs.RoamingAppData) || PathUtil.IsUnder(dir, KnownDirs.ProgramData);
                    Add(g, ItemKind.Directory, local ? ItemCategory.Settings : ItemCategory.Saves, dir,
                        L.F("Usata solo da {0}, che non esiste più (rilevata dal Prefetch di Windows)", Path.GetFileName(list[0].ExePath)), Confidence.Medium);
                }
            }
            // crash dump degli eseguibili cancellati
            foreach (var g in groups.Values.Where(x => x.ExeNames.Count > 0))
            {
                foreach (var f in PathUtil.Files(KnownDirs.CrashDumps, "*.dmp"))
                    if (g.ExeNames.Any(e => Path.GetFileName(f).StartsWith(e + ".", StringComparison.OrdinalIgnoreCase)))
                        Add(g, ItemKind.File, ItemCategory.Cache, f, L.F("File di crash (dump) di {0}", (g.Kind == AppKind.Game ? L.T("un gioco eliminato") : L.T("un programma eliminato"))), Confidence.High);
            }
        }

        /// <summary>La cartella porta il nome del programma eliminato (eden per eden.exe, Godot per Godot_v4.7.2-stable_win64.exe).</summary>
        private static bool OwnsFolderName(Group g, string folder)
        {
            string k = NameUtil.Norm(folder);
            if (k.Length < 4 || NameUtil.IsGeneric(k, AppKind.Program)) return false;
            if (NameUtil.Norm(g.Name) == k) return true;
            return g.ExeNames.Any(e => { string n = NameUtil.Norm(Path.GetFileNameWithoutExtension(e)); return n == k || n.StartsWith(k); });
        }

        // ------------------------------------------------------------------ Tracce nel registro di eseguibili che non esistono più

        private void RegistryTraceOrphans()
        {
            var traces = Traces.ExePathValues().Concat(Traces.GameConfigStore()).Concat(Traces.AppPaths()).Concat(Traces.NotificationSettings()).ToList();
            var firewall = Traces.FirewallPrograms().Select(e => new RegTrace { KeyPath = e, ExePath = e, What = L.T("regole di Windows Firewall") });
            foreach (var t in traces.Concat(firewall))
            {
                string exe = PathUtil.Normalize(t.ExePath);
                if (!IsGone(exe)) continue;
                // Game Bar e le preferenze della GPU riguardano quasi sempre i giochi
                bool gameLike = t.KeyPath.IndexOf("GameConfigStore", StringComparison.OrdinalIgnoreCase) >= 0
                    || t.KeyPath.IndexOf("UserGpuPreferences", StringComparison.OrdinalIgnoreCase) >= 0;
                var g = GroupOrCatchAll(exe, gameLike, "traces", L.T("Tracce di programmi eliminati"), "Registro");
                g.HasEvidence = true;
                if (!g.Key.StartsWith("x:")) AddExeNames(g, exe);
                string reason = L.F("Traccia di Windows ({0}) di {1}, che non esiste più", t.What, Path.GetFileName(exe));
                if (t.What == L.T("regole di Windows Firewall"))
                    Add(g, ItemKind.FirewallRule, ItemCategory.Registry, exe, reason, Confidence.High);
                else if (t.ValueName != null)
                    Add(g, ItemKind.RegistryValue, ItemCategory.Registry, t.KeyPath, reason, Confidence.High, t.ValueName);
                else
                    Add(g, ItemKind.RegistryKey, ItemCategory.Registry, t.KeyPath, reason, Confidence.High);
            }
        }

        // ------------------------------------------------------------------ Collegamenti non validi

        private void ShortcutOrphans()
        {
            var epicInstalled = entries.Where(x => x.EpicAppName != null).Select(x => x.EpicAppName).ToList();
            foreach (var s in Traces.Shortcuts())
            {
                if (s.Target != null)
                {
                    if (!IsGone(s.Target)) continue;
                    // i collegamenti "pubblicizzati" di Windows Installer puntano a C:\Windows\Installer anche quando il programma c'è
                    var owner = GroupForExe(s.Target, true);
                    Group g;
                    if (owner != null)
                    {
                        owner.HasEvidence = true; // collegamento al gioco o programma, che però non c'è più
                        owner.Names.Add(Path.GetFileNameWithoutExtension(s.File));
                        g = owner;
                    }
                    else g = IsUnderGameRoot(s.Target) ? CatchAll("shortcuts-giochi", L.T("Collegamenti a giochi eliminati"), "Collegamenti", AppKind.Game)
                                                        : CatchAll("shortcuts", L.T("Collegamenti non validi"), "Collegamenti");
                    Add(g, ItemKind.File, ItemCategory.Shortcuts, s.File, L.F("Collegamento a {0}, che non esiste più", s.Target), Confidence.High);
                    continue;
                }
                if (s.Url == null) continue;
                string sid = Traces.SteamIdFromUrl(s.Url);
                if (sid != null && !SteamInstalled(sid))
                {
                    var g = SteamGroup(sid);
                    Add(g, ItemKind.File, ItemCategory.Shortcuts, s.File, L.T("Collegamento Steam a un'app non installata"), Confidence.High);
                    if (s.IconFile != null && SteamInfo.SteamPath != null && PathUtil.IsUnder(s.IconFile, Path.Combine(SteamInfo.SteamPath, "steam", "games")))
                        Add(g, ItemKind.File, ItemCategory.Cache, s.IconFile, L.T("Icona del collegamento creata da Steam"), Confidence.High);
                }
                else if (s.Url.StartsWith("com.epicgames.launcher://apps/", StringComparison.OrdinalIgnoreCase)
                         && !epicInstalled.Any(a => s.Url.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    Add(CatchAll("shortcuts-giochi", L.T("Collegamenti a giochi eliminati"), "Collegamenti", AppKind.Game), ItemKind.File, ItemCategory.Shortcuts, s.File,
                        L.T("Collegamento Epic Games a un gioco non installato"), Confidence.High);
                }
            }
        }

        // ------------------------------------------------------------------ Voci di disinstallazione non valide

        private void UninstallOrphans()
        {
            foreach (var e in InstalledApps.ReadUninstallEntries())
            {
                if (e.IsSystemOrUpdate || string.IsNullOrWhiteSpace(e.DisplayName)) continue;
                if (InstalledApps.SteamAppKey.IsMatch(e.KeyName)) continue; // gestite con Steam
                string exe = PathUtil.Normalize(PathUtil.ExeFromCommand(e.UninstallString));
                if (!IsGone(exe)) continue;
                string loc = PathUtil.Normalize(e.InstallLocation);
                if (loc != null && Directory.Exists(loc)) continue;
                string dir = loc ?? Path.GetDirectoryName(exe);
                if (IsClaimedPath(dir)) continue;
                bool gameLike = IsUnderGameRoot(dir) || Regex.IsMatch(e.DisplayName, @"(ElAmigos|FitGirl|DODI|MULTi\d|Repack)", RegexOptions.IgnoreCase)
                    || e.KeyName.StartsWith("Uplay Install", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(e.KeyName, @"^\d{8,}_is1$");
                var g = GroupForExe(loc != null ? Path.Combine(loc, "_") : exe, false, gameLike);
                string clean = NameUtil.StripVersion(NameUtil.CleanDisplayName(e.DisplayName), e.DisplayVersion);
                if (g == null && !IsClaimedName(clean) && !IsClaimedName(NameUtil.BaseProgramName(clean)))
                    g = NameGroup(clean, gameLike ? L.T("Gioco eliminato") : L.T("Programma eliminato"), gameLike ? AppKind.Game : AppKind.Program);
                if (g == null) continue;
                if (gameLike) MarkGame(g);
                g.HasEvidence = true;
                g.Names.Add(clean);
                if (g.Company == null && !string.IsNullOrWhiteSpace(e.Publisher)) g.Company = e.Publisher;
                if (loc != null && !g.Roots.Any(r => PathUtil.Same(r, loc))) g.Roots.Add(loc);
                Add(g, ItemKind.RegistryKey, ItemCategory.Registry, e.KeyPath,
                    L.F("Voce in \"App installate\" per «{0}», ma il programma di disinstallazione non esiste più", e.DisplayName), Confidence.High);
            }
        }

        // ------------------------------------------------------------------ Avvio automatico, attività, servizi

        private void AutostartOrphans()
        {
            var runNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in Traces.RunEntries())
            {
                runNames.Add(t.ValueName);
                if (!IsGone(t.ExePath)) continue;
                var g = GroupOrCatchAll(t.ExePath, false, "startup", L.T("Voci di avvio automatico non valide"), "Avvio automatico");
                g.HasEvidence = true;
                Add(g, ItemKind.RegistryValue, ItemCategory.Startup, t.KeyPath, L.F("Avvio automatico di {0}, che non esiste più", Path.GetFileName(t.ExePath)), Confidence.High, t.ValueName);
                foreach (var s in Traces.StartupApproved(t.ValueName))
                    Add(g, ItemKind.RegistryValue, ItemCategory.Startup, s.KeyPath, L.T("Stato dell'avvio automatico di un programma che non esiste più"), Confidence.High, s.ValueName);
            }
            // stato "attivo/disattivato" di voci di avvio che non esistono più
            var startupFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in new[] { Path.Combine(KnownDirs.StartMenu, "Startup"), Path.Combine(KnownDirs.CommonStartMenu, "Startup") })
                foreach (var f in PathUtil.Files(dir)) startupFiles.Add(Path.GetFileName(f));
            foreach (var s in Traces.AllStartupApproved())
            {
                bool folder = s.KeyPath.EndsWith("StartupFolder", StringComparison.OrdinalIgnoreCase);
                if (folder ? startupFiles.Contains(s.ValueName) : runNames.Contains(s.ValueName)) continue;
                if (folder && runNames.Contains(s.ValueName)) continue;
                Add(CatchAll("startup", L.T("Voci di avvio automatico non valide"), "Avvio automatico"), ItemKind.RegistryValue, ItemCategory.Startup, s.KeyPath,
                    L.F("Stato dell'avvio automatico di «{0}», che non esiste più", s.ValueName), Confidence.Medium, s.ValueName);
            }
        }

        private void TaskOrphans()
        {
            foreach (var t in Traces.ScheduledTasks())
            {
                if (t.Exes.Count == 0 || !t.Exes.All(IsGone)) continue;
                var g = GroupOrCatchAll(t.Exes[0], false, "tasks", L.T("Attività pianificate non valide"), "Attività pianificate");
                g.HasEvidence = true;
                var item = Add(g, ItemKind.ScheduledTask, ItemCategory.Startup, t.Path, L.F("Attività pianificata che avvia {0}, che non esiste più", Path.GetFileName(t.Exes[0])), Confidence.High);
                if (item != null) item.NeedsAdmin = t.NeedsAdmin;
            }
        }

        private void ServiceOrphans()
        {
            foreach (var s in Traces.Services())
            {
                if (!IsGone(s.ExePath)) continue;
                var g = GroupOrCatchAll(s.ExePath, false, "services", L.T("Servizi non validi"), "Servizi");
                g.HasEvidence = true;
                Add(g, ItemKind.Service, ItemCategory.Startup, s.Name, L.F("Servizio di Windows che avvia {0}, che non esiste più", Path.GetFileName(s.ExePath)), Confidence.High, null, s.DisplayName);
            }
        }

        // ------------------------------------------------------------------ Associazioni di file, menu contestuali, componenti COM

        private void ClassOrphans()
        {
            const string catchKey = "assoc", catchName = "Associazioni di file e menu non validi", catchSource = "Registro";
            var removedProgIds = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
            foreach (var grp in Traces.ClassCommands().GroupBy(t => t.KeyPath, StringComparer.OrdinalIgnoreCase))
            {
                var list = grp.ToList();
                // tutti i comandi puntano a file spariti (e sparita è anche la loro cartella)
                if (!list.All(t => IsGone(t.ExePath) && !Directory.Exists(Path.GetDirectoryName(t.ExePath) ?? ""))) continue;
                var first = list[0];
                var g = GroupOrCatchAll(first.ExePath, false, catchKey, catchName, catchSource);
                g.HasEvidence = true;
                bool progId = first.What.StartsWith("tipo");
                Add(g, ItemKind.RegistryKey, ItemCategory.Registry, first.KeyPath,
                    L.F("{0} di un programma che non esiste più ({1})", (progId ? L.F("Tipo di file «{0}»", first.Tag) : L.F("Registrazione \"Apri con\" di {0}", first.Tag)), Path.GetFileName(first.ExePath)), Confidence.High);
                if (progId) removedProgIds[first.Tag] = g;
            }
            // riferimenti "Apri con" a tipi di file che non esistono (o che stanno per essere eliminati)
            foreach (var t in Traces.AllOpenWithProgIds())
            {
                Group owner;
                if (removedProgIds.TryGetValue(t.Tag, out owner))
                    Add(owner, ItemKind.RegistryValue, ItemCategory.Registry, t.KeyPath,
                        L.F("Riferimento al tipo di file «{0}», che viene eliminato ({1})", t.Tag, t.What), Confidence.High, t.ValueName);
                else if (!Traces.ProgIdExists(t.Tag))
                    Add(CatchAll(catchKey, catchName, catchSource), ItemKind.RegistryValue, ItemCategory.Registry, t.KeyPath,
                        L.F("Riferimento al tipo di file «{0}», che non esiste più ({1})", t.Tag, t.What), Confidence.Medium, t.ValueName);
            }
            foreach (var t in Traces.ShellVerbs())
            {
                if (!IsGone(t.ExePath)) continue;
                var g = GroupOrCatchAll(t.ExePath, false, catchKey, catchName, catchSource);
                g.HasEvidence = true;
                Add(g, ItemKind.RegistryKey, ItemCategory.Registry, t.KeyPath, L.F("Voce «{0}» nel menu contestuale: avvia {1}, che non esiste più", t.Tag, Path.GetFileName(t.ExePath)), Confidence.High);
            }
            // componenti COM il cui file non esiste più, insieme alla loro cartella
            var clsidGroups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in Traces.ComServers())
            {
                if (!IsGone(t.ExePath) || Directory.Exists(Path.GetDirectoryName(t.ExePath) ?? "")) continue;
                var g = GroupOrCatchAll(t.ExePath, false, catchKey, catchName, catchSource);
                g.HasEvidence = true;
                Add(g, ItemKind.RegistryKey, ItemCategory.Registry, t.KeyPath, L.F("Componente COM di {0}, che non esiste più", Path.GetFileName(t.ExePath)), Confidence.High);
                clsidGroups[t.Tag] = g;
            }
            foreach (var t in Traces.ShellExtensionRefs(clsidGroups.Keys))
            {
                Group g;
                if (!clsidGroups.TryGetValue(t.Tag, out g)) continue;
                if (t.ValueName != null) Add(g, ItemKind.RegistryValue, ItemCategory.Registry, t.KeyPath, L.F("Estensione di un programma che non esiste più: {0}", t.What), Confidence.High, t.ValueName);
                else Add(g, ItemKind.RegistryKey, ItemCategory.Registry, t.KeyPath, L.F("Estensione di un programma che non esiste più: {0}", t.What), Confidence.High);
            }
            foreach (var t in Traces.RegisteredApplications())
            {
                // Tag = chiave Capabilities (cercata in entrambe le viste del registro), null se non esiste più
                bool broken = t.Tag == null || (t.ExePath != null && IsGone(t.ExePath));
                if (!broken) continue;
                var g = t.ExePath != null ? GroupOrCatchAll(t.ExePath, false, catchKey, catchName, catchSource) : CatchAll(catchKey, catchName, catchSource);
                Add(g, ItemKind.RegistryValue, ItemCategory.Registry, t.KeyPath, L.F("Registrazione tra le app predefinite di «{0}», che non esiste più", t.ValueName), Confidence.High, t.ValueName);
            }
            foreach (var t in Traces.Clients())
            {
                if (!IsGone(t.ExePath)) continue;
                var g = GroupOrCatchAll(t.ExePath, false, catchKey, catchName, catchSource);
                Add(g, ItemKind.RegistryKey, ItemCategory.Registry, t.KeyPath, L.F("Traccia di Windows ({0}) di {1}, che non esiste più", t.What, Path.GetFileName(t.ExePath)), Confidence.High);
            }
        }

        // ------------------------------------------------------------------ Variabile PATH

        private void PathOrphans()
        {
            foreach (var e in Traces.PathEntries())
            {
                if (e.Expanded == null || !IsGone(e.Expanded)) continue;
                var g = GroupOrCatchAll(Path.Combine(e.Expanded, "_"), false, "path", L.T("Cartelle inesistenti nella variabile PATH"), "Variabili d'ambiente");
                g.HasEvidence = true;
                Add(g, ItemKind.PathEntry, ItemCategory.Registry, e.KeyPath, L.T("Cartella nella variabile PATH che non esiste più"), Confidence.High, e.ValueName, e.Raw);
            }
        }

        // ------------------------------------------------------------------ Cartelle rimaste in Programmi

        private static readonly Regex BinaryFile = new Regex(@"\.(exe|dll|sys|msi|jar|com|scr|ocx|cpl|node|pyd|so)$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Cartelle in Programmi (e AppData\Local\Programs) che non contengono più alcun eseguibile e non appartengono
        /// a nessun programma installato: restano dopo le disinstallazioni con log, impostazioni o file vuoti.
        /// </summary>
        private void ProgramFilesOrphans()
        {
            foreach (var root in new[] { KnownDirs.ProgramFiles, KnownDirs.ProgramFilesX86, KnownDirs.LocalPrograms })
            {
                foreach (var d1 in PathUtil.Dirs(root))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!ConsiderProgramFolder(d1)) continue;
                    // cartella dell'editore con più prodotti: si guarda dentro
                    var subs = PathUtil.Dirs(d1);
                    bool vendor = subs.Length > 0 && PathUtil.Files(d1).Length == 0;
                    if (vendor && subs.Any(s => IsClaimedPath(s) || ContainsBinaries(s)))
                    {
                        foreach (var d2 in subs) if (ConsiderProgramFolder(d2)) AddProgramFolder(d2, Path.GetFileName(d1));
                        continue;
                    }
                    AddProgramFolder(d1, null);
                }
            }
        }

        private bool ConsiderProgramFolder(string dir)
        {
            string name = Path.GetFileName(dir);
            if (Traces.SkipTopLevel.Contains(NameUtil.Norm(name)) || PathUtil.IsReparsePoint(dir)) return false;
            if (NameUtil.IsGeneric(NameUtil.Norm(name), AppKind.Program)) return false; // es. AppData\Local\Programs\Common (cartella di Windows)
            if (name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) return false;
            if (IsClaimedPath(dir) || IsClaimedName(name)) return false;
            return Safety.IsDeletablePath(dir);
        }

        private void AddProgramFolder(string dir, string vendor)
        {
            if (ContainsBinaries(dir)) return;
            int files;
            DateTime newest;
            PathUtil.Size(dir, out files, out newest, ct);
            string name = Path.GetFileName(dir);
            var g = GroupForFolder(dir) ?? NameGroup(name, L.T("Programma eliminato"), AppKind.Program);
            if (!g.Roots.Any(r => PathUtil.Same(r, dir))) g.Roots.Add(dir);
            if (vendor != null && g.Company == null) g.Company = vendor;
            g.HasEvidence = true; // un programma senza più eseguibili non c'è più
            bool recent = newest > DateTime.Now.AddDays(-7);
            var conf = recent ? Confidence.Low : files == 0 ? Confidence.High : Confidence.Medium;
            Add(g, ItemKind.Directory, ItemCategory.AppFolder, dir,
                L.F("{0} di un programma non più installato{1}", (files == 0 ? L.T("Cartella vuota") : L.T("Cartella senza più programmi (solo dati, log o impostazioni)")), (recent ? L.T(". Attenzione: modificata di recente, potrebbe essere un'installazione in corso") : "")), conf);
        }

        /// <summary>
        /// True se nella cartella c'è almeno un eseguibile o una libreria, oppure se non si può dire con certezza:
        /// troppo grande, sottocartelle non leggibili o collegamenti verso altre posizioni.
        /// </summary>
        private static bool ContainsBinaries(string dir)
        {
            try
            {
                int n = 0;
                var stack = new Stack<string>();
                stack.Push(dir);
                while (stack.Count > 0)
                {
                    string cur = stack.Pop();
                    foreach (var f in Directory.GetFiles(cur))
                        if (BinaryFile.IsMatch(f) || ++n > 5000) return true;
                    foreach (var d in Directory.GetDirectories(cur))
                    {
                        if (PathUtil.IsReparsePoint(d)) return true;
                        stack.Push(d);
                    }
                }
                return false;
            }
            catch { return true; }
        }

        // ------------------------------------------------------------------ Dati dei programmi eliminati (per nome)

        /// <summary>
        /// Per i programmi di cui c'è la prova della cancellazione: cartelle dati e chiavi di registro con lo stesso nome
        /// (o con il nome del prodotto indicato dall'eseguibile), purché nessun programma installato lo usi.
        /// </summary>
        private void ProgramDataOrphans()
        {
            var targets = groups.Values.Where(g => g.Kind == AppKind.Program && g.HasEvidence && !g.Key.StartsWith("x:")).ToList();
            if (targets.Count == 0) return;
            var byKey = new Dictionary<string, Group>();
            var companies = new Dictionary<string, Group>();
            foreach (var g in targets)
            {
                // nomi certi: quello del gruppo e delle sue cartelle
                var own = new[] { g.Name, NameUtil.BaseProgramName(g.Name) }.Concat(g.Roots.Select(r => NameUtil.CleanFolderTitle(Path.GetFileName(r))));
                // nomi ricordati da Windows (nome del prodotto, collegamenti, voci di disinstallazione): possono essere generici
                // ("Electron", "Python", "Java"), quindi si usa il filtro più severo
                foreach (var n in own.Select(x => Tuple.Create(x, AppKind.Program)).Concat(g.Names.Select(x => Tuple.Create(x, AppKind.Game))))
                {
                    string k = NameUtil.Norm(n.Item1);
                    if (k.Length < 5 || NameUtil.IsGeneric(k, n.Item2) || claimedKeys.Contains(k) || byKey.ContainsKey(k)) continue;
                    byKey[k] = g;
                }
                string c = g.Company == null ? "" : NameUtil.Norm(Regex.Replace(g.Company, @"[\s,\.]+(inc|ltd|llc|gmbh|corp|corporation|limited|co)\.?\s*$", "", RegexOptions.IgnoreCase));
                if (c.Length >= 4 && !NameUtil.IsGeneric(c, AppKind.Program) && !claimedKeys.Contains(c) && !companies.ContainsKey(c)) companies[c] = g;
            }
            if (byKey.Count == 0) return;
            foreach (var root in new[]
            {
                Tuple.Create(KnownDirs.RoamingAppData, @"AppData\Roaming"), Tuple.Create(KnownDirs.LocalAppData, @"AppData\Local"),
                Tuple.Create(KnownDirs.LocalLow, @"AppData\LocalLow"), Tuple.Create(KnownDirs.ProgramData, "ProgramData"),
                Tuple.Create(KnownDirs.Documents, "Documenti"),
            })
            {
                foreach (var d1 in PathUtil.Dirs(root.Item1))
                {
                    string n1 = Path.GetFileName(d1), k1 = NameUtil.Norm(n1);
                    if (Traces.SkipTopLevel.Contains(k1) || PathUtil.IsReparsePoint(d1) || IsClaimedPath(d1)) continue;
                    Group g;
                    if (byKey.TryGetValue(k1, out g))
                    {
                        // in Documenti possono esserci file creati dall'utente: si propongono, ma da verificare
                        bool documents = root.Item1 == KnownDirs.Documents;
                        Add(g, ItemKind.Directory, documents ? ItemCategory.Saves : ItemCategory.Settings, d1,
                            L.F("Cartella «{0}» in {1}: ha il nome di un programma che non esiste più{2}", n1, root.Item2, (documents ? L.T(". Controlla che non contenga tuoi documenti") : "")), documents ? Confidence.Low : Confidence.Medium);
                        continue;
                    }
                    // cartella dell'editore: si cercano i prodotti al suo interno
                    Group owner;
                    bool company = companies.TryGetValue(k1, out owner);
                    foreach (var d2 in PathUtil.Dirs(d1))
                    {
                        string n2 = Path.GetFileName(d2), k2 = NameUtil.Norm(n2);
                        if (!byKey.TryGetValue(k2, out g) || IsClaimedPath(d2)) continue;
                        if (!company && k2.Length < 8) continue;
                        Add(g, ItemKind.Directory, ItemCategory.Settings, d2,
                            L.F("Cartella «{0}\\{1}» in {2}: ha il nome di un programma che non esiste più", n1, n2, root.Item2), Confidence.Medium);
                    }
                }
            }
            foreach (var root in new[] { @"HKCU\Software", @"HKLM\SOFTWARE", @"HKLM\SOFTWARE\WOW6432Node" })
            {
                using (var rk = RegUtil.Open(root))
                {
                    foreach (var k1 in RegUtil.SubKeys(rk))
                    {
                        string n1 = NameUtil.Norm(k1);
                        Group g;
                        if (byKey.TryGetValue(n1, out g))
                        {
                            Add(g, ItemKind.RegistryKey, ItemCategory.Registry, root + "\\" + k1, L.F("Chiave «{0}» con il nome di un programma che non esiste più", k1), Confidence.Medium);
                            continue;
                        }
                        Group owner;
                        bool company = companies.TryGetValue(n1, out owner);
                        using (var k = OpenSub(rk, k1))
                        {
                            foreach (var k2 in RegUtil.SubKeys(k))
                            {
                                string n2 = NameUtil.Norm(k2);
                                if (!byKey.TryGetValue(n2, out g) || (!company && n2.Length < 8)) continue;
                                Add(g, ItemKind.RegistryKey, ItemCategory.Registry, root + "\\" + k1 + "\\" + k2, L.F("Chiave «{0}\\{1}» con il nome di un programma che non esiste più", k1, k2), Confidence.Medium);
                            }
                        }
                    }
                }
            }
        }

        // ------------------------------------------------------------------ Cache degli shader dei driver

        private void ShaderCaches()
        {
            var g = new Group { Key = "x:shader", Name = L.T("Cache degli shader dei driver grafici"), Source = "Driver video", CheckRecency = false, Kind = AppKind.Game };
            groups[g.Key] = g;
            string why = L.T("Si rigenera da sola: i giochi potrebbero caricare più lentamente al primo avvio");
            foreach (var dir in LogJanitor.ShaderCacheDirs())
                Add(g, ItemKind.Directory, ItemCategory.Cache, dir, why, Confidence.Low);
        }

        // ------------------------------------------------------------------ Rifinitura

        /// <summary>
        /// Un "programma" eliminato è in realtà un gioco se il suo nome è quello di un gioco noto a Steam (sul PC)
        /// o già riconosciuto online, oppure se le sue cartelle hanno le sigle di repack e trainer.
        /// </summary>
        private void ReclassifyGames()
        {
            var steamGames = SteamInfo.GameNameKeys();
            foreach (var g in groups.Values.Where(x => x.Kind == AppKind.Program && !x.Key.StartsWith("x:")))
            {
                var names = new[] { g.Name }.Concat(g.Names).Concat(g.Roots.Select(r => NameUtil.CleanFolderTitle(Path.GetFileName(r)))).ToList();
                bool game = g.Roots.Any(r => NameUtil.GameReleaseTags.IsMatch(Path.GetFileName(r)))
                    || names.Any(n => { string k = NameUtil.Norm(n); return k.Length >= 4 && (steamGames.Contains(k) || CoverService.IsKnownGame(k)); });
                if (game)
                {
                    g.Kind = AppKind.Game;
                    if (g.Source == L.T("Programma eliminato")) g.Source = L.T("Gioco eliminato");
                }
            }
        }

        /// <summary>
        /// Unisce i gruppi dello stesso titolo: omonimi, oppure varianti di un titolo più corto
        /// ("Crimson Desert Update...", "InfinityCloud - Copia", "LemmingBallZ ALPHA" → il gruppo del titolo principale).
        /// </summary>
        private void MergeSameTitle()
        {
            var candidates = groups.Values.Where(x => !x.Key.StartsWith("x:")).OrderBy(x => NameUtil.Norm(x.Name).Length).ToList();
            for (int i = 0; i < candidates.Count; i++)
            {
                var main = candidates[i];
                if (!groups.ContainsKey(main.Key)) continue;
                string mk = NameUtil.Norm(main.Name);
                if (mk.Length < 7) continue;
                for (int j = i + 1; j < candidates.Count; j++)
                {
                    var other = candidates[j];
                    if (other == main || !groups.ContainsKey(other.Key)) continue;
                    string ok = NameUtil.Norm(other.Name);
                    // stesso titolo: sempre lo stesso gioco o programma; varianti del titolo: solo se dello stesso tipo
                    if (ok == mk || (ok.StartsWith(mk) && other.Kind == main.Kind)) main = Merge(main, other);
                }
            }
        }

        private List<LeftoverItem> Finish()
        {
            // "Giochi salvati\Sviluppatore" di un unico gioco Unity orfano dello stesso sviluppatore → stesso gruppo
            foreach (var sg in groups.Values.Where(x => x.Source == "Salvataggi").ToList())
            {
                string k = sg.Key.Substring(2);
                var unity = groups.Values.Where(x => x.Company != null && x.Kind == AppKind.Game && NameUtil.Norm(x.Company) == k).ToList();
                if (unity.Count == 1) Merge(unity[0], sg);
            }

            // unisce i gruppi Steam con i gruppi omonimi trovati per nome
            foreach (var sg in groups.Values.Where(x => x.Key.StartsWith("s:")).ToList())
            {
                Group ng;
                if (groups.TryGetValue("n:" + NameUtil.Norm(sg.Name), out ng) && ng != sg) Merge(sg, ng);
            }

            ReclassifyGames();
            MergeSameTitle();

            var all = new List<LeftoverItem>();
            var excluded = new List<string>();
            foreach (var g in groups.Values)
            {
                foreach (var i in g.Items)
                {
                    i.GroupKey = g.Key;
                    i.GroupName = g.Name;
                    i.GroupSource = g.Source;
                    i.GroupImage = g.Image;
                    i.GroupKind = g.Kind;
                }
                all.AddRange(g.Items.Where(i =>
                {
                    if (Safety.IsDeletable(i)) return true;
                    // i dati di AppManager stesso non sono residui: nessuna nota nel registro
                    if (!(i.IsFileSystem && Safety.IsOwnData(i.Path))) excluded.Add(i.DisplayPath);
                    return false;
                }));
            }
            // una sola riga per analisi (prima erano decine di righe identiche a ogni analisi)
            if (excluded.Count > 0)
                Log.Info("Residui orfani esclusi per sicurezza: {0}. {1}{2}", excluded.Count,
                    string.Join(" · ", excluded.Take(3)), excluded.Count > 3 ? " · …" : "");

            var sorted = all.OrderBy(i => i.Path.Length).ToList();
            var result = new List<LeftoverItem>();
            foreach (var i in sorted)
            {
                bool covered = result.Any(r => r.Kind == ItemKind.Directory && i.IsFileSystem && PathUtil.IsUnder(i.Path, r.Path))
                    || result.Any(r => r.Kind == ItemKind.RegistryKey && i.IsRegistry && (PathUtil.IsUnder(i.Path, r.Path) || (i.Kind == ItemKind.RegistryValue && PathUtil.Same(i.Path, r.Path))));
                if (!covered) result.Add(i);
            }

            var recent = DateTime.Now.AddDays(-30);
            foreach (var i in result)
            {
                ct.ThrowIfCancellationRequested();
                if (!i.IsFileSystem) continue;
                int count;
                DateTime newest;
                i.Size = PathUtil.Size(i.Path, out count, out newest, ct);
                i.FileCount = count;
                i.LastWrite = newest == DateTime.MinValue ? (DateTime?)null : newest;
                Group g;
                // "da verificare" solo se non c'è alcuna prova che il gioco o programma sia stato cancellato:
                // dati usati di recente potrebbero appartenere a qualcosa installato altrove
                if (groups.TryGetValue(i.GroupKey, out g) && g.CheckRecency && !g.HasEvidence && newest > recent
                    && (i.Category == ItemCategory.Saves || i.Category == ItemCategory.Settings) && i.Confidence > Confidence.Low)
                {
                    i.Confidence = Confidence.Low;
                    i.Reason += L.F(". Attenzione: modificata di recente, verifica che {0} non sia ancora installato altrove", (g.Kind == AppKind.Game ? L.T("il gioco") : L.T("il programma")));
                }
            }
            // con la prova che il gioco o programma è stato cancellato, anche i suoi elementi incerti vanno eliminati
            foreach (var i in result)
            {
                Group g;
                if (i.Confidence == Confidence.Low && groups.TryGetValue(i.GroupKey, out g) && g.HasEvidence && !g.Key.StartsWith("x:")
                    && i.Reason.IndexOf(L.T("installazione in corso"), StringComparison.Ordinal) < 0
                    && !(g.Kind == AppKind.Program && (PathUtil.IsUnder(i.Path, KnownDirs.Documents) || PathUtil.IsUnder(i.Path, KnownDirs.PublicDocuments))))
                {
                    i.Confidence = Confidence.Medium;
                    i.Reason += g.Kind == AppKind.Game ? L.T(" (il gioco risulta cancellato)") : L.T(" (il programma risulta cancellato)");
                }
            }
            return result;
        }
    }
}
