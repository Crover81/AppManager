using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AppManager
{
    /// <summary>Le due sezioni del programma.</summary>
    internal enum AppKind { Game, Program }

    internal enum LauncherKind { Steam, Epic, GOG, Ubisoft, EA, BattleNet, Rockstar, Amazon, Xbox, Store, Other }

    /// <summary>Come è stato trovato un gioco o un programma (vale il primo metodo che lo trova).</summary>
    internal enum FoundVia { Launcher, Installed, Store, Shortcut, Folder, Manual }

    internal static class Launchers
    {
        public static string Label(LauncherKind k)
        {
            switch (k)
            {
                case LauncherKind.Steam: return "Steam";
                case LauncherKind.Epic: return "Epic Games";
                case LauncherKind.GOG: return "GOG";
                case LauncherKind.Ubisoft: return "Ubisoft Connect";
                case LauncherKind.EA: return "EA app";
                case LauncherKind.BattleNet: return "Battle.net";
                case LauncherKind.Rockstar: return "Rockstar";
                case LauncherKind.Amazon: return "Amazon Games";
                case LauncherKind.Xbox: return "Xbox";
                case LauncherKind.Store: return "Microsoft Store";
                default: return L.T("Altro");
            }
        }

        /// <summary>Colore del badge (RGB).</summary>
        public static string Color(LauncherKind k)
        {
            switch (k)
            {
                case LauncherKind.Steam: return "#3D8BD9";
                case LauncherKind.Epic: return "#8A8F98";
                case LauncherKind.GOG: return "#A34FD1";
                case LauncherKind.Ubisoft: return "#2F7BFF";
                case LauncherKind.EA: return "#FF5A5F";
                case LauncherKind.BattleNet: return "#1FA2FF";
                case LauncherKind.Rockstar: return "#F5A623";
                case LauncherKind.Amazon: return "#FF9900";
                case LauncherKind.Xbox: return "#3FB950";
                case LauncherKind.Store: return "#2AA1F0";
                default: return "#7D8590";
            }
        }
    }

    /// <summary>Un gioco o un programma rilevato sul PC.</summary>
    internal sealed class AppEntry
    {
        public string Id;
        public AppKind Kind;
        public string Name;
        public LauncherKind Launcher;
        public string InstallDir;
        /// <summary>Altre cartelle dello stesso programma (es. quella indicata dai collegamenti, se diversa).</summary>
        public readonly List<string> ExtraDirs = new List<string>();
        public long SizeBytes = -1;
        public string Publisher;
        public string Version;
        public DateTime? InstallDate;
        public string Source;
        public FoundVia Via;
        /// <summary>Tutti i metodi che hanno trovato questo gioco o programma (Via è il primo): servono ai contatori delle Impostazioni.</summary>
        public readonly HashSet<FoundVia> FoundBy = new HashSet<FoundVia>();

        /// <summary>Runtime, driver o app di Windows: nell'elenco dei programmi si vedono solo su richiesta.</summary>
        public bool IsComponent;
        public string ComponentLabel;
        /// <summary>La cartella contiene quelle di altri giochi o programmi (es. Steam): non va mai proposta per intero.</summary>
        public bool ContainsOthers;
        /// <summary>Programma portable composto da un solo eseguibile, in una cartella condivisa con altri file.</summary>
        public bool IsSingleFile;

        public string SteamAppId;
        public string SteamLibrary;       // ...\steamapps
        public string SteamManifestPath;

        public string EpicAppName;
        public string EpicManifestPath;

        public string UninstallKey;       // percorso completo della voce "Uninstall" nel registro
        public string UninstallString;
        public string QuietUninstallString;

        public string PackageName;        // pacchetti MSIX/AppX: giochi Xbox e app dello Store
        public string PackageFamily;
        public string PackageFullName;

        public string MainExe;
        public string IconPath;           // "file,indice" (come DisplayIcon) o immagine PNG (logo delle app dello Store)
        public string CoverImage;         // verticale (600x900)
        public string HeaderImage;        // orizzontale con il titolo (testata)
        public string HeroImage;          // orizzontale senza titolo (sfondo)
        public string LogoImage;          // logo trasparente da sovrapporre all'hero
        public bool IsManual;

        public bool IsGame { get { return Kind == AppKind.Game; } }

        /// <summary>"gioco" o "programma", per i testi.</summary>
        public string Noun { get { return IsGame ? L.T("gioco") : L.T("programma"); } }

        public bool HasUninstaller
        {
            get
            {
                return Launcher == LauncherKind.Steam || Launcher == LauncherKind.Epic || Launcher == LauncherKind.Xbox
                    || Launcher == LauncherKind.Store || !string.IsNullOrEmpty(UninstallString);
            }
        }

        /// <summary>Cartelle del gioco o programma che esistono ancora.</summary>
        public IEnumerable<string> Dirs
        {
            get
            {
                if (!string.IsNullOrEmpty(InstallDir)) yield return InstallDir;
                foreach (var d in ExtraDirs) if (!string.IsNullOrEmpty(d)) yield return d;
            }
        }

        public bool IsInstalled
        {
            get
            {
                if (Launcher == LauncherKind.Steam) return SteamManifestPath != null && File.Exists(SteamManifestPath);
                if (Launcher == LauncherKind.Epic) return EpicManifestPath != null && File.Exists(EpicManifestPath);
                if (IsSingleFile) return MainExe != null && File.Exists(MainExe);
                return !string.IsNullOrEmpty(InstallDir) && Directory.Exists(InstallDir);
            }
        }

        public override string ToString() { return Name + " [" + Kind + ", " + Launcher + "] " + (InstallDir ?? MainExe); }
    }

    /// <summary>"Impronta" di un gioco o programma: tutto ciò che serve per riconoscerne i file sparsi nel sistema.</summary>
    internal sealed class AppProfile
    {
        public AppEntry Entry;
        public string InstallDir;

        /// <summary>Nomi candidati (forma originale) → motivo.</summary>
        public readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Nomi normalizzati "forti" (NameUtil.Norm).</summary>
        public readonly HashSet<string> NameKeys = new HashSet<string>();

        /// <summary>Nomi normalizzati "deboli" (parti del titolo): corrispondenze da verificare.</summary>
        public readonly HashSet<string> WeakKeys = new HashSet<string>();

        /// <summary>Nomi brevi (3 lettere) accettati solo perché coincidono con l'eseguibile principale (es. "vlc").</summary>
        public readonly HashSet<string> ShortKeys = new HashSet<string>();

        public readonly HashSet<string> ExeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> ExePaths = new List<string>();
        public readonly List<string> UnrealProjects = new List<string>();
        public readonly HashSet<string> SteamAppIds = new HashSet<string>();
        public readonly List<string> PublisherKeys = new List<string>();
        public string UnityCompany;
        public string UnityProduct;
        public string Engine;

        /// <summary>Cartelle dati trovate nei file Prefetch di Windows (accessi reali del gioco o programma).</summary>
        public readonly List<string> PrefetchDirs = new List<string>();
        public int PrefetchFiles;

        public bool IsGame { get { return Entry.IsGame; } }

        public bool Matches(string folderName)
        {
            return NameKeys.Contains(NameUtil.Norm(folderName));
        }
    }

    internal enum ItemKind { Directory, File, RegistryKey, RegistryValue, FirewallRule, ScheduledTask, Service, PathEntry }

    internal enum ItemCategory { AppFolder, Saves, Settings, Media, Cache, Startup, Shortcuts, Registry, Other }

    internal enum Confidence { Low = 0, Medium = 1, High = 2 }

    internal static class Categories
    {
        public static readonly ItemCategory[] Order =
        {
            ItemCategory.AppFolder, ItemCategory.Saves, ItemCategory.Settings, ItemCategory.Media, ItemCategory.Cache,
            ItemCategory.Startup, ItemCategory.Shortcuts, ItemCategory.Registry, ItemCategory.Other
        };

        public static string Label(ItemCategory c, AppKind kind = AppKind.Game)
        {
            switch (c)
            {
                case ItemCategory.AppFolder: return kind == AppKind.Game ? L.T("Cartella del gioco") : L.T("Cartella del programma");
                case ItemCategory.Saves: return kind == AppKind.Game ? L.T("Salvataggi") : L.T("Documenti e dati salvati");
                case ItemCategory.Settings: return L.T("Impostazioni e dati");
                case ItemCategory.Media: return L.T("Screenshot e video");
                case ItemCategory.Cache: return L.T("Cache, log e crash");
                case ItemCategory.Startup: return L.T("Avvio automatico, servizi e attività");
                case ItemCategory.Shortcuts: return L.T("Collegamenti");
                case ItemCategory.Registry: return L.T("Registro di sistema");
                default: return L.T("Altro");
            }
        }

        /// <summary>Glifo Segoe MDL2/Fluent Icons.</summary>
        public static string Glyph(ItemCategory c)
        {
            switch (c)
            {
                case ItemCategory.AppFolder: return "";
                case ItemCategory.Saves: return "";
                case ItemCategory.Settings: return "";
                case ItemCategory.Media: return "";
                case ItemCategory.Cache: return "";
                case ItemCategory.Startup: return "";
                case ItemCategory.Shortcuts: return "";
                case ItemCategory.Registry: return "";
                default: return "";
            }
        }

        /// <summary>Categorie incluse nel backup automatico prima della pulizia.</summary>
        public static bool IsBackedUp(ItemCategory c)
        {
            return c == ItemCategory.Saves || c == ItemCategory.Settings || c == ItemCategory.Registry || c == ItemCategory.Startup;
        }
    }

    /// <summary>
    /// Un elemento residuo: file, cartella, chiave o valore di registro, regola firewall, attività pianificata,
    /// servizio o voce della variabile PATH.
    /// </summary>
    internal sealed class LeftoverItem
    {
        public ItemKind Kind;
        public ItemCategory Category;
        public string Path;          // file/cartella, chiave di registro, eseguibile (firewall), attività, nome del servizio, chiave della variabile
        public string ValueName;     // RegistryValue e PathEntry (nome della variabile)
        public string Data;          // PathEntry: la cartella da togliere; Service: nome visualizzato
        public long Size = -1;
        public int FileCount;
        public string Reason;
        public Confidence Confidence;
        public DateTime? LastWrite;
        public bool NeedsAdmin;      // attività create da un amministratore o dal sistema
        public string GroupKey;      // raggruppamento dei residui orfani
        public string GroupName;
        public string GroupSource;
        public string GroupImage;
        public AppKind GroupKind;

        public bool IsRegistry { get { return Kind == ItemKind.RegistryKey || Kind == ItemKind.RegistryValue; } }

        /// <summary>File o cartella (tutto il resto è configurazione di sistema).</summary>
        public bool IsFileSystem { get { return Kind == ItemKind.Directory || Kind == ItemKind.File; } }

        public bool RequiresAdmin
        {
            get
            {
                if (Kind == ItemKind.FirewallRule || Kind == ItemKind.Service || NeedsAdmin) return true;
                if (Kind == ItemKind.ScheduledTask) return false;
                if (IsRegistry || Kind == ItemKind.PathEntry)
                    return Path.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase) || Path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase);
                // la cartella di Steam è scrivibile dall'utente; Programmi, ProgramData e Windows no
                if (SteamInfo.SteamPath != null && PathUtil.IsUnder(Path, SteamInfo.SteamPath)) return false;
                return PathUtil.IsUnder(Path, KnownDirs.ProgramFiles) || PathUtil.IsUnder(Path, KnownDirs.ProgramFilesX86)
                    || PathUtil.IsUnder(Path, KnownDirs.ProgramData) || PathUtil.IsUnder(Path, KnownDirs.Windows);
            }
        }

        public string Key
        {
            get { return Kind + "|" + (Path ?? "").ToLowerInvariant() + "|" + (ValueName ?? "").ToLowerInvariant() + "|" + (Kind == ItemKind.PathEntry ? (Data ?? "").ToLowerInvariant() : ""); }
        }

        public string DisplayPath
        {
            get
            {
                switch (Kind)
                {
                    case ItemKind.RegistryValue: return RegUtil.ShortName(Path) + "  →  " + (ValueName.Length == 0 ? "(predefinito)" : ValueName);
                    case ItemKind.RegistryKey: return RegUtil.ShortName(Path);
                    case ItemKind.FirewallRule: return L.F("Regole firewall per {0}", Path);
                    case ItemKind.ScheduledTask: return L.F("Attività pianificata {0}", Path);
                    case ItemKind.Service: return L.F("Servizio «{0}» ({1})", (string.IsNullOrEmpty(Data) ? Path : Data), Path);
                    case ItemKind.PathEntry:
                        return L.F("Variabile PATH {0}  →  {1}", Path.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) ? L.T("dell'utente") : L.T("di sistema"), Data);
                }
                return Path;
            }
        }

        public override string ToString() { return Category + " [" + Confidence + "] " + DisplayPath + " — " + Reason; }
    }

    /// <summary>Impostazioni salvate in %LOCALAPPDATA%\AppManager\settings.json (o nella cartella Dati, in modalità portable).</summary>
    internal sealed class AppSettings
    {
        public bool UseRecycleBin { get; set; }
        public bool BackupBeforeClean { get; set; }
        public string BackupFolder { get; set; }
        public bool UsePrefetch { get; set; }
        public bool ShowSteamTools { get; set; }
        public bool ShowComponents { get; set; }
        public bool DownloadCovers { get; set; }
        public bool SearchSavesOnline { get; set; }            // cerca su PCGamingWiki dove un gioco tiene i salvataggi
        public bool BackgroundMonitor { get; set; }            // sorveglianza in background (vale per un PC: sta nel suo profilo)
        public bool TrayHintShown { get; set; }                // spiegato una volta che chiudendo la finestra AppManager resta nell'area di notifica
        public bool CleanableAlert { get; set; }               // con la sorveglianza: avvisa quando log e cache superano la soglia
        public int CleanableThresholdMb { get; set; }          // soglia dell'avviso di log e cache
        public List<string> CleanableExcluded { get; set; }    // gruppi della pagina Log che l'avviso non conta
        public List<string> CleanableOff { get; set; }         // categorie (windows, browser, launcher…) spente per l'avviso
        public string CleanableSnoozeUntil { get; set; }       // «Non ora»: fino a quando non avvisare
        public string SteamGridDbKey { get; set; }
        public List<string> LibraryFolders { get; set; }       // cartelle dei giochi portable
        public List<string> ProgramFolders { get; set; }       // cartelle dei programmi portable
        public List<SavedEntry> HiddenGames { get; set; }      // Path = Id del gioco
        public List<SavedEntry> ManualGames { get; set; }
        public List<SavedEntry> HiddenPrograms { get; set; }   // Path = Id del programma
        public List<SavedEntry> ManualPrograms { get; set; }
        public string SaveVaultFolder { get; set; }            // deposito dei salvataggi (istantanee e profili)
        public int LogRetentionDays { get; set; }              // righe del registro attività più vecchie di così vengono tolte
        public int BackupsToKeep { get; set; }                 // backup da tenere per ogni applicazione (0 = tutti)
        public List<SaveLocation> SaveLocations { get; set; }  // cartelle dei salvataggi aggiunte o escluse a mano
        public List<string> SavesWithSettings { get; set; }    // giochi (Id) per cui backup e profili includono anche le impostazioni
        public string Theme { get; set; }                      // aspetto: system, light, dark
        public string Language { get; set; }                   // lingua: auto (quella di Windows) o il codice della lingua
        public int CoverBudgetMb { get; set; }                 // spazio massimo per le immagini scaricate (0 = automatico)
        public string LastPcId { get; set; }                   // ultimo PC su cui ha girato: serve a riconoscere un PC nuovo

        public AppSettings()
        {
            UseRecycleBin = true;
            BackupBeforeClean = true;
            UsePrefetch = true;
            DownloadCovers = true;
            SearchSavesOnline = true;
            CleanableAlert = true;
            CleanableThresholdMb = CleanableCheck.DefaultThresholdMb;
            CleanableExcluded = new List<string>();
            CleanableOff = new List<string>();
            BackupFolder = AppPaths.DefaultBackupDir;
            LibraryFolders = new List<string>();
            ProgramFolders = new List<string>();
            HiddenGames = new List<SavedEntry>();
            ManualGames = new List<SavedEntry>();
            HiddenPrograms = new List<SavedEntry>();
            ManualPrograms = new List<SavedEntry>();
            SaveVaultFolder = AppPaths.DefaultSaveVaultDir;
            LogRetentionDays = 30;
            SaveLocations = new List<SaveLocation>();
            SavesWithSettings = new List<string>();
            Theme = "system";
            Language = "auto";
        }

        public List<SavedEntry> Hidden(AppKind kind) { return kind == AppKind.Game ? HiddenGames : HiddenPrograms; }
        public List<SavedEntry> Manual(AppKind kind) { return kind == AppKind.Game ? ManualGames : ManualPrograms; }
        public List<string> PortableFolders(AppKind kind) { return kind == AppKind.Game ? LibraryFolders : ProgramFolders; }

        private static AppSettings current;

        /// <summary>Solo per le prove: la prossima lettura riparte dal file, come a un nuovo avvio.</summary>
        internal static void ResetForTests() { current = null; }

        public static AppSettings Current
        {
            get
            {
                if (current == null)
                {
                    bool existed;
                    try { existed = File.Exists(AppPaths.SettingsFile); } catch { existed = false; }
                    var s = Load();
                    // cartelle, elementi nascosti e giochi noti valgono per un PC solo: la cartella Dati gira su PC diversi
                    ThisPc.Bind(s, existed);
                    current = s;
                    s.Save();
                }
                return current;
            }
        }

        private static AppSettings Load()
        {
            string file = AppPaths.SettingsFile;
            try
            {
                if (File.Exists(file))
                {
                    var s = Json.Deserialize<AppSettings>(File.ReadAllText(file));
                    if (s == null) throw new FormatException(L.T("file vuoto"));
                    s.LibraryFolders = Clean(s.LibraryFolders);
                    s.ProgramFolders = Clean(s.ProgramFolders);
                    s.HiddenGames = Clean(s.HiddenGames);
                    s.ManualGames = Clean(s.ManualGames);
                    s.HiddenPrograms = Clean(s.HiddenPrograms);
                    s.ManualPrograms = Clean(s.ManualPrograms);
                    if (string.IsNullOrEmpty(s.BackupFolder) || PathUtil.Normalize(s.BackupFolder) == null) s.BackupFolder = AppPaths.DefaultBackupDir;
                    if (string.IsNullOrEmpty(s.SaveVaultFolder) || PathUtil.Normalize(s.SaveVaultFolder) == null) s.SaveVaultFolder = AppPaths.DefaultSaveVaultDir;
                    if (s.LogRetentionDays < 0) s.LogRetentionDays = 30;
                    if (s.BackupsToKeep < 0) s.BackupsToKeep = 0;
                    s.SaveLocations = s.SaveLocations == null ? new List<SaveLocation>()
                        : s.SaveLocations.Where(x => x != null && !string.IsNullOrWhiteSpace(x.GameId) && !string.IsNullOrWhiteSpace(x.Path)).ToList();
                    s.SavesWithSettings = Clean(s.SavesWithSettings);
                    if (s.Theme != "light" && s.Theme != "dark") s.Theme = "system";
                    if (string.IsNullOrEmpty(s.Language)) s.Language = "auto";
                    if (s.CoverBudgetMb < 0) s.CoverBudgetMb = 0;
                    if (AppPaths.MigrateBackupFolder(s)) s.Save();
                    return s;
                }
            }
            catch (Exception ex)
            {
                // file illeggibile: si conserva com'è (per recuperarlo a mano) e si riparte dalle impostazioni predefinite,
                // invece di sovrascriverlo al primo salvataggio
                Log.Error("Lettura impostazioni", ex);
                try { File.Copy(file, file + ".danneggiato", true); } catch { }
            }
            return new AppSettings();
        }

        private static List<string> Clean(List<string> list)
        {
            return list == null ? new List<string>() : list.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        }

        private static List<SavedEntry> Clean(List<SavedEntry> list)
        {
            return list == null ? new List<SavedEntry>() : list.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Path)).ToList();
        }

        /// <summary>Modalità di verifica grafica: le impostazioni si possono cambiare in memoria ma non vengono mai salvate.</summary>
        internal static bool SaveDisabled { get; set; }

        public void Save()
        {
            if (SaveDisabled) return;
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                PathUtil.WriteAllTextAtomic(AppPaths.SettingsFile, Json.Serialize(this));
                if (this == current) ThisPc.Save(this);
            }
            catch (Exception ex) { Log.Error("Salvataggio impostazioni", ex); }
        }
    }

    /// <summary>Cartella dei salvataggi di un gioco aggiunta a mano (o, con Excluded, tolta da quelle trovate da sole).</summary>
    internal sealed class SaveLocation
    {
        public string GameId { get; set; }
        public string Path { get; set; }
        public bool Excluded { get; set; }
    }

    /// <summary>Gioco o programma ricordato nelle impostazioni (aggiunto a mano o nascosto).</summary>
    internal sealed class SavedEntry
    {
        public string Name { get; set; }
        public string Path { get; set; }
    }
}
