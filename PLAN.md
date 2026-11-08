
# Project: KidLauncher

Build a complete, production-quality, child-friendly Windows game launcher and graphical configuration tool.

The launcher is intended for a four-year-old child playing classic Windows CD-ROM games on an existing Windows 7 computer.

The games are already installed and working. They must continue running on Windows 7. Upgrading the operating system is NOT an option.

I want a polished, extremely simple experience resembling a children's game console:

1. Turn on the computer.
2. See a fullscreen collection of large, colorful game tiles.
3. Single-click a game.
4. The launcher automatically mounts the correct CD image if needed, changes the display resolution, and starts the game.
5. Press one dedicated keyboard key (F6 by default) to exit the game.
6. The launcher restores the original resolution and returns to the game selection screen.

The child must never need to interact with Windows Explorer, desktop shortcuts, CD mounting software, configuration dialogs, or the Windows taskbar.

The project must include a separate graphical configuration application so that I never need to manually edit JSON files.

Implement the actual working software, not just a prototype or architectural proposal.

## 1. Platform and technical requirements

Target computer:
- Windows 7 SP1
- Both 32-bit and 64-bit Windows 7 installations should be supported.
- 21-inch monitor, native resolution 1920x1080
- Mouse and keyboard
- Legacy Windows games, including older fullscreen DirectDraw and DirectX applications
- Virtual CloneDrive 5.5.x already installed
- Some games may require administrator privileges.
- The computer must remain on Windows 7.

Technology:
- C#
- WPF
- .NET Framework 4.8 EXACTLY
- x86 compilation target
- Classic WPF App (.NET Framework) project
- Windows 7-compatible Win32 interop where necessary
- No modern .NET runtime
- No Electron, Tauri, WebView2, WinUI, UWP, or browser engine
- No Windows 8/10/11-only APIs
- No runtime internet dependency
- Avoid unnecessary external NuGet dependencies.
- Do not introduce unsupported .NET APIs.

The software must run on Windows 7 SP1 with .NET Framework 4.8 installed.

Development and compilation may happen on a modern Windows machine using Visual Studio 2022 or compatible MSBuild tools.

Do not require Visual Studio, an SDK, Node.js, Python, or developer tools on the target Windows 7 computer.

Deliver portable release builds that can be copied to the target computer without an installer.

## 2. Solution architecture

Create a clean Visual Studio solution with these projects:

KidLauncher
- Main fullscreen child-facing launcher
- WPF executable

KidLauncher.Configurator
- Separate graphical configuration utility
- WPF executable

KidLauncher.Core
- Shared configuration models
- Configuration loading, validation, and saving
- Game process management
- Display resolution management
- Virtual CloneDrive integration
- Keyboard hotkey handling
- Recovery and logging functionality

KidLauncher.Tests
- Automated tests for non-UI logic
- Use a testing setup compatible with the project requirements.

Use clean separation of responsibilities and interfaces for operating-system-dependent services.

Keep the architecture practical. Do not overengineer it.

## 3. Main launcher interface

The launcher must be designed specifically for a four-year-old.

Target display: 1920x1080 on a 21-inch monitor.

Requirements:
- Start automatically in borderless fullscreen mode.
- Cover the Windows desktop and taskbar.
- No title bar, minimize button, or standard window controls.
- Display a visually attractive collection of large game tiles.
- Use a 4-column by 3-row layout by default.
- Maximum 12 games per page.
- Tiles should be approximately 400x240 pixels at the target resolution, adjusted to fit.
- Use generous spacing between tiles.
- Display large artwork with a clearly readable game title.
- Entire tile must be clickable.
- Single mouse click launches the game.
- Never require double-clicking.
- Prevent accidental multiple launches from repeated clicks.
- Provide visual feedback when a tile is clicked.
- Show a simple, attractive loading state when starting a game.
- No technical information in the child interface.
- Support more than 12 games using additional pages.
- Use large previous/next navigation arrows.
- Keep navigation simple and consistent.
- Support mouse-wheel navigation between pages if useful.
- No tiny buttons, small scrollbars, or complicated menus.
- Automatically hide the mouse cursor after a configurable period of inactivity.
- Show the cursor immediately when the mouse moves.
- Restore the same launcher page after returning from a game.
- Handle Windows display scaling correctly.

The UI should look like a dedicated game console, not a standard business application.

Prefer a playful but clean and timeless visual design.

Avoid excessive animations or GPU-heavy effects that might perform poorly on old hardware.

Use WPF layout and rendering rather than any embedded web technology.

## 4. Game tiles and artwork

Each game has:
- Display title
- Executable path
- Working directory
- Optional command-line arguments
- Tile image
- Optional executable icon fallback
- Sort order
- Visibility setting
- Optional custom resolution
- Optional CD image
- Process tracking configuration

Artwork requirements:
- Support PNG and JPEG images.
- Support extracting icons from .exe and .lnk files.
- Display a reasonable fallback when artwork is missing.
- Preserve image aspect ratios.
- Allow artwork cropping through the configurator.
- Store managed artwork in an Assets directory.
- Avoid stretching images incorrectly.
- Allow games to be reordered.

The launcher must never modify game installation files unnecessarily.

## 5. Game launching

When a game is selected:

1. Prevent additional game launches.
2. Validate the game configuration.
3. Prepare the required CD image, if any.
4. Capture the current Windows display mode.
5. Switch to the configured game resolution, if required.
6. Hide the launcher.
7. Start the executable with its configured arguments and working directory.
8. Track the game processes.
9. Wait until the game has finished.
10. Restore the original display mode.
11. Perform CD-image cleanup.
12. Restore and focus the fullscreen launcher.

Handle launch failures gracefully.

Some old games start a secondary process and immediately terminate their original launcher process.

Support this behavior.

Each game must support multiple configurable process names.

Do not assume that the initially launched process is always the actual game process.

Implement reasonable child-process discovery and configurable startup grace periods.

Do not automatically return to the launcher simply because a small bootstrap process exits.

Avoid accidentally tracking or terminating unrelated Windows processes that happen to share the same executable name.

## 6. Per-game resolution management

Every game must support an optional display resolution setting.

Examples:
- Use current resolution
- 640x480
- 800x600
- 1024x768
- 1280x720

The actual available resolutions must be enumerated from the Windows display driver.

Support:
- Width
- Height
- Refresh rate
- Color depth

Use Windows 7-compatible Win32 APIs such as:
- EnumDisplaySettings
- ChangeDisplaySettingsEx
- DEVMODE

Requirements:
- Read and store the complete original display configuration.
- Validate a requested resolution before applying it.
- Change resolution temporarily.
- Never permanently change the registry display configuration.
- Do not use CDS_UPDATEREGISTRY.
- Restore the exact previous display mode whenever possible.
- Handle games that change resolution themselves.
- Restore the display after normal exit, F6 exit, forced termination, launch failure, or game crash.
- Attempt restoration during application shutdown.
- Persist recovery state before changing the resolution.

Store recovery state in a writable location.

For example:
Data/display-session.json

If the launcher crashes while a game resolution is active, detect this on the next startup and attempt restoration before showing the launcher.

Only clear recovery state after successful restoration.

Do not assume recovery is possible after every type of system failure. Document any limitations and provide a manual recovery procedure.

If restoration fails, show an adult-oriented error and log the problem.

## 7. Global exit-game hotkey

Implement a configurable global key used to exit the currently running game and return to the launcher.

Default key: F6

The key must work while a game has keyboard focus.

It should work with as many legacy fullscreen games as technically possible.

Implement this using Windows 7-compatible Win32 APIs.

Evaluate:
- RegisterHotKey
- WH_KEYBOARD_LL low-level keyboard hook

Choose a robust implementation with a fallback if necessary.

Do not rely on WPF keyboard events because the launcher will not have focus while a game is running.

Requirements:
- Default hotkey is F6.
- Hotkey can be changed in the configurator.
- Support unusual keys such as Scroll Lock or Pause where feasible.
- Validate that the hotkey can be registered.
- Avoid F12 as the default.
- Do not send the hotkey to the game when it is being used by the launcher, where technically possible.
- Prevent repeated keydown events from triggering duplicate shutdown operations.
- Support configurable forced-exit behavior.

The most important requirement:

ONE SHORT PRESS OF F6 MUST BE SUFFICIENT TO RETURN TO THE LAUNCHER, EVEN IF THE GAME DOES NOT RESPOND TO A NORMAL CLOSE REQUEST.

Exit procedure:

1. F6 is pressed once.
2. Attempt graceful game shutdown via CloseMainWindow and/or WM_CLOSE.
3. Wait for a configurable timeout, default 3 seconds.
4. If the game still runs, automatically terminate only the tracked game processes when automatic force-exit is enabled.
5. Wait for process termination.
6. Restore the original display resolution.
7. Clean up the mounted CD image.
8. Restore the fullscreen launcher.

Allow automatic forced termination to be disabled for specific games if needed.

Do not kill unrelated processes.

Log whether a game exited gracefully or was forcefully terminated.

If the global hotkey cannot reliably be received from a particular game or privilege level, detect or document this limitation rather than claiming universal compatibility.

Ensure the launcher is not left in an unusable state.

## 8. Virtual CloneDrive 5.5.x integration

The target computer already has Virtual CloneDrive 5.5.x installed.

Several games require CD images.

There are no unused virtual drive slots available.

The launcher must reuse an existing Virtual CloneDrive virtual drive and automatically switch images before launching games.

Do not create additional virtual drives.

Do not install another virtual drive driver.

Use the installed Virtual CloneDrive command-line utility:

VCDMount.exe

Supported command examples:

Mount:
VCDMount.exe /d=0 "C:\Images\Game.iso"

Unmount:
VCDMount.exe /d=0 /u

Use ProcessStartInfo to launch VCDMount.exe.

Ensure correct quoting and working directory handling.

Do not invoke VCDMount.exe through an unnecessary command shell.

Detect the installed VCDMount.exe path when possible.

Also allow manually selecting its location through the configurator.

Do not assume every system has the same installation path.

Support:
- ISO
- BIN
- IMG
- UDF
- DVD
- CCD
- Other image formats supported by the installed Virtual CloneDrive version.

## 9. Per-game CD image settings

CD image mounting must be optional per game.

Each game can configure:

- Whether a disc image is required
- Image file path
- Virtual CloneDrive unit index
- Expected drive letter
- Optional expected volume label
- Optional file to check for mount readiness
- Mount timeout
- Whether to unmount after exiting

Example:

discImage:
  enabled: true
  imagePath: "Disc Images\PuttPutt.iso"
  virtualDriveIndex: 0
  expectedDriveLetter: "V:"
  mountTimeoutMs: 10000
  unmountAfterExit: true

The actual persistent configuration format should be JSON.

Important:
- Virtual CloneDrive unit index and Windows drive letter are not necessarily interchangeable.
- Verify the actual mapping.
- Never assume unit 0 is always V:.
- A game may expect a specific CD-ROM drive letter.
- Do not change unrelated Virtual CloneDrive units.

When starting a game requiring a disc:

1. Verify the configured drive letter belongs to Virtual CloneDrive.
2. Eject any current media from that dedicated drive, regardless of who mounted it.
3. Mount the requested image.
4. Wait for the Virtual CloneDrive command to complete.
5. Wait until Windows recognizes the new virtual disc.
6. Verify the expected volume or readiness file when configured.
7. Continue only when the disc is ready.

Use polling with a configurable timeout.

Do not assume the disc is ready immediately after VCDMount.exe exits.

Handle failures, including:
- Missing image file
- Missing VCDMount.exe
- Invalid unit index
- Wrong drive letter
- Mount failure
- Mount timeout
- Unexpected disc contents
- Locked or inaccessible drive

If mounting fails, do not start the game.

Show a simple full-screen error with an option to return to the launcher.

Technical details must be available in logs or a protected adult view.

After game exit:
- Wait until the tracked game processes have terminated.
- Never unmount while the game is still accessing the virtual disc.
- Unmount if configured.
- Never restore the previous image.
- Preserve media on other drives. The configured drive belongs exclusively to KidLauncher.

Treat mounting and unmounting as serialized operations.

Maintain recovery information for interrupted mounting and cleanup. Do not require a media-change counter or previous-image ownership journal.

## 10. Fullscreen and window management

The launcher must behave like a game console interface.

Requirements:
- Start borderless and maximized on the primary monitor.
- Hide while a game is running.
- Avoid taskbar flickering when returning.
- Restore focus after game termination.
- Handle display changes.
- Work correctly when a game runs in exclusive fullscreen mode.
- Avoid overlay-based controls that may fail with legacy DirectDraw games.
- Do not depend on Windows 10 virtual desktop features.

Support starting automatically when the Windows user logs in.

Prefer a normal startup shortcut rather than replacing explorer.exe as the Windows shell.

Provide optional configuration for starting with Windows.

Keep an adult-only route back to the Windows desktop.

## 11. Separate graphical configurator

Build a second application:

KidLauncher.Configurator.exe

This application should be fully graphical.

I must never need to edit games.json manually.

It must work on Windows 7 SP1.

The configurator should have a modern, tidy desktop UI with sensible tabs or sections:

- Games
- Appearance
- Controls
- Virtual Drives
- General Settings
- Diagnostics

The configurator must share the configuration models and validation logic with the launcher.

### Games management

Support:
- Add game
- Edit game
- Duplicate game
- Remove game
- Hide/show game
- Reorder games via drag and drop
- Reorder via up/down buttons
- Browse for an executable
- Browse for a Windows shortcut
- Automatically populate the working directory
- Configure command-line arguments
- Configure process names
- Configure image artwork
- Configure resolution
- Configure CD image settings
- Test the game

Display a clear list of all configured games.

### Desktop shortcut importing

This is an important feature.

The installed games currently have Windows desktop shortcuts.

Add an "Import from Desktop" feature.

Scan the current user's and public desktop locations for .lnk shortcuts.

Allow selecting which shortcuts to import.

Extract when possible:
- Display name
- Executable path
- Arguments
- Working directory
- Icon
- Run-as-administrator setting or relevant launch information

Handle shortcuts that point to another launcher rather than directly to the game.

Do not import every desktop shortcut automatically.

Provide a selection screen with a preview before importing.

Use Windows 7-compatible Shell Link APIs or COM interop.

### Artwork editor

Allow selecting PNG or JPEG files.

Support:
- Image preview
- Cropping to tile aspect ratio
- Zooming
- Centering
- Replacing artwork
- Extracting executable icons as fallbacks

Copy selected artwork into the launcher's Assets directory when appropriate.

### Resolution editor

Provide a dropdown of supported display modes.

Include:
- Use current resolution
- Specific resolution
- Refresh rate
- Color depth

Provide a resolution test feature.

When testing a resolution:
- Temporarily apply the selected mode.
- Display a confirmation dialog with a countdown.
- Restore the previous mode unless confirmed.
- Always restore after testing is completed.

### Process detection

Provide a "Test Game" feature.

When testing:
- Start the game using its configured settings.
- Detect the initial executable.
- Observe child processes and process changes.
- Show candidate process names.
- Let the user select the correct processes to track.
- Save the selected process names to the game's configuration.

Be careful not to confuse unrelated processes with game processes.

### Virtual CloneDrive configuration

Provide a dedicated Virtual Drives section.

Features:
- Detect VCDMount.exe.
- Allow manual path selection.
- Detect available Virtual CloneDrive units when possible.
- Show their corresponding Windows drive letters.
- Allow selecting a managed unit.
- Browse for CD images.
- Mount image for testing.
- Unmount image.
- Read the mounted volume label.
- Select a readiness file.
- Configure mount timeout.
- Configure post-game cleanup.
- Validate that the intended disc can actually be accessed.

Do not modify unrelated virtual drives during testing.

### Appearance

Allow configuring:
- Tile columns
- Tile rows
- Tiles per page
- Spacing
- Background color or image
- Tile corner radius
- Game title visibility
- Mouse cursor hiding delay

Provide a live preview of the fullscreen launcher at a scaled representation of 1920x1080.

Preview artwork, spacing, titles, pagination, and tile sizing.

### Controls

Allow configuring:
- Exit-game hotkey, default F6
- Graceful exit timeout
- Automatic forced termination after timeout
- Adult settings access
- Optional startup with Windows

The selected hotkey must be checked for compatibility where possible.

### Protected adult access

The child-facing launcher must not expose an obvious settings button.

Provide a hidden method for opening adult settings.

This may be a keyboard combination or a hidden corner interaction.

Protect entry using a PIN when one is configured. Without a PIN, the hidden shortcut opens the adult menu directly and permits exit to Windows.

Do not store the PIN in plaintext.

Use a suitable salted password hash supported on .NET Framework 4.8.

The separate configurator may also be opened directly from Windows Explorer.

## 12. Configuration storage

Use JSON for persistent settings.

However, all normal configuration must be possible through the GUI.

Include:
- Schema version
- Global launcher settings
- Game collection
- Display settings
- CD mounting settings
- Process tracking settings
- Hotkey settings
- Appearance preferences

Use strongly typed C# configuration models.

Support validation and future schema migrations.

Prefer relative paths for files located inside the launcher directory.

Support absolute paths for externally installed games and CD images.

Resolve relative paths against the application directory, not the current process working directory.

Save configuration safely:
- Write a temporary configuration file.
- Validate it.
- Atomically replace the previous configuration where supported.
- Create a backup before overwriting.
- Preserve a previous valid configuration if saving fails.

Avoid configuration corruption after power loss.

Do not write to Program Files.

Choose a sensible writable data location, or a portable application directory with appropriate permissions.

Clearly document the storage behavior.

## 13. Error handling and crash recovery

This application is intended to be operated by a child.

Reliability is extremely important.

The launcher must survive common problems without leaving the child at a Windows error dialog or unusable desktop.

Handle:
- Missing game executable
- Missing artwork
- Invalid configuration
- Failed game launch
- Failed resolution change
- Failed CD mount
- Missing virtual drive
- Game process crash
- Forced game termination
- Launcher restart
- Unhandled exceptions
- Windows shutdown
- Multiple launcher instances

Use a single-instance mutex.

Add structured file logging.

Logs should contain:
- Timestamp
- Game selected
- Image mounted
- Mount result
- Resolution before launch
- Resolution applied
- Process started
- Detected game processes
- Hotkey pressed
- Game exit result
- Forced termination events
- Resolution restoration
- Image cleanup
- Errors and exceptions

Keep logs reasonably small using rotation.

Implement startup recovery for unfinished display and disc sessions.

Use try/finally for cleanup, but also provide recovery mechanisms for cases where finally cannot execute.

Do not rely solely on Application.Current.Exit for recovery.

Make recovery operations idempotent where possible.

If recovery cannot be completed automatically, explain the problem in an adult-friendly diagnostic view.

## 14. Compatibility and permissions

Some games display UAC administrator shields on their shortcuts.

Do not assume all games need elevation.

Provide diagnostics for:
- Executable requiring administrator privileges
- Launcher and game running at different integrity levels
- Insufficient permissions to terminate a game
- Failure to register the global hotkey
- Insufficient permissions to access game files

Do not disable Windows UAC globally.

Do not silently grant administrator privileges to arbitrary programs.

Document the possible elevation setup for trusted, already-installed games.

Support the target environment without changing the games themselves unless explicitly necessary.

Do not interfere with game compatibility modes.

Avoid enabling compatibility shims automatically.

## 15. Autostart and child-friendly environment

Provide an option in the configurator to enable launcher autostart for the current Windows user.

Use Windows 7-compatible startup mechanisms.

The launcher should automatically appear fullscreen after login.

Avoid changing the Windows shell or system-wide configuration unnecessarily.

Provide clear instructions for creating a dedicated local Games user account.

The launcher itself must work completely offline.

## 16. Testing strategy

The software must be testable without the developer having Virtual CloneDrive or Windows 7 installed.

Introduce interfaces for:
- Display resolution operations
- Virtual drive operations
- Process launching and tracking
- Hotkey registration
- File system operations where useful

Create mock or fake implementations for tests.

Test at least:
- Configuration serialization/deserialization
- Configuration validation
- Relative and absolute path resolution
- Missing executable handling
- Unsupported display modes
- Resolution restoration after exceptions
- Launch failures
- Child process handoff behavior
- Normal game exit
- F6 graceful exit
- F6 forced exit
- Mounting the correct image
- Reusing an already mounted image
- Mount timeout
- Failed image verification
- Cleaning up a mounted image
- Preserving unrelated virtual drives
- Recovery after interrupted sessions
- Single-instance behavior

Do not claim that Windows 7 compatibility has been verified merely because compilation succeeded on modern Windows.

Provide a manual testing checklist for the actual Windows 7 computer.

## 17. Packaging and delivery

Create a complete Visual Studio solution.

Include:
- Full source code
- Project files
- Shared library
- Configurator
- Tests
- Release build script
- Example configuration
- README
- Windows 7 deployment guide
- Troubleshooting guide

Provide a portable release directory.

Suggested structure:

KidLauncher/
  KidLauncher.exe
  KidLauncher.Configurator.exe
  KidLauncher.Core.dll
  games.json
  Assets/
  Disc Images/
  Data/
  Logs/
  Backups/

Adapt the structure as necessary for a clean implementation.

The release must not require an installer.

Document how to:
- Install .NET Framework 4.8 on Windows 7 SP1
- Copy the application onto the computer
- Configure games
- Import existing desktop shortcuts
- Configure Virtual CloneDrive
- Configure per-game resolution
- Configure the F6 exit key
- Enable autostart
- Recover from a stuck display resolution
- Diagnose a game that does not close correctly

## 18. Acceptance criteria

Consider the project complete only when these scenarios are implemented:

Scenario A: Normal game without CD image
- User clicks a game tile once.
- The configured resolution is applied.
- The game opens.
- User presses F6.
- The game closes.
- The original 1920x1080 resolution returns.
- The fullscreen launcher returns.

Scenario B: Game requiring CD image
- User clicks a game tile.
- The existing configured VCD unit is reused.
- The correct image is mounted.
- The launcher verifies the disc is ready.
- The configured resolution is applied.
- The game launches.
- User presses F6.
- The game exits.
- Original resolution is restored.
- The image is unmounted or restored according to configuration.
- The launcher returns.

Scenario C: Game refuses to close
- User presses F6 once.
- A graceful exit is attempted.
- After the configured timeout, the launcher forcefully closes only
  the tracked game processes if automatic force-exit is enabled.
- The original display resolution is restored.
- The launcher returns.

Scenario D: Game crashes
- The game crashes unexpectedly.
- The launcher detects termination.
- Display resolution is restored.
- Disc state is cleaned up according to configuration.
- The launcher returns.

Scenario E: Configuration
- Adult opens KidLauncher.Configurator.exe.
- Imports an existing game from a desktop shortcut.
- Chooses tile artwork.
- Sets the desired resolution.
- Selects a CD image if required.
- Tests the game.
- Saves configuration through the GUI.
- The game appears correctly in the launcher.
- No manual JSON editing is necessary.

Scenario F: More than twelve games
- Launcher shows twelve large tiles.
- User can navigate to additional pages.
- All tiles remain large and easy to click.

Scenario G: Interrupted session
- A previous launcher session ended unexpectedly.
- On restart, the launcher detects unfinished recovery state.
- It attempts to restore the original display resolution and managed
  virtual-disc state before entering normal operation.

## 19. Implementation instructions

Implement the actual project.

Do not stop after producing a design document.

Work in logical stages:

1. Create the solution and compatible project structure.
2. Implement shared configuration models and validation.
3. Implement the fullscreen launcher and game tile interface.
4. Implement game process launching and tracking.
5. Implement resolution switching and recovery.
6. Implement the global exit-game hotkey.
7. Implement Virtual CloneDrive integration.
8. Implement the graphical configurator.
9. Implement desktop shortcut importing.
10. Implement artwork, game testing, and process detection.
11. Implement startup behavior, diagnostics, and crash recovery.
12. Implement automated tests.
13. Build the Release configuration.
14. Fix compilation errors.
15. Produce the portable release and documentation.

Use real implementations rather than placeholder methods or simulated behavior in the final application.

If the current environment does not support compiling .NET Framework Windows applications, still create the complete project, but clearly state that compilation and real-machine testing remain unverified.

Do not claim that a feature works unless it has been implemented.

Prioritize Windows 7 compatibility, reliability, simplicity, and the child's user experience over modern frameworks or architectural complexity.

The final result should feel like a dedicated children's gaming console running on top of Windows 7.

Development environment:
- Development happens on macOS or Linux using Codex or Claude Code.
- Do not assume Visual Studio or Windows is installed locally.
- Include a GitHub Actions workflow using windows-2022.
- Automatically compile all applications targeting .NET Framework 4.8 x86.
- Run automated tests during CI.
- Package the portable release as a downloadable ZIP artifact.
- Keep Windows-specific build requirements isolated to CI.
- The resulting applications must run on Windows 7 SP1.
