using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace AppManager
{
    /// <summary>Tema in uso (chiaro o scuro) e adattamento dei colori decisi dal codice (badge, stati, launcher).</summary>
    internal static class ThemeInfo
    {
        private const double MinContrast = 4.5;
        private static readonly Dictionary<Color, Color> Adapted = new Dictionary<Color, Color>();

        public static bool IsLight { get; set; }

        /// <summary>Il tema delle app scelto in Windows. Se non si può leggere si resta sullo scuro, quello storico del programma.</summary>
        public static bool SystemUsesLight()
        {
            try
            {
                using (var k = RegUtil.Open(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v != 0;
                }
            }
            catch { return false; }
        }

        public static bool Resolve(string choice)
        {
            return choice == "light" || (choice != "dark" && SystemUsesLight());
        }

        /// <summary>
        /// I colori pensati per lo sfondo scuro (giallo, verde chiaro…) sul bianco non si leggono:
        /// nel tema chiaro si scuriscono finché il contrasto non basta, senza cambiare tinta.
        /// </summary>
        public static Color Adapt(Color c)
        {
            if (!IsLight) return c;
            Color r;
            lock (Adapted)
            {
                if (Adapted.TryGetValue(c, out r)) return r;
                r = c;
                for (int i = 0; i < 40 && Contrast(r, Colors.White) < MinContrast; i++)
                    r = Color.FromArgb(r.A, (byte)(r.R * 0.93), (byte)(r.G * 0.93), (byte)(r.B * 0.93));
                Adapted[c] = r;
            }
            return r;
        }

        public static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        private static double Luminance(Color c)
        {
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }

        private static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
    }
}
