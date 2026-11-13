# Recover from a game or launcher failure

## Restore a stuck display resolution

1. Close the game, including its child processes, in Windows Task Manager.
2. Restart KidLauncher. It attempts to restore the saved display mode before enabling games.
3. If automatic restoration fails, open Windows **Screen Resolution** and select the monitor's normal resolution, refresh rate, and color depth.
4. Preserve `Data\display-session.json` and the logs while investigating the failure.

Do not delete recovery state merely to bypass an error. KidLauncher retains it if display or disc restoration fails. Recovery refuses to change resources while an identified game process is still running or its liveness cannot be checked.

If the launcher stopped during process creation, automatic recovery preserves the display and disc because the game identity may be missing. Close every game in Task Manager first. Then open the configurator's **Diagnostics** tab and select **Confirm games closed and recover…**. The confirmation does not override checks for known live game processes.

A power failure, driver failure, monitor replacement, PID reuse, or interruption between process creation and recording its identity can prevent complete automatic recovery. Verify the display and disc manually after such a failure. A saved mode that the current display driver rejects cannot be forced safely.

## F6 does not close the game

1. Check that the configured exit key is F6.
2. Check the log for hotkey-registration errors and process-access errors.
3. Check whether the game is elevated and the launcher is not.
4. Check whether automatic forced exit is enabled for this game.
5. Test a different unreserved key, such as Scroll Lock, through the configurator.

By default, one press force-closes the tracked processes at once (**Controls → Close games instantly with the exit key**). Games with force-exit disabled, and all games when instant exit is off, get a graceful close request first. With automatic force-exit enabled, tracked processes that remain after the configured timeout are terminated automatically. With force-exit disabled, an unresponsive game must be closed manually. Unsaved game progress can be lost after forced termination.

The low-level keyboard hook stays active even when RegisterHotKey succeeds. Key-state polling provides another detection path. The log includes `source=WH_KEYBOARD_LL` or `source=GetAsyncKeyState polling` when an exit key reaches the session. These mechanisms cannot guarantee input from a secure desktop or a higher-integrity game. If no input source receives the key, close the game through Windows and retain the log. F12 is reserved for debugging and is not a recommended exit key.

KidLauncher tracks process identity and verified descendants, not every process with a matching executable name. A bootstrapper that delegates through an unrelated service, an already-running process, or an intermediate process that disappears before discovery may need a direct game executable instead. Do not broaden tracking to unrelated processes merely to make a game close.

With no process names selected, KidLauncher tracks all verified descendants. If you select names, it tracks only matching descendants and the initial process. Other descendants remain visible as test candidates but do not receive exit requests. Select every executable that needs the game's display and disc settings. Recovery preserves the selection saved when the session started.

## A disc will not mount

1. Verify the image and `VCDMount.exe` paths.
2. Verify that the selected unit is an existing Virtual CloneDrive unit.
3. Verify the unit's Windows drive letter in Virtual CloneDrive and Windows Explorer.
4. Check the expected volume label and readiness file. The readiness file must be relative to the CD drive.
5. Increase the mount timeout if Windows recognizes this image slowly.
6. Check whether another application has locked the drive.

KidLauncher does not create units or install a driver. It waits for both the command and Windows disc readiness. An unverifiable unit mapping is an error, not permission to guess. Different Virtual CloneDrive releases can expose different registry or device state. Collect the diagnostic before changing unrelated units.

The selected drive is dedicated to KidLauncher. Starting a disc game replaces any existing disc without identifying or saving it. Previous images are never restored. Cleanup ejects the dedicated drive when **Unmount after the game exits** is enabled. Other drives are not changed.

If an older build reported a missing media-change counter, update both applications. Current builds no longer require that counter. Close all games and retry recovery to finish an interrupted cleanup. Preserve recovery records and logs if display restoration or eject still fails.

## Configuration will not load or save

1. Check the data directory shown in Diagnostics.
2. Preserve `games.json` and `games.json.bak`.
3. Check that the directory is writable and has free space.
4. Restore a known valid backup if both files are damaged.

Future schema versions are rejected rather than silently downgraded. An older application must not overwrite configuration from a newer incompatible version. Normal configuration changes belong in the graphical configurator.

## 16-bit Windows 3.x games

Windows 7 normally runs every 16-bit Windows program inside one shared `ntvdm.exe`. A program started into that shared process has no process of its own, so the launcher could not record it. KidLauncher therefore starts each `.exe` game with its own `ntvdm.exe` (`CREATE_SEPARATE_WOW_VDM`). F6 closes that `ntvdm.exe` and the game inside it. 32-bit games ignore this setting. Shortcuts (`.lnk`) and games set to run as administrator still start through the Windows shell.

Windows also runs a new 16-bit program inside an `ntvdm.exe` that is already running, even when the game's own launcher (such as CWRUN) starts it. An `ntvdm.exe` left over from an earlier game is not a child of the next game, so the launcher cannot follow that game: it ends the session after the startup grace period and ejects the disc while the game still runs. Before each game starts, KidLauncher therefore closes any running `ntvdm.exe` and logs `Closing leftover 16-bit helper from an earlier game`. This also closes a 16-bit program started outside the launcher. A game's own `ntvdm.exe` is always tracked and closed on exit, even when **Process names** lists only other names.
