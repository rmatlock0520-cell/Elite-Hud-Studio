# Changelog

## 1.6

- The app version no longer starts PowerShell: the helper that reads and writes the color files is now built into `Elite HUD Studio.exe`.
- The app version no longer asks for administrator rights.
- Two downloads: a browser version with no `.exe` (recommended) and the app version.

## 1.5

- New cockpit view made from a real cockpit screenshot, with the game's own HUD recolored live.
- Color matrix sliders now adjust the colors you have and can be put back exactly; each slider has a number box.
- Nav panel and systems panel previews corrected: rows and tabs follow the color matrix, headings follow the main text color.

## 1.4

- Logs are also kept in `%LOCALAPPDATA%\Elite HUD Studio\Logs`, so they survive antivirus folder protection.
- The launcher checks that Studio stays open before reporting PASS.
- Antivirus section added to the README.

## 1.3

- Pass/fail launcher (`Start Elite HUD Studio.bat`) with a launcher log.
- Studio remembers the confirmed game folder.

## 1.1 - 1.2

- Studio runs in its own window (WebView2).
- Crash reports and a diagnostic report on the Setup tab.

## 1.0

- First version: browser-based editor with click-to-edit previews, Quick Theme, themes, backups and undo.
