# 🎮 BagaGame Launcher [v0.6.0]

**BagaGame Launcher** is a lightweight, fast, and smart game library organizer designed in the Windows 11 Fluent style. It is built for those who are tired of cluttering their desktops with hundreds of game shortcuts and want a beautiful, clean, and well-structured collection reminiscent of console interfaces.

Unlike heavy alternatives, BagaGame is written in **C# / WPF** using **WPF-UI 4.3.0**. The installed application takes up only **13.5 MB**, consumes minimal system resources, and consolidates all your games (Steam, Epic Games, GOG, and DRM-free/pirated titles) into one cozy place.
<p align="center">
  <img src="https://github.com" width="750" style="border-radius: 12px;" alt="BagaGame Launcher Screenshot">
</p>
---

## 🛠 Key Features

* **🔍 All-Devouring Scanner (Deep Scan):** Automatically scans designated directories upon launch. It accurately detects executable files even within hidden or non-standard structures (up to 8 folders deep) and pulls high-quality cover art and genres from the web.
* **🔌 Store Integration:** Seamlessly imports licensed titles directly from **Steam**, **Epic Games Launcher**, and **GOG Galaxy**.
* **🗂 Automatic Series Grouping:** Keeps your library tidy by automatically bundling franchises (*The Witcher*, *Assassin's Creed*, *Resident Evil*, etc.) using a curated database and smart first-word title heuristics.
* **⏱ Built-in Playtime Tracker:** Tracks every minute you spend playing. The tracker pings active game processes every 20 seconds and counts time only when the game is launched through BagaGame.
* **🎨 UI & Customization:** Instantly switch between light and dark themes with full native Mica effect support on Windows 11, rounded poster cards, and a convenient context menu (RMB) for quick library management.

---

## 🚀 Changelog

### Version 0.6.0 (Current)
* **Time Tracking:** Added a built-in game playtime counter.
* **Deep Scan:** Enhanced search algorithms to locate hidden `.exe` files and obscure file structures.
* **System Tray Integration:** BagaGame now automatically minimizes to the Windows system tray when a game starts.
* **Quick Library Management:** Added a "Delete from library" option to the card context menu (RMB).
* **Startup Auto-Scan:** The launcher now automatically scans game directories on boot to immediately pull new games and artworks.

### Version 0.5.3
* First public release and setup installer compilation.
* Basic Steam and Epic Games Launcher import integration.
* Implemented the franchise/series grouping mechanic.
* Added manual shortcut and folder addition buttons on the main screen.

---

## 📥 Requirements & Setup

### For Users:
1. Navigate to the **[Releases](https://github.com)** section of this repository.
2. Download the latest `.exe` installer for version `v0.6.0`.
3. Run the installer and follow the prompt instructions.
* *Supported OS:* Windows 10 (1809+) or Windows 11.

### For Developers (Building from Source):
* Requires **.NET 8 SDK** and **Visual Studio 2022** (optional).
```powershell
dotnet restore
dotnet run
```
Alternatively, open `BagaGame.csproj` in Visual Studio and press **F5**.

---

## ⚙️ Technical Documentation (For Contributors)

### Component Behavior & Specifications

| Feature | Behavior |
|---|---|
| **Scanning** | Targets Steam, Epic Games, GOG Galaxy, and user-defined directories. |
| **Manual Add** | Individual `.exe` files or whole game folders can be added instantly without running a full library re-scan. |
| **Library View** | Displays 180×260 rounded posters; switches between grid or list view (saved in json). |
| **Grouping Modes** | Categories: All, A–Z, Genres, and Series (includes custom headers and game counters). |
| **Search & Shortcuts** | Real-time live filtering. **Ctrl+F** to focus search, **Esc** to clear, **F5** to force re-scan. |
| **Context Menu** | Right-click options: Play, Open Folder, Rename, Hide from Library, Delete. |
| **Tray Behavior** | Minimizes to system tray on game launch to keep the playtime tracker thread alive. |
| **Covers & Genres** | Looks for local assets, then falls back to Steam Store scraping. Fully cached. |

### Manual Add Mechanics
The **"Add Game"** tool processes directories in two ways:
1. **Game Folder:** Automatically identifies the core executable while stripping away utility files like `unins`, `vcredist`, or `crashhandler`. Internal build directories (`bin`, `x64`, `ph`, `work`) are skipped when generating game names. Unreal Engine structures (e.g., *Days Gone*) are parsed correctly to map the true root directory.
2. **Direct .exe:** Hard-links the selected executable. 

### Architecture & Project Structure
```text
BagaGame.csproj
App.xaml / App.xaml.cs              — Application startup, WPF-UI theme handling, and palettes
Themes/Dark.xaml / Light.xaml       — Theme resource dictionaries (AccentBrush, CardFillBrush, etc.)
MainWindow.xaml / MainWindow.xaml.cs— Main window design, card grids, search bar, and hotkeys
Models/Game.cs                      — Game data model (+ header group parameters)
Models/ManualGameEntry.cs           — Data model for manually indexed games
ViewModels/MainViewModel.cs         — Manages library filtering, groups, status bar, and cover arts
Services/GameLibraryService.cs      — Main scanner for Steam/Epic/GOG/custom paths and CDN scraping
Services/PlaytimeTracker.cs         — Manages playtime logic (process polling every 20 seconds)
Services/TrayIcon.cs                — Handles system tray behavior and notifications
Services/LibraryGrouping.cs         — Dictionary clustering for alphabet, genres, and KnownSeries
Services/LibraryStore.cs            — Handles JSON state serialization
Services/ThemeService.cs            — Intermediary for WPF-UI accent switches
Assets/bagagame.ico                 — Native application icon
```

### Application Data & Configuration (`library.json`)
Local configuration and cache files are stored at:
```text
%LocalAppData%\BagaGame\library.json   — Themes, overrides, manual titles, tracked playtime, layouts
%LocalAppData%\BagaGame\covers\        — Local cover art cache (<AppID>.jpg) and storeids.json
```

| JSON Key | Purpose |
|---|---|
| `Hidden` | Array of hidden game IDs |
| `CustomTitles` | User-defined custom game names |
| `Folders` | User-defined root directories for manual scanning |
| `ManualGames` | Array of individually added games |
| `Genres` | Cached genres mapped with localized descriptors |
| `Playtime` | Total accumulated playtime in seconds mapped by game ID |
| `Theme`, `DownloadCovers`, `CacheDirectory` | Preferences for UI styling and local cache positioning |

---

## ⚠️ Known Limitations

* **Genres:** Genre categories are exclusively parsed via the Steam Store API; unsupported or unidentified titles default to the "Other" group.
* **Custom Folders:** Custom path scanning relies entirely on detecting `.exe` files; structural utility tools may occasionally appear in your library (can be hidden or removed via RMB).
* **Mica Effect:** The Fluent Mica background requires Windows 11 (build 22000+). On Windows 10, the UI renders as a solid, opaque theme color.
* **Tray Behavior:** The window will minimize to the notification tray on launch (and may hide inside the Windows 11 taskbar overflow arrow). It does not automatically restore to the foreground when a game exits—you must double-click the tray icon to restore the window.

---

## 🐛 Bug Reporting & Contributions

This launcher is actively maintained, and your feedback is highly appreciated! If you encounter layout issues, visual bugs, or have feature requests:
1. Navigate to the **[Issues](https://github.com)** tab.
2. Open a **New Issue** with a clear description and steps to reproduce.

---
Built with ❤️ for gamers. Enjoy!
