using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace AppManager
{
    /// <summary>Un programma che parte con Windows.</summary>
    internal sealed class StartupEntry
    {
        public string Name;
        public string Command;
        public string Exe;              // eseguibile o collegamento (null se non si ricava dal comando)
        public string SourceText;       // da dove viene: "Registro (utente)", "Cartella Esecuzione automatica"...
        public string ApprovedKey;      // chiave StartupApproved in cui Windows ricorda se è attivo
        public string StateName;        // nome della voce di stato: il valore del registro o il nome del file
        public bool IsEnabled;
        public bool RequiresAdmin;
        public bool TargetMissing;
    }

    /// <summary>
    /// Avvio automatico: elenca i programmi e li attiva o disattiva come Gestione attività, scrivendo solo lo stato
    /// in StartupApproved. La voce non viene mai cancellata, quindi riattivarla è sempre possibile.
    /// </summary>
    internal static class StartupManager
    {
        private const string Approved = @"\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        /// <summary>Solo per le prove: radice del registro in cui stanno gli stati, per non toccare quelli veri.</summary>
        internal static string ApprovedRootOverride;

        private static string ApprovedKey(string hive, string leaf)
        {
            if (ApprovedRootOverride != null) return ApprovedRootOverride + "\\" + hive + "\\" + leaf;
            return hive + (hive == "HKLM" ? Approved.Replace(@"\Software", @"\SOFTWARE") : Approved) + leaf;
        }

        public static List<StartupEntry> List()
        {
            var list = new List<StartupEntry>();
            AddRun(list, @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", ApprovedKey("HKCU", "Run"), L.T("Registro di Windows (solo il tuo utente)"), false);
            AddRun(list, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", ApprovedKey("HKLM", "Run"), L.T("Registro di Windows (tutti gli utenti)"), true);
            AddRun(list, @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", ApprovedKey("HKLM", "Run32"), L.T("Registro di Windows (tutti gli utenti)"), true);
            AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), ApprovedKey("HKCU", "StartupFolder"), L.T("Cartella Esecuzione automatica (solo il tuo utente)"), false);
            AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), ApprovedKey("HKLM", "StartupFolder"), L.T("Cartella Esecuzione automatica (tutti gli utenti)"), true);
            return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static void AddRun(List<StartupEntry> list, string key, string approvedKey, string source, bool admin)
        {
            bool wow = RegUtil.IsWow32(key);
            using (var k = RegUtil.Open(key))
            {
                foreach (var v in RegUtil.ValueNames(k))
                {
                    if (v.Length == 0) continue;
                    string cmd = RegUtil.GetString(k, v);
                    if (string.IsNullOrWhiteSpace(cmd)) continue;
                    string exe = PathUtil.ExeFromCommand(PathUtil.Expand(cmd, wow) ?? cmd);
                    list.Add(new StartupEntry
                    {
                        Name = v, Command = cmd, Exe = exe, SourceText = source, ApprovedKey = approvedKey, RequiresAdmin = admin,
                        StateName = v, IsEnabled = ReadState(approvedKey, v), TargetMissing = exe != null && !File.Exists(exe),
                    });
                }
            }
        }

        private static void AddFolder(List<StartupEntry> list, string dir, string approvedKey, string source, bool admin)
        {
            if (string.IsNullOrEmpty(dir)) return;
            foreach (var f in PathUtil.Files(dir))
            {
                string name = Path.GetFileName(f);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new StartupEntry
                {
                    Name = Path.GetFileNameWithoutExtension(f), Command = f, Exe = f, SourceText = source, ApprovedKey = approvedKey, RequiresAdmin = admin,
                    StateName = name, IsEnabled = ReadState(approvedKey, name),
                });
            }
        }

        private static bool ReadState(string approvedKey, string valueName)
        {
            using (var k = RegUtil.Open(approvedKey))
            {
                if (k == null) return true;
                return IsEnabledValue(k.GetValue(valueName) as byte[]);
            }
        }

        /// <summary>Nessuna voce = attivo. Windows segna "disattivato" con il primo byte dispari (0x03, 0x07...).</summary>
        internal static bool IsEnabledValue(byte[] data)
        {
            return data == null || data.Length == 0 || (data[0] & 1) == 0;
        }

        /// <summary>Stesso formato di Gestione attività: 12 byte, con la data di disattivazione negli ultimi 8.</summary>
        internal static byte[] BuildValue(bool enabled, DateTime when)
        {
            var data = new byte[12];
            data[0] = enabled ? (byte)2 : (byte)3;
            if (!enabled) Array.Copy(BitConverter.GetBytes(when.ToFileTimeUtc()), 0, data, 4, 8);
            return data;
        }

        public static void SetEnabled(StartupEntry e, bool enabled)
        {
            string sub;
            RegistryHive hive;
            if (!RegUtil.Split(e.ApprovedKey, out hive, out sub)) throw new InvalidOperationException(L.F("Chiave non valida: {0}", e.ApprovedKey));
            using (var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
            using (var k = root.CreateSubKey(sub))
            {
                if (k == null) throw new InvalidOperationException(L.F("Impossibile aprire {0}", e.ApprovedKey));
                k.SetValue(e.StateName, BuildValue(enabled, DateTime.Now), RegistryValueKind.Binary);
            }
            e.IsEnabled = enabled;
            Log.Info("Avvio automatico di {0}: {1}", e.Name, enabled ? L.T("attivato") : L.T("disattivato"));
        }
    }
}
