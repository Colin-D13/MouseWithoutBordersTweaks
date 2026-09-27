---
name: mwb-setup
description: Set up, build and install Mouse Without Borders Tweaks on this PC, step by step. Use when someone wants to install the project, rebuild it after a PowerToys update, or set up the settings window, auto-patcher or Stream Deck plugin.
---

Walk the user through setup one step at a time. Before each step, say what it will change. Confirm before steps marked (ask). After each step, check it worked before moving on. If Claude's shell is sandboxed and a process it starts would stay inside that sandbox, give the user the command to run in their own terminal instead.

1. **Check prerequisites**:
   - Windows 10/11.
   - `git --version` works.
   - PowerToys is installed per user: `%LOCALAPPDATA%\PowerToys\PowerToys.MouseWithoutBorders.dll` exists. Read its version from the file's version info.
   - Mouse Without Borders is enabled and connected to the other PC in PowerToys.
2. **Location**: the repo must be at `%LOCALAPPDATA%\Programs\mwb8k`, because MWB reads `settings.ini` from there. If it's elsewhere, (ask) clone it there: `git clone https://github.com/Colin-D13/MouseWithoutBordersTweaks.git "%LOCALAPPDATA%\Programs\mwb8k"`. Then work from that folder.
3. **Patch MWB** (ask; PowerToys will restart): run `powershell -ExecutionPolicy Bypass -File apply.ps1`. It:
   - downloads the .NET SDK into `dotnet\` (first run only),
   - clones the matching PowerToys tag and applies `mwb.patch`,
   - builds the DLL, backs up the stock DLL as `.orig-<version>`, swaps in the new one and restarts PowerToys.
   - If the patch no longer applies to the installed version, stop and see step 9.
4. **Verify**: the installed DLL contains the text `MouseMoveCoalescer`. After an edge hit, the MWB log (`%LOCALAPPDATA%\Microsoft\PowerToys\MouseWithoutBorders\Logs\<ver>\`) has a `Tweaks:` line.
5. **Settings window**: build it with `dotnet\dotnet.exe build settingsgui\MwbSettings.csproj -c Release -o .`, then have the user open `MwbSettings.exe`. Explain the settings:
   - drag speed (dragging into an edge),
   - push speed and rest time (pushing after resting on the edge),
   - updates per second sent to the other PC.
   Test mode measures edge hits without switching, and holds only while that window is focused. Nothing applies until Save. (ask) Offer a Start Menu shortcut.
6. **Auto re-patch** (ask; adds an HKCU Run key): build it with `dotnet\dotnet.exe build autopatch\MwbAutoPatch.csproj -c Release -o .`, then run `MwbAutoPatch.exe --install`. It re-applies the patch after PowerToys updates and logs to `autopatch.log`. `--uninstall` removes it.
7. **Easy Mouse** (in PowerToys, not this repo): Enabled = a flick or push switches PCs; Ctrl/Shift = hold that key too; Disabled = never. Optional:
   - "Disable Easy Mouse when the foreground window is fullscreen" blocks switching in games.
   - Turning off "Wrap mouse" stops the outer edges wrapping around.
8. **Stream Deck plugin** (optional; ask before restarting Stream Deck):
   1. Build it with `dotnet\dotnet.exe build streamdeck\MwbToggle.csproj -c Release -o streamdeck\bin`.
   2. Run `powershell -File streamdeck\make-icons.ps1 -OutDir <plugin>\imgs`.
   3. Copy `MwbToggle.exe`, `MwbToggle.exe.config` and `streamdeck\manifest.json` into `%APPDATA%\Elgato\StreamDeck\Plugins\com.colin.mwbtoggle.sdPlugin`.
   4. Have the user quit and reopen Stream Deck.
   5. The user drags *Game Lock* and *PC Switch* from the *Mouse Without Borders* category onto keys.
9. **New PowerToys version breaks the patch**:
   1. Rebase the changes in `build\<ver>\src` (see CLAUDE.md).
   2. Regenerate the patch from bash with `git diff HEAD > mwb.patch`.
   3. Rerun step 3.
   `apply.ps1 -Revert` restores the stock DLL and stops auto re-patching.

Finish with a short summary of what's installed and where the settings live.
