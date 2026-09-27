# Mouse Without Borders Tweaks

Patches for PowerToys **Mouse Without Borders** (MWB). The DLL is rebuilt from the matching PowerToys source, and the patch is re-applied automatically after updates. Built on [PowerToys](https://github.com/microsoft/PowerToys) (MIT); not affiliated with Microsoft.

## Features
- **4k/8k polling fix**: mouse moves sent to the other PC are merged, so high-polling mice don't lag it.
- **Edge resistance**: you have to flick or push the cursor into a screen edge to switch PCs.
  - *Drag into the edge*: the cursor arrives fast, or carries on into the edge without stopping.
  - *Push from the border*: you push after the cursor has rested on the edge. Pushes are read from raw input while the cursor is pinned there.
  - It follows the Easy Mouse setting: Enabled = flick or push alone, Ctrl/Shift = hold that key too, Disabled = never.
- **Primary monitor edges**: only the primary monitor's free edges switch PCs, and the cursor lands back on it.
- **Remote input passthrough**: input from Parsec and other remote-desktop apps stays on the PC. It isn't forwarded and doesn't switch machines.
- **Auto re-patch**: `MwbAutoPatch.exe` runs at login, notices PowerToys updates (including mid-session) and re-applies the patch, with a notification.
- **Settings window**: `MwbSettings.exe` has sliders for every setting. Its test mode shows what MWB measured on each edge hit (drag vs push, peak vs needed) without switching.
- **Stream Deck plugin**:
  - *Game Lock* toggles PowerToys' "Disable Easy Mouse when the foreground window is fullscreen".
  - *PC Switch* turns edge switching off, and back on to the previous mode.
  - Both keys show the current state.

## Settings
Settings live in `settings.ini` next to `apply.ps1` and are re-read within a second, with no restart needed.

| Key | Default | Meaning |
|---|---|---|
| `EdgeFlickSpeed` | 2500 | px/s needed when dragging into an edge (0 = switch on touch) |
| `EdgePushSpeed` | 1200 | px/s needed when pushing from the border |
| `EdgePushRestMs` | 100 | ms the cursor must rest on the border before a push counts |
| `MaxMouseMoveHz` | 500 | mouse updates per second sent to the other PC (0 = unlimited) |
| `IgnoreRemoteInput` | 1 | 0 = MWB handles remote-desktop input like stock MWB does |

## Install
You need Windows 10/11, PowerToys (per-user install), Git and PowerShell 5.1.
1. Clone the repo to `%LOCALAPPDATA%\Programs\mwb8k`. MWB looks for `settings.ini` there.
2. Run `powershell -ExecutionPolicy Bypass -File apply.ps1`. It:
   - downloads the .NET SDK to `dotnet\`,
   - clones the matching PowerToys tag and applies `mwb.patch`,
   - builds the DLL, backs up the stock DLL (`.orig-<version>`) and restarts PowerToys.
3. Build the tools next to it:
   - `dotnet\dotnet.exe build autopatch\MwbAutoPatch.csproj -c Release -o .`
   - `dotnet\dotnet.exe build settingsgui\MwbSettings.csproj -c Release -o .`
4. Run `MwbAutoPatch.exe --install` to start it at login (`--uninstall` removes it).

`apply.ps1 -Revert` puts the stock DLL back and stops the automatic re-patching.

## Stream Deck
1. Build `streamdeck\MwbToggle.csproj`.
2. Run `streamdeck\make-icons.ps1 -OutDir <plugin>\imgs`.
3. Copy `MwbToggle.exe`, `manifest.json` and `imgs\` to `%APPDATA%\Elgato\StreamDeck\Plugins\com.colin.mwbtoggle.sdPlugin`.
4. Restart Stream Deck. The keys are listed under *Mouse Without Borders*.

## New PowerToys versions
If `mwb.patch` stops applying, rebase it in `build\<version>\src` and regenerate it with `git diff HEAD > mwb.patch`.
