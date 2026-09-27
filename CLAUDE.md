# Mouse Without Borders Tweaks

A patch set for PowerToys Mouse Without Borders (MWB), plus Windows tools around it. See README.md for features. `/mwb-setup` walks a user through setup.

## Layout
- `mwb.patch`: every MWB change, as a `git diff` against the matching PowerToys tag. It is the source of truth.
  - Edited files: `Class/InputHook.cs`, `Class/NativeMethods.cs`, `Core/Event.cs`, `Core/MachineStuff.cs`.
  - New files in `Core/`: `EdgePush`, `RawPush`, `EdgeStats`, `MouseMoveCoalescer`, `Tweaks`.
- `apply.ps1`: builds the DLL for the installed PowerToys version and swaps it in (see below). `-Revert` restores the stock DLL.
- `MouseWithoutBorders.csproj`: a standalone build of the MWB DLL against the installed PowerToys DLLs.
- `autopatch/` (MwbAutoPatch.exe), `settingsgui/` (MwbSettings.exe), `streamdeck/` (Stream Deck plugin): .NET Framework 4.8 tools, built with the SDK in `dotnet\`.

## What apply.ps1 does
1. Installs the .NET SDK into `dotnet\` if it's missing.
2. Sparse-clones the matching PowerToys tag into `build\<ver>\src`.
3. Runs `git apply -3 mwb.patch`.
4. Builds the DLL and swaps it into PowerToys.

## Rules
- The repo must live at `%LOCALAPPDATA%\Programs\mwb8k`, because `Tweaks.cs` reads `settings.ini` from there.
- To change MWB:
  1. Edit in `build\<ver>\src`, and run `git add -N` on new files.
  2. Regenerate the patch from bash with `git diff HEAD > mwb.patch`. PowerShell `Set-Content` mangles patches.
  3. Rebuild with `apply.ps1`.
- Build a tool with `dotnet\dotnet.exe build <proj> -c Release -o <dir>`. The exes run from the repo root, next to `apply.ps1`.
- The MWB DLL is locked while PowerToys runs, so `apply.ps1` stops PowerToys and starts it again.
- Settings:
  - `settings.ini` is re-read at most once a second.
  - MWB also live-reloads PowerToys' `%LOCALAPPDATA%\Microsoft\PowerToys\MouseWithoutBorders\settings.json` (EasyMouse, WrapMouse, DisableEasyMouseWhenForegroundWindowIsFullscreen). Edit it in place, keep its encoding, and never print or commit it: it holds the MWB security key.
- Stream Deck:
  - It loads plugins and profile changes only when it starts.
  - Edit profiles only while it is closed, or it overwrites them.
  - It may run elevated.
- Debugging: the MWB log is at `%LOCALAPPDATA%\Microsoft\PowerToys\MouseWithoutBorders\Logs\<ver>\Log_<date>.log`.
  - `Tweaks:` lines show the settings it loaded.
  - `EdgePush ... passes` lines explain each switch.
- Lessons learned:
  - While the cursor is pushed against an edge, the low-level hook reports positions past it. Treat "on the border" as a range (`edgeAt`).
  - Raw input counts are scaled by the Windows pointer speed.
  - Don't inject test input while the user is moving the mouse.
- Ask before side effects: restarting PowerToys or Stream Deck, writing registry Run keys, or editing PowerToys/Stream Deck settings.
