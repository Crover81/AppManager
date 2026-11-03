using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AppManager
{
    /// <summary>Cartella (o file) con i salvataggi o le impostazioni di un gioco.</summary>
    internal sealed class SaveLocationInfo
    {
        public string Path;
        public bool IsFile;
        public ItemCategory Category;
        public Confidence Confidence;
        public string Reason;
        public bool Manual;
        public long Size;
        public int FileCount;
        public DateTime? LastWrite;
        public bool Exists { get { return IsFile ? File.Exists(PathUtil.Long(Path)) : Directory.Exists(PathUtil.Long(Path)); } }
    }

    /// <summary>Dove torna una parte dei file di un profilo messo da parte (Key = sottocartella nel deposito).</summary>
    internal sealed class SaveMapping
    {
        public string Key { get; set; }
        public string Path { get; set; }
        public bool IsFile { get; set; }
    }

    /// <summary>
    /// Profilo di salvataggio: un insieme di salvataggi (e, a scelta, impostazioni). Quello "in uso" ha i file nelle cartelle
    /// del gioco; gli altri li hanno nel deposito, in profiles\&lt;Id&gt;, con l'elenco di dove devono tornare.
    /// </summary>
    internal sealed class SaveProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Created { get; set; }        // date in formato ISO (JavaScriptSerializer altrimenti le converte in UTC)
        public string LastUsed { get; set; }
        public List<SaveMapping> Stored { get; set; }

        public SaveProfile() { Stored = new List<SaveMapping>(); }

        public DateTime CreatedDate { get { return SaveVault.ParseDate(Created); } }
        public DateTime LastUsedDate { get { return SaveVault.ParseDate(LastUsed); } }
    }

    /// <summary>Stato del deposito di un gioco (vault.json nella sua cartella).</summary>
    internal sealed class SaveVaultState
    {
        public int Version { get; set; }
        public string GameId { get; set; }
        public string GameName { get; set; }
        public string ActiveProfile { get; set; }
        public bool IncludeSettings { get; set; }         // solo lettura dei depositi della versione 2.1: ora è in AppSettings.SavesWithSettings
        public List<SaveProfile> Profiles { get; set; }
        public List<SaveMapping> Keys { get; set; }   // sottocartella stabile per ogni posizione (uguale in tutti i profili)

        public SaveVaultState()
        {
            Version = 1;
            Profiles = new List<SaveProfile>();
            Keys = new List<SaveMapping>();
        }

        public SaveProfile Active { get { return Profiles.FirstOrDefault(p => p.Id == ActiveProfile); } }
    }

    /// <summary>
    /// Deposito dei salvataggi: backup dei salvataggi (zip nella sezione Backup) e profili intercambiabili
    /// (più giocatori, nuova partita senza perdere la vecchia, impostazioni diverse).
    /// Ogni spostamento è annotato: se qualcosa non riesce a metà, tutto torna com'era.
    /// </summary>
    internal static class SaveVault
    {
        public const string SavesReason = "Salvataggi";
        public const string BeforeRestoreReason = "Prima del ripristino";
        private const string StateFile = "vault.json";

        /// <summary>Solo per le prove automatiche: false = i file tolti vengono eliminati invece di finire nel Cestino.</summary>
        internal static bool UseRecycleBin = true;

        public static string Root
        {
            get
            {
                string r = AppSettings.Current.SaveVaultFolder;
                return string.IsNullOrEmpty(r) ? AppPaths.DefaultSaveVaultDir : r;
            }
        }

        internal static DateTime ParseDate(string s)
        {
            DateTime d;
            return !string.IsNullOrEmpty(s) && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out d) ? d : DateTime.MinValue;
        }

        private static string Now() { return DateTime.Now.ToString("o"); }

        // ------------------------------------------------------------------ Cartella e stato del gioco

        /// <summary>Cartella del gioco nel deposito: quella con lo stesso Id, altrimenti una nuova con il nome del gioco.</summary>
        public static string GameDir(AppEntry g)
        {
            string root = Root;
            foreach (var dir in PathUtil.Dirs(root))
            {
                var st = ReadState(dir);
                if (st != null && st.GameId == g.Id) return dir;
            }
            string name = PathUtil.SafeFileName(g.Name).Trim().TrimEnd('.');
            if (name.Length > 60) name = name.Substring(0, 60).Trim();
            if (name.Length == 0) name = L.T("Gioco");
            string path = Path.Combine(root, name);
            for (int k = 2; Directory.Exists(path) && ReadState(path) != null; k++) path = Path.Combine(root, name + " (" + k + ")");
            return path;
        }

        private static SaveVaultState ReadState(string dir)
        {
            try
            {
                string f = Path.Combine(dir, StateFile);
                if (!File.Exists(f)) return null;
                return Json.Deserialize<SaveVaultState>(File.ReadAllText(f));
            }
            catch (Exception ex)
            {
                Log.Error(L.F("Lettura del deposito dei salvataggi {0}", dir), ex);
                return null;
            }
        }

        public static SaveVaultState Load(AppEntry g)
        {
            var st = ReadState(GameDir(g)) ?? new SaveVaultState();
            st.GameId = g.Id;
            st.GameName = g.Name;
            if (st.Profiles == null) st.Profiles = new List<SaveProfile>();
            if (st.Keys == null) st.Keys = new List<SaveMapping>();
            foreach (var p in st.Profiles) if (p.Stored == null) p.Stored = new List<SaveMapping>();
            return st;
        }

        public static void Store(AppEntry g, SaveVaultState st)
        {
            string dir = GameDir(g);
            Directory.CreateDirectory(dir);
            PathUtil.WriteAllTextAtomic(Path.Combine(dir, StateFile), Json.Serialize(st));
        }

        /// <summary>True se backup e profili del gioco includono anche le impostazioni (preferenza salvata nelle impostazioni di AppManager).</summary>
        public static bool IncludesSettings(AppEntry g)
        {
            return AppSettings.Current.SavesWithSettings.Contains(g.Id);
        }

        /// <summary>Da chiamare dal thread dell'interfaccia (salva le impostazioni).</summary>
        public static void SetIncludesSettings(AppEntry g, bool value)
        {
            var list = AppSettings.Current.SavesWithSettings;
            list.RemoveAll(x => x == g.Id);
            if (value) list.Add(g.Id);
            AppSettings.Current.Save();
        }

        /// <summary>
        /// Senza profili messi da parte il deposito del gioco non serve più: vengono tolti vault.json e le cartelle vuote,
        /// così in «AppManager Salvataggi» non resta una cartella che fa pensare a salvataggi ancora conservati.
        /// Se nel deposito ci sono ancora dei file (non dovrebbe succedere) la cartella resta, per non perdere nulla.
        /// </summary>
        public static bool RemoveIfUnused(AppEntry g, SaveVaultState st)
        {
            if (st.Profiles.Any(p => p.Id != st.ActiveProfile)) return false;
            string dir = GameDir(g);
            string l = PathUtil.Long(dir);
            if (Directory.Exists(l))
            {
                var links = new List<string>();
                PathUtil.DirsNoLinks(dir, links);
                var files = PathUtil.FilesNoLinks(dir).Where(f => !IsStateFile(f)).ToList();
                if (files.Count > 0 || links.Count > 0)
                {
                    Log.Warn("Salvataggi di {0}: nel deposito sono rimasti {1} file senza profilo ({2}): la cartella resta", g.Name, files.Count + links.Count, dir);
                    return false;
                }
                foreach (var f in PathUtil.FilesNoLinks(dir).ToList()) File.Delete(PathUtil.Long(f));
                RemoveEmptyDirs(dir);
                if (!Directory.Exists(l)) Log.Info("Salvataggi di {0}: nessun profilo messo da parte, tolta la sua cartella dal deposito", g.Name);
            }
            st.Profiles.Clear();
            st.Keys.Clear();
            st.ActiveProfile = null;
            return true;
        }

        private static bool IsStateFile(string f)
        {
            string n = Path.GetFileName(f);
            return n.Equals(StateFile, StringComparison.OrdinalIgnoreCase) || n.Equals(StateFile + ".tmp", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Primo nome libero «Profilo N», da 2 in su (il profilo attuale, al primo cambio, diventa «Principale»).</summary>
        public static string FreeProfileName(IEnumerable<string> used)
        {
            var set = new HashSet<string>(used.Where(x => x != null).Select(x => x.Trim()), StringComparer.CurrentCultureIgnoreCase);
            set.Add(L.T("Principale"));
            for (int n = 2; ; n++)
            {
                string name = L.F("Profilo {0}", n);
                if (!set.Contains(name)) return name;
            }
        }

        /// <summary>Spazio occupato nel deposito dai profili messi da parte.</summary>
        public static long StoredSize(AppEntry g, SaveProfile p)
        {
            string dir = Path.Combine(GameDir(g), "profiles", p.Id);
            return Directory.Exists(dir) ? PathUtil.Size(dir) : 0;
        }

        // ------------------------------------------------------------------ Dove sono i salvataggi

        /// <summary>
        /// Cartelle dei salvataggi (e, a scelta, delle impostazioni) trovate dall'analisi dei dati del gioco,
        /// più quelle aggiunte a mano, meno quelle escluse. Mai collegamenti né aree protette.
        /// </summary>
        public static List<SaveLocationInfo> FindLocations(AppEntry g, IEnumerable<AppEntry> others, bool includeSettings, Action<string> progress, CancellationToken ct)
        {
            var profile = ProfileBuilder.Get(g);
            var otherProfiles = others.Where(o => o.Id != g.Id).Select(ProfileBuilder.Get).ToList();
            var items = LeftoverScanner.Scan(profile, otherProfiles, false, progress, ct);
            var list = new List<SaveLocationInfo>();
            foreach (var i in items)
            {
                if (!i.IsFileSystem) continue;
                if (i.Category != ItemCategory.Saves && !(includeSettings && i.Category == ItemCategory.Settings)) continue;
                if (PathUtil.IsReparsePoint(i.Path)) continue;
                list.Add(new SaveLocationInfo
                {
                    Path = i.Path, IsFile = i.Kind == ItemKind.File, Category = i.Category, Confidence = i.Confidence, Reason = i.Reason,
                    Size = Math.Max(0, i.Size), FileCount = i.FileCount, LastWrite = i.LastWrite,
                });
            }
            // portable e repack salvano spesso nella cartella del gioco
            foreach (var path in LocalSaves.Find(g.InstallDir).Concat(SaveDatabase.InGamePaths(g)))
            {
                if (list.Any(l => PathUtil.Same(l.Path, path))) continue;
                var info = new SaveLocationInfo { Path = path, IsFile = File.Exists(PathUtil.Long(path)), Category = ItemCategory.Saves, Confidence = Confidence.Medium, Reason = L.T("Salvataggi nella cartella del gioco") };
                Measure(info);
                list.Add(info);
            }
            var settings = AppSettings.Current;
            foreach (var m in settings.SaveLocations.Where(x => x.GameId == g.Id && !x.Excluded))
            {
                string n = PathUtil.Normalize(m.Path);
                if (n == null || list.Any(l => PathUtil.Same(l.Path, n))) continue;
                bool isFile = File.Exists(PathUtil.Long(n));
                var info = new SaveLocationInfo { Path = n, IsFile = isFile, Category = ItemCategory.Saves, Confidence = Confidence.High, Reason = L.T("Aggiunta a mano"), Manual = true };
                Measure(info);
                list.Add(info);
            }
            var excluded = settings.SaveLocations.Where(x => x.GameId == g.Id && x.Excluded).Select(x => x.Path).ToList();
            list.RemoveAll(l => excluded.Any(e => PathUtil.Same(e, l.Path)) || !IsUsable(l.Path));
            // una posizione dentro un'altra è già compresa
            list = list.Where(l => !list.Any(o => o != l && !o.IsFile && PathUtil.IsUnder(l.Path, o.Path))).ToList();
            return list.OrderBy(l => l.Category).ThenByDescending(l => l.Confidence).ThenBy(l => l.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static void Measure(SaveLocationInfo info)
        {
            int count;
            DateTime newest;
            info.Size = PathUtil.Size(info.Path, out count, out newest);
            info.FileCount = count;
            info.LastWrite = newest == DateTime.MinValue ? (DateTime?)null : newest;
        }

        /// <summary>
        /// Una posizione si può gestire se si potrebbe anche eliminare (quindi mai Documenti, AppData, cartelle di sistema,
        /// cartelle di altri giochi...) e non è né contiene il deposito o i backup.
        /// </summary>
        public static string CheckUsable(string path)
        {
            string n = PathUtil.Normalize(path);
            if (n == null) return L.T("Il percorso non è valido.");
            if (Safety.IsOwnData(n)) return L.T("È una cartella di AppManager (deposito dei salvataggi o backup).");
            if (!Safety.IsDeletablePath(n)) return L.T("È una cartella protetta (di Windows, dell'utente o di un altro programma): AppManager non la sposta.");
            if (PathUtil.IsReparsePoint(n)) return L.T("È un collegamento a un'altra cartella: scegli la cartella vera.");
            return null;
        }

        private static bool IsUsable(string path) { return CheckUsable(path) == null; }

        public static void AddManualLocation(AppEntry g, string path)
        {
            var s = AppSettings.Current;
            string n = PathUtil.Normalize(path);
            s.SaveLocations.RemoveAll(x => x.GameId == g.Id && PathUtil.Same(x.Path, n));
            s.SaveLocations.Add(new SaveLocation { GameId = g.Id, Path = n });
            s.Save();
        }

        /// <summary>Toglie una posizione: quelle aggiunte a mano spariscono, quelle trovate da sole vengono escluse.</summary>
        public static void RemoveLocation(AppEntry g, SaveLocationInfo l)
        {
            var s = AppSettings.Current;
            s.SaveLocations.RemoveAll(x => x.GameId == g.Id && PathUtil.Same(x.Path, l.Path));
            if (!l.Manual) s.SaveLocations.Add(new SaveLocation { GameId = g.Id, Path = l.Path, Excluded = true });
            s.Save();
        }

        /// <summary>True se nelle posizioni c'è qualcosa (almeno un file).</summary>
        public static bool HasContent(IEnumerable<string> paths)
        {
            foreach (var p in paths)
            {
                string l = PathUtil.Long(p);
                if (File.Exists(l)) return true;
                if (Directory.Exists(l) && PathUtil.FilesNoLinks(p).Any()) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ Backup dei salvataggi (zip nella sezione Backup)

        /// <summary>Crea un backup (zip) delle posizioni indicate. Restituisce il percorso del file.</summary>
        public static string Backup(AppEntry g, IEnumerable<SaveLocationInfo> locations, string reason, byte[] imagePng, CancellationToken ct, string protect = null)
        {
            var items = locations.Where(l => l.Exists).Select(l => new LeftoverItem
            {
                Kind = l.IsFile ? ItemKind.File : ItemKind.Directory, Category = l.Category, Path = l.Path, Confidence = Confidence.High, Reason = l.Reason,
            }).ToList();
            if (items.Count == 0) throw new InvalidOperationException(L.T("Non ci sono salvataggi da copiare: le cartelle sono vuote o non esistono."));
            var meta = new BackupMeta { AppName = g.Name, AppId = g.Id, AppKind = AppKind.Game, Reason = reason, ImagePng = imagePng, ImageFile = imagePng == null ? g.CoverImage : null };
            return Cleaner.BackupOnly(items, meta, ct, protect);
        }

        /// <summary>
        /// Ripristina un backup dei salvataggi: prima un backup automatico dello stato attuale ("Prima del ripristino"),
        /// poi le posizioni del backup vengono svuotate (nel Cestino) e riempite con i file del backup.
        /// Così il gioco ritrova esattamente la situazione salvata, senza file più recenti mescolati.
        /// </summary>
        public static CleanResult RestoreBackup(AppEntry g, string zipPath, byte[] imagePng, Action<string, double> progress)
        {
            CheckNotRunning(g);
            var targets = Cleaner.BackupFileTargets(zipPath);
            foreach (var t in targets)
            {
                string problem = CheckUsable(t.Path);
                if (problem != null) throw new InvalidOperationException(t.Path + ": " + problem);
            }
            var current = targets.Select(t => new SaveLocationInfo { Path = t.Path, IsFile = t.IsFile, Category = ItemCategory.Saves }).ToList();
            if (HasContent(current.Select(c => c.Path)))
            {
                if (progress != null) progress(L.T("Backup dei salvataggi attuali…"), 0.05);
                // il backup da ripristinare non deve sparire con la conservazione degli ultimi N backup
                string before = Backup(g, current, BeforeRestoreReason, imagePng, CancellationToken.None, zipPath);
                Log.Info("Salvataggi di {0}: backup prima del ripristino {1}", g.Name, before);
            }
            if (progress != null) progress(L.T("Tolgo i salvataggi attuali…"), 0.2);
            foreach (var t in targets) ClearLocation(t.Path, t.IsFile);
            var result = Cleaner.Restore(zipPath, true, (s, d) => { if (progress != null) progress(s, 0.3 + 0.7 * d); });
            Log.Info("Salvataggi di {0} ripristinati da {1}", g.Name, zipPath);
            return result;
        }

        /// <summary>Svuota una posizione (i file vanno nel Cestino se possibile: c'è comunque il backup appena fatto).</summary>
        private static void ClearLocation(string path, bool isFile)
        {
            string l = PathUtil.Long(path);
            if (isFile)
            {
                if (!File.Exists(l)) return;
                if (UseRecycleBin) { try { Native.SendToRecycleBin(path); } catch { } }
                if (File.Exists(l)) File.Delete(l);
                return;
            }
            if (!Directory.Exists(l)) return;
            foreach (var entry in Directory.GetFileSystemEntries(l))
            {
                string p = entry.StartsWith(@"\\?\") ? entry.Substring(4) : entry;
                if (UseRecycleBin) { try { Native.SendToRecycleBin(p); } catch { } }
                if (File.Exists(entry)) File.Delete(entry);
                else if (Directory.Exists(entry))
                {
                    // un collegamento si toglie senza toccare la cartella a cui punta
                    if (PathUtil.IsReparsePoint(p)) Directory.Delete(entry, false);
                    else Directory.Delete(entry, true);
                }
            }
        }

        // ------------------------------------------------------------------ Profili

        /// <summary>Solleva un'eccezione se il gioco è aperto: i suoi file sarebbero in uso o verrebbero riscritti subito.</summary>
        public static void CheckNotRunning(AppEntry g)
        {
            var running = UninstallSession.RunningFrom(g);
            if (running.Length > 0)
                throw new InvalidOperationException(L.F("Il gioco è aperto ({0}): chiudilo e riprova.", string.Join(", ", running)));
        }

        private static string KeyFor(SaveVaultState st, string path, bool isFile)
        {
            var m = st.Keys.FirstOrDefault(k => PathUtil.Same(k.Path, path));
            if (m != null) return m.Key;
            string leaf = PathUtil.SafeFileName(Path.GetFileName(path.TrimEnd('\\')) ?? "cartella");
            if (leaf.Length > 40) leaf = leaf.Substring(0, 40);
            string key = (st.Keys.Count + 1).ToString("00") + " " + leaf;
            st.Keys.Add(new SaveMapping { Key = key, Path = path, IsFile = isFile });
            return key;
        }

        /// <summary>Crea il primo profilo ("Principale") con i salvataggi attuali, se non esiste ancora nessun profilo.</summary>
        public static void EnsureActiveProfile(SaveVaultState st, string gameDir)
        {
            if (st.Active != null) return;
            var p = new SaveProfile
            {
                Id = NewId(st, gameDir), Name = st.Profiles.Count == 0 ? L.T("Principale") : FreeProfileName(st.Profiles.Select(x => x.Name)), Created = Now(), LastUsed = Now(),
            };
            st.Profiles.Add(p);
            st.ActiveProfile = p.Id;
        }

        /// <summary>Identificativo nuovo: mai quello di un profilo esistente né di una cartella rimasta nel deposito (i suoi file finirebbero nel profilo nuovo).</summary>
        private static string NewId(SaveVaultState st, string gameDir)
        {
            for (int n = 1; ; n++)
            {
                string id = "p" + n;
                if (st.Profiles.Any(p => p.Id == id)) continue;
                if (gameDir != null && Directory.Exists(PathUtil.Long(Path.Combine(gameDir, "profiles", id)))) continue;
                return id;
            }
        }

        /// <summary>
        /// Cambia profilo: i file del profilo in uso vengono messi da parte nel deposito, quelli del profilo scelto tornano
        /// nelle cartelle del gioco. Con <paramref name="targetId"/> null si crea un profilo nuovo e vuoto ("nuova partita")
        /// chiamato <paramref name="newName"/>. In caso di errore tutti gli spostamenti vengono annullati.
        /// </summary>
        public static SaveProfile Switch(AppEntry g, SaveVaultState st, IEnumerable<SaveLocationInfo> current, string targetId, string newName, Action<string> progress)
        {
            CheckNotRunning(g);
            string gameDir = GameDir(g);
            EnsureActiveProfile(st, gameDir);
            var active = st.Active;
            SaveProfile target;
            if (targetId == null)
            {
                target = new SaveProfile
                {
                    Id = NewId(st, gameDir), Name = string.IsNullOrWhiteSpace(newName) ? FreeProfileName(st.Profiles.Select(x => x.Name)) : newName.Trim(), Created = Now(),
                };
            }
            else
            {
                target = st.Profiles.FirstOrDefault(p => p.Id == targetId);
                if (target == null) throw new InvalidOperationException(L.T("Profilo non trovato."));
                if (target == active) return active;
            }

            // posizioni da mettere da parte: quelle trovate ora più tutte quelle già usate dai profili
            var paths = new List<SaveMapping>();
            foreach (var l in current) if (!paths.Any(p => PathUtil.Same(p.Path, l.Path))) paths.Add(new SaveMapping { Path = l.Path, IsFile = l.IsFile });
            foreach (var k in st.Keys) if (!paths.Any(p => PathUtil.Same(p.Path, k.Path))) paths.Add(new SaveMapping { Path = k.Path, IsFile = k.IsFile });
            foreach (var m in target.Stored) if (!paths.Any(p => PathUtil.Same(p.Path, m.Path))) paths.Add(new SaveMapping { Path = m.Path, IsFile = m.IsFile });
            foreach (var p in paths)
            {
                string problem = CheckUsable(p.Path);
                if (problem != null) throw new InvalidOperationException(p.Path + ": " + problem);
            }

            string activeDir = Path.Combine(gameDir, "profiles", active.Id);
            string targetDir = Path.Combine(gameDir, "profiles", target.Id);
            // cartelle vuote rimaste da un tentativo annullato non contano: conta solo se ci sono file veri
            RemoveEmptyDirs(activeDir);
            if (Directory.Exists(PathUtil.Long(activeDir)) && Directory.EnumerateFileSystemEntries(PathUtil.Long(activeDir)).Any())
                throw new InvalidOperationException(L.F("La cartella del profilo «{0}» nel deposito non è vuota ({1}): controllala prima di continuare.", active.Name, activeDir));

            var journal = new List<KeyValuePair<string, string>>();
            var stored = new List<SaveMapping>();
            try
            {
                // 1) il profilo in uso va nel deposito
                foreach (var p in paths)
                {
                    string l = PathUtil.Long(p.Path);
                    bool exists = p.IsFile ? File.Exists(l) : Directory.Exists(l);
                    if (!exists) continue;
                    if (progress != null) progress(L.F("Metto da parte: {0}", p.Path));
                    string key = KeyFor(st, p.Path, p.IsFile);
                    string dest = Path.Combine(activeDir, key);
                    if (p.IsFile) MoveEntry(p.Path, Path.Combine(dest, Path.GetFileName(p.Path)), journal);
                    else MoveContents(p.Path, dest, journal);
                    stored.Add(new SaveMapping { Key = key, Path = p.Path, IsFile = p.IsFile });
                }
                // 2) il profilo scelto torna nelle cartelle del gioco
                foreach (var m in target.Stored)
                {
                    string src = Path.Combine(targetDir, m.Key);
                    if (!Directory.Exists(PathUtil.Long(src))) continue;
                    if (progress != null) progress(L.F("Rimetto a posto: {0}", m.Path));
                    if (m.IsFile) MoveEntry(Path.Combine(src, Path.GetFileName(m.Path)), m.Path, journal);
                    else MoveContents(src, m.Path, journal);
                }
            }
            catch (Exception ex)
            {
                Log.Error(L.F("Cambio di profilo di {0}", g.Name), ex);
                string rollback = Rollback(journal);
                // solo le cartelle create in questo tentativo (il deposito del profilo in uso era vuoto)
                RemoveEmptyDirs(activeDir);
                throw new InvalidOperationException(L.F("Cambio di profilo non riuscito: {0}{1}", ex.Message, (rollback == null ? L.T(" Nulla è stato cambiato.") : L.F(" Attenzione: {0}", rollback))), ex);
            }

            active.Stored = stored;
            active.LastUsed = Now();
            target.Stored = new List<SaveMapping>();
            target.LastUsed = Now();
            if (!st.Profiles.Contains(target)) st.Profiles.Add(target);
            st.ActiveProfile = target.Id;
            Store(g, st);
            RemoveEmptyDirs(targetDir);
            Log.Info("Salvataggi di {0}: profilo «{1}» messo da parte, ora in uso «{2}»", g.Name, active.Name, target.Name);
            return target;
        }

        /// <summary>Elimina un profilo messo da parte (i suoi file vanno nel Cestino). Il profilo in uso non si elimina.</summary>
        public static void DeleteProfile(AppEntry g, SaveVaultState st, string id)
        {
            var p = st.Profiles.FirstOrDefault(x => x.Id == id);
            if (p == null) return;
            if (p.Id == st.ActiveProfile) throw new InvalidOperationException(L.T("Il profilo in uso non si può eliminare: passa prima a un altro profilo."));
            string dir = Path.Combine(GameDir(g), "profiles", p.Id);
            if (Directory.Exists(PathUtil.Long(dir)))
            {
                if (UseRecycleBin)
                {
                    try { Native.SendToRecycleBin(dir); }
                    catch (Exception ex) { Log.Warn("Profilo {0} non spostato nel Cestino ({1}): eliminato", p.Name, ex.Message); }
                }
                if (Directory.Exists(PathUtil.Long(dir))) Directory.Delete(PathUtil.Long(dir), true);
            }
            st.Profiles.Remove(p);
            Log.Info("Salvataggi di {0}: profilo «{1}» eliminato", g.Name, p.Name);
            // rimasto solo il profilo in uso: il deposito del gioco non serve più
            if (!RemoveIfUnused(g, st)) Store(g, st);
        }

        public static void RenameProfile(AppEntry g, SaveVaultState st, string id, string name)
        {
            var p = st.Profiles.FirstOrDefault(x => x.Id == id);
            if (p == null || string.IsNullOrWhiteSpace(name)) return;
            p.Name = name.Trim();
            Store(g, st);
        }

        // ------------------------------------------------------------------ Spostamenti con giornale

        /// <summary>Sposta il contenuto di una cartella (non la cartella stessa, che resta: il gioco se la aspetta).</summary>
        private static void MoveContents(string src, string dst, List<KeyValuePair<string, string>> journal)
        {
            string ls = PathUtil.Long(src);
            Directory.CreateDirectory(PathUtil.Long(dst));
            foreach (var entry in Directory.GetFileSystemEntries(ls))
            {
                string p = entry.StartsWith(@"\\?\") ? entry.Substring(4) : entry;
                MoveEntry(p, Path.Combine(dst, Path.GetFileName(p)), journal);
            }
        }

        private static void MoveEntry(string from, string to, List<KeyValuePair<string, string>> journal)
        {
            string lf = PathUtil.Long(from), lt = PathUtil.Long(to);
            if (File.Exists(lt) || Directory.Exists(lt)) throw new IOException(L.F("Esiste già: {0}", to));
            Directory.CreateDirectory(Path.GetDirectoryName(lt));
            bool isDir = Directory.Exists(lf);
            bool sameVolume = string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase);
            if (sameVolume || (isDir && PathUtil.IsReparsePoint(from)))
            {
                // stesso disco: rinomina istantanea (anche un collegamento viene spostato come collegamento)
                if (isDir) Directory.Move(lf, lt); else File.Move(lf, lt);
            }
            else if (isDir)
            {
                CopyDir(from, to);
                Directory.Delete(lf, true);
            }
            else
            {
                File.Copy(lf, lt);
                if (new FileInfo(lt).Length != new FileInfo(lf).Length) throw new IOException(L.F("Copia incompleta: {0}", from));
                File.Delete(lf);
            }
            journal.Add(new KeyValuePair<string, string>(from, to));
        }

        private static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(PathUtil.Long(to));
            foreach (var d in PathUtil.DirsNoLinks(from))
                Directory.CreateDirectory(PathUtil.Long(to + d.Substring(from.Length)));
            foreach (var f in PathUtil.FilesNoLinks(from))
            {
                string t = to + f.Substring(from.Length);
                File.Copy(PathUtil.Long(f), PathUtil.Long(t));
                if (new FileInfo(PathUtil.Long(t)).Length != new FileInfo(PathUtil.Long(f)).Length) throw new IOException(L.F("Copia incompleta: {0}", f));
            }
        }

        /// <summary>Annulla gli spostamenti (dall'ultimo al primo). Null se tutto è tornato a posto, altrimenti cosa è rimasto dove.</summary>
        private static string Rollback(List<KeyValuePair<string, string>> journal)
        {
            var failed = new List<string>();
            for (int i = journal.Count - 1; i >= 0; i--)
            {
                var j = journal[i];
                try
                {
                    var none = new List<KeyValuePair<string, string>>();
                    MoveEntry(j.Value, j.Key, none);
                }
                catch (Exception ex)
                {
                    Log.Error("Annullamento dello spostamento " + j.Value + " → " + j.Key, ex);
                    failed.Add(j.Value);
                }
            }
            return failed.Count == 0 ? null : L.F("{0} nel deposito ({1}{2}).", Fmt.Count(failed.Count, "elemento è rimasto", "elementi sono rimasti"), failed[0], (failed.Count > 1 ? "…" : ""));
        }

        private static void RemoveEmptyDirs(string dir)
        {
            try
            {
                string l = PathUtil.Long(dir);
                if (!Directory.Exists(l)) return;
                foreach (var d in Directory.GetDirectories(l)) RemoveEmptyDirs(d.StartsWith(@"\\?\") ? d.Substring(4) : d);
                if (!Directory.EnumerateFileSystemEntries(l).Any()) Directory.Delete(l);
            }
            catch { }
        }
    }
}
