# KidLauncher

KidLauncher is a portable game launcher and graphical configurator for Windows 7 SP1 with .NET Framework 4.8. Both applications target x86 and run on 32-bit and 64-bit Windows.

The launcher provides large game tiles, temporary resolution changes, Virtual CloneDrive integration, and a global exit key. The default exit key is F6. The configurator manages games and settings without manual JSON edits.

## Get a Windows build

Open the latest successful **Windows build** run in [GitHub Actions](https://github.com/pascalbaljet/w7l/actions/workflows/windows.yml). Download the **KidLauncher-win7-x86** artifact and extract its ZIP. GitHub may wrap the portable ZIP in another ZIP.

Read the [deployment guide](docs/deployment.md) before testing with installed games. Windows CI compilation and automated tests do not establish Windows 7, fullscreen-game, or Virtual CloneDrive compatibility. Use the [real-machine checklist](docs/testing.md) to record those results.

## Build from source

On Windows, install the .NET 8 SDK and the .NET Framework 4.8 Developer Pack. Visual Studio 2022 Build Tools with the .NET desktop workload also supplies the framework reference assemblies. The SDK is a build tool, not the application runtime.

Run `scripts/build.ps1` from PowerShell. The script builds the solution, runs the dependency-free .NET Framework test executable, and creates `artifacts/KidLauncher-win7-x86.zip`. A failed build or test stops packaging.

The solution also compiles against framework reference assemblies in a Linux orb. Application execution and WPF rendering require Windows. `.agents/setup` installs the pinned compiler SDK and restores solution references in Amp orbs. `.agents/resume` performs no installs.

### Run the desktop E2E tests

Use an unlocked Windows desktop with no other KidLauncher instance running. Install `ffmpeg` on `PATH`. After `scripts/build.ps1`, run `scripts/e2e.ps1` from PowerShell. Do not use the mouse or keyboard during the run.

The tests extract the portable ZIP into disposable directories, configure games and a PIN through the GUI, and reopen the configurator to check persistence. Real mouse and keyboard input launches a test game and requests one-press F6 shutdown. The tests cover graceful and forced shutdown, an unrelated same-name process, a missing executable, PIN rejection, and authorized desktop exit. A separate negative control configures F7 and must fail specifically when F6 cannot close the game.

Windows CI runs these tests after the regression tests and WPF render checks. Download **Windows-E2E-evidence** from the workflow run for screenshots, desktop recordings, fixture events, and application logs. The tests do not switch display modes or mount disc images. Windows 7, real games, elevation, and Virtual CloneDrive still require the [real-machine checklist](docs/testing.md).

## Project layout

- `src/KidLauncher`: fullscreen WPF launcher.
- `src/KidLauncher.Configurator`: graphical parent configuration tool.
- `src/KidLauncher.Core`: configuration, session orchestration, recovery, and Windows integration.
- `tests/KidLauncher.Tests`: executable regression tests with fake operating-system services.
- `tests/KidLauncher.E2E`: external Windows UI Automation driver and native test game, without application project references.
- `scripts/build.ps1`: Windows build, test, and portable packaging.
- `scripts/e2e.ps1`: packaged-app desktop tests and evidence collection.
- `examples/games.json`: empty configuration for first-run setup.

`PLAN.md` defines the intended requirements. See [troubleshooting](docs/troubleshooting.md) for operational limits and recovery procedures.
