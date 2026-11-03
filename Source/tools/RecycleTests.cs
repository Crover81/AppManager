using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace AppManager
{
    /// <summary>
    /// Prove del Cestino organizzato per applicazione, su dati finti: file e cartelle raccolti in «AppManager - …» con il
    /// percorso completo, voci di registro in «File registro», file in uso lasciati al loro posto. La cartella di raccolta
    /// non va nel Cestino dell'utente: viene controllata e poi eliminata.
    /// </summary>
    internal static class RecycleTests
    {
        private static int failures;
        private const string AppName = "Prova AppManager (test)";
        private const string KeyA = @"HKCU\Software\AppManagerRecycleTest";
        private const string KeyB = @"HKCU\Software\AppManagerRecycleTest2";

        private static void Check(bool ok, string what)
        {
            Console.WriteLine("  {0}  {1}", ok ? "OK  " : "ERRORE", what);
            if (!ok) failures++;
        }

        public static int Run(string sandboxRoot)
        {
            string sandbox = Path.Combine(Path.GetFullPath(sandboxRoot), "recycletest");
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            string app = Path.Combine(sandbox, "App"), ini = Path.Combine(sandbox, "Dati", "impostazioni.ini"), locked = Path.Combine(sandbox, "Log", "in_uso.log");
            Directory.CreateDirectory(Path.Combine(app, "bin"));
            Directory.CreateDirectory(Path.GetDirectoryName(ini));
            Directory.CreateDirectory(Path.GetDirectoryName(locked));
            File.WriteAllText(Path.Combine(app, "bin", "app.exe"), "exe");
            File.WriteAllText(Path.Combine(app, "leggimi.txt"), "ciao");
            File.WriteAllText(ini, "[opzioni]");
            File.WriteAllText(locked, "log");
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\AppManagerRecycleTest\Sotto")) k.SetValue("Valore", "uno");
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\AppManagerRecycleTest2")) { k.SetValue("Opzione", 42); k.SetValue("Altro", "resta"); }

            AppSettings.SaveDisabled = true;
            RecycleStaging.SendToBin = false;
            var result = new CleanResult();
            string staging = null;
            try
            {
                var items = new List<LeftoverItem>
                {
                    new LeftoverItem { Kind = ItemKind.Directory, Category = ItemCategory.AppFolder, Path = app, Confidence = Confidence.High },
                    new LeftoverItem { Kind = ItemKind.File, Category = ItemCategory.Settings, Path = ini, Confidence = Confidence.High },
                    new LeftoverItem { Kind = ItemKind.File, Category = ItemCategory.Other, Path = locked, Confidence = Confidence.High },
                    new LeftoverItem { Kind = ItemKind.RegistryKey, Category = ItemCategory.Registry, Path = KeyA, Confidence = Confidence.High },
                    new LeftoverItem { Kind = ItemKind.RegistryValue, Category = ItemCategory.Registry, Path = KeyB, ValueName = "Opzione", Confidence = Confidence.High },
                };
                var opt = new CleanOptions { UseRecycleBin = true, Backup = false, Title = AppName, AppName = AppName, AppKind = AppKind.Program, Reason = "Pulizia" };
                using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                    result = Cleaner.Clean(items, opt, null, CancellationToken.None);

                Console.WriteLine("Cartella dell'applicazione");
                Check(result.RecycleFolders.Count == 1, "una sola cartella per l'applicazione (" + result.RecycleFolders.Count + ")");
                staging = result.RecycleFolders.FirstOrDefault();
                Check(staging != null && Path.GetFileName(staging) == RecycleStaging.Prefix + AppName, "nome «" + (staging == null ? "?" : Path.GetFileName(staging)) + "»");
                Check(staging != null && Path.GetDirectoryName(staging).Length <= 3, "nella radice dell'unità: " + staging);

                Console.WriteLine("Percorsi completi");
                Func<string, string> mirror = p => Path.Combine(staging, p.Substring(0, 1), p.Substring(3));
                Check(staging != null && File.Exists(mirror(Path.Combine(app, "bin", "app.exe"))), "cartella del programma con il suo percorso: " + (staging == null ? "" : mirror(app)));
                Check(staging != null && File.Exists(mirror(ini)), "file singolo con il suo percorso");
                Check(!Directory.Exists(app) && !File.Exists(ini), "originali tolti");

                Console.WriteLine("File registro");
                string reg = staging == null ? null : Path.Combine(staging, RecycleStaging.RegistryFolder);
                var regs = reg != null && Directory.Exists(reg) ? Directory.GetFiles(reg) : new string[0];
                Check(regs.Length == 2, "2 file in «File registro» (" + regs.Length + ")");
                Check(regs.All(f => f.EndsWith(".reg") && File.ReadAllText(f).StartsWith("Windows Registry Editor Version 5.00")), "file .reg importabili con un doppio clic");
                Check(regs.Any(f => File.ReadAllText(f).Contains("\"Valore\"=\"uno\"")), "la chiave contiene anche le sottochiavi e i valori");
                Check(regs.Any(f => Path.GetFileName(f).Contains("Opzione") && File.ReadAllText(f).Contains("\"Opzione\"=dword:0000002a")), "il valore singolo ha il suo file");
                Check(!RegUtil.KeyExists(KeyA), "chiave tolta dal registro");
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\AppManagerRecycleTest2"))
                    Check(k != null && k.GetValue("Opzione") == null && (string)k.GetValue("Altro") == "resta", "tolto solo il valore scelto");

                Console.WriteLine("File in uso");
                Check(File.Exists(locked), "il file in uso resta al suo posto");
                Check(result.Errors.Any(e => e.Contains(locked)), "ed è segnalato tra gli errori");
                Check(result.Removed == 4, "4 elementi tolti (" + result.Removed + ")");

                Console.WriteLine("Sicurezza");
                Check(staging != null && !Safety.IsDeletablePath(staging) && Safety.IsOwnData(Path.Combine(staging, "C")), "la cartella di raccolta non è mai proposta come residuo");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("  ERRORE imprevisto: " + ex);
            }
            finally
            {
                try { if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception ex) { Console.WriteLine("Pulizia di " + staging + " non riuscita: " + ex.Message); }
                foreach (var extra in result.RecycleFolders.Concat(result.KeptFolders))
                    try { if (Directory.Exists(extra)) Directory.Delete(extra, true); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\AppManagerRecycleTest", false); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\AppManagerRecycleTest2", false); } catch { }
                try { Directory.Delete(sandbox, true); } catch { }
            }
            Console.WriteLine(failures == 0 ? "Tutte le prove superate." : failures + " prove non superate.");
            return failures == 0 ? 0 : 1;
        }
    }
}
