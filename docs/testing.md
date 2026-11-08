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
- [ ] The pointer is large over every tile. Hover gently enlarges one tile without moving its neighbors and reverses when the pointer leaves.
- [ ] Click a tile, exit the game with F6, and move the pointer away. The card returns to rest and responds to another hover.
- [ ] A partially filled page centers its cards. The launcher stays still while idle, including with Windows animations enabled.
- [ ] The game grid has no decorative headings or page text. Page arrows appear only for multiple pages.
- [ ] Ctrl+Alt+S requires the correct PIN when configured. Without a PIN, it opens the adult menu and permits exit to Windows.
- [ ] Import previews user and public Desktop shortcuts, including arguments and working directory. Only selected shortcuts are imported.
- [ ] Add, duplicate, remove, hide, reorder, crop artwork, appearance preview, and save work without editing JSON.
- [ ] Autostart opens the fullscreen launcher after login and can be disabled again.

## Game and process lifecycle

- [ ] Selecting 640×480, closing the resolution dropdown, and scrolling over it leaves 640×480 selected and scrolls the editor instead.
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
- [ ] The log records the requested mode, actual mode before and after process start, and the input source when F6 is received.
- [ ] The configurator cannot start another game test while the launcher owns an active session.

## Disc lifecycle

- [ ] The configured existing unit and drive letter are correctly verified.
- [ ] A wrong letter is rejected without changing another drive.
- [ ] The correct image is mounted before launch and its volume or readiness file is checked.
- [ ] An existing disc, whether unknown or the same image, is replaced by the requested game image.
- [ ] Mount failures and timeouts prevent game launch.
- [ ] Cleanup waits until all tracked game processes have exited.
- [ ] Unmount after exit follows the chosen setting. The previous image is never restored, including with older configuration or recovery files.
- [ ] Other Virtual CloneDrive units and unrelated mounted images remain unchanged.
- [ ] Mounting and ejecting work without a driver media-change counter.

## Recovery

- [ ] End the launcher unexpectedly during a controlled game test, then close the game manually. Restart and verify saved display and disc recovery.
- [ ] Restart while a tracked game is still running. Recovery refuses unsafe cleanup and preserves state.
- [ ] Simulate a restoration failure and verify that recovery state remains for the next retry.
- [ ] Check the logs for launch, detected processes, hotkey/exit, display restoration, disc cleanup, and errors.

Do not mark the build fully accepted until scenarios A–G in `PLAN.md` have passed on the target computer. Report the failed checklist item and attach the relevant log, without personal paths or game data if those are sensitive.
