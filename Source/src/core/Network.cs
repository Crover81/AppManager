using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;

namespace AppManager
{
    /// <summary>Una connessione TCP attiva, con l'eseguibile che l'ha aperta.</summary>
    internal sealed class NetConnection
    {
        public int Pid;
        public IPAddress Local, Remote;
        public int LocalPort, RemotePort;
        public int State;              // MIB_TCP_STATE
        public bool IPv6;

        public bool HasRemote
        {
            get { return Remote != null && !Remote.Equals(IPAddress.Any) && !Remote.Equals(IPAddress.IPv6Any); }
        }

        public string RemoteText { get { return Remote == null ? "" : (IPv6 ? "[" + Remote + "]" : Remote.ToString()) + ":" + RemotePort; } }

        public string StateText
        {
            get
            {
                switch (State)
                {
                    case 2: return L.T("In ascolto");
                    case 3: return L.T("Connessione…");
                    case 4: return L.T("Connessione…");
                    case 5: return L.T("Attiva");
                    case 8: return L.T("In chiusura");
                    case 11: return L.T("Attesa");
                    default: return "";
                }
            }
        }
    }

    /// <summary>Legge le connessioni di rete attive e a quale processo appartengono (come netstat -b). Sola lettura.</summary>
    internal static class NetMonitor
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);

        private const int AF_INET = 2, AF_INET6 = 23;
        private const int TCP_TABLE_OWNER_PID_ALL = 5;

        public static List<NetConnection> Connections()
        {
            var list = new List<NetConnection>();
            Read(AF_INET, list);
            Read(AF_INET6, list);
            return list;
        }

        private static void Read(int af, List<NetConnection> list)
        {
            int size = 0;
            uint rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
            if (size == 0) return;
            IntPtr table = Marshal.AllocHGlobal(size);
            try
            {
                rc = GetExtendedTcpTable(table, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
                if (rc != 0) return;
                int count = Marshal.ReadInt32(table);
                IntPtr row = table + 4;
                int rowSize = af == AF_INET ? 24 : 56;
                for (int i = 0; i < count; i++)
                {
                    var c = af == AF_INET ? ReadV4(row) : ReadV6(row);
                    if (c != null) list.Add(c);
                    row += rowSize;
                }
            }
            finally { Marshal.FreeHGlobal(table); }
        }

        // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, owningPid (6 DWORD)
        private static NetConnection ReadV4(IntPtr row)
        {
            int state = Marshal.ReadInt32(row);
            uint localAddr = (uint)Marshal.ReadInt32(row + 4);
            int localPort = Port(Marshal.ReadInt32(row + 8));
            uint remoteAddr = (uint)Marshal.ReadInt32(row + 12);
            int remotePort = Port(Marshal.ReadInt32(row + 16));
            int pid = Marshal.ReadInt32(row + 20);
            return new NetConnection
            {
                Pid = pid, State = state, IPv6 = false,
                Local = new IPAddress(BitConverter.GetBytes(localAddr)), LocalPort = localPort,
                Remote = new IPAddress(BitConverter.GetBytes(remoteAddr)), RemotePort = state == 2 ? 0 : remotePort,
            };
        }

        // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid
        private static NetConnection ReadV6(IntPtr row)
        {
            var local = new byte[16]; Marshal.Copy(row, local, 0, 16);
            int localPort = Port(Marshal.ReadInt32(row + 20));
            var remote = new byte[16]; Marshal.Copy(row + 24, remote, 0, 16);
            int remotePort = Port(Marshal.ReadInt32(row + 44));
            int state = Marshal.ReadInt32(row + 48);
            int pid = Marshal.ReadInt32(row + 52);
            return new NetConnection
            {
                Pid = pid, State = state, IPv6 = true,
                Local = new IPAddress(local), LocalPort = localPort,
                Remote = new IPAddress(remote), RemotePort = state == 2 ? 0 : remotePort,
            };
        }

        /// <summary>La porta è nei 16 bit bassi del DWORD, in ordine di rete (big-endian).</summary>
        private static int Port(int value)
        {
            return ((value & 0xff) << 8) | ((value >> 8) & 0xff);
        }
    }

    /// <summary>
    /// Regole di Windows Firewall create da AppManager per bloccare l'accesso a Internet di programmi, giochi
    /// o singole destinazioni. Tutte portano lo stesso "gruppo", così si possono elencare e togliere con sicurezza.
    /// Creare o togliere una regola richiede i permessi di amministratore.
    /// </summary>
    internal static class Firewall
    {
        public const string Group = "AppManager (blocco rete)";
        private const int ActionBlock = 0;
        private const int DirIn = 1, DirOut = 2;
        private const int ProfileAll = 0x7fffffff;

        private static dynamic Policy()
        {
            var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (t == null) throw new InvalidOperationException(L.T("Windows Firewall non è disponibile su questo sistema."));
            return Activator.CreateInstance(t);
        }

        private static dynamic NewRule()
        {
            return Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule"));
        }

        /// <summary>True se il servizio di Windows Firewall è raggiungibile.</summary>
        public static bool Available
        {
            get { try { var p = Policy(); return p != null; } catch { return false; } }
        }

        private static void AddRule(dynamic rules, string name, string exe, string remote, int dir, string description)
        {
            dynamic r = NewRule();
            r.Name = name;
            r.Description = description;
            r.Grouping = Group;
            r.Direction = dir;
            r.Action = ActionBlock;
            r.Enabled = true;
            r.Profiles = ProfileAll;
            if (!string.IsNullOrEmpty(exe)) r.ApplicationName = exe;
            if (!string.IsNullOrEmpty(remote)) r.RemoteAddresses = remote;
            rules.Add(r);
        }

        /// <summary>Blocca l'accesso a Internet (in entrata e in uscita) per un eseguibile.</summary>
        public static void BlockProgram(string exe, string label)
        {
            if (string.IsNullOrEmpty(exe)) return;
            RemoveWhere(r => SameApp(r, exe) && string.IsNullOrEmpty(RemoteOf(r)));
            dynamic policy = Policy();
            AddRule(policy.Rules, RuleName(label, "uscita", exe), exe, null, DirOut, L.F("Blocco Internet per {0}", label));
            AddRule(policy.Rules, RuleName(label, "entrata", exe), exe, null, DirIn, L.F("Blocco Internet per {0}", label));
        }

        /// <summary>Blocca (in uscita) tutte le connessioni verso un indirizzo remoto, per un eseguibile o per tutti.</summary>
        public static void BlockRemote(string remoteIp, string exe, string label)
        {
            if (string.IsNullOrEmpty(remoteIp)) return;
            var policy = Policy();
            AddRule(policy.Rules, RuleName(label + " → " + remoteIp, "uscita", (exe ?? "") + "|" + remoteIp), exe, remoteIp, DirOut,
                "Blocco verso " + remoteIp + (exe != null ? " per " + label : ""));
        }

        /// <summary>Blocca un insieme di eseguibili (per esempio tutti quelli di un gioco).</summary>
        public static void BlockPrograms(IEnumerable<string> exes, string label)
        {
            dynamic policy = Policy();
            foreach (var exe in exes.Where(e => !string.IsNullOrEmpty(e)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string one = exe;
                RemoveWhere(r => SameApp(r, one) && string.IsNullOrEmpty(RemoteOf(r)));
                AddRule(policy.Rules, RuleName(label, "uscita", one), one, null, DirOut, L.F("Blocco Internet per {0}", label));
                AddRule(policy.Rules, RuleName(label, "entrata", one), one, null, DirIn, L.F("Blocco Internet per {0}", label));
            }
        }

        /// <summary>Toglie ogni regola di AppManager che blocca del tutto (senza indirizzo) uno di questi eseguibili.</summary>
        public static void UnblockPrograms(IEnumerable<string> exes)
        {
            var set = new HashSet<string>(exes.Where(e => !string.IsNullOrEmpty(e)), StringComparer.OrdinalIgnoreCase);
            if (set.Count == 0) return;
            RemoveWhere(r => { string a = AppOf(r); return a != null && set.Contains(a) && string.IsNullOrEmpty(RemoteOf(r)); });
        }

        /// <summary>True se almeno uno di questi eseguibili è bloccato del tutto (senza indirizzo) da una regola di AppManager.</summary>
        public static bool AnyBlocked(IEnumerable<string> exes)
        {
            try
            {
                var set = new HashSet<string>(exes.Where(e => !string.IsNullOrEmpty(e)), StringComparer.OrdinalIgnoreCase);
                if (set.Count == 0) return false;
                foreach (dynamic r in Policy().Rules)
                {
                    if (!IsOurs(r)) continue;
                    string a = AppOf(r);
                    if (a != null && set.Contains(a) && string.IsNullOrEmpty(RemoteOf(r))) return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// L'insieme degli eseguibili bloccati del tutto (senza indirizzo) da AppManager. Una sola lettura del firewall,
        /// da usare nel monitor invece di interrogare regola per regola a ogni aggiornamento.
        /// </summary>
        public static HashSet<string> BlockedProgramSet()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (dynamic r in Policy().Rules)
                {
                    if (!IsOurs(r) || !string.IsNullOrEmpty(RemoteOf(r))) continue;
                    string a = AppOf(r);
                    if (a != null) set.Add(a);
                }
            }
            catch (Exception ex) { Log.Error("Lettura programmi bloccati", ex); }
            return set;
        }

        /// <summary>True se è bloccato del tutto un eseguibile che si trova nella cartella indicata (o è proprio quello principale).</summary>
        public static bool AnyBlockedUnder(string dir, string mainExe)
        {
            try
            {
                foreach (dynamic r in Policy().Rules)
                {
                    if (!IsOurs(r) || !string.IsNullOrEmpty(RemoteOf(r))) continue;
                    string a = AppOf(r);
                    if (a == null) continue;
                    if (mainExe != null && string.Equals(a, mainExe, StringComparison.OrdinalIgnoreCase)) return true;
                    if (dir != null && PathUtil.IsUnder(a, dir)) return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>Toglie il blocco totale da tutti gli eseguibili nella cartella indicata (e da quello principale).</summary>
        public static void UnblockUnder(string dir, string mainExe)
        {
            RemoveWhere(r =>
            {
                if (!string.IsNullOrEmpty(RemoteOf(r))) return false;
                string a = AppOf(r);
                if (a == null) return false;
                return (mainExe != null && string.Equals(a, mainExe, StringComparison.OrdinalIgnoreCase)) || (dir != null && PathUtil.IsUnder(a, dir));
            });
        }

        /// <summary>Toglie una regola dal nome (per l'elenco nelle Impostazioni).</summary>
        public static void RemoveByName(string name)
        {
            try { Policy().Rules.Remove(name); } catch { }
        }

        /// <summary>Elenca le regole di blocco create da AppManager.</summary>
        public static List<FirewallRuleInfo> List()
        {
            var list = new List<FirewallRuleInfo>();
            try
            {
                foreach (dynamic r in Policy().Rules)
                {
                    if (!IsOurs(r)) continue;
                    list.Add(new FirewallRuleInfo
                    {
                        Name = SafeStr(() => (string)r.Name),
                        Program = AppOf(r),
                        Remote = RemoteOf(r),
                        Outbound = SafeInt(() => (int)r.Direction) == DirOut,
                    });
                }
            }
            catch (Exception ex) { Log.Error("Lettura regole firewall", ex); }
            return list;
        }

        private static void RemoveWhere(Func<dynamic, bool> match)
        {
            dynamic policy = Policy();
            var names = new List<string>();
            foreach (dynamic r in policy.Rules)
                if (IsOurs(r) && match(r)) names.Add((string)r.Name);
            foreach (var n in names) { try { policy.Rules.Remove(n); } catch { } }
        }

        private static bool IsOurs(dynamic r)
        {
            return string.Equals(SafeStr(() => (string)r.Grouping), Group, StringComparison.Ordinal);
        }

        private static bool SameApp(dynamic r, string exe)
        {
            string a = AppOf(r);
            return a != null && string.Equals(a, exe, StringComparison.OrdinalIgnoreCase);
        }

        private static string AppOf(dynamic r) { return NullIfEmpty(SafeStr(() => (string)r.ApplicationName)); }

        private static string RemoteOf(dynamic r)
        {
            string s = SafeStr(() => (string)r.RemoteAddresses);
            if (s == "*") return null;
            // Windows aggiunge la maschera a un indirizzo singolo ("1.2.3.4/255.255.255.255"): la togliamo per mostrarlo pulito
            if (s.EndsWith("/255.255.255.255", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 16);
            return NullIfEmpty(s);
        }

        private static string RuleName(string label, string dir, string key)
        {
            int h = (key ?? "").ToLowerInvariant().GetHashCode();
            return "AppManager • " + Trim(label) + " • " + dir + " • " + ((uint)h).ToString("x8");
        }

        private static string Trim(string s) { s = (s ?? "").Trim(); return s.Length > 60 ? s.Substring(0, 60) : s; }
        private static string NullIfEmpty(string s) { return string.IsNullOrEmpty(s) ? null : s; }
        private static string SafeStr(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
        private static int SafeInt(Func<int> f) { try { return f(); } catch { return 0; } }
    }

    internal sealed class FirewallRuleInfo
    {
        public string Name, Program, Remote;
        public bool Outbound;
    }
}
