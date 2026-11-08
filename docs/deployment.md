# Deploy KidLauncher on Windows 7

## Prepare the computer

1. Confirm that Windows 7 SP1 is installed.
2. Install Microsoft's offline .NET Framework 4.8 runtime for Windows 7. Install any prerequisite Windows updates requested by the installer, including SHA-2 support, and restart the computer.
3. Confirm that the games already run from their existing shortcuts.
4. Confirm that Virtual CloneDrive 5.5.x can mount the required images. Do not add virtual drives for KidLauncher.
5. Create a standard local Windows account named Games if you want a separate child account. Sign in and check that the games are accessible there.

The target computer does not need Visual Studio, the .NET SDK, Node.js, Python, or internet access to run KidLauncher.

## Install or update KidLauncher

1. Download `KidLauncher-Setup-win7-x86.exe` from [GitHub Releases](https://github.com/pascalbaljet/w7l/releases).
2. Close KidLauncher and its configurator.
3. Run Setup and accept the administrator prompt.
4. Click **Install / Update** to install to `C:\Games\KidLauncher`.
5. Close Setup and open `KidLauncher.Configurator.exe` from that directory.

Setup replaces application files but preserves existing `games.json`, its backup, data, logs, artwork, disc images, and other user files. Setup does not move an older installation from another folder. Back up that folder before moving your configuration and assets. Setup does not install .NET Framework or Virtual CloneDrive.

For a different local directory, run Setup with `--install-dir "D:\Games\KidLauncher"`. Add `--quiet` for an unattended install from an already elevated process. Setup still requires administrator permission.

## Configure games

1. If you use the portable ZIP instead of Setup, extract the entire `KidLauncher` directory to a writable location. Do not copy only the executables.
2. Open `KidLauncher.Configurator.exe`.
3. Add games with **Add game**, or use **Import from Desktop** and select the shortcuts you want. Review the executable, arguments, working directory, and administrator setting before importing.
4. Choose artwork. Use the artwork editor to crop and save a managed copy in `Assets`.
5. Select **Use current resolution** unless the game needs a specific driver-supported mode. Test a selected mode and wait for restoration.
6. Set the adult PIN in **Controls**. Use at least four characters. The default game-exit key is F6.
7. Save the configuration.
8. Open `KidLauncher.exe` and select one game.
9. Press F6 once. Check that the game exits and the launcher returns at the original resolution.

The hidden adult shortcut is Ctrl+Alt+S. Select **Exit launcher to Windows** to close the launcher. If a PIN is configured, adult access requires that PIN. Without a PIN, the adult menu opens directly. Keep a copy of the original portable build and your configured data before updating.

The children's launcher defaults to Dutch, including when an older configuration has no language setting. To select English, choose **General → Children's launcher language → English**, save, and reopen the launcher. The configurator and adult dialogs stay in English.

## Configure a disc game

1. Select the installed `VCDMount.exe` in **Virtual drives**, or use detection.
2. Discover or select the existing Virtual CloneDrive drive letter. The service verifies both its ELBY device identity and the current user's `VCDDriveMask`.
3. Select the image in the game's disc settings and set the expected drive letter. Commands use `VCDMount.exe /l=<letter>` so Virtual CloneDrive resolves its own target unit. The legacy slot ID is not a driver index.
4. Set a volume label or a readiness file if the game needs additional disc verification.
5. Test mounting before launching the game.
6. Choose whether to unmount after exit. If disabled, the game's disc remains mounted until the next game replaces it.

Dedicate the selected drive to KidLauncher. Every disc game replaces whatever is mounted there, including media mounted outside KidLauncher. Previous images are never restored. The old Restore Previous Image setting is ignored in existing configuration and recovery files. Mount tests replace the disc and eject it when complete.

KidLauncher still verifies that the selected letter belongs to Virtual CloneDrive before changing it. Other drives remain untouched. Media-change counters and ownership journals are no longer required. Game and test recovery records remain in use to finish interrupted cleanup after the game exits.

## Enable automatic startup

1. Open **General** in the configurator.
2. Enable startup with Windows for the current user.
3. Sign out and sign in again to test the Startup shortcut.

KidLauncher does not replace Explorer or disable UAC. A game that needs elevation may show a UAC prompt. A non-elevated launcher may be unable to close an elevated game. For a trusted installed game, test running the launcher at the same privilege level. Do not disable UAC globally or grant elevation to untrusted programs.

## Find saved data

In a writable portable directory, `games.json` is stored beside the executables and runtime data is stored in `Data`. If the directory is not writable, configuration and data use `%LOCALAPPDATA%\KidLauncher` instead. Paths inside game settings are resolved against the application directory, not the current working directory.

Configuration saves write and flush a temporary file before atomic replacement. The previous valid configuration is retained as `games.json.bak`. A damaged primary file can be loaded from its valid backup. Never overwrite both files while investigating a load failure.

Recovery state is `Data\display-session.json`. Logs are under `Data\Logs`, with a 1 MiB rotation threshold and three retained archives. The configurator shows the active data paths.
