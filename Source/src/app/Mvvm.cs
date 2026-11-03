using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppManager
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        protected void Raise(params string[] names)
        {
            var h = PropertyChanged;
            if (h == null) return;
            foreach (var n in names) h(this, new PropertyChangedEventArgs(n));
        }
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object> execute;
        private readonly Func<object, bool> canExecute;

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            this.execute = execute;
            this.canExecute = canExecute;
        }

        public RelayCommand(Action execute, Func<bool> canExecute = null)
            : this(o => execute(), canExecute == null ? (Func<object, bool>)null : o => canExecute())
        {
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter) { return canExecute == null || canExecute(parameter); }

        public void Execute(object parameter)
        {
            try { execute(parameter); }
            catch (Exception ex)
            {
                Log.Error("Comando", ex);
                MessageBox.Show(ex.Message, "AppManager", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // ====================================================================== Convertitori usati nello XAML

    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        /// <summary>Nasconde mantenendo lo spazio (Hidden invece di Collapsed): quando l'elemento ricompare la pagina non si sposta.</summary>
        public bool KeepSpace { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool b = value is bool ? (bool)value : value != null && !(value is string && ((string)value).Length == 0);
            if (value is int) b = (int)value != 0;
            if (Invert) b = !b;
            return b ? Visibility.Visible : KeepSpace ? Visibility.Hidden : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }

    public sealed class ColorToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                var c = ThemeInfo.Adapt((Color)ColorConverter.ConvertFromString(value as string ?? "#7D8590"));
                double opacity;
                if (parameter != null && double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out opacity))
                    c.A = (byte)(255 * opacity);
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }
            catch { return Brushes.Gray; }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }

    /// <summary>Visibile solo se tutti i valori sono true (es. scheda giusta e risultati presenti).</summary>
    public sealed class AllTrueToVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            foreach (var v in values) if (!(v is bool) || !(bool)v) return Visibility.Collapsed;
            return Visibility.Visible;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            return null;
        }
    }

    /// <summary>True se il valore è uguale al parametro (per RadioButton legati a enum/stringhe).</summary>
    public sealed class EqualsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value != null && parameter != null && value.ToString() == parameter.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool && (bool)value ? parameter : Binding.DoNothing;
        }
    }

    // ====================================================================== Immagini

    internal static class Images
    {
        /// <summary>Copertina verticale (sì) o icona quadrata (no): decide come mostrarla, così un'icona non viene stirata.</summary>
        public static bool IsPortrait(ImageSource img)
        {
            return img != null && img.Height > img.Width * 1.15;
        }

        /// <summary>
        /// Luminosità media (0-1) della parte visibile di un'immagine; -1 se non si può calcolare.
        /// Serve a riconoscere le icone quasi bianche o quasi nere, che su uno sfondo simile sparirebbero.
        /// </summary>
        public static double VisibleLuminance(ImageSource source)
        {
            var bmp = source as BitmapSource;
            if (bmp == null || bmp.PixelWidth == 0 || bmp.PixelHeight == 0) return -1;
            try
            {
                var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                int stride = conv.PixelWidth * 4;
                var px = new byte[stride * conv.PixelHeight];
                conv.CopyPixels(px, stride, 0);
                double sum = 0, weight = 0;
                for (int i = 0; i < px.Length; i += 4)
                {
                    double a = px[i + 3] / 255.0;
                    if (a < 0.2) continue;
                    sum += a * (0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2]) / 255.0;
                    weight += a;
                }
                return weight <= 0 ? -1 : sum / weight;
            }
            catch { return -1; }
        }

        /// <summary>Colore della piastrina dietro un'icona che si confonderebbe con lo sfondo del tema in uso (null se non serve).</summary>
        public static string BackdropFor(double luminance)
        {
            if (luminance < 0) return null;
            if (ThemeInfo.IsLight) return luminance > 0.82 ? "#3A4250" : null;
            return luminance < 0.16 ? "#E8EAF0" : null;
        }

        /// <summary>Codifica un'immagine in PNG, ridotta a <paramref name="maxWidth"/> pixel di larghezza (null se non si può).</summary>
        public static byte[] ToPng(ImageSource source, int maxWidth)
        {
            var bmp = source as BitmapSource;
            if (bmp == null) return null;
            try
            {
                BitmapSource frame = bmp;
                if (bmp.PixelWidth > maxWidth)
                {
                    double k = (double)maxWidth / bmp.PixelWidth;
                    frame = new TransformedBitmap(bmp, new ScaleTransform(k, k));
                }
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(frame));
                using (var ms = new MemoryStream())
                {
                    enc.Save(ms);
                    return ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Immagine non convertita: {0}", ex.Message);
                return null;
            }
        }

        /// <summary>Immagine da byte (PNG, JPG...), ridotta in larghezza; null se i dati non sono un'immagine.</summary>
        public static ImageSource FromBytes(byte[] data, int decodeWidth)
        {
            if (data == null || data.Length == 0) return null;
            try
            {
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(data))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>Carica un'immagine da file in background (ridimensionata), restituendo un oggetto utilizzabile da qualunque thread.</summary>
        public static Task<ImageSource> LoadAsync(string path, int decodeWidth)
        {
            return Task.Run(() => Load(path, decodeWidth));
        }

        public static ImageSource Load(string path, int decodeWidth)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var bmp = new BitmapImage();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                    bmp.StreamSource = fs;
                    bmp.EndInit();
                }
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }

    // ====================================================================== Azioni verso la shell

    internal static class ShellActions
    {
        /// <summary>Apre Esplora risorse sul file o sulla cartella (o sulla cartella esistente più vicina).</summary>
        public static void Reveal(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (File.Exists(path) || Directory.Exists(path))
            {
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
                return;
            }
            string dir = path;
            while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
            if (!string.IsNullOrEmpty(dir)) Process.Start("explorer.exe", "\"" + dir + "\"");
        }

        public static void OpenFolder(string path)
        {
            if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
            else Reveal(path);
        }

        /// <summary>Apre l'Editor del Registro di sistema posizionato sulla chiave indicata.</summary>
        public static void OpenRegistry(string keyPath)
        {
            using (var k = RegUtil.Open(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", true))
            {
                if (k != null) k.SetValue("LastKey", "Computer\\" + RegUtil.LongName(keyPath));
            }
            // "/m" apre una nuova finestra anche se Regedit è già aperto; all'avvio si posiziona su LastKey
            Process.Start("regedit.exe", "/m");
        }

        /// <summary>
        /// Avvia un programma, un gioco o un indirizzo come farebbe un doppio clic. Se AppManager è amministratore si passa da
        /// Esplora risorse: un gioco non deve ereditare i privilegi di amministratore.
        /// </summary>
        public static void Launch(string target)
        {
            string dir = File.Exists(target) ? Path.GetDirectoryName(target) : null;
            if (Native.IsElevated && Native.ShellExecuteViaExplorer(target, dir)) return;
            var info = new ProcessStartInfo(target) { UseShellExecute = true };
            if (dir != null) info.WorkingDirectory = dir;
            Process.Start(info);
        }

        public static void OpenUrl(string url)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + url + "\"") { UseShellExecute = false });
        }
    }
}
