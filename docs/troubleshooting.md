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

One press requests graceful closure. With automatic force-exit enabled, tracked processes that remain after the configured timeout are terminated automatically. With force-exit disabled, an unresponsive game must be closed manually. Unsaved game progress can be lost after forced termination.

RegisterHotKey and low-level keyboard hooks cannot guarantee delivery from every exclusive-fullscreen, protected, or elevated application. If neither receives the key, close the game through Windows. F12 is reserved for debugging and is not a recommended exit key.

KidLauncher tracks process identity and verified descendants, not every process with a matching executable name. A bootstrapper that delegates through an unrelated service, an already-running process, or an intermediate process that disappears before discovery may need a direct game executable instead. Do not broaden tracking to unrelated processes merely to make a game close.

## A disc will not mount

1. Verify the image and `VCDMount.exe` paths.
2. Verify that the selected unit is an existing Virtual CloneDrive unit.
3. Verify the unit's Windows drive letter in Virtual CloneDrive and Windows Explorer.
4. Check the expected volume label and readiness file. The readiness file must be relative to the CD drive.
5. Increase the mount timeout if Windows recognizes this image slowly.
6. Check whether another application has locked the drive.

KidLauncher does not create units or install a driver. It waits for both the command and Windows disc readiness. An unverifiable unit mapping or previous-image identity is an error, not permission to guess. Different Virtual CloneDrive releases can expose different registry or device state. Collect the diagnostic before changing unrelated units.

If recovery cannot restore a previous image, mount it manually using Virtual CloneDrive after confirming that the game has exited. Preserve the recovery record until you have verified both display and disc state.

## Configuration will not load or save

1. Check the data directory shown in Diagnostics.
2. Preserve `games.json` and `games.json.bak`.
3. Check that the directory is writable and has free space.
4. Restore a known valid backup if both files are damaged.

Future schema versions are rejected rather than silently downgraded. An older application must not overwrite configuration from a newer incompatible version. Normal configuration changes belong in the graphical configurator.
