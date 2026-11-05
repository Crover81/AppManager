using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace AppManager
{
    /// <summary>Un gruppo di file che si possono svuotare senza perdere nulla (log, dump, file temporanei, cache).</summary>
    internal sealed class CleanableGroup
    {
        public string Key, Name;
        public long Size;
        public int Count;
    }

    /// <summary>Una categoria di gruppi (Windows, browser, launcher…) che l'avviso può contare o no.</summary>
    internal sealed class CleanableCategory
    {
        public readonly string Key, Name, Detail;
        public readonly string[] Groups;
        public CleanableCategory(string key, string name, string detail, params string[] groups) { Key = key; Name = name; Detail = detail; Groups = groups; }
    }

    internal sealed class CleanableSummary
    {
        public readonly List<CleanableGroup> Groups = new List<CleanableGroup>();
        public long Total { get { return Groups.Sum(g => g.Size); } }
    }

    /// <summary>
    /// Ogni tanto, con la sorveglianza attiva, misura log, dump, file temporanei e cache (quelli della pagina Log) e avvisa
    /// quando superano la soglia. Senza assillare: al massimo un controllo ogni 6 ore, «Non ora» rimanda di 7 giorni, un avviso
    /// lasciato sparire da solo di 3; i gruppi esclusi non contano. Non elimina mai nulla.
    /// </summary>
    internal static class CleanableCheck
    {
        public static readonly int[] ThresholdChoicesMb = { 500, 1024, 2048, 5120 };
        public const int DefaultThresholdMb = 1024;
        public static readonly TimeSpan Interval = TimeSpan.FromHours(6), FirstDelay = TimeSpan.FromMinutes(10);
        public const int SnoozeDays = 7, UnansweredDays = 3;

        /// <summary>I file da svuotare superano la soglia (chiamato dal thread della sorveglianza).</summary>
        public static event Action<CleanableSummary> Found;

        private static DateTime lastRun = DateTime.MinValue;

        private static AppSettings S { get { return AppSettings.Current; } }

        public static long ThresholdBytes { get { return (S.CleanableThresholdMb > 0 ? S.CleanableThresholdMb : DefaultThresholdMb) * 1024L * 1024L; } }

        public static DateTime? SnoozedUntil
        {
            get
            {
                DateTime d;
                return DateTime.TryParse(S.CleanableSnoozeUntil, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d) ? d : (DateTime?)null;
            }
        }

        /// <summary>Se ora tocca a un controllo: attivo, non rimandato, non troppo presto dall'avvio o dall'ultimo.</summary>
        internal static bool IsDue(DateTime now, DateTime watcherStarted, DateTime last, DateTime? snoozedUntil, bool enabled)
        {
            if (!enabled) return false;
            if (snoozedUntil.HasValue && now < snoozedUntil.Value) return false;
            return now - watcherStarted >= FirstDelay && now - last >= Interval;
        }

        public static void RunIfDue(DateTime watcherStarted, CancellationToken ct)
        {
            var now = DateTime.Now;
            if (!IsDue(now, watcherStarted, lastRun, SnoozedUntil, S.CleanableAlert)) return;
            lastRun = now;
            var summary = Measure(ct);
            Log.Info("Controllo di log e cache: {0} da poter liberare", Fmt.Size(summary.Total));
            if (summary.Total < ThresholdBytes) return;
            var h = Found;
            if (h != null) try { h(summary); } catch (Exception ex) { Log.Error("Avviso di log e cache", ex); }
        }

        /// <summary>Gli stessi file della pagina Log (solo quelli più vecchi di 7 giorni), meno i gruppi esclusi.</summary>
        public static CleanableSummary Measure(CancellationToken ct)
        {
            int locked;
            var items = LogJanitor.Find(7, null, ct, out locked);
            var summary = new CleanableSummary();
            foreach (var g in items.Where(i => Counts(i.GroupKey)).GroupBy(i => i.GroupKey))
                summary.Groups.Add(new CleanableGroup { Key = g.Key, Name = g.First().GroupName, Size = g.Sum(i => Math.Max(0, i.Size)), Count = g.Count() });
            summary.Groups.Sort((a, b) => b.Size.CompareTo(a.Size));
            return summary;
        }

        /// <summary>Le categorie che si accendono e spengono nelle Impostazioni. Un gruppo senza categoria conta sempre.</summary>
        internal static List<CleanableCategory> Categories()
        {
            return new List<CleanableCategory>
            {
                new CleanableCategory("windows", L.T("Windows"), L.T("Log, segnalazioni di errore e dump"), "log:windows", "log:wer", "log:dump", "log:temp"),
                new CleanableCategory("temp", L.T("File temporanei"), L.T("Di Windows e del tuo utente"), "log:tempfiles"),
                new CleanableCategory("updates", L.T("Aggiornamenti di Windows"), L.T("Download già installati"), "log:wucache", "log:docache"),
                new CleanableCategory("driver", L.T("Driver video"), L.T("Log, pacchetti già installati, cache degli shader"), "log:driver", "log:driverpkg", "log:shader"),
                new CleanableCategory("browser", L.T("Browser"), L.T("Solo la cache: password e cronologia restano"), "log:browser"),
                new CleanableCategory("launcher", L.T("Launcher"), L.T("Log di Steam, Epic, EA, Ubisoft, GOG e Battle.net"), "log:steam", "log:epic", "log:ea", "log:ubi", "log:gog", "log:bnet"),
                new CleanableCategory("games", L.T("Giochi"), L.T("Log dei giochi Unity e Unreal Engine"), "log:unity", "log:unreal"),
            };
        }

        internal static CleanableCategory CategoryOf(string groupKey)
        {
            return groupKey == null ? null : Categories().FirstOrDefault(c => c.Groups.Contains(groupKey, StringComparer.OrdinalIgnoreCase));
        }

        public static bool IsCategoryOn(string category)
        {
            return S.CleanableOff == null || !S.CleanableOff.Contains(category, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// L'interruttore vale per tutta la categoria: acceso, contano tutti i suoi gruppi; spento, nessuno.
        /// Le esclusioni dei singoli gruppi della categoria vengono quindi tolte.
        /// </summary>
        public static void SetCategoryOn(string category, bool on)
        {
            if (string.IsNullOrEmpty(category)) return;
            if (S.CleanableOff == null) S.CleanableOff = new List<string>();
            S.CleanableOff.RemoveAll(k => k.Equals(category, StringComparison.OrdinalIgnoreCase));
            if (!on) S.CleanableOff.Add(category);
            var c = Categories().FirstOrDefault(x => x.Key.Equals(category, StringComparison.OrdinalIgnoreCase));
            if (c != null && S.CleanableExcluded != null) S.CleanableExcluded.RemoveAll(k => c.Groups.Contains(k, StringComparer.OrdinalIgnoreCase));
            S.Save();
        }

        /// <summary>I gruppi della categoria esclusi uno per uno dalla pagina Log (la categoria resta accesa per gli altri).</summary>
        internal static List<string> ExcludedIn(CleanableCategory c)
        {
            return c.Groups.Where(IsExcluded).ToList();
        }

        /// <summary>
        /// Tiene coerenti interruttori ed esclusioni: una categoria con tutti i gruppi esclusi diventa spenta,
        /// una categoria spenta non tiene esclusioni dei suoi gruppi. Vale anche per impostazioni salvate prima.
        /// </summary>
        public static void Normalize()
        {
            bool changed = false;
            foreach (var c in Categories())
            {
                var excluded = ExcludedIn(c);
                if (excluded.Count == 0) continue;
                if (IsCategoryOn(c.Key) && excluded.Count < c.Groups.Length) continue;
                if (S.CleanableOff == null) S.CleanableOff = new List<string>();
                if (IsCategoryOn(c.Key)) S.CleanableOff.Add(c.Key);
                S.CleanableExcluded.RemoveAll(k => c.Groups.Contains(k, StringComparer.OrdinalIgnoreCase));
                changed = true;
            }
            if (changed) S.Save();
        }

        /// <summary>Il gruppo conta per l'avviso: la sua categoria è accesa e non è escluso uno per uno.</summary>
        public static bool Counts(string key)
        {
            if (key == null || IsExcluded(key)) return false;
            var c = CategoryOf(key);
            return c == null || IsCategoryOn(c.Key);
        }

        /// <summary>
        /// Dalla pagina Log: fa contare (o no) un solo gruppo. Escluso l'ultimo gruppo, la categoria si spegne
        /// (e l'interruttore lo mostra). Incluso un gruppo di una categoria spenta, la riaccende solo per quello.
        /// </summary>
        public static void SetCounts(string key, bool counts)
        {
            if (string.IsNullOrEmpty(key)) return;
            var c = CategoryOf(key);
            if (counts && c != null && !IsCategoryOn(c.Key))
            {
                SetCategoryOn(c.Key, true);
                foreach (var g in c.Groups) if (!g.Equals(key, StringComparison.OrdinalIgnoreCase)) SetExcluded(g, true);
            }
            SetExcluded(key, !counts);
            Normalize();
        }

        public static bool IsExcluded(string key)
        {
            return key != null && S.CleanableExcluded != null && S.CleanableExcluded.Contains(key, StringComparer.OrdinalIgnoreCase);
        }

        public static void SetExcluded(string key, bool excluded)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (S.CleanableExcluded == null) S.CleanableExcluded = new List<string>();
            S.CleanableExcluded.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (excluded) S.CleanableExcluded.Add(key);
            S.Save();
        }

        public static void ClearExclusions()
        {
            S.CleanableExcluded = new List<string>();
            S.Save();
        }

        /// <summary>«Non ora» (7 giorni) o avviso sparito senza risposta (3 giorni).</summary>
        public static void Snooze(int days)
        {
            S.CleanableSnoozeUntil = DateTime.Now.AddDays(days).ToString("o");
            S.Save();
        }

        /// <summary>Solo per le prove: il prossimo controllo non aspetta le 6 ore.</summary>
        internal static void ResetForTests() { lastRun = DateTime.MinValue; }
    }
}
