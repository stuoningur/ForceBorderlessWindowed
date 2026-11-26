# Force Borderless Windowed

A small Windows tray app that makes games run in a borderless window, for games that only offer fullscreen or a normal window with a frame.

You add a game once. From then on, whenever it runs, the app removes the window frame and puts the window where you want it: centered at full height, at a size and position you choose, or at the game's own size. When you close the app or pause it, the windows get their frame back.

It works with any program that has a normal window. It can't do anything for games that only run in exclusive fullscreen; start those in windowed mode first.

## Download

Get `ForceBorderlessWindowed.exe` from the [releases page](https://github.com/stuoningur/ForceBorderlessWindowed/releases). There is no installer: put the exe in a folder you keep it in and start it.

Requirements:

- Windows 10 or 11, 64-bit
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64). If it's missing, Windows tells you when you start the app and links to the download.

The exe isn't code-signed yet, so Windows SmartScreen will probably warn you the first time ("Windows protected your PC"). Click "More info", then "Run anyway". Each release lists the SHA-256 checksum of the exe if you want to check your download.

## Getting started

1. Start the game in windowed mode.
2. Open Force Borderless Windowed (it lives in the notification area) and go to the Games tab.
3. Add the game from the list of running programs, or pick its exe with Browse.
4. Choose where the window should go and select Save & apply.

There is also a global hotkey that adds whatever program you're using to the list right away. It's off until you set it on the Hotkeys tab.

## What it can do

- Placement per game: centered at full height (optionally keeping the aspect ratio), a custom size and position, or the game's own size, centered. Works on any display, including "the display the game is on".
- Optional, while the game is in the foreground: keep the mouse inside the game window, hide the taskbar, cover the rest of the display in black.
- Several games sharing one exe (`javaw.exe`, emulators): match by window title or full path.
- Pause from the tray menu or a hotkey; windows get their frame back until you resume.
- A one-off hotkey that makes the current window borderless without adding it to the list.
- Light and dark theme, English and German.

## Games that run as administrator

Windows doesn't let a normal program change the window of a program that runs as administrator. For those games, Force Borderless Windowed has to run as administrator too. You can restart it that way from the Games tab, or turn on "With administrator rights" under Start with Windows.

That option copies the app to `C:\Program Files\ForceBorderlessWindowed` (only administrators can change files there) and creates a scheduled task that starts the copy when you sign in, without a UAC prompt. Turning the option off removes the task and the copy.

## Anti-cheat

The app only changes the window's style, size and position through normal Windows functions. It doesn't read or write game memory or inject anything. Some anti-cheat software still blocks changes to the game's window; in that case the Games tab tells you it couldn't be changed. Whether a game's anti-cheat is fine with tools like this is up to the game, so check before you use it in online games.

## Where it keeps its files

- Settings: `%APPDATA%\ForceBorderlessWindowed\settings.json`
- Logs (14 days) and the list of windows it changed: `%LOCALAPPDATA%\ForceBorderlessWindowed`
- The tray menu has Troubleshooting → Open log folder.

If the app is closed unexpectedly while a game is borderless, it puts that window back the next time it starts.

## Uninstalling

1. On the General tab, turn off "Start with Windows". This also removes the administrator copy and its scheduled task if you used that.
2. Exit the app from the tray menu and delete the exe.
3. Delete `%APPDATA%\ForceBorderlessWindowed` and `%LOCALAPPDATA%\ForceBorderlessWindowed` if you don't want to keep your settings.

## Building

You need the .NET SDK version from `global.json` (currently 11.0.100) and Windows.

```
dotnet build
dotnet test
dotnet publish ForceBorderlessWindowed -p:PublishProfile=Portable
```

The last command builds the release exe into `ForceBorderlessWindowed\bin\Publish\`.

Releases are built by GitHub Actions: pushing a tag like `v0.9.0-beta` that matches `<Version>` in `ForceBorderlessWindowed.csproj` creates a draft release with the exe and its checksum.

## License

MIT, see [LICENSE](LICENSE). The licenses of the libraries included in the exe are in `ForceBorderlessWindowed/Resources/ThirdPartyNotices.txt` and in the app under General → About.
