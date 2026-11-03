using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AppManager
{
    /// <summary>Cerca tutto ciò che un gioco o un programma ha lasciato nel sistema.</summary>
    internal sealed class LeftoverScanner
    {
        private readonly AppProfile p;
        private readonly List<AppProfile> others;
        private readonly bool afterUninstall;
        private readonly CancellationToken ct;
        private readonly Action<string> progress;
        private readonly Dictionary<string, LeftoverItem> items = new Dictionary<string, LeftoverItem>();
        private readonly List<string> ownDirs;
        private readonly List<string> otherDirs;
        private readonly string noun;

        private LeftoverScanner(AppProfile profile, IEnumerable<AppProfile> others, bool afterUninstall, Action<string> progress, CancellationToken ct)
        {
            p = profile;
            this.others = others.Where(o => o != null && o.Entry.Id != profile.Entry.Id).ToList();
            this.afterUninstall = afterUninstall;
            this.progress = progress;
            this.ct = ct;
            noun = profile.Entry.Noun;
            ownDirs = profile.Entry.Dirs.ToList();
            otherDirs = this.others.SelectMany(o => o.Entry.Dirs).ToList();
        }

        /// <param name="afterUninstall">True dopo la disinstallazione: include la cartella, le voci di disinstallazione e i servizi.</param>
        public static List<LeftoverItem> Scan(AppProfile profile, IEnumerable<AppProfile> others, bool afterUninstall,
            Action<string> progress, CancellationToken ct)
        {
            var s = new LeftoverScanner(profile, others, afterUninstall, progress, ct);
            s.Run();
            return s.Finish();
        }

        private void Run()
        {
            bool game = p.IsGame;
            Step(L.T("cartella di installazione"), AppFolder);
            Step(L.T("dati di Steam"), SteamData);
            if (game) Step(L.T("salvataggi per AppID"), AppIdSaves);
            Step(L.F("cartelle con il nome del {0}", noun), NamedFolders);
            if (game) Step(L.T("dati del motore di gioco"), EngineData);
            if (game) Step(L.T("sigla del gioco in Giochi salvati"), SavedGamesAcronym);
            if (game) Step(L.T("cartelle indicate da PCGamingWiki"), OnlineFolders);
            if (game) Step(L.T("cartelle dei titoli di una raccolta"), CollectionFolders);
            Step(L.T("registro di sistema"), RegistryKeys);
            Step(L.T("tracce di Windows"), ExeTraces);
            Step(L.T("avvio automatico"), Autostart);
            Step(L.T("attività pianificate"), Tasks);
            Step(L.T("servizi"), ServicesStep);
            if (!game) Step(L.T("associazioni dei file e menu di Esplora risorse"), Associations);
            if (!game) Step(L.T("variabile PATH"), PathStep);
            Step(L.T("crash e report di errore"), CrashData);
            Step(L.T("collegamenti"), ShortcutsStep);
            if (game) Step(L.T("screenshot e video"), MediaStep);
            Step(L.T("Prefetch di Windows"), PrefetchStep);
        }

        private void Step(string what, Action action)
        {
            ct.ThrowIfCancellationRequested();
            if (progress != null) progress(L.F("Analisi: {0}…", what));
            try { action(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Error(L.F("Scansione ({0}) di {1}", what, p.Entry.Name), ex); }
        }

        /// <summary>Affidabilità delle tracce di sistema: certe dopo la disinstallazione, da verificare prima.</summary>
        private Confidence TraceConfidence { get { return afterUninstall ? Confidence.High : Confidence.Low; } }

        // ------------------------------------------------------------------ Aggiunta e confronto

        private LeftoverItem Add(ItemKind kind, ItemCategory cat, string path, string reason, Confidence conf, string valueName = null, string data = null)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (kind == ItemKind.Directory && !Directory.Exists(path)) return null;
            if (kind == ItemKind.File && !File.Exists(path)) return null;
            if (kind == ItemKind.Directory || kind == ItemKind.File)
            {
                path = PathUtil.Normalize(path);
                // prima della disinstallazione non si propone nulla dentro la cartella del gioco o programma
                if (!afterUninstall && ownDirs.Any(d => PathUtil.IsUnder(path, d, true))) return null;
                if (!afterUninstall && p.Entry.IsSingleFile && PathUtil.Same(path, p.Entry.MainExe)) return null;
            }
            var item = new LeftoverItem { Kind = kind, Category = cat, Path = path, ValueName = valueName, Data = data, Reason = reason, Confidence = conf };
            LeftoverItem existing;
            if (items.TryGetValue(item.Key, out existing))
            {
                if (conf > existing.Confidence) { existing.Confidence = conf; existing.Reason = reason; existing.Category = cat; }
                return existing;
            }
            items[item.Key] = item;
            return item;
        }

        /// <summary>
        /// Confronta un nome di cartella/chiave con i nomi del gioco o programma. Al primo livello di una radice (es. AppData\Local\Nome)
        /// la corrispondenza esatta è affidabile; al secondo livello (AppData\Local\Qualcosa\Nome) serve un nome più lungo.
        /// </summary>
        private Confidence? MatchName(string name, int level = 1, bool publisherParent = false)
        {
            string k = NameUtil.Norm(name);
            if (k.Length < 3) return null;
            if (p.NameKeys.Contains(k))
            {
                if (publisherParent) return Confidence.High;
                if (level == 1) return k.Length >= 5 ? Confidence.High : k.Length >= 4 ? (Confidence?)Confidence.Medium : null;
                return k.Length >= 8 ? Confidence.Medium : k.Length >= 5 ? (Confidence?)Confidence.Low : null;
            }
            if (p.ShortKeys.Contains(k) && (level == 1 || publisherParent)) return Confidence.Medium;
            if (!p.IsGame)
            {
                // cartelle con la versione nel nome ("AndroidStudio2025.3", "IntelliJIdea2024.1")
                foreach (var key in p.NameKeys)
                {
                    if (key.Length >= 6 && k.Length > key.Length && k.StartsWith(key) && k.Substring(key.Length).All(char.IsDigit))
                        return level == 1 || publisherParent ? Confidence.Medium : Confidence.Low;
                }
            }
            if (p.WeakKeys.Contains(k) && (level == 1 || publisherParent)) return Confidence.Low;
            return null;
        }

        private string NameReason(string name, string where)
        {
            string why = p.Names.Where(kv => NameUtil.Norm(kv.Key) == NameUtil.Norm(name)).Select(kv => kv.Value).FirstOrDefault()
                         ?? (p.IsGame ? L.T("titolo del gioco") : L.T("nome del programma"));
            return L.F("Cartella «{0}» in {1}: corrisponde al {2}", name, where, why);
        }

        /// <summary>True se il nome o il percorso appartengono (anche) a un altro gioco o programma installato.</summary>
        private bool ClaimedByOther(string name, string path)
        {
            string k = NameUtil.Norm(name);
            foreach (var o in others)
            {
                if (o.NameKeys.Contains(k) || o.ShortKeys.Contains(k)) return true;
                if (path == null) continue;
                foreach (var d in o.Entry.Dirs)
                    if (PathUtil.IsUnder(path, d, true) || PathUtil.IsUnder(d, path)) return true;
                if (o.Entry.IsSingleFile && o.Entry.MainExe != null && PathUtil.IsUnder(o.Entry.MainExe, path)) return true;
            }
            return false;
        }

        /// <summary>
        /// True se l'eseguibile (o la libreria) appartiene solo a questo gioco o programma: è nella sua cartella e non
        /// in quella di un altro contenuto o condiviso (es. un gioco dentro la cartella di Steam).
        /// </summary>
        private bool IsOwnFile(string file)
        {
            string f = PathUtil.Normalize(file);
            if (f == null) return false;
            if (p.Entry.IsSingleFile) return PathUtil.Same(f, p.Entry.MainExe);
            if (!ownDirs.Any(d => PathUtil.IsUnder(f, d))) return false;
            return !otherDirs.Any(o => PathUtil.IsUnder(f, o, true) && ownDirs.Any(d => PathUtil.IsUnder(o, d, true)));
        }

        /// <summary>La cartella contiene (o è) quella di un altro gioco o programma installato.</summary>
        private bool IsShared(string dir)
        {
            return p.Entry.ContainsOthers || otherDirs.Any(o => PathUtil.IsUnder(o, dir, true));
        }

        // ------------------------------------------------------------------ Passi della scansione

        private void AppFolder()
        {
            var g = p.Entry;
            if (!afterUninstall) return;
            for (int i = 0; i < ownDirs.Count; i++)
            {
                string dir = ownDirs[i];
                if (!Directory.Exists(dir) || IsShared(dir)) continue;
                if (InstalledApps.LooksLikeUserFolder(dir))
                {
                    // dentro ci sono anche documenti, foto o un progetto: si propone, ma non preselezionata
                    Add(ItemKind.Directory, ItemCategory.AppFolder, dir, L.F("Cartella del {0} rimasta sul disco. Attenzione: contiene anche documenti, foto o un progetto, controllala prima di eliminarla", noun), Confidence.Low);
                    continue;
                }
                if (g.IsGame && LocalSaves.Find(dir).Count > 0)
                {
                    // il programma di disinstallazione lascia spesso proprio i salvataggi: non vanno eliminati senza che l'utente lo decida
                    Add(ItemKind.Directory, ItemCategory.AppFolder, dir, L.T("Cartella del gioco rimasta sul disco. Attenzione: contiene salvataggi, controllala prima di eliminarla"), Confidence.Low);
                    continue;
                }
                if (i == 0 && PathUtil.Same(dir, g.InstallDir))
                    Add(ItemKind.Directory, ItemCategory.AppFolder, dir, L.T("Cartella di installazione rimasta sul disco"), Confidence.High);
                else
                    Add(ItemKind.Directory, ItemCategory.AppFolder, dir, L.F("Cartella del {0} indicata dai suoi collegamenti, rimasta sul disco", noun), Confidence.Medium);
            }
            if (g.IsSingleFile && g.MainExe != null && File.Exists(g.MainExe))
                Add(ItemKind.File, ItemCategory.AppFolder, g.MainExe, L.T("Eseguibile del programma portable"), Confidence.High);
            if (g.UninstallKey != null && RegUtil.KeyExists(g.UninstallKey))
                Add(ItemKind.RegistryKey, ItemCategory.Registry, g.UninstallKey, L.T("Voce di disinstallazione rimasta nel registro (compare ancora in \"App installate\")"), Confidence.High);
            foreach (var e in InstalledApps.ReadUninstallEntries())
            {
                string loc = PathUtil.Normalize(e.InstallLocation);
                if (loc != null && loc.Length > 3 && ownDirs.Any(d => PathUtil.IsUnder(loc, d, true)) && !otherDirs.Any(o => PathUtil.Same(o, loc)))
                    Add(ItemKind.RegistryKey, ItemCategory.Registry, e.KeyPath, L.F("Voce di disinstallazione che punta alla cartella del {0}", noun), Confidence.High);
            }
            if (!g.IsGame) return;
            foreach (var root in new[] { @"HKLM\SOFTWARE\WOW6432Node\GOG.com\Games", @"HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs" })
            {
                using (var rk = RegUtil.Open(root))
                {
                    foreach (var name in RegUtil.SubKeys(rk))
                    {
                        using (var k = rk.OpenSubKey(name))
                        {
                            string dir = PathUtil.Normalize(k == null ? null : (RegUtil.GetString(k, "path") ?? RegUtil.GetString(k, "InstallDir")));
                            if (dir != null && dir.Length > 3 && PathUtil.IsUnder(dir, g.InstallDir, true))
                                Add(ItemKind.RegistryKey, ItemCategory.Registry, root + "\\" + name, L.T("Registrazione del launcher per questo gioco"), Confidence.High);
                        }
                    }
                }
            }
        }

        private void SteamData()
        {
            var g = p.Entry;
            if (g.Launcher != LauncherKind.Steam || string.IsNullOrEmpty(g.SteamAppId)) return;
            string id = g.SteamAppId;
            foreach (var sa in SteamInfo.SteamAppsDirs)
            {
                Add(ItemKind.Directory, ItemCategory.Cache, Path.Combine(sa, "shadercache", id), L.F("Cache degli shader creata da Steam per questo {0}", noun), Confidence.High);
                Add(ItemKind.Directory, ItemCategory.Other, Path.Combine(sa, "workshop", "content", id), L.T("Contenuti del Workshop di Steam (mod scaricate)"), Confidence.High);
                Add(ItemKind.File, ItemCategory.Other, Path.Combine(sa, "workshop", "appworkshop_" + id + ".acf"), L.T("Elenco dei contenuti Workshop"), Confidence.High);
                Add(ItemKind.Directory, ItemCategory.Cache, Path.Combine(sa, "workshop", "downloads", id), L.T("Download Workshop incompleti"), Confidence.High);
                if (afterUninstall)
                {
                    Add(ItemKind.Directory, ItemCategory.Cache, Path.Combine(sa, "downloading", id), L.T("Download o aggiornamento interrotto"), Confidence.High);
                    Add(ItemKind.Directory, ItemCategory.Cache, Path.Combine(sa, "temp", id), L.T("File temporanei di Steam"), Confidence.High);
                }
            }
            foreach (var ud in SteamInfo.UserDataDirs)
            {
                Add(ItemKind.Directory, ItemCategory.Saves, Path.Combine(ud, id), L.T("Salvataggi e impostazioni locali di Steam Cloud (restano comunque nel cloud di Steam)"), Confidence.High);
                Add(ItemKind.Directory, ItemCategory.Media, Path.Combine(ud, "760", "remote", id), L.T("Screenshot fatti con Steam"), Confidence.High);
                string rec = Path.Combine(ud, "gamerecordings");
                foreach (var sub in PathUtil.Dirs(rec))
                {
                    foreach (var entry in PathUtil.Dirs(sub).Concat(PathUtil.Files(sub)))
                    {
                        string n = Path.GetFileNameWithoutExtension(entry);
                        if (n == id || n.Contains("_" + id + "_") || n.EndsWith("_" + id))
                            Add(Directory.Exists(entry) ? ItemKind.Directory : ItemKind.File, ItemCategory.Media, entry, L.T("Registrazione o clip di Steam"), Confidence.High);
                    }
                }
            }
            if (afterUninstall)
                Add(ItemKind.RegistryKey, ItemCategory.Registry, @"HKCU\Software\Valve\Steam\Apps\" + id, L.F("Informazioni di Steam su questo {0}", noun), Confidence.High);
        }

        /// <summary>Giochi salvati è fatta apposta per i salvataggi: lì una cartella con la sigla del titolo è del gioco ("Assetto Corsa EVO" → ACE).</summary>
        private void SavedGamesAcronym()
        {
            var words = System.Text.RegularExpressions.Regex.Split(p.Entry.Name, @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToList();
            if (words.Count < 3) return;
            string acronym = string.Concat(words.Select(w => w.Substring(0, 1)));
            string dir = Path.Combine(KnownDirs.SavedGames, acronym);
            if (others.Any(o => o.Entry.Id != p.Entry.Id && NameUtil.Norm(o.Entry.Name) == NameUtil.Norm(acronym))) return;
            Add(ItemKind.Directory, ItemCategory.Saves, dir, L.F("Cartella «{0}» in Giochi salvati: è la sigla del gioco", acronym), Confidence.Medium);
        }

        /// <summary>Cartelle che PCGamingWiki indica per questo gioco. Un nome che non richiama il gioco può essere dell'editore: resta da verificare.</summary>
        private void OnlineFolders()
        {
            foreach (var f in SaveDatabase.For(p.Entry))
            {
                if (others.Any(o => o.Entry.Dirs.Any(d => PathUtil.IsUnder(d, f.Path, true)))) continue;
                Add(ItemKind.Directory, f.IsConfig ? ItemCategory.Settings : ItemCategory.Saves, f.Path,
                    f.IsConfig ? L.T("Impostazioni del gioco, secondo PCGamingWiki") : L.T("Salvataggi del gioco, secondo PCGamingWiki"), f.NameMatches ? Confidence.Medium : Confidence.Low);
            }
        }

        private static readonly System.Text.RegularExpressions.Regex CollectionTitle = new System.Text.RegularExpressions.Regex(
            @"\b(trilogy|trilogia|collection|collezione|anthology|antologia|saga|bundle|compilation|quadrilogy|duology|complete pack)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// Una raccolta salva titolo per titolo ("GTA: The Trilogy" → Rockstar Games\GTA San Andreas…, Goldberg SocialClub Emu Saves\GTA_DEF).
        /// Il nome non basta a escludere gli altri giochi della serie, perciò restano «Da verificare».
        /// </summary>
        private void CollectionFolders()
        {
            string title = p.Entry.Name ?? "";
            if (!CollectionTitle.IsMatch(title)) return;
            var first = System.Text.RegularExpressions.Regex.Match(title, @"[\p{L}\p{N}]+");
            if (!first.Success || first.Value.Length < 3 || NameUtil.IsGenericWord(NameUtil.Norm(first.Value), AppKind.Game)) return;
            var parents = new List<string>(Traces.NamedEmuSaveRoots());
            foreach (var root in new[] { KnownDirs.Documents, KnownDirs.LocalAppData, KnownDirs.RoamingAppData })
                parents.AddRange(PathUtil.Dirs(root).Where(d => Traces.PublisherFolder.IsMatch(Path.GetFileName(d)) || p.PublisherKeys.Contains(NameUtil.Norm(Path.GetFileName(d)))));
            foreach (var parent in parents)
                foreach (var dir in PathUtil.Dirs(parent))
                {
                    string name = Path.GetFileName(dir);
                    var w = System.Text.RegularExpressions.Regex.Match(name, @"[\p{L}\p{N}]+");
                    if (!w.Success || !w.Value.Equals(first.Value, StringComparison.OrdinalIgnoreCase) || ClaimedByOther(name, dir)) continue;
                    Add(ItemKind.Directory, ItemCategory.Saves, dir, L.F("Cartella «{0}»: sembra di un titolo della raccolta. Controlla prima di eliminarla", name), Confidence.Low);
                }
        }

        private void AppIdSaves()
        {
            var ids = new HashSet<string>(p.SteamAppIds);
            // copia non di Steam, senza l'AppID nei suoi file: lo si chiede al database
            if (ids.Count == 0 && p.Entry.Launcher != LauncherKind.Steam) ids.UnionWith(SaveDatabase.AppIdsFor(p.Entry));
            ids.RemoveWhere(id => others.Any(o => o.SteamAppIds.Contains(id)));
            foreach (var root in Traces.AppIdSaveRoots())
                foreach (var id in ids)
                    Add(ItemKind.Directory, ItemCategory.Saves, Path.Combine(root, id), L.F("Salvataggi legati all'AppID {0} del gioco", id), Confidence.High);
            // un gioco spostato fuori da Steam o una copia diversa: Steam conserva comunque i suoi dati
            if (p.Entry.Launcher != LauncherKind.Steam)
                foreach (var ud in SteamInfo.UserDataDirs)
                    foreach (var id in ids)
                        Add(ItemKind.Directory, ItemCategory.Saves, Path.Combine(ud, id), L.F("Dati di Steam per l'AppID {0} del gioco", id), Confidence.Medium);
        }

        private void NamedFolders()
        {
            var dataRoots = Traces.DataRoots(p.Entry.Kind);
            if (!p.IsGame && afterUninstall) dataRoots.AddRange(Traces.ProgramRoots());
            foreach (var root in dataRoots)
            {
                string rootDir = root.Item1;
                if (!Directory.Exists(rootDir)) continue;
                bool userProfile = PathUtil.Same(rootDir, KnownDirs.UserProfile);
                foreach (var d1 in PathUtil.Dirs(rootDir))
                {
                    ct.ThrowIfCancellationRequested();
                    string n1 = Path.GetFileName(d1);
                    string k1 = NameUtil.Norm(n1);
                    if (Traces.SkipTopLevel.Contains(k1) || PathUtil.IsReparsePoint(d1)) continue;
                    // nella cartella dell'utente solo le cartelle "nascoste" dei programmi (".vscode"): le altre sono sue
                    // (progetti, cartelle sincronizzate come Dropbox o Nextcloud, librerie di Zotero o Calibre...)
                    if (userProfile && !n1.StartsWith(".")) continue;
                    if (dataRoots.Any(r => PathUtil.Same(r.Item1, d1))) continue;
                    var conf = MatchName(n1);
                    // nella cartella dell'utente solo nomi esatti (es. ".nomeprogramma")
                    if (userProfile && conf != Confidence.High) continue;
                    if (conf.HasValue)
                    {
                        if (!ClaimedByOther(n1, d1)) Add(ItemKind.Directory, CategoryFor(root.Item2, d1), d1, NameReason(n1, root.Item3), conf.Value);
                        continue;
                    }
                    if (userProfile) continue;
                    string[] subs = PathUtil.Dirs(d1);
                    if (subs.Length > 300) continue;
                    bool publisherDir = p.PublisherKeys.Contains(k1);
                    foreach (var d2 in subs)
                    {
                        string n2 = Path.GetFileName(d2);
                        var c2 = MatchName(n2, 2, publisherDir);
                        if (!c2.HasValue || ClaimedByOther(n2, d2)) continue;
                        Add(ItemKind.Directory, CategoryFor(root.Item2, d2), d2, NameReason(n2, root.Item3 + "\\" + n1), c2.Value);
                    }
                }
            }
            // UAC VirtualStore: file che il gioco o programma ha provato a scrivere nella propria cartella in Programmi
            foreach (var dir in ownDirs)
            {
                foreach (var pf in new[] { KnownDirs.ProgramFiles, KnownDirs.ProgramFilesX86 })
                {
                    if (!PathUtil.IsUnder(dir, pf)) continue;
                    string rel = dir.Substring(pf.Length).TrimStart('\\');
                    string vs = Path.Combine(KnownDirs.VirtualStore, Path.GetFileName(pf), rel);
                    Add(ItemKind.Directory, ItemCategory.Settings, vs, L.F("Copia virtualizzata da Windows (VirtualStore) della cartella del {0}", noun), Confidence.High);
                }
            }
            if (!string.IsNullOrEmpty(p.Entry.PackageFamily))
                Add(ItemKind.Directory, p.IsGame ? ItemCategory.Saves : ItemCategory.Settings, Path.Combine(KnownDirs.LocalAppData, "Packages", p.Entry.PackageFamily),
                    p.IsGame ? L.T("Dati dell'app Xbox (salvataggi locali e impostazioni)") : L.T("Dati dell'app (impostazioni, cache e file locali)"), Confidence.High);
        }

        private ItemCategory CategoryFor(ItemCategory rootCategory, string dir)
        {
            if (p.IsGame && rootCategory == ItemCategory.Settings && Directory.Exists(Path.Combine(dir, "Saved", "SaveGames"))) return ItemCategory.Saves;
            return rootCategory;
        }

        private void EngineData()
        {
            if (p.UnityCompany != null && p.UnityProduct != null)
            {
                string c = p.UnityCompany, pr = p.UnityProduct;
                Add(ItemKind.Directory, ItemCategory.Saves, Path.Combine(KnownDirs.LocalLow, c, pr), L.T("Dati del gioco Unity (salvataggi, impostazioni, log)"), Confidence.High);
                Add(ItemKind.Directory, ItemCategory.Cache, Path.Combine(KnownDirs.Temp, c, pr), L.T("File temporanei e crash del gioco Unity"), Confidence.High);
                Add(ItemKind.Directory, ItemCategory.Cache, Path.Combine(KnownDirs.LocalLow, "Unity", c + "_" + pr), L.T("Dati diagnostici di Unity per questo gioco"), Confidence.High);
                string key = @"HKCU\Software\" + c + "\\" + pr;
                if (RegUtil.KeyExists(key))
                    Add(ItemKind.RegistryKey, ItemCategory.Registry, key, L.T("Preferenze del gioco Unity (PlayerPrefs)"), Confidence.High);
            }
            foreach (var proj in p.UnrealProjects)
            {
                Add(ItemKind.Directory, ItemCategory.Saves, Path.Combine(KnownDirs.LocalAppData, proj), L.T("Dati del gioco Unreal Engine (salvataggi, configurazione, log)"), Confidence.High);
                Add(ItemKind.Directory, ItemCategory.Saves, Path.Combine(KnownDirs.MyGames, "UnrealEngine3", proj), L.T("Salvataggi del gioco Unreal Engine 3"), Confidence.High);
                Add(ItemKind.Directory, ItemCategory.Saves, Path.Combine(KnownDirs.MyGames, proj), L.T("Salvataggi del gioco Unreal Engine"), Confidence.High);
            }
        }

        private void RegistryKeys()
        {
            var skip = new HashSet<string>(new[] { "microsoft", "classes", "policies", "wow6432node", "clients", "registeredapplications", "valve", "odbc", "intel", "nvidiacorporation", "google", "mozilla" });
            foreach (var root in new[] { @"HKCU\Software", @"HKLM\SOFTWARE", @"HKLM\SOFTWARE\WOW6432Node" })
            {
                using (var rk = RegUtil.Open(root))
                {
                    foreach (var k1 in RegUtil.SubKeys(rk))
                    {
                        string n1 = NameUtil.Norm(k1);
                        if (skip.Contains(n1)) continue;
                        var conf = MatchName(k1);
                        if (conf.HasValue)
                        {
                            if (!ClaimedByOther(k1, null))
                                Add(ItemKind.RegistryKey, ItemCategory.Registry, root + "\\" + k1, L.F("Chiave «{0}» con il nome del {1}", k1, noun), conf.Value);
                            continue;
                        }
                        bool publisher = p.PublisherKeys.Contains(n1);
                        using (var k = OpenSub(rk, k1))
                        {
                            foreach (var k2 in RegUtil.SubKeys(k))
                            {
                                var c2 = MatchName(k2, 2, publisher);
                                if (!c2.HasValue || ClaimedByOther(k2, null)) continue;
                                Add(ItemKind.RegistryKey, ItemCategory.Registry, root + "\\" + k1 + "\\" + k2, L.F("Chiave «{0}\\{1}» con il nome del {2}", k1, k2, noun), c2.Value);
                            }
                        }
                    }
                }
            }
        }

        private static Microsoft.Win32.RegistryKey OpenSub(Microsoft.Win32.RegistryKey parent, string name)
        {
            try { return parent.OpenSubKey(name); }
            catch { return null; }
        }

        private void ExeTraces()
        {
            if (ownDirs.Count == 0 && !p.Entry.IsSingleFile) return;
            var conf = TraceConfidence;
            foreach (var t in Traces.ExePathValues())
                if (IsOwnFile(t.ExePath))
                    Add(ItemKind.RegistryValue, ItemCategory.Registry, t.KeyPath, L.F("Traccia di Windows: {0}", t.What), conf, t.ValueName);
            foreach (var t in Traces.GameConfigStore().Concat(Traces.AppPaths()).Concat(Traces.NotificationSettings()))
                if (IsOwnFile(t.ExePath))
                    Add(ItemKind.RegistryKey, ItemCategory.Registry, t.KeyPath, L.F("Traccia di Windows: {0}", t.What), conf);
            foreach (var exe in Traces.FirewallPrograms())
                if (IsOwnFile(exe))
                    Add(ItemKind.FirewallRule, ItemCategory.Registry, exe, L.F("Regole di Windows Firewall create per il {0}", noun), conf);
        }

        private void Autostart()
        {
            foreach (var t in Traces.RunEntries())
            {
                if (!IsOwnFile(t.ExePath)) continue;
                var conf = TraceConfidence;
                Add(ItemKind.RegistryValue, ItemCategory.Startup, t.KeyPath, L.F("Avvio automatico con Windows ({0})", Path.GetFileName(t.ExePath)), conf, t.ValueName);
                foreach (var s in Traces.StartupApproved(t.ValueName))
                    Add(ItemKind.RegistryValue, ItemCategory.Startup, s.KeyPath, L.T("Stato dell'avvio automatico in Gestione attività"), conf, s.ValueName);
            }
        }

        private void Tasks()
        {
            foreach (var t in Traces.ScheduledTasks())
            {
                string exe = t.Exes.FirstOrDefault(IsOwnFile);
                if (exe == null) continue;
                var item = Add(ItemKind.ScheduledTask, ItemCategory.Startup, t.Path, L.F("Attività pianificata che avvia {0}", Path.GetFileName(exe)), TraceConfidence);
                if (item != null) item.NeedsAdmin = t.NeedsAdmin;
            }
        }

        private void ServicesStep()
        {
            // un servizio di un programma ancora installato non si propone: smetterebbe di funzionare
            if (!afterUninstall) return;
            foreach (var s in Traces.Services())
                if (IsOwnFile(s.ExePath))
                    Add(ItemKind.Service, ItemCategory.Startup, s.Name, L.F("Servizio di Windows installato dal {0} ({1})", noun, Path.GetFileName(s.ExePath)),
                        File.Exists(s.ExePath) ? Confidence.Medium : Confidence.High, null, s.DisplayName);
        }

        private void Associations()
        {
            if (ownDirs.Count == 0 && !p.Entry.IsSingleFile) return;
            var conf = TraceConfidence;
            // tipi di file e registrazioni "Apri con": solo se TUTTI i loro comandi avviano questo programma
            var progIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var grp in Traces.ClassCommands().GroupBy(t => t.KeyPath, StringComparer.OrdinalIgnoreCase))
            {
                if (!grp.All(t => IsOwnFile(t.ExePath))) continue;
                var first = grp.First();
                Add(ItemKind.RegistryKey, ItemCategory.Registry, first.KeyPath,
                    (first.What.StartsWith("tipo") ? L.F("Tipo di file «{0}» registrato dal {1}", first.Tag, noun) : L.F("Registrazione del {0} per \"Apri con\"", noun)), conf);
                if (first.What.StartsWith("tipo")) progIds.Add(first.Tag);
            }
            foreach (var t in Traces.OpenWithProgIds(progIds))
                Add(ItemKind.RegistryValue, ItemCategory.Registry, t.KeyPath, L.F("Riferimento al tipo di file «{0}»: {1}", t.Tag, t.What), conf, t.ValueName);
            // voci aggiunte ai menu contestuali di Windows ("Apri con...", "Aggiungi a...")
            foreach (var t in Traces.ShellVerbs())
                if (IsOwnFile(t.ExePath))
                    Add(ItemKind.RegistryKey, ItemCategory.Registry, t.KeyPath, L.F("Voce «{0}» aggiunta al menu contestuale di Esplora risorse", t.Tag), conf);
            // componenti COM (estensioni di Esplora risorse) implementati da file del programma
            var clsids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in Traces.ComServers())
            {
                if (!IsOwnFile(t.ExePath)) continue;
                Add(ItemKind.RegistryKey, ItemCategory.Registry, t.KeyPath, L.F("Componente COM del {0} ({1})", noun, Path.GetFileName(t.ExePath)), conf);
                clsids.Add(t.Tag);
            }
            foreach (var t in Traces.ShellExtensionRefs(clsids))
            {
                if (t.ValueName != null) Add(ItemKind.RegistryValue, ItemCategory.Registry, t.KeyPath, L.F("Estensione del {0}: {1}", noun, t.What), conf, t.ValueName);
                else Add(ItemKind.RegistryKey, ItemCategory.Registry, t.KeyPath, L.F("Estensione del {0}: {1}", noun, t.What), conf);
            }
            foreach (var t in Traces.RegisteredApplications())
                if (t.ExePath != null && IsOwnFile(t.ExePath))
                    Add(ItemKind.RegistryValue, ItemCategory.Registry, t.KeyPath, L.F("Registrazione del {0} tra le app predefinite", noun), conf, t.ValueName);
            foreach (var t in Traces.Clients())
                if (IsOwnFile(t.ExePath))
                    Add(ItemKind.RegistryKey, ItemCategory.Registry, t.KeyPath, L.F("Traccia di Windows: {0}", t.What), conf);
        }

        private void PathStep()
        {
            foreach (var e in Traces.PathEntries())
            {
                if (e.Expanded == null) continue;
                bool own = p.Entry.IsSingleFile ? false : ownDirs.Any(d => PathUtil.IsUnder(e.Expanded, d, true)) && !otherDirs.Any(o => PathUtil.IsUnder(e.Expanded, o, true) && !ownDirs.Any(d => PathUtil.Same(d, o)));
                if (!own) continue;
                Add(ItemKind.PathEntry, ItemCategory.Registry, e.KeyPath, L.F("Cartella del {0} aggiunta alla variabile d'ambiente PATH", noun), TraceConfidence, e.ValueName, e.Raw);
            }
        }

        private void CrashData()
        {
            var exeNames = p.ExeNames.ToList();
            if (exeNames.Count == 0) return;
            foreach (var f in PathUtil.Files(KnownDirs.CrashDumps, "*.dmp"))
            {
                string n = Path.GetFileName(f);
                if (exeNames.Any(e => n.StartsWith(e + ".", StringComparison.OrdinalIgnoreCase)))
                    Add(ItemKind.File, ItemCategory.Cache, f, L.F("File di crash (dump) del {0}", noun), Confidence.High);
            }
            foreach (var root in Traces.WerRoots())
            {
                foreach (var d in PathUtil.Dirs(root))
                {
                    string n = Path.GetFileName(d);
                    int us = n.IndexOf('_');
                    if (us < 0) continue;
                    string rest = n.Substring(us + 1);
                    string exe = exeNames.FirstOrDefault(e => WerNameMatches(rest, e));
                    if (exe == null) continue;
                    string app = Traces.WerAppPath(d);
                    bool exact = app != null && (IsOwnFile(app) || Path.GetFileName(app).Equals(exe, StringComparison.OrdinalIgnoreCase));
                    if (app != null && !exact) continue;
                    Add(ItemKind.Directory, ItemCategory.Cache, d, L.F("Report di errore di Windows relativo al {0}", noun), exact ? Confidence.High : Confidence.Medium);
                }
            }
        }

        /// <summary>Le cartelle WER sono "AppCrash_[nome exe troncato]_[hash]...": confronta il prefisso troncato.</summary>
        private static bool WerNameMatches(string rest, string exeName)
        {
            for (int len = Math.Min(exeName.Length, rest.Length - 1); len >= Math.Min(exeName.Length, 12); len--)
            {
                if (rest[len] == '_' && string.Compare(rest, 0, exeName, 0, len, StringComparison.OrdinalIgnoreCase) == 0)
                    return true;
            }
            return false;
        }

        private void ShortcutsStep()
        {
            var g = p.Entry;
            var conf = afterUninstall ? Confidence.High : Confidence.Low;
            foreach (var s in Traces.Shortcuts())
            {
                bool match = false;
                if (s.Target != null && IsOwnFile(s.Target)) match = true;
                if (s.Url != null)
                {
                    string sid = Traces.SteamIdFromUrl(s.Url);
                    if (sid != null && g.Launcher == LauncherKind.Steam && sid == g.SteamAppId) match = true;
                    if (g.EpicAppName != null && s.Url.StartsWith("com.epicgames.launcher://", StringComparison.OrdinalIgnoreCase)
                        && s.Url.IndexOf(g.EpicAppName, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                    if (s.IconFile != null && IsOwnFile(s.IconFile)) match = true;
                }
                if (!match) continue;
                bool startup = PathUtil.IsUnder(s.File, Path.Combine(KnownDirs.StartMenu, "Startup")) || PathUtil.IsUnder(s.File, Path.Combine(KnownDirs.CommonStartMenu, "Startup"));
                Add(ItemKind.File, startup ? ItemCategory.Startup : ItemCategory.Shortcuts, s.File,
                    startup ? L.T("Collegamento nella cartella Esecuzione automatica (avvio con Windows)") : L.F("Collegamento al {0}", noun), conf);
                if (s.IconFile != null && SteamInfo.SteamPath != null && PathUtil.IsUnder(s.IconFile, Path.Combine(SteamInfo.SteamPath, "steam", "games")))
                    Add(ItemKind.File, ItemCategory.Cache, s.IconFile, L.T("Icona del collegamento creata da Steam"), conf);
            }
        }

        private void MediaStep()
        {
            foreach (var root in Traces.MediaRoots())
            {
                foreach (var d in PathUtil.Dirs(root))
                {
                    string n = Path.GetFileName(d);
                    var conf = MatchName(n);
                    if (conf == Confidence.High && !ClaimedByOther(n, d))
                        Add(ItemKind.Directory, ItemCategory.Media, d, L.T("Cartella di screenshot/video con il nome del gioco"), Confidence.High);
                }
            }
            if (Directory.Exists(KnownDirs.Captures))
            {
                foreach (var f in PathUtil.Files(KnownDirs.Captures))
                {
                    string title = Traces.CaptureTitleOf(f);
                    if (title != null && MatchName(title) == Confidence.High && !ClaimedByOther(title, null))
                        Add(ItemKind.File, ItemCategory.Media, f, L.T("Acquisizione della Xbox Game Bar di questo gioco"), Confidence.High);
                }
            }
        }

        private void PrefetchStep()
        {
            if ((ownDirs.Count == 0 && !p.Entry.IsSingleFile) || !AppSettings.Current.UsePrefetch) return;
            var records = PrefetchIndex.Records.Where(r => r.ExePath != null && IsOwnFile(r.ExePath)).ToList();
            p.PrefetchFiles = records.Count;
            p.PrefetchDirs.Clear();
            foreach (var dir in PrefetchIndex.ExclusiveAnchors(records))
            {
                string name = Path.GetFileName(dir);
                if (ClaimedByOther(name, dir) || !Safety.IsDeletablePath(dir)) continue;
                p.PrefetchDirs.Add(dir);
                var conf = MatchName(name) == Confidence.High ? Confidence.High : Confidence.Medium;
                var cat = PathUtil.IsUnder(dir, KnownDirs.LocalAppData) || PathUtil.IsUnder(dir, KnownDirs.RoamingAppData) || PathUtil.IsUnder(dir, KnownDirs.ProgramData)
                    ? ItemCategory.Settings : ItemCategory.Saves;
                Add(ItemKind.Directory, CategoryFor(cat, dir), dir, L.F("Cartella usata solo da questo {0} (rilevata dal Prefetch di Windows)", noun), conf);
            }
        }

        // ------------------------------------------------------------------ Rifinitura

        private List<LeftoverItem> Finish()
        {
            var list = items.Values.Where(i =>
            {
                if (Safety.IsDeletable(i)) return true;
                // i dati di AppManager (es. il deposito dei salvataggi, che ha il nome del gioco) non sono tracce: nessuna nota
                if (!(i.IsFileSystem && Safety.IsOwnData(i.Path))) Log.Info("Escluso per sicurezza: {0}", i.DisplayPath);
                return false;
            }).OrderBy(i => i.Path.Length).ToList();

            // rimuove gli elementi già compresi in una cartella o chiave selezionata
            var result = new List<LeftoverItem>();
            foreach (var i in list)
            {
                bool covered = result.Any(r =>
                    (r.Kind == ItemKind.Directory && i.IsFileSystem && PathUtil.IsUnder(i.Path, r.Path)) ||
                    (r.Kind == ItemKind.RegistryKey && i.IsRegistry && (PathUtil.IsUnder(i.Path, r.Path) || (i.Kind == ItemKind.RegistryValue && PathUtil.Same(i.Path, r.Path)))));
                if (!covered) result.Add(i);
            }
            foreach (var i in result)
            {
                ct.ThrowIfCancellationRequested();
                if (i.IsFileSystem)
                {
                    int count;
                    i.Size = PathUtil.Size(i.Path, out count, ct);
                    i.FileCount = count;
                    i.LastWrite = PathUtil.LastWrite(i.Path);
                }
            }
            return result.OrderBy(i => Array.IndexOf(Categories.Order, i.Category))
                .ThenByDescending(i => i.Confidence).ThenBy(i => i.DisplayPath, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
