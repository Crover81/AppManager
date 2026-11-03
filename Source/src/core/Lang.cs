using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AppManager
{
    /// <summary>
    /// Traduzione dell'interfaccia. Nel codice e nello XAML il testo è scritto in italiano e fa da chiave; i cataloghi
    /// (ui\lang\*.txt, incorporati nell'eseguibile) danno lo stesso testo nelle altre lingue. Una frase si cerca intera,
    /// così ogni lingua può mettere le parole nel proprio ordine: i valori variabili sono i segnaposto {0}, {1}…
    /// </summary>
    internal static class L
    {
        public static readonly string[] Supported = { "en", "it", "fr", "de", "ru", "uk", "es" };
        public const string Fallback = "en";

        private static readonly object Sync = new object();
        private static Dictionary<string, string> table;
        private static Dictionary<string, string> fallbackTable;
        private static string code;
        private static CultureInfo culture;

        /// <summary>Solo per le prove: forza la lingua (in italiano le chiavi restano come sono).</summary>
        internal static string ForcedCode;

        /// <summary>Solo per le prove: passa subito a un'altra lingua.</summary>
        internal static void Use(string c)
        {
            lock (Sync)
            {
                ForcedCode = c;
                table = null;
            }
            EnsureLoaded();
        }

        /// <summary>Codice della lingua in uso: quella scelta nelle Impostazioni, altrimenti quella di Windows, altrimenti l'inglese.</summary>
        public static string Code
        {
            get
            {
                EnsureLoaded();
                return code;
            }
        }

        public static CultureInfo Culture
        {
            get
            {
                EnsureLoaded();
                return culture;
            }
        }

        /// <summary>La lingua di Windows, se è tra quelle disponibili; altrimenti l'inglese.</summary>
        public static string SystemLanguage()
        {
            string two = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            try { two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant(); } catch { }
            return Supported.Contains(two) ? two : Fallback;
        }

        /// <summary>Lingua che si avrebbe con questa scelta ("auto" = quella di Windows).</summary>
        public static string Resolve(string choice)
        {
            return Supported.Contains(choice) ? choice : SystemLanguage();
        }

        private static void EnsureLoaded()
        {
            if (table != null) return;
            lock (Sync)
            {
                if (table != null) return;
                string c = ForcedCode ?? ChosenCode();
                culture = CultureFor(c);
                fallbackTable = c == "it" || c == Fallback ? null : Build(Fallback);
                code = c;
                table = c == "it" ? new Dictionary<string, string>() : Build(c);
            }
        }

        // nessuna lettura delle impostazioni se la lingua è già forzata: il registro attività scrive prima che le impostazioni siano pronte
        private static string ChosenCode()
        {
            string env = Environment.GetEnvironmentVariable("APPMANAGER_DATA") == null ? null : Environment.GetEnvironmentVariable("APPMANAGER_LANG");
            if (!string.IsNullOrEmpty(env)) return Resolve(env);
            try { return Resolve(PeekChoice()); }
            catch { return SystemLanguage(); }
        }

        /// <summary>Legge solo il campo Language del file: AppSettings.Current scrive nel registro, che a sua volta chiede la lingua.</summary>
        private static string PeekChoice()
        {
            string file = AppPaths.SettingsFile;
            if (!File.Exists(file)) return "auto";
            return Json.Str(Json.Parse(File.ReadAllText(file)), "Language") ?? "auto";
        }

        private static CultureInfo CultureFor(string c)
        {
            switch (c)
            {
                case "it": return new CultureInfo("it-IT");
                case "fr": return new CultureInfo("fr-FR");
                case "de": return new CultureInfo("de-DE");
                case "ru": return new CultureInfo("ru-RU");
                case "uk": return new CultureInfo("uk-UA");
                case "es": return new CultureInfo("es-ES");
                default: return new CultureInfo("en-GB");
            }
        }

        // Il catalogo è un file per lingua, con righe "numero<TAB>testo": it.txt dice a quale frase corrisponde ogni numero.
        private static Dictionary<string, string> Build(string lang)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var keys = ReadCatalog("it");
            var values = ReadCatalog(lang);
            foreach (var k in keys)
            {
                string v;
                if (values.TryGetValue(k.Key, out v) && !result.ContainsKey(k.Value)) result[k.Value] = v;
            }
            return result;
        }

        private static Dictionary<int, string> ReadCatalog(string lang)
        {
            var map = new Dictionary<int, string>();
            var s = typeof(L).Assembly.GetManifestResourceStream("AppManager.ui.lang." + lang + ".txt");
            if (s == null) return map;
            using (s)
            using (var r = new StreamReader(s, System.Text.Encoding.UTF8))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    int tab = line.IndexOf('\t');
                    int n;
                    if (tab <= 0 || !int.TryParse(line.Substring(0, tab), NumberStyles.None, CultureInfo.InvariantCulture, out n)) continue;
                    map[n] = Unescape(line.Substring(tab + 1));
                }
            }
            return map;
        }

        private static string Unescape(string s)
        {
            return s.IndexOf('\\') < 0 ? s : s.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");
        }

        /// <summary>Traduce un testo fisso.</summary>
        public static string T(string italian)
        {
            if (string.IsNullOrEmpty(italian)) return italian;
            EnsureLoaded();
            string v;
            if (table.TryGetValue(italian, out v)) return v;
            if (fallbackTable != null && fallbackTable.TryGetValue(italian, out v)) return v;
            return italian;
        }

        /// <summary>Traduce una frase con valori variabili ({0}, {1}…).</summary>
        public static string F(string italian, params object[] args)
        {
            return string.Format(Culture, T(italian), args);
        }

        /// <summary>
        /// Forma giusta per il numero n. Nel catalogo la voce "uno|molti" contiene le forme della lingua:
        /// due per quasi tutte, tre per russo e ucraino (1 файл · 2 файла · 5 файлов).
        /// </summary>
        public static string Plural(int n, string one, string many)
        {
            string forms = T(one + "|" + many);
            if (forms == one + "|" + many) return n == 1 ? one : many;
            var p = forms.Split('|');
            if (p.Length >= 3)
            {
                int n10 = Math.Abs(n) % 10, n100 = Math.Abs(n) % 100;
                if (n10 == 1 && n100 != 11) return p[0];
                return n10 >= 2 && n10 <= 4 && (n100 < 12 || n100 > 14) ? p[1] : p[2];
            }
            return p.Length == 2 ? (n == 1 ? p[0] : p[1]) : forms;
        }

        /// <summary>Traduce i testi fissi di uno XAML prima che venga caricato (Text, Content, ToolTip, Tag, Value…).</summary>
        public static string TranslateXaml(string xaml)
        {
            EnsureLoaded();
            if (code == "it") return xaml;
            return XamlAttr.Replace(xaml, m =>
            {
                string raw = m.Groups[2].Value;
                if (raw.StartsWith("{") || raw.StartsWith("&#x")) return m.Value;
                string plain = raw.Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
                string tr = T(plain);
                if (tr == plain) return m.Value;
                return m.Groups[1].Value + "=\"" + tr.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;") + "\"";
            });
        }

        private static readonly System.Text.RegularExpressions.Regex XamlAttr =
            new System.Text.RegularExpressions.Regex("\\b(Text|Content|ToolTip|Tag)=\"([^\"]*)\"", System.Text.RegularExpressions.RegexOptions.Compiled);
    }
}
