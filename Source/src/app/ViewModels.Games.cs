using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;

namespace AppManager
{
    // ====================================================================== Pagina "Giochi"

    public sealed class GamesPageViewModel : LibraryPageViewModel
    {
        public GamesPageViewModel()
        {
            SavesCommand = new RelayCommand(p => MainViewModel.Current.OpenSaves(p as EntryViewModel ?? Selected), p => (p as EntryViewModel ?? Selected) != null);
            var t = LoadAsync();
        }

        /// <summary>Gestore dei salvataggi del gioco selezionato (backup, profili, nuova partita).</summary>
        public ICommand SavesCommand { get; private set; }

        internal override AppKind Kind { get { return AppKind.Game; } }

        internal override List<AppEntry> Detect(Action<string> progress)
        {
            SteamInfo.Reset();
            ProfileBuilder.ClearCache();
            Safety.Invalidate();
            var list = GameDetector.DetectAll(AppSettings.Current, progress);
            Safety.SetInstalledDirs("games", list.SelectMany(g => g.Dirs));
            // immagini scaricate di giochi che non sono più sul PC: si eliminano
            try { CoverService.Prune(list); }
            catch (Exception ex) { Log.Error("Pulizia cache copertine", ex); }
            return list;
        }

        internal override EntryViewModel CreateItem(AppEntry e)
        {
            return new GameViewModel(e);
        }

        internal override void BuildFilterChips()
        {
            var chips = new List<FilterChip> { new FilterChip { Key = "all", Label = L.T("Tutti"), Count = Items.Count, Color = "#5B8CFF" } };
            foreach (var grp in Items.GroupBy(g => g.Entry.Launcher).OrderByDescending(x => x.Count()))
                chips.Add(new FilterChip { Key = grp.Key.ToString(), Label = Launchers.Label(grp.Key), Count = grp.Count(), Color = Launchers.Color(grp.Key) });
            SetFilterChips(chips);
        }

        internal override bool MatchesFilter(EntryViewModel e)
        {
            return filterKey == "all" || e.Entry.Launcher.ToString() == filterKey;
        }

        internal override string SummaryFor(IList<EntryViewModel> visible, long total)
        {
            return Fmt.Count(visible.Count, "gioco", "giochi") + (total > 0 ? L.F(" · {0} su disco", Fmt.Size(total)) : "");
        }

        /// <summary>I programmi si rilevano escludendo i giochi: dopo ogni aggiornamento dei giochi va aggiornato anche il loro elenco.</summary>
        internal override void OnLoaded()
        {
            if (MainViewModel.Current != null)
            {
                var t = MainViewModel.Current.Programs.LoadAsync();
            }
        }
    }

    // ====================================================================== Un gioco nell'elenco

    public sealed class GameViewModel : EntryViewModel
    {
        private ImageSource cover, header, hero, logo, icon;
        private bool coverRequested, detailsRequested;
        private string engine;

        internal GameViewModel(AppEntry g) : base(g)
        {
        }

        public string Engine { get { return engine; } private set { if (Set(ref engine, value)) Raise("HasEngine"); } }
        public bool HasEngine { get { return !string.IsNullOrEmpty(engine) && engine != L.T("Gioco"); } }

        public ImageSource Cover
        {
            get
            {
                if (!coverRequested)
                {
                    coverRequested = true;
                    LoadCover();
                }
                return cover;
            }
        }

        public bool HasCover { get { return cover != null; } }
        public ImageSource Header { get { return header; } }
        public ImageSource Hero { get { return hero; } }
        public ImageSource Logo { get { return logo; } }
        public bool HasLogo { get { return logo != null; } }
        public ImageSource Icon { get { return icon; } }
        public bool HasIcon { get { return icon != null && cover == null; } }
        public override ImageSource Thumbnail { get { return Cover ?? icon; } }

        // Immagine grande del pannello: hero + logo (stile libreria Steam), altrimenti la testata con il titolo,
        // altrimenti l'hero da solo; la copertina sfocata solo se non c'è nient'altro.
        public bool ShowHero { get { return hero != null && (logo != null || header == null); } }
        public bool ShowHeader { get { return header != null && !ShowHero; } }
        public bool ShowBlur { get { return !ShowHero && !ShowHeader; } }

        private void RaiseArt()
        {
            Raise("Header", "Hero", "Logo", "HasLogo", "ShowHero", "ShowHeader", "ShowBlur");
        }

        private async void LoadCover()
        {
            var g = Entry;
            var img = await Images.LoadAsync(g.CoverImage, 120);
            if (img != null)
            {
                cover = img;
                Raise("Cover", "HasCover", "HasIcon", "Thumbnail");
            }
            else
            {
                // subito l'icona dell'eseguibile, intanto si cercano le immagini online
                icon = await Task.Run(() => FindIcon(g));
                if (icon != null) Raise("Icon", "HasIcon", "Thumbnail");
            }
            bool lowRes = g.CoverImage != null && CoverService.IsLowRes(g.CoverImage);
            bool needed = g.CoverImage == null || lowRes || (g.HeaderImage == null && g.HeroImage == null);
            if (!CoverService.Enabled || !needed) return;
            CoverResult online = null;
            try { online = await Task.Run(() => CoverService.ForGame(g)); }
            catch (Exception ex) { Log.Error(L.F("Immagini di {0}", g.Name), ex); }
            if (online == null) return;
            if (online.Portrait != null && (cover == null || lowRes))
            {
                var better = await Images.LoadAsync(online.Portrait, 120);
                if (better != null)
                {
                    g.CoverImage = online.Portrait;
                    cover = better;
                    Raise("Cover", "HasCover", "HasIcon", "Thumbnail");
                }
            }
            bool changed = false;
            if (g.HeaderImage == null && online.Header != null) { g.HeaderImage = online.Header; changed = true; }
            if (g.HeroImage == null && online.Hero != null) { g.HeroImage = online.Hero; changed = true; }
            if (g.LogoImage == null && online.Logo != null) { g.LogoImage = online.Logo; changed = true; }
            if (changed && detailsRequested) await LoadArt();
        }

        /// <summary>
        /// L'icona del gioco: quella dell'eseguibile principale, poi quella che il gioco dichiara a Windows (per i giochi di Steam è il loro logo),
        /// poi quella degli altri eseguibili della cartella, dal più grande. Un lanciatore senza icona propria non lascia il gioco senza immagine.
        /// </summary>
        internal static ImageSource FindIcon(AppEntry g)
        {
            try
            {
                if (g.MainExe != null && File.Exists(g.MainExe))
                {
                    var img = Native.ExtractIcon(g.MainExe, 96);
                    if (img != null) return img;
                }
                if (!string.IsNullOrEmpty(g.IconPath))
                {
                    var parts = g.IconPath.Split(',');
                    string file = PathUtil.Normalize(parts[0].Trim().Trim('"'));
                    int index;
                    if (parts.Length < 2 || !int.TryParse(parts[1].Trim(), out index)) index = 0;
                    string ext = file == null ? "" : Path.GetExtension(file).ToLowerInvariant();
                    if (file != null && File.Exists(file) && (ext == ".exe" || ext == ".ico" || ext == ".dll") && !Path.GetFileName(file).StartsWith("unins", StringComparison.OrdinalIgnoreCase))
                    {
                        var img = Native.ExtractIcon(file, index, 96);
                        if (img != null) return img;
                    }
                }
                if (string.IsNullOrEmpty(g.InstallDir) || !Directory.Exists(g.InstallDir)) return null;
                var exes = PathUtil.FilesNoLinks(g.InstallDir).Take(4000)
                    .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !InstalledApps.IsAuxiliaryExe(f) && !PathUtil.Same(f, g.MainExe))
                    .Take(200).ToList()
                    .OrderByDescending(f => f.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(f => { try { return new FileInfo(f).Length; } catch { return 0L; } })
                    .Take(8);
                foreach (var exe in exes)
                {
                    var img = Native.ExtractIcon(exe, 96);
                    if (img != null) return img;
                }
                // eseguibili senza icona: a volte il gioco ne porta una a parte nella sua cartella
                foreach (var ico in PathUtil.Files(g.InstallDir, "*.ico").Take(3))
                {
                    var img = Native.ExtractIcon(ico, 96);
                    if (img != null) return img;
                }
            }
            catch (Exception ex) { Log.Warn("Icona di {0}: {1}", g.Name, ex.Message); }
            return null;
        }

        private async Task LoadArt()
        {
            header = await Images.LoadAsync(Entry.HeaderImage, 760);
            hero = await Images.LoadAsync(Entry.HeroImage, 900);
            logo = await Images.LoadAsync(Entry.LogoImage, 420);
            RaiseArt();
        }

        internal override void ReleaseDetails()
        {
            if (!detailsRequested) return;
            detailsRequested = false;
            header = hero = logo = null;
            RaiseArt();
        }

        internal override async void LoadDetails()
        {
            if (detailsRequested) return;
            detailsRequested = true;
            await LoadArt();
            try
            {
                var e = Entry;
                Engine = await Task.Run(() => ProfileBuilder.Get(e).Engine);
            }
            catch { }
        }
    }
}
