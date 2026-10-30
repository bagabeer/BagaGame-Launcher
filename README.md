# 🎮 BagaGame Launcher

[Русский](README.ru.md) · **English**

**BagaGame Launcher** is a lightweight, fast game library organizer built in the Windows 11 Fluent style. It gathers installed games into one clean collection with posters, series and genre grouping, live search and playtime tracking — instead of dozens of desktop shortcuts.

The launcher is written in **C# / WPF** on **.NET Framework 4.7.2** and uses no third-party UI framework: every control is styled by hand. That .NET version ships with Windows 10 and 11, so there is no runtime to install, and the whole app is about **2 MB**.

---

## 📥 Installation

1. Open the **[Releases](https://github.com/bagabeer/BagaGame-Launcher/releases)** page and download the latest archive (`v0.7.0`).
2. Extract it to any writable folder (for example, `D:\BagaGame`).
3. Run **`BagaGame.exe`**.

There is no installer — the app works right after unpacking. All data (library, covers, settings) is stored in the `BagaGameData` folder next to the `.exe`. If that folder is read-only (for example, under `Program Files`), the launcher automatically falls back to `%LOCALAPPDATA%\BagaGame`.

> **Requirements:** Windows 10 version 1803+ or Windows 11. .NET Framework 4.7.2 is already included in the OS — nothing else needs to be installed.
>
> If Windows SmartScreen warns about an unknown publisher, click **"More info" → "Run anyway"**: the executable is not code-signed.

---

## 📸 Screenshots

| Dark theme | Light theme |
|---|---|
| <img src="docs/screenshots/main-dark.png" alt="Library, dark theme" width="100%"> | <img src="docs/screenshots/main-light.png" alt="Library, light theme" width="100%"> |

| Settings (RU) | Settings (EN) |
|---|---|
| <img src="docs/screenshots/settings-ru.png" alt="Settings in Russian" width="100%"> | <img src="docs/screenshots/settings-en.png" alt="Settings in English" width="100%"> |

| Scanning | Series grouping |
|---|---|
| <img src="docs/screenshots/scan.png" alt="Scan page" width="100%"> | <img src="docs/screenshots/series.png" alt="Series grouping" width="100%"> |

| Context menu | Add game dialog |
|---|---|
| <img src="docs/screenshots/context-menu.png" alt="Game card context menu" width="100%"> | <img src="docs/screenshots/add-game.png" alt="Add game dialog" width="100%"> |

---

## ✨ Features

* **Deep scanning.** Finds games in the selected folders, including non-standard layouts and nested directories, while filtering out service executables (`unins`, `vcredist`, `crashhandler` and similar).
* **Store integration.** Imports libraries from **Steam**, **Epic Games** and **GOG Galaxy**.
* **Covers and genres.** Downloads posters and genres from the web, caches them locally and keeps working offline.
* **Grouping.** "All", "A–Z", "Genres" and "Series" modes: franchises (*Resident Evil*, *Dying Light*, *Silent Hill*…) are grouped automatically.
* **Playtime tracking.** Counts hours for games launched through the launcher and shows them right on the card.
* **Tray mode.** When a game starts, the window hides to the system tray and **automatically comes back** once the game exits.
* **Russian and English UI.** Switches in Settings without a restart and remembers the choice.
* **Dark and light themes.** Native Mica on Windows 11; a clean opaque background on Windows 10.
* **Grid and list views.** Two library layouts, live search (`Ctrl+F`) and rescan (`F5`).
* **Manual additions.** Add a whole game folder or a specific `.exe`, then rename, hide or delete games from the context menu.
* **Single instance.** Launching the `.exe` again brings the running window to the front instead of opening a second copy.
* **Reliable saving.** The library is written atomically with a backup, and if a drive disappears the card stays with a "Path unavailable" badge and a "Choose folder again" action.

---

## 🗂 Application data

```text
<folder with BagaGame.exe>\BagaGameData\library.json   — library, settings, playtime, hidden games
<folder with BagaGame.exe>\BagaGameData\covers\        — cover cache (<AppID>.jpg) and storeids.json
```

If the folder next to the `.exe` is not writable, `%LOCALAPPDATA%\BagaGame` is used instead.

| `library.json` key | Purpose |
|---|---|
| `Hidden` | Hidden games |
| `CustomTitles` | User-defined titles |
| `Folders` | Folders to scan |
| `ManualGames` | Manually added games |
| `Genres` | Genre cache (canonical English names are stored) |
| `Playtime` | Played time in seconds |
| `Theme`, `Language`, `GridView`, `Grouping`, `DownloadCovers`, `CacheDirectory` | Interface preferences |

---

## 🛠 Building from source

All you need is the **.NET SDK 8+**: it builds the project for .NET Framework 4.7.2 through reference assemblies, so Visual Studio is optional.

```powershell
dotnet build BagaGame.csproj -c Release
dotnet run --project BagaGame.csproj
```

The ready `.exe` appears in `bin\Release\net472\`. No installer or portable publish step is required — copying the build folder is enough.

### Project structure

```text
BagaGame.csproj                     — net472, WPF + WinForms (tray only), no third-party UI packages
App.xaml / App.xaml.cs              — startup, single instance, language, theme
MainWindow.xaml / .xaml.cs          — window, cards, search, hotkeys, tray
ViewModels/MainViewModel.cs         — filtering, groups, statuses, covers
Models/Game.cs                      — game model and localized labels
Services/GameLibraryService.cs      — Steam / Epic / GOG / folder scanner, covers and genres
Services/PlaytimeTracker.cs         — playtime accounting and game-exit event
Services/LibraryStore.cs            — atomic library.json persistence
Services/LibraryGrouping.cs         — "A–Z", "Genres" and "Series" groups
Services/Localization.cs            — RU/EN and string dictionaries
Services/TrayIcon.cs                — tray icon and menu
Services/AppPaths.cs                — portable data folder resolution
Themes/                             — palettes, control styles, Strings.ru/en.xaml
Controls/                           — custom Fluent controls (window, buttons, toggles)
Compat/                             — polyfills for modern C# on net472
```

---

## 📝 Changelog

### Version 0.7.0 (current)

**Size and platform**
* Retargeted from .NET 8 to **.NET Framework 4.7.2**, which ships with Windows 10 1803+ and Windows 11, so the launcher starts without installing a runtime.
* **WPF-UI removed**: buttons, switches, the title bar, scroll bars, menus and tooltips are now custom. No third-party UI packages are referenced.
* Build size dropped from **13.5 MB to ~2 MB**.
* PowerShell scripts and everything that was needed to build the project besides the .NET SDK were removed from the repository.

**Fixes (issue #1 and Pikabu feedback)**
* Atomic `library.json` saves with a `.bak` backup — a failed write no longer wipes the library.
* Safe path handling: no escaping the selected folder, drive availability checks.
* Cover limits: file size, resolution, pixel count and an allowlist of image hosts.
* Exact game-process matching — the tracker no longer counts time for a similarly named process.
* Proper scan cancellation and protection against overlapping scans.
* Stable source ids (`Folder` / `Manual`) and migration of older genre values.
* Manual games are kept when their folder is temporarily unavailable.
* Deleted games no longer return after a rescan: they are automatically hidden.
* Duplicates found through different `.exe` files in the same folder collapse into one card.
* TLS 1.2 is enforced for cover and genre downloads.

**New**
* Automatic return from the tray after the launched game exits.
* **RU / EN** switching without a restart, saved between sessions.
* Portable data in `BagaGameData` next to the `.exe`, with a `%LOCALAPPDATA%` fallback.
* Second-instance protection: launching the app again brings up the existing window.
* "Path unavailable" badge on the card and a "Choose folder again" menu action.
* Asynchronous manual game addition — the UI no longer freezes on large folders.
* Updated interface strings, found/shown game counters.

### Version 0.6.0
* Built-in playtime counter.
* Improved deep `.exe` search in hidden and non-standard folders.
* Minimizing to the system tray when a game starts.
* "Delete from library" item in the context menu.
* Automatic scan on startup.

### Version 0.5.3
* First public release.
* Steam and Epic Games import.
* Series grouping.
* Manual game and folder additions.

---

## ⚠️ Known limitations

* **Genres.** Resolved through the Steam Store API; unknown games fall into the "Other" group.
* **Custom folders.** Detection is based on `.exe` files, so utility apps may occasionally appear in the library — hide or delete them from the context menu.
* **Mica.** The effect is available on Windows 11 (build 22000+) only. Windows 10 uses a regular theme background.
* **Anti-cheat and third-party launchers.** If a game is started through an external launcher (Ubisoft Connect, Rockstar Launcher, etc.), the tracker may not see the process and may not count playtime.

---

## 🐛 Feedback

Found a bug or have an idea? Open an **[Issue](https://github.com/bagabeer/BagaGame-Launcher/issues)** with steps to reproduce — it really helps.

---

## 📄 License

This project is distributed under the **MIT License**. See [LICENSE](LICENSE) for details.

Made with ❤️ for gamers.
