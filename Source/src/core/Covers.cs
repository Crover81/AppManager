using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AppManager
{
    internal enum ArtKind { Portrait, Header, Hero, Logo }

    /// <summary>Immagini trovate per un gioco (file già salvati nella cache locale).</summary>
    internal sealed class CoverResult
    {
        public string AppId;
        public string Name;
        public string Source;
        public string Portrait;   // copertina verticale
        public string Header;     // orizzontale con il titolo
        public string Hero;       // orizzontale senza titolo
        public string Logo;       // logo trasparente

        public bool HasArt { get { return Portrait != null || Header != null || Hero != null || Logo != null; } }
        public bool HasLandscape { get { return Header != null || Hero != null; } }
        public bool Found { get { return HasArt || Name != null; } }

        /// <summary>Completa i campi mancanti con quelli di un'altra fonte.</summary>
        public void Fill(CoverResult other)
        {
            if (other == null) return;
            bool used = (Portrait == null && other.Portrait != null) || (Header == null && other.Header != null)
                        || (Hero == null && other.Hero != null) || (Logo == null && other.Logo != null);
            if (Portrait == null) Portrait = other.Portrait;
            if (Header == null) Header = other.Header;
            if (Hero == null) Hero = other.Hero;
            if (Logo == null) Logo = other.Logo;
            if (Name == null) Name = other.Name;
            if (AppId == null) AppId = other.AppId;
            if (used && other.Source != null) Source = Source == null ? other.Source : Source + " + " + other.Source;
        }
    }

    /// <summary>
    /// Immagini dei giochi da database online, a più livelli: Steam (giochi e pacchetti), PCGamingWiki come
    /// dizionario per trovare l'ID Steam, SteamGridDB (con chiave personale), Wikipedia. Il nome viene provato
    /// in più varianti, ma ogni risultato deve corrispondere al titolo completo. Viene inviato solo il nome del gioco.
    /// </summary>
    internal static class CoverService
    {
        private static readonly object Sync = new object();
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(3);
        private static Dictionary<string, object> index;
        private const string UserAgent = "AppManager/2.0 (programma personale per Windows; ricerca copertine dei giochi)";
        private const string SteamAssets = "https://shared.akamai.steamstatic.com/store_item_assets/";

        private static string Dir { get { return Path.Combine(AppPaths.DataDir, "covers"); } }
        private static string IndexFile { get { return Path.Combine(Dir, "index.json"); } }

        /// <summary>Con l'unità dei dati quasi piena non si scarica nulla: le immagini già presenti restano.</summary>
        public static bool Enabled { get { return AppSettings.Current.DownloadCovers && !PcResources.IsLowSpace; } }

        private static string SgdbKey
        {
            get { string k = AppSettings.Current.SteamGridDbKey; return string.IsNullOrWhiteSpace(k) ? null : k.Trim(); }
        }

        static CoverService()
        {
            // TLS 1.2 obbligatorio per i servizi online (0 = lascia decidere a Windows, già corretto)
            var p = ServicePointManager.SecurityProtocol;
            if (p != 0 && (p & SecurityProtocolType.Tls12) == 0) ServicePointManager.SecurityProtocol = p | SecurityProtocolType.Tls12;
        }

        // ================================================================== API per i giochi

        /// <summary>Immagini mancanti di un gioco installato.</summary>
        public static CoverResult ForGame(AppEntry g)
        {
            if (!Enabled) return null;
            string key = "g:" + NameUtil.Norm(g.Name);
            var cached = FromIndex(key);
            if (cached != null) return cached.Found ? cached : null;

            string folder = string.IsNullOrEmpty(g.InstallDir) ? null : Path.GetFileName(g.InstallDir);
            var names = new[] { g.Name, folder }.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            bool wantPortrait = g.CoverImage == null || IsLowRes(g.CoverImage);
            var r = new CoverResult();

            // 1) Steam: AppID dichiarato (giochi Steam, steam_appid.txt) oppure ricerca per nome
            string appId = g.Launcher == LauncherKind.Steam ? g.SteamAppId : null;
            if (appId == null && !string.IsNullOrEmpty(g.InstallDir)) appId = SteamInfo.AppIdsInFolder(g.InstallDir).FirstOrDefault();
            if (appId != null) r.Fill(FetchSteamApp(appId, null));
            if (!r.HasArt)
            {
                var found = SearchSteam(names);
                if (found != null) r.Fill(FetchSteamApp(found.Item1, found.Item2));
            }

            // 2) PCGamingWiki come dizionario: trova l'ID Steam (gioco o pacchetto) anche quando la ricerca di Steam fallisce
            if (!r.HasArt) r.Fill(FromPcGamingWiki(names));

            // 3) raccolte: il pacchetto Steam che contiene i giochi presenti nelle sottocartelle
            var components = Components(g.InstallDir);
            if (!r.HasLandscape && components.Count > 0) r.Fill(CollectionPackage(names, components));

            // 4) SteamGridDB (con chiave): copertina in alta risoluzione, immagine hero e logo
            if (SgdbKey != null && ((wantPortrait && r.Portrait == null) || r.Hero == null || r.Logo == null))
                r.Fill(FromSteamGridDb(names, r.AppId ?? appId));

            // 5) Wikipedia: solo la copertina, se manca del tutto
            if (r.Portrait == null && g.CoverImage == null) r.Fill(SearchWikipedia(names));

            // 6) raccolte: la copertina di uno dei giochi contenuti
            if (r.Portrait == null && g.CoverImage == null)
            {
                foreach (var part in components)
                {
                    var c = ForName(part);
                    if (c == null || c.Portrait == null) continue;
                    r.Portrait = c.Portrait;
                    r.Source = (r.Source == null ? "" : r.Source + " + ") + L.F("raccolta: {0}", part);
                    break;
                }
            }

            Save(key, r);
            Log.Info("Immagini di \"{0}\": {1}", g.Name, r.HasArt ? r.Source ?? L.T("trovate") : L.T("non trovate"));
            return r.Found ? r : null;
        }

        /// <summary>Immagini cercate solo per titolo (residui di giochi non più installati, giochi di una raccolta).</summary>
        public static CoverResult ForName(string name)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(name)) return null;
            string key = "n:" + NameUtil.Norm(name);
            var cached = FromIndex(key);
            if (cached != null) return cached.Found ? cached : null;
            var names = new List<string> { name };
            var r = new CoverResult();
            var found = SearchSteam(names);
            if (found != null) r.Fill(FetchSteamApp(found.Item1, found.Item2));
            if (!r.HasArt) r.Fill(FromPcGamingWiki(names));
            if (SgdbKey != null && r.Portrait == null) r.Fill(FromSteamGridDb(names, r.AppId));
            if (r.Portrait == null) r.Fill(SearchWikipedia(names));
            Save(key, r);
            return r.Found ? r : null;
        }

        /// <summary>Nome e immagini di un AppID (per i residui "Gioco con AppID ...").</summary>
        public static CoverResult ForAppId(string appId)
        {
            if (!Enabled || string.IsNullOrEmpty(appId)) return null;
            string key = "a:" + appId;
            var cached = FromIndex(key);
            if (cached != null) return cached.Found ? cached : null;
            var r = FetchSteamApp(appId, SteamInfo.AppName(appId));
            if (r.Portrait == null && SgdbKey != null) r.Fill(FromSteamGridDb(new List<string>(), appId));
            Save(key, r);
            return r.Found ? r : null;
        }

        /// <summary>
        /// AppID Steam già associati (dalle ricerche per nome) ai giochi installati: servono a non scambiare
        /// per residui i salvataggi di un gioco presente, anche se i suoi file non dichiarano l'AppID.
        /// </summary>
        public static HashSet<string> KnownAppIds(IEnumerable<AppEntry> installed)
        {
            var ids = new HashSet<string>();
            lock (Sync)
            {
                foreach (var g in installed)
                {
                    object o;
                    if (!Index.TryGetValue("g:" + NameUtil.Norm(g.Name), out o)) continue;
                    string id = Json.Str(o, "AppId");
                    if (!string.IsNullOrEmpty(id) && id.All(char.IsDigit)) ids.Add(id);
                }
            }
            return ids;
        }

        /// <summary>
        /// True se il titolo (normalizzato) è già stato riconosciuto come gioco in una ricerca precedente
        /// (gioco installato o residuo a cui è stata trovata la copertina).
        /// </summary>
        public static bool IsKnownGame(string normalizedName)
        {
            if (string.IsNullOrEmpty(normalizedName)) return false;
            lock (Sync)
            {
                foreach (var prefix in new[] { "g:", "n:" })
                {
                    object o;
                    if (!Index.TryGetValue(prefix + normalizedName, out o)) continue;
                    if (Json.Str(o, "AppId") != null || Json.Str(o, "Portrait") != null || Json.Str(o, "Header") != null) return true;
                }
            }
            return false;
        }

        // ================================================================== Cache: statistiche, pulizia

        /// <summary>Svuota la cache delle immagini scaricate (per ripetere le ricerche).</summary>
        public static void ClearCache()
        {
            lock (Sync)
            {
                index = null;
                try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); }
                catch (Exception ex) { Log.Error("Svuotamento cache copertine", ex); }
            }
        }

        /// <summary>Dimenticare le ricerche incomplete (es. dopo aver inserito la chiave SteamGridDB).</summary>
        public static void ForgetIncomplete()
        {
            lock (Sync)
            {
                foreach (var k in Index.Keys.ToList())
                {
                    var d = Index[k] as Dictionary<string, object>;
                    if (d == null || Json.Str(d, "Portrait") == null || Json.Str(d, "Hero") == null || Json.Str(d, "Logo") == null)
                        Index.Remove(k);
                }
                SaveIndex();
            }
        }

        public static void CacheStats(out int files, out long bytes)
        {
            files = 0;
            bytes = 0;
            foreach (var f in PathUtil.Files(Dir))
            {
                if (Path.GetFileName(f).Equals("index.json", StringComparison.OrdinalIgnoreCase)) continue;
                files++;
                try { bytes += new FileInfo(f).Length; } catch { }
            }
        }

        /// <summary>
        /// Tiene in ordine la cache sapendo che la cartella Dati gira su più PC. Le immagini di un gioco restano finché è noto
        /// su questo PC o su un altro PC conosciuto: passare a un altro PC non costringe a riscaricare tutto al ritorno.
        /// Si tolgono quelle di giochi non più visti da nessuna parte e, oltre il limite di spazio, quelle meno utili.
        /// Restituisce il numero di file eliminati.
        /// </summary>
        public static int Prune(IEnumerable<AppEntry> installed)
        {
            lock (Sync)
            {
                ThisPc.NoteGames(installed.Select(g => "g:" + NameUtil.Norm(g.Name)).ToList());
                if (!Directory.Exists(Dir)) return 0;
                var mine = ThisPc.MyGameKeys();
                var others = ThisPc.OtherPcGameKeys();
                var limit = DateTime.Now.AddDays(-30);
                foreach (var k in Index.Keys.ToList())
                {
                    bool remove;
                    if (k.StartsWith("g:")) remove = !mine.Contains(k) && !others.ContainsKey(k);
                    else
                    {
                        DateTime used;
                        var d = Index[k] as Dictionary<string, object>;
                        remove = !DateTime.TryParse(Json.Str(d, "Used") ?? Json.Str(d, "When"), null, DateTimeStyles.RoundtripKind, out used) || used < limit;
                    }
                    if (remove) Index.Remove(k);
                }
                int deleted = DeleteUnreferenced();
                if (deleted > 0) Log.Info("Cache copertine: eliminate {0} immagini di giochi non più presenti", deleted);
                int evicted = EnforceBudget(mine, others);
                if (evicted > 0) Log.Info("Cache copertine: eliminate {0} immagini per restare nel limite di spazio", evicted);
                SaveIndex();
                return deleted + evicted;
            }
        }

        /// <summary>Riapplica subito il limite di spazio (per esempio dopo averlo cambiato o dopo aver dimenticato un PC).</summary>
        public static int FreeSpaceNow(IEnumerable<AppEntry> installed)
        {
            return Prune(installed);
        }

        /// <summary>
        /// Oltre il limite si toglie prima ciò che serve meno: le immagini dei residui già mostrati, poi quelle dei giochi
        /// degli altri PC, partendo dal PC visto da più tempo. Le immagini dei giochi di questo PC non si toccano.
        /// </summary>
        private static int EnforceBudget(HashSet<string> mine, Dictionary<string, DateTime> others)
        {
            long total = CacheBytes();
            long budget = PcResources.CoverBudget(total);
            if (total <= budget) return 0;
            var candidates = Index.Keys.Where(k => !mine.Contains(k)).Select(k =>
            {
                DateTime seen;
                bool other = others.TryGetValue(k, out seen);
                return new { Key = k, Rank = other ? 1 : 0, Seen = other ? seen : DateTime.MinValue };
            }).OrderBy(c => c.Rank).ThenBy(c => c.Seen).ToList();
            int deleted = 0;
            foreach (var c in candidates)
            {
                if (total <= budget) break;
                Index.Remove(c.Key);
                deleted += DeleteUnreferenced();
                total = CacheBytes();
            }
            return deleted;
        }

        private static long CacheBytes()
        {
            int files;
            long bytes;
            CacheStats(out files, out bytes);
            return bytes;
        }

        private static int DeleteUnreferenced()
        {
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in Index.Values)
                foreach (var f in new[] { "Portrait", "Header", "Hero", "Logo" })
                {
                    string name = Json.Str(o, f);
                    if (name != null) referenced.Add(name);
                }
            int deleted = 0;
            foreach (var f in PathUtil.Files(Dir))
            {
                string name = Path.GetFileName(f);
                if (name.Equals("index.json", StringComparison.OrdinalIgnoreCase) || referenced.Contains(name)) continue;
                try { File.Delete(f); deleted++; }
                catch { }
            }
            return deleted;
        }

        /// <summary>True se l'immagine è piccola (es. copertina inclusa in un repack): se ne cerca una migliore online.</summary>
        public static bool IsLowRes(string file)
        {
            if (file == null) return true;
            if (SteamInfo.SteamPath != null && PathUtil.IsUnder(file, SteamInfo.SteamPath)) return false;
            if (PathUtil.IsUnder(file, Dir)) return false;
            int w, h;
            return !ImageSize(file, out w, out h) || w < 400;
        }

        // ================================================================== Copertina inclusa nel gioco

        private static readonly Regex LocalCoverName = new Regex(@"(cover|copertina|folder|poster|box\s*art|boxart|box_?front|capsule|key\s*art)", RegexOptions.IgnoreCase);

        /// <summary>Immagine di copertina inclusa nella cartella del gioco (molti repack e alcune edizioni la contengono).</summary>
        public static string FindLocalCover(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            string best = null;
            long bestArea = 0;
            foreach (var f in PathUtil.Files(dir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".jpg" && ext != ".jpeg" && ext != ".png") continue;
                if (!LocalCoverName.IsMatch(Path.GetFileNameWithoutExtension(f))) continue;
                int w, h;
                if (!ImageSize(f, out w, out h) || w < 120 || h < w * 0.9) continue; // solo immagini verticali o quadrate
                if ((long)w * h > bestArea) { bestArea = (long)w * h; best = f; }
            }
            return best;
        }

        /// <summary>Dimensioni di un PNG o JPEG leggendo solo l'intestazione del file.</summary>
        public static bool ImageSize(string file, out int width, out int height)
        {
            width = height = 0;
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs))
                {
                    var head = br.ReadBytes(24);
                    if (head.Length >= 24 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
                    {
                        width = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
                        height = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
                        return true;
                    }
                    if (head.Length < 4 || head[0] != 0xFF || head[1] != 0xD8) return false;
                    fs.Position = 2;
                    while (fs.Position < fs.Length)
                    {
                        if (br.ReadByte() != 0xFF) continue;
                        byte marker = br.ReadByte();
                        while (marker == 0xFF) marker = br.ReadByte();
                        if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;
                        int len = (br.ReadByte() << 8) | br.ReadByte();
                        bool sof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                        if (sof)
                        {
                            br.ReadByte(); // precisione
                            height = (br.ReadByte() << 8) | br.ReadByte();
                            width = (br.ReadByte() << 8) | br.ReadByte();
                            return width > 0 && height > 0;
                        }
                        fs.Position += len - 2;
                    }
                }
            }
            catch { }
            return false;
        }

        // ================================================================== Varianti del nome e confronto

        private static readonly Dictionary<string, string> Abbreviations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "gta", "grand theft auto" }, { "cod", "call of duty" }, { "rdr", "red dead redemption" }, { "de", "definitive edition" },
            { "goty", "game of the year" }, { "tlou", "the last of us" }, { "le", "legendary edition" }, { "nfs", "need for speed" },
            { "mgs", "metal gear solid" }, { "tes", "the elder scrolls" }, { "mk", "mortal kombat" },
        };

        private static readonly Dictionary<string, string> Roman = new Dictionary<string, string>
        {
            { "ii", "2" }, { "iii", "3" }, { "iv", "4" }, { "v", "5" }, { "vi", "6" }, { "vii", "7" }, { "viii", "8" }, { "ix", "9" },
            { "x", "10" }, { "xi", "11" }, { "xii", "12" }, { "xiii", "13" }, { "xiv", "14" }, { "xv", "15" }, { "xvi", "16" },
        };

        private static readonly HashSet<string> Stopwords = new HashSet<string>(new[]
        {
            "the", "of", "a", "an", "and", "edition", "definitive", "remastered", "remake", "deluxe", "ultimate", "complete",
            "goty", "game", "year", "enhanced", "gold", "premium", "standard", "digital", "directors", "cut", "collection",
            "legendary", "anniversary", "video",
        });

        /// <summary>Sigle maiuscole sostituite dal nome completo ("GTA: The Trilogy - DE" → "Grand Theft Auto: The Trilogy - Definitive Edition").</summary>
        public static string ExpandAbbreviations(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return Regex.Replace(s, @"\b[A-Za-z]{2,5}\b", m =>
            {
                string exp;
                if (!m.Value.All(char.IsUpper) || !Abbreviations.TryGetValue(m.Value, out exp)) return m.Value;
                return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(exp);
            });
        }

        /// <summary>Varianti del titolo da usare come ricerca (la verifica avviene sempre sul titolo completo).</summary>
        public static List<string> QueryVariants(IEnumerable<string> names)
        {
            var list = new List<string>();
            Action<string> add = s =>
            {
                s = Regex.Replace(s ?? "", @"\s+", " ").Trim(' ', '-', ':', '–', '—');
                if (s.Length >= 3 && !list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s);
            };
            foreach (var raw in names)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string clean = NameUtil.CleanDisplayName(raw);
                string expanded = ExpandAbbreviations(clean);
                add(clean);
                add(expanded);
                add(NameUtil.StripEdition(expanded));
                var parts = Regex.Split(expanded, @"\s*(?::|\s[-–—|]\s)\s*").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                if (parts.Count > 1)
                {
                    add(parts[0] + ": " + parts[1]);           // "Grand Theft Auto: The Trilogy"
                    add(string.Join(" ", parts));              // tutte le parti senza separatori
                    add(parts[0] + " " + parts[parts.Count - 1]);
                    add(parts[0]);                             // solo la prima parte (ultima risorsa)
                }
            }
            return list;
        }

        private static List<string> References(IEnumerable<string> names)
        {
            return names.Concat(names.Select(ExpandAbbreviations)).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        }

        private static List<string> Tokens(string s)
        {
            s = s.Replace("&", " and ").Replace("'", "").Replace("’", "");
            var list = new List<string>();
            foreach (var w in Regex.Split(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0))
            {
                string exp, num;
                if (Abbreviations.TryGetValue(w, out exp)) list.AddRange(exp.Split(' '));
                else if (Roman.TryGetValue(w, out num)) list.Add(num);
                else list.Add(w);
            }
            return list.Select(w => NameUtil.Norm(w)).Where(w => w.Length > 0 && !Stopwords.Contains(w)).ToList();
        }

        /// <summary>
        /// Quanto il titolo trovato corrisponde a quello cercato (0 = da scartare, 1 = identico).
        /// Una copertina sbagliata è peggio di nessuna: i numeri dei seguiti devono coincidere,
        /// le parole distintive devono comparire e un titolo più generico (la serie, non il gioco) viene scartato.
        /// </summary>
        public static double MatchScore(string query, string candidate)
        {
            if (NameUtil.Norm(query) == NameUtil.Norm(candidate)) return 1;
            var q = Tokens(query).Distinct().ToList();
            var c = Tokens(candidate).Distinct().ToList();
            if (q.Count == 0 || c.Count == 0) return 0;
            if (q.Count == c.Count && q.All(c.Contains)) return 1;
            var qNums = new HashSet<string>(q.Where(t => t.All(char.IsDigit)));
            if (!qNums.SetEquals(c.Where(t => t.All(char.IsDigit)))) return 0;
            if (c.All(q.Contains)) return 0; // il risultato è più generico del titolo cercato (es. la serie)
            int common = q.Count(c.Contains);
            double dice = 2.0 * common / (q.Count + c.Count);
            bool subset = q.All(c.Contains) && c.Count(t => !q.Contains(t)) <= 1;
            if (subset) return Math.Max(dice, 0.85);
            return dice >= 0.8 ? dice : 0;
        }

        private static double BestScore(IEnumerable<string> references, string candidate)
        {
            return references.Select(r => MatchScore(r, candidate)).DefaultIfEmpty(0).Max();
        }

        /// <summary>Giochi contenuti in una raccolta (sottocartelle che sono a loro volta giochi).</summary>
        private static List<string> Components(string dir)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return list;
            foreach (var sub in PathUtil.Dirs(dir))
            {
                string name = Path.GetFileName(sub);
                if (name.StartsWith("_") || name.Equals("Engine", StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(Path.Combine(sub, "Engine")) || GameMarkers.Detect(sub, 1) != null) list.Add(NameUtil.CleanDisplayName(name));
            }
            return list.Count >= 2 ? list : new List<string>();
        }

        // ================================================================== Steam

        /// <summary>Cerca il gioco nel negozio di Steam: restituisce (AppID, nome) solo se il titolo corrisponde davvero.</summary>
        public static Tuple<string, string> SearchSteam(IList<string> names)
        {
            var references = References(names);
            Tuple<string, string> best = null;
            double bestScore = 0;
            foreach (var q in QueryVariants(names).Take(6))
            {
                string json = DownloadText("https://store.steampowered.com/api/storesearch/?term=" + Uri.EscapeDataString(q) + "&l=italian&cc=IT");
                var items = Json.Child(json == null ? null : Json.Parse(json), "items") as object[];
                if (items == null) continue;
                foreach (var it in items.Take(8))
                {
                    string id = Json.Str(it, "id"), name = Json.Str(it, "name");
                    if (id == null || name == null) continue;
                    double score = BestScore(references, name);
                    if (score > bestScore) { bestScore = score; best = Tuple.Create(id, name); }
                }
                if (bestScore >= 0.99) break;
            }
            Log.Info("Immagini: Steam per \"{0}\" → {1} ({2:0.00})", names.FirstOrDefault(), best == null ? L.T("niente") : best.Item2, bestScore);
            return bestScore >= 0.8 ? best : null;
        }

        /// <summary>Scheda del negozio Steam (gioco o pacchetto) con nome e percorsi esatti delle immagini.</summary>
        private static object SteamStoreItem(string idField, string id)
        {
            string input = "{\"ids\":[{\"" + idField + "\":" + id + "}],\"context\":{\"language\":\"italian\",\"country_code\":\"IT\"}," + "\"data_request\":{\"include_assets\":true,\"include_basic_info\":true}}";
            string json = DownloadText("https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(input));
            var items = Json.Child(Json.Child(json == null ? null : Json.Parse(json), "response"), "store_items") as object[];
            var item = items != null && items.Length > 0 ? items[0] : null;
            return item != null && Json.Str(item, "name") != null ? item : null;
        }

        private static IEnumerable<string> AssetUrls(object item, params string[] keys)
        {
            var assets = Json.Child(item, "assets");
            string format = Json.Str(assets, "asset_url_format");
            if (format == null) yield break;
            foreach (var k in keys)
            {
                string f = Json.Str(assets, k);
                if (f != null) yield return SteamAssets + format.Replace("${FILENAME}", f);
            }
        }

        private static CoverResult FetchSteamApp(string appId, string knownName)
        {
            var item = SteamStoreItem("appid", appId);
            var r = new CoverResult { AppId = appId, Name = knownName ?? Json.Str(item, "name"), Source = "Steam" };
            string legacy = SteamAssets + "steam/apps/" + appId + "/";
            // dal 2024 le immagini dei giochi nuovi hanno percorsi con un codice univoco: si usano quelli indicati dal negozio
            r.Portrait = DownloadImage(appId + "_portrait", AssetUrls(item, "library_capsule_2x", "library_capsule")
                .Concat(new[] { legacy + "library_600x900_2x.jpg", "https://cdn.cloudflare.steamstatic.com/steam/apps/" + appId + "/library_600x900.jpg" }));
            r.Header = DownloadImage(appId + "_header", AssetUrls(item, "header").Concat(new[] { legacy + "header.jpg" }));
            r.Hero = DownloadImage(appId + "_hero", AssetUrls(item, "library_hero").Concat(new[] { legacy + "library_hero.jpg" }));
            r.Logo = DownloadImage(appId + "_logo", new[] { legacy + "logo.png", legacy + "logo_2x.png" });
            if (!r.HasArt && item == null) r.Name = knownName;
            return r;
        }

        /// <summary>Immagini di un pacchetto Steam (le raccolte vendute come pacchetto non hanno una scheda di gioco).</summary>
        private static CoverResult FetchSteamPackage(string packageId, string knownName)
        {
            var item = SteamStoreItem("packageid", packageId);
            if (item == null) return null;
            var r = new CoverResult { Name = knownName ?? Json.Str(item, "name"), Source = "Steam (pacchetto)" };
            r.Header = DownloadImage("p" + packageId + "_header", AssetUrls(item, "header", "package_header", "main_capsule"));
            return r.HasArt ? r : null;
        }

        /// <summary>Raccolte: tra le opzioni d'acquisto dei giochi contenuti c'è il pacchetto con il nome della raccolta.</summary>
        private static CoverResult CollectionPackage(IList<string> names, List<string> components)
        {
            var references = References(names);
            foreach (var part in components.Take(3))
            {
                var app = SearchSteam(new List<string> { part });
                if (app == null) continue;
                string json = DownloadText("https://store.steampowered.com/api/appdetails?appids=" + app.Item1 + "&cc=us&l=english");
                var data = Json.Child(Json.Child(json == null ? null : Json.Parse(json), app.Item1), "data");
                var groups = Json.Child(data, "package_groups") as object[];
                if (groups == null) continue;
                string bestId = null, bestName = null;
                double bestScore = 0;
                foreach (var grp in groups)
                {
                    foreach (var sub in (Json.Child(grp, "subs") as object[]) ?? new object[0])
                    {
                        string text = Regex.Replace(Json.Str(sub, "option_text") ?? "", "<[^>]+>", " ");
                        text = Regex.Replace(text, @"\s+[-–]\s+[^-–]*\d[^-–]*$", "").Trim();   // toglie il prezzo finale
                        double score = BestScore(references, text);
                        if (score > bestScore) { bestScore = score; bestId = Json.Str(sub, "packageid"); bestName = text; }
                    }
                }
                Log.Info("Immagini: pacchetto Steam per \"{0}\" → {1} ({2:0.00})", names.FirstOrDefault(), bestName ?? L.T("niente"), bestScore);
                if (bestId != null && bestScore >= 0.8) return FetchSteamPackage(bestId, bestName);
            }
            return null;
        }

        // ================================================================== PCGamingWiki (solo come dizionario di ID Steam)

        /// <summary>Titolo già associato a un gioco dalle ricerche delle immagini (può essere più completo del nome rilevato).</summary>
        internal static string KnownTitle(AppEntry g)
        {
            lock (Sync)
            {
                object o;
                return Index.TryGetValue("g:" + NameUtil.Norm(g.Name), out o) ? Json.Str(o, "Name") : null;
            }
        }

        /// <summary>La pagina di PCGamingWiki di un gioco, solo se il titolo corrisponde davvero (null altrimenti).</summary>
        internal static string PcGamingWikiTitle(IList<string> names)
        {
            var references = References(names);
            string bestTitle = null;
            double bestScore = 0;
            foreach (var q in QueryVariants(names).Take(3))
            {
                string json = DownloadText("https://www.pcgamingwiki.com/w/api.php?action=query&list=search&srsearch=" + Uri.EscapeDataString(q) + "&srlimit=6&format=json");
                var results = Json.Child(Json.Child(json == null ? null : Json.Parse(json), "query"), "search") as object[];
                if (results == null) continue;
                foreach (var res in results)
                {
                    string title = Json.Str(res, "title");
                    if (title == null) continue;
                    double score = BestScore(references, title);
                    if (score > bestScore) { bestScore = score; bestTitle = title; }
                }
                if (bestScore >= 0.99) break;
            }
            return bestTitle == null || bestScore < 0.8 ? null : bestTitle;
        }

        internal static string PcGamingWikiText(string title)
        {
            string page = DownloadText("https://www.pcgamingwiki.com/w/api.php?action=parse&page=" + Uri.EscapeDataString(title) + "&prop=wikitext&format=json");
            return Json.Str(Json.Child(Json.Child(page == null ? null : Json.Parse(page), "parse"), "wikitext"), "*");
        }

        private static CoverResult FromPcGamingWiki(IList<string> names)
        {
            string bestTitle = PcGamingWikiTitle(names);
            if (bestTitle == null) return null;
            string page = DownloadText("https://www.pcgamingwiki.com/w/api.php?action=parse&page=" + Uri.EscapeDataString(bestTitle) + "&prop=wikitext&section=0&format=json");
            string wikitext = Json.Str(Json.Child(Json.Child(page == null ? null : Json.Parse(page), "parse"), "wikitext"), "*");
            if (wikitext == null) return null;
            var m = Regex.Match(wikitext, @"\|\s*steam appid\s*=\s*([0-9][0-9 ,]*)", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            foreach (var id in m.Groups[1].Value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Take(2))
            {
                Log.Info("Immagini: PCGamingWiki per \"{0}\" → {1}, ID Steam {2}", names.FirstOrDefault(), bestTitle, id);
                var app = FetchSteamApp(id, bestTitle);
                if (app.HasArt) return app;
                var pkg = FetchSteamPackage(id, bestTitle);   // l'ID può essere quello di un pacchetto
                if (pkg != null) return pkg;
            }
            return null;
        }

        // ================================================================== SteamGridDB (chiave API personale e gratuita)

        private const string SgdbApi = "https://www.steamgriddb.com/api/v2/";

        /// <summary>Verifica una chiave SteamGridDB: true = valida, false = rifiutata, null = servizio non raggiungibile.</summary>
        public static bool? TestSteamGridDbKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            int status;
            var bytes = Download(SgdbApi + "search/autocomplete/portal", key.Trim(), out status);
            if (status == 401 || status == 403) return false;
            if (bytes == null) return null;
            return Json.Str(Json.Parse(Encoding.UTF8.GetString(bytes)), "success") == "True";
        }

        private static object SgdbGet(string path)
        {
            string key = SgdbKey;
            if (key == null) return null;
            int status;
            var bytes = Download(SgdbApi + path, key, out status);
            if (bytes == null) return null;
            var obj = Json.Parse(Encoding.UTF8.GetString(bytes));
            return Json.Str(obj, "success") == "True" ? Json.Child(obj, "data") : null;
        }

        private static CoverResult FromSteamGridDb(IList<string> names, string steamAppId)
        {
            string gameId = null, gameName = null;
            if (steamAppId != null)
            {
                var g = SgdbGet("games/steam/" + steamAppId);
                gameId = Json.Str(g, "id");
                gameName = Json.Str(g, "name");
            }
            if (gameId == null && names.Count > 0)
            {
                var references = References(names);
                double bestScore = 0;
                foreach (var q in QueryVariants(names).Take(3))
                {
                    var results = SgdbGet("search/autocomplete/" + Uri.EscapeDataString(q)) as object[];
                    if (results == null) continue;
                    foreach (var res in results)
                    {
                        string name = Json.Str(res, "name");
                        if (name == null) continue;
                        double score = BestScore(references, name);
                        if (score > bestScore) { bestScore = score; gameId = Json.Str(res, "id"); gameName = name; }
                    }
                    if (bestScore >= 0.99) break;
                }
                Log.Info("Immagini: SteamGridDB per \"{0}\" → {1} ({2:0.00})", names.FirstOrDefault(), gameName ?? L.T("niente"), bestScore);
                if (bestScore < 0.8) gameId = null;
            }
            if (gameId == null) return null;
            // solo immagini statiche PNG/JPEG (WPF non legge WebP su tutti i PC), senza contenuti per adulti o umoristici
            const string filters = "types=static&nsfw=false&humor=false";
            var r = new CoverResult { Name = gameName, Source = "SteamGridDB" };
            r.Portrait = DownloadImage("sgdb" + gameId + "_portrait", SgdbUrls("grids/game/" + gameId + "?dimensions=600x900,660x930&mimes=image/png,image/jpeg&" + filters));
            r.Hero = DownloadImage("sgdb" + gameId + "_hero", SgdbUrls("heroes/game/" + gameId + "?mimes=image/png,image/jpeg&" + filters));
            r.Logo = DownloadImage("sgdb" + gameId + "_logo", SgdbUrls("logos/game/" + gameId + "?mimes=image/png&" + filters));
            return r.HasArt ? r : null;
        }

        /// <summary>Immagini di SteamGridDB ordinate per voto della comunità (prima le più apprezzate).</summary>
        private static IEnumerable<string> SgdbUrls(string path)
        {
            var list = SgdbGet(path) as object[];
            if (list == null) return new string[0];
            return list.OrderByDescending(o => { int s; return int.TryParse(Json.Str(o, "score"), out s) ? s : 0; })
                       .Take(3).Select(o => Json.Str(o, "url")).Where(u => u != null).ToList();
        }

        // ================================================================== Wikipedia (copertina)

        /// <summary>Cerca la voce del gioco su Wikipedia e ne prende la copertina (l'immagine del riquadro informativo).</summary>
        private static CoverResult SearchWikipedia(IList<string> names)
        {
            var references = References(names);
            string bestTitle = null;
            double bestScore = 0;
            foreach (var q in QueryVariants(names).Take(4))
            {
                string json = DownloadText("https://en.wikipedia.org/w/api.php?action=query&list=search&srsearch=" + Uri.EscapeDataString(q + " video game") + "&srlimit=8&format=json");
                var results = Json.Child(Json.Child(json == null ? null : Json.Parse(json), "query"), "search") as object[];
                if (results == null) continue;
                foreach (var res in results)
                {
                    string title = Json.Str(res, "title");
                    if (title == null) continue;
                    // "Titolo (videogioco del 2021)" → si confronta il titolo senza la parte tra parentesi
                    double score = BestScore(references, Regex.Replace(title, @"\s*\([^)]*\)\s*$", ""));
                    if (score > bestScore) { bestScore = score; bestTitle = title; }
                }
                if (bestScore >= 0.99) break;
            }
            Log.Info("Immagini: Wikipedia per \"{0}\" → {1} ({2:0.00})", names.FirstOrDefault(), bestTitle ?? L.T("niente"), bestScore);
            if (bestTitle == null || bestScore < 0.8) return null;

            string page = DownloadText("https://en.wikipedia.org/w/api.php?action=query&titles=" + Uri.EscapeDataString(bestTitle) + "&prop=pageimages&piprop=original|thumbnail&pithumbsize=600&pilicense=any&redirects=1&format=json");
            var pages = Json.Child(Json.Child(page == null ? null : Json.Parse(page), "query"), "pages") as IDictionary<string, object>;
            var p = pages == null ? null : pages.Values.FirstOrDefault();
            var original = Json.Child(p, "original");
            var thumb = Json.Child(p, "thumbnail");
            string src = Json.Str(thumb, "source") ?? Json.Str(original, "source");
            int w, h;
            if (src == null || !int.TryParse(Json.Str(original, "width"), out w) || !int.TryParse(Json.Str(original, "height"), out h)) return null;
            string ext = Path.GetExtension(src.Split('?')[0]).ToLowerInvariant();
            if ((ext != ".jpg" && ext != ".jpeg" && ext != ".png") || h < w * 0.9) return null; // niente loghi o immagini orizzontali

            string file = DownloadImage("wp_" + Hash(bestTitle) + "_portrait", new[] { src });
            return file == null ? null : new CoverResult { Name = bestTitle, Portrait = file, Source = "Wikipedia" };
        }

        private static string Hash(string s)
        {
            using (var md5 = MD5.Create())
                return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        // ================================================================== Download

        private static string DownloadImage(string baseName, IEnumerable<string> urls)
        {
            foreach (var ext in new[] { ".jpg", ".png" })
            {
                string existing = Path.Combine(Dir, baseName + ext);
                if (File.Exists(existing) && new FileInfo(existing).Length > 0) return existing;
            }
            Directory.CreateDirectory(Dir);
            foreach (var url in urls.Distinct())
            {
                int status;
                var bytes = Download(url, null, out status);
                if (bytes == null || bytes.Length < 1000) continue;
                bool jpeg = bytes[0] == 0xFF && bytes[1] == 0xD8;
                bool png = bytes[0] == 0x89 && bytes[1] == 0x50;
                if (!jpeg && !png) continue;
                string file = Path.Combine(Dir, baseName + (png ? ".png" : ".jpg"));
                File.WriteAllBytes(file, bytes);
                return file;
            }
            return null;
        }

        /// <summary>L'ultimo scaricamento non ha ricevuto risposta dal sito (rete assente, sito irraggiungibile).</summary>
        internal static bool LastDownloadFailed;

        private static string DownloadText(string url)
        {
            int status;
            var bytes = Download(url, null, out status);
            LastDownloadFailed = bytes == null && status == 0;
            return bytes == null ? null : Encoding.UTF8.GetString(bytes);
        }

        private static byte[] Download(string url, string bearer, out int status)
        {
            status = 0;
            Gate.Wait();
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = UserAgent;
                req.Timeout = 12000;
                req.ReadWriteTimeout = 12000;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                // la chiave SteamGridDB va solo alle API di SteamGridDB, mai ad altri siti
                if (bearer != null && url.StartsWith(SgdbApi, StringComparison.Ordinal)) req.Headers[HttpRequestHeader.Authorization] = "Bearer " + bearer;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    status = (int)resp.StatusCode;
                    if (resp.StatusCode != HttpStatusCode.OK) return null;
                    var buf = new byte[81920];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        ms.Write(buf, 0, n);
                        if (ms.Length > 12 * 1024 * 1024) return null;
                    }
                    return ms.ToArray();
                }
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp != null) status = (int)resp.StatusCode;
                return null;
            }
            catch (Exception ex) { Log.Error("Download " + url, ex); return null; }
            finally { Gate.Release(); }
        }

        // ================================================================== Indice locale (per non ripetere le ricerche)

        private static Dictionary<string, object> Index
        {
            get
            {
                if (index == null)
                {
                    try { index = File.Exists(IndexFile) ? Json.Parse(File.ReadAllText(IndexFile)) as Dictionary<string, object> : null; }
                    catch { index = null; }
                    if (index == null) index = new Dictionary<string, object>();
                }
                return index;
            }
        }

        /// <summary>Versione delle ricerche: le voci create con fonti diverse (versioni precedenti) vengono ripetute.</summary>
        private const int CacheVersion = 2;

        private static string InCache(string fileName)
        {
            return string.IsNullOrEmpty(fileName) ? null : Path.Combine(Dir, Path.GetFileName(fileName));
        }

        private static CoverResult FromIndex(string key)
        {
            lock (Sync)
            {
                object o;
                if (!Index.TryGetValue(key, out o)) return null;
                var d = o as Dictionary<string, object>;
                if (d == null) return null;
                int version;
                if (!int.TryParse(Json.Str(d, "V"), out version) || version < CacheVersion) return null;
                // nell'indice ci sono solo i nomi dei file: la cartella può cambiare lettera (chiavetta)
                var r = new CoverResult
                {
                    AppId = Json.Str(d, "AppId"), Name = Json.Str(d, "Name"), Source = Json.Str(d, "Source"),
                    Portrait = InCache(Json.Str(d, "Portrait")), Header = InCache(Json.Str(d, "Header")),
                    Hero = InCache(Json.Str(d, "Hero")), Logo = InCache(Json.Str(d, "Logo")),
                };
                DateTime when;
                // una ricerca senza risultati si riprova dopo una settimana
                if (!r.Found && (!DateTime.TryParse(Json.Str(d, "When"), null, DateTimeStyles.RoundtripKind, out when) || (DateTime.Now - when).TotalDays > 7))
                    return null;
                foreach (var f in new[] { r.Portrait, r.Header, r.Hero, r.Logo })
                    if (f != null && !File.Exists(f)) return null;
                d["Used"] = DateTime.Now.ToString("o");
                return r;
            }
        }

        private static void Save(string key, CoverResult r)
        {
            lock (Sync)
            {
                Index[key] = new Dictionary<string, object>
                {
                    { "V", CacheVersion }, { "AppId", r.AppId }, { "Name", r.Name }, { "Source", r.Source },
                    { "When", DateTime.Now.ToString("o") }, { "Used", DateTime.Now.ToString("o") },
                    { "Portrait", r.Portrait == null ? null : Path.GetFileName(r.Portrait) },
                    { "Header", r.Header == null ? null : Path.GetFileName(r.Header) },
                    { "Hero", r.Hero == null ? null : Path.GetFileName(r.Hero) },
                    { "Logo", r.Logo == null ? null : Path.GetFileName(r.Logo) },
                };
                SaveIndex();
            }
        }

        private static void SaveIndex()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                PathUtil.WriteAllTextAtomic(IndexFile, Json.Serialize(Index));
            }
            catch (Exception ex) { Log.Error("Salvataggio indice copertine", ex); }
        }
    }
}
