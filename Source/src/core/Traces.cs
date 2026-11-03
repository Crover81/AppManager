using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AppManager
{
    /// <summary>Traccia nel registro che fa riferimento al percorso di un eseguibile (o di una libreria).</summary>
    internal sealed class RegTrace
    {
        public string KeyPath;
        public string ValueName;   // null = l'intera chiave
        public string ExePath;
        public string What;
        public string Tag;         // informazione aggiuntiva (CLSID, nome del ProgID, percorso delle Capabilities...)
    }

    internal sealed class ShortcutInfo
    {
        public string File;
        public string Target;
        public string Url;
        public string IconFile;
    }

    /// <summary>Attività dell'Utilità di pianificazione (escluse quelle di Windows).</summary>
    internal sealed class TaskInfo
    {
        public string Path;                                   // \Cartella\Nome
        public readonly List<string> Exes = new List<string>(); // programmi avviati (già espansi)
        public bool NeedsAdmin;
    }

    /// <summary>Servizio di Windows (solo quelli con un proprio eseguibile, niente driver).</summary>
    internal sealed class ServiceInfo
    {
        public string Name;
        public string DisplayName;
        public string ExePath;
        public int Start;
    }

    /// <summary>Una cartella nella variabile d'ambiente PATH.</summary>
    internal sealed class PathEntryInfo
    {
        public string KeyPath;
        public string ValueName;
        public string Raw;         // come scritta nel registro (anche con %VARIABILI%)
        public string Expanded;
    }

    /// <summary>Lettori delle tracce che Windows, i giochi e i programmi lasciano in posti "noti".</summary>
    internal static class Traces
    {
        private static readonly KeyValuePair<string, string>[] ExeValueKeys =
        {
            new KeyValuePair<string, string>(@"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", L.T("impostazioni di compatibilità")),
            new KeyValuePair<string, string>(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", L.T("impostazioni di compatibilità")),
            new KeyValuePair<string, string>(@"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store", L.T("Assistente compatibilità programmi")),
            new KeyValuePair<string, string>(@"HKCU\Software\Microsoft\DirectX\UserGpuPreferences", L.T("preferenza GPU (Impostazioni grafiche)")),
            new KeyValuePair<string, string>(@"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", L.T("nome in cache di Esplora risorse")),
            new KeyValuePair<string, string>(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppSwitched", L.T("statistiche d'uso della barra delle applicazioni")),
            new KeyValuePair<string, string>(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\ShowJumpView", L.T("statistiche d'uso della barra delle applicazioni")),
            new KeyValuePair<string, string>(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppBadgeUpdated", L.T("statistiche d'uso della barra delle applicazioni")),
            new KeyValuePair<string, string>(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppLaunch", L.T("statistiche d'uso della barra delle applicazioni")),
        };

        public const string UserAssistRoot = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";

        private static readonly Regex DrivePath = new Regex(@"^[A-Za-z]:\\", RegexOptions.Compiled);

        /// <summary>Estrae il percorso .exe dal nome di un valore (es. MuiCache "C:\x\gioco.exe.FriendlyAppName").</summary>
        private static string ExeFromValueName(string name)
        {
            name = KnownDirs.ExpandKnownFolderId(name);
            if (name == null || !DrivePath.IsMatch(name)) return null;
            int idx = name.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return idx > 0 ? name.Substring(0, idx + 4) : null;
        }

        public static List<RegTrace> ExePathValues()
        {
            var list = new List<RegTrace>();
            foreach (var kv in ExeValueKeys)
            {
                using (var k = RegUtil.Open(kv.Key))
                {
                    foreach (var v in RegUtil.ValueNames(k))
                    {
                        string exe = ExeFromValueName(v);
                        if (exe != null) list.Add(new RegTrace { KeyPath = kv.Key, ValueName = v, ExePath = exe, What = kv.Value });
                    }
                }
            }
            // UserAssist: i nomi dei valori sono cifrati in ROT13 (statistiche dei programmi avviati)
            using (var ua = RegUtil.Open(UserAssistRoot))
            {
                foreach (var guid in RegUtil.SubKeys(ua))
                {
                    string key = UserAssistRoot + "\\" + guid + "\\Count";
                    using (var k = RegUtil.Open(key))
                    {
                        foreach (var v in RegUtil.ValueNames(k))
                        {
                            string exe = ExeFromValueName(Rot13(v));
                            if (exe != null) list.Add(new RegTrace { KeyPath = key, ValueName = v, ExePath = exe, What = L.T("statistiche dei programmi avviati (UserAssist)") });
                        }
                    }
                }
            }
            return list;
        }

        private static string Rot13(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c >= 'a' && c <= 'z') sb.Append((char)('a' + (c - 'a' + 13) % 26));
                else if (c >= 'A' && c <= 'Z') sb.Append((char)('A' + (c - 'A' + 13) % 26));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Nomi e produttori dei programmi avviati, ricordati da Esplora risorse (anche di quelli ormai cancellati).</summary>
        public static Dictionary<string, Tuple<string, string>> MuiCacheNames()
        {
            var map = new Dictionary<string, Tuple<string, string>>(StringComparer.OrdinalIgnoreCase);
            using (var k = RegUtil.Open(@"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache"))
            {
                foreach (var v in RegUtil.ValueNames(k))
                {
                    string exe = ExeFromValueName(v);
                    if (exe == null) continue;
                    Tuple<string, string> cur;
                    map.TryGetValue(exe, out cur);
                    string friendly = cur == null ? null : cur.Item1, company = cur == null ? null : cur.Item2;
                    if (v.EndsWith(".FriendlyAppName", StringComparison.OrdinalIgnoreCase)) friendly = RegUtil.GetString(k, v);
                    else if (v.EndsWith(".ApplicationCompany", StringComparison.OrdinalIgnoreCase)) company = RegUtil.GetString(k, v);
                    else continue;
                    map[exe] = Tuple.Create(friendly, company);
                }
            }
            return map;
        }

        /// <summary>Configurazioni di Game Bar / Modalità gioco (una sottochiave per eseguibile).</summary>
        public static List<RegTrace> GameConfigStore()
        {
            var list = new List<RegTrace>();
            const string root = @"HKCU\System\GameConfigStore\Children";
            using (var rk = RegUtil.Open(root))
            {
                foreach (var name in RegUtil.SubKeys(rk))
                {
                    try
                    {
                        using (var k = rk.OpenSubKey(name))
                        {
                            string exe = k == null ? null : RegUtil.GetString(k, "MatchedExeFullPath");
                            if (!string.IsNullOrEmpty(exe))
                                list.Add(new RegTrace { KeyPath = root + "\\" + name, ExePath = exe, What = L.T("impostazioni Game Bar e Modalità gioco") });
                        }
                    }
                    catch { }
                }
            }
            return list;
        }

        public static List<RegTrace> AppPaths()
        {
            var list = new List<RegTrace>();
            foreach (var root in new[] { @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", @"HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths" })
            {
                using (var rk = RegUtil.Open(root))
                {
                    foreach (var name in RegUtil.SubKeys(rk))
                    {
                        try
                        {
                            using (var k = rk.OpenSubKey(name))
                            {
                                string exe = k == null ? null : PathUtil.Normalize(RegUtil.GetString(k, ""));
                                if (!string.IsNullOrEmpty(exe))
                                    list.Add(new RegTrace { KeyPath = root + "\\" + name, ExePath = exe, What = "registrazione \"App Paths\"" });
                            }
                        }
                        catch { }
                    }
                }
            }
            return list;
        }

        /// <summary>Impostazioni delle notifiche per app: la chiave ha come nome l'ID dell'app, spesso il percorso dell'eseguibile.</summary>
        public static List<RegTrace> NotificationSettings()
        {
            var list = new List<RegTrace>();
            const string root = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
            using (var rk = RegUtil.Open(root))
            {
                foreach (var name in RegUtil.SubKeys(rk))
                {
                    string exe = ExeFromValueName(name);
                    if (exe != null) list.Add(new RegTrace { KeyPath = root + "\\" + name, ExePath = exe, What = L.T("impostazioni delle notifiche") });
                }
            }
            return list;
        }

        /// <summary>Eseguibili che hanno regole nel Windows Firewall.</summary>
        public static List<string> FirewallPrograms()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var k = RegUtil.Open(@"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules"))
            {
                foreach (var v in RegUtil.ValueNames(k))
                {
                    string data = RegUtil.GetString(k, v);
                    if (data == null) continue;
                    var m = Regex.Match(data, @"\|App=([^|]+)\|", RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    string exe = PathUtil.Normalize(m.Groups[1].Value);
                    if (exe != null && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !PathUtil.IsUnder(exe, KnownDirs.Windows))
                        set.Add(exe);
                }
            }
            return set.ToList();
        }

        // ------------------------------------------------------------------ Riga di comando → file eseguito

        private static readonly Regex HostExes = new Regex(@"^(rundll32|regsvr32|cmd|powershell|pwsh|wscript|cscript|mshta|conhost|explorer)\.exe$", RegexOptions.IgnoreCase);
        private static readonly Regex ArgPath = new Regex(@"[A-Za-z]:\\[^""|<>*?\r\n]+?\.(exe|dll|bat|cmd|vbs|js|ps1)\b", RegexOptions.IgnoreCase);

        /// <summary>
        /// Il file davvero eseguito da una riga di comando: l'eseguibile, oppure lo script o la DLL passati
        /// a un programma di Windows (rundll32, powershell, wscript...).
        /// </summary>
        public static string TargetOfCommand(string command, bool wow32 = false)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            string cmd = PathUtil.Expand(command.Trim(), wow32);
            string exe = PathUtil.Normalize(PathUtil.ExeFromCommand(cmd));
            string file = exe == null ? Path.GetFileName(PathUtil.ExeFromCommand(cmd) ?? "") : Path.GetFileName(exe);
            if (HostExes.IsMatch(file ?? ""))
            {
                string rest = cmd.Substring(Math.Min(cmd.Length, cmd.IndexOf(file, StringComparison.OrdinalIgnoreCase) + file.Length));
                var m = ArgPath.Match(rest);
                if (m.Success) return PathUtil.Normalize(m.Value);
            }
            return exe;
        }

        // ------------------------------------------------------------------ Avvio automatico

        public static readonly string[] RunKeys =
        {
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce",
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
            @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce",
        };

        public static readonly string[] StartupApprovedKeys =
        {
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
        };

        /// <summary>Programmi avviati con Windows (chiavi Run e RunOnce).</summary>
        public static List<RegTrace> RunEntries()
        {
            var list = new List<RegTrace>();
            foreach (var key in RunKeys)
            {
                bool wow = RegUtil.IsWow32(key);
                using (var k = RegUtil.Open(key))
                {
                    foreach (var v in RegUtil.ValueNames(k))
                    {
                        if (v.Length == 0) continue;
                        string target = TargetOfCommand(RegUtil.GetPath(k, v, wow), wow);
                        if (target != null) list.Add(new RegTrace { KeyPath = key, ValueName = v, ExePath = target, What = L.T("avvio automatico con Windows") });
                    }
                }
            }
            return list;
        }

        /// <summary>Stato "attivo/disattivato" dell'avvio automatico (Gestione attività) per la voce con questo nome.</summary>
        public static List<RegTrace> StartupApproved(string valueName)
        {
            var list = new List<RegTrace>();
            foreach (var key in StartupApprovedKeys)
                if (RegUtil.ValueExists(key, valueName))
                    list.Add(new RegTrace { KeyPath = key, ValueName = valueName, What = L.T("stato dell'avvio automatico (Gestione attività)") });
            return list;
        }

        /// <summary>Tutte le voci di stato dell'avvio automatico, con la chiave in cui si trovano.</summary>
        public static List<RegTrace> AllStartupApproved()
        {
            var list = new List<RegTrace>();
            foreach (var key in StartupApprovedKeys)
                using (var k = RegUtil.Open(key))
                    foreach (var v in RegUtil.ValueNames(k))
                        if (v.Length > 0) list.Add(new RegTrace { KeyPath = key, ValueName = v, What = L.T("stato dell'avvio automatico (Gestione attività)") });
            return list;
        }

        // ------------------------------------------------------------------ Attività pianificate

        /// <summary>
        /// Attività dell'Utilità di pianificazione con i programmi che avviano, escluse quelle di Windows (\Microsoft\...).
        /// Si usa l'interfaccia COM ufficiale, che legge anche le attività nascoste.
        /// </summary>
        public static List<TaskInfo> ScheduledTasks()
        {
            var list = new List<TaskInfo>();
            try
            {
                var type = Type.GetTypeFromProgID("Schedule.Service");
                if (type == null) return list;
                dynamic svc = Activator.CreateInstance(type);
                svc.Connect();
                string user = Environment.UserName;
                CollectTasks(svc.GetFolder("\\"), list, user, 0);
            }
            catch (Exception ex) { Log.Error("Lettura attività pianificate", ex); }
            return list;
        }

        /// <summary>Programmi avviati da una singola attività (null se l'attività non esiste o non si può leggere).</summary>
        public static List<string> TaskTargets(string taskPath)
        {
            try
            {
                var type = Type.GetTypeFromProgID("Schedule.Service");
                if (type == null) return null;
                dynamic svc = Activator.CreateInstance(type);
                svc.Connect();
                int slash = taskPath.LastIndexOf('\\');
                dynamic folder = svc.GetFolder(slash <= 0 ? "\\" : taskPath.Substring(0, slash));
                dynamic t = folder.GetTask(taskPath.Substring(slash + 1));
                var list = new List<string>();
                foreach (dynamic a in t.Definition.Actions)
                {
                    if ((int)a.Type != 0) continue;
                    string exePath = ((string)a.Path ?? "").Trim().Trim('"');
                    if (exePath.Length == 0) continue;
                    string target = TargetOfCommand("\"" + exePath + "\" " + (string)a.Arguments);
                    list.Add(target ?? exePath);
                }
                return list;
            }
            catch { return null; }
        }

        private static void CollectTasks(dynamic folder, List<TaskInfo> list, string user, int depth)
        {
            string path = folder.Path;
            if (path.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase) || depth > 8) return;
            try
            {
                foreach (dynamic t in folder.GetTasks(1 /* TASK_ENUM_HIDDEN */))
                {
                    try
                    {
                        var info = new TaskInfo { Path = t.Path };
                        dynamic def = t.Definition;
                        foreach (dynamic a in def.Actions)
                        {
                            if ((int)a.Type != 0) continue; // solo "Avvia programma"
                            string exePath = ((string)a.Path ?? "").Trim().Trim('"');
                            if (exePath.Length == 0) continue;
                            string target = TargetOfCommand("\"" + exePath + "\" " + (string)a.Arguments);
                            if (target != null) info.Exes.Add(target);
                        }
                        string uid = def.Principal.UserId ?? "";
                        info.NeedsAdmin = (int)def.Principal.RunLevel != 0 || !(uid.EndsWith("\\" + user, StringComparison.OrdinalIgnoreCase)
                                                                              || uid.Equals(user, StringComparison.OrdinalIgnoreCase));
                        list.Add(info);
                    }
                    catch { }
                }
                foreach (dynamic sub in folder.GetFolders(0)) CollectTasks(sub, list, user, depth + 1);
            }
            catch { }
        }

        // ------------------------------------------------------------------ Servizi

        public const string ServicesRoot = @"HKLM\SYSTEM\CurrentControlSet\Services";

        /// <summary>Servizi con un proprio eseguibile fuori da Windows (niente driver, niente servizi ospitati da svchost).</summary>
        public static List<ServiceInfo> Services()
        {
            var list = new List<ServiceInfo>();
            using (var rk = RegUtil.Open(ServicesRoot))
            {
                foreach (var name in RegUtil.SubKeys(rk))
                {
                    try
                    {
                        using (var k = rk.OpenSubKey(name))
                        {
                            if (k == null) continue;
                            object type = k.GetValue("Type");
                            if (!(type is int) || ((int)type & 0x30) == 0) continue;
                            string image = RegUtil.GetString(k, "ImagePath");
                            if (string.IsNullOrWhiteSpace(image) || image.StartsWith("\\SystemRoot", StringComparison.OrdinalIgnoreCase)
                                || image.StartsWith("system32", StringComparison.OrdinalIgnoreCase)) continue;
                            string exe = PathUtil.Normalize(PathUtil.ExeFromCommand(image));
                            if (exe == null || PathUtil.IsUnder(exe, KnownDirs.Windows)) continue;
                            string display = RegUtil.GetString(k, "DisplayName");
                            if (display != null && display.StartsWith("@")) display = Native.LoadIndirectString(display) ?? display;
                            object start = k.GetValue("Start");
                            list.Add(new ServiceInfo { Name = name, DisplayName = display, ExePath = exe, Start = start is int ? (int)start : 3 });
                        }
                    }
                    catch { }
                }
            }
            return list;
        }

        // ------------------------------------------------------------------ Associazioni di file e componenti COM

        private static readonly string[] ClassRoots = { @"HKCU\Software\Classes", @"HKLM\SOFTWARE\Classes" };

        /// <summary>Chiavi di primo livello di Classes che non sono tipi di file di un programma.</summary>
        private static readonly HashSet<string> SystemClassKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CLSID", "Interface", "TypeLib", "AppID", "Wow6432Node", "Installer", "Local Settings", "MIME", "SystemFileAssociations", "PackagedCom",
            "ActivatableClasses", "Extensions", "DirectShow", "Media Type", "MediaFoundation", "Record", "Component Categories", "FileType", "Applications",
            "*", "AllFilesystemObjects", "Directory", "Drive", "Folder", "DesktopBackground", "Network", "NetworkExplorerPlugins", "Printers",
            "exefile", "lnkfile", "batfile", "cmdfile", "comfile", "piffile", "scrfile", "htmlfile", "mhtmlfile", "http", "https", "ftp", "mailto",
            "InternetShortcut", "Unknown", "CompressedFolder", "txtfile", "regfile", "inffile", "inifile", "jsfile", "JSEFile", "VBSFile", "VBEFile",
            "WSFFile", "WSHFile", "Msi.Package", "Msi.Patch", "cplfile", "dllfile", "sysfile", "ocxfile", "themefile", "fonfile", "ttffile", "otffile",
            "Microsoft.PowerShellScript.1", "Microsoft.PowerShellData.1", "Microsoft.PowerShellModule.1", "ms-settings", "search-ms", "shell",
            "Launcher.ImmersiveApplication", "AudioCD", "DVD", "CABFolder", "SearchFolder", "Stack", "StartMenuFolder", "UserLibraryFolder",
        };

        private static bool IsSystemClassKey(string name)
        {
            return name.StartsWith(".") || SystemClassKeys.Contains(name) || name.StartsWith("AppX", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ms-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("{", StringComparison.Ordinal);
        }

        /// <summary>
        /// Tipi di file (ProgID) e registrazioni "Applications\programma.exe" con i comandi che avviano:
        /// dicono a quale programma appartengono le associazioni dei file.
        /// </summary>
        public static List<RegTrace> ClassCommands()
        {
            var list = new List<RegTrace>();
            foreach (var root in ClassRoots)
            {
                using (var rk = RegUtil.Open(root))
                {
                    foreach (var name in RegUtil.SubKeys(rk))
                    {
                        if (IsSystemClassKey(name)) continue;
                        AddClassCommands(rk, name, root + "\\" + name, name, L.T("tipo di file (ProgID)"), list);
                    }
                }
                using (var apps = RegUtil.Open(root + @"\Applications"))
                {
                    foreach (var name in RegUtil.SubKeys(apps))
                        AddClassCommands(apps, name, root + @"\Applications\" + name, name, L.T("registrazione del programma per \"Apri con\""), list);
                }
            }
            return list;
        }

        private static void AddClassCommands(RegistryKey parent, string name, string keyPath, string tag, string what, List<RegTrace> list)
        {
            try
            {
                using (var k = parent.OpenSubKey(name))
                using (var shell = k == null ? null : k.OpenSubKey("shell"))
                {
                    if (k == null) return;
                    int found = 0;
                    foreach (var verb in RegUtil.SubKeys(shell))
                    {
                        using (var cmd = shell.OpenSubKey(verb + "\\command"))
                        {
                            string target = cmd == null ? null : TargetOfCommand(RegUtil.GetString(cmd, ""));
                            if (target == null) continue;
                            list.Add(new RegTrace { KeyPath = keyPath, ExePath = target, What = what, Tag = tag });
                            found++;
                        }
                    }
                    if (found > 0) return;
                    // tipi di file che hanno solo l'icona del programma
                    using (var icon = k.OpenSubKey("DefaultIcon"))
                    {
                        string ic = icon == null ? null : RegUtil.GetString(icon, "");
                        string path = ic == null ? null : PathUtil.Normalize(ic.Split(',')[0]);
                        if (path != null) list.Add(new RegTrace { KeyPath = keyPath, ExePath = path, What = L.F("{0} (solo icona)", what), Tag = tag });
                    }
                }
            }
            catch { }
        }

        /// <summary>Tipi di Windows a cui i programmi aggiungono voci nel menu contestuale ("Apri con Code", "Git Bash Here"...).</summary>
        public static readonly string[] VerbTargets = { "*", "Directory", @"Directory\Background", "Folder", "Drive", "AllFilesystemObjects", "DesktopBackground", @"LibraryFolder\background" };

        /// <summary>Voci aggiunte ai menu contestuali di Esplora risorse, con il programma che avviano.</summary>
        public static List<RegTrace> ShellVerbs()
        {
            var list = new List<RegTrace>();
            foreach (var root in ClassRoots)
            {
                var targets = VerbTargets.ToList();
                using (var sfa = RegUtil.Open(root + @"\SystemFileAssociations"))
                    foreach (var ext in RegUtil.SubKeys(sfa)) targets.Add(@"SystemFileAssociations\" + ext);
                foreach (var target in targets)
                {
                    string shellKey = root + "\\" + target + "\\shell";
                    using (var shell = RegUtil.Open(shellKey))
                    {
                        foreach (var verb in RegUtil.SubKeys(shell))
                        {
                            using (var vk = OpenSub(shell, verb))
                            using (var cmd = vk == null ? null : OpenSub(vk, "command"))
                            {
                                string exe = cmd == null ? null : TargetOfCommand(RegUtil.GetString(cmd, ""));
                                if (exe == null || PathUtil.IsUnder(exe, KnownDirs.Windows)) continue;
                                string label = RegUtil.GetString(vk, "MUIVerb") ?? RegUtil.GetString(vk, "") ?? verb;
                                if (label.StartsWith("@")) label = Native.LoadIndirectString(label) ?? verb;
                                list.Add(new RegTrace { KeyPath = shellKey + "\\" + verb, ExePath = exe, What = L.T("voce nel menu contestuale"), Tag = label.Replace("&", "") });
                            }
                        }
                    }
                }
            }
            return list;
        }

        private static readonly string[] ClsidRoots =
        {
            @"HKCU\Software\Classes\CLSID", @"HKLM\SOFTWARE\Classes\CLSID", @"HKLM\SOFTWARE\WOW6432Node\Classes\CLSID",
        };

        /// <summary>Componenti COM (estensioni di Esplora risorse, gestori di anteprima...) con il file che li implementa.</summary>
        public static List<RegTrace> ComServers()
        {
            var list = new List<RegTrace>();
            foreach (var root in ClsidRoots)
            {
                bool wow = RegUtil.IsWow32(root);
                using (var rk = RegUtil.Open(root))
                {
                    foreach (var clsid in RegUtil.SubKeys(rk))
                    {
                        if (!clsid.StartsWith("{")) continue;
                        try
                        {
                            foreach (var server in new[] { "InprocServer32", "LocalServer32" })
                            {
                                using (var k = rk.OpenSubKey(clsid + "\\" + server))
                                {
                                    if (k == null) continue;
                                    string raw = RegUtil.GetPath(k, "", wow);
                                    string path = server == "LocalServer32" ? TargetOfCommand(raw, wow) : PathUtil.Normalize(raw);
                                    if (path == null || PathUtil.IsUnder(path, KnownDirs.Windows)) continue;
                                    list.Add(new RegTrace { KeyPath = root + "\\" + clsid, ExePath = path, What = L.T("componente COM"), Tag = clsid.ToUpperInvariant() });
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            return list;
        }

        private static readonly string[] ShellExTargets = { "*", "Directory", @"Directory\Background", "Folder", "Drive", "AllFilesystemObjects", "DesktopBackground", "lnkfile", "exefile" };
        private static readonly string[] ShellExKinds = { "ContextMenuHandlers", "DragDropHandlers", "PropertySheetHandlers", "CopyHookHandlers", "IconOverlayHandlers" };

        /// <summary>
        /// Voci che attivano i componenti COM indicati nei menu di Esplora risorse (menu contestuale, sovrimpressioni delle icone)
        /// e nell'elenco delle estensioni approvate.
        /// </summary>
        public static List<RegTrace> ShellExtensionRefs(ICollection<string> clsids)
        {
            var list = new List<RegTrace>();
            if (clsids.Count == 0) return list;
            var set = new HashSet<string>(clsids, StringComparer.OrdinalIgnoreCase);
            foreach (var root in ClassRoots)
            {
                foreach (var target in ShellExTargets)
                {
                    foreach (var kind in ShellExKinds)
                    {
                        string key = root + "\\" + target + "\\shellex\\" + kind;
                        using (var rk = RegUtil.Open(key))
                        {
                            foreach (var name in RegUtil.SubKeys(rk))
                            {
                                string clsid = name.StartsWith("{") ? name : null;
                                using (var k = OpenSub(rk, name)) if (k != null) clsid = RegUtil.GetString(k, "") ?? clsid;
                                if (clsid != null && set.Contains(clsid.Trim()))
                                    list.Add(new RegTrace { KeyPath = key + "\\" + name, What = L.T("voce nel menu di Esplora risorse"), Tag = clsid.Trim().ToUpperInvariant() });
                            }
                        }
                    }
                }
            }
            foreach (var key in new[] { @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers",
                                        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers" })
            {
                using (var rk = RegUtil.Open(key))
                {
                    foreach (var name in RegUtil.SubKeys(rk))
                    {
                        using (var k = OpenSub(rk, name))
                        {
                            string clsid = k == null ? null : RegUtil.GetString(k, "");
                            if (clsid != null && set.Contains(clsid.Trim()))
                                list.Add(new RegTrace { KeyPath = key + "\\" + name, What = L.T("sovrimpressione sulle icone di Esplora risorse"), Tag = clsid.Trim().ToUpperInvariant() });
                        }
                    }
                }
            }
            foreach (var key in new[] { @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved",
                                        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved",
                                        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved" })
            {
                using (var k = RegUtil.Open(key))
                    foreach (var v in RegUtil.ValueNames(k))
                        if (set.Contains(v.Trim()))
                            list.Add(new RegTrace { KeyPath = key, ValueName = v, What = L.T("estensione approvata di Esplora risorse"), Tag = v.Trim().ToUpperInvariant() });
            }
            return list;
        }

        /// <summary>Riferimenti "Apri con" ai ProgID indicati (valori OpenWithProgids delle estensioni).</summary>
        public static List<RegTrace> OpenWithProgIds(ICollection<string> progIds)
        {
            var list = new List<RegTrace>();
            if (progIds.Count == 0) return list;
            var set = new HashSet<string>(progIds, StringComparer.OrdinalIgnoreCase);
            var roots = ClassRoots.Concat(new[] { @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts" });
            foreach (var root in roots)
            {
                using (var rk = RegUtil.Open(root))
                {
                    foreach (var ext in RegUtil.SubKeys(rk))
                    {
                        if (!ext.StartsWith(".")) continue;
                        string key = root + "\\" + ext + "\\OpenWithProgids";
                        using (var k = OpenSub(rk, ext + "\\OpenWithProgids"))
                            foreach (var v in RegUtil.ValueNames(k))
                                if (set.Contains(v))
                                    list.Add(new RegTrace { KeyPath = key, ValueName = v, What = L.F("voce \"Apri con\" per i file {0}", ext), Tag = v });
                    }
                }
            }
            return list;
        }

        /// <summary>Tutti i valori OpenWithProgids (per trovare quelli che puntano a ProgID che non esistono più).</summary>
        public static List<RegTrace> AllOpenWithProgIds()
        {
            var list = new List<RegTrace>();
            foreach (var root in ClassRoots.Concat(new[] { @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts" }))
            {
                using (var rk = RegUtil.Open(root))
                {
                    foreach (var ext in RegUtil.SubKeys(rk))
                    {
                        if (!ext.StartsWith(".")) continue;
                        using (var k = OpenSub(rk, ext + "\\OpenWithProgids"))
                            foreach (var v in RegUtil.ValueNames(k))
                                if (v.Length > 0) list.Add(new RegTrace { KeyPath = root + "\\" + ext + "\\OpenWithProgids", ValueName = v, What = L.F("voce \"Apri con\" per i file {0}", ext), Tag = v });
                    }
                }
            }
            return list;
        }

        /// <summary>True se il ProgID esiste (per l'utente o per il computer).</summary>
        public static bool ProgIdExists(string progId)
        {
            return ClassRoots.Any(r => RegUtil.KeyExists(r + "\\" + progId)) || RegUtil.KeyExists(@"HKLM\SOFTWARE\WOW6432Node\Classes\" + progId);
        }

        /// <summary>
        /// Programmi registrati come "app predefinite" (RegisteredApplications): il valore punta alla chiave Capabilities,
        /// da cui si ricava l'eseguibile.
        /// </summary>
        public static List<RegTrace> RegisteredApplications()
        {
            var list = new List<RegTrace>();
            foreach (var root in new[] { @"HKLM\SOFTWARE\RegisteredApplications", @"HKCU\Software\RegisteredApplications", @"HKLM\SOFTWARE\WOW6432Node\RegisteredApplications" })
            {
                string hive = root.Substring(0, 4);
                using (var k = RegUtil.Open(root))
                {
                    foreach (var v in RegUtil.ValueNames(k))
                    {
                        string caps = RegUtil.GetString(k, v);
                        // le registrazioni delle app dello Store le gestisce Windows
                        if (string.IsNullOrEmpty(caps) || v.StartsWith("AppX", StringComparison.OrdinalIgnoreCase)
                            || caps.IndexOf(@"\AppModel\", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        string capsKey = hive + "\\" + caps.TrimStart('\\');
                        string exe = null;
                        bool exists = false;
                        using (var ck = RegUtil.OpenAnyView(capsKey))
                        {
                            exists = ck != null;
                            string icon = ck == null ? null : RegUtil.GetString(ck, "ApplicationIcon");
                            if (icon != null) exe = PathUtil.Normalize(icon.Split(',')[0].Trim('"'));
                        }
                        list.Add(new RegTrace { KeyPath = root, ValueName = v, ExePath = exe, What = L.T("registrazione tra le app predefinite"), Tag = exists ? capsKey : null });
                    }
                }
            }
            return list;
        }

        /// <summary>Client registrati (browser, posta, lettori multimediali) con il comando che avviano.</summary>
        public static List<RegTrace> Clients()
        {
            var list = new List<RegTrace>();
            foreach (var root in new[] { @"HKLM\SOFTWARE\Clients", @"HKLM\SOFTWARE\WOW6432Node\Clients", @"HKCU\Software\Clients" })
            {
                bool wow = RegUtil.IsWow32(root);
                using (var rk = RegUtil.Open(root))
                {
                    foreach (var type in RegUtil.SubKeys(rk))
                    {
                        using (var tk = OpenSub(rk, type))
                        {
                            foreach (var app in RegUtil.SubKeys(tk))
                            {
                                using (var cmd = OpenSub(tk, app + @"\shell\open\command"))
                                {
                                    string target = cmd == null ? null : TargetOfCommand(RegUtil.GetPath(cmd, "", wow), wow);
                                    if (target != null)
                                        list.Add(new RegTrace { KeyPath = root + "\\" + type + "\\" + app, ExePath = target, What = L.F("registrazione come programma predefinito ({0})", type) });
                                }
                            }
                        }
                    }
                }
            }
            return list;
        }

        private static RegistryKey OpenSub(RegistryKey parent, string name)
        {
            try { return parent == null ? null : parent.OpenSubKey(name); }
            catch { return null; }
        }

        // ------------------------------------------------------------------ Variabile PATH

        public const string UserEnvironment = @"HKCU\Environment";
        public const string SystemEnvironment = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

        /// <summary>Le cartelle elencate nella variabile PATH dell'utente e di sistema.</summary>
        public static List<PathEntryInfo> PathEntries()
        {
            var list = new List<PathEntryInfo>();
            foreach (var key in new[] { UserEnvironment, SystemEnvironment })
            {
                using (var k = RegUtil.Open(key))
                {
                    if (k == null) continue;
                    string name = RegUtil.ValueNames(k).FirstOrDefault(v => v.Equals("Path", StringComparison.OrdinalIgnoreCase));
                    if (name == null) continue;
                    string raw = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (raw == null) continue;
                    foreach (var part in raw.Split(';'))
                    {
                        string p = part.Trim();
                        if (p.Length == 0) continue;
                        list.Add(new PathEntryInfo { KeyPath = key, ValueName = name, Raw = p, Expanded = PathUtil.Normalize(p) });
                    }
                }
            }
            return list;
        }

        // ------------------------------------------------------------------ Collegamenti

        public static List<ShortcutInfo> Shortcuts()
        {
            var list = new List<ShortcutInfo>();
            foreach (var f in InstalledApps.ShortcutFiles())
            {
                var s = new ShortcutInfo { File = f };
                if (f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string target, args;
                    if (Native.ReadShortcut(f, out target, out args)) s.Target = PathUtil.Normalize(target);
                }
                else
                {
                    try
                    {
                        foreach (var line in File.ReadLines(f))
                        {
                            if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) s.Url = line.Substring(4).Trim();
                            else if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) s.IconFile = PathUtil.Normalize(line.Substring(9));
                        }
                    }
                    catch { }
                }
                list.Add(s);
            }
            return list;
        }

        /// <summary>Estrae l'AppID da un collegamento Steam (steam://rungameid/123 o steam://run/123).</summary>
        public static string SteamIdFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var m = Regex.Match(url, @"^steam://(rungameid|run|launch)/(\d+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[2].Value : null;
        }

        // ------------------------------------------------------------------ Cartelle dei dati

        private static IEnumerable<string> AppIdSaveCandidates()
        {
            string r = KnownDirs.RoamingAppData, l = KnownDirs.LocalAppData, d = KnownDirs.Documents, pub = KnownDirs.PublicDocuments;
            foreach (var n in new[] { "Goldberg SteamEmu Saves", "GSE Saves", @"Steam\CODEX", @"Steam\RUNE", "EMPRESS", "RUNE", "CODEX", "TENOKE", "SmartSteamEmu", "CreamAPI", "OnlineFix", "SKIDROW", "3DMGAME", "ALI213", "Razor1911", "PLAZA", "HOODLUM", "FLT" })
                yield return Path.Combine(r, n);
            foreach (var n in new[] { @"Steam\CODEX", @"Steam\RUNE", "EMPRESS", "OnlineFix", "CPY_SAVES", "TENOKE", "PLAZA" })
                yield return Path.Combine(pub, n);
            foreach (var n in new[] { "SKIDROW", "CODEX", "RUNE", "RLD!", "3DMGAME" })
                yield return Path.Combine(l, n);
            foreach (var n in new[] { "CPY_SAVES", "SKIDROW", "CODEX", "RUNE", "EMPRESS", "3DMGAME", "ALI213", "RELOADED", "PLAZA", "VALVE", "SteamEmu", "DODI", "FitGirl" })
                yield return Path.Combine(d, n);
            foreach (var d2 in PathUtil.Dirs(Path.Combine(KnownDirs.ProgramData, "Steam"))) yield return d2;
            foreach (var d2 in PathUtil.Dirs(Path.Combine(pub, "Steam"))) yield return d2;
        }

        /// <summary>Cartelle in cui alcuni giochi (e gli emulatori delle API di Steam) salvano per AppID.</summary>
        public static List<string> AppIdSaveRoots()
        {
            return AppIdSaveCandidates().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Cartelle degli emulatori di altri servizi: dentro non c'è un AppID, ma un nome scelto dal crack ("GTA_DEF").</summary>
        public static List<string> NamedEmuSaveRoots()
        {
            return new[] { "Goldberg SocialClub Emu Saves", "Goldberg UplayEmu Saves", "NemirtingasEpicEmu", "NemirtingasGalaxyEmu", "Nemirtingas", "GalaxyEmu" }
                .Select(n => Path.Combine(KnownDirs.RoamingAppData, n)).Where(Directory.Exists).ToList();
        }

        /// <summary>Editori che tengono più giochi in una stessa cartella: quella cartella non è mai un residuo o il salvataggio di un solo gioco.</summary>
        public static readonly Regex PublisherFolder = new Regex(
            @"^(rockstar games|electronic arts|ea games|ubisoft|ubisoft game launcher|bethesda.*|cd projekt.*|activision|sega|capcom|bandai.*|square enix|take-?two|2k games?|2k|warner bros.*|konami|focus home.*|thq.*|paradox interactive)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Radici dei dati utente usate per la ricerca per nome, con categoria e descrizione.</summary>
        public static List<Tuple<string, ItemCategory, string>> DataRoots(AppKind kind = AppKind.Game)
        {
            var list = new List<Tuple<string, ItemCategory, string>>();
            if (kind == AppKind.Game)
            {
                list.Add(Tuple.Create(Path.Combine(KnownDirs.MyGames, "UnrealEngine3"), ItemCategory.Saves, @"Documenti\My Games\UnrealEngine3"));
                list.Add(Tuple.Create(KnownDirs.MyGames, ItemCategory.Saves, L.T(@"Documenti\My Games")));
                list.Add(Tuple.Create(KnownDirs.SavedGames, ItemCategory.Saves, L.T("Giochi salvati")));
            }
            list.Add(Tuple.Create(KnownDirs.Documents, ItemCategory.Saves, L.T("Documenti")));
            list.Add(Tuple.Create(KnownDirs.PublicDocuments, ItemCategory.Saves, L.T("Documenti pubblici")));
            list.Add(Tuple.Create(KnownDirs.LocalLow, kind == AppKind.Game ? ItemCategory.Saves : ItemCategory.Settings, @"AppData\LocalLow"));
            list.Add(Tuple.Create(KnownDirs.LocalAppData, ItemCategory.Settings, @"AppData\Local"));
            list.Add(Tuple.Create(KnownDirs.RoamingAppData, ItemCategory.Settings, @"AppData\Roaming"));
            list.Add(Tuple.Create(KnownDirs.ProgramData, ItemCategory.Settings, "ProgramData"));
            list.Add(Tuple.Create(Path.Combine(KnownDirs.VirtualStore, "ProgramData"), ItemCategory.Settings, @"VirtualStore\ProgramData"));
            list.Add(Tuple.Create(KnownDirs.Temp, ItemCategory.Cache, L.T("file temporanei")));
            if (kind == AppKind.Program)
                list.Add(Tuple.Create(KnownDirs.UserProfile, ItemCategory.Settings, L.T("cartella dell'utente")));
            return list;
        }

        /// <summary>Cartelle in cui si installano i programmi: i residui lì dentro si cercano solo dopo la disinstallazione.</summary>
        public static List<Tuple<string, ItemCategory, string>> ProgramRoots()
        {
            return new List<Tuple<string, ItemCategory, string>>
            {
                Tuple.Create(KnownDirs.ProgramFiles, ItemCategory.AppFolder, "Program Files"),
                Tuple.Create(KnownDirs.ProgramFilesX86, ItemCategory.AppFolder, "Program Files (x86)"),
                Tuple.Create(KnownDirs.LocalPrograms, ItemCategory.AppFolder, @"AppData\Local\Programs"),
            };
        }

        /// <summary>Nomi di primo livello da non esplorare durante la ricerca per nome.</summary>
        public static readonly HashSet<string> SkipTopLevel = new HashSet<string>(new[]
        {
            "microsoft", "packages", "programs", "temp", "crashdumps", "d3dscache", "virtualstore", "mygames", "nvidia",
            "nvidiacorporation", "google", "mozilla", "windows", "windowsapps", "packagecache", "publishers", "comms",
            "connecteddevicesplatform", "elevateddiagnostics", "identityservice", "cronologia", "datiapplicazioni", "history",
            "temporaryinternetfiles", "applicationdata", "desktopini", "intel", "amd", "unity", "unrealengine", "crashreportclient",
            "steam", "epicgameslauncher", "adobe", "apple", "npmcache", "pip", "python", "jetbrains", "github", "githubdesktop",
            "immagini", "musica", "video", "documents", "mymusic", "mypictures", "myvideos", "usoshared", "ssh", "regid19918com.microsoft",
            "appdata", "desktop", "downloads", "onedrive", "favorites", "links", "contacts", "searches", "savedgames", "music", "pictures",
            "videos", "3dobjects", "commonfiles", "windowspowershell", "internetexplorer", "windowsdefender", "windowsmail",
            "windowsmediaplayer", "windowsnt", "windowsphotoviewer", "windowssidebar", "windowsportabledevices", "referenceassemblies",
            "msbuild", "uninstallinformation", "modifiablewindowsapps", "dotnet", "windowskits", "microsoftnet", "packagemanagement",
            "squirreltemp", "installshieldinstallationinformation", "installer", "installers", "downloadedinstallations",
        });

        /// <summary>Cartelle di acquisizione video/screenshot per gioco (Game Bar, NVIDIA, AMD).</summary>
        public static List<string> MediaRoots()
        {
            return new List<string>
            {
                KnownDirs.Videos, Path.Combine(KnownDirs.Videos, "NVIDIA"), Path.Combine(KnownDirs.Videos, "Radeon ReLive"),
                KnownDirs.Pictures, Path.Combine(KnownDirs.Pictures, "UbisoftConnect"), Path.Combine(KnownDirs.Pictures, "Screenshots"),
            }.Where(Directory.Exists).ToList();
        }

        private static readonly Regex CaptureTitle = new Regex(@"^(.*?)\s+\d{1,4}[-_.]\d{1,2}[-_.]\d{1,4}[\s_]+\d{1,2}[-_.]\d{1,2}[-_.]\d{1,2}", RegexOptions.Compiled);

        /// <summary>Titolo della finestra ricavato dal nome di un file della Game Bar ("Gioco 2024-05-02 21-34-12.png").</summary>
        public static string CaptureTitleOf(string file)
        {
            var m = CaptureTitle.Match(Path.GetFileNameWithoutExtension(file));
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        /// <summary>Cartelle dei report di errore di Windows (WER).</summary>
        public static List<string> WerRoots()
        {
            return new[]
            {
                Path.Combine(KnownDirs.ProgramData, @"Microsoft\Windows\WER\ReportArchive"),
                Path.Combine(KnownDirs.ProgramData, @"Microsoft\Windows\WER\ReportQueue"),
                Path.Combine(KnownDirs.LocalAppData, @"Microsoft\Windows\WER\ReportArchive"),
                Path.Combine(KnownDirs.LocalAppData, @"Microsoft\Windows\WER\ReportQueue"),
            }.Where(Directory.Exists).ToList();
        }

        /// <summary>Legge il percorso dell'applicazione da un report WER (Report.wer, UTF-16).</summary>
        public static string WerAppPath(string reportDir)
        {
            try
            {
                string f = Path.Combine(reportDir, "Report.wer");
                if (!File.Exists(f)) return null;
                foreach (var line in File.ReadLines(f))
                    if (line.StartsWith("AppPath=", StringComparison.OrdinalIgnoreCase)) return line.Substring(8).Trim();
            }
            catch { }
            return null;
        }
    }
}
