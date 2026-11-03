using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace AppManager
{
    /// <summary>Esporta chiavi e valori del registro nel formato .reg di Regedit (reimportabile con "reg import").</summary>
    internal static class RegExport
    {
        public const string Header = "Windows Registry Editor Version 5.00\r\n";

        public static string ExportKey(string fullPath)
        {
            var sb = new StringBuilder(Header);
            using (var key = RegUtil.Open(fullPath))
            {
                if (key == null) return null;
                WriteKey(sb, RegUtil.LongName(fullPath), key, 0);
            }
            return sb.ToString();
        }

        public static string ExportValue(string keyPath, string valueName)
        {
            using (var key = RegUtil.Open(keyPath))
            {
                if (key == null || !RegUtil.ValueNames(key).Contains(valueName, StringComparer.OrdinalIgnoreCase)) return null;
                var sb = new StringBuilder(Header);
                sb.Append("\r\n[").Append(RegUtil.LongName(keyPath)).Append("]\r\n");
                WriteValue(sb, key, valueName);
                return sb.ToString();
            }
        }

        private static void WriteKey(StringBuilder sb, string name, RegistryKey key, int depth)
        {
            if (depth > 64) return;
            sb.Append("\r\n[").Append(name).Append("]\r\n");
            foreach (var v in RegUtil.ValueNames(key)) WriteValue(sb, key, v);
            foreach (var sub in RegUtil.SubKeys(key))
            {
                try
                {
                    using (var sk = key.OpenSubKey(sub))
                        if (sk != null) WriteKey(sb, name + "\\" + sub, sk, depth + 1);
                }
                catch { }
            }
        }

        private static void WriteValue(StringBuilder sb, RegistryKey key, string name)
        {
            RegistryValueKind kind;
            object value;
            try
            {
                kind = key.GetValueKind(name);
                value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            }
            catch { return; }
            sb.Append(name.Length == 0 ? "@" : "\"" + Escape(name) + "\"").Append('=');
            switch (kind)
            {
                case RegistryValueKind.String:
                    string str = Convert.ToString(value);
                    // il formato .reg non sa rappresentare a capo e caratteri di controllo tra virgolette: si salvano in esadecimale
                    if (str.Any(c => c < ' ')) sb.Append("hex(1):").Append(Hex(Encoding.Unicode.GetBytes(str + "\0")));
                    else sb.Append('"').Append(Escape(str)).Append('"');
                    break;
                case RegistryValueKind.DWord:
                    sb.Append("dword:").Append(unchecked((uint)Convert.ToInt32(value)).ToString("x8"));
                    break;
                case RegistryValueKind.QWord:
                    sb.Append("hex(b):").Append(Hex(BitConverter.GetBytes(Convert.ToInt64(value))));
                    break;
                case RegistryValueKind.ExpandString:
                    sb.Append("hex(2):").Append(Hex(Encoding.Unicode.GetBytes(Convert.ToString(value) + "\0")));
                    break;
                case RegistryValueKind.MultiString:
                    var parts = value as string[] ?? new string[0];
                    // ogni stringa termina con \0, più un \0 finale; un elenco vuoto è solo il \0 finale
                    sb.Append("hex(7):").Append(Hex(Encoding.Unicode.GetBytes(parts.Length == 0 ? "\0" : string.Join("\0", parts) + "\0\0")));
                    break;
                case RegistryValueKind.Binary:
                    sb.Append("hex:").Append(Hex(value as byte[] ?? new byte[0]));
                    break;
                default:
                    // REG_NONE e tipi non standard: .NET li restituisce come dati grezzi
                    sb.Append("hex(0):").Append(Hex(value as byte[] ?? new byte[0]));
                    break;
            }
            sb.Append("\r\n");
        }

        private static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>
        /// True se il testo .reg scrive solo nella chiave indicata (o nelle sue sottochiavi, se <paramref name="subkeys"/>)
        /// e non contiene eliminazioni: un backup alterato non può toccare altre parti del registro.
        /// </summary>
        public static bool IsConfinedTo(string regText, string keyPath, bool subkeys)
        {
            string root = RegUtil.ShortName(keyPath ?? "").TrimEnd('\\');
            if (root.Length == 0 || root.Contains(@"\\")) return false;
            bool any = false;
            bool inString = false;
            foreach (var raw in regText.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (inString) { inString = OpenString(line, inString); continue; }
                string t = line.Trim();
                if (t.StartsWith("["))
                {
                    if (!t.EndsWith("]") || t.StartsWith("[-")) return false;
                    string key = RegUtil.ShortName(t.Substring(1, t.Length - 2)).TrimEnd('\\');
                    bool ok = key.Equals(root, StringComparison.OrdinalIgnoreCase)
                              || (subkeys && key.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));
                    if (!ok || key.Contains(@"\\")) return false;
                    any = true;
                    continue;
                }
                if (t.Length == 0 || t.StartsWith(";") || t.StartsWith("Windows Registry Editor", StringComparison.OrdinalIgnoreCase)) continue;
                if (!any) return false;              // valori fuori da una chiave
                if (t.EndsWith("=-")) return false;  // eliminazione di un valore
                inString = OpenString(line, false);
            }
            return any;
        }

        /// <summary>True se la riga lascia aperta una stringa tra virgolette (che continua alla riga successiva).</summary>
        private static bool OpenString(string line, bool open)
        {
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '\\' && open) { i++; continue; }
                if (line[i] == '"') open = !open;
            }
            return open;
        }

        private static string Hex(byte[] data)
        {
            return string.Join(",", data.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        /// <summary>Importa un file .reg con lo strumento di Windows. Restituisce null se tutto ok, altrimenti l'errore.</summary>
        public static string Import(string regText)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "am_restore_" + Guid.NewGuid().ToString("N") + ".reg");
            try
            {
                File.WriteAllText(tmp, regText, Encoding.Unicode);
                var psi = new System.Diagnostics.ProcessStartInfo("reg.exe", "import \"" + tmp + "\"")
                {
                    CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(30000);
                    return p.ExitCode == 0 ? null : (err.Trim().Length > 0 ? err.Trim() : L.T("reg import non riuscito"));
                }
            }
            catch (Exception ex) { return ex.Message; }
            finally { try { File.Delete(tmp); } catch { } }
        }
    }
}
