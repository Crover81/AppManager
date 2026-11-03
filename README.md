# AppManager

**Manage your games and programs on Windows, and remove them completely.**
AppManager uninstalls a game or program and then finds everything it and Windows left behind: saves, settings, caches, screenshots, registry entries, startup entries, scheduled tasks, services, file types, context-menu entries, firewall rules and shortcuts. Nothing is deleted until you have seen the list and confirmed it.

[Italiano](README.it.md) · **[Download](../../releases/latest)** · Windows 10 / 11 · portable, no installation · 7 languages · dark and light theme

![Game library](docs/images/games-dark.png)

---

## Highlights

- **One place for everything installed**: Steam, Epic Games, GOG, Ubisoft Connect, EA app, Battle.net, Rockstar, Amazon Games, Xbox / PC Game Pass, Microsoft Store apps, classic installers, repacks and **portable** copies.
- **Uninstall and clean**: runs the right uninstaller, waits for it, then shows every leftover grouped by category, each with the reason it was found and how sure AppManager is ("Certain", "Likely", "To verify").
- **Leftovers of apps that are already gone**, including games deleted by hand.
- **Background monitoring** (optional): watches installs and uninstalls from the notification area and tells you when an uninstall left something behind.
- **Save manager**: save backups, several save profiles per game, "new game" without losing the old one.
- **Safety first**: Recycle Bin and zip backups before deleting, protected system areas, nothing ever deleted without your confirmation.
- **Always portable**: everything lives in a `Dati` folder next to the program; each PC gets its own profile.

## Screenshots

### Overview
The PC at a glance: how long it has been on, when Windows was installed, what changed since the last start, disk space and what takes the most room. The list of games and programs can be exported to CSV.

![Overview](docs/images/overview.png)

### Games
All installed games with covers, size and launcher. The detail panel shows the folder, the publisher, how the game was found, and gives quick access to saves, uninstall, launch, folder and Internet blocking. Missing covers are looked up online (Steam, PCGamingWiki, optional SteamGridDB key, Wikipedia). Without a cover, the game's own icon is used.

![Games, light theme](docs/images/games-light.png)

Games are found even without a launcher: by the fingerprints of game engines (Unity, Unreal, CryEngine, Source, RPG Maker, Ren'Py…), crack and Steam-emulator signatures (CODEX, RUNE, EMPRESS, Goldberg…), repack names, games added to Steam by hand, and executables Windows remembers launching, wherever they are.

### Programs
Installed programs, Microsoft Store apps, Steam tools and portable programs. Runtimes, drivers and built-in Windows apps are hidden behind "Show Windows components", so they are not removed by mistake. A portable program inside a development project or a folder of documents is proposed as its executable only, never as the whole folder.

![Programs](docs/images/programs.png)

### Uninstall and clean
AppManager starts the right uninstaller (Steam, the program's own uninstaller, Windows for Store apps, direct removal for portable copies). When it finishes, it looks for leftovers.

![Uninstall confirmation](docs/images/uninstall.png)

The leftovers are grouped by category. "Certain" and "Likely" items are pre-selected; "To verify" items are not. The same window, opened with **Data**, shows a game's or program's data without uninstalling anything.

![Leftover review](docs/images/leftovers-review.png)

What is searched, besides the data folders in `AppData`, `ProgramData` and `Documents`:
- startup entries (Run, RunOnce, Task Manager state, Startup folder), scheduled tasks and services of the program;
- file types, "Open with" entries, default apps, context-menu entries, COM components and shell extensions;
- folders added to `PATH`, notification settings, launch history, compatibility settings, GPU preferences, firewall rules;
- for games: Steam data, Unity and Unreal folders, saves in `My Games` and `Saved Games`, Steam-emulator save folders, shader caches.

System entries are proposed only when they really belong to the program: anything that points to Windows is never touched.

### Leftovers of apps already removed
A single analysis finds the remains of games and programs that are no longer installed, or were deleted by hand, and splits them between the Games and Programs sections. Items that are clearly orphaned are selected; recently used data without that proof stays "To verify".

![Leftovers](docs/images/leftovers.png)

### Background monitoring
Off by default. When turned on, AppManager starts with Windows and stays in the notification area. It follows Windows' uninstall entries and the Steam and Epic libraries. When a program or game disappears, it waits for the uninstaller to finish and then looks for leftovers.

![Monitoring history](docs/images/monitoring.png)

If something was left behind, a notice appears above the notification area, without covering the taskbar. **View and remove** opens the list; **Ignore** closes it. The notice disappears after 20 seconds and waits while a full-screen game or a presentation is running. If several uninstalls happen in a row, the same notice collects them. The **Monitoring** page keeps the history: installs, updates and uninstalls, including the ones done with AppManager, with what was left and what has already been removed. Monitoring never deletes anything by itself.

![Notification](docs/images/notification.png)

### Saves and profiles
For each game, **Manage saves** finds its save folders by itself: Steam, `My Games`, `Saved Games`, `AppData`, Unity and Unreal folders, Steam-emulator folders used by unofficial copies, and the game's own folder, where many portable copies save. It also asks PCGamingWiki for saves that cannot be recognised by name; only the game title is sent.
- **Save backups** as zip files, restorable at any time. Before every restore, the current saves are backed up first.
- **Profiles**: several sets of saves for the same game, for example one per player.
- **New game**: puts the current saves aside and starts from scratch; you can switch back whenever you want.

![Saves](docs/images/saves.png)

### Startup and network
**Startup** lists the programs that start with Windows and turns them on or off like Task Manager. Turning one off deletes nothing.

![Startup](docs/images/startup.png)

**Network** shows, in real time, which running programs are connected and where. You can block a single destination, or a program's entire Internet access, with Windows Firewall. Every rule can be removed with one click from the Settings.

![Network](docs/images/network.png)

### Settings
Theme (light, dark or as Windows), language, space limit for downloaded images, Recycle Bin and backup defaults, background monitoring, portable folders, detection methods and network rules.

![Settings](docs/images/settings.png)

## Safety

- **Nothing is deleted without your confirmation.** You always see the full list, with the reason and the confidence of each item.
- **Recycle Bin and backup**: by default deleted files go to the Recycle Bin, in one folder per application, keeping their full paths. System entries are saved there too, as `.reg`, `.xml` and `.txt` files. Saves, settings and system entries are also copied to a zip backup, restorable from the **Backup** section. If the backup fails, the cleanup does not start.
- **Protected areas**: Windows, the main Program Files and ProgramData folders, Windows components, the user folders (Documents, Desktop, Downloads, AppData…), system folders at the root of the drives, launchers, and the folders of every installed game and program. A folder that contains a protected one is never deleted.
- **Links are never followed**: only the junction or symbolic link itself is removed, never what it points to.
- **Strange paths** (short names, variables, `..`, network and `\\?\` paths) are resolved to the real path before any check, or refused. Paths longer than 260 characters are supported.
- **One copy at a time**, settings saved atomically, and every operation recorded in the activity log.

## Privacy

AppManager connects to the Internet only to look up game covers and save folders. For saves it asks PCGamingWiki, sending only the game title. Both lookups can be turned off. An optional SteamGridDB key is sent only to SteamGridDB. There is no telemetry. Each PC is recognised by an anonymous fingerprint of its Windows ID; the ID itself is never stored or sent.

## Languages

English, Italiano, Français, Deutsch, Español, Русский, Українська. The language follows Windows (English if not available) and can be changed in Settings.

## Download and use

1. Download `AppManager.exe` from the [latest release](../../releases/latest).
2. Put it in any folder, even on a USB stick, and start it. Windows asks for administrator rights; they are needed for the Prefetch, system registry keys, services, system scheduled tasks and firewall rules. Without them, AppManager runs in "limited mode" and still finds and cleans your user's data.

Requirements: Windows 10 or 11. It uses .NET Framework 4.8, already included in Windows. To update, replace `AppManager.exe`: the `Dati` folder stays.

**Keyboard**: Ctrl+F search · F5 refresh · Del uninstall the selected item (opens the confirmation only) · Esc close · Enter confirm (never for actions that delete something).

## Build from source

No Visual Studio needed: the C# compiler included in Windows is enough (C# 5, .NET Framework 4.8).

```
cd Source
powershell -ExecutionPolicy Bypass -File build.ps1
```

`build.ps1 -AsInvoker` builds `bin\AppManager.Test.exe` without the administrator request, for testing. `build.ps1 -Harness` also builds `tools\Harness.exe`, the console test tool:

| Command | What it checks |
|---|---|
| `robust <dir>` | edge cases: protected paths, links, long paths, Unicode names, locked files, tampered backups (fake data only) |
| `invariants` | read-only: everything the analyses propose on this PC respects the safety rules |
| `savetest <dir>` | save manager: backups, new game, profile switch, rollback on error, restore |
| `recycletest <dir>` | Recycle Bin organised per application, without using the real Recycle Bin |
| `pctest <dir>` | PC recognition, resources, startup, export, translations, detection of pirated and portable copies, monitoring logic |
| `logtest <dir>` | activity log |
| `watchtest <dir>` | end-to-end background monitoring with a fake application created and removed by the test |

`pctest`, `logtest` and `watchtest` need `APPMANAGER_DATA` set to the same test folder; `savetest` and `recycletest` need it set to a different one.

### Project structure

```
Source/
  src/core   detection, leftover scanning, cleaning, backups, saves, monitoring (no UI)
  src/app    WPF view models, tray icon, notifications
  ui         XAML (loaded at runtime) and ui/lang/*.txt translations
  tools      console test tool (Harness)
  build.ps1  build script
docs/images  screenshots
```

The texts are written in Italian in the code and XAML and act as keys; the translations are in `ui/lang/*.txt`.

## License

[MIT](LICENSE) © Crover81
