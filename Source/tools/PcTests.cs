using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace AppManager
{
    /// <summary>
    /// Prove di riconoscimento del PC, risorse, avvio automatico, esportazione e lingue.
    /// Vanno eseguite con APPMANAGER_DATA impostata su una cartella di prova: nulla di vero viene toccato.
    /// </summary>
    internal static class PcTests
    {
        private static int failures;

        /// <summary>Copie portable, piratate e repack ricostruite in una cartella di prova: nessuna dipende da ciò che è installato su questo PC.</summary>
        static void DetectionCases()
        {
            Console.WriteLine("Copie portable, piratate e repack");
            string root = Path.Combine(Path.GetTempPath(), "AppManagerCasi_" + Guid.NewGuid().ToString("N"));
            Func<string, string> file = rel =>
            {
                string p = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllBytes(p, new byte[] { 0x4D, 0x5A });
                return p;
            };
            try
            {
                // giochi: crack, repack, motori
                file(@"Roba\Elden.Ring-CODEX\eldenring.exe"); file(@"Roba\Elden.Ring-CODEX\steam_api64.dll"); file(@"Roba\Elden.Ring-CODEX\steam_emu.ini");
                file(@"Roba\Sub\Hollow Knight [FitGirl Repack]\hollow_knight.exe"); file(@"Roba\Sub\Hollow Knight [FitGirl Repack]\UnityPlayer.dll");
                file(@"Roba\Oscuro Motore v1.2-DODI\OscuroMotore.exe");
                file(@"Giochi vecchi\Stalker\bin\xrEngine.exe"); file(@"Giochi vecchi\Stalker\bin\mss32.dll");
                file(@"Giochi vecchi\Unrealone\Engine\Binaries\Win64\x.dll"); file(@"Giochi vecchi\Unrealone\Unrealone\Binaries\Win64\Unrealone-Win64-Shipping.exe");
                file(@"Giochi vecchi\RpgVecchio\RPG_RT.exe");
                file(@"Giochi vecchi\Solo Emu\Gioco.exe"); file(@"Giochi vecchi\Solo Emu\steam_settings\steam_appid.txt");
                // programmi
                file(@"Attrezzi\Foo 1.2\foo64.exe");
                file(@"Attrezzi\Tagliatore\ffmpeg.exe"); file(@"Attrezzi\Tagliatore\melt.exe"); file(@"Attrezzi\Tagliatore\ffprobe.exe"); file(@"Attrezzi\Tagliatore\tagliatore.exe");
                file(@"Attrezzi\Scalatore\Scalatore.exe"); file(@"Attrezzi\Scalatore\steam_api64.dll");
                // da non confondere
                file(@"Scaricati\Altro-RUNE\setup.exe"); file(@"Scaricati\Altro-RUNE\game.bin"); file(@"Scaricati\Altro-RUNE\RUNE\rune.ini");
                file(@"Scaricati\Foo2\Foo2-setup.exe");
                file(@"Scaricati\Addon\mod.vpk");
                file(@"Progetti\MioProgetto\MioProgetto.exe"); file(@"Progetti\MioProgetto\package.json");
                file(@"Progetti\MioGioco\.git\config"); file(@"Progetti\MioGioco\MioGioco.exe"); file(@"Progetti\MioGioco\UnityPlayer.dll");
                file(@"UE_5.3\Engine\Binaries\Win64\UnrealEditor.exe");
                var found = InstalledApps.ScanDisks(new[] { Tuple.Create(root, false) });
                Func<string, InstalledApps.FoundApp> at = rel => found.FirstOrDefault(f => PathUtil.Same(f.Dir, Path.Combine(root, rel)));
                Action<string, bool> game = (rel, expected) => { var f = at(rel); Check(f != null && f.IsGame == expected && !f.IsSingleFile, rel + (expected ? ": gioco" : ": programma")); };
                game(@"Roba\Elden.Ring-CODEX", true);
                game(@"Roba\Sub\Hollow Knight [FitGirl Repack]", true);
                game(@"Roba\Oscuro Motore v1.2-DODI", true);
                game(@"Giochi vecchi\Stalker", true);
                game(@"Giochi vecchi\Unrealone", true);
                game(@"Giochi vecchi\RpgVecchio", true);
                game(@"Giochi vecchi\Solo Emu", true);
                game(@"Attrezzi\Foo 1.2", false);
                game(@"Attrezzi\Tagliatore", false);
                game(@"Attrezzi\Scalatore", false);
                Check(at(@"Roba") == null && at(@"Giochi vecchi") == null && at("Attrezzi") == null, "una cartella che contiene applicazioni non è un'applicazione");
                Check(at(@"Scaricati\Altro-RUNE") == null, "un repack da installare non è un gioco");
                Check(at(@"Scaricati\Foo2") == null, "un programma di installazione non è un programma");
                Check(at(@"Scaricati\Addon") == null, "una cartella di mod senza eseguibile non è un gioco");
                Check(at("UE_5.3") == null, "l'editor di un motore non è un gioco");
                var proj = at(@"Progetti\MioProgetto");
                Check(proj != null && proj.IsSingleFile && !proj.IsGame, "progetto di sviluppo: solo l'eseguibile");
                var projGame = at(@"Progetti\MioGioco");
                Check(projGame != null && projGame.IsSingleFile && !projGame.IsGame, "progetto con un motore di gioco: solo l'eseguibile");
                Check(found.All(f => f.IsSingleFile || f.IsGame || Directory.Exists(f.Dir)), "cartelle esistenti");

                Check(NameUtil.CleanFolderTitle("Elden.Ring-CODEX") == "Elden Ring", "titolo: " + NameUtil.CleanFolderTitle("Elden.Ring-CODEX"));
                Check(NameUtil.CleanFolderTitle("Hollow Knight [FitGirl Repack]") == "Hollow Knight", "titolo: " + NameUtil.CleanFolderTitle("Hollow Knight [FitGirl Repack]"));
                Check(NameUtil.CleanFolderTitle("Oscuro Motore v1.2-DODI") == "Oscuro Motore", "titolo: " + NameUtil.CleanFolderTitle("Oscuro Motore v1.2-DODI"));
                Check(NameUtil.CleanFolderTitle("The Sims 4") == "The Sims 4", "il numero del seguito resta");

                Check(InstalledApps.IsAuxiliaryExe("gioco-setup.exe") && InstalledApps.IsAuxiliaryExe("Gioco Trainer.exe") && !InstalledApps.IsAuxiliaryExe("gioco.exe"), "eseguibili di supporto");
                Check(Path.GetFileName(InstalledApps.ExeNamedLikeFolder(Path.Combine(root, @"Attrezzi\Tagliatore"))) == "tagliatore.exe", "eseguibile principale tra quelli accessori");

                // salvataggi nella cartella del gioco
                file(@"Salvano\Saved\SaveGames\slot1.sav"); file(@"Salvano\saves\a.dat"); file(@"Salvano\profilo.sav");
                file(@"Salvano\Engine\Saves\x.dat"); file(@"Salvano\Save\tool.dll"); file(@"Salvano\Mappe\niente.dat");
                var saves = LocalSaves.Find(Path.Combine(root, "Salvano"));
                Check(saves.Any(s => s.EndsWith(@"Saved\SaveGames")) && saves.Any(s => s.EndsWith(@"Salvano\saves")) && saves.Any(s => s.EndsWith("profilo.sav")), "cartelle e file di salvataggio (" + saves.Count + ")");
                Check(!saves.Any(s => s.IndexOf(@"\Engine\", StringComparison.OrdinalIgnoreCase) >= 0) && !saves.Any(s => s.EndsWith(@"\Save")), "non le cartelle tecniche, né quelle con librerie");
                Check(LocalSaves.Find(null).Count == 0 && LocalSaves.Find(Path.Combine(root, "nessuna")).Count == 0, "cartella assente: nessun errore");
                string gdir = Path.Combine(root, "Salvano");
                Check(PathUtil.Same(SaveDatabase.ExpandInGame("{{p|game}}\\saves\\<user-id>\\", gdir), Path.Combine(gdir, "saves")), "database: cartella del gioco");
                Check(SaveDatabase.ExpandInGame("{{p|game}}\\..\\..\\Windows", gdir) == null && SaveDatabase.ExpandInGame("{{p|game}}\\", gdir) == null, "database: mai fuori dal gioco, mai tutta la cartella");

                // giochi aggiunti a mano a Steam
                var vdf = new List<byte>();
                Action<string> s0 = x => { vdf.AddRange(System.Text.Encoding.UTF8.GetBytes(x)); vdf.Add(0); };
                vdf.Add(0); s0("shortcuts"); vdf.Add(0); s0("0"); vdf.Add(2); s0("appid"); vdf.AddRange(new byte[] { 1, 2, 3, 4 });
                vdf.Add(1); s0("AppName"); s0("Gioco Crackato"); vdf.Add(1); s0("Exe"); s0("\"D:\\Giochi\\Gioco\\g.exe\""); vdf.Add(1); s0("StartDir"); s0("\"D:\\Giochi\\Gioco\\\"");
                vdf.Add(8); vdf.Add(0); s0("1"); vdf.Add(1); s0("appname"); s0("Città"); vdf.Add(1); s0("exe"); s0("C:\\c\\c.exe"); vdf.Add(8); vdf.Add(8);
                var sc = InstalledApps.ParseSteamShortcuts(vdf.ToArray());
                Check(sc.Count == 2 && sc[0].Key == "Gioco Crackato" && sc[0].Value == @"D:\Giochi\Gioco\g.exe" && sc[1].Key == "Città", "giochi aggiunti a Steam (" + sc.Count + ")");
                Check(InstalledApps.ParseSteamShortcuts(new byte[] { 1, 2, 3 }).Count == 0, "file di Steam non valido: nessun errore");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>Sorveglianza in background: solo logica e file nella cartella dati di prova. Nessuna attività pianificata viene creata.</summary>
        static void MonitorCases()
        {
            Console.WriteLine("Sorveglianza in background");
            Func<string, string, string, string, string, WatchedApp> app = (key, name, pub, ver, src) => new WatchedApp { Key = key, Name = name, Publisher = pub, Version = ver, Source = src };
            Func<WatchedApp[], Dictionary<string, WatchedApp>> map = list => list.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
            var before = map(new[] { app("reg:a", "Editor 1.0", "Ditta", "1.0", "registry"), app("reg:b", "Lettore", "Altra", "3", "registry"),
                                     app("steam:1", "Gioco", null, null, "steam"), app("reg:d", "Strumento", "Uno", "1", "registry") });
            var after = map(new[] { app("reg:a2", "Editor 2.0", "Ditta", "2.0", "registry"), app("steam:1", "Gioco", null, null, "steam"),
                                    app("reg:c", "Nuovo", "Terza", "1", "registry"), app("reg:d2", "Strumento", "Due", "1", "registry") });
            var d = WatchDiff.Compare(before, after);
            Check(d.Updated.Count == 1 && d.Updated[0].Item1.Key == "reg:a" && d.Updated[0].Item2.Key == "reg:a2", "voce cambiata per una nuova versione: aggiornamento, non disinstallazione");
            Check(d.Removed.Select(x => x.Key).OrderBy(x => x).SequenceEqual(new[] { "reg:b", "reg:d" }), "disinstallati: quelli spariti, anche con un omonimo di un altro editore (" + string.Join(",", d.Removed.Select(x => x.Key)) + ")");
            Check(d.Added.Select(x => x.Key).OrderBy(x => x).SequenceEqual(new[] { "reg:c", "reg:d2" }), "installati: quelli comparsi");
            Check(WatchDiff.Compare(after, after).IsEmpty, "nessun cambiamento: nessuna voce");
            Check(!WatchDiff.SameProduct(app("reg:x", "Gioco", null, null, "registry"), app("steam:9", "Gioco", null, null, "steam")), "stesso nome da fonti diverse: non è un aggiornamento");

            string xml = BackgroundStartup.BuildTaskXml(@"D:\Giochi & App\AppManager\AppManager.exe", @"PC\Mario <Rossi>", true);
            var doc = new System.Xml.XmlDocument();
            bool parsed = true;
            try { doc.LoadXml(xml.Substring(xml.IndexOf("<Task", StringComparison.Ordinal))); } catch { parsed = false; }
            var ns = new System.Xml.XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            Check(parsed && doc.SelectSingleNode("//t:Exec/t:Command", ns).InnerText == @"D:\Giochi & App\AppManager\AppManager.exe", "attività pianificata: percorso con caratteri speciali");
            Check(parsed && doc.SelectSingleNode("//t:Exec/t:Arguments", ns).InnerText == BackgroundStartup.Argument
                  && doc.SelectSingleNode("//t:RunLevel", ns).InnerText == "HighestAvailable" && doc.SelectSingleNode("//t:LogonTrigger/t:UserId", ns).InnerText == @"PC\Mario <Rossi>",
                  "attività pianificata: all'accesso dell'utente, con i privilegi più elevati");
            Check(parsed && doc.SelectSingleNode("//t:ExecutionTimeLimit", ns).InnerText == "PT0S" && doc.SelectSingleNode("//t:DisallowStartIfOnBatteries", ns).InnerText == "false",
                  "attività pianificata: non si ferma dopo 3 giorni né con la batteria");

            var item = new LeftoverItem { Kind = ItemKind.RegistryValue, Category = ItemCategory.Registry, Path = @"HKCU\Software\X", ValueName = "v", Reason = "r", Confidence = Confidence.Medium, Size = 5 };
            var back = StoredItem.From(item).ToItem();
            Check(back != null && back.Key == item.Key && back.Confidence == Confidence.Medium && back.Reason == "r", "residuo salvato e riletto uguale");
            Check(new StoredItem { Kind = "Sconosciuto", Category = "Registry" }.ToItem() == null, "residuo con un tipo sconosciuto: scartato");

            string dir = Path.Combine(Path.GetTempPath(), "AppManagerSorveglianza_" + Guid.NewGuid().ToString("N"));
            const string regKey = @"Software\AppManagerProvaSorveglianza";
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "cartella"));
                File.WriteAllText(Path.Combine(dir, "file.txt"), "x");
                using (var k = Registry.CurrentUser.CreateSubKey(regKey)) k.SetValue("valore", "1");
                var items = new List<LeftoverItem>
                {
                    new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.Settings, Path = Path.Combine(dir, "cartella"), Confidence = Confidence.High },
                    new LeftoverItem { Kind = ItemKind.File, Category = ItemCategory.Cache, Path = Path.Combine(dir, "file.txt"), Confidence = Confidence.Low },
                    new LeftoverItem { Kind = ItemKind.RegistryKey, Category = ItemCategory.Registry, Path = @"HKCU\" + regKey, Confidence = Confidence.High },
                    new LeftoverItem { Kind = ItemKind.RegistryValue, Category = ItemCategory.Registry, Path = @"HKCU\" + regKey, ValueName = "valore", Confidence = Confidence.Medium },
                };
                Check(items.All(LeftoverCheck.Exists), "residui presenti: riconosciuti");
                Check(!LeftoverCheck.Exists(new LeftoverItem { Kind = ItemKind.RegistryValue, Path = @"HKCU\" + regKey, ValueName = "manca" }), "valore mancante: riconosciuto");

                MonitorHistory.Clear();
                var g = new AppEntry { Id = "reg:prova", Kind = AppKind.Program, Name = "Programma di prova", Publisher = "Ditta" };
                string id = MonitorHistory.RecordUninstallByApp(g, items);
                var ev = MonitorHistory.Get(id);
                Check(ev != null && ev.Status == HistoryEvent.Leftovers && ev.ItemCount == 4 && ev.HasRegistryItems && ev.WorthNotifying, "disinstallazione annotata con i residui");
                MonitorHistory.UseInMemory(null); // si rilegge dal file
                ev = MonitorHistory.Get(id);
                Check(ev != null && ev.ItemCount == 4 && ev.Items[3].ValueName == "valore" && ev.ByAppManager, "cronologia salvata e riletta");

                File.Delete(Path.Combine(dir, "file.txt"));
                Registry.CurrentUser.DeleteSubKeyTree(regKey);
                var left = MonitorHistory.Recheck(id);
                Check(left.Count == 1 && MonitorHistory.Get(id).ItemCount == 1 && MonitorHistory.Get(id).Status == HistoryEvent.Leftovers, "ricontrollo: restano solo i residui ancora presenti");
                Directory.Delete(Path.Combine(dir, "cartella"));
                MonitorHistory.Recheck(id);
                Check(MonitorHistory.Get(id).Status == HistoryEvent.Cleaned && MonitorHistory.Get(id).ItemCount == 0, "tutto eliminato: «I residui sono stati eliminati»");

                string clean = MonitorHistory.RecordUninstallByApp(g, new List<LeftoverItem>());
                Check(MonitorHistory.Get(clean).Status == HistoryEvent.Clean && !MonitorHistory.Get(clean).WorthNotifying, "nessun residuo: nessun avviso");
                string low = MonitorHistory.RecordUninstallByApp(g, new[] { new LeftoverItem { Kind = ItemKind.File, Category = ItemCategory.Other, Path = @"C:\x", Confidence = Confidence.Low } });
                Check(!MonitorHistory.Get(low).WorthNotifying, "solo residui «Da verificare»: annotati, senza avviso");
                for (int i = 0; i < MonitorHistory.MaxEvents + 5; i++) MonitorHistory.Add(HistoryEvent.For(app("reg:n" + i, "N" + i, null, null, "registry"), HistoryEvent.Installed));
                Check(MonitorHistory.All().Count == MonitorHistory.MaxEvents, "cronologia limitata alle voci più recenti");
                MonitorHistory.Clear();
                Check(MonitorHistory.All().Count == 0, "cronologia svuotata");

                var snap = map(new[] { app("reg:z", "Zeta", "Z", "1", "registry") });
                WatchSnapshot.Save(snap);
                var read = WatchSnapshot.Load();
                Check(read != null && read.Count == 1 && read["reg:z"].Name == "Zeta", "istantanea salvata e riletta");
                WatchSnapshot.Delete();
                Check(WatchSnapshot.Load() == null, "istantanea tolta");
                var real = WatchSnapshot.Take(new List<AppEntry>());
                Check(real.Count > 0 && real.Values.All(w => !string.IsNullOrEmpty(w.Name)), "istantanea di questo PC: " + real.Count + " applicazioni (sola lettura)");
            }
            finally
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(regKey, false); } catch { }
                try { Directory.Delete(dir, true); } catch { }
                MonitorHistory.Clear();
            }
        }

        private static void Check(bool ok, string what)
        {
            Console.WriteLine("  {0}  {1}", ok ? "OK  " : "ERRORE", what);
            if (!ok) failures++;
        }

        private static AppSettings Open(string pc)
        {
            ThisPc.IdOverride = pc;
            ThisPc.ResetForTests();
            AppSettings.ResetForTests();
            return AppSettings.Current;
        }

        private static AppEntry Game(string name)
        {
            return new AppEntry { Id = "t:" + name, Kind = AppKind.Game, Name = name };
        }

        private static void Touch(string file, int kb)
        {
            File.WriteAllBytes(file, new byte[kb * 1024]);
        }

        public static int Run(string expectedDataDir)
        {
            if (!PathUtil.Same(AppPaths.DataDir, Path.GetFullPath(expectedDataDir)))
            {
                Console.WriteLine("Imposta APPMANAGER_DATA={0} prima di eseguire la prova (cartella dati attuale: {1})", expectedDataDir, AppPaths.DataDir);
                return 1;
            }
            foreach (var f in Directory.GetFiles(AppPaths.DataDir)) File.Delete(f);
            foreach (var d in Directory.GetDirectories(AppPaths.DataDir)) Directory.Delete(d, true);
            Pcs();
            Covers();
            Leftovers();
            Legacy();
            Startup();
            Export();
            Languages();
            Console.WriteLine(failures == 0 ? "Tutte le prove superate." : failures + " prove non superate.");
            return failures == 0 ? 0 : 1;
        }

        private static void Pcs()
        {
            Console.WriteLine("Da quanto è installato Windows");
            Check(ThisPc.Age(new DateTime(2023, 3, 5), new DateTime(2026, 5, 10)) == "3 anni, 2 mesi", "anni e mesi");
            Check(ThisPc.Age(new DateTime(2025, 10, 31), new DateTime(2026, 10, 31)) == "1 anno", "un anno esatto");
            Check(ThisPc.Age(new DateTime(2026, 4, 18), new DateTime(2026, 10, 31)) == "6 mesi", "solo mesi");
            Check(ThisPc.Age(new DateTime(2026, 4, 30), new DateTime(2026, 5, 29)) == "29 giorni", "mese non ancora compiuto");
            Check(ThisPc.Age(new DateTime(2026, 10, 30), new DateTime(2026, 10, 31)) == "1 giorno", "un giorno");
            Check(ThisPc.Age(new DateTime(2026, 10, 31, 8, 0, 0), new DateTime(2026, 10, 31, 9, 0, 0)) == "meno di un giorno", "stesso giorno");

            Console.WriteLine("Tempo di accensione");
            Check(ThisPc.Uptime(TimeSpan.FromSeconds(20)) == "meno di un minuto", "meno di un minuto");
            Check(ThisPc.Uptime(TimeSpan.FromMinutes(1)) == "1 minuto" && ThisPc.Uptime(TimeSpan.FromMinutes(45)) == "45 minuti", "minuti");
            Check(ThisPc.Uptime(new TimeSpan(1, 16, 30)) == "1 ora, 16 minuti" && ThisPc.Uptime(TimeSpan.FromHours(2)) == "2 ore", "ore");
            Check(ThisPc.Uptime(new TimeSpan(3, 4, 59, 0)) == "3 giorni, 4 ore" && ThisPc.Uptime(TimeSpan.FromDays(1)) == "1 giorno", "giorni");
            var on = ThisPc.LastPowerOn();
            Check(on <= DateTime.Now && on > DateTime.Now.AddYears(-1), "ultima accensione letta: " + on.ToString("yyyy-MM-dd HH:mm"));

            Console.WriteLine("Cartelle dei salvataggi da PCGamingWiki");
            var cells = new List<string>();
            SaveDatabase.Extract("{{Game data/saves|Windows|{{p|userprofile\\Documents}}\\Editore\\Gioco\\<user-id>\\<ref>nota</ref><br>{{p|appdata}}\\Gioco\\}}\n{{Game data/saves|Linux|{{p|xdg}}/x}}\n{{Game data/saves|Windows|}}", "saves", cells);
            Check(cells.Count == 2 && cells[0].Contains("<user-id>") && !cells[0].Contains("nota"), "celle lette, senza note né altri sistemi (" + cells.Count + ")");
            var ids = SaveDatabase.ExtractAppIds("{{Infobox game\n|steam appid  = 271590\n|steam appid side = 12120, 12210\n|gogcom id = 1\n}}");
            Check(ids.Count == 3 && ids[0] == "271590" && ids[2] == "12210", "AppID di Steam del titolo (" + string.Join(",", ids) + ")");
            string fake = Path.Combine(KnownDirs.Documents, "AppManagerProvaPercorso");
            Check(SaveDatabase.Expand("{{p|userprofile\\Documents}}\\AppManagerProvaPercorso\\<user-id>\\") == null, "cartella inesistente: scartata");
            Directory.CreateDirectory(fake);
            try
            {
                Check(PathUtil.Same(SaveDatabase.Expand("{{p|userprofile\\Documents}}\\AppManagerProvaPercorso\\<user-id>\\"), fake), "si ferma prima della parte variabile");
                Check(PathUtil.Same(SaveDatabase.Expand("{{p|userprofile\\Documents}}\\AppManagerProvaPercorso\\*.sav"), fake), "si ferma prima dei caratteri jolly");
            }
            finally { Directory.Delete(fake); }
            Check(SaveDatabase.Expand("{{p|userprofile\\Documents}}\\") == null && SaveDatabase.Expand("{{p|userprofile\\Documents}}\\<user-id>") == null, "mai Documenti intera");
            Check(SaveDatabase.Expand("{{p|game}}\\Saves") == null && SaveDatabase.Expand("{{p|hkcu}}\\Software\\X") == null, "niente cartella del gioco né registro");
            Check(SaveDatabase.Expand("{{p|appdata}}\\..\\..\\Windows") == null && SaveDatabase.Expand("{{p|appdata}}\\C:\\Windows") == null, "percorsi che escono dalla radice: scartati");
            Check(SaveDatabase.Expand("C:\\Windows\\System32") == null, "percorsi assoluti: scartati");

            DetectionCases();
            MonitorCases();

            Console.WriteLine("Riconoscimento del PC");
            var s = Open("pcA");
            Check(ThisPc.IsNew, "primo avvio assoluto: PC nuovo");
            Check(AppPaths.IsPortable, "sempre portable, senza nessun file che lo attivi");
            s.LibraryFolders.Add(@"D:\GiochiDiA");
            s.HiddenGames.Add(new SavedEntry { Name = "x", Path = "x" });
            s.Theme = "dark";
            s.Save();

            s = Open("pcA");
            Check(!ThisPc.IsNew, "stesso PC: riconosciuto");
            Check(s.LibraryFolders.Contains(@"D:\GiochiDiA"), "ritrova le sue cartelle");

            s = Open("pcB");
            Check(ThisPc.IsNew, "altro PC: nuovo");
            Check(s.LibraryFolders.Count == 0 && s.HiddenGames.Count == 0, "parte pulito, senza i percorsi dell'altro PC");
            Check(s.Theme == "dark", "le preferenze generali (tema) restano");
            s.ProgramFolders.Add(@"E:\ProgrammiDiB");
            s.Save();
            Check(ThisPc.Others().Any(p => p.Id == "pcA"), "conosce l'altro PC");

            s = Open("pcA");
            Check(s.LibraryFolders.Contains(@"D:\GiochiDiA") && s.ProgramFolders.Count == 0, "tornando sul primo PC ritrova i suoi valori, non quelli dell'altro");

            // impostazioni di una versione precedente, senza profili: appartengono al PC su cui si trovano
            string pcDir = Path.Combine(AppPaths.DataDir, "pc");
            Directory.Delete(pcDir, true);
            var legacy = Json.Parse(File.ReadAllText(AppPaths.SettingsFile)) as Dictionary<string, object>;
            legacy.Remove("LastPcId");
            File.WriteAllText(AppPaths.SettingsFile, Json.Serialize(legacy));
            s = Open("pcC");
            Check(!ThisPc.IsNew && s.LibraryFolders.Contains(@"D:\GiochiDiA"), "impostazioni di una versione precedente: non si perdono e il PC non risulta nuovo");

            // cartella dei backup su un disco che non c'è più: si torna a quella predefinita
            s.BackupFolder = @"Q:\NonEsiste\Sotto\Backup";
            s.Save();
            s = Open("pcC");
            Check(PathUtil.Same(s.BackupFolder, AppPaths.DefaultBackupDir), "cartella dei backup non raggiungibile: quella predefinita");
        }

        private static void SetIndex(params string[] keysAndFiles)
        {
            string dir = Path.Combine(AppPaths.DataDir, "covers");
            CoverService.ClearCache();
            Directory.CreateDirectory(dir);
            var index = new Dictionary<string, object>();
            string now = DateTime.Now.ToString("o");
            for (int i = 0; i < keysAndFiles.Length; i += 2)
            {
                index[keysAndFiles[i]] = new Dictionary<string, object> { { "V", 2 }, { "When", now }, { "Used", now }, { "Portrait", keysAndFiles[i + 1] } };
                Touch(Path.Combine(dir, keysAndFiles[i + 1]), 1024);
            }
            File.WriteAllText(Path.Combine(dir, "index.json"), Json.Serialize(index));
        }

        private static bool Cover(string name)
        {
            return File.Exists(Path.Combine(AppPaths.DataDir, "covers", name));
        }

        private static void AgeProfile(string pc, int days)
        {
            string file = Path.Combine(AppPaths.DataDir, "pc", pc + ".json");
            var p = Json.Deserialize<PcProfile>(File.ReadAllText(file));
            p.LastSeen = DateTime.Now.AddDays(-days).ToString("o");
            File.WriteAllText(file, Json.Serialize(p));
        }

        private static void Covers()
        {
            Console.WriteLine("Immagini scaricate su più PC");
            var s = Open("pcA");
            s.CoverBudgetMb = 0;
            s.Save();
            s = Open("pcB");
            CoverService.ClearCache();
            CoverService.Prune(new[] { Game("Beta") });
            s = Open("pcA");
            SetIndex("g:alfa", "alfa.jpg", "g:beta", "beta.jpg", "g:gamma", "gamma.jpg");
            CoverService.Prune(new[] { Game("Alfa") });
            s = Open("pcB");
            CoverService.Prune(new[] { Game("Beta") });
            Check(Cover("alfa.jpg") && Cover("beta.jpg"), "cambiare PC non cancella le immagini dei giochi dell'altro PC");
            Check(!Cover("gamma.jpg"), "le immagini di un gioco che non è su nessun PC vengono tolte");

            Console.WriteLine("Limite di spazio");
            s = Open("pcB");
            s.CoverBudgetMb = 1;
            SetIndex("g:alfa", "alfa.jpg", "g:beta", "beta.jpg");
            CoverService.Prune(new[] { Game("Beta") });
            Check(!Cover("alfa.jpg"), "oltre il limite si tolgono prima quelle degli altri PC");
            Check(Cover("beta.jpg"), "mai quelle dei giochi di questo PC");
            s.CoverBudgetMb = 0;
            s.Save();
            Check(PcResources.CoverBudget(0) >= 50L * 1024 * 1024 && PcResources.CoverBudget(0) <= 1024L * 1024 * 1024, "limite automatico tra 50 MB e 1 GB");

            Console.WriteLine("Giochi spariti e PC dimenticati");
            s = Open("pcA");
            SetIndex("g:alfa", "alfa.jpg", "g:beta", "beta.jpg");
            CoverService.Prune(new[] { Game("Alfa") });
            s = Open("pcB");
            CoverService.Prune(new[] { Game("Beta") });
            CoverService.Prune(new AppEntry[0]);
            Check(Cover("beta.jpg"), "un gioco appena sparito (disco staccato) conserva le immagini per qualche giorno");
            AgeProfile("pcA", ThisPc.ForgetAfterDays + 5);
            Check(ThisPc.ForgetExpired() == 1, "PC non visto da oltre 6 mesi: dimenticato");
            CoverService.Prune(new[] { Game("Beta") });
            Check(!Cover("alfa.jpg"), "le immagini che servivano solo a lui vengono tolte");
            ThisPc.Forget(ThisPc.Id);
            Check(File.Exists(Path.Combine(AppPaths.DataDir, "pc", ThisPc.Id + ".json")), "il PC in uso non si può dimenticare");
        }

        private static void Leftovers()
        {
            Console.WriteLine("File rimasti e cartelle vuote");
            string root = Path.Combine(AppPaths.DataDir, "prova-residui");
            Directory.CreateDirectory(root);
            string oldTemp = Path.Combine(root, "am_task_vecchio.xml"), newTemp = Path.Combine(root, "am_restore_nuovo.reg"), other = Path.Combine(root, "altrui.xml");
            foreach (var f in new[] { oldTemp, newTemp, other }) File.WriteAllText(f, "x");
            File.SetLastWriteTime(oldTemp, DateTime.Now.AddDays(-3));
            File.SetLastWriteTime(other, DateTime.Now.AddDays(-3));
            Check(PcResources.CleanTempLeftovers(root) == 1 && !File.Exists(oldTemp), "tolto il temporaneo vecchio");
            Check(File.Exists(newTemp), "uno recente può essere in uso: resta");
            Check(File.Exists(other), "i file di altri programmi non si toccano");

            string tree = Path.Combine(root, "albero");
            Directory.CreateDirectory(Path.Combine(tree, "a", "b"));
            Check(PcResources.RemoveIfEmpty(tree) && !Directory.Exists(tree), "cartella con sole sottocartelle vuote: tolta");
            Directory.CreateDirectory(Path.Combine(tree, "a"));
            Directory.CreateDirectory(Path.Combine(tree, "c"));
            File.WriteAllText(Path.Combine(tree, "a", "tieni.txt"), "x");
            Check(!PcResources.RemoveIfEmpty(tree) && File.Exists(Path.Combine(tree, "a", "tieni.txt")), "cartella con un file: resta");
            Check(!Directory.Exists(Path.Combine(tree, "c")), "ma le sottocartelle vuote se ne vanno");
            Directory.Delete(root, true);
        }

        private static void Legacy()
        {
            Console.WriteLine("Dati delle versioni non portable");
            string legacy = Path.Combine(AppPaths.DataDir, "vecchia");
            string covers = Path.Combine(legacy, "covers");
            Directory.CreateDirectory(covers);
            var old = new AppSettings { SteamGridDbKey = "chiave-vecchia" };
            old.HiddenPrograms.Add(new SavedEntry { Name = "Nascosto", Path = "id-nascosto" });
            File.WriteAllText(Path.Combine(legacy, "settings.json"), Json.Serialize(old));
            File.WriteAllText(Path.Combine(legacy, "log.txt"), "x");
            Touch(Path.Combine(covers, "vecchia.jpg"), 1);
            File.WriteAllText(Path.Combine(covers, "index.json"), Json.Serialize(new Dictionary<string, object> { { "g:vecchio", new Dictionary<string, object> { { "V", 2 }, { "Portrait", "vecchia.jpg" } } } }));
            var s = Open("pcD");
            s.SteamGridDbKey = null;
            s.Save();
            PcResources.AdoptLegacyData(legacy);
            CoverService.ClearCache();
            Check(!Directory.Exists(legacy), "la vecchia cartella non resta sul PC");
            var merged = Json.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile));
            Check(merged.SteamGridDbKey == "chiave-vecchia", "la chiave di SteamGridDB viene conservata");
            Check(merged.HiddenPrograms.Any(h => h.Path == "id-nascosto"), "gli elementi nascosti vengono conservati");

            string foreign = Path.Combine(AppPaths.DataDir, "vecchia2");
            Directory.CreateDirectory(foreign);
            File.WriteAllText(Path.Combine(foreign, "log.txt"), "x");
            File.WriteAllText(Path.Combine(foreign, "documento-altrui.txt"), "x");
            PcResources.AdoptLegacyData(foreign);
            Check(File.Exists(Path.Combine(foreign, "documento-altrui.txt")) && !File.Exists(Path.Combine(foreign, "log.txt")), "un file non suo resta dov'è");
            Directory.Delete(foreign, true);
        }

        private static void Startup()
        {
            Console.WriteLine("Avvio automatico");
            Check(StartupManager.IsEnabledValue(null) && StartupManager.IsEnabledValue(new byte[] { 2, 0, 0, 0 }), "nessuna voce o 0x02: attivo");
            Check(!StartupManager.IsEnabledValue(new byte[] { 3, 0, 0, 0 }) && !StartupManager.IsEnabledValue(new byte[] { 7, 0, 0, 0 }), "0x03 e 0x07: disattivato");
            var off = StartupManager.BuildValue(false, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Check(off.Length == 12 && off[0] == 3 && BitConverter.ToInt64(off, 4) != 0, "valore disattivato: 12 byte con la data, come Gestione attività");
            Check(StartupManager.BuildValue(true, DateTime.Now).Skip(1).All(b => b == 0), "valore attivo: senza data");

            const string root = @"HKCU\Software\AppManagerProva";
            StartupManager.ApprovedRootOverride = root;
            try
            {
                var e = new StartupEntry { Name = "Prova", StateName = "Prova", ApprovedKey = root + @"\HKCU\Run", IsEnabled = true };
                StartupManager.SetEnabled(e, false);
                using (var k = RegUtil.Open(e.ApprovedKey)) Check(!StartupManager.IsEnabledValue(k.GetValue("Prova") as byte[]) && !e.IsEnabled, "disattivare scrive lo stato");
                StartupManager.SetEnabled(e, true);
                using (var k = RegUtil.Open(e.ApprovedKey)) Check(StartupManager.IsEnabledValue(k.GetValue("Prova") as byte[]) && e.IsEnabled, "riattivare torna come prima");
            }
            finally
            {
                StartupManager.ApprovedRootOverride = null;
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\AppManagerProva", false);
            }
            Check(StartupManager.List() != null, "elenco reale leggibile (sola lettura)");
        }

        private static void Export()
        {
            Console.WriteLine("Esportazione dell'elenco");
            Check(AppExport.Cell("semplice") == "semplice", "testo semplice");
            Check(AppExport.Cell("a;b") == "\"a;b\"", "separatore tra virgolette");
            Check(AppExport.Cell("di \"lui\"") == "\"di \"\"lui\"\"\"", "virgolette raddoppiate");
            Check(AppExport.Cell("=CMD()") == "'=CMD()" && AppExport.Cell("+1") == "'+1" && AppExport.Cell("-1") == "'-1" && AppExport.Cell("@x") == "'@x", "il nome di un programma non può diventare una formula");
            Check(AppExport.Cell(null) == "" && AppExport.Cell("a\r\nb") == "a  b", "valori vuoti e a capo");
            var csv = AppExport.ToCsv(new[] { new AppEntry { Kind = AppKind.Program, Name = "Zeta" }, new AppEntry { Kind = AppKind.Game, Name = "Alfa", SizeBytes = 1048576 } });
            Check(csv.Split('\n').Length == 4, "intestazione e una riga per elemento");
        }

        private static void Languages()
        {
            Console.WriteLine("Lingue");
            var keys = Catalog("it");
            Check(keys.Count > 0, "catalogo italiano incorporato (" + keys.Count + " testi)");
            foreach (var lang in L.Supported.Where(c => c != "it"))
            {
                var values = Catalog(lang);
                // le voci tecniche del registro attività (dal numero 5000) esistono solo in inglese
                var missing = keys.Keys.Where(k => !values.ContainsKey(k) && (k < 5000 || lang == "en")).ToList();
                var extra = values.Keys.Where(k => !keys.ContainsKey(k)).ToList();
                Check(missing.Count == 0 && extra.Count == 0, lang + ": tutti i testi tradotti" + (missing.Count > 0 ? " (mancano: " + string.Join(", ", missing.Take(8)) + ")" : "") + (extra.Count > 0 ? " (in più: " + string.Join(", ", extra.Take(8)) + ")" : ""));
                var wrong = keys.Where(k => values.ContainsKey(k.Key) && !Same(Placeholders(k.Value), Placeholders(values[k.Key]))).Select(k => k.Key).ToList();
                Check(wrong.Count == 0, lang + ": stessi segnaposto {0} in ogni frase" + (wrong.Count > 0 ? " (diversi: " + string.Join(", ", wrong.Take(8)) + ")" : ""));
                var spaces = keys.Where(k => values.ContainsKey(k.Key) && (k.Value.StartsWith(" ") != values[k.Key].StartsWith(" ") || k.Value.EndsWith(" ") != values[k.Key].EndsWith(" ") || Count(k.Value, "\\n") != Count(values[k.Key], "\\n"))).Select(k => k.Key).ToList();
                Check(spaces.Count == 0, lang + ": stessi spazi iniziali e finali e stessi a capo" + (spaces.Count > 0 ? " (diversi: " + string.Join(", ", spaces.Take(8)) + ")" : ""));
                var plural = keys.Where(k => k.Value.Contains("|") && !k.Value.Contains("*") && values.ContainsKey(k.Key) && values[k.Key].Split('|').Length < (lang == "ru" || lang == "uk" ? 3 : 2)).Select(k => k.Key).ToList();
                Check(plural.Count == 0, lang + ": forme del plurale" + (plural.Count > 0 ? " (mancano: " + string.Join(", ", plural.Take(8)) + ")" : ""));
                var empty = values.Where(v => string.IsNullOrWhiteSpace(v.Value)).Select(v => v.Key).ToList();
                Check(empty.Count == 0, lang + ": nessun testo vuoto");
            }

            L.Use("ru");
            Check(L.Plural(1, "file", "file") == "файл" && L.Plural(3, "file", "file") == "файла" && L.Plural(5, "file", "file") == "файлов" && L.Plural(11, "file", "file") == "файлов" && L.Plural(21, "file", "file") == "файл", "russo: 1 файл, 3 файла, 5 файлов, 11 файлов, 21 файл");
            L.Use("en");
            Check(L.Plural(1, "file", "file") == "file" && L.Plural(2, "file", "file") == "files", "inglese: 1 file, 2 files");
            string xaml = L.TranslateXaml("<TextBlock Text=\"Impostazioni\" Style=\"{StaticResource H1}\" ToolTip=\"{Binding X}\" Tag=\"&#xE74C;\"/>");
            Check(xaml.Contains("Text=\"Settings\"") && xaml.Contains("{Binding X}") && xaml.Contains("&#xE74C;"), "XAML: tradotti i testi fissi, non i legami né le icone");
            L.Use("it");
            Check(L.T("Impostazioni") == "Impostazioni" && L.Plural(2, "file", "file") == "file", "italiano: il testo resta quello del codice");
            Check(L.Supported.Contains(L.Resolve("auto")), "lingua automatica sempre tra quelle disponibili");
            Check(L.Resolve("de") == "de", "lingua scelta rispettata");
        }

        private static int Count(string s, string what)
        {
            return s.Split(new[] { what }, StringSplitOptions.None).Length - 1;
        }

        private static bool Same(List<string> a, List<string> b)
        {
            return a.OrderBy(x => x).SequenceEqual(b.OrderBy(x => x));
        }

        private static List<string> Placeholders(string s)
        {
            return System.Text.RegularExpressions.Regex.Matches(s, @"\{\d+\}").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).Distinct().ToList();
        }

        private static Dictionary<int, string> Catalog(string lang)
        {
            var map = new Dictionary<int, string>();
            var stream = typeof(L).Assembly.GetManifestResourceStream("AppManager.ui.lang." + lang + ".txt");
            if (stream == null) return map;
            using (stream)
            using (var r = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    int tab = line.IndexOf('\t');
                    int n;
                    if (tab > 0 && int.TryParse(line.Substring(0, tab), out n)) map[n] = line.Substring(tab + 1);
                }
            }
            return map;
        }
    }
}
