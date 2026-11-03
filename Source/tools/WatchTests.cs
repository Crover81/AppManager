using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace AppManager
{
    /// <summary>
    /// Prova completa della sorveglianza con una finta applicazione: voce di disinstallazione, cartella di dati e chiave nel registro
    /// creati dalla prova. La voce sparisce come dopo una disinstallazione e i residui devono comparire nella cronologia.
    /// Non tocca nient'altro e alla fine toglie tutto ciò che ha creato.
    /// </summary>
    internal static class WatchTests
    {
        private const string Name = "AppManager Prova Sorveglianza";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AppManagerProvaSorveglianza";
        private const string DataKey = @"Software\AppManager Prova Sorveglianza";
        private static int failures;

        public static int Run(string expectedDataDir)
        {
            if (!PathUtil.Same(AppPaths.DataDir, Path.GetFullPath(expectedDataDir)))
            {
                Console.WriteLine("Imposta APPMANAGER_DATA={0} prima di eseguire la prova (cartella dati attuale: {1})", expectedDataDir, AppPaths.DataDir);
                return 1;
            }
            string installDir = Path.Combine(Path.GetTempPath(), "AppManagerProvaSorveglianza_" + Guid.NewGuid().ToString("N"));
            string dataDir = Path.Combine(KnownDirs.RoamingAppData, Name);
            if (Directory.Exists(dataDir) || Registry.CurrentUser.OpenSubKey(UninstallKey) != null)
            {
                Console.WriteLine("Restano dati di una prova precedente: {0}", dataDir);
                return 1;
            }
            HistoryEvent found = null;
            var done = new ManualResetEvent(false);
            Action<HistoryEvent, bool> handler = (e, notify) =>
            {
                if (e.Name == Name && e.Status != HistoryEvent.Checking) { found = e; Console.WriteLine("  voce: {0} {1}, avviso={2}", e.Type, e.Status, notify); done.Set(); }
            };
            try
            {
                Directory.CreateDirectory(installDir);
                File.WriteAllBytes(Path.Combine(installDir, "ProvaSorveglianza.exe"), new byte[] { 0x4D, 0x5A });
                Directory.CreateDirectory(dataDir);
                File.WriteAllText(Path.Combine(dataDir, "impostazioni.ini"), "prova");
                using (var k = Registry.CurrentUser.CreateSubKey(DataKey)) k.SetValue("Prova", 1);
                using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    k.SetValue("DisplayName", Name);
                    k.SetValue("Publisher", "AppManager");
                    k.SetValue("DisplayVersion", "1.0");
                    k.SetValue("InstallLocation", installDir);
                    k.SetValue("UninstallString", "\"" + Path.Combine(installDir, "ProvaSorveglianza.exe") + "\" /uninstall");
                }
                WatchSnapshot.Delete();
                MonitorHistory.Clear();
                InstallWatcher.Recorded += handler;
                Console.WriteLine("Avvio della sorveglianza (prima istantanea)…");
                InstallWatcher.Start();
                for (int i = 0; i < 240 && WatchSnapshot.Load() == null; i++) Thread.Sleep(1000);
                Check(WatchSnapshot.Load() != null && WatchSnapshot.Load().Values.Any(w => w.Name == Name), "la finta applicazione è nella prima istantanea");
                // le impronte delle applicazioni si calcolano dopo la prima istantanea: si aspetta che la sorveglianza sia in ascolto
                for (int i = 0; i < 300 && InstallWatcher.Status != L.T("In ascolto"); i++) Thread.Sleep(1000);
                Console.WriteLine("Disinstallazione simulata: tolgo la voce dal registro…");
                Directory.Delete(installDir, true);
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKey);
                bool ok = done.WaitOne(TimeSpan.FromMinutes(4));
                Check(ok && found != null && found.Type == HistoryEvent.Uninstalled, "disinstallazione rilevata entro 4 minuti");
                if (found != null)
                {
                    var items = (found.Items ?? new List<StoredItem>()).Select(x => x.ToItem()).Where(x => x != null).ToList();
                    foreach (var i in items) Console.WriteLine("    {0} | {1} | {2}", i.Confidence, i.DisplayPath, i.Reason);
                    Check(items.Any(i => i.Kind == ItemKind.Directory && PathUtil.Same(i.Path, dataDir)), "residuo trovato: la sua cartella in AppData");
                    Check(items.Any(i => i.Kind == ItemKind.RegistryKey && i.Path.EndsWith(DataKey, StringComparison.OrdinalIgnoreCase)), "residuo trovato: la sua chiave nel registro");
                    Check(found.Status == HistoryEvent.Leftovers && found.WorthNotifying, "voce «Da rivedere» con avviso");
                    Check(items.All(i => i.Path.IndexOf("AppManager Prova", StringComparison.OrdinalIgnoreCase) >= 0 || i.Path.IndexOf("AppManagerProva", StringComparison.OrdinalIgnoreCase) >= 0),
                          "solo cose della finta applicazione, niente di estraneo");
                }
                Check(Directory.Exists(dataDir) && Registry.CurrentUser.OpenSubKey(DataKey) != null, "la sorveglianza non ha eliminato nulla da sola");
            }
            catch (Exception ex) { Check(false, "eccezione: " + ex.Message); }
            finally
            {
                InstallWatcher.Recorded -= handler;
                InstallWatcher.Stop();
                try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(DataKey, false); } catch { }
                try { if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true); } catch { }
                try { if (Directory.Exists(installDir)) Directory.Delete(installDir, true); } catch { }
                MonitorHistory.Clear();
                WatchSnapshot.Delete();
            }
            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "Tutte le prove superate." : failures + " prove non superate.");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(bool ok, string what)
        {
            Console.WriteLine("  {0}  {1}", ok ? "OK  " : "ERRORE", what);
            if (!ok) failures++;
        }
    }
}
