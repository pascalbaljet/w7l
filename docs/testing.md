# Test a portable build on Windows 7

Record the build commit, Windows version and bitness, graphics driver, monitor, privilege levels, and Virtual CloneDrive version. CI builds on Windows Server 2022 and does not replace these checks.

Start with a disposable game or a game with saved progress backed up. Keep a keyboard and access to Task Manager available during the first tests.

## Launcher and settings

- [ ] Both executables open on Windows 7 SP1 with only .NET Framework 4.8 installed.
- [ ] A second launcher does not create another instance.
- [ ] Twelve tiles fit at 1920×1080 without clipping. Test Windows display scaling as well.
- [ ] Single-click launches once. Repeated clicks do not start duplicate sessions.
- [ ] Artwork preserves its aspect ratio and missing artwork has a usable fallback.
- [ ] Thirteen games produce another page. Arrows and mouse wheel work, and the page is preserved after playing.
- [ ] Idle cursor hiding and immediate return on mouse movement work.
- [ ] Ctrl+Alt+S requires the correct PIN. Settings and adult exit are not exposed as child controls.
- [ ] Import previews user and public Desktop shortcuts, including arguments and working directory. Only selected shortcuts are imported.
- [ ] Add, duplicate, remove, hide, reorder, crop artwork, appearance preview, and save work without editing JSON.
- [ ] Autostart opens the fullscreen launcher after login and can be disabled again.

## Game and process lifecycle

- [ ] A game without a disc launches at its configured supported resolution.
- [ ] One short F6 press returns from a cooperative game and restores the exact prior display mode.
- [ ] One short F6 press closes an unresponsive tracked game after the force-exit timeout.
- [ ] Disabling force-exit leaves an unresponsive game alive and does not unmount its disc or clear recovery state.
- [ ] A bootstrap process can exit while the child game remains active.
- [ ] An unrelated process with the same name is not closed or killed.
- [ ] A crashed game returns to the launcher at the prior resolution.
- [ ] A missing executable and a rejected display mode show a recoverable error.
- [ ] Resolution-test countdown always restores the previous mode, including after closing the dialog.
- [ ] An elevated game is tested separately. Record any privilege or hotkey limitation.
- [ ] The configurator cannot start another game test while the launcher owns an active session.

## Disc lifecycle

- [ ] The configured existing unit and drive letter are correctly verified.
- [ ] A wrong letter is rejected without changing another drive.
- [ ] The correct image is mounted before launch and its volume or readiness file is checked.
- [ ] An already mounted requested image is reused without unnecessary replacement.
- [ ] Mount failures and timeouts prevent game launch.
- [ ] Cleanup waits until all tracked game processes have exited.
- [ ] Unmount and previous-image restoration follow the chosen settings.
- [ ] Other Virtual CloneDrive units and unrelated mounted images remain unchanged.

## Recovery

- [ ] End the launcher unexpectedly during a controlled game test, then close the game manually. Restart and verify saved display and disc recovery.
- [ ] Restart while a tracked game is still running. Recovery refuses unsafe cleanup and preserves state.
- [ ] Simulate a restoration failure and verify that recovery state remains for the next retry.
- [ ] Check the logs for launch, detected processes, hotkey/exit, display restoration, disc cleanup, and errors.

Do not mark the build fully accepted until scenarios A–G in `PLAN.md` have passed on the target computer. Report the failed checklist item and attach the relevant log, without personal paths or game data if those are sensitive.
