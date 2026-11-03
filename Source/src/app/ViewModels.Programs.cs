using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;

namespace AppManager
{
    // ====================================================================== Pagina "Programmi"

    public sealed class ProgramsPageViewModel : LibraryPageViewModel
    {
        public ProgramsPageViewModel()
        {
            ToggleComponentsCommand = new RelayCommand(() => ShowComponents = !ShowComponents);
        }

        internal override AppKind Kind { get { return AppKind.Program; } }

        public System.Windows.Input.ICommand ToggleComponentsCommand { get; private set; }

        public bool HasComponents { get { return Items.Any(e => e.Entry.IsComponent); } }

        /// <summary>Testo del link accanto al riepilogo: mostra o nasconde runtime, driver e app di Windows.</summary>
        public string ComponentsToggleText
        {
            get
            {
                int hidden = Items.Count(e => e.Entry.IsComponent);
                if (hidden == 0) return null;
                return ShowComponents ? L.T("Nascondi i componenti di Windows") : L.F("Mostra {0}", Fmt.Count(hidden, "componente di Windows", "componenti di Windows"));
            }
        }

        internal override List<AppEntry> Detect(Action<string> progress)
        {
            var games = MainViewModel.Current == null ? new List<AppEntry>() : MainViewModel.Current.Games.Detected;
            var list = ProgramDetector.DetectAll(AppSettings.Current, games, progress);
            Safety.SetInstalledDirs("programs", list.SelectMany(p => p.Dirs.Concat(p.IsSingleFile && p.MainExe != null ? new[] { p.MainExe } : new string[0])));
            return list;
        }

        internal override EntryViewModel CreateItem(AppEntry e)
        {
            return new ProgramViewModel(e);
        }

        /// <summary>Runtime, driver e app di Windows: normalmente nascosti, si vedono su richiesta.</summary>
        public bool ShowComponents
        {
            get { return AppSettings.Current.ShowComponents; }
            set
            {
                AppSettings.Current.ShowComponents = value;
                AppSettings.Current.Save();
                Raise("ShowComponents");
                RefreshView();
            }
        }

        private bool Visible(EntryViewModel e)
        {
            return ShowComponents || !e.Entry.IsComponent;
        }

        internal static string TypeKey(AppEntry e)
        {
            if (e.Launcher == LauncherKind.Store) return "store";
            if (e.Launcher == LauncherKind.Steam) return "steam";
            return e.HasUninstaller ? "installed" : "portable";
        }

        internal override void BuildFilterChips()
        {
            var visible = Items.Where(Visible).ToList();
            var chips = new List<FilterChip> { new FilterChip { Key = "all", Label = L.T("Tutti"), Count = visible.Count, Color = "#5B8CFF" } };
            foreach (var t in new[]
            {
                Tuple.Create("installed", L.T("Installati"), "#8A8F98"), Tuple.Create("portable", "Portable", "#F5B94B"),
                Tuple.Create("store", "Microsoft Store", Launchers.Color(LauncherKind.Store)), Tuple.Create("steam", "Steam", Launchers.Color(LauncherKind.Steam)),
            })
            {
                int n = visible.Count(e => TypeKey(e.Entry) == t.Item1);
                if (n > 0 || t.Item1 == "portable") chips.Add(new FilterChip { Key = t.Item1, Label = t.Item2, Count = n, Color = t.Item3 });
            }
            SetFilterChips(chips);
        }

        internal override bool MatchesFilter(EntryViewModel e)
        {
            if (!Visible(e)) return false;
            return filterKey == "all" || TypeKey(e.Entry) == filterKey;
        }

        /// <summary>Giochi e programmi sono pronti: la Panoramica può confrontarli con l'ultimo avvio.</summary>
        internal override void OnLoaded()
        {
            if (MainViewModel.Current != null) MainViewModel.Current.Overview.OnLibrariesLoaded();
        }

        internal override string SummaryFor(IList<EntryViewModel> visible, long total)
        {
            // il numero dei componenti nascosti compare nel link accanto ("Mostra N componenti di Windows")
            Raise("ComponentsToggleText", "HasComponents");
            return Fmt.Count(visible.Count, "programma", "programmi") + (total > 0 ? L.F(" · {0} su disco", Fmt.Size(total)) : "");
        }
    }

    // ====================================================================== Un programma nell'elenco

    public sealed class ProgramViewModel : EntryViewModel
    {
        private ImageSource icon, bigIcon;
        private bool iconRequested, bigRequested;
        private double iconLuminance = -1;

        internal ProgramViewModel(AppEntry e) : base(e)
        {
        }

        public string TypeLabel
        {
            get
            {
                switch (ProgramsPageViewModel.TypeKey(Entry))
                {
                    case "store": return "Microsoft Store";
                    case "steam": return "Steam";
                    case "portable": return "Portable";
                    default: return L.T("Installato");
                }
            }
        }

        public string TypeColor
        {
            get
            {
                switch (ProgramsPageViewModel.TypeKey(Entry))
                {
                    case "store": return Launchers.Color(LauncherKind.Store);
                    case "steam": return Launchers.Color(LauncherKind.Steam);
                    case "portable": return "#F5B94B";
                    default: return "#8A8F98";
                }
            }
        }

        public bool IsComponent { get { return Entry.IsComponent; } }
        public string ComponentLabel { get { return Entry.ComponentLabel; } }

        /// <summary>Riga sotto il nome: editore e versione.</summary>
        public string DetailLine
        {
            get
            {
                var parts = new List<string>();
                if (Publisher != null) parts.Add(Publisher);
                if (Version != null) parts.Add(char.IsDigit(Version[0]) ? "v" + Version : Version);
                return parts.Count == 0 ? InstallDir : string.Join("  ·  ", parts);
            }
        }

        public ImageSource Icon
        {
            get
            {
                if (!iconRequested)
                {
                    iconRequested = true;
                    LoadIcon(false);
                }
                return icon;
            }
        }

        public ImageSource BigIcon
        {
            get
            {
                if (!bigRequested)
                {
                    bigRequested = true;
                    LoadIcon(true);
                }
                return bigIcon ?? icon;
            }
        }

        public bool HasIcon { get { return icon != null; } }

        /// <summary>Piastrina dietro un'icona che sul tema in uso non si vedrebbe (per esempio bianca sul chiaro).</summary>
        public string IconBackdrop { get { return Images.BackdropFor(iconLuminance); } }

        internal override void ReleaseDetails()
        {
            if (bigIcon == null) return;
            bigIcon = null;
            bigRequested = false;
            Raise("BigIcon", "Thumbnail");
        }
        public override ImageSource Thumbnail { get { return BigIcon; } }

        private async void LoadIcon(bool big)
        {
            var e = Entry;
            ImageSource img = null;
            try { img = await Task.Run(() => LoadIconFile(e.IconPath, big ? 96 : 48) ?? (e.MainExe != e.IconPath ? LoadIconFile(e.MainExe, big ? 96 : 48) : null)); }
            catch (Exception ex) { Log.Error(L.F("Icona di {0}", e.Name), ex); }
            if (img == null) return;
            if (big) { bigIcon = img; Raise("BigIcon", "Thumbnail"); }
            else
            {
                icon = img;
                iconLuminance = await Task.Run(() => Images.VisibleLuminance(img));
                Raise("Icon", "HasIcon", "BigIcon", "Thumbnail", "IconBackdrop");
            }
        }

        /// <summary>Icona da "file,indice" (come DisplayIcon), da un .ico/.exe/.dll o da un'immagine PNG (logo delle app dello Store).</summary>
        private static ImageSource LoadIconFile(string spec, int size)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            string file = spec.Trim();
            int index = 0;
            int comma = file.LastIndexOf(',');
            if (comma > 2)
            {
                int n;
                if (int.TryParse(file.Substring(comma + 1).Trim(), out n)) { index = n; file = file.Substring(0, comma); }
            }
            file = PathUtil.Normalize(file.Trim().Trim('"'));
            if (file == null || !File.Exists(file)) return null;
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg") return Images.Load(file, size * 2);
            return Native.ExtractIcon(file, index, size);
        }
    }
}
