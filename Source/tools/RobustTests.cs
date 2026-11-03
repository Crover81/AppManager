using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace AppManager
{
    /// <summary>
    /// Prove sulle "situazioni disperate". Si lavora solo su dati finti creati nella cartella di prova
    /// (e su chiavi HKCU\Software\AppManagerRobust*), che alla fine vengono rimossi. Le verifiche sulle
    /// cartelle e chiavi di sistema sono solo in lettura: si controlla la risposta dei controlli di sicurezza,
    /// senza mai tentare di eliminare qualcosa di vero.
    /// </summary>
    internal static class RobustTests
    {
        private static int pass, fail;
        private static string root;

        private static void Check(string what, bool ok, string detail = null)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine("{0} {1}{2}", ok ? "  ok  " : "  FAIL", what, detail == null ? "" : "  [" + detail + "]");
        }

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("== " + title);
        }

        public static int Run(string sandbox, List<AppEntry> games, List<AppEntry> programs, string only)
        {
            root = Path.Combine(Path.GetFullPath(sandbox), "robust");
            HarnessDelete(root);
            Directory.CreateDirectory(root);
            // le cartelle «AppManager - …» per il Cestino restano nella cartella di prova: né la radice del disco né il Cestino vero
            RecycleStaging.SendToBin = false;
            RecycleStaging.RootOverride = Path.Combine(root, "cestino");
            Directory.CreateDirectory(RecycleStaging.RootOverride);
            var rootBefore = StagingAtDriveRoot();
            try
            {
                var tests = new List<KeyValuePair<string, Action>>
                {
                    new KeyValuePair<string, Action>("paths", () => PathMatrix(games, programs)),
                    new KeyValuePair<string, Action>("registry", RegistryMatrix),
                    new KeyValuePair<string, Action>("system", SystemMatrix),
                    new KeyValuePair<string, Action>("links", Links),
                    new KeyValuePair<string, Action>("long", LongPaths),
                    new KeyValuePair<string, Action>("names", OddNames),
                    new KeyValuePair<string, Action>("locked", LockedAndReadOnly),
                    new KeyValuePair<string, Action>("backup", BackupEdgeCases),
                    new KeyValuePair<string, Action>("restore", TamperedBackups),
                    new KeyValuePair<string, Action>("regvalues", RegistryValueRoundTrip),
                    new KeyValuePair<string, Action>("cancel", Cancellation),
                    new KeyValuePair<string, Action>("userfolders", UserFolders),
                };
                tests.Add(new KeyValuePair<string, Action>("stagingroot", () =>
                {
                    Section("Cartelle per il Cestino");
                    var extra = StagingAtDriveRoot().Except(rootBefore).ToList();
                    Check("nessuna cartella «AppManager - …» lasciata nella radice del disco dalle prove", extra.Count == 0, string.Join(" | ", extra));
                }));
                foreach (var t in tests)
                {
                    if (only != null && t.Key != only && t.Key != "stagingroot") continue;
                    try { t.Value(); }
                    catch (Exception ex) { Check("eccezione non prevista nel test " + t.Key, false, ex.GetType().Name + ": " + ex.Message); }
                }
            }
            finally
            {
                HarnessDelete(root);
                int purged = PurgeOwnRecycleBinItems(root);
                DeleteTestKeys();
                Console.WriteLine();
                Console.WriteLine("Pulizia della prova: cartella rimasta={0}, elementi di prova tolti dal Cestino={1}", Directory.Exists(root), purged);
            }
            Console.WriteLine("Risultato: {0} ok, {1} falliti", pass, fail);
            return fail;
        }

        // ================================================================== Matrice di sicurezza: percorsi

        private static void PathMatrix(List<AppEntry> games, List<AppEntry> programs)
        {
            Section("Percorsi che non si devono MAI poter eliminare");
            string user = KnownDirs.UserProfile;
            var denied = new List<string>
            {
                @"C:\", "C:", @"C:\Windows", @"C:\Windows\System32", @"C:\WINDOWS\system32\drivers", @"C:\PROGRA~1", @"C:\PROGRA~2",
                KnownDirs.ProgramFiles, KnownDirs.ProgramFilesX86, Path.Combine(KnownDirs.ProgramFiles, "Common Files"),
                Path.Combine(KnownDirs.ProgramFiles, @"Common Files\Qualcosa"), Path.Combine(KnownDirs.ProgramFiles, @"WindowsApps\Pacchetto_1.0_x64__abc"),
                Path.Combine(KnownDirs.ProgramFiles, "Windows Defender"), Path.Combine(KnownDirs.ProgramFiles, @"Windows Defender\MsMpEng.exe"),
                KnownDirs.ProgramData, Path.Combine(KnownDirs.ProgramData, "Microsoft"), Path.Combine(KnownDirs.ProgramData, @"Microsoft\Windows\Start Menu\Programs\Startup"),
                user, KnownDirs.Documents, KnownDirs.Desktop, KnownDirs.Downloads, KnownDirs.Pictures, KnownDirs.Videos, KnownDirs.SavedGames, KnownDirs.MyGames,
                Path.Combine(user, "AppData"), KnownDirs.RoamingAppData, KnownDirs.LocalAppData, KnownDirs.LocalLow, KnownDirs.Temp,
                Path.GetDirectoryName(user), Path.Combine(Path.GetDirectoryName(user), "Public"), Path.Combine(KnownDirs.LocalAppData, "Packages"),
                Path.Combine(KnownDirs.LocalAppData, @"Packages\Pacchetto_abc\LocalState"), Path.Combine(KnownDirs.LocalAppData, @"Microsoft\Edge"),
                Path.Combine(KnownDirs.RoamingAppData, @"Microsoft\Windows\Start Menu"), Path.Combine(user, "OneDrive"),
                Path.Combine(user, "Dropbox"), Path.Combine(user, "Nextcloud"), Path.Combine(user, "source"), Path.Combine(user, "NTUSER.DAT"),
                Path.Combine(user, "Zotero"), Path.Combine(user, "OneDrive - Azienda"),
                Path.Combine(KnownDirs.StartMenu, "Startup"), Path.Combine(KnownDirs.StartMenu, "Accessories"), Path.Combine(KnownDirs.CommonStartMenu, "Windows PowerShell"),
                Path.Combine(KnownDirs.CommonStartMenu, "Administrative Tools"), KnownDirs.StartMenu, KnownDirs.CommonStartMenu,
                @"C:\$Recycle.Bin", @"C:\$Recycle.Bin\S-1-5-21-1", @"C:\System Volume Information", @"C:\Recovery", @"C:\Config.Msi", @"C:\Documents and Settings",
                @"C:\PerfLogs", @"C:\$WinREAgent", @"C:\Windows.old", @"C:\Boot",
                @"\\server\condivisione\cartella", @"\\?\C:\Temp\x", @"\\.\C:\x", @"\\?\GLOBALROOT\Device\HarddiskVolume3\x", @"cartella\relativa", @"C:relativo",
                @"C:\Windows\..\Windows", Path.Combine(KnownDirs.LocalAppData, @"..\..\..\..\Windows"), @"%SystemRoot%\Temp", @"%WINDIR%",
                @"C:\Windows.", @"C:\Windows ", @"C:/Windows/System32", @"C:\Windows\\System32", @"  C:\Windows  ", "\"C:\\Windows\"",
                @"C:\Windows\System32\config\SAM:stream", "", null, @"C:\Users\..", @"C:\Program Files\.",
            };
            // cartelle madri di giochi e programmi installati
            foreach (var e in games.Concat(programs).Where(x => x.InstallDir != null).Take(40))
            {
                string parent = Path.GetDirectoryName(e.InstallDir);
                if (parent != null && parent.Length > 3) denied.Add(parent);
            }
            if (SteamInfo.SteamPath != null)
            {
                denied.Add(SteamInfo.SteamPath);
                foreach (var sa in SteamInfo.SteamAppsDirs) { denied.Add(sa); denied.Add(Path.Combine(sa, "common")); }
            }
            int bad = 0;
            foreach (var p in denied.Distinct())
            {
                bool ok;
                try { ok = !Safety.IsDeletablePath(p); }
                catch (Exception ex) { ok = false; Console.WriteLine("      eccezione per {0}: {1}", p, ex.Message); }
                if (!ok) { bad++; Check("protetto: " + (p ?? "(null)"), false); }
            }
            Check(denied.Distinct().Count() + " percorsi protetti respinti", bad == 0, bad + " accettati per errore");

            // file e cartelle come oggetti: anche il tipo "File" deve rispettare le stesse regole
            Check("file di sistema come elemento File", !Safety.IsDeletable(new LeftoverItem { Kind = ItemKind.File, Path = Path.Combine(KnownDirs.System32, "kernel32.dll") }));
            Check("NTUSER.DAT come elemento File", !Safety.IsDeletable(new LeftoverItem { Kind = ItemKind.File, Path = Path.Combine(user, "NTUSER.DAT") }));

            Section("Percorsi normali che invece si possono eliminare");
            var allowed = new[]
            {
                Path.Combine(root, "qualcosa"), Path.Combine(KnownDirs.LocalAppData, "FornitoreInesistenteXYZ"), Path.Combine(KnownDirs.RoamingAppData, @"FornitoreXYZ\AppXYZ"),
                Path.Combine(KnownDirs.MyGames, "GiocoInesistenteXYZ"), Path.Combine(KnownDirs.ProgramFiles, "FornitoreInesistenteXYZ"),
                Path.Combine(KnownDirs.ProgramData, "FornitoreInesistenteXYZ"), Path.Combine(user, ".programmaxyz"), Path.Combine(KnownDirs.StartMenu, "ProgrammaXYZ"),
                Path.Combine(KnownDirs.StartMenu, @"Accessories\ProgrammaXYZ.lnk"), Path.Combine(KnownDirs.LocalAppData, @"Packages\Pacchetto.Inesistente_abc"),
                Path.Combine(KnownDirs.Desktop, "ProgrammaXYZ.lnk"), Path.Combine(KnownDirs.Documents, "ProgrammaXYZ"),
            };
            foreach (var p in allowed) Check("consentito: " + p, Safety.IsDeletablePath(p));
        }

        // ================================================================== Matrice di sicurezza: registro

        private static void RegistryMatrix()
        {
            Section("Chiavi e valori del registro che non si devono MAI poter eliminare");
            var denied = new[]
            {
                "HKCU", @"HKCU\", @"HKCU\Software", @"HKLM\SOFTWARE", @"HKEY_CURRENT_USER\Software", @"HKCU\Software\Microsoft", @"HKCU\Software\Microsoft\Windows",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", @"HKLM\SYSTEM\CurrentControlSet\Services\WinDefend", @"HKLM\SYSTEM",
                @"HKCU\Software\\Microsoft", @"HKCU\Software\Microsoft\\", @"HKCU\\Software\Microsoft", @"HKCU\Software\Classes", @"HKCU\Software\Classes\CLSID",
                @"HKCU\Software\Classes\.txt", @"HKCU\Software\Classes\txtfile", @"HKCU\Software\Classes\*", @"HKCU\Software\Classes\Directory\shell",
                @"HKCU\Software\Classes\Directory", @"HKCU\Software\Classes\exefile", @"HKCU\Software\Classes\http", @"HKLM\SOFTWARE\Classes\CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
                @"HKU\.DEFAULT\Software\Qualcosa", @"HKCR\Qualcosa", @"HKEY_CLASSES_ROOT\CLSID", @"HKCU\Software\Policies\Qualcosa", @"HKCU\Environment",
                @"HKCU\Control Panel\Desktop", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Qualcosa", @"HKCU\Software\Valve", @"HKCU\Software\Valve\Steam",
                @"HKLM\SAM", @"HKLM\SECURITY", @"HKLM\HARDWARE", @"HKLM\BCD00000000", @"HKCU\System\CurrentControlSet", @"HKCU\Software\Classes\Local Settings",
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"HKCU\Software\Wow6432Node", "", null, @"Software\Qualcosa", @"HKXX\Software\Qualcosa",
            };
            int bad = 0;
            foreach (var k in denied)
            {
                bool ok;
                try { ok = !Safety.IsDeletableRegistryKey(k); }
                catch (Exception ex) { ok = false; Console.WriteLine("      eccezione per {0}: {1}", k, ex.Message); }
                if (!ok) { bad++; Check("protetta: " + (k ?? "(null)"), false); }
            }
            Check(denied.Length + " chiavi protette respinte", bad == 0, bad + " accettate per errore");

            // valori: voci di avvio di Windows e la variabile PATH non si toccano come valori generici
            Check("valore Path in HKCU\\Environment", !Safety.IsDeletableRegistryValue(@"HKCU\Environment", "Path"));
            Check("valore con percorso doppio \\\\", !Safety.IsDeletableRegistryValue(@"HKCU\Software\\Microsoft\Windows\CurrentVersion\Run", "Qualcosa"));
            Check("valore predefinito di una chiave qualsiasi", !Safety.IsDeletableRegistryValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer", ""));
            foreach (var runKey in Traces.RunKeys)
                using (var k = RegUtil.Open(runKey))
                    foreach (var v in RegUtil.ValueNames(k))
                    {
                        string target = Traces.TargetOfCommand(RegUtil.GetString(k, v));
                        if (target != null && Safety.IsSystemPath(target))
                            Check("avvio automatico di Windows «" + v + "» (" + runKey.Substring(0, 4) + ")", !Safety.IsDeletableRegistryValue(runKey, v));
                    }

            Section("Chiavi normali che invece si possono eliminare");
            foreach (var k in new[] { @"HKCU\Software\FornitoreInesistenteXYZ", @"HKLM\SOFTWARE\FornitoreInesistenteXYZ\App", @"HKEY_CURRENT_USER\Software\FornitoreXYZ" })
                Check("consentita: " + k, Safety.IsDeletableRegistryKey(k));
        }

        // ================================================================== Servizi, attività, PATH

        private static void SystemMatrix()
        {
            Section("Servizi, attività pianificate e PATH di Windows");
            foreach (var s in new[] { "WinDefend", "wuauserv", "Dhcp", "Dnscache", "EventLog", "RpcSs", "Tcpip", "disk", "ACPI", "Schedule", "TrustedInstaller", "", "..\\x", @"a\b" })
                Check("servizio protetto: " + s, !Safety.IsDeletableService(s));
            foreach (var t in new[] { @"\Microsoft\Windows\Defrag\ScheduledDefrag", @"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan", "\\", "", "Senza barra", @"\Microsoft\" })
                Check("attività protetta: " + (t.Length == 0 ? "(vuota)" : t), !Safety.IsDeletableTask(t));
            // attività di Windows nella cartella principale (es. CreateExplorerShellUnelevatedTask): avviano programmi di Windows
            var rootTasks = Traces.ScheduledTasks().Where(t => t.Path.LastIndexOf('\\') == 0 && t.Exes.Count > 0 && t.Exes.All(Safety.IsSystemPath)).ToList();
            foreach (var t in rootTasks) Check("attività di Windows fuori da \\Microsoft: " + t.Path, !Safety.IsDeletableTask(t.Path));
            if (rootTasks.Count == 0) Console.WriteLine("      (nessuna attività di Windows nella cartella principale su questo PC)");
            string env = Traces.UserEnvironment;
            foreach (var p in new[] { @"%SystemRoot%\system32", @"C:\Windows", @"C:\Windows\System32\Wbem", @"%USERPROFILE%\AppData\Local\Microsoft\WindowsApps",
                                       Path.Combine(KnownDirs.ProgramFiles, @"Common Files\Oracle\Java\javapath"), "", @"relativo\bin" })
                Check("PATH protetta: " + (p.Length == 0 ? "(vuota)" : p), !Safety.IsDeletablePathEntry(env, "Path", p));
            Check("PATH di un programma consentita", Safety.IsDeletablePathEntry(env, "Path", @"C:\ProgrammaXYZ\bin"));
            Check("valore diverso da Path respinto", !Safety.IsDeletablePathEntry(env, "TEMP", @"C:\ProgrammaXYZ\bin"));

            Section("Righe di comando dei programmi di disinstallazione");
            string msi = "\"" + Path.Combine(KnownDirs.System32, "MsiExec.exe") + "\"";
            var commands = new[]
            {
                Tuple.Create(@"C:\Program Files\Foo Bar\uninstall.exe /S", "\"C:\\Program Files\\Foo Bar\\uninstall.exe\" /S"),
                Tuple.Create(@"C:\Program Files (x86)\Foo\unins000.exe", "\"C:\\Program Files (x86)\\Foo\\unins000.exe\""),
                Tuple.Create("MsiExec.exe /X{12345678-1234-1234-1234-123456789012}", msi + " /X{12345678-1234-1234-1234-123456789012}"),
                Tuple.Create("MsiExec /X{12345678-1234-1234-1234-123456789012}", msi + " /X{12345678-1234-1234-1234-123456789012}"),
                Tuple.Create("\"C:\\x y\\a.exe\" /q", "\"C:\\x y\\a.exe\" /q"),
                Tuple.Create(@"C:\Tools\a.exe --uninstall", @"C:\Tools\a.exe --uninstall"),
                Tuple.Create("programma_inesistente.exe /x", "programma_inesistente.exe /x"),
            };
            foreach (var c in commands)
            {
                string got = UninstallSession.QualifyCommand(c.Item1);
                Check("riga di comando: " + c.Item1, got == c.Item2, got);
            }
        }

        // ================================================================== Collegamenti (junction)

        private static void Links()
        {
            Section("Collegamenti (junction) verso cartelle preziose");
            string precious = Path.Combine(root, "preziosa");
            Directory.CreateDirectory(Path.Combine(precious, "sotto"));
            File.WriteAllText(Path.Combine(precious, "tieni.txt"), "dati da non perdere");
            File.WriteAllText(Path.Combine(precious, "sotto", "anche.txt"), "anche questi");
            Func<bool> preciousIntact = () => File.Exists(Path.Combine(precious, "tieni.txt")) && File.Exists(Path.Combine(precious, "sotto", "anche.txt"));

            Check("rilevamento di una junction come collegamento", MakeJunction(Path.Combine(root, "j0"), precious) && PathUtil.IsReparsePoint(Path.Combine(root, "j0")));
            Check("una cartella normale non è un collegamento", !PathUtil.IsReparsePoint(precious));
            Check("i file visti senza seguire i collegamenti", !PathUtil.FilesNoLinks(root).Any(f => f.StartsWith(Path.Combine(root, "j0"), StringComparison.OrdinalIgnoreCase)));
            Check("dimensione senza seguire i collegamenti", PathUtil.Size(Path.Combine(root, "j0")) >= 0);

            foreach (var recycle in new[] { false, true })
            {
                foreach (var backup in new[] { false, true })
                {
                    string label = (recycle ? "Cestino" : "definitiva") + (backup ? " + backup" : "");
                    // 1) cartella da eliminare che contiene una junction verso la cartella preziosa (anche annidata)
                    string victim = Path.Combine(root, "vittima_" + (recycle ? "c" : "d") + (backup ? "b" : ""));
                    Directory.CreateDirectory(Path.Combine(victim, "livello1"));
                    File.WriteAllText(Path.Combine(victim, "proprio.txt"), "suo");
                    bool made = MakeJunction(Path.Combine(victim, "collegamento"), precious) && MakeJunction(Path.Combine(victim, "livello1", "annidato"), precious);
                    var res = Clean(new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Settings, Path = victim }, recycle, backup);
                    Check("cartella con junction interne, eliminazione " + label, made && !Directory.Exists(victim) && preciousIntact(), Summary(res));
                    if (backup && res.BackupPath != null)
                    {
                        using (var zip = ZipFile.OpenRead(res.BackupPath))
                            Check("il backup non contiene i file raggiunti tramite la junction (" + label + ")",
                                !zip.Entries.Any(e => e.FullName.EndsWith("tieni.txt") || e.FullName.EndsWith("anche.txt")) && zip.Entries.Any(e => e.FullName.EndsWith("proprio.txt")));
                    }

                    // 2) l'elemento stesso è una junction: si toglie solo il collegamento
                    string link = Path.Combine(root, "link_" + (recycle ? "c" : "d") + (backup ? "b" : ""));
                    made = MakeJunction(link, precious);
                    res = Clean(new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Settings, Path = link }, recycle, backup);
                    Check("elemento che è esso stesso una junction, " + label, made && !Directory.Exists(link) && preciousIntact() && res.Notes.Any(n => n.Contains("collegamento")), Summary(res));
                }
            }
            // 3) cache con junction interna (eliminazione file per file)
            string cache = Path.Combine(root, "cache_con_link");
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, "a.bin"), "x");
            bool m2 = MakeJunction(Path.Combine(cache, "dentro"), precious);
            var rc = Clean(new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Cache, Path = cache }, true, false);
            Check("cache con junction interna", m2 && !Directory.Exists(cache) && preciousIntact(), Summary(rc));

            Section("Junction in una cartella superiore verso aree protette (solo controlli, nessuna eliminazione)");
            string toWindows = Path.Combine(root, "verso_windows");
            string toLocal = Path.Combine(root, "verso_local");
            if (MakeJunction(toWindows, KnownDirs.Windows) && MakeJunction(toLocal, KnownDirs.LocalAppData))
            {
                foreach (var p in new[] { Path.Combine(toWindows, "System32"), Path.Combine(toWindows, "Temp"), Path.Combine(toWindows, @"System32\drivers"),
                                          Path.Combine(toLocal, "Microsoft"), Path.Combine(toLocal, "Packages"), Path.Combine(toLocal, "Temp") })
                {
                    Check("respinto (posizione reale protetta): " + p.Substring(root.Length + 1), !Safety.IsDeletablePath(p));
                    Check("respinto anche come elemento da pulire: " + p.Substring(root.Length + 1),
                        !Safety.IsDeletable(new LeftoverItem { Kind = ItemKind.Directory, Path = p }));
                }
                Check("il collegamento stesso si può togliere (non tocca la destinazione)", Safety.IsDeletablePath(toWindows));
                RemoveLink(toWindows);
                RemoveLink(toLocal);
                Check("collegamenti di prova verso Windows e AppData rimossi", !Directory.Exists(toWindows) && !Directory.Exists(toLocal) && Directory.Exists(Path.Combine(KnownDirs.Windows, "System32")));
            }
            else Check("creazione delle junction di prova", false);

            // link simbolico a una cartella (serve la Modalità sviluppatore o l'amministratore)
            string sym = Path.Combine(root, "symlink");
            if (CreateSymbolicLink(sym, precious, 1 | 2))
            {
                string container = Path.Combine(root, "symcontainer");
                Directory.CreateDirectory(container);
                bool inner = CreateSymbolicLink(Path.Combine(container, "dentro"), precious, 1 | 2);
                var rs = Clean(new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Settings, Path = container }, false, false);
                Check("cartella con link simbolico interno", inner && !Directory.Exists(container) && preciousIntact(), Summary(rs));
                rs = Clean(new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Settings, Path = sym }, true, false);
                Check("elemento che è un link simbolico", !Directory.Exists(sym) && preciousIntact(), Summary(rs));
            }
            else Console.WriteLine("      (link simbolici non creabili senza Modalità sviluppatore o amministratore: prova saltata)");
            Check("cartella preziosa ancora intatta alla fine", preciousIntact());
        }

        // ================================================================== Percorsi lunghi

        private static void LongPaths()
        {
            Section("Percorsi oltre i 260 caratteri");
            foreach (var recycle in new[] { false, true })
            {
                string top = Path.Combine(root, recycle ? "lungo_c" : "lungo_d");
                string deep = top;
                while (deep.Length < 330) deep = Path.Combine(deep, "cartella_con_un_nome_molto_lungo_0123456789");
                bool created;
                try
                {
                    Directory.CreateDirectory(PathUtil.Long(deep));
                    File.WriteAllText(PathUtil.Long(Path.Combine(deep, "profondo.txt")), "fondo");
                    File.WriteAllText(Path.Combine(top, "cima.txt"), "cima");
                    created = File.Exists(PathUtil.Long(Path.Combine(deep, "profondo.txt")));
                }
                catch (Exception ex) { created = false; Console.WriteLine("      creazione: " + ex.Message); }
                Check("creazione di un file a " + (deep.Length + 13) + " caratteri", created);
                if (!created) continue;
                int files;
                long size = PathUtil.Size(top, out files);
                Check("dimensione e numero di file contano anche il file profondo", files == 2 && size == 9, files + " file, " + size + " byte");
                Check("il file profondo compare nell'elenco senza collegamenti", PathUtil.FilesNoLinks(top).Any(f => f.EndsWith("profondo.txt")));
                var res = Clean(new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Settings, Path = top }, recycle, true);
                Check("eliminazione " + (recycle ? "con Cestino" : "definitiva") + " di una cartella con percorsi lunghi", !Directory.Exists(top), Summary(res));
                if (res.BackupPath != null)
                    using (var zip = ZipFile.OpenRead(res.BackupPath))
                        Check("il backup contiene il file profondo", zip.Entries.Any(e => e.FullName.EndsWith("profondo.txt")));
                if (res.BackupPath != null && !recycle)
                {
                    var rr = Cleaner.Restore(res.BackupPath, false, null);
                    string back = PathUtil.Long(Path.Combine(deep, "profondo.txt"));
                    Check("ripristino del file profondo", File.Exists(back) && File.ReadAllText(back) == "fondo", string.Join(" | ", rr.Errors));
                    Check("conflitti contati anche per i percorsi lunghi", Cleaner.CountConflicts(res.BackupPath) == 2);
                    HarnessDelete(top);
                }
            }
            // elementi il cui percorso stesso supera i 260 caratteri
            string longItem = root;
            while (longItem.Length < 300) longItem = Path.Combine(longItem, "annidata_per_superare_il_limite_dei_260");
            try
            {
                foreach (var cat in new[] { ItemCategory.Cache, ItemCategory.Settings, ItemCategory.AppFolder })
                {
                    foreach (var recycle in new[] { true, false })
                    {
                        Directory.CreateDirectory(PathUtil.Long(Path.Combine(longItem, "sotto")));
                        File.WriteAllText(PathUtil.Long(Path.Combine(longItem, "sotto", "x.txt")), "x");
                        var r2 = Clean(new LeftoverItem { Kind = ItemKind.Directory, Category = cat, Path = longItem }, recycle, true);
                        bool gone = !Directory.Exists(PathUtil.Long(longItem));
                        Check("elemento a " + longItem.Length + " caratteri (" + cat + ", " + (recycle ? "Cestino" : "definitiva") + ")", gone, Summary(r2));
                        // col Cestino (le cache si eliminano sempre direttamente) nulla sparisce senza possibilità di recupero:
                        // l'elemento è nella cartella «AppManager - …», con il suo percorso lungo
                        if (recycle && cat != ItemCategory.Cache)
                        {
                            string rel = longItem.Substring(0, 1) + longItem.Substring(2);
                            bool staged = r2.RecycleFolders.Any(f => File.Exists(PathUtil.Long(Path.Combine(f, rel, "sotto", "x.txt"))));
                            Check("  … ed è recuperabile nella cartella per il Cestino, con il percorso completo", staged, string.Join(" | ", r2.RecycleFolders));
                        }
                        HarnessDelete(longItem);
                    }
                }
                var r3 = Clean(new LeftoverItem { Kind = ItemKind.File, Category = ItemCategory.Settings, Path = Path.Combine(longItem, "y.txt") }, true, true);
                Check("file già sparito: nessun errore", r3.Errors.Count == 0, Summary(r3));
                Directory.CreateDirectory(PathUtil.Long(longItem));
                string lf = Path.Combine(longItem, "file_singolo_lungo.cfg");
                File.WriteAllText(PathUtil.Long(lf), "cfg");
                var r4 = Clean(new LeftoverItem { Kind = ItemKind.File, Category = ItemCategory.Settings, Path = lf }, true, true);
                Check("file singolo a " + lf.Length + " caratteri, con backup", !File.Exists(PathUtil.Long(lf)) && r4.BackupPath != null, Summary(r4));
                var rr4 = Cleaner.Restore(r4.BackupPath, false, null);
                Check("ripristino del file singolo lungo", File.Exists(PathUtil.Long(lf)), string.Join(" | ", rr4.Errors));
            }
            catch (Exception ex) { Check("elemento con percorso lungo", false, ex.Message); }
        }

        // ================================================================== Nomi strani

        private static void OddNames()
        {
            Section("Nomi Unicode, spazi, punti, emoji");
            string dir = Path.Combine(root, "Ñandú ü 日本語 🎮 (copia) [1]");
            var files = new Dictionary<string, string>
            {
                { "salvataggio émoji 🎮.sav", "contenuto 1" }, { "日本語ファイル.txt", "contenuto 2" }, { "nome con  due spazi.cfg", "3" },
                { "punti..multipli...ini", "4" }, { "senza_estensione", "5" }, { ".nascosto", "6" }, { "ÄÖÜß€.dat", "7" },
            };
            Directory.CreateDirectory(Path.Combine(dir, "sotto ✓"));
            foreach (var f in files) File.WriteAllText(Path.Combine(dir, f.Key), f.Value, Encoding.UTF8);
            File.WriteAllText(Path.Combine(dir, "sotto ✓", "ok.txt"), "ok");
            // date fuori dall'intervallo dello zip (prima del 1980, dopo il 2107): capitano davvero nei file di alcuni giochi
            File.SetLastWriteTime(Path.Combine(dir, "senza_estensione"), new DateTime(1975, 1, 1));
            File.SetLastWriteTime(Path.Combine(dir, "punti..multipli...ini"), new DateTime(1601, 1, 2));
            File.SetLastWriteTime(Path.Combine(dir, "ÄÖÜß€.dat"), new DateTime(2150, 6, 1));
            var res = Clean(new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Saves, Path = dir }, false, true);
            Check("pulizia di una cartella con nomi Unicode", !Directory.Exists(dir) && res.BackupPath != null, Summary(res));
            if (res.BackupPath == null) return;
            var rr = Cleaner.Restore(res.BackupPath, false, null);
            bool same = files.All(f => File.Exists(Path.Combine(dir, f.Key)) && File.ReadAllText(Path.Combine(dir, f.Key), Encoding.UTF8) == f.Value)
                        && File.Exists(Path.Combine(dir, "sotto ✓", "ok.txt"));
            Check("ripristino identico di tutti i file con nomi Unicode", same, string.Join(" | ", rr.Errors));
            var list = Cleaner.ListBackups(Path.GetDirectoryName(res.BackupPath));
            Check("il backup compare nell'elenco", list.Any(b => PathUtil.Same(b.ZipPath, res.BackupPath)));
            HarnessDelete(dir);

            // titolo del backup con caratteri non validi e lunghissimo
            string d2 = Path.Combine(root, "titolo_strano");
            Directory.CreateDirectory(d2);
            File.WriteAllText(Path.Combine(d2, "a.txt"), "a");
            var opt = new CleanOptions { UseRecycleBin = false, Backup = true, BackupFolder = Path.Combine(root, "backup"), Title = "Gioco: \"Edizione\" <Speciale> | ? * / \\ " + new string('x', 300) };
            var r2 = Cleaner.Clean(new List<LeftoverItem> { new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Saves, Path = d2 } }, opt, null, CancellationToken.None);
            Check("backup con un titolo pieno di caratteri non validi e lunghissimo", r2.BackupPath != null && File.Exists(r2.BackupPath) && !Directory.Exists(d2), Summary(r2));
        }

        // ================================================================== File bloccati e di sola lettura

        private static void LockedAndReadOnly()
        {
            Section("Sola lettura, file nascosti e di sistema, file in uso");
            foreach (var recycle in new[] { false, true })
            {
                string dir = Path.Combine(root, "sola_lettura_" + (recycle ? "c" : "d"));
                Directory.CreateDirectory(Path.Combine(dir, "sotto"));
                string f1 = Path.Combine(dir, "ro.txt"), f2 = Path.Combine(dir, "sotto", "hs.txt");
                File.WriteAllText(f1, "1");
                File.WriteAllText(f2, "2");
                File.SetAttributes(f1, FileAttributes.ReadOnly);
                File.SetAttributes(f2, FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly);
                new DirectoryInfo(Path.Combine(dir, "sotto")).Attributes |= FileAttributes.ReadOnly;
                new DirectoryInfo(dir).Attributes |= FileAttributes.ReadOnly;
                var res = Clean(new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Settings, Path = dir }, recycle, false);
                Check("cartella e file di sola lettura/nascosti/di sistema (" + (recycle ? "Cestino" : "definitiva") + ")", !Directory.Exists(dir), Summary(res));
            }
            string single = Path.Combine(root, "singolo_ro.cfg");
            File.WriteAllText(single, "x");
            File.SetAttributes(single, FileAttributes.ReadOnly | FileAttributes.Hidden);
            var rs = Clean(new LeftoverItem { Kind = ItemKind.File, Category = ItemCategory.Settings, Path = single }, false, true);
            Check("file singolo di sola lettura e nascosto", !File.Exists(single), Summary(rs));

            // file in uso (aperto da un programma che ne consente la lettura): niente crash, errore chiaro, tutto nel backup
            string busy = Path.Combine(root, "in_uso");
            Directory.CreateDirectory(busy);
            File.WriteAllText(Path.Combine(busy, "libero.txt"), "libero");
            string lockedFile = Path.Combine(busy, "bloccato.txt");
            File.WriteAllText(lockedFile, "bloccato");
            CleanResult rl;
            using (new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                rl = Clean(new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Settings, Path = busy }, true, true);
            Check("cartella con un file in uso: errore segnalato, nessuna eccezione", rl.Errors.Count > 0 && rl.Failed.Count == 1, Summary(rl));
            Check("il file in uso è ancora al suo posto", File.Exists(lockedFile));
            if (rl.BackupPath != null)
                using (var zip = ZipFile.OpenRead(rl.BackupPath))
                    Check("il backup contiene tutti i file (anche quello in uso, letto in condivisione)", zip.Entries.Count(e => e.FullName.EndsWith(".txt")) == 2);
            // file aperto in scrittura esclusiva: non leggibile per il backup → non si elimina
            string excl = Path.Combine(root, "esclusivo");
            Directory.CreateDirectory(excl);
            string ex1 = Path.Combine(excl, "db.sqlite");
            File.WriteAllText(ex1, "dati");
            File.WriteAllText(Path.Combine(excl, "altro.txt"), "altro");
            using (var fs = new FileStream(ex1, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var rx = Clean(new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Saves, Path = excl }, false, true);
                Check("salvataggi non leggibili per il backup: non vengono eliminati", Directory.Exists(excl) && File.Exists(Path.Combine(excl, "altro.txt")), Summary(rx));
            }
            HarnessDelete(busy);
            HarnessDelete(excl);
        }

        // ================================================================== Casi limite del backup

        private static void BackupEdgeCases()
        {
            Section("Backup: cartella dentro l'elemento, cartella non valida, disco non scrivibile");
            string victim = Path.Combine(root, "contiene_backup");
            Directory.CreateDirectory(victim);
            File.WriteAllText(Path.Combine(victim, "s.sav"), "s");
            var opt = new CleanOptions { UseRecycleBin = false, Backup = true, BackupFolder = Path.Combine(victim, "backup"), Title = "t" };
            var res = Cleaner.Clean(new List<LeftoverItem> { new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Saves, Path = victim } }, opt, null, CancellationToken.None);
            Check("cartella dei backup dentro l'elemento: niente eliminato", Directory.Exists(victim) && File.Exists(Path.Combine(victim, "s.sav")) && res.Removed == 0, Summary(res));

            string blocker = Path.Combine(root, "sono_un_file");
            File.WriteAllText(blocker, "x");
            opt = new CleanOptions { UseRecycleBin = false, Backup = true, BackupFolder = Path.Combine(blocker, "backup"), Title = "t" };
            res = Cleaner.Clean(new List<LeftoverItem> { new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Saves, Path = victim } }, opt, null, CancellationToken.None);
            Check("cartella dei backup impossibile da creare: niente eliminato", Directory.Exists(victim) && res.Removed == 0, Summary(res));

            foreach (var bad in new[] { KnownDirs.Windows, Path.Combine(KnownDirs.System32, "AppManagerBackup"), @"\\server\x", "relativa" })
            {
                opt = new CleanOptions { UseRecycleBin = false, Backup = true, BackupFolder = bad, Title = "t" };
                res = Cleaner.Clean(new List<LeftoverItem> { new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Saves, Path = victim } }, opt, null, CancellationToken.None);
                Check("cartella dei backup non valida (" + bad + "): niente eliminato", Directory.Exists(victim) && res.Removed == 0 && !Directory.Exists(Path.Combine(KnownDirs.System32, "AppManagerBackup")), Summary(res));
            }

            // cartella dei backup di sola lettura (ACL: scrittura negata) → nessun file eliminato, nessuno zip a metà
            string roBackup = Path.Combine(root, "backup_negato");
            Directory.CreateDirectory(roBackup);
            int code;
            Run("icacls.exe", "\"" + roBackup + "\" /deny *S-1-1-0:(OI)(CI)(W,D,DC)", out code);
            opt = new CleanOptions { UseRecycleBin = false, Backup = true, BackupFolder = roBackup, Title = "t" };
            res = Cleaner.Clean(new List<LeftoverItem> { new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Saves, Path = victim } }, opt, null, CancellationToken.None);
            Run("icacls.exe", "\"" + roBackup + "\" /remove:d *S-1-1-0", out code);
            Check("cartella dei backup senza permesso di scrittura: niente eliminato", code == 0 && Directory.Exists(victim) && res.Removed == 0, Summary(res));
            Check("nessuno zip a metà lasciato nella cartella", !Directory.GetFiles(roBackup).Any());

            // elemento sparito tra l'analisi e la pulizia
            var gone = new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Saves, Path = Path.Combine(root, "gia_sparito") };
            var goneKey = new LeftoverItem { Kind = ItemKind.RegistryKey, Category = ItemCategory.Registry, Path = @"HKCU\Software\AppManagerRobustSparita" };
            res = Clean(gone, true, true, goneKey);
            Check("elementi già spariti: nessun errore", res.Errors.Count == 0, Summary(res));
        }

        // ================================================================== Backup alterati

        private static void TamperedBackups()
        {
            Section("Ripristino di backup alterati o danneggiati");
            // chiave "vittima" che un .reg alterato proverebbe a eliminare
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\AppManagerRobustVittima")) k.SetValue("x", "resta");
            string zipDir = Path.Combine(root, "backup_alterati");
            Directory.CreateDirectory(zipDir);

            string z1 = MakeBackup(zipDir, "reg_altre_chiavi", new[]
            {
                Item("RegistryKey", @"HKCU\Software\AppManagerRobustLecita", "registry/0.reg"),
            }, new Dictionary<string, string>
            {
                { "registry/0.reg", RegExport.Header + "\r\n[HKEY_CURRENT_USER\\Software\\AppManagerRobustLecita]\r\n\"a\"=\"1\"\r\n\r\n[HKEY_CURRENT_USER\\Software\\AppManagerRobustAltra]\r\n\"b\"=\"2\"\r\n" },
            });
            var r1 = Cleaner.Restore(z1, true, null);
            Check(".reg che scrive anche in altre chiavi: rifiutato", !RegUtil.KeyExists(@"HKCU\Software\AppManagerRobustAltra") && !RegUtil.KeyExists(@"HKCU\Software\AppManagerRobustLecita"), string.Join(" | ", r1.Errors));

            string z2 = MakeBackup(zipDir, "reg_elimina", new[]
            {
                Item("RegistryKey", @"HKCU\Software\AppManagerRobustLecita", "registry/0.reg"),
            }, new Dictionary<string, string>
            {
                { "registry/0.reg", RegExport.Header + "\r\n[-HKEY_CURRENT_USER\\Software\\AppManagerRobustVittima]\r\n" },
            });
            Cleaner.Restore(z2, true, null);
            Check(".reg che elimina chiavi: rifiutato", RegUtil.KeyExists(@"HKCU\Software\AppManagerRobustVittima"));

            string z2b = MakeBackup(zipDir, "reg_elimina_valore", new[]
            {
                Item("RegistryKey", @"HKCU\Software\AppManagerRobustVittima", "registry/0.reg"),
            }, new Dictionary<string, string>
            {
                { "registry/0.reg", RegExport.Header + "\r\n[HKEY_CURRENT_USER\\Software\\AppManagerRobustVittima]\r\n\"x\"=-\r\n" },
            });
            Cleaner.Restore(z2b, true, null);
            Check(".reg che elimina valori: rifiutato", RegUtil.ValueExists(@"HKCU\Software\AppManagerRobustVittima", "x"));

            string z3 = MakeBackup(zipDir, "posizione_di_sistema", new[]
            {
                Item("File", Path.Combine(KnownDirs.System32, "amrobust_test.dll"), "files/0/amrobust_test.dll"),
                Item("Directory", Path.Combine(KnownDirs.Windows, "AMRobustTest"), "files/1/"),
                Item("RegistryKey", @"HKCU\Software\Microsoft\AMRobustTest", "registry/2.reg"),
                Item("ScheduledTask", @"\Microsoft\Windows\AMRobustTest", "tasks/3.xml"),
                Item("Service", "WinDefend", "registry/4.reg"),
            }, new Dictionary<string, string>
            {
                { "files/0/amrobust_test.dll", "MZ" }, { "files/1/x.txt", "x" },
                { "registry/2.reg", RegExport.Header + "\r\n[HKEY_CURRENT_USER\\Software\\Microsoft\\AMRobustTest]\r\n\"a\"=\"1\"\r\n" },
                { "tasks/3.xml", "<Task/>" }, { "registry/4.reg", RegExport.Header + "\r\n[HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\WinDefend]\r\n\"Start\"=dword:00000004\r\n" },
            });
            var r3 = Cleaner.Restore(z3, true, null);
            Check("backup che punta a posizioni di sistema: nulla ripristinato", r3.Removed == 0 && r3.Errors.Count == 5
                && !File.Exists(Path.Combine(KnownDirs.System32, "amrobust_test.dll")) && !RegUtil.KeyExists(@"HKCU\Software\Microsoft\AMRobustTest"), r3.Errors.Count + " rifiuti");

            string target = Path.Combine(root, "ripristino_traversal");
            string z4 = MakeBackup(zipDir, "traversal", new[] { Item("Directory", target, "files/0/") }, new Dictionary<string, string>
            {
                { "files/0/ok.txt", "ok" }, { "files/0/../../fuori.txt", "no" }, { "files/0/..\\..\\fuori2.txt", "no" },
            });
            Cleaner.Restore(z4, true, null);
            Check("nomi con ../ nel backup: niente scritto fuori dalla cartella", File.Exists(Path.Combine(target, "ok.txt"))
                && !File.Exists(Path.Combine(root, "fuori.txt")) && !File.Exists(Path.Combine(root, "fuori2.txt")) && !File.Exists(Path.Combine(zipDir, "fuori.txt")));

            string z5 = Path.Combine(zipDir, "manifest_rotto.zip");
            using (var zip = ZipFile.Open(z5, ZipArchiveMode.Create))
            using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open())) w.Write("{ \"App\": \"AppManager\", \"Items\": [ { \"Kind\": ");
            Check("manifest illeggibile: nessuna eccezione, errore chiaro", NoThrow(() => { var r = Cleaner.Restore(z5, false, null); return r.Errors.Count == 1; }));
            string z6 = Path.Combine(zipDir, "non_zip.zip");
            File.WriteAllText(z6, "questo non è uno zip");
            Check("file .zip non valido nell'elenco dei backup: ignorato", NoThrow(() => !Cleaner.ListBackups(zipDir).Any(b => b.ZipPath == z6)));
            string z7 = Path.Combine(zipDir, "items_non_lista.zip");
            using (var zip = ZipFile.Open(z7, ZipArchiveMode.Create))
            using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open())) w.Write("{ \"App\": \"AppManager\", \"Items\": \"stringa\" }");
            Check("manifest con Items non valido: nessuna eccezione", NoThrow(() => { Cleaner.ListBackups(zipDir); return Cleaner.Restore(z7, false, null).Removed == 0; }));
        }

        private static Dictionary<string, object> Item(string kind, string path, string entry)
        {
            return new Dictionary<string, object> { { "Kind", kind }, { "Path", path }, { "Entry", entry }, { "Category", "Settings" } };
        }

        private static string MakeBackup(string dir, string name, Dictionary<string, object>[] items, Dictionary<string, string> entries)
        {
            string path = Path.Combine(dir, name + ".zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var e in entries)
                {
                    var ze = zip.CreateEntry(e.Key);
                    var enc = e.Key.EndsWith(".reg") || e.Key.EndsWith(".xml") ? Encoding.Unicode : Encoding.UTF8;
                    using (var s = ze.Open())
                    {
                        var b = enc.GetPreamble().Concat(enc.GetBytes(e.Value)).ToArray();
                        s.Write(b, 0, b.Length);
                    }
                }
                var info = new Dictionary<string, object> { { "App", "AppManager" }, { "Version", 2 }, { "Title", name }, { "Created", DateTime.Now.ToString("o") }, { "Items", items } };
                using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open())) w.Write(Json.Serialize(info));
            }
            return path;
        }

        // ================================================================== Valori del registro particolari

        private static void RegistryValueRoundTrip()
        {
            Section("Backup e ripristino di valori del registro particolari");
            const string key = @"Software\AppManagerRobustValori";
            using (var k = Registry.CurrentUser.CreateSubKey(key))
            {
                k.SetValue("aCapo", "riga 1\r\nriga 2\ttab", RegistryValueKind.String);
                k.SetValue("virgolette", "C:\\x \"y\" \\\\z", RegistryValueKind.String);
                k.SetValue("vuoto", "", RegistryValueKind.String);
                k.SetValue("nessuno", new byte[] { 1, 2, 3 }, RegistryValueKind.None);
                k.SetValue("binVuoto", new byte[0], RegistryValueKind.Binary);
                k.SetValue("multiVuoto", new string[0], RegistryValueKind.MultiString);
                k.SetValue("unicode", "日本語 🎮 àèì", RegistryValueKind.String);
                k.SetValue("[parentesi]", "[HKEY_LOCAL_MACHINE\\SOFTWARE\\Finta]", RegistryValueKind.String);
                using (var s = k.CreateSubKey("sotto chiave con spazi")) s.SetValue("", "predefinito");
            }
            var before = Snapshot(@"HKCU\" + key);
            var res = Clean(new LeftoverItem { Kind = ItemKind.RegistryKey, Category = ItemCategory.Registry, Path = @"HKCU\" + key }, false, true);
            Check("chiave con valori particolari eliminata con backup", !RegUtil.KeyExists(@"HKCU\" + key) && res.BackupPath != null, Summary(res));
            if (res.BackupPath == null) return;
            var rr = Cleaner.Restore(res.BackupPath, false, null);
            var after = Snapshot(@"HKCU\" + key);
            Check("ripristino identico (a capo, REG_NONE, vuoti, Unicode, parentesi quadre)", before == after, rr.Errors.Count > 0 ? string.Join(" | ", rr.Errors) : Diff(before, after));
            Registry.CurrentUser.DeleteSubKeyTree(key, false);
        }

        private static string Snapshot(string path)
        {
            var sb = new StringBuilder();
            using (var k = RegUtil.Open(path)) Dump(k, "", sb);
            return sb.ToString();
        }

        private static void Dump(RegistryKey k, string prefix, StringBuilder sb)
        {
            if (k == null) { sb.Append("(assente)"); return; }
            foreach (var v in k.GetValueNames().OrderBy(x => x))
            {
                object o = k.GetValue(v, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                string data = o is byte[] ? BitConverter.ToString((byte[])o) : o is string[] ? string.Join("|", (string[])o) : Convert.ToString(o);
                sb.Append(prefix).Append(v).Append('=').Append(k.GetValueKind(v)).Append(':').Append(data).Append('\n');
            }
            foreach (var s in k.GetSubKeyNames().OrderBy(x => x))
                using (var sk = k.OpenSubKey(s)) Dump(sk, prefix + s + "\\", sb);
        }

        private static string Diff(string a, string b)
        {
            var la = a.Split('\n');
            var lb = b.Split('\n');
            return "prima: " + string.Join(" ; ", la.Except(lb)) + " / dopo: " + string.Join(" ; ", lb.Except(la));
        }

        // ================================================================== Annullamento

        private static void Cancellation()
        {
            Section("Annullamento");
            string d = Path.Combine(root, "annullata");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "a.txt"), "a");
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var items = new List<LeftoverItem> { new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Saves, Path = d } };
            CleanResult r = null;
            Check("pulizia annullata prima di iniziare: nessuna eccezione", NoThrow(() =>
            {
                r = Cleaner.Clean(items, new CleanOptions { UseRecycleBin = false, Backup = true, BackupFolder = Path.Combine(root, "backup"), Title = "annullata" }, null, cts.Token);
                return true;
            }));
            Check("pulizia annullata: niente eliminato, risultato segnato come interrotto", r != null && r.Cancelled && Directory.Exists(d) && r.Removed == 0, r == null ? null : Summary(r));
            r = null;
            NoThrow(() => { r = Cleaner.Clean(items, new CleanOptions { UseRecycleBin = false, Backup = false, Title = "annullata" }, null, cts.Token); return true; });
            Check("annullata senza backup: niente eliminato", r != null && r.Cancelled && Directory.Exists(d), r == null ? null : Summary(r));
        }

        // ================================================================== Cartelle dell'utente scambiate per cartelle di programmi

        private static void UserFolders()
        {
            Section("Cartelle dell'utente che contengono un eseguibile");
            string docs = Path.Combine(root, "Lavoro");
            Directory.CreateDirectory(docs);
            foreach (var n in new[] { "relazione.docx", "bilancio.xlsx", "foto.cr2", "contratto.pdf", "preventivo.pdf" }) File.WriteAllText(Path.Combine(docs, n), "x");
            Check("cartella con documenti riconosciuta come dell'utente", InstalledApps.LooksLikeUserFolder(docs));
            string proj = Path.Combine(root, "MioProgetto");
            Directory.CreateDirectory(Path.Combine(proj, ".git"));
            Check("progetto con repository git riconosciuto come dell'utente", InstalledApps.LooksLikeUserFolder(proj));
            string proj2 = Path.Combine(root, "MioProgetto2");
            Directory.CreateDirectory(proj2);
            File.WriteAllText(Path.Combine(proj2, "MioProgetto2.sln"), "");
            Check("progetto con soluzione .sln riconosciuto come dell'utente", InstalledApps.LooksLikeUserFolder(proj2));
            string app = Path.Combine(root, "ProgrammaPortable");
            Directory.CreateDirectory(Path.Combine(app, "docs"));
            File.WriteAllText(Path.Combine(app, "programma.exe"), "MZ");
            foreach (var n in new[] { "a.pdf", "b.pdf", "c.pdf", "d.pdf", "e.pdf", "f.pdf" }) File.WriteAllText(Path.Combine(app, "docs", n), "x");
            File.WriteAllText(Path.Combine(app, "manuale.pdf"), "x");
            Check("programma portable con i suoi manuali in docs\\ resta un programma", !InstalledApps.LooksLikeUserFolder(app));
        }

        // ================================================================== Invarianti sui dati veri (solo lettura)

        /// <summary>
        /// Tutto ciò che le analisi propongono sul PC vero deve rispettare le regole: passa i controlli di sicurezza,
        /// non sta dentro la cartella di un altro gioco o programma installato e non la contiene. Non elimina nulla.
        /// </summary>
        public static int Invariants(List<AppEntry> games, List<AppEntry> programs, int maxApps)
        {
            var all = games.Concat(programs).ToList();
            // i launcher (Steam, Epic...) contengono per definizione i dati dei loro giochi: esclusi dal controllo "dentro un altro"
            var owners = all.Where(e => !e.ContainsOthers).SelectMany(e => e.Dirs.Select(d => Tuple.Create(e, d))).ToList();
            var sw = Stopwatch.StartNew();

            Section("Residui orfani (analisi completa del PC)");
            var orphans = OrphanScanner.Scan(games, programs, null, CancellationToken.None);
            Console.WriteLine("      {0} elementi in {1} ms", orphans.Count, sw.ElapsedMilliseconds);
            CheckItems("residui orfani", orphans, owners, null);

            Section("Analisi di ogni gioco e programma (prima della disinstallazione)");
            var profiles = all.Select(ProfileBuilder.Get).ToList();
            int scanned = 0, total = 0;
            sw.Restart();
            foreach (var e in all.Take(maxApps))
            {
                var p = ProfileBuilder.Get(e);
                var others = profiles.Where(x => x.Entry.Id != e.Id).ToList();
                List<LeftoverItem> items;
                try { items = LeftoverScanner.Scan(p, others, false, null, CancellationToken.None); }
                catch (Exception ex) { Check("analisi di " + e.Name, false, ex.Message); continue; }
                scanned++;
                total += items.Count;
                CheckItems(e.Name, items, owners, e);
            }
            Console.WriteLine("      {0} analisi, {1} elementi in {2} ms", scanned, total, sw.ElapsedMilliseconds);

            Section("Simulazione del dopo-disinstallazione (solo elenco, nulla viene eliminato)");
            sw.Restart();
            scanned = total = 0;
            foreach (var e in all.Take(maxApps))
            {
                var p = ProfileBuilder.Get(e);
                var others = profiles.Where(x => x.Entry.Id != e.Id).ToList();
                List<LeftoverItem> items;
                try { items = LeftoverScanner.Scan(p, others, true, null, CancellationToken.None); }
                catch (Exception ex) { Check("analisi (dopo) di " + e.Name, false, ex.Message); continue; }
                scanned++;
                total += items.Count;
                CheckItems(e.Name + " (dopo)", items, owners, e);
            }
            Console.WriteLine("      {0} analisi, {1} elementi in {2} ms", scanned, total, sw.ElapsedMilliseconds);
            Console.WriteLine("Risultato: {0} ok, {1} falliti", pass, fail);
            return fail;
        }

        private static void CheckItems(string label, List<LeftoverItem> items, List<Tuple<AppEntry, string>> owners, AppEntry self)
        {
            int bad = 0;
            foreach (var i in items)
            {
                string problem = null;
                if (!Safety.IsDeletable(i)) problem = "non supera i controlli di sicurezza";
                else if (i.IsFileSystem)
                {
                    string n = PathUtil.Normalize(i.Path);
                    string parent = n == null ? null : Path.GetDirectoryName(n);
                    if (n == null) problem = "percorso non valido";
                    else if (PathUtil.Same(parent, KnownDirs.UserProfile) && !Path.GetFileName(n).StartsWith(".")) problem = "cartella dell'utente";
                    else
                    {
                        foreach (var o in owners)
                        {
                            AppEntry e = o.Item1;
                            string d = o.Item2;
                            if (self != null && e.Id == self.Id) continue;
                            // ciò che appartiene a un gioco o programma installato non va mai proposto
                            if (PathUtil.IsUnder(n, d, true)) { problem = "dentro la cartella di " + e.Name; break; }
                            if (PathUtil.IsUnder(d, n)) { problem = "contiene la cartella di " + e.Name; break; }
                        }
                    }
                }
                if (problem == null) continue;
                bad++;
                if (bad <= 25) Console.WriteLine("  FAIL {0}: {1} → {2} ({3})", label, i.DisplayPath, problem, i.Reason);
            }
            if (bad > 0) fail++; else pass++;
            if (bad > 0 || self == null) Console.WriteLine("{0} {1}: {2} elementi, {3} problemi", bad == 0 ? "  ok  " : "  FAIL", label, items.Count, bad);
        }

        // ================================================================== Strumenti

        private static List<string> StagingAtDriveRoot()
        {
            string drive = Path.GetPathRoot(root);
            try { return Directory.GetDirectories(drive, RecycleStaging.Prefix + "*").ToList(); }
            catch { return new List<string>(); }
        }

        private static CleanResult Clean(LeftoverItem item, bool recycle, bool backup, params LeftoverItem[] more)
        {
            var list = new List<LeftoverItem> { item };
            list.AddRange(more);
            return Cleaner.Clean(list, new CleanOptions { UseRecycleBin = recycle, Backup = backup, BackupFolder = Path.Combine(root, "backup"), Title = "prova" }, null, CancellationToken.None);
        }

        private static string Summary(CleanResult r)
        {
            return "rimossi=" + r.Removed + (r.Errors.Count > 0 ? " errori: " + string.Join(" | ", r.Errors) : "") + (r.Notes.Count > 0 ? " note: " + string.Join(" | ", r.Notes) : "");
        }

        private static bool NoThrow(Func<bool> f)
        {
            try { return f(); }
            catch (Exception ex) { Console.WriteLine("      eccezione: " + ex.GetType().Name + " - " + ex.Message); return false; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateSymbolicLink(string link, string target, int flags);

        private static bool MakeJunction(string link, string target)
        {
            int code;
            Run("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"", out code);
            return code == 0 && Directory.Exists(link);
        }

        private static void RemoveLink(string link)
        {
            if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0) throw new InvalidOperationException("non è un collegamento: " + link);
            Directory.Delete(link, false);
        }

        /// <summary>
        /// Eliminazione della cartella di prova, scritta a parte (non usa il codice del programma che si sta verificando):
        /// ogni reparse point viene tolto come collegamento, senza mai entrarci.
        /// </summary>
        private static void HarnessDelete(string dir)
        {
            string p = dir.StartsWith(@"\\?\") ? dir : @"\\?\" + dir; // sempre in forma estesa: vale anche oltre i 260 caratteri
            if (!Directory.Exists(p)) return;
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(p, false); return; }
            foreach (var d in Directory.GetDirectories(p))
            {
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d, false);
                else HarnessDelete(d);
            }
            foreach (var f in Directory.GetFiles(p))
            {
                File.SetAttributes(f, FileAttributes.Normal);
                File.Delete(f);
            }
            new DirectoryInfo(p).Attributes = FileAttributes.Directory;
            Directory.Delete(p, false);
        }

        /// <summary>
        /// Toglie dal Cestino solo gli elementi finiti lì durante queste prove (quelli il cui percorso originale era nella cartella di prova).
        /// </summary>
        private static int PurgeOwnRecycleBinItems(string prefix)
        {
            int n = 0;
            try
            {
                string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
                string bin = Path.Combine(Path.GetPathRoot(prefix), "$Recycle.Bin", sid);
                foreach (var info in Directory.GetFiles(bin, "$I*"))
                {
                    byte[] b = File.ReadAllBytes(info);
                    if (b.Length < 28) continue;
                    long version = BitConverter.ToInt64(b, 0);
                    string original = version >= 2
                        ? Encoding.Unicode.GetString(b, 28, Math.Min(BitConverter.ToInt32(b, 24) * 2, b.Length - 28)).TrimEnd('\0')
                        : Encoding.Unicode.GetString(b, 24, Math.Min(520, b.Length - 24)).TrimEnd('\0');
                    if (!original.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                    string data = Path.Combine(bin, "$R" + Path.GetFileName(info).Substring(2));
                    if (Directory.Exists(data)) HarnessDelete(data);
                    else if (File.Exists(data)) { File.SetAttributes(data, FileAttributes.Normal); File.Delete(data); }
                    File.Delete(info);
                    n++;
                }
            }
            catch (Exception ex) { Console.WriteLine("      pulizia del Cestino: " + ex.Message); }
            return n;
        }

        private static void DeleteTestKeys()
        {
            foreach (var k in new[] { "AppManagerRobustVittima", "AppManagerRobustLecita", "AppManagerRobustAltra", "AppManagerRobustValori", "AppManagerRobustSparita" })
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\" + k, false);
        }

        private static string Run(string exe, string args, out int code)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                code = p.ExitCode;
                return o;
            }
        }
    }
}
