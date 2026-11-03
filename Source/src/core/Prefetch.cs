using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace AppManager
{
    /// <summary>
    /// Un file Prefetch di Windows (C:\Windows\Prefetch\*.pf): per ogni eseguibile avviato elenca
    /// i file e le cartelle usati nei primi secondi. Da qui ricaviamo dove un gioco tiene i suoi dati.
    /// </summary>
    internal sealed class PrefetchRecord
    {
        public string PfFile;
        public string ExeName;
        public string ExePath;
        public bool ExeExists;
        public DateTime? LastRun;
        public int RunCount;
        public readonly HashSet<string> Anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> MarkerHits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string OwnerKey { get { return PfFile.ToLowerInvariant(); } }
    }

    internal static class PrefetchIndex
    {
        private static readonly object Sync = new object();
        private static List<PrefetchRecord> records;
        private static Dictionary<string, HashSet<string>> owners;
        private static Dictionary<uint, string> serialToDrive;
        private static Dictionary<string, string> deviceToDrive;

        public static string Folder { get { return Path.Combine(KnownDirs.Windows, "Prefetch"); } }

        public static bool IsAvailable
        {
            get
            {
                try { Directory.GetFiles(Folder, "*.pf").Take(1).ToList(); return true; }
                catch { return false; }
            }
        }

        public static void Reset()
        {
            lock (Sync) { records = null; owners = null; }
        }

        public static List<PrefetchRecord> Records
        {
            get
            {
                lock (Sync)
                {
                    if (records == null) Load();
                    return records;
                }
            }
        }

        /// <summary>Per ogni cartella "àncora": gli eseguibili (file .pf) che l'hanno usata.</summary>
        public static Dictionary<string, HashSet<string>> AnchorOwners
        {
            get
            {
                lock (Sync)
                {
                    if (records == null) Load();
                    return owners;
                }
            }
        }

        private static void Load()
        {
            records = new List<PrefetchRecord>();
            owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (!AppSettings.Current.UsePrefetch) return;
            string[] files;
            try { files = Directory.GetFiles(Folder, "*.pf"); }
            catch { return; }
            BuildDriveMaps();
            foreach (var f in files)
            {
                try
                {
                    var r = Parse(f);
                    if (r == null) continue;
                    records.Add(r);
                    foreach (var a in r.Anchors)
                    {
                        HashSet<string> set;
                        if (!owners.TryGetValue(a, out set)) owners[a] = set = new HashSet<string>();
                        set.Add(r.OwnerKey);
                    }
                }
                catch { }
            }
            Log.Info("Prefetch: letti {0} file su {1}", records.Count, files.Length);
        }

        // ------------------------------------------------------------------ Mappatura volumi → lettere di unità

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint QueryDosDevice(string device, StringBuilder target, int max);

        private static void BuildDriveMaps()
        {
            serialToDrive = new Dictionary<uint, string>();
            deviceToDrive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in DriveInfo.GetDrives())
            {
                string letter = d.Name.Substring(0, 2);
                uint? serial = Native.GetVolumeSerial(d.Name);
                if (serial.HasValue && !serialToDrive.ContainsKey(serial.Value)) serialToDrive[serial.Value] = letter;
                var sb = new StringBuilder(260);
                if (QueryDosDevice(letter, sb, sb.Capacity) != 0) deviceToDrive[sb.ToString()] = letter;
            }
        }

        private static readonly Regex VolumeRx = new Regex(@"^\\VOLUME\{[0-9a-f]+-([0-9a-f]{8})\}", RegexOptions.IgnoreCase);

        private static string ToDrivePath(string ntPath)
        {
            if (string.IsNullOrEmpty(ntPath)) return null;
            if (serialToDrive == null) BuildDriveMaps();
            var m = VolumeRx.Match(ntPath);
            if (m.Success)
            {
                uint serial = Convert.ToUInt32(m.Groups[1].Value, 16);
                string letter;
                if (!serialToDrive.TryGetValue(serial, out letter)) return null;
                return letter + ntPath.Substring(m.Length);
            }
            foreach (var kv in deviceToDrive)
                if (ntPath.StartsWith(kv.Key + "\\", StringComparison.OrdinalIgnoreCase))
                    return kv.Value + ntPath.Substring(kv.Key.Length);
            return null;
        }

        // ------------------------------------------------------------------ Parsing

        public static PrefetchRecord Parse(string pfFile)
        {
            byte[] raw = File.ReadAllBytes(pfFile);
            byte[] data = raw;
            if (raw.Length > 8 && raw[0] == 'M' && raw[1] == 'A' && raw[2] == 'M')
            {
                int size = BitConverter.ToInt32(raw, 4);
                bool crc = (raw[3] & 0x80) != 0;
                data = Native.Decompress((ushort)(raw[3] & 0x0F), raw, crc ? 12 : 8, size);
                if (data == null) return null;
            }
            if (data.Length < 0xD4 || data[4] != 'S' || data[5] != 'C' || data[6] != 'C' || data[7] != 'A') return null;
            int version = BitConverter.ToInt32(data, 0);
            if (version < 23) return null;

            var r = new PrefetchRecord { PfFile = Path.GetFileName(pfFile) };
            r.ExeName = ReadFixedString(data, 0x10, 60);
            long ft = BitConverter.ToInt64(data, 0x80);
            if (ft > 0) try { r.LastRun = DateTime.FromFileTime(ft); } catch { }
            r.RunCount = BitConverter.ToInt32(data, 0xD0);

            var paths = new List<string>();
            int fnOff = BitConverter.ToInt32(data, 0x64), fnSize = BitConverter.ToInt32(data, 0x68);
            if (fnOff > 0 && fnSize > 0 && fnOff + fnSize <= data.Length)
                paths.AddRange(Encoding.Unicode.GetString(data, fnOff, fnSize).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries));

            int volOff = BitConverter.ToInt32(data, 0x6C), volCount = BitConverter.ToInt32(data, 0x70);
            int entrySize = version >= 30 ? 96 : 104;
            for (int v = 0; v < volCount && v < 16; v++)
            {
                int e = volOff + v * entrySize;
                if (e + 0x24 > data.Length) break;
                int dirOff = BitConverter.ToInt32(data, e + 0x1C), dirCount = BitConverter.ToInt32(data, e + 0x20);
                int p = volOff + dirOff;
                for (int i = 0; i < dirCount && p + 2 <= data.Length; i++)
                {
                    int chars = BitConverter.ToUInt16(data, p);
                    p += 2;
                    if (p + chars * 2 > data.Length) break;
                    paths.Add(Encoding.Unicode.GetString(data, p, chars * 2));
                    p += chars * 2 + 2;
                }
            }

            foreach (var nt in paths)
            {
                string path = ToDrivePath(nt);
                if (path == null) continue;
                string fileName = Path.GetFileName(path);
                if (r.ExePath == null && r.ExeName.Length > 0 && fileName.StartsWith(r.ExeName, StringComparison.OrdinalIgnoreCase)
                    && fileName.EndsWith(".EXE", StringComparison.OrdinalIgnoreCase))
                    r.ExePath = path;
                if (GameMarkers.IsMarkerFileName(fileName)) r.MarkerHits.Add(fileName.ToLowerInvariant());
                foreach (var a in Anchors(path)) r.Anchors.Add(a);
            }
            if (r.ExePath != null)
            {
                r.ExePath = RealCase(r.ExePath);
                r.ExeExists = File.Exists(r.ExePath);
            }
            return r;
        }

        private static string ReadFixedString(byte[] data, int offset, int bytes)
        {
            string s = Encoding.Unicode.GetString(data, offset, bytes);
            int z = s.IndexOf('\0');
            return z >= 0 ? s.Substring(0, z) : s;
        }

        // ------------------------------------------------------------------ Cartelle "àncora"

        /// <summary>Radici dei dati utente, dalla più specifica alla più generica, con la profondità delle àncore.</summary>
        private static readonly Lazy<List<KeyValuePair<string, int[]>>> Roots = new Lazy<List<KeyValuePair<string, int[]>>>(() =>
        {
            var list = new List<KeyValuePair<string, int[]>>
            {
                new KeyValuePair<string, int[]>(Path.Combine(KnownDirs.MyGames, "UnrealEngine3"), new[] { 1 }),
                new KeyValuePair<string, int[]>(KnownDirs.MyGames, new[] { 1 }),
                new KeyValuePair<string, int[]>(Path.Combine(KnownDirs.LocalLow, "Unity"), new[] { 1 }),
                new KeyValuePair<string, int[]>(Path.Combine(KnownDirs.VirtualStore, "Program Files (x86)"), new[] { 1 }),
                new KeyValuePair<string, int[]>(Path.Combine(KnownDirs.VirtualStore, "Program Files"), new[] { 1 }),
                new KeyValuePair<string, int[]>(KnownDirs.LocalLow, new[] { 1, 2 }),
                new KeyValuePair<string, int[]>(KnownDirs.LocalAppData, new[] { 1, 2 }),
                new KeyValuePair<string, int[]>(KnownDirs.RoamingAppData, new[] { 1, 2 }),
                new KeyValuePair<string, int[]>(KnownDirs.SavedGames, new[] { 1, 2 }),
                new KeyValuePair<string, int[]>(KnownDirs.PublicDocuments, new[] { 1, 2 }),
                new KeyValuePair<string, int[]>(KnownDirs.Documents, new[] { 1, 2 }),
                new KeyValuePair<string, int[]>(KnownDirs.ProgramData, new[] { 1, 2 }),
            };
            return list.Where(kv => kv.Key != null).OrderByDescending(kv => kv.Key.Length).ToList();
        });

        /// <summary>Cartelle condivise da molti programmi: non sono mai dati di un singolo gioco.</summary>
        private static readonly HashSet<string> SharedNames = new HashSet<string>(new[]
        {
            "microsoft", "nvidia", "nvidiacorporation", "d3dscache", "amd", "ati", "intel", "temp", "tmp", "packages", "programs",
            "crashdumps", "unrealengine", "crashreportclient", "epicgameslauncher", "epicgames", "epiconlineservicesuihelper",
            "epiconlineservices", "easyanticheat", "battleye", "steam", "unity", "google", "mozilla", "discord", "overwolf",
            "razer", "logitech", "logishrd", "lghub", "corsair", "msi", "rivatuner", "rtss", "packagecache", "publishers",
            "connecteddevicesplatform", "comms", "isolatedstorage", "peerdistrepub", "cef", "cache", "fontconfig", "pip",
            "npmcache", "elevateddiagnostics", "identityservice", "windows", "windowsapps", "virtualstore", "mygames",
            "obsstudio", "nvidiaapp", "ubisoft", "ubisoftgamelauncher", "electronicarts", "origin", "eadesktop", "battlenet",
            "blizzardentertainment", "gogcom", "goggalaxy", "amazongames", "xbox", "gamingservices", "steamvr", "openvr",
            "oculus", "desktopini", "applicationdata", "datiapplicazioni", "cronologia", "temporaryinternetfiles", "history",
            "microsoftgames", "gfnruntimesdk", "nvidiageforcenow", "squirreltemp", "dotnet", "nuget", "python", "java",
            "vulkan", "mesa", "reshade", "specialk", "dxvk", "modio", "moddb", "overwolfelectron", "medal", "outplayed",
            "engine", "binaries", "shadercache", "pipelinecache",
        });

        public static IEnumerable<string> Anchors(string path)
        {
            foreach (var kv in Roots.Value)
            {
                string root = kv.Key;
                if (!PathUtil.IsUnder(path, root)) continue;
                var parts = path.Substring(root.Length + 1).Split('\\');
                // un file (ultima parte con estensione) non conta come cartella
                int dirParts = parts.Length;
                foreach (int depth in kv.Value)
                {
                    if (depth > dirParts) break;
                    if (depth == dirParts && Path.HasExtension(parts[dirParts - 1]) && dirParts == parts.Length && LooksLikeFile(parts[dirParts - 1])) break;
                    if (SharedNames.Contains(NameUtil.Norm(parts[0]))) break;
                    yield return root + "\\" + string.Join("\\", parts, 0, depth);
                }
                yield break;
            }
        }

        private static bool LooksLikeFile(string name)
        {
            string ext = Path.GetExtension(name);
            return ext.Length >= 2 && ext.Length <= 6 && ext.Skip(1).All(char.IsLetterOrDigit);
        }

        /// <summary>Converte un percorso (in maiuscolo nel Prefetch) nel percorso reale su disco.</summary>
        public static string RealCase(string path)
        {
            return PathUtil.RealCase(path);
        }

        /// <summary>
        /// Cartelle dati usate esclusivamente dagli eseguibili indicati (e da nessun altro programma).
        /// </summary>
        public static List<string> ExclusiveAnchors(ICollection<PrefetchRecord> group)
        {
            var result = new List<string>();
            if (group.Count == 0) return result;
            var mine = new HashSet<string>(group.Select(r => r.OwnerKey));
            var candidates = new HashSet<string>(group.SelectMany(r => r.Anchors), StringComparer.OrdinalIgnoreCase);
            var map = AnchorOwners;
            var exclusive = candidates.Where(a =>
            {
                HashSet<string> set;
                return map.TryGetValue(a, out set) && set.All(mine.Contains) && Directory.Exists(a);
            }).OrderBy(a => a.Length).ToList();
            foreach (var a in exclusive)
            {
                if (result.Any(r => PathUtil.IsUnder(a, r, true))) continue; // già coperta dalla cartella madre
                // una cartella "madre" (es. dell'editore) con altre sottocartelle: si propongono solo le sottocartelle del gioco
                var children = exclusive.Where(c => PathUtil.IsUnder(c, a) && c.Substring(a.Length + 1).IndexOf('\\') < 0).ToList();
                int subdirs = PathUtil.Dirs(a).Length;
                if (children.Count > 0 && subdirs > children.Count)
                {
                    foreach (var c in children) result.Add(c);
                    continue;
                }
                result.Add(a);
            }
            return result.Select(RealCase).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
