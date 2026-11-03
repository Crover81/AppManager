using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AppManager
{
    /// <summary>
    /// Prove del deposito dei salvataggi su dati finti creati (e poi eliminati) in una cartella di prova:
    /// backup, nuova partita, cambio di profilo avanti e indietro, ripristino, annullamento in caso di errore.
    /// </summary>
    internal static class SaveTests
    {
        private static int failures;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine("  {0}  {1}", ok ? "OK  " : "ERRORE", what);
            if (!ok) failures++;
        }

        private static string Read(string f) { return File.Exists(f) ? File.ReadAllText(f) : null; }

        public static int Run(string sandboxRoot)
        {
            string sandbox = Path.Combine(Path.GetFullPath(sandboxRoot), "savetest");
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            string game = Path.Combine(sandbox, "Gioco"), saves = Path.Combine(sandbox, "Salvataggi", "GiocoDiProva"), cfg = Path.Combine(sandbox, "Config", "gioco.ini");
            Directory.CreateDirectory(game);
            Directory.CreateDirectory(Path.Combine(saves, "sub"));
            Directory.CreateDirectory(Path.GetDirectoryName(cfg));
            File.WriteAllText(Path.Combine(game, "gioco.exe"), "exe");
            File.WriteAllText(Path.Combine(saves, "slot1.sav"), "A1");
            File.WriteAllText(Path.Combine(saves, "sub", "slot2.sav"), "A2");
            File.WriteAllText(cfg, "cfgA");

            AppSettings.SaveDisabled = true;
            // i file tolti dalle prove non devono finire nel Cestino dell'utente
            SaveVault.UseRecycleBin = false;
            var s = AppSettings.Current;
            s.SaveVaultFolder = Path.Combine(sandbox, "Deposito");
            s.BackupFolder = Path.Combine(sandbox, "Backup");
            s.BackupsToKeep = 0;
            Safety.Invalidate();
            var g = new AppEntry { Id = "test:salvataggi", Kind = AppKind.Game, Name = "Gioco di prova salvataggi", InstallDir = game };
            try
            {
                Console.WriteLine("Posizioni");
                SaveVault.AddManualLocation(g, saves);
                SaveVault.AddManualLocation(g, cfg);
                var locs = SaveVault.FindLocations(g, new List<AppEntry>(), false, null, CancellationToken.None);
                Check(locs.Count == 2, "2 posizioni gestite (trovate: " + locs.Count + ")");
                Check(SaveVault.CheckUsable(KnownDirs.Documents) != null, "Documenti rifiutata come cartella dei salvataggi");
                Check(SaveVault.CheckUsable(s.SaveVaultFolder) != null, "il deposito stesso rifiutato");

                Console.WriteLine("Backup");
                string zip = SaveVault.Backup(g, locs, SaveVault.SavesReason, null, CancellationToken.None);
                Check(File.Exists(zip), "backup creato: " + zip);
                var info = Cleaner.ListBackups(s.BackupFolder).FirstOrDefault(b => b.ZipPath == zip);
                Check(info != null && info.AppName == g.Name && info.AppKind == AppKind.Game && info.Reason == SaveVault.SavesReason, "backup riconosciuto per applicazione e motivo");
                Check(PathUtil.Same(Path.GetDirectoryName(zip), Path.Combine(s.BackupFolder, g.Name)), "backup nella cartella dell'applicazione");

                Console.WriteLine("Nuova partita");
                var st = SaveVault.Load(g);
                SaveVault.Switch(g, st, locs, null, "Luigi", null);
                st = SaveVault.Load(g);
                Check(st.Profiles.Count == 2 && st.Active != null && st.Active.Name == "Luigi", "profilo «Luigi» in uso, 2 profili");
                Check(Directory.Exists(saves) && !Directory.EnumerateFileSystemEntries(saves).Any(), "cartella dei salvataggi vuota ma presente");
                Check(!File.Exists(cfg), "file di configurazione messo da parte");
                var main = st.Profiles.First(p => p.Name == "Principale");
                Check(main.Stored.Count == 2, "profilo «Principale» con 2 posizioni nel deposito");

                File.WriteAllText(Path.Combine(saves, "luigi.sav"), "L1");
                File.WriteAllText(cfg, "cfgL");

                Console.WriteLine("Ritorno al profilo precedente");
                SaveVault.Switch(g, st, locs, main.Id, null, null);
                Check(Read(Path.Combine(saves, "slot1.sav")) == "A1" && Read(Path.Combine(saves, "sub", "slot2.sav")) == "A2", "salvataggi di «Principale» tornati");
                Check(!File.Exists(Path.Combine(saves, "luigi.sav")), "salvataggio di Luigi non mescolato");
                Check(Read(cfg) == "cfgA", "configurazione di «Principale» tornata");

                Console.WriteLine("Di nuovo Luigi");
                st = SaveVault.Load(g);
                var luigi = st.Profiles.First(p => p.Name == "Luigi");
                SaveVault.Switch(g, st, locs, luigi.Id, null, null);
                Check(Read(Path.Combine(saves, "luigi.sav")) == "L1" && !File.Exists(Path.Combine(saves, "slot1.sav")) && Read(cfg) == "cfgL", "file di Luigi tornati");
                st = SaveVault.Load(g);
                SaveVault.Switch(g, st, locs, st.Profiles.First(p => p.Name == "Principale").Id, null, null);
                Check(Read(Path.Combine(saves, "slot1.sav")) == "A1", "di nuovo «Principale»");

                Console.WriteLine("Annullamento in caso di errore");
                st = SaveVault.Load(g);
                luigi = st.Profiles.First(p => p.Name == "Luigi");
                bool failed = false;
                using (new FileStream(Path.Combine(saves, "sub", "slot2.sav"), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    try { SaveVault.Switch(g, st, locs, luigi.Id, null, null); }
                    catch (InvalidOperationException ex) { failed = true; Console.WriteLine("    (errore atteso: " + ex.Message + ")"); }
                }
                Check(failed, "cambio di profilo rifiutato con un file in uso");
                st = SaveVault.Load(g);
                Check(st.Active.Name == "Principale", "profilo in uso invariato");
                Check(Read(Path.Combine(saves, "slot1.sav")) == "A1" && Read(Path.Combine(saves, "sub", "slot2.sav")) == "A2" && Read(cfg) == "cfgA", "file tornati al loro posto");
                string mainDir = Path.Combine(SaveVault.GameDir(g), "profiles", st.Active.Id);
                Check(!Directory.Exists(mainDir) || !Directory.EnumerateFiles(mainDir, "*", SearchOption.AllDirectories).Any(), "nessun file rimasto nel deposito del profilo in uso");
                SaveVault.Switch(g, st, locs, luigi.Id, null, null);
                Check(Read(Path.Combine(saves, "luigi.sav")) == "L1", "dopo l'errore il cambio riesce");
                st = SaveVault.Load(g);
                SaveVault.Switch(g, st, locs, st.Profiles.First(p => p.Name == "Principale").Id, null, null);

                Console.WriteLine("Ripristino di un backup");
                File.WriteAllText(Path.Combine(saves, "slot1.sav"), "MODIFICATO");
                File.WriteAllText(Path.Combine(saves, "nuovo.sav"), "nuovo");
                var r = SaveVault.RestoreBackup(g, zip, null, null);
                Check(r.Errors.Count == 0, "ripristino senza errori" + (r.Errors.Count > 0 ? ": " + r.Errors[0] : ""));
                Check(Read(Path.Combine(saves, "slot1.sav")) == "A1" && Read(Path.Combine(saves, "sub", "slot2.sav")) == "A2", "salvataggi del backup rimessi");
                Check(!File.Exists(Path.Combine(saves, "nuovo.sav")), "file più recenti non mescolati");
                Check(Cleaner.ListBackups(s.BackupFolder).Any(b => b.Reason == SaveVault.BeforeRestoreReason && b.AppName == g.Name), "backup automatico «Prima del ripristino» creato");

                Console.WriteLine("Ripristino con la conservazione degli ultimi backup attiva");
                s.BackupsToKeep = 1;
                string before = Cleaner.ListBackups(s.BackupFolder).First(b => b.Reason == SaveVault.BeforeRestoreReason && b.AppName == g.Name).ZipPath;
                File.WriteAllText(Path.Combine(saves, "slot1.sav"), "ALTRO");
                var r2 = SaveVault.RestoreBackup(g, before, null, null);
                Check(r2.Errors.Count == 0 && Read(Path.Combine(saves, "slot1.sav")) == "MODIFICATO" && Read(Path.Combine(saves, "nuovo.sav")) == "nuovo",
                    "il backup «Prima del ripristino» si ripristina anche se la conservazione ne crea un altro");
                Check(File.Exists(before), "il backup ripristinato non è stato tolto dalla conservazione");
                s.BackupsToKeep = 0;

                Console.WriteLine("Eliminazione di un profilo");
                st = SaveVault.Load(g);
                bool refused = false;
                try { SaveVault.DeleteProfile(g, st, st.ActiveProfile); } catch (InvalidOperationException) { refused = true; }
                Check(refused, "il profilo in uso non si elimina");
                string gameDir = SaveVault.GameDir(g);
                SaveVault.DeleteProfile(g, st, st.Profiles.First(p => p.Name == "Luigi").Id);
                st = SaveVault.Load(g);
                Check(st.Profiles.Count == 0, "profilo «Luigi» eliminato (resta solo quello in uso, che non ha bisogno del deposito)");
                Check(!Directory.Exists(gameDir), "nessuna cartella del gioco rimasta nel deposito");
                Check(Read(Path.Combine(saves, "slot1.sav")) == "MODIFICATO", "salvataggi in uso intatti");
                Check(SaveVault.FreeProfileName(st.Profiles.Select(p => p.Name)) == "Profilo 2", "nome proposto di nuovo «Profilo 2»");

                Console.WriteLine("Nuovo profilo con una cartella rimasta nel deposito");
                string stale = Path.Combine(gameDir, "profiles", "p2", "01 GiocoDiProva");
                Directory.CreateDirectory(stale);
                File.WriteAllText(Path.Combine(stale, "vecchio.sav"), "vecchio");
                st = SaveVault.Load(g);
                var nuovo = SaveVault.Switch(g, st, locs, null, null, null);
                Check(nuovo.Id != "p2" && nuovo.Name == "Profilo 2", "il profilo nuovo non usa la cartella rimasta (id " + nuovo.Id + ", nome «" + nuovo.Name + "»)");
                Check(!File.Exists(Path.Combine(saves, "vecchio.sav")), "i file rimasti non finiscono nella nuova partita");
                st = SaveVault.Load(g);
                SaveVault.Switch(g, st, locs, st.Profiles.First(p => p.Name == "Principale").Id, null, null);
                st = SaveVault.Load(g);
                SaveVault.DeleteProfile(g, st, nuovo.Id);
                Check(File.Exists(Path.Combine(stale, "vecchio.sav")), "con file estranei nel deposito la cartella resta (niente va perso)");
                Check(Read(Path.Combine(saves, "slot1.sav")) == "MODIFICATO", "salvataggi in uso ancora intatti");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("  ERRORE imprevisto: " + ex);
            }
            finally
            {
                try { Directory.Delete(sandbox, true); } catch (Exception ex) { Console.WriteLine("Pulizia della cartella di prova non riuscita: " + ex.Message); }
            }
            Console.WriteLine(failures == 0 ? "Tutte le prove superate." : failures + " prove non superate.");
            return failures == 0 ? 0 : 1;
        }
    }
}
