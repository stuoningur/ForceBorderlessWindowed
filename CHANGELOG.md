# Changelog

Every release gets a section here before its tag is pushed: the release workflow takes the release notes from it
and stops if the section is missing. Newest first; the heading is `## [version] - date`, with the version exactly as
in `<Version>` of `ForceBorderlessWindowed.csproj`.

## [Unreleased]

### Changed

- Games tab: the game's name, its status and the checkbox that turns it on, now called "Make borderless", stay at the top of the details while you scroll. A game that is turned off says "Off" first, before why it isn't running or matched.
- General tab: "Default window placement" now comes right after Detection.
- The "Add a game" dialog no longer selects the first running program for you: Add stays disabled until you pick one. After typing a search, the best match is selected, so Enter still adds it.
- "Remove all" moved into a "⋯" menu next to Remove, as "Remove all games…".
- Shorter descriptions on the Hotkeys tab. The details of the temporary borderless hotkey are in its tooltip.
- Clearer wording: the message when another game takes a running window no longer claims its filters are always more specific, the duplicate-game error says which filter to change, and "Keep the shape" is now "Keep the aspect ratio". German texts say "Tastenkürzel" throughout.

### Fixed

- In a narrow window, the text on the Hotkeys tab sat at the top of its row instead of being centered with the icon and the field.

## [0.10.1-beta] - 2026-11-27

### Changed

- More useful logs: when a window can't be changed or restored, the log names the Windows function that refused and its error code. Each start records the Windows version, whether the app runs as administrator and whether it started at sign-in. Lines show the time zone and a shorter source name, and "made borderless" lines give the exact position and display.
- If the app fails while starting, before its log is set up, it now shows a message and writes a `crash-....txt` file to the log folder instead of closing silently.
- The error message no longer says details are in the log file when no log file could be written.
- If the Pause or "add the game in the foreground" hotkey can't save the settings, you now hear the failure sound and see a message over the game instead of an error dialog that takes the focus from the game.
- Switching from a game to the settings window releases the mouse lock, the hidden taskbar and the black background right away.
- Windows made borderless with the one-off hotkey are noticed right away when they put their frame back, like game windows.

### Fixed

- Games and programs whose main window belongs to a hidden helper window (some older engines and Delphi programs) can now be found as games and used with the hotkeys.
- Updating while the old version asks "Save your changes?": if the new version stopped waiting before you answered, the old one now stays open instead of exiting and leaving neither running. This takes effect for updates from this version on.
- A power cut right after saving could leave an empty settings file, and the next start used the defaults.
- A failure while the app starts watching for game windows now shows the error message instead of closing the app silently.
- Unplugging a monitor while the settings window is open no longer shows an error message.
- With a renamed exe, starting it again now brings up the window of the "Start with Windows" (as administrator) copy.
- Games installed in folders with very long paths (over 1024 characters) are now recognized.

## [0.10.0-beta] - 2026-11-27

### Added

- Window position as a setting: left, center or right, and top, center or bottom of the display, for every placement mode. It follows the display, so "16:9 on the left" works on any monitor. Custom sizes can still use an exact offset; settings from 0.9.0 keep working as before.
- Presets in the placement editor: the whole display, 16:9 centered, on the left or on the right, and the left or right half.
- "Add" opens a dialog with the running windows and a search box. It shows the display each window is on and which game already takes it.
- "Reset to defaults" on the General tab (language, theme, default placement and hotkeys; your games and "Start with Windows" stay) and "Remove all" on the Games tab. Both take effect when you save.
- "Fit to display" shrinks custom values that are too large for the selected display.

### Fixed

- Picking another display no longer overwrites your custom width, height and offset.
- With "Use default" and "the display the game is on", the summary showed a different size than the preview.
- When two games match the same window, the winner no longer depends on the games' names. The Games tab now says when another game takes a game's window.

## [0.9.0-beta] - 2026-11-26

First public beta.

- Removes the frame of the games you add and places their window: at full height in an aspect ratio, at a custom size, or at the game's own size, on any display.
- Optional while a game is in the foreground: keep the mouse in the window, hide the taskbar, black background.
- Pause from the tray or a hotkey; windows get their frame back on pause, exit and after a crash.
- Hotkeys to add the game in the foreground, or to make any window borderless once.
- English and German, light and dark theme.
