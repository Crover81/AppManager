using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace AppManager
{
#pragma warning disable 0649 // i campi dell'applicazione vengono impostati dall'interfaccia (non compilata con il Harness)
    internal sealed class CleanOptions
    {
        public bool UseRecycleBin = true;
        public bool Backup = true;
        public string BackupFolder;
        public string Title;
        // identità dell'applicazione a cui appartiene il backup (null per i residui: ogni gruppo ha il suo backup)
        public string AppName;
        public string AppId;
        public AppKind AppKind;
        public string Reason = "Pulizia";          // Disinstallazione, Pulizia dei dati, Residui, Salvataggi...
        public byte[] ImagePng;                     // copertina o icona, salvata nel backup
        public string ImageFile;                    // in alternativa: file immagine da includere
    }
#pragma warning restore 0649

    /// <summary>A quale applicazione appartiene un backup (scritto nel manifest).</summary>
    internal sealed class BackupMeta
    {
        public string AppName;
        public string AppId;
        public AppKind AppKind;
        public string Reason;
        public byte[] ImagePng;
        public string ImageFile;
    }

    internal sealed class CleanResult
    {
        public int Removed;
        public long FreedBytes;
        public bool Cancelled;
        public string BackupPath;
        public readonly List<string> BackupPaths = new List<string>();
        public readonly List<string> RecycleFolders = new List<string>();   // cartelle «AppManager - …» mandate nel Cestino
        public readonly List<string> KeptFolders = new List<string>();      // cartelle che il Cestino ha rifiutato (restano sul disco)
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public readonly List<LeftoverItem> Failed = new List<LeftoverItem>();
    }

    internal sealed class BackupInfo
    {
        public string ZipPath;
        public string Title;
        public DateTime Created;
        public long Size;
        public int ItemCount;
        public int RegistryCount;
        public string AppName;          // applicazione (per i backup precedenti: ricavata dal titolo)
        public string AppId;
        public AppKind? AppKind;        // null se non si sa (backup precedenti)
        public string Reason;
        public bool HasImage;
        public List<string> Paths = new List<string>();   // posizioni originali degli elementi (per "Mostra contenuto")
    }

    /// <summary>Esegue la pulizia: backup (zip), eliminazione, rimozione delle cartelle/chiavi rimaste vuote.</summary>
    internal static class Cleaner
    {
        public static CleanResult Clean(IList<LeftoverItem> items, CleanOptions opt, Action<string, double> progress, CancellationToken ct)
        {
            var result = new CleanResult();
            var todo = new List<LeftoverItem>();
            foreach (var i in items)
            {
                // si lavora sul percorso completo e normalizzato, lo stesso che è stato controllato
                if (i.IsFileSystem)
                {
                    string n = PathUtil.Normalize(i.Path);
                    if (n != null && n != i.Path) i.Path = n;
                }
                if (Safety.IsDeletable(i)) todo.Add(i);
                else { result.Failed.Add(i); result.Errors.Add(L.F("Protetto, non eliminato: {0}", i.DisplayPath)); }
            }
            Log.Info("Pulizia \"{0}\": {1} elementi (cestino={2}, backup={3})", opt.Title, todo.Count, opt.UseRecycleBin, opt.Backup);

            // 1) backup di salvataggi, impostazioni e registro
            var notBackedUp = new HashSet<LeftoverItem>();
            var backedUp = new HashSet<LeftoverItem>();
            if (opt.Backup && todo.Any(i => Categories.IsBackedUp(i.Category)))
            {
                // un backup dentro una cartella da eliminare sparirebbe insieme a lei
                string folder = PathUtil.Normalize(string.IsNullOrEmpty(opt.BackupFolder) ? AppPaths.DefaultBackupDir : opt.BackupFolder);
                var container = todo.FirstOrDefault(i => i.Kind == ItemKind.Directory && folder != null && PathUtil.IsUnder(folder, i.Path, true));
                string problem = folder == null ? L.T("La cartella dei backup non è valida")
                    : container != null ? L.F("La cartella dei backup ({0}) è dentro {1}, che sta per essere eliminata", folder, container.Path)
                    : Safety.IsSystemPath(folder) ? L.F("La cartella dei backup ({0}) è una cartella di Windows", folder)
                    : null;
                if (problem != null)
                {
                    result.Errors.Add(L.F("{0}: scegli un'altra cartella dei backup nelle Impostazioni. Nessun file eliminato.", problem));
                    result.Failed.AddRange(todo);
                    return result;
                }
                opt.BackupFolder = folder;
                if (progress != null) progress(L.T("Creo il backup…"), 0);
                try
                {
                    // un backup per applicazione: nella sezione Backup si capisce subito di chi è ogni file
                    foreach (var part in SplitByApp(todo.Where(i => Categories.IsBackedUp(i.Category)).ToList(), opt))
                    {
                        string zip = CreateBackup(part.Value, folder, part.Key, notBackedUp, backedUp, ct);
                        if (result.BackupPath == null) result.BackupPath = zip;
                        result.BackupPaths.Add(zip);
                        Log.Info("Backup creato: {0}", zip);
                        if (AppSettings.Current.BackupsToKeep > 0) PruneBackups(folder, part.Key, AppSettings.Current.BackupsToKeep, zip, null);
                    }
                }
                catch (OperationCanceledException)
                {
                    result.Cancelled = true;
                    result.Errors.Add(L.T("Pulizia annullata durante il backup: nessun file eliminato."));
                    result.Failed.AddRange(todo);
                    return result;
                }
                catch (Exception ex)
                {
                    Log.Error("Backup", ex);
                    result.Errors.Add(L.F("Backup non riuscito ({0}): pulizia annullata, nessun file eliminato.", ex.Message));
                    result.Failed.AddRange(todo);
                    return result;
                }
            }

            // 2) eliminazione: prima collegamenti e file, poi cartelle, poi registro, attività, servizi e firewall.
            //    Con il Cestino tutto ciò che si toglie viene raccolto in una cartella per applicazione (vedi RecycleStaging)
            var staging = opt.UseRecycleBin ? new RecycleStaging() : null;
            var ordered = todo.OrderBy(i => DeleteOrder(i.Kind)).ToList();
            for (int n = 0; n < ordered.Count; n++)
            {
                if (ct.IsCancellationRequested)
                {
                    // interruzione: quello che è già stato eliminato resta nel risultato, il resto non si tocca
                    result.Cancelled = true;
                    result.Failed.AddRange(ordered.Skip(n));
                    result.Errors.Add(L.F("Pulizia interrotta: {0}.", Fmt.Count(ordered.Count - n, "elemento non eliminato", "elementi non eliminati")));
                    Log.Info("Pulizia interrotta dopo {0} elementi", n);
                    break;
                }
                var i = ordered[n];
                if (progress != null) progress(L.F("Elimino: {0}", i.DisplayPath), 0.1 + 0.9 * n / Math.Max(1, ordered.Count));
                if (notBackedUp.Contains(i))
                {
                    result.Failed.Add(i);
                    result.Errors.Add(L.F("Saltato perché il backup non è riuscito: {0}", i.DisplayPath));
                    continue;
                }
                try
                {
                    long size = Math.Max(0, i.Size);
                    if (i.Kind == ItemKind.Directory && i.Category == ItemCategory.Cache)
                    {
                        // le cache si rigenerano: niente Cestino, si elimina file per file saltando quelli in uso
                        int locked, scheduled;
                        DeleteCacheDirectory(i.Path, out locked, out scheduled);
                        result.Removed++;
                        result.FreedBytes += size;
                        Log.Info("Eliminato (cache, {0} file in uso): {1}", locked, i.DisplayPath);
                        if (locked > 0)
                            result.Errors.Add(L.F("{0} in {1}", Fmt.Count(locked, "file in uso", "file in uso"), i.Path) + (scheduled == locked ? L.T(": verranno eliminati al prossimo riavvio del PC") : L.T(": chiudi i giochi e le app aperte e riprova")));
                        CleanEmptyParents(i);
                        continue;
                    }
                    if (i.IsFileSystem && PathUtil.IsReparsePoint(i.Path))
                    {
                        // l'elemento stesso è un collegamento: si toglie solo quello, i dati a cui punta restano dove sono
                        string target = Native.FinalPath(i.Path);
                        if (i.Kind == ItemKind.Directory) Directory.Delete(PathUtil.Long(i.Path), false);
                        else File.Delete(PathUtil.Long(i.Path));
                        result.Removed++;
                        result.Notes.Add(L.F("Era un collegamento{0}: è stato rimosso solo il collegamento, i dati a cui puntava non sono stati toccati ({1}).", (target != null ? " a " + target : ""), i.Path));
                        Log.Info("Rimosso il collegamento {0} (destinazione {1} intatta)", i.Path, target ?? "?");
                        CleanEmptyParents(i);
                        continue;
                    }
                    string app = !string.IsNullOrEmpty(opt.AppName) ? opt.AppName : !string.IsNullOrEmpty(i.GroupName) ? i.GroupName : opt.Title;
                    if (staging != null && i.IsFileSystem && RecycleStaging.Supports(i.Path) && PathUtil.Exists(i.Path))
                    {
                        string moved = null;
                        try
                        {
                            // i collegamenti interni si tolgono prima, senza seguirli: nel Cestino finisce solo il contenuto vero
                            if (i.Kind == ItemKind.Directory) RemoveLinks(i.Path);
                            moved = staging.Move(i, app);
                        }
                        catch (Exception ex)
                        {
                            // file aperti dentro la cartella, un'unità montata in una cartella...: si torna al Cestino elemento per elemento
                            // (che segnala l'errore se non riesce nemmeno lui); niente è stato spostato
                            if (!(ex is IOException || ex is UnauthorizedAccessException)) throw;
                            Log.Info("Raccolta per il Cestino non riuscita per {0} ({1}): lo mando nel Cestino da solo", i.DisplayPath, ex.Message);
                        }
                        if (moved != null)
                        {
                            result.Removed++;
                            result.FreedBytes += size;
                            Log.Info("Nel Cestino ({0}): {1}", RecycleStaging.FolderName(app), i.DisplayPath);
                            CleanEmptyParents(i);
                            continue;
                        }
                    }
                    if (staging != null && !i.IsFileSystem)
                    {
                        // copia della voce di sistema in «File registro»: senza la copia non si toglie
                        try { staging.SaveSystemCopy(i, app, TaskXml); }
                        catch (Exception ex)
                        {
                            Log.Error(L.F("Copia per il Cestino di {0}", i.DisplayPath), ex);
                            result.Failed.Add(i);
                            result.Errors.Add(L.F("Non eliminato perché non è stato possibile salvarne una copia in «{0}»: {1}", RecycleStaging.RegistryFolder, i.DisplayPath));
                            continue;
                        }
                    }
                    bool recycled = opt.UseRecycleBin && i.IsFileSystem;
                    try { Delete(i, opt.UseRecycleBin); }
                    catch (RecycleBinException ex)
                    {
                        // il Cestino rifiuta alcuni elementi (percorsi troppo lunghi, dischi senza Cestino, conferma negata):
                        // se sono già nel backup (o si rigenerano da soli) si eliminano definitivamente, altrimenti si segnala
                        if (!(backedUp.Contains(i) || i.Category == ItemCategory.Cache || i.Category == ItemCategory.Shortcuts)) throw;
                        Log.Info("Cestino non disponibile per {0} ({1}): eliminazione definitiva", i.DisplayPath, ex.Message);
                        Delete(i, false);
                        recycled = false;
                        result.Notes.Add(L.F("Eliminato definitivamente perché il Cestino non lo accetta{0}: {1}", (backedUp.Contains(i) ? L.T(" (è comunque nel backup)") : ""), i.Path));
                    }
                    result.Removed++;
                    result.FreedBytes += size;
                    Log.Info("Eliminato ({0}): {1}", recycled ? L.T("cestino") : L.T("definitivo"), i.DisplayPath);
                    CleanEmptyParents(i);
                }
                catch (Exception ex)
                {
                    Log.Error("Eliminazione " + i.DisplayPath, ex);
                    result.Failed.Add(i);
                    result.Errors.Add(Describe(ex) + ": " + i.DisplayPath);
                }
            }
            // 3) le cartelle «AppManager - …» nel Cestino (anche dopo un'interruzione: quello che è stato raccolto ci va)
            if (staging != null)
            {
                if (progress != null) progress(L.T("Sposto nel Cestino…"), 0.98);
                staging.SendAll(result);
            }
            if (progress != null) progress(L.T("Fatto"), 1);
            return result;
        }

        private static int DeleteOrder(ItemKind k)
        {
            switch (k)
            {
                case ItemKind.File: return 0;
                case ItemKind.Directory: return 1;
                case ItemKind.RegistryValue: case ItemKind.PathEntry: return 2;
                case ItemKind.RegistryKey: return 3;
                case ItemKind.ScheduledTask: case ItemKind.Service: return 4;
                default: return 5;
            }
        }

        private static string Describe(Exception ex)
        {
            if (ex is UnauthorizedAccessException || ex is System.Security.SecurityException) return L.T("Accesso negato (serve l'amministratore o il file è in uso)");
            var com = ex as System.Runtime.InteropServices.COMException;
            if (com != null && (uint)com.ErrorCode == 0x80070005) return L.T("Accesso negato (serve l'amministratore)");
            if (ex is RecycleBinException) return L.F("{0} (puoi disattivare il Cestino nelle opzioni della pulizia per eliminarlo definitivamente)", ex.Message);
            if (ex is PathTooLongException) return L.T("Percorso troppo lungo");
            if (ex is DirectoryNotFoundException || ex is FileNotFoundException) return L.T("Non trovato (forse già eliminato)");
            if (ex is IOException) return L.T("File in uso o non accessibile");
            return ex.Message;
        }

        // ------------------------------------------------------------------ Eliminazione

        public static void Delete(LeftoverItem i, bool recycle)
        {
            switch (i.Kind)
            {
                case ItemKind.File:
                    string file = PathUtil.Long(i.Path);
                    if (!File.Exists(file)) return;
                    if (recycle) Native.SendToRecycleBin(i.Path);
                    else { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
                    break;
                case ItemKind.Directory:
                    if (!Directory.Exists(PathUtil.Long(i.Path))) return;
                    if (recycle)
                    {
                        // i collegamenti interni (junction, link simbolici) si tolgono prima, senza seguirli:
                        // nel Cestino finisce solo il contenuto vero della cartella
                        RemoveLinks(i.Path);
                        Native.SendToRecycleBin(i.Path);
                    }
                    else DeleteDirectory(i.Path);
                    break;
                case ItemKind.RegistryKey:
                    DeleteRegistryKey(i.Path);
                    break;
                case ItemKind.RegistryValue:
                    using (var k = RegUtil.Open(i.Path, true))
                        if (k != null) k.DeleteValue(i.ValueName, false);
                    break;
                case ItemKind.FirewallRule:
                    DeleteFirewallRules(i.Path);
                    break;
                case ItemKind.ScheduledTask:
                    DeleteTask(i.Path);
                    break;
                case ItemKind.Service:
                    DeleteService(i.Path);
                    break;
                case ItemKind.PathEntry:
                    RemovePathEntry(i.Path, i.ValueName, i.Data);
                    break;
            }
        }

        // ------------------------------------------------------------------ Attività pianificate, servizi, PATH

        private static dynamic TaskService()
        {
            dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
            svc.Connect();
            return svc;
        }

        private static void SplitTaskPath(string taskPath, out string folder, out string name)
        {
            int slash = taskPath.LastIndexOf('\\');
            folder = slash <= 0 ? "\\" : taskPath.Substring(0, slash);
            name = taskPath.Substring(slash + 1);
        }

        private static void DeleteTask(string taskPath)
        {
            string folderPath, name;
            SplitTaskPath(taskPath, out folderPath, out name);
            dynamic svc = TaskService();
            dynamic folder;
            try { folder = svc.GetFolder(folderPath); }
            catch (System.Runtime.InteropServices.COMException) { return; } // cartella inesistente: attività già eliminata
            try { folder.GetTask(name); }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                if ((uint)ex.ErrorCode == 0x80070005) throw; // esiste ma non è accessibile
                return;
            }
            folder.DeleteTask(name, 0);
        }

        /// <summary>XML dell'attività (per il backup), o null se non esiste.</summary>
        private static string TaskXml(string taskPath)
        {
            string folderPath, name;
            SplitTaskPath(taskPath, out folderPath, out name);
            try
            {
                dynamic svc = TaskService();
                dynamic task = svc.GetFolder(folderPath).GetTask(name);
                return task.Xml;
            }
            catch { return null; }
        }

        /// <summary>Rimuove le cartelle dell'Utilità di pianificazione rimaste vuote (mai quelle di Windows).</summary>
        private static void DeleteEmptyTaskFolders(string taskPath)
        {
            string folderPath, name;
            SplitTaskPath(taskPath, out folderPath, out name);
            dynamic svc = TaskService();
            for (int n = 0; n < 3 && folderPath.Length > 1 && !folderPath.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase); n++)
            {
                dynamic folder = svc.GetFolder(folderPath);
                if ((int)folder.GetTasks(1).Count > 0 || (int)folder.GetFolders(0).Count > 0) break;
                string parentPath, child;
                SplitTaskPath(folderPath, out parentPath, out child);
                svc.GetFolder(parentPath).DeleteFolder(child, 0);
                Log.Info("Rimossa cartella vuota delle attività: {0}", folderPath);
                folderPath = parentPath;
            }
        }

        private static void DeleteService(string name)
        {
            if (!RegUtil.KeyExists(Traces.ServicesRoot + "\\" + name)) return;
            int code;
            RunTool("sc.exe", "stop \"" + name + "\"", 15000, out code); // se è in esecuzione; altrimenti l'errore non conta
            string output = RunTool("sc.exe", "delete \"" + name + "\"", 30000, out code);
            // 1060 = il servizio non esiste; 1072 = già contrassegnato per l'eliminazione (sparirà al riavvio)
            if (code != 0 && code != 1060 && code != 1072)
                throw new UnauthorizedAccessException(output.Trim());
        }

        private static string RunTool(string exe, string args, int timeoutMs, out int exitCode)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } exitCode = -1; return output; }
                exitCode = p.ExitCode;
                return output;
            }
        }

        private static bool SamePathEntry(string a, string b)
        {
            return string.Equals((a ?? "").Trim().TrimEnd('\\'), (b ?? "").Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Toglie una cartella dalla variabile PATH, lasciando intatte le altre.</summary>
        private static void RemovePathEntry(string keyPath, string valueName, string entry)
        {
            using (var k = RegUtil.Open(keyPath, true))
            {
                if (k == null) return;
                var kind = k.GetValueKind(valueName);
                string raw = k.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (raw == null) return;
                var parts = raw.Split(';').ToList();
                if (parts.RemoveAll(x => SamePathEntry(x, entry)) == 0) return;
                k.SetValue(valueName, string.Join(";", parts), kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
            }
            Native.BroadcastEnvironmentChange();
        }

        /// <summary>Rimette una cartella nella variabile PATH (ripristino da backup).</summary>
        private static void AddPathEntry(string keyPath, string valueName, string entry)
        {
            using (var k = RegUtil.Open(keyPath, true))
            {
                if (k == null) throw new InvalidOperationException(L.T("Variabili d'ambiente non accessibili"));
                var kind = RegUtil.ValueNames(k).Contains(valueName, StringComparer.OrdinalIgnoreCase) ? k.GetValueKind(valueName) : RegistryValueKind.ExpandString;
                string raw = (k.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string) ?? "";
                if (raw.Split(';').Any(x => SamePathEntry(x, entry))) return;
                string updated = raw.Length == 0 ? entry : raw.TrimEnd(';') + ";" + entry;
                k.SetValue(valueName, updated, kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
            }
            Native.BroadcastEnvironmentChange();
        }

        /// <summary>
        /// Elimina il contenuto di una cartella di cache; i file in uso vengono programmati per il riavvio.
        /// I collegamenti (junction, link simbolici) vengono rimossi come tali: ciò a cui puntano non si tocca.
        /// </summary>
        private static void DeleteCacheDirectory(string path, out int locked, out int scheduled)
        {
            locked = 0;
            scheduled = 0;
            if (!Directory.Exists(PathUtil.Long(path))) return;
            if (PathUtil.IsReparsePoint(path)) { Directory.Delete(PathUtil.Long(path), false); return; }
            foreach (var f in PathUtil.FilesNoLinks(path).ToList())
            {
                string lf = PathUtil.Long(f);
                try
                {
                    File.SetAttributes(lf, FileAttributes.Normal);
                    File.Delete(lf);
                }
                catch
                {
                    locked++;
                    if (Native.DeleteAtReboot(lf)) scheduled++;
                }
            }
            var links = new List<string>();
            var dirs = PathUtil.DirsNoLinks(path, links);
            foreach (var l in links)
            {
                try { Directory.Delete(PathUtil.Long(l), false); } catch { }
            }
            foreach (var d in dirs.OrderByDescending(d => d.Length))
            {
                try { ClearReadOnly(d); Directory.Delete(PathUtil.Long(d)); } catch { }
            }
            try { ClearReadOnly(path); Directory.Delete(PathUtil.Long(path)); } catch { }
        }

        /// <summary>Toglie i collegamenti a cartelle (a qualunque livello) senza toccare ciò a cui puntano.</summary>
        private static void RemoveLinks(string path)
        {
            var links = new List<string>();
            PathUtil.DirsNoLinks(path, links);
            foreach (var l in links)
            {
                Directory.Delete(PathUtil.Long(l), false);
                Log.Info("Rimosso il collegamento {0} (la destinazione resta intatta)", l);
            }
        }

        private static void ClearReadOnly(string dir)
        {
            var di = new DirectoryInfo(PathUtil.Long(dir));
            if ((di.Attributes & FileAttributes.ReadOnly) != 0) di.Attributes &= ~FileAttributes.ReadOnly;
        }

        /// <summary>
        /// Eliminazione definitiva di una cartella che non segue mai i collegamenti: junction e link simbolici
        /// vengono tolti come semplici collegamenti, senza entrare nella cartella a cui puntano.
        /// Funziona anche con i percorsi oltre i 260 caratteri.
        /// </summary>
        private static void DeleteDirectory(string path)
        {
            string lp = PathUtil.Long(path);
            if (PathUtil.IsReparsePoint(path)) { Directory.Delete(lp, false); return; }
            foreach (var sub in Directory.GetDirectories(lp))
            {
                string d = PathUtil.Plain(sub);
                if (PathUtil.IsReparsePoint(d)) Directory.Delete(PathUtil.Long(d), false);
                else DeleteDirectory(d);
            }
            foreach (var f in Directory.GetFiles(lp))
            {
                string lf = PathUtil.Long(PathUtil.Plain(f));
                try { File.SetAttributes(lf, FileAttributes.Normal); } catch { }
                File.Delete(lf);
            }
            ClearReadOnly(path);
            Directory.Delete(lp, false);
        }

        private static void DeleteRegistryKey(string fullPath)
        {
            int slash = fullPath.LastIndexOf('\\');
            if (slash < 0) throw new InvalidOperationException(L.T("Chiave non valida"));
            using (var parent = RegUtil.Open(fullPath.Substring(0, slash), true))
            {
                if (parent == null) return;
                parent.DeleteSubKeyTree(fullPath.Substring(slash + 1), false);
            }
        }

        private static void DeleteFirewallRules(string program)
        {
            var psi = new ProcessStartInfo("netsh.exe", "advfirewall firewall delete rule name=all program=\"" + program + "\"")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(30000);
                // netsh restituisce 1 anche quando non ci sono regole corrispondenti: lo consideriamo un successo
                if (p.ExitCode != 0 && !Traces.FirewallPrograms().All(e => !PathUtil.Same(e, program)))
                    throw new UnauthorizedAccessException(output.Trim());
            }
        }

        /// <summary>Rimuove le cartelle (e chiavi) genitrici rimaste vuote, senza mai superare le cartelle protette.</summary>
        private static void CleanEmptyParents(LeftoverItem i)
        {
            try
            {
                if (i.Kind == ItemKind.File || i.Kind == ItemKind.Directory)
                {
                    string parent = Path.GetDirectoryName(i.Path);
                    for (int n = 0; n < 4 && parent != null && Safety.IsDeletablePath(parent) && Directory.Exists(PathUtil.Long(parent))
                                    && !PathUtil.IsReparsePoint(parent) && !Directory.EnumerateFileSystemEntries(PathUtil.Long(parent)).Any(); n++)
                    {
                        Directory.Delete(PathUtil.Long(parent));
                        Log.Info("Rimossa cartella vuota: {0}", parent);
                        parent = Path.GetDirectoryName(parent);
                    }
                }
                else if (i.Kind == ItemKind.RegistryKey)
                {
                    string parent = i.Path.Substring(0, i.Path.LastIndexOf('\\'));
                    for (int n = 0; n < 3 && Safety.IsDeletableRegistryKey(parent); n++)
                    {
                        using (var k = RegUtil.Open(parent))
                        {
                            if (k == null || k.SubKeyCount > 0 || k.ValueCount > 0) break;
                        }
                        DeleteRegistryKey(parent);
                        Log.Info("Rimossa chiave vuota: {0}", parent);
                        parent = parent.Substring(0, parent.LastIndexOf('\\'));
                    }
                }
                else if (i.Kind == ItemKind.ScheduledTask)
                {
                    DeleteEmptyTaskFolders(i.Path);
                }
            }
            catch (Exception ex) { Log.Error("Pulizia cartelle vuote", ex); }
        }

        // ------------------------------------------------------------------ Backup

        /// <summary>
        /// Divide gli elementi per applicazione: con un'app indicata (disinstallazione, dati, salvataggi) è una sola;
        /// per i residui ogni gioco o programma orfano ha il suo backup.
        /// </summary>
        private static List<KeyValuePair<BackupMeta, List<LeftoverItem>>> SplitByApp(List<LeftoverItem> items, CleanOptions opt)
        {
            var parts = new List<KeyValuePair<BackupMeta, List<LeftoverItem>>>();
            if (!string.IsNullOrEmpty(opt.AppName))
            {
                parts.Add(new KeyValuePair<BackupMeta, List<LeftoverItem>>(new BackupMeta
                {
                    AppName = opt.AppName, AppId = opt.AppId, AppKind = opt.AppKind, Reason = opt.Reason, ImagePng = opt.ImagePng, ImageFile = opt.ImageFile,
                }, items));
                return parts;
            }
            foreach (var g in items.GroupBy(i => string.IsNullOrEmpty(i.GroupName) ? (opt.Title ?? L.T("Backup")) : i.GroupName))
            {
                var first = g.First();
                parts.Add(new KeyValuePair<BackupMeta, List<LeftoverItem>>(new BackupMeta
                {
                    AppName = g.Key, AppId = first.GroupKey, AppKind = first.GroupKind, Reason = opt.Reason,
                    ImageFile = first.GroupImage,
                }, g.ToList()));
            }
            return parts;
        }

        /// <summary>Sottocartella dei backup di un'applicazione (nome leggibile, senza caratteri non validi).</summary>
        internal static string AppFolder(string folder, string appName)
        {
            string name = PathUtil.SafeFileName(appName ?? L.T("Backup")).Trim().TrimEnd('.');
            if (name.Length > 60) name = name.Substring(0, 60).Trim();
            if (name.Length == 0) name = L.T("Backup");
            return Path.Combine(folder, name);
        }

        private static string CreateBackup(List<LeftoverItem> items, string folder, BackupMeta meta, HashSet<LeftoverItem> failed, HashSet<LeftoverItem> done, CancellationToken ct)
        {
            string dir = AppFolder(folder, meta.AppName);
            Directory.CreateDirectory(dir);
            string reason = PathUtil.SafeFileName(meta.Reason ?? L.T("Backup"));
            string stem = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + " - " + reason);
            string zipPath = stem + ".zip";
            // due pulizie nello stesso secondo con lo stesso titolo: il secondo backup non deve fallire né sovrascrivere il primo
            for (int k = 2; File.Exists(zipPath); k++) zipPath = stem + " (" + k + ").zip";
            var manifest = new List<Dictionary<string, object>>();
            try
            {
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    for (int n = 0; n < items.Count; n++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var i = items[n];
                        var entry = new Dictionary<string, object>
                        {
                            { "Id", n }, { "Kind", i.Kind.ToString() }, { "Category", i.Category.ToString() }, { "Path", i.Path }, { "ValueName", i.ValueName },
                        };
                        try
                        {
                            if (i.IsFileSystem && PathUtil.IsReparsePoint(i.Path))
                            {
                                done.Add(i); // un collegamento: si toglie solo quello, non c'è niente da salvare
                                continue;
                            }
                            if (!AddToBackup(zip, i, n, entry))
                            {
                                // non leggibile (es. permessi): senza backup non si elimina
                                if (StillExists(i)) failed.Add(i);
                                continue;
                            }
                            manifest.Add(entry);
                            done.Add(i);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Log.Error(L.F("Backup di {0}", i.DisplayPath), ex);
                            failed.Add(i);
                        }
                    }
                    var info = new Dictionary<string, object>
                    {
                        { "App", "AppManager" }, { "Version", 3 }, { "Title", meta.AppName }, { "Created", DateTime.Now.ToString("o") }, { "Items", manifest },
                        { "AppName", meta.AppName }, { "AppId", meta.AppId }, { "AppKind", meta.AppKind.ToString() }, { "Reason", meta.Reason },
                    };
                    if (AddImage(zip, meta)) info["Image"] = "image";
                    var me = zip.CreateEntry("manifest.json");
                    using (var w = new StreamWriter(me.Open(), new UTF8Encoding(false))) w.Write(Json.Serialize(info));
                }
            }
            catch
            {
                // disco pieno, cartella non scrivibile...: niente backup a metà
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                throw;
            }
            return zipPath;
        }

        /// <summary>
        /// Solo backup, senza eliminare nulla (backup manuale dei salvataggi). Gli elementi non leggibili vengono saltati;
        /// se non si è potuto salvare niente il file non viene creato.
        /// </summary>
        /// <param name="protect">backup da non togliere mai con la conservazione (es. quello che si sta per ripristinare)</param>
        public static string BackupOnly(List<LeftoverItem> items, BackupMeta meta, CancellationToken ct, string protect = null)
        {
            string folder = PathUtil.Normalize(AppSettings.Current.BackupFolder) ?? AppPaths.DefaultBackupDir;
            if (Safety.IsSystemPath(folder)) throw new InvalidOperationException(L.F("La cartella dei backup ({0}) è una cartella di Windows: scegline un'altra nelle Impostazioni.", folder));
            var failed = new HashSet<LeftoverItem>();
            var done = new HashSet<LeftoverItem>();
            string zip = CreateBackup(items, folder, meta, failed, done, ct);
            if (done.Count == 0)
            {
                try { File.Delete(zip); } catch { }
                throw new IOException(L.T("Nessun file è stato copiato nel backup (file in uso o non leggibili)."));
            }
            if (failed.Count > 0) Log.Warn("Backup di {0}: {1} elementi non leggibili saltati", meta.AppName, failed.Count);
            Log.Info("Backup creato: {0}", zip);
            if (AppSettings.Current.BackupsToKeep > 0) PruneBackups(folder, meta, AppSettings.Current.BackupsToKeep, zip, protect);
            return zip;
        }

        /// <summary>File e cartelle contenuti in un backup (dove torneranno), senza le voci di sistema.</summary>
        public static List<SaveMapping> BackupFileTargets(string zipPath)
        {
            var list = new List<SaveMapping>();
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var me = zip.GetEntry("manifest.json");
                if (me == null) throw new InvalidDataException(L.T("Il backup non contiene il manifest."));
                Dictionary<string, object> info;
                using (var r = new StreamReader(me.Open(), Encoding.UTF8)) info = Json.Parse(r.ReadToEnd()) as Dictionary<string, object>;
                var items = info == null ? null : info["Items"] as object[];
                if (items == null) return list;
                foreach (var x in items)
                {
                    string kind = Json.Str(x, "Kind"), path = PathUtil.Normalize(Json.Str(x, "Path"));
                    if (path == null || (kind != "Directory" && kind != "File")) continue;
                    list.Add(new SaveMapping { Path = path, IsFile = kind == "File" });
                }
            }
            return list;
        }

        /// <summary>Copia nel backup la copertina o l'icona dell'app (piccola), così resta riconoscibile anche dopo la disinstallazione.</summary>
        private static bool AddImage(ZipArchive zip, BackupMeta meta)
        {
            try
            {
                byte[] data = meta.ImagePng;
                if (data == null && !string.IsNullOrEmpty(meta.ImageFile) && File.Exists(meta.ImageFile) && new FileInfo(meta.ImageFile).Length < 3 * 1024 * 1024)
                    data = File.ReadAllBytes(meta.ImageFile);
                if (data == null || data.Length == 0) return false;
                var e = zip.CreateEntry("image", CompressionLevel.NoCompression);
                using (var s = e.Open()) s.Write(data, 0, data.Length);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Immagine non aggiunta al backup di {0}: {1}", meta.AppName, ex.Message);
                return false;
            }
        }

        private static bool StillExists(LeftoverItem i)
        {
            switch (i.Kind)
            {
                case ItemKind.Directory: case ItemKind.File: return PathUtil.Exists(i.Path);
                case ItemKind.RegistryKey: return RegUtil.KeyExists(i.Path);
                case ItemKind.RegistryValue: return RegUtil.ValueExists(i.Path, i.ValueName);
                case ItemKind.Service: return RegUtil.KeyExists(Traces.ServicesRoot + "\\" + i.Path);
                default: return true;
            }
        }

        /// <summary>Aggiunge un elemento al backup; false se non c'è niente da salvare (già sparito o non leggibile).</summary>
        private static bool AddToBackup(ZipArchive zip, LeftoverItem i, int n, Dictionary<string, object> entry)
        {
            if (i.Kind == ItemKind.Directory)
            {
                if (!Directory.Exists(PathUtil.Long(i.Path))) return false;
                string prefix = "files/" + n + "/";
                // i collegamenti (junction, link simbolici) non si seguono: puntano a dati che non fanno parte dell'elemento
                foreach (var f in PathUtil.FilesNoLinks(i.Path))
                    AddFile(zip, f, prefix + f.Substring(i.Path.Length + 1).Replace('\\', '/'));
                foreach (var d in PathUtil.DirsNoLinks(i.Path))
                    if (!Directory.EnumerateFileSystemEntries(PathUtil.Long(d)).Any())
                        zip.CreateEntry(prefix + d.Substring(i.Path.Length + 1).Replace('\\', '/') + "/");
                entry["Entry"] = prefix;
                return true;
            }
            if (i.Kind == ItemKind.File)
            {
                if (!File.Exists(PathUtil.Long(i.Path))) return false;
                string name = "files/" + n + "/" + Path.GetFileName(i.Path);
                AddFile(zip, i.Path, name);
                entry["Entry"] = name;
                return true;
            }
            if (i.Kind == ItemKind.RegistryKey || i.Kind == ItemKind.RegistryValue || i.Kind == ItemKind.Service)
            {
                string reg = i.Kind == ItemKind.RegistryKey ? RegExport.ExportKey(i.Path)
                    : i.Kind == ItemKind.Service ? RegExport.ExportKey(Traces.ServicesRoot + "\\" + i.Path)
                    : RegExport.ExportValue(i.Path, i.ValueName);
                if (reg == null) return false;
                string name = "registry/" + n + ".reg";
                WriteText(zip, name, reg, Encoding.Unicode);
                entry["Entry"] = name;
                return true;
            }
            if (i.Kind == ItemKind.ScheduledTask)
            {
                string xml = TaskXml(i.Path);
                if (xml == null) return false;
                string name = "tasks/" + n + ".xml";
                WriteText(zip, name, xml, Encoding.Unicode);
                entry["Entry"] = name;
                return true;
            }
            if (i.Kind == ItemKind.PathEntry)
            {
                entry["Entry"] = "path";   // basta ricordare la cartella tolta
                entry["Data"] = i.Data;
                return true;
            }
            return false;
        }

        private static void WriteText(ZipArchive zip, string name, string text, Encoding enc)
        {
            var ze = zip.CreateEntry(name);
            using (var s = ze.Open())
            {
                var bytes = enc.GetPreamble().Concat(enc.GetBytes(text)).ToArray();
                s.Write(bytes, 0, bytes.Length);
            }
        }

        private static void AddFile(ZipArchive zip, string file, string entryName)
        {
            var e = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            string lf = PathUtil.Long(file);
            // lo zip accetta solo date dal 1980 al 2107: un file con una data assurda non deve far fallire il backup
            DateTime written = File.GetLastWriteTime(lf);
            if (written.Year >= 1980 && written.Year <= 2107) e.LastWriteTime = written;
            using (var src = new FileStream(lf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = e.Open())
                src.CopyTo(dst);
        }

        // ------------------------------------------------------------------ Elenco e ripristino dei backup

        /// <summary>
        /// Backup nella cartella indicata (nella radice, come nelle versioni precedenti, e nelle sottocartelle delle applicazioni)
        /// e in quella usata da GameManager (versione 1), se esiste ancora.
        /// </summary>
        public static List<BackupInfo> ListBackups(string folder)
        {
            var list = new List<BackupInfo>();
            var files = new List<string>();
            foreach (var root in new[] { folder, PathUtil.Same(folder, AppPaths.LegacyBackupDir) ? null : AppPaths.LegacyBackupDir })
            {
                if (root == null) continue;
                files.AddRange(PathUtil.Files(root, "*.zip"));
                foreach (var sub in PathUtil.Dirs(root)) files.AddRange(PathUtil.Files(sub, "*.zip"));
            }
            foreach (var zipPath in files)
            {
                try
                {
                    using (var zip = ZipFile.OpenRead(zipPath))
                    {
                        var me = zip.GetEntry("manifest.json");
                        if (me == null) continue;
                        Dictionary<string, object> info;
                        using (var r = new StreamReader(me.Open(), Encoding.UTF8)) info = Json.Parse(r.ReadToEnd()) as Dictionary<string, object>;
                        string app = info == null ? null : Json.Str(info, "App");
                        if (app != "AppManager" && app != "GameManager") continue;
                        var items = (info["Items"] as object[]) ?? new object[0];
                        DateTime created;
                        if (!DateTime.TryParse(Json.Str(info, "Created"), null, System.Globalization.DateTimeStyles.RoundtripKind, out created))
                            created = File.GetCreationTime(zipPath);
                        var b = new BackupInfo
                        {
                            ZipPath = zipPath,
                            Title = Json.Str(info, "Title") ?? Path.GetFileNameWithoutExtension(zipPath),
                            Created = created,
                            Size = new FileInfo(zipPath).Length,
                            ItemCount = items.Length,
                            RegistryCount = items.Count(x => !new[] { "Directory", "File" }.Contains(Json.Str(x, "Kind") ?? "")),
                            AppName = Json.Str(info, "AppName"),
                            AppId = Json.Str(info, "AppId"),
                            Reason = Json.Str(info, "Reason"),
                            HasImage = Json.Str(info, "Image") != null && zip.GetEntry("image") != null,
                        };
                        AppKind k;
                        if (Enum.TryParse(Json.Str(info, "AppKind") ?? "", out k)) b.AppKind = k;
                        if (b.AppName == null) DescribeLegacy(b);
                        foreach (var x in items)
                        {
                            string p = Json.Str(x, "Path");
                            if (p == null) continue;
                            string v = Json.Str(x, "ValueName");
                            b.Paths.Add(string.IsNullOrEmpty(v) ? p : p + "  →  " + v);
                        }
                        list.Add(b);
                    }
                }
                catch (Exception ex) { Log.Error(L.F("Lettura backup {0}", zipPath), ex); }
            }
            return list.OrderByDescending(b => b.Created).ToList();
        }

        /// <summary>Backup delle versioni precedenti (senza AppName): l'applicazione si ricava dal titolo.</summary>
        private static void DescribeLegacy(BackupInfo b)
        {
            string t = b.Title ?? "";
            foreach (var p in new[] { L.T("Residui dei giochi"), L.T("Residui dei programmi") })
            {
                if (!t.StartsWith(p, StringComparison.OrdinalIgnoreCase)) continue;
                // i residui di più applicazioni finivano in un unico backup: restano insieme
                b.AppName = p;
                b.AppKind = p.EndsWith("giochi") ? AppKind.Game : AppKind.Program;
                b.Reason = "Residui";
                return;
            }
            b.AppName = t.Length > 0 ? t : Path.GetFileNameWithoutExtension(b.ZipPath);
            b.Reason = "Pulizia";
        }

        /// <summary>Immagine dell'applicazione salvata nel backup (null se non c'è).</summary>
        public static byte[] ReadImage(string zipPath)
        {
            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var e = zip.GetEntry("image");
                    if (e == null || e.Length > 8 * 1024 * 1024) return null;
                    using (var s = e.Open())
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch { return null; }
        }

        /// <summary>Sposta un backup nel Cestino (o lo elimina, se il Cestino lo rifiuta) e toglie la cartella dell'app rimasta vuota.</summary>
        public static void DeleteBackup(string zipPath, string folder)
        {
            if (zipPath == null || !zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !File.Exists(zipPath)) return;
            // solo file dentro le cartelle dei backup: il percorso arriva dall'elenco, ma meglio non fidarsi
            bool inside = PathUtil.IsUnder(zipPath, folder) || PathUtil.IsUnder(zipPath, AppPaths.LegacyBackupDir);
            if (!inside) throw new InvalidOperationException(L.T("Il file non è nella cartella dei backup."));
            if (SaveVault.UseRecycleBin)
            {
                try { Native.SendToRecycleBin(zipPath); }
                catch (Exception ex) { Log.Warn("Cestino non disponibile per {0}: {1}", zipPath, ex.Message); }
            }
            if (File.Exists(zipPath)) File.Delete(zipPath);
            Log.Info("Backup eliminato: {0}", zipPath);
            string dir = Path.GetDirectoryName(zipPath);
            try
            {
                if (!PathUtil.Same(dir, folder) && !PathUtil.Same(dir, AppPaths.LegacyBackupDir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch { }
        }

        /// <summary>Tiene solo gli ultimi <paramref name="keep"/> backup dell'applicazione (quelli più vecchi vanno nel Cestino).</summary>
        private static void PruneBackups(string folder, BackupMeta meta, int keep, string justCreated, string protect)
        {
            try
            {
                var same = ListBackups(folder)
                    .Where(b => string.Equals(b.AppName, meta.AppName, StringComparison.OrdinalIgnoreCase) && string.Equals(b.Reason, meta.Reason, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(b => b.Created).ToList();
                foreach (var old in same.Skip(keep))
                    if (!PathUtil.Same(old.ZipPath, justCreated) && (protect == null || !PathUtil.Same(old.ZipPath, protect))) DeleteBackup(old.ZipPath, folder);
            }
            catch (Exception ex) { Log.Error("Pulizia dei backup vecchi", ex); }
        }

        /// <summary>Ripristina un backup: file nelle posizioni originali, registro con "reg import".</summary>
        public static CleanResult Restore(string zipPath, bool overwrite, Action<string, double> progress)
        {
            var result = new CleanResult();
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                object[] items;
                try
                {
                    Dictionary<string, object> info;
                    using (var r = new StreamReader(zip.GetEntry("manifest.json").Open(), Encoding.UTF8)) info = (Dictionary<string, object>)Json.Parse(r.ReadToEnd());
                    items = (object[])info["Items"];
                }
                catch (Exception ex)
                {
                    Log.Error(L.F("Manifest del backup {0}", zipPath), ex);
                    result.Errors.Add(L.T("Il backup è danneggiato (elenco del contenuto illeggibile): niente è stato ripristinato."));
                    return result;
                }
                for (int n = 0; n < items.Length; n++)
                {
                    string kind = Json.Str(items[n], "Kind"), path = Json.Str(items[n], "Path"), entry = Json.Str(items[n], "Entry");
                    if (path == null || entry == null) continue;
                    if (progress != null) progress(L.F("Ripristino: {0}", path), (double)n / items.Length);
                    try
                    {
                        // si rimette solo ciò che AppManager avrebbe potuto togliere: un backup alterato non può scrivere altrove
                        if (!CanRestore(kind, path, Json.Str(items[n], "ValueName"), Json.Str(items[n], "Data")))
                        {
                            result.Errors.Add(L.F("Posizione protetta, non ripristinato: {0}", path));
                            continue;
                        }
                        if (kind == "Directory")
                        {
                            path = PathUtil.Normalize(path);
                            Directory.CreateDirectory(PathUtil.Long(path));
                            foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith(entry, StringComparison.Ordinal)))
                            {
                                string rel = e.FullName.Substring(entry.Length).Replace('/', '\\');
                                if (rel.Length == 0) continue;
                                string target = Path.GetFullPath(Path.Combine(path, rel));
                                if (!PathUtil.IsUnder(target, path)) continue; // protezione da percorsi malformati
                                if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(PathUtil.Long(target)); continue; }
                                Extract(e, target, overwrite, result);
                            }
                        }
                        else if (kind == "File")
                        {
                            var e = zip.GetEntry(entry);
                            if (e != null) Extract(e, path, overwrite, result);
                        }
                        else if (kind == "RegistryKey" || kind == "RegistryValue" || kind == "Service")
                        {
                            var e = zip.GetEntry(entry);
                            if (e == null) continue;
                            string text;
                            using (var r = new StreamReader(e.Open(), Encoding.Unicode, true)) text = r.ReadToEnd();
                            string scope = kind == "Service" ? Traces.ServicesRoot + "\\" + path : path;
                            if (!RegExport.IsConfinedTo(text, scope, kind != "RegistryValue"))
                                throw new InvalidOperationException(L.T("il file del registro nel backup riguarda anche altre chiavi, non lo importo"));
                            string err = RegExport.Import(text);
                            if (err != null) throw new InvalidOperationException(err);
                            result.Removed++;
                            if (kind == "Service") result.Notes.Add(L.F("Servizio «{0}» ripristinato: sarà attivo dopo il riavvio del PC.", path));
                        }
                        else if (kind == "ScheduledTask")
                        {
                            var e = zip.GetEntry(entry);
                            if (e == null) continue;
                            string tmp = Path.Combine(Path.GetTempPath(), "am_task_" + Guid.NewGuid().ToString("N") + ".xml");
                            try
                            {
                                e.ExtractToFile(tmp, true);
                                int code;
                                string output = RunTool("schtasks.exe", "/Create /TN \"" + path + "\" /XML \"" + tmp + "\" /F", 30000, out code);
                                if (code != 0) throw new InvalidOperationException(output.Trim());
                            }
                            finally { try { File.Delete(tmp); } catch { } }
                            result.Removed++;
                        }
                        else if (kind == "PathEntry")
                        {
                            AddPathEntry(path, Json.Str(items[n], "ValueName") ?? "Path", Json.Str(items[n], "Data"));
                            result.Removed++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Ripristino " + path, ex);
                        result.Errors.Add(Describe(ex) + ": " + path);
                    }
                }
            }
            Log.Info("Ripristino di {0}: {1} elementi, {2} errori", zipPath, result.Removed, result.Errors.Count);
            return result;
        }

        private static bool CanRestore(string kind, string path, string valueName, string data)
        {
            switch (kind)
            {
                case "Directory": case "File": return Safety.IsDeletablePath(path);
                case "RegistryKey": return Safety.IsDeletableRegistryKey(path);
                case "RegistryValue": return Safety.IsDeletableRegistryValue(path, valueName);
                case "Service": return Safety.IsDeletableService(path);
                case "ScheduledTask": return Safety.IsDeletableTask(path);
                case "PathEntry": return Safety.IsDeletablePathEntry(path, valueName ?? "Path", data);
                default: return false;
            }
        }

        private static void Extract(ZipArchiveEntry e, string target, bool overwrite, CleanResult result)
        {
            string lt = PathUtil.Long(target);
            if (File.Exists(lt) && !overwrite) return;
            Directory.CreateDirectory(PathUtil.Long(Path.GetDirectoryName(target)));
            if (File.Exists(lt)) File.SetAttributes(lt, FileAttributes.Normal); // un file di sola lettura non si sovrascriverebbe
            e.ExtractToFile(lt, true);
            result.Removed++;
            result.FreedBytes += e.Length;
        }

        /// <summary>Quanti file del backup esistono già nella posizione originale.</summary>
        public static int CountConflicts(string zipPath)
        {
            int count = 0;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var me = zip.GetEntry("manifest.json");
                if (me == null) return 0;
                Dictionary<string, object> info;
                using (var r = new StreamReader(me.Open(), Encoding.UTF8)) info = (Dictionary<string, object>)Json.Parse(r.ReadToEnd());
                foreach (var item in (object[])info["Items"])
                {
                    string kind = Json.Str(item, "Kind"), path = Json.Str(item, "Path"), entry = Json.Str(item, "Entry");
                    if (path == null) continue;
                    if (kind == "File" && File.Exists(PathUtil.Long(path))) count++;
                    if (kind == "Directory" && entry != null)
                        count += zip.Entries.Count(e => e.FullName.StartsWith(entry, StringComparison.Ordinal) && !e.FullName.EndsWith("/")
                            && File.Exists(PathUtil.Long(Path.Combine(path, e.FullName.Substring(entry.Length).Replace('/', '\\')))));
                }
            }
            return count;
        }
    }
}
