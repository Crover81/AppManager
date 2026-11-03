using System;
using System.IO;
using System.Linq;
using System.Text;

namespace AppManager
{
    /// <summary>
    /// Prove del registro attività. Vanno eseguite con APPMANAGER_DATA impostata su una cartella di prova
    /// (il registro vero non deve essere toccato).
    /// </summary>
    internal static class LogTests
    {
        private static int failures;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine("  {0}  {1}", ok ? "OK  " : "ERRORE", what);
            if (!ok) failures++;
        }

        public static int Run(string expectedDataDir)
        {
            if (!PathUtil.Same(AppPaths.DataDir, Path.GetFullPath(expectedDataDir)))
            {
                Console.WriteLine("Imposta APPMANAGER_DATA={0} prima di eseguire la prova (cartella dati attuale: {1})", expectedDataDir, AppPaths.DataDir);
                return 1;
            }
            string f = Log.FilePath;
            Log.Flush();

            Console.WriteLine("Conservazione per giorni e vecchio log.txt.old");
            var sb = new StringBuilder();
            sb.AppendFormat("{0:yyyy-MM-dd HH:mm:ss} [INFO] vecchia di 60 giorni\r\n", DateTime.Now.AddDays(-60));
            sb.AppendFormat("{0:yyyy-MM-dd HH:mm:ss} [ERRORE] errore vecchio\r\n", DateTime.Now.AddDays(-45));
            sb.AppendFormat("{0:yyyy-MM-dd HH:mm:ss} [DETTAGLI] traccia\r\n   at Riga1\r\n   at Riga2\r\n", DateTime.Now.AddDays(-45));
            sb.AppendFormat("{0:yyyy-MM-dd HH:mm:ss} [INFO] recente di 10 giorni\r\n", DateTime.Now.AddDays(-10));
            File.WriteAllText(f, sb.ToString(), Encoding.UTF8);
            File.WriteAllText(f + ".old", "vecchio");
            Log.Maintain(30);
            string text = File.ReadAllText(f);
            Check(!text.Contains("60 giorni") && !text.Contains("errore vecchio") && !text.Contains("Riga1"), "voci più vecchie di 30 giorni tolte (dettagli compresi)");
            Check(text.Contains("recente di 10 giorni"), "voci recenti conservate");
            Check(!File.Exists(f + ".old"), "log.txt.old eliminato");

            Console.WriteLine("Compattazione delle ripetizioni");
            for (int i = 0; i < 5; i++) Log.Info("messaggio ripetuto");
            Log.Warn("messaggio diverso");
            text = File.ReadAllText(f);
            int count = text.Split(new[] { "messaggio ripetuto" }, StringSplitOptions.None).Length - 1;
            Check(count == 1, "riga ripetuta scritta una sola volta (trovata " + count + " volte)");
            Check(text.Contains("ripetuto altre 4 volte"), "nota «ripetuto altre 4 volte»");
            Check(text.Contains("[AVVISO] messaggio diverso"), "livello AVVISO");

            Console.WriteLine("Lettura delle voci");
            try { throw new InvalidOperationException("riga uno"); }
            catch (Exception ex) { Log.Error("Errore di prova", ex); }
            var entries = Log.ReadEntries(1000);
            Check(entries.Count > 0 && entries[0].Level == "DETTAGLI" && entries[0].Message.Contains("\n"), "dettagli su più righe in un'unica voce, dalla più recente");
            Check(entries.Any(e => e.Level == "ERRORE" && e.Message.Contains("riga uno")), "voce di errore letta");

            Console.WriteLine("Dimensione massima");
            var big = new string('x', 900);
            for (int i = 0; i < 3000; i++) Log.Info("riga lunga {0} {1}", i, big);
            long size = new FileInfo(f).Length;
            Check(size <= 2 * 1024 * 1024, "file sotto i 2 MB (" + Fmt.Size(size) + ")");
            Check(File.ReadAllText(f).Contains("riga lunga 2999"), "le voci più recenti restano");

            Console.WriteLine("Svuotamento");
            Log.Clear();
            entries = Log.ReadEntries(10);
            Check(entries.Count == 1 && entries[0].Message.Contains("svuotato"), "registro svuotato con una riga di nota");

            Console.WriteLine(failures == 0 ? "Tutte le prove superate." : failures + " prove non superate.");
            return failures == 0 ? 0 : 1;
        }
    }
}
