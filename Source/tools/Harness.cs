using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32;

// come il programma: .NET Framework 4.8 (percorsi lunghi, TLS moderno), così le prove valgono anche per lui
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace AppManager
{
    /// <summary>Strumento di test da console. Legge soltanto, tranne i test su dati finti creati (e poi eliminati) qui.</summary>
    internal static class Harness
    {
        /// <summary>Contatori come nelle Impostazioni: trovati da ciascun metodo e, tra questi, quelli già trovati da un metodo precedente.</summary>
        private static void PrintSources(string what, List<AppEntry> list)
        {
            Console.WriteLine("Metodi di ricerca ({0}):", what);
            foreach (FoundVia via in Enum.GetValues(typeof(FoundVia)))
            {
                int total = list.Count(e => e.Via == via || e.FoundBy.Contains(via));
                int before = list.Count(e => e.Via != via && e.FoundBy.Contains(via));
                Console.WriteLine("  {0,-10} {1,4} trovati, {2} già trovati prima", via, total, before);
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            // le prove confrontano i testi originali: la lingua del PC su cui girano non deve contare
            L.ForcedCode = "it";
            SaveDatabase.TestsDisabled = args.Length == 0 || args[0] != "detectaudit";
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "games";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var games = GameDetector.DetectAll(AppSettings.Current, null);
            Safety.SetInstalledDirs("games", games.SelectMany(g => g.Dirs));
            Console.WriteLine("Rilevamento: {0} giochi in {1} ms", games.Count, sw.ElapsedMilliseconds);
            List<AppEntry> programs = new List<AppEntry>();
            if (new[] { "programs", "scan", "orphans", "apptest", "orphantest", "traces", "safety", "robust", "invariants", "cmds" }.Contains(cmd))
            {
                sw.Restart();
                programs = ProgramDetector.DetectAll(AppSettings.Current, games, null);
                Safety.SetInstalledDirs("programs", programs.SelectMany(p => p.Dirs.Concat(p.IsSingleFile && p.MainExe != null ? new[] { p.MainExe } : new string[0])));
                Console.WriteLine("Rilevamento: {0} programmi in {1} ms", programs.Count, sw.ElapsedMilliseconds);
            }
            var all = games.Concat(programs).ToList();

            if (cmd == "games")
            {
                foreach (var g in games)
                {
                    Console.WriteLine("- {0} [{1}] {2}", g.Name, Launchers.Label(g.Launcher), g.InstallDir);
                    Console.WriteLine("    id={0} fonte={1} size={2} exe={3}", g.Id, g.Source, Fmt.Size(g.SizeBytes), g.MainExe);
                    Console.WriteLine("    uninstall={0} | {1}", g.UninstallKey, g.UninstallString);
                    Console.WriteLine("    cover={0} header={1} publisher={2}", g.CoverImage, g.HeaderImage, g.Publisher);
                    Console.WriteLine("    trovato da: {0} (primo: {1})", string.Join(", ", g.FoundBy.OrderBy(v => (int)v)), g.Via);
                }
                PrintSources("giochi", games);
            }
            else if (cmd == "programs")
            {
                bool brief = args.Length > 1 && args[1] == "brief";
                foreach (var p in programs)
                {
                    string comp = p.IsComponent ? " {" + p.ComponentLabel + "}" : "";
                    Console.WriteLine("- {0}{1} [{2}/{3}] {4}", p.Name, comp, Launchers.Label(p.Launcher), p.Via, p.IsSingleFile ? "(file) " + p.MainExe : p.InstallDir);
                    if (brief) continue;
                    Console.WriteLine("    id={0} fonte={1} size={2} ver={3} data={4} editore={5}", p.Id, p.Source, Fmt.Size(p.SizeBytes), p.Version, Fmt.Date(p.InstallDate), p.Publisher);
                    Console.WriteLine("    exe={0} icona={1} contiene altri={2} extra={3}", p.MainExe, p.IconPath, p.ContainsOthers, string.Join(" | ", p.ExtraDirs));
                    if (p.UninstallString != null) Console.WriteLine("    uninstall={0}", p.UninstallString);
                }
                Console.WriteLine("Componenti nascosti: {0}; portable: {1}; Store: {2}", programs.Count(p => p.IsComponent),
                    programs.Count(p => !p.HasUninstaller), programs.Count(p => p.Launcher == LauncherKind.Store));
                PrintSources("programmi", programs);
            }
            else if (cmd == "scan")
            {
                string filter = args.Length > 1 ? args[1] : "";
                bool after = args.Length > 2 && args[2] == "after";
                var profiles = all.Select(ProfileBuilder.Get).ToList();
                foreach (var g in all.Where(x => x.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    var p = ProfileBuilder.Get(g);
                    Console.WriteLine();
                    Console.WriteLine("=== {0} ({1}, {2}) motore={3}", g.Name, g.Kind, Launchers.Label(g.Launcher), p.Engine);
                    Console.WriteLine("  nomi forti: {0}", string.Join(", ", p.NameKeys));
                    Console.WriteLine("  nomi deboli: {0}   brevi: {1}", string.Join(", ", p.WeakKeys), string.Join(", ", p.ShortKeys));
                    Console.WriteLine("  editori: {0}", string.Join(", ", p.PublisherKeys));
                    Console.WriteLine("  unity: {0} / {1}   unreal: {2}", p.UnityCompany, p.UnityProduct, string.Join(", ", p.UnrealProjects));
                    Console.WriteLine("  appid: {0}   exe: {1}", string.Join(", ", p.SteamAppIds), string.Join(", ", p.ExeNames.Take(8)));
                    sw.Restart();
                    var items = LeftoverScanner.Scan(p, profiles, after, null, CancellationToken.None);
                    Console.WriteLine("  prefetch: {0} file, cartelle esclusive: {1}", p.PrefetchFiles, string.Join(" | ", p.PrefetchDirs));
                    Console.WriteLine("  -> {0} elementi in {1} ms", items.Count, sw.ElapsedMilliseconds);
                    foreach (var i in items) PrintItem(i);
                }
            }
            else if (cmd == "orphans")
            {
                sw.Restart();
                var items = OrphanScanner.Scan(games, programs, null, CancellationToken.None);
                Console.WriteLine("Residui orfani: {0} elementi in {1} ms", items.Count, sw.ElapsedMilliseconds);
                bool brief = args.Length > 1 && args[1] == "brief";
                foreach (var grp in items.GroupBy(i => i.GroupKey).OrderBy(x => x.First().GroupKind).ThenByDescending(x => x.Sum(i => Math.Max(0, i.Size))))
                {
                    var first = grp.First();
                    if (brief)
                    {
                        Console.WriteLine("{0,-8} {1,-45} [{2,-15}] {3,9}  file={4} sistema={5} collegamenti={6}", first.GroupKind, first.GroupName, first.GroupSource,
                            Fmt.Size(grp.Sum(i => Math.Max(0, i.Size))), grp.Count(i => i.IsFileSystem && i.Category != ItemCategory.Shortcuts),
                            grp.Count(i => !i.IsFileSystem), grp.Count(i => i.Category == ItemCategory.Shortcuts));
                        continue;
                    }
                    Console.WriteLine();
                    Console.WriteLine("### [{0}] {1}  [{2}]  {3}  img={4}", first.GroupKind, first.GroupName, first.GroupSource, Fmt.Size(grp.Sum(i => Math.Max(0, i.Size))), first.GroupImage != null);
                    foreach (var i in grp) PrintItem(i);
                }
            }
            else if (cmd == "traces")
            {
                Time("Run", () => Traces.RunEntries().Count);
                Time("attività", () => Traces.ScheduledTasks().Count);
                Time("servizi", () => Traces.Services().Count);
                Time("ProgID e Applications", () => Traces.ClassCommands().Count);
                Time("voci dei menu", () => Traces.ShellVerbs().Count);
                Time("componenti COM", () => Traces.ComServers().Count);
                Time("Apri con (tutti)", () => Traces.AllOpenWithProgIds().Count);
                Time("app predefinite", () => Traces.RegisteredApplications().Count);
                Time("client", () => Traces.Clients().Count);
                Time("PATH", () => Traces.PathEntries().Count);
                Time("tracce exe", () => Traces.ExePathValues().Count);
                Time("notifiche", () => Traces.NotificationSettings().Count);
                foreach (var t in Traces.ShellVerbs().Take(15)) Console.WriteLine("  menu: {0} → {1} ({2})", t.Tag, t.ExePath, t.KeyPath);
                foreach (var t in Traces.ScheduledTasks()) Console.WriteLine("  attività: {0} → {1} (admin={2})", t.Path, string.Join(" ; ", t.Exes), t.NeedsAdmin);
                foreach (var e in Traces.PathEntries()) Console.WriteLine("  PATH {0}: {1} → {2}", e.KeyPath.StartsWith("HKCU") ? "utente" : "sistema", e.Raw, e.Expanded);
            }
            else if (cmd == "cmds")
            {
                // solo lettura: come verrebbero avviate le disinstallazioni dei programmi veri
                foreach (var p in all.Where(x => !string.IsNullOrWhiteSpace(x.UninstallString)))
                {
                    string c = Environment.ExpandEnvironmentVariables(p.UninstallString.Trim());
                    string q = UninstallSession.QualifyCommand(c);
                    string exe = PathUtil.ExeFromCommand(q);
                    bool ok = exe != null && (!Path.IsPathRooted(exe) || File.Exists(exe));
                    Console.WriteLine("{0} {1,-40} {2}", q == c ? "  " : "=>", p.Name.Length > 40 ? p.Name.Substring(0, 40) : p.Name, q == c ? c : c + "  →  " + q);
                    if (!ok) Console.WriteLine("   (eseguibile non trovato: {0})", exe);
                }
            }
            else if (cmd == "robust") return RobustTests.Run(args[1], games, programs, args.Length > 2 ? args[2] : null) == 0 ? 0 : 1;
            else if (cmd == "savetest") return SaveTests.Run(args[1]);
            else if (cmd == "recycletest") return RecycleTests.Run(args[1]);
            else if (cmd == "logtest") return LogTests.Run(args[1]);
            else if (cmd == "pctest") return PcTests.Run(args[1]);
            else if (cmd == "watchtest") return WatchTests.Run(args[1]);
            else if (cmd == "detectaudit")
            {
                // sola lettura: cosa viene trovato, per metodo e con le cartelle dei salvataggi di ogni gioco
                Console.WriteLine("== GIOCHI");
                foreach (var g in games.OrderBy(x => x.Name)) Console.WriteLine("{0} | {1} | {2} | exe={3}", g.Name, g.Via, g.InstallDir, g.MainExe);
                Console.WriteLine("== PROGRAMMI");
                var realPrograms = ProgramDetector.DetectAll(AppSettings.Current, games, null);
                Safety.SetInstalledDirs("games", games.SelectMany(x => x.Dirs));
                Safety.SetInstalledDirs("programs", realPrograms.SelectMany(p => p.Dirs.Concat(p.IsSingleFile && p.MainExe != null ? new[] { p.MainExe } : new string[0])));
                foreach (var g in realPrograms.Where(x => !x.IsComponent).OrderBy(x => x.Name)) Console.WriteLine("{0} | {1} | {2} | exe={3}", g.Name, g.Via, g.InstallDir, g.MainExe);
                Console.WriteLine("== CARTELLE PORTABLE CONTROLLATE");
                var auto = new AppSettings { LibraryFolders = new List<string>(), ProgramFolders = new List<string>() };
                foreach (var f in InstalledApps.PortableRoots(AppKind.Game, auto)) Console.WriteLine("giochi: " + f);
                foreach (var f in InstalledApps.PortableRoots(AppKind.Program, auto)) Console.WriteLine("programmi: " + f);
                Console.WriteLine("== SALVATAGGI");
                foreach (var g in games.OrderBy(x => x.Name))
                {
                    var locs = SaveVault.FindLocations(g, games.Concat(realPrograms), false, null, CancellationToken.None);
                    Console.WriteLine("{0}: {1} posizioni", g.Name, locs.Count);
                    foreach (var l in locs) Console.WriteLine("    {0} | {1} | {2}", l.Confidence, l.Path, Fmt.Size(l.Size));
                }
                Console.WriteLine("== RESIDUI");
                var left = OrphanScanner.Scan(games, realPrograms, null, CancellationToken.None);
                foreach (var grp in left.GroupBy(i => i.GroupKey).OrderBy(x => x.First().GroupKind).ThenBy(x => x.First().GroupName))
                {
                    var f = grp.First();
                    Console.WriteLine("[{0}] {1} ({2}) {3} elementi", f.GroupKind, f.GroupName, f.GroupSource, grp.Count());
                    foreach (var i in grp.Take(4)) Console.WriteLine("    {0} | {1} | {2}", i.Confidence, i.DisplayPath.Length > 110 ? i.DisplayPath.Substring(0, 110) + "…" : i.DisplayPath, i.Reason.Length > 70 ? i.Reason.Substring(0, 70) + "…" : i.Reason);
                }
                return 0;
            }
            else if (cmd == "launchtest") return Native.ShellExecuteViaExplorer(args[1], Path.GetDirectoryName(args[1])) ? 0 : 1;
            else if (cmd == "invariants") return RobustTests.Invariants(games, programs, args.Length > 1 ? int.Parse(args[1]) : 1000) == 0 ? 0 : 1;
            else if (cmd == "settingstest")
            {
                // da lanciare con APPMANAGER_DATA verso una cartella di prova che contiene file corrotti
                Console.WriteLine("Dati: {0}", AppPaths.DataDir);
                var s = AppSettings.Current;
                Console.WriteLine("Impostazioni caricate: cestino={0} backup={1} cartella={2} librerie={3} nascosti={4}", s.UseRecycleBin, s.BackupBeforeClean, s.BackupFolder,
                    s.LibraryFolders.Count, s.HiddenGames.Count);
                s.Save();
                Console.WriteLine("Salvate di nuovo: {0}; copia del file danneggiato: {1}", File.Exists(AppPaths.SettingsFile), File.Exists(AppPaths.SettingsFile + ".danneggiato"));
                Console.WriteLine("Indice delle copertine corrotto: pulizia della cache con {0} file eliminati, indice riscritto: {1}", CoverService.Prune(games),
                    File.Exists(Path.Combine(AppPaths.DataDir, "covers", "index.json")));
            }
            else if (cmd == "apptest") AppTest(args[1], all);
            else if (cmd == "orphantest") OrphanTest(args[1], games, programs);
            else if (cmd == "cleantest")
            {
                // Test su dati sintetici creati qui (cartella di prova + HKCU\Software\AppManagerTest)
                string sandbox = args[1];
                string saveDir = Path.Combine(sandbox, "TestPublisher", "TestGame");
                Directory.CreateDirectory(Path.Combine(saveDir, "slots", "empty"));
                File.WriteAllText(Path.Combine(saveDir, "save1.dat"), "progress=42");
                File.WriteAllText(Path.Combine(saveDir, "slots", "slot1.sav"), "slot data àèìòù");
                string ro = Path.Combine(saveDir, "readonly.cfg");
                File.WriteAllText(ro, "ro");
                File.SetAttributes(ro, FileAttributes.ReadOnly);
                string single = Path.Combine(sandbox, "screenshot.png");
                File.WriteAllBytes(single, new byte[] { 1, 2, 3, 4 });
                const string key = @"HKCU\Software\AppManagerTest\TestGame";
                using (var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                using (var k = root.CreateSubKey(@"Software\AppManagerTest\TestGame"))
                using (var sub = k.CreateSubKey("Sub"))
                {
                    k.SetValue("", "valore predefinito");
                    k.SetValue("Str", "C:\\percorso \"con\" virgolette");
                    k.SetValue("Dw", 42, RegistryValueKind.DWord);
                    k.SetValue("Qw", 1234567890123L, RegistryValueKind.QWord);
                    k.SetValue("Bin", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, RegistryValueKind.Binary);
                    k.SetValue("Multi", new[] { "uno", "due" }, RegistryValueKind.MultiString);
                    k.SetValue("Exp", "%TEMP%\\x", RegistryValueKind.ExpandString);
                    sub.SetValue("Nested", "ok");
                }
                var items = new List<LeftoverItem>
                {
                    new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Saves, Path = saveDir, Size = PathUtil.Size(saveDir) },
                    new LeftoverItem { Kind = ItemKind.File, Category = ItemCategory.Settings, Path = single, Size = 4 },
                    new LeftoverItem { Kind = ItemKind.RegistryKey, Category = ItemCategory.Registry, Path = key },
                };
                var opt = new CleanOptions { UseRecycleBin = false, Backup = true, BackupFolder = Path.Combine(sandbox, "backup"), Title = "Test Game" };
                var res = Cleaner.Clean(items, opt, null, CancellationToken.None);
                Console.WriteLine("Pulizia: rimossi={0} liberati={1} errori={2} backup={3}", res.Removed, Fmt.Size(res.FreedBytes), string.Join(" | ", res.Errors), res.BackupPath);
                Console.WriteLine("  cartella eliminata: {0}; cartella madre vuota rimossa: {1}", !Directory.Exists(saveDir), !Directory.Exists(Path.GetDirectoryName(saveDir)));
                Console.WriteLine("  file eliminato: {0}; chiave eliminata: {1}; chiave madre vuota rimossa: {2}", !File.Exists(single), !RegUtil.KeyExists(key), !RegUtil.KeyExists(@"HKCU\Software\AppManagerTest"));
                var list = Cleaner.ListBackups(opt.BackupFolder);
                Console.WriteLine("Backup elencati: {0} ({1}, {2} elementi, {3} di sistema)", list.Count, list[0].Title, list[0].ItemCount, list[0].RegistryCount);
                var rr = Cleaner.Restore(list[0].ZipPath, false, null);
                Console.WriteLine("Ripristino: elementi={0} errori={1}", rr.Removed, string.Join(" | ", rr.Errors));
                Console.WriteLine("  save1.dat = {0}", File.ReadAllText(Path.Combine(saveDir, "save1.dat")));
                Console.WriteLine("  slot1.sav = {0}", File.ReadAllText(Path.Combine(saveDir, "slots", "slot1.sav")));
                Console.WriteLine("  cartella vuota ripristinata: {0}", Directory.Exists(Path.Combine(saveDir, "slots", "empty")));
                using (var k = RegUtil.Open(key))
                {
                    Console.WriteLine("  reg: def={0} | Str={1} | Dw={2} | Qw={3} | Bin={4} | Multi={5} | Exp={6}", k.GetValue(""), k.GetValue("Str"), k.GetValue("Dw"), k.GetValue("Qw"),
                        BitConverter.ToString((byte[])k.GetValue("Bin")), string.Join(",", (string[])k.GetValue("Multi")),
                        k.GetValue("Exp", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
                    using (var sub = k.OpenSubKey("Sub")) Console.WriteLine("  reg sub: {0}", sub.GetValue("Nested"));
                }
                Console.WriteLine("Conflitti ora (file già presenti): {0}", Cleaner.CountConflicts(list[0].ZipPath));
                // pulizia finale dei dati di prova (senza backup)
                var res2 = Cleaner.Clean(items, new CleanOptions { UseRecycleBin = false, Backup = false, Title = "fine test" }, null, CancellationToken.None);
                Console.WriteLine("Pulizia finale: rimossi={0} errori={1}; chiave test rimasta: {2}", res2.Removed, res2.Errors.Count, RegUtil.KeyExists(@"HKCU\Software\AppManagerTest"));
                Console.WriteLine("Sicurezza: C:\\Windows={0} Documenti={1} HKCU\\Software\\Microsoft\\X={2} HKCU\\Software={3}",
                    Safety.IsDeletablePath(@"C:\Windows"), Safety.IsDeletablePath(KnownDirs.Documents),
                    Safety.IsDeletableRegistryKey(@"HKCU\Software\Microsoft\X"), Safety.IsDeletableRegistryKey(@"HKCU\Software"));
            }
            else if (cmd == "pftest")
            {
                // File Prefetch sintetico (formato v30, Windows 10/11) compresso come fa Windows
                string outFile = args[1];
                uint serial = Native.GetVolumeSerial(@"C:\").Value;
                string vol = @"\VOLUME{01d9a1b2c3d4e5f6-" + serial.ToString("X8") + "}";
                string user = KnownDirs.UserProfile.Substring(2).ToUpperInvariant(); // \USERS\NOME
                var files = new[]
                {
                    vol + @"\WINDOWS\SYSTEM32\NTDLL.DLL",
                    vol + @"\GAMES\FAKE PORTABLE\BIN\FAKEGAME-WIN64-SHIPPING.EXE",
                    vol + @"\GAMES\FAKE PORTABLE\BIN\STEAM_API64.DLL",
                    vol + user + @"\APPDATA\LOCAL\NVIDIA\DXCACHE\ABC.BIN",
                    vol + user + @"\DOCUMENTS\MY GAMES\FAKE SAVES\PROFILE.SAV",
                };
                var dirs = new[] { vol + @"\GAMES", vol + @"\GAMES\FAKE PORTABLE", vol + user + @"\APPDATA\LOCAL\FAKESTUDIO", vol + user + @"\APPDATA\LOCAL\FAKESTUDIO\SAVED" };
                var fn = new List<byte>();
                foreach (var f in files) fn.AddRange(Encoding.Unicode.GetBytes(f + "\0"));
                int fnOff = 0x130, volOff = fnOff + fn.Count;
                var volBlock = new List<byte>(new byte[96]);
                int devOff = volBlock.Count;
                volBlock.AddRange(Encoding.Unicode.GetBytes(vol + "\0"));
                int dirOff = volBlock.Count;
                foreach (var d in dirs)
                {
                    volBlock.AddRange(BitConverter.GetBytes((ushort)d.Length));
                    volBlock.AddRange(Encoding.Unicode.GetBytes(d + "\0"));
                }
                var entry = volBlock.ToArray();
                BitConverter.GetBytes(devOff).CopyTo(entry, 0x00);
                BitConverter.GetBytes(vol.Length).CopyTo(entry, 0x04);
                BitConverter.GetBytes(serial).CopyTo(entry, 0x10);
                BitConverter.GetBytes(dirOff).CopyTo(entry, 0x1C);
                BitConverter.GetBytes(dirs.Length).CopyTo(entry, 0x20);
                var data = new byte[volOff + entry.Length];
                BitConverter.GetBytes(30).CopyTo(data, 0);
                Encoding.ASCII.GetBytes("SCCA").CopyTo(data, 4);
                BitConverter.GetBytes(data.Length).CopyTo(data, 0x0C);
                Encoding.Unicode.GetBytes("FAKEGAME-WIN64-SHIPPING.EXE").CopyTo(data, 0x10);
                BitConverter.GetBytes(fnOff).CopyTo(data, 0x64);
                BitConverter.GetBytes(fn.Count).CopyTo(data, 0x68);
                BitConverter.GetBytes(volOff).CopyTo(data, 0x6C);
                BitConverter.GetBytes(1).CopyTo(data, 0x70);
                BitConverter.GetBytes(entry.Length).CopyTo(data, 0x74);
                BitConverter.GetBytes(DateTime.Now.AddDays(-3).ToFileTime()).CopyTo(data, 0x80);
                BitConverter.GetBytes(17).CopyTo(data, 0xD0);
                fn.ToArray().CopyTo(data, fnOff);
                entry.CopyTo(data, volOff);
                var comp = Native.Compress(Native.XpressHuffman, data);
                var file = new byte[8 + comp.Length];
                Encoding.ASCII.GetBytes("MAM").CopyTo(file, 0);
                file[3] = 4;
                BitConverter.GetBytes(data.Length).CopyTo(file, 4);
                comp.CopyTo(file, 8);
                File.WriteAllBytes(outFile, file);
                Console.WriteLine("Scritto {0}: {1} byte (non compresso {2})", outFile, file.Length, data.Length);
                var r = PrefetchIndex.Parse(outFile);
                Console.WriteLine("Exe: {0} | percorso: {1} | esiste: {2} | avvii: {3} | ultimo: {4}", r.ExeName, r.ExePath, r.ExeExists, r.RunCount, r.LastRun);
                Console.WriteLine("Impronte di gioco: {0}", string.Join(", ", r.MarkerHits));
                Console.WriteLine("Cartelle àncora: {0}", string.Join(" | ", r.Anchors));
            }
            else if (cmd == "match")
            {
                var pairs = new[,]
                {
                    { "Control Resonant", "Control Ultimate Edition" }, { "Wreckfest 2", "Wreckfest" }, { "GTA: The Trilogy - DE", "Grand Theft Auto: The Trilogy – The Definitive Edition" },
                    { "DMC Devil May Cry", "DmC: Devil May Cry" }, { "Pinball FX", "Pinball FX3" }, { "Pinball FX", "Pinball FX" },
                    { "Assassin's Creed Black Flag Resynced", "Assassin's Creed IV Black Flag" }, { "Lost Records: Bloom & Rage", "Lost Records: Bloom & Rage" },
                    { "Terminator 2D - NO FATE", "Terminator 2D: NO FATE" }, { "Soulstone Survivors", "Soulstone Survivors - Ancient Journey" },
                };
                for (int i = 0; i < pairs.GetLength(0); i++)
                    Console.WriteLine("{0,-40} ~ {1,-58} = {2:0.00}", pairs[i, 0], pairs[i, 1], CoverService.MatchScore(pairs[i, 0], pairs[i, 1]));
                foreach (var n in new[] { "Python 3.12.10 (64-bit)", "PuTTY release 0.84 (64-bit)", "Cheat Engine 7.7", "K-Lite Codec Pack 19.6.0 Basic", "Microsoft Visual C++ 2008 Redistributable - x64 9.0.30729.6161", "NVIDIA App 11.0.8.299", "7-Zip 24.08 (x64)" })
                    Console.WriteLine("nome di base: {0,-62} → {1}", n, NameUtil.BaseProgramName(n));
                Console.WriteLine("versione tolta: {0} | {1}", NameUtil.StripVersion("LocalSend versione 1.17.0", "1.17.0"), NameUtil.StripVersion("Win32DiskImager version 1.0.0", "1.0.0"));
            }
            else if (cmd == "covers")
            {
                foreach (var g in games.Where(x => x.CoverImage == null || CoverService.IsLowRes(x.CoverImage) || (x.HeaderImage == null && x.HeroImage == null)))
                {
                    sw.Restart();
                    string local = g.CoverImage != null && !g.CoverImage.Contains("librarycache") ? " (locale: " + Path.GetFileName(g.CoverImage) + ")" : "";
                    var r = CoverService.ForGame(g);
                    Console.WriteLine("{0,-38}{1} → {2} | fonte={3} | copertina={4} testata={5} hero={6} logo={7} ({8} ms)", g.Name, local, r == null ? "—" : r.Name,
                        r == null ? "" : r.Source, r != null && r.Portrait != null, r != null && r.Header != null, r != null && r.Hero != null,
                        r != null && r.Logo != null, sw.ElapsedMilliseconds);
                }
            }
            else if (cmd == "net")
            {
                // solo lettura: connessioni attive per processo (confrontabile con netstat -ano)
                var conns = NetMonitor.Connections();
                Console.WriteLine("Connessioni TCP totali: {0}", conns.Count);
                foreach (var g in conns.Where(c => c.HasRemote).GroupBy(c => c.Pid).OrderBy(x => x.Key))
                {
                    string path = Native.GetProcessPath(g.Key);
                    Console.WriteLine("PID {0}  {1}", g.Key, path ?? "?");
                    foreach (var c in g.Take(6))
                        Console.WriteLine("    {0,-8} {1,-46} -> {2}", c.StateText, (c.IPv6 ? "[" + c.Local + "]" : c.Local.ToString()) + ":" + c.LocalPort, c.RemoteText);
                }
            }
            else if (cmd == "cachetest")
            {
                // cartella di cache finta con un file bloccato (come i file in uso dal driver video)
                string dir = Path.Combine(args[1], "FakeShaderCache");
                Directory.CreateDirectory(Path.Combine(dir, "sub"));
                File.WriteAllText(Path.Combine(dir, "a.bin"), "a");
                File.WriteAllText(Path.Combine(dir, "sub", "b.bin"), "b");
                string lockedFile = Path.Combine(dir, "locked.bin");
                File.WriteAllText(lockedFile, "c");
                using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var item = new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Cache, Path = dir, Size = 3 };
                    var res = Cleaner.Clean(new List<LeftoverItem> { item }, new CleanOptions { UseRecycleBin = true, Backup = false, Title = "test cache" }, null, CancellationToken.None);
                    Console.WriteLine("rimossi={0} errori={1}", res.Removed, string.Join(" | ", res.Errors));
                    Console.WriteLine("a.bin={0} sub\\b.bin={1} locked.bin={2}", File.Exists(Path.Combine(dir, "a.bin")), File.Exists(Path.Combine(dir, "sub", "b.bin")), File.Exists(lockedFile));
                }
                Directory.Delete(dir, true);
                Console.WriteLine("pulizia della prova: cartella rimasta={0}", Directory.Exists(dir));
            }
            else if (cmd == "prune")
            {
                // simula l'immagine scaricata di un gioco che non c'è più
                string dir = Path.Combine(AppPaths.DataDir, "covers");
                Directory.CreateDirectory(dir);
                string fake = Path.Combine(dir, "gioco_rimosso_portrait.jpg");
                File.WriteAllBytes(fake, new byte[] { 0xFF, 0xD8, 0xFF });
                int before = Directory.GetFiles(dir).Length;
                int removed = CoverService.Prune(games);
                Console.WriteLine("File prima: {0}, eliminati: {1}, immagine finta rimasta: {2}", before, removed, File.Exists(fake));
            }
            else if (cmd == "pf")
            {
                Console.WriteLine("Prefetch disponibile: {0}", PrefetchIndex.IsAvailable);
                Console.WriteLine("Record: {0}", PrefetchIndex.Records.Count);
            }
            else if (cmd == "safety")
            {
                foreach (var path in args.Skip(1))
                    Console.WriteLine("{0} -> {1}", path, path.StartsWith("HK") ? Safety.IsDeletableRegistryKey(path) : Safety.IsDeletablePath(path));
            }
            else if (cmd == "appinfo")
            {
                var info = SteamInfo.AppInfo;
                Console.WriteLine("appinfo: {0} app", info.Count);
                foreach (var id in args.Skip(1))
                {
                    uint n;
                    SteamAppInfo a;
                    if (uint.TryParse(id, out n) && info.TryGetValue(n, out a))
                        Console.WriteLine("{0}: {1} [{2}] dir={3}", id, a.Name, a.Type, a.InstallDir);
                }
            }
            else if (cmd == "paths")
            {
                Console.WriteLine("Dati: {0} (portable={1})", AppPaths.DataDir, AppPaths.IsPortable);
                Console.WriteLine("Backup predefinito: {0}; impostato: {1}", AppPaths.DefaultBackupDir, AppSettings.Current.BackupFolder);
            }
            Console.WriteLine("Tempo totale: {0} ms", sw.ElapsedMilliseconds);
            return 0;
        }

        private static void PrintItem(LeftoverItem i)
        {
            Console.WriteLine("     [{0,-10}] {1,-6} {2,9}  {3}\n                              ↳ {4}", i.Category, i.Confidence, i.Size >= 0 ? Fmt.Size(i.Size) : "", i.DisplayPath, i.Reason);
        }

        private static void Time(string what, Func<int> f)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = f();
            Console.WriteLine("{0,-24} {1,6} in {2,5} ms", what, n, sw.ElapsedMilliseconds);
        }

        // ------------------------------------------------------------------ Test completo su un programma finto

        private const string TestProgId = "AppManagerTest.Document";
        private const string TestClsid = "{7E57A11E-0000-4000-8000-A99A11A9E57A}";
        private const string TestTask = "\\AppManagerTest Aggiornamento";
        private const string TestRun = "AppManagerTest";

        /// <summary>
        /// Crea un programma finto con tracce in tutti i posti noti (Run, attività, tipo di file, menu contestuale, COM, PATH,
        /// cartelle dati), lo "disinstalla" eliminandone la cartella e verifica che AppManager trovi, elimini e ripristini tutto.
        /// Alla fine rimuove ogni traccia e rimette la variabile PATH com'era.
        /// </summary>
        private static void AppTest(string sandbox, List<AppEntry> installed)
        {
            string app = Path.Combine(sandbox, "AppManagerTestApp");
            string exe = Path.Combine(app, "amtestapp.exe");
            string dll = Path.Combine(app, "amtestshell.dll");
            string bin = Path.Combine(app, "bin");
            string data = Path.Combine(KnownDirs.RoamingAppData, "AppManagerTestApp");
            string originalPath = UserPath();
            try
            {
                Directory.CreateDirectory(bin);
                File.Copy(Path.Combine(KnownDirs.System32, "whoami.exe"), exe, true);
                File.WriteAllBytes(dll, new byte[] { 0x4D, 0x5A });
                Directory.CreateDirectory(data);
                File.WriteAllText(Path.Combine(data, "settings.ini"), "tema=scuro");
                CreateTraces(exe, dll, bin);

                var entry = new AppEntry { Id = "test:amtestapp", Kind = AppKind.Program, Name = "AppManagerTestApp", InstallDir = app, MainExe = exe, Launcher = LauncherKind.Other };
                var others = installed.Select(ProfileBuilder.Get).ToList();
                var profile = ProfileBuilder.Build(entry);
                Console.WriteLine("Prima della disinstallazione (analisi):");
                foreach (var i in LeftoverScanner.Scan(profile, others, false, null, CancellationToken.None)) PrintItem(i);

                // "disinstallazione": la cartella del programma sparisce
                Directory.Delete(app, true);
                var items = LeftoverScanner.Scan(profile, others, true, null, CancellationToken.None);
                Console.WriteLine("Dopo la disinstallazione: {0} elementi", items.Count);
                foreach (var i in items) PrintItem(i);

                var opt = new CleanOptions { UseRecycleBin = false, Backup = true, BackupFolder = Path.Combine(sandbox, "backup"), Title = "AppManagerTestApp" };
                var res = Cleaner.Clean(items, opt, null, CancellationToken.None);
                Console.WriteLine("Pulizia: rimossi={0} errori={1}", res.Removed, string.Join(" | ", res.Errors));
                ReportTraces("dopo la pulizia", exe, dll, bin, data);

                var backups = Cleaner.ListBackups(opt.BackupFolder);
                var rr = Cleaner.Restore(backups[0].ZipPath, false, null);
                Console.WriteLine("Ripristino: elementi={0} errori={1} note={2}", rr.Removed, string.Join(" | ", rr.Errors), string.Join(" | ", rr.Notes));
                ReportTraces("dopo il ripristino", exe, dll, bin, data);
            }
            finally
            {
                RemoveTraces(data, originalPath);
                if (Directory.Exists(app)) Directory.Delete(app, true);
                ReportTraces("dopo la rimozione finale", exe, dll, bin, data);
                Console.WriteLine("PATH dell'utente com'era: {0}", UserPath() == originalPath);
            }
        }

        /// <summary>Le stesse tracce, ma per un programma "già cancellato": devono comparire tra i residui dei programmi.</summary>
        private static void OrphanTest(string sandbox, List<AppEntry> games, List<AppEntry> programs)
        {
            string app = Path.Combine(KnownDirs.LocalPrograms, "AppManagerGoneTest");
            string exe = Path.Combine(app, "amgone.exe");
            string dll = Path.Combine(app, "amgoneshell.dll");
            string bin = Path.Combine(app, "bin");
            string data = Path.Combine(KnownDirs.RoamingAppData, "AppManagerGoneTest");
            string originalPath = UserPath();
            try
            {
                Directory.CreateDirectory(data);
                File.WriteAllText(Path.Combine(data, "config.json"), "{}");
                CreateTraces(exe, dll, bin);
                var items = OrphanScanner.Scan(games, programs, null, CancellationToken.None);
                foreach (var grp in items.Where(i => (i.GroupName ?? "").IndexOf("AppManagerGoneTest", StringComparison.OrdinalIgnoreCase) >= 0
                                                  || (i.DisplayPath ?? "").IndexOf("AppManager", StringComparison.OrdinalIgnoreCase) >= 0
                                                  || (i.Data ?? "").IndexOf("AppManager", StringComparison.OrdinalIgnoreCase) >= 0).GroupBy(i => i.GroupKey))
                {
                    var first = grp.First();
                    Console.WriteLine("### [{0}] {1} [{2}]", first.GroupKind, first.GroupName, first.GroupSource);
                    foreach (var i in grp) PrintItem(i);
                }
            }
            finally
            {
                RemoveTraces(data, originalPath);
                ReportTraces("dopo la rimozione finale", exe, dll, bin, data);
                Console.WriteLine("PATH dell'utente com'era: {0}", UserPath() == originalPath);
            }
        }

        private static string UserPath()
        {
            using (var k = RegUtil.Open(Traces.UserEnvironment))
                return k == null ? null : k.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }

        private static void CreateTraces(string exe, string dll, string bin)
        {
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            {
                using (var run = hkcu.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) run.SetValue(TestRun, "\"" + exe + "\" --minimized");
                using (var pid = hkcu.CreateSubKey(@"Software\Classes\" + TestProgId + @"\shell\open\command")) pid.SetValue("", "\"" + exe + "\" \"%1\"");
                using (var ext = hkcu.CreateSubKey(@"Software\Classes\.amtest\OpenWithProgids")) ext.SetValue(TestProgId, new byte[0], RegistryValueKind.None);
                using (var verb = hkcu.CreateSubKey(@"Software\Classes\Directory\shell\AppManagerTestVerb"))
                {
                    verb.SetValue("", "Apri con AppManagerTest");
                    using (var c = verb.CreateSubKey("command")) c.SetValue("", "\"" + exe + "\" \"%V\"");
                }
                using (var cls = hkcu.CreateSubKey(@"Software\Classes\CLSID\" + TestClsid + @"\InprocServer32")) cls.SetValue("", dll);
                using (var sx = hkcu.CreateSubKey(@"Software\Classes\*\shellex\ContextMenuHandlers\AppManagerTest")) sx.SetValue("", TestClsid);
                using (var app = hkcu.CreateSubKey(@"Software\AppManagerTestApp")) app.SetValue("Versione", "1.0");
            }
            string path = UserPath() ?? "";
            using (var env = RegUtil.Open(Traces.UserEnvironment, true))
                env.SetValue("Path", path.TrimEnd(';') + ";" + bin, RegistryValueKind.ExpandString);
            int code;
            string output = Run("schtasks.exe", "/Create /TN \"" + TestTask + "\" /TR \"\\\"" + exe + "\\\" --update\" /SC ONCE /ST 23:59 /SD 01/01/2030 /F", out code);
            Console.WriteLine("Attività di prova: codice {0} {1}", code, output.Trim());
        }

        private static void RemoveTraces(string data, string originalPath)
        {
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            {
                using (var run = hkcu.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) if (run != null) run.DeleteValue(TestRun, false);
                foreach (var k in new[] { @"Software\Classes\" + TestProgId, @"Software\Classes\.amtest", @"Software\Classes\Directory\shell\AppManagerTestVerb",
                                          @"Software\Classes\CLSID\" + TestClsid, @"Software\Classes\*\shellex\ContextMenuHandlers\AppManagerTest", @"Software\AppManagerTestApp" })
                    hkcu.DeleteSubKeyTree(k, false);
            }
            if (originalPath != null)
                using (var env = RegUtil.Open(Traces.UserEnvironment, true)) env.SetValue("Path", originalPath, RegistryValueKind.ExpandString);
            int code;
            Run("schtasks.exe", "/Delete /TN \"" + TestTask + "\" /F", out code);
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }

        private static void ReportTraces(string when, string exe, string dll, string bin, string data)
        {
            bool run = RegUtil.ValueExists(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", TestRun);
            bool progId = RegUtil.KeyExists(@"HKCU\Software\Classes\" + TestProgId);
            bool openWith = RegUtil.ValueExists(@"HKCU\Software\Classes\.amtest\OpenWithProgids", TestProgId);
            bool verb = RegUtil.KeyExists(@"HKCU\Software\Classes\Directory\shell\AppManagerTestVerb");
            bool clsid = RegUtil.KeyExists(@"HKCU\Software\Classes\CLSID\" + TestClsid);
            bool shellex = RegUtil.KeyExists(@"HKCU\Software\Classes\*\shellex\ContextMenuHandlers\AppManagerTest");
            bool key = RegUtil.KeyExists(@"HKCU\Software\AppManagerTestApp");
            bool path = (UserPath() ?? "").IndexOf(bin, StringComparison.OrdinalIgnoreCase) >= 0;
            int code;
            bool task = Run("schtasks.exe", "/Query /TN \"" + TestTask + "\"", out code) != null && code == 0;
            Console.WriteLine("Tracce {0}: Run={1} ProgID={2} ApriCon={3} menu={4} COM={5} shellex={6} chiave={7} PATH={8} attività={9} dati={10}",
                when, run, progId, openWith, verb, clsid, shellex, key, path, task, Directory.Exists(data));
        }

        private static string Run(string exe, string args, out int code)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                code = p.ExitCode;
                return o;
            }
        }
    }
}
