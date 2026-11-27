# Changelog

Every release gets a section here before its tag is pushed: the release workflow takes the release notes from it
and stops if the section is missing. Newest first; the heading is `## [version] - date`, with the version exactly as
in `<Version>` of `ForceBorderlessWindowed.csproj`.

## [Unreleased]

### Changed

- More useful logs: when a window can't be changed or restored, the log names the Windows function that refused and its error code. Each start records the Windows version, whether the app runs as administrator and whether it started at sign-in. Lines show the time zone and a shorter source name, and "made borderless" lines give the exact position and display.
- If the app fails while starting, before its log is set up, it now shows a message and writes a `crash-....txt` file to the log folder instead of closing silently.
- The error message no longer says details are in the log file when no log file could be written.

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
