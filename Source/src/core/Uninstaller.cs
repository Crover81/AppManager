using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AppManager
{
    internal enum UninstallState { Running, WaitingUser, Completed, Failed }

    /// <summary>
    /// Avvia la disinstallazione nel modo giusto per ogni launcher o programma e ne segue l'avanzamento.
    /// Il chiamante interroga <see cref="Poll"/> periodicamente.
    /// </summary>
    internal sealed class UninstallSession : IDisposable
    {
        public AppEntry Entry { get; private set; }
        public UninstallState State { get; private set; }
        public string Status { get; private set; }
        public string Hint { get; private set; }
        /// <summary>Se la disinstallazione fallisce, si può eliminare direttamente la cartella (non per i pacchetti di Windows).</summary>
        public bool CanForce { get { return State == UninstallState.Failed && Entry.Launcher != LauncherKind.Store && Entry.Launcher != LauncherKind.Xbox; } }

        private Native.JobProcess job;
        private Process shellProcess;
        private Task ownTask;
        private DateTime started;
        private DateTime? processesEndedAt;

        private UninstallSession(AppEntry g)
        {
            Entry = g;
            State = UninstallState.Running;
            started = DateTime.Now;
        }

        public static UninstallSession Start(AppEntry g, CancellationToken ct)
        {
            var s = new UninstallSession(g);
            try { s.Launch(ct); }
            catch (Exception ex)
            {
                Log.Error(L.F("Avvio disinstallazione {0}", g.Name), ex);
                s.State = UninstallState.Failed;
                s.Status = L.F("Impossibile avviare la disinstallazione: {0}", ex.Message);
                s.Hint = s.CanForce
                    ? L.F("Puoi usare la rimozione forzata: {0} direttamente, insieme ai residui.", (g.IsGame ? L.T("il gioco verrà eliminato") : L.T("il programma verrà eliminato")))
                    : L.T("Prova da Impostazioni > App > App installate.");
            }
            return s;
        }

        private void Launch(CancellationToken ct)
        {
            var g = Entry;
            Log.Info("Disinstallazione di {0} ({1}, {2})", g.Name, g.Kind, g.Launcher);
            switch (g.Launcher)
            {
                case LauncherKind.Steam:
                    OpenUrl("steam://uninstall/" + g.SteamAppId);
                    Status = L.T("Conferma la disinstallazione nella finestra di Steam…");
                    Hint = L.T("Steam chiede sempre una conferma: la trovi nella sua finestra (a volte dietro questa).");
                    return;

                case LauncherKind.Epic:
                    Status = L.T("Elimino i file del gioco…");
                    Hint = L.T("Epic Games Launcher non offre un comando di disinstallazione: i file vengono rimossi direttamente e il gioco tolto dalla libreria del launcher.");
                    ownTask = Task.Run(() =>
                    {
                        if (Directory.Exists(g.InstallDir) && Safety.IsDeletablePath(g.InstallDir))
                            Cleaner.Delete(new LeftoverItem { Kind = ItemKind.Directory, Path = g.InstallDir }, false);
                        EpicInfo.Unregister(g);
                    }, ct);
                    return;

                case LauncherKind.Xbox:
                case LauncherKind.Store:
                    Status = L.T("Disinstallazione tramite Windows in corso…");
                    Hint = L.T("Il pacchetto viene rimosso come da Impostazioni > App.");
                    string filter = g.PackageFullName != null
                        ? "Get-AppxPackage | Where-Object { $_.PackageFullName -eq '" + g.PackageFullName.Replace("'", "''") + "' }"
                        : "Get-AppxPackage -Name '" + g.PackageName.Replace("'", "''") + "'";
                    job = Native.JobProcess.Start("powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + filter + " | Remove-AppxPackage\"", null);
                    return;
            }

            string cmd = g.UninstallString;
            if (string.IsNullOrWhiteSpace(cmd))
                throw new InvalidOperationException(L.F("questo {0} non ha un programma di disinstallazione", g.Noun));
            cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
            // i pacchetti MSI a volte registrano /I (installa/ripara) invece di /X (rimuovi)
            cmd = Regex.Replace(cmd, @"(msiexec(\.exe)?\s+)/I\s*(\{[0-9A-Fa-f\-]+\})", "$1/X$3", RegexOptions.IgnoreCase);
            cmd = QualifyCommand(cmd);
            string exe = PathUtil.ExeFromCommand(cmd);
            if (exe != null && Path.IsPathRooted(exe) && !File.Exists(exe))
                throw new FileNotFoundException(L.F("il programma di disinstallazione non esiste più ({0})", exe));
            Status = L.T("Segui le istruzioni del programma di disinstallazione…");
            Hint = L.T("Quando il programma di disinstallazione termina, si passa automaticamente alla ricerca dei residui.");
            string workDir = exe != null && Path.IsPathRooted(exe) ? Path.GetDirectoryName(exe) : null;
            try
            {
                job = Native.JobProcess.Start(cmd, workDir);
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode != 740) throw; // ERROR_ELEVATION_REQUIRED: serve l'UAC
                string args = exe != null && cmd.Length > exe.Length ? cmd.Substring(cmd.IndexOf(exe, StringComparison.OrdinalIgnoreCase) + exe.Length).TrimStart('"', ' ') : "";
                shellProcess = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas", WorkingDirectory = workDir ?? "" });
            }
        }

        /// <summary>
        /// Rende sicura la riga di comando prima di passarla a Windows: un percorso con spazi senza virgolette
        /// ("C:\Program Files\X\uninstall.exe /S") verrebbe provato prima come "C:\Program.exe", e un programma
        /// di sistema senza percorso ("MsiExec.exe /X...") verrebbe cercato prima nella cartella di AppManager.
        /// </summary>
        internal static string QualifyCommand(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd) || cmd.StartsWith("\"")) return cmd;
            string exe = PathUtil.ExeFromCommand(cmd);
            if (exe == null || !cmd.StartsWith(exe, StringComparison.OrdinalIgnoreCase)) return cmd;
            string rest = cmd.Substring(exe.Length);
            if (!Path.IsPathRooted(exe))
            {
                string name = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : exe + ".exe";
                string sys = Path.Combine(KnownDirs.System32, name);
                return name.IndexOfAny(new[] { '\\', '/' }) < 0 && File.Exists(sys) ? "\"" + sys + "\"" + rest : cmd;
            }
            return exe.IndexOf(' ') >= 0 ? "\"" + exe + "\"" + rest : cmd;
        }

        /// <summary>Apre un URL (steam://...) tramite Esplora risorse, così il launcher non parte come amministratore.</summary>
        public static void OpenUrl(string url)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + url + "\"") { UseShellExecute = false });
        }

        /// <summary>Controlla lo stato; va chiamato circa una volta al secondo.</summary>
        public UninstallState Poll()
        {
            if (State == UninstallState.Completed || State == UninstallState.Failed) return State;
            var g = Entry;

            if (ownTask != null)
            {
                if (!ownTask.IsCompleted) return State;
                if (ownTask.IsFaulted)
                {
                    var ex = ownTask.Exception.GetBaseException();
                    Log.Error("Disinstallazione " + g.Name, ex);
                    State = UninstallState.Failed;
                    Status = L.F("Errore durante l'eliminazione: {0}", ex.Message);
                    Hint = L.T("Chiudi il gioco e il launcher se sono aperti, poi riprova.");
                    return State;
                }
                return Complete();
            }

            if (g.Launcher == LauncherKind.Steam)
            {
                if (g.SteamManifestPath == null || !File.Exists(g.SteamManifestPath)) return Complete();
                if ((DateTime.Now - started).TotalSeconds > 60)
                {
                    State = UninstallState.WaitingUser;
                    Hint = L.F("Se non vedi la finestra di Steam, aprilo e disinstalla {0} dalla libreria: qui si proseguirà da solo.", (g.IsGame ? L.T("il gioco") : "l'applicazione"));
                }
                return State;
            }

            bool processesRunning = (job != null && job.ActiveProcesses > 0) || (shellProcess != null && !shellProcess.HasExited);
            bool gone = IsGone();
            if (!processesRunning)
            {
                if (!processesEndedAt.HasValue) processesEndedAt = DateTime.Now;
                if (gone) return Complete();
                if (g.Launcher == LauncherKind.Store || g.Launcher == LauncherKind.Xbox)
                {
                    // Remove-AppxPackage è terminato ma il pacchetto è ancora registrato: Windows ha rifiutato la rimozione
                    if ((DateTime.Now - processesEndedAt.Value).TotalSeconds < 5) return State;
                    State = UninstallState.Failed;
                    Status = L.F("Windows non ha rimosso {0}", (g.IsGame ? L.T("il gioco.") : "l'app."));
                    Hint = L.T("Alcune app di sistema non si possono disinstallare; la rimozione può anche essere bloccata da un criterio o da un aggiornamento. Prova da Impostazioni > App > App installate.");
                    Log.Info("Remove-AppxPackage terminato ma {0} è ancora installato", g.Name);
                    return State;
                }
                // alcuni launcher (es. Ubisoft) ricevono il comando e disinstallano per conto loro: si aspetta ancora un po'
                if ((DateTime.Now - processesEndedAt.Value).TotalSeconds > 4)
                {
                    State = UninstallState.WaitingUser;
                    Status = L.F("Il programma di disinstallazione è terminato, ma {0}", (g.IsGame ? L.T("il gioco risulta ancora presente.") : L.T("il programma risulta ancora presente.")));
                    Hint = L.T("Se hai annullato, premi Annulla. Se la disinstallazione è ancora in corso, attendi. Per cercare comunque i residui, premi Continua.");
                }
            }
            else if (gone && g.Launcher != LauncherKind.Xbox && g.Launcher != LauncherKind.Store)
            {
                // la voce è sparita ma il disinstallatore mostra ancora la schermata finale
                Status = L.T("Disinstallazione quasi completata: chiudi la finestra del programma di disinstallazione.");
            }
            return State;
        }

        private bool IsGone()
        {
            var g = Entry;
            if ((g.Launcher == LauncherKind.Xbox || g.Launcher == LauncherKind.Store) && !string.IsNullOrEmpty(g.PackageFullName))
            {
                // la registrazione per l'utente corrente è ciò che conta: i file possono restare se il pacchetto serve ad altri utenti
                return !RegUtil.KeyExists(@"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\" + g.PackageFullName);
            }
            if (g.Launcher == LauncherKind.Xbox)
                return !File.Exists(Path.Combine(g.InstallDir ?? "", "Content", "appxmanifest.xml"));
            if (g.Launcher == LauncherKind.Store)
                return !File.Exists(Path.Combine(g.InstallDir ?? "", "AppxManifest.xml"));
            bool keyGone = g.UninstallKey == null || !RegUtil.KeyExists(g.UninstallKey);
            bool exeGone = g.MainExe == null || !File.Exists(g.MainExe);
            return keyGone && (exeGone || !g.Dirs.Any(Directory.Exists));
        }

        private UninstallState Complete()
        {
            State = UninstallState.Completed;
            Status = L.T("Disinstallazione completata.");
            Hint = null;
            Log.Info("Disinstallazione completata: {0}", Entry.Name);
            return State;
        }

        /// <summary>L'utente dichiara di voler proseguire comunque con la ricerca dei residui.</summary>
        public void ForceContinue()
        {
            Log.Info("Disinstallazione di {0}: l'utente prosegue manualmente", Entry.Name);
            State = UninstallState.Completed;
        }

        public void Dispose()
        {
            if (job != null) { job.Dispose(); job = null; }
            if (shellProcess != null) { shellProcess.Dispose(); shellProcess = null; }
        }

        /// <summary>Processi in esecuzione dalle cartelle indicate (il gioco o programma è aperto?).</summary>
        public static string[] RunningFrom(AppEntry e)
        {
            var dirs = e.Dirs.ToList();
            if (dirs.Count == 0 && e.MainExe == null) return new string[0];
            return Process.GetProcesses().Select(p =>
            {
                try { return Native.GetProcessPath(p.Id); }
                catch { return null; }
                finally { p.Dispose(); }
            }).Where(path => path != null && (dirs.Any(d => PathUtil.IsUnder(path, d)) || (e.IsSingleFile && PathUtil.Same(path, e.MainExe))))
              .Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
