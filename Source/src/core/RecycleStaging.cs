using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace AppManager
{
    /// <summary>
    /// Cestino organizzato per applicazione. Invece di mandare nel Cestino ogni file per conto suo, i file vengono raccolti in
    /// una cartella «AppManager - nome dell'applicazione» nella radice della loro unità, dentro la quale ognuno mantiene il suo
    /// percorso completo (C:\Program Files\Gioco\gioco.exe → «AppManager - Gioco\C\Program Files\Gioco\gioco.exe»); le voci di
    /// sistema tolte (registro, servizi, attività, PATH, firewall) finiscono come file in «File registro». Alla fine ogni
    /// cartella va nel Cestino come un unico elemento: per recuperare qualcosa si ripristina la cartella e si ha tutto sotto mano.
    /// Una cartella per unità: il Cestino è per unità e lo spostamento sulla stessa unità è istantaneo (anche per decine di GB).
    /// </summary>
    internal sealed class RecycleStaging
    {
        public const string Prefix = "AppManager - ";
        public static string RegistryFolder { get { return L.T("File registro"); } }

        /// <summary>Solo per le prove automatiche: false = le cartelle restano dove sono invece di andare nel Cestino.</summary>
        internal static bool SendToBin = true;

        /// <summary>Solo per le prove automatiche: cartella (sulla stessa unità) in cui creare le cartelle di raccolta invece della radice.</summary>
#pragma warning disable 0649 // impostata solo dalle prove (tools\RobustTests.cs)
        internal static string RootOverride;
#pragma warning restore 0649

        // (applicazione, unità) → cartella di raccolta
        private readonly Dictionary<string, string> folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // quanti elementi sono stati davvero raccolti in ogni cartella (le cartelle solo "di impalcatura" non vanno nel Cestino)
        private readonly Dictionary<string, int> collected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private void Count(string folder)
        {
            int n;
            collected.TryGetValue(folder, out n);
            collected[folder] = n + 1;
        }

        /// <summary>True se la cartella è una cartella di raccolta di AppManager (nella radice di un'unità).</summary>
        public static bool IsStagingFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string parent = Path.GetDirectoryName(path.TrimEnd('\\'));
            return parent != null && parent.Length <= 3 && (Path.GetFileName(path.TrimEnd('\\')) ?? "").StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Si può raccogliere solo su un'unità fissa del PC (le chiavette e le unità di rete non hanno il Cestino).</summary>
        public static bool Supports(string path)
        {
            try
            {
                string root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root) || root.Length != 3 || root[1] != ':') return false;
                return new DriveInfo(root).DriveType == DriveType.Fixed;
            }
            catch { return false; }
        }

        /// <summary>Nome dell'applicazione come compare nel Cestino.</summary>
        public static string FolderName(string appName)
        {
            string name = PathUtil.SafeFileName(string.IsNullOrWhiteSpace(appName) ? L.T("Pulizia") : appName).Trim().TrimEnd('.');
            if (name.Length > 60) name = name.Substring(0, 60).Trim();
            if (name.Length == 0) name = "Pulizia";
            return Prefix + name;
        }

        private string FolderFor(string appName, string root)
        {
            string key = (appName ?? "") + "|" + root;
            string dir;
            if (folders.TryGetValue(key, out dir)) return dir;
            string baseName = FolderName(appName);
            string parent = RootOverride != null && string.Equals(Path.GetPathRoot(RootOverride), root, StringComparison.OrdinalIgnoreCase) ? RootOverride : root;
            dir = Unique(parent, baseName);
            try { Directory.CreateDirectory(dir); }
            catch (UnauthorizedAccessException)
            {
                // radice dell'unità di sistema chiusa ai non amministratori: si usa la cartella dell'utente, sulla stessa unità
                string profile = KnownDirs.UserProfile;
                if (RootOverride != null || !string.Equals(Path.GetPathRoot(profile), root, StringComparison.OrdinalIgnoreCase)) throw;
                dir = Unique(profile, baseName);
                Directory.CreateDirectory(dir);
            }
            usedNames.Add(dir);
            folders[key] = dir;
            return dir;
        }

        /// <summary>Nome libero: una cartella con lo stesso nome (es. ripristinata dal Cestino) non va mai mescolata con questa.</summary>
        private string Unique(string parent, string baseName)
        {
            string dir = Path.Combine(parent, baseName);
            for (int k = 2; Directory.Exists(dir) || File.Exists(dir) || usedNames.Contains(dir); k++) dir = Path.Combine(parent, baseName + " (" + k + ")");
            return dir;
        }

        /// <summary>Dove finisce l'elemento nella cartella di raccolta: la lettera dell'unità e poi il percorso completo.</summary>
        private static string MirrorPath(string folder, string path)
        {
            string root = Path.GetPathRoot(path);
            return Path.Combine(folder, root.Substring(0, 1).ToUpperInvariant(), path.Substring(root.Length));
        }

        /// <summary>Sposta un file o una cartella nella cartella di raccolta dell'applicazione. Restituisce la nuova posizione.</summary>
        public string Move(LeftoverItem i, string appName)
        {
            string root = Path.GetPathRoot(i.Path);
            string folder = FolderFor(appName, root);
            string target = MirrorPath(folder, i.Path);
            string lt = PathUtil.Long(target), ls = PathUtil.Long(i.Path);
            Directory.CreateDirectory(PathUtil.Long(Path.GetDirectoryName(target)));
            if (i.Kind == ItemKind.Directory)
            {
                if (Directory.Exists(lt)) throw new IOException(L.F("Esiste già nella cartella del Cestino: {0}", target));
                Directory.Move(ls, lt);
            }
            else
            {
                if (File.Exists(lt)) throw new IOException(L.F("Esiste già nella cartella del Cestino: {0}", target));
                File.SetAttributes(ls, File.GetAttributes(ls) & ~FileAttributes.ReadOnly);
                File.Move(ls, lt);
            }
            Count(folder);
            return target;
        }

        /// <summary>
        /// Scrive in «File registro» una copia della voce di sistema che sta per essere tolta: .reg per registro e servizi
        /// (si rimettono con un doppio clic), .xml per le attività (Utilità di pianificazione › Importa attività), .txt per PATH e firewall.
        /// Restituisce false se la voce non c'è più (niente da copiare); solleva un'eccezione se la copia non riesce.
        /// </summary>
        public bool SaveSystemCopy(LeftoverItem i, string appName, Func<string, string> taskXml)
        {
            string content, file;
            Encoding enc = Encoding.Unicode;
            switch (i.Kind)
            {
                case ItemKind.RegistryKey:
                    content = RegExport.ExportKey(i.Path);
                    file = L.F("Registro - {0}.reg", ShortKey(i.Path));
                    break;
                case ItemKind.RegistryValue:
                    content = RegExport.ExportValue(i.Path, i.ValueName);
                    file = L.F("Registro - {0} - {1}.reg", ShortKey(i.Path), (string.IsNullOrEmpty(i.ValueName) ? "(predefinito)" : i.ValueName));
                    break;
                case ItemKind.Service:
                    content = RegExport.ExportKey(Traces.ServicesRoot + "\\" + i.Path);
                    file = L.F("Servizio - {0}.reg", i.Path);
                    break;
                case ItemKind.ScheduledTask:
                    content = taskXml(i.Path);
                    file = L.F("Attività pianificata - {0}.xml", i.Path.Trim('\\').Replace('\\', ' '));
                    break;
                case ItemKind.PathEntry:
                    content = L.F("Cartella tolta dalla variabile d'ambiente PATH\r\n\r\nVariabile: {0} ({1})\r\nChiave: {2}\r\nCartella: {3}\r\n\r\nPer rimetterla: Impostazioni di sistema › Variabili d'ambiente, aggiungi la cartella alla variabile indicata.\r\n",
                        i.ValueName ?? "Path", i.Path.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) ? L.T("utente") : L.T("sistema"), i.Path, i.Data);
                    file = "PATH - " + Path.GetFileName((i.Data ?? "cartella").TrimEnd('\\')) + ".txt";
                    enc = new UTF8Encoding(true);
                    break;
                case ItemKind.FirewallRule:
                    content = FirewallRulesText(i.Path);
                    file = "Firewall - " + Path.GetFileName(i.Path) + ".txt";
                    enc = new UTF8Encoding(true);
                    break;
                default:
                    return false;
            }
            if (content == null) return false;
            string folder = FolderFor(appName, SystemRoot);
            string dir = Path.Combine(folder, RegistryFolder);
            Directory.CreateDirectory(dir);
            string name = PathUtil.SafeFileName(file);
            if (name.Length > 150) name = name.Substring(0, 146).Trim() + Path.GetExtension(file);
            string target = Path.Combine(dir, name);
            for (int k = 2; File.Exists(target); k++)
                target = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + " (" + k + ")" + Path.GetExtension(name));
            File.WriteAllText(target, content, enc);
            Count(folder);
            return true;
        }

        private static string SystemRoot
        {
            get
            {
                string sys = Environment.GetEnvironmentVariable("SystemDrive");
                return string.IsNullOrEmpty(sys) ? "C:\\" : sys.TrimEnd('\\') + "\\";
            }
        }

        /// <summary>Le ultime parti del percorso di una chiave (quelle che la riconoscono), con la radice abbreviata.</summary>
        private static string ShortKey(string keyPath)
        {
            var parts = keyPath.Split('\\');
            var tail = parts.Length <= 3 ? parts : new[] { parts[0] }.Concat(parts.Skip(parts.Length - 2)).ToArray();
            return string.Join(" ", tail);
        }

        /// <summary>Descrizione delle regole di Windows Firewall del programma (per ricrearle a mano, se servisse).</summary>
        private static string FirewallRulesText(string program)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh.exe", "advfirewall firewall show rule name=all program=\"" + program + "\" verbose")
                {
                    CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(30000);
                    return L.F("Regole di Windows Firewall tolte per {0}\r\n\r\n{1}\r\n", program, output.Trim());
                }
            }
            catch (Exception ex)
            {
                return L.F("Regole di Windows Firewall tolte per {0}\r\n(dettagli non disponibili: {1})\r\n", program, ex.Message);
            }
        }

        /// <summary>
        /// Manda nel Cestino ogni cartella di raccolta (una per applicazione e per unità). Se il Cestino ne rifiuta una
        /// (troppo grande, conferma negata...) la cartella resta dove è, con tutto dentro: niente viene perso e lo si segnala.
        /// </summary>
        public void SendAll(CleanResult result)
        {
            foreach (var dir in folders.Values.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    int n;
                    if (!collected.TryGetValue(dir, out n) || n == 0)
                    {
                        // niente è stato raccolto (per esempio tutti i file erano in uso): ci sono solo le cartelle create qui, vuote
                        Directory.Delete(dir, true);
                        continue;
                    }
                    if (!SendToBin)
                    {
                        result.RecycleFolders.Add(dir);
                        continue;
                    }
                    Native.SendToRecycleBin(dir);
                    result.RecycleFolders.Add(dir);
                    Log.Info("Nel Cestino: {0}", dir);
                }
                catch (Exception ex)
                {
                    Log.Error(L.F("Cestino per {0}", dir), ex);
                    result.KeptFolders.Add(dir);
                    result.Notes.Add(L.F("Il Cestino ha rifiutato la cartella «{0}» ({1}). I file sono in {2}: recuperali da lì o elimina la cartella.", Path.GetFileName(dir), ex.Message, dir));
                }
            }
        }
    }
}
