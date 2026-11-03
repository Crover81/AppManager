using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AppManager
{
    /// <summary>Elenco di giochi e programmi in un file CSV: utile per ricordare cosa reinstallare o per confrontare due PC.</summary>
    internal static class AppExport
    {
        /// <summary>Testo CSV con il separatore ";" (quello che Excel si aspetta con le impostazioni europee).</summary>
        public static string ToCsv(IEnumerable<AppEntry> entries)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(";", new[]
            {
                L.T("Tipo"), L.T("Nome"), L.T("Editore"), L.T("Versione"), L.T("Dimensione (MB)"), L.T("Cartella"), L.T("Rilevato tramite"), L.T("Installato il"),
            }.Select(Cell)));
            foreach (var e in entries.OrderBy(x => x.Kind).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                sb.AppendLine(string.Join(";", new[]
                {
                    e.IsGame ? L.T("Gioco") : L.T("Programma"),
                    e.Name,
                    e.Publisher,
                    e.Version,
                    e.SizeBytes > 0 ? (e.SizeBytes / (1024.0 * 1024)).ToString("0.#", L.Culture) : "",
                    e.IsSingleFile ? e.MainExe : e.InstallDir,
                    e.Source,
                    e.InstallDate.HasValue ? e.InstallDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "",
                }.Select(Cell)));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Una cella: virgolette se servono. Un testo che inizia con = + - @ verrebbe preso da Excel per una formula:
        /// il nome di un programma non deve poter eseguire nulla, quindi si antepone un apostrofo.
        /// </summary>
        internal static string Cell(string value)
        {
            string v = (value ?? "").Replace("\r", " ").Replace("\n", " ");
            if (v.Length > 0 && "=+-@\t".IndexOf(v[0]) >= 0) v = "'" + v;
            return v.IndexOfAny(new[] { ';', '"', ',' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }
    }
}
