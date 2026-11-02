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

## Project layout

- `src/KidLauncher`: fullscreen WPF launcher.
- `src/KidLauncher.Configurator`: graphical parent configuration tool.
- `src/KidLauncher.Core`: configuration, session orchestration, recovery, and Windows integration.
- `tests/KidLauncher.Tests`: executable regression tests with fake operating-system services.
- `scripts/build.ps1`: Windows build, test, and portable packaging.
- `examples/games.json`: empty configuration for first-run setup.

`PLAN.md` defines the intended requirements. See [troubleshooting](docs/troubleshooting.md) for operational limits and recovery procedures.
