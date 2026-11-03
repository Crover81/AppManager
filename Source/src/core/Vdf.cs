using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AppManager
{
    /// <summary>Nodo del formato testuale KeyValues di Steam (.vdf / .acf).</summary>
    internal sealed class VdfNode
    {
        public readonly Dictionary<string, object> Items = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public string Str(string key)
        {
            object v;
            return Items.TryGetValue(key, out v) ? v as string : null;
        }

        public VdfNode Node(string key)
        {
            object v;
            return Items.TryGetValue(key, out v) ? v as VdfNode : null;
        }

        public static VdfNode ParseFile(string path)
        {
            try { return Parse(File.ReadAllText(path, Encoding.UTF8)); }
            catch { return null; }
        }

        public static VdfNode Parse(string text)
        {
            int pos = 0;
            var root = new VdfNode();
            ParseInto(root, text, ref pos);
            return root;
        }

        private static void ParseInto(VdfNode node, string s, ref int pos)
        {
            while (true)
            {
                string key = NextToken(s, ref pos);
                if (key == null || key == "}") return;
                if (key == "{") continue; // struttura malformata: ignora
                string value = NextToken(s, ref pos);
                if (value == null) return;
                if (value == "{")
                {
                    var child = new VdfNode();
                    ParseInto(child, s, ref pos);
                    node.Items[key] = child;
                }
                else if (value == "}")
                {
                    return;
                }
                else
                {
                    node.Items[key] = value;
                }
                // condizioni tipo [$WIN32] dopo un valore
                SkipWhitespace(s, ref pos);
                if (pos < s.Length && s[pos] == '[')
                {
                    int end = s.IndexOf(']', pos);
                    pos = end < 0 ? s.Length : end + 1;
                }
            }
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length)
            {
                if (char.IsWhiteSpace(s[pos])) { pos++; continue; }
                if (s[pos] == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
                {
                    int nl = s.IndexOf('\n', pos);
                    pos = nl < 0 ? s.Length : nl + 1;
                    continue;
                }
                break;
            }
        }

        private static string NextToken(string s, ref int pos)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) return null;
            char c = s[pos];
            if (c == '{' || c == '}') { pos++; return c.ToString(); }
            var sb = new StringBuilder();
            if (c == '"')
            {
                pos++;
                while (pos < s.Length && s[pos] != '"')
                {
                    if (s[pos] == '\\' && pos + 1 < s.Length)
                    {
                        char n = s[pos + 1];
                        sb.Append(n == 'n' ? '\n' : n == 't' ? '\t' : n);
                        pos += 2;
                        continue;
                    }
                    sb.Append(s[pos++]);
                }
                pos++;
                return sb.ToString();
            }
            while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] != '{' && s[pos] != '}' && s[pos] != '"')
                sb.Append(s[pos++]);
            return sb.ToString();
        }
    }

    /// <summary>Informazioni essenziali di un'app Steam lette da appcache\appinfo.vdf.</summary>
    internal sealed class SteamAppInfo
    {
        public uint AppId;
        public string Name;
        public string Type;
        public string InstallDir;
    }

    /// <summary>Lettore del file binario appinfo.vdf (formati v27, v28, v29).</summary>
    internal static class SteamAppInfoReader
    {
        public static Dictionary<uint, SteamAppInfo> Read(string path)
        {
            var result = new Dictionary<uint, SteamAppInfo>();
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var br = new BinaryReader(fs))
                {
                    uint magic = br.ReadUInt32();
                    br.ReadUInt32(); // universe
                    string[] table = null;
                    if (magic == 0x07564429)
                    {
                        long tableOffset = br.ReadInt64();
                        long back = fs.Position;
                        fs.Position = tableOffset;
                        uint count = br.ReadUInt32();
                        table = new string[count];
                        for (int i = 0; i < count; i++) table[i] = ReadCString(br);
                        fs.Position = back;
                    }
                    else if (magic != 0x07564428 && magic != 0x07564427)
                    {
                        return result;
                    }
                    int headerSkip = magic >= 0x07564428 ? 60 : 40;
                    while (fs.Position + 8 <= fs.Length)
                    {
                        uint appId = br.ReadUInt32();
                        if (appId == 0) break;
                        uint size = br.ReadUInt32();
                        long end = fs.Position + size;
                        try
                        {
                            fs.Position += headerSkip;
                            var kv = ReadKv(br, table, 0);
                            var appinfo = Child(kv, "appinfo") ?? kv;
                            var common = Child(appinfo, "common");
                            var config = Child(appinfo, "config");
                            var info = new SteamAppInfo { AppId = appId };
                            if (common != null)
                            {
                                info.Name = common.ContainsKey("name") ? common["name"] as string : null;
                                info.Type = common.ContainsKey("type") ? common["type"] as string : null;
                            }
                            if (config != null && config.ContainsKey("installdir"))
                                info.InstallDir = config["installdir"] as string;
                            result[appId] = info;
                        }
                        catch { }
                        fs.Position = end;
                    }
                }
            }
            catch (Exception ex) { Log.Error("Lettura appinfo.vdf", ex); }
            return result;
        }

        private static Dictionary<string, object> Child(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        private static Dictionary<string, object> ReadKv(BinaryReader br, string[] table, int depth)
        {
            if (depth > 64) throw new InvalidDataException();
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                byte type = br.ReadByte();
                if (type == 0x08 || type == 0x0B) break;
                string key = table != null ? table[br.ReadInt32()] : ReadCString(br);
                object value;
                switch (type)
                {
                    case 0x00: value = ReadKv(br, table, depth + 1); break;
                    case 0x01: value = ReadCString(br); break;
                    case 0x02: case 0x04: case 0x06: value = br.ReadInt32(); break;
                    case 0x03: value = br.ReadSingle(); break;
                    case 0x05: value = ReadWString(br); break;
                    case 0x07: case 0x0A: value = br.ReadUInt64(); break;
                    default: throw new InvalidDataException("Tipo KV sconosciuto " + type);
                }
                d[key] = value;
            }
            return d;
        }

        private static string ReadCString(BinaryReader br)
        {
            var bytes = new List<byte>(32);
            byte b;
            while ((b = br.ReadByte()) != 0) bytes.Add(b);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static string ReadWString(BinaryReader br)
        {
            var sb = new StringBuilder();
            char c;
            while ((c = (char)br.ReadUInt16()) != 0) sb.Append(c);
            return sb.ToString();
        }
    }
}
