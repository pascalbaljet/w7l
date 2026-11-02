using System;
using System.Collections.Generic;

namespace KidLauncher.Core
{
    public sealed class LauncherConfiguration
    {
        public int SchemaVersion { get; set; } = 1;
        public List<Game> Games { get; set; } = new List<Game>();
        public AppearanceSettings Appearance { get; set; } = new AppearanceSettings();
        public ControlSettings Controls { get; set; } = new ControlSettings();
        public string VcdMountPath { get; set; } = "";
    }

    public sealed class Game
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "New game";
        public string ExecutablePath { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string ArtworkPath { get; set; } = "";
        public bool Visible { get; set; } = true;
        public int SortOrder { get; set; }
        public bool RunAsAdministrator { get; set; }
        public DisplayMode Resolution { get; set; }
        public DiscSettings Disc { get; set; } = new DiscSettings();
        public List<string> ProcessNames { get; set; } = new List<string>();
        public int StartupGraceMs { get; set; } = 5000;
        public int GracefulExitTimeoutMs { get; set; } = 3000;
        public bool AutomaticForceExit { get; set; } = true;
    }

    public sealed class AppearanceSettings
    {
        public int Columns { get; set; } = 4;
        public int Rows { get; set; } = 3;
        public int Spacing { get; set; } = 24;
        public string BackgroundColor { get; set; } = "#14233B";
        public string BackgroundImage { get; set; } = "";
        public int CornerRadius { get; set; } = 24;
        public bool ShowTitles { get; set; } = true;
        public int CursorHideSeconds { get; set; } = 5;
    }

    public sealed class ControlSettings
    {
        public int ExitVirtualKey { get; set; } = 0x75;
        public string PinSalt { get; set; } = "";
        public string PinHash { get; set; } = "";
    }

    public sealed class DiscSettings
    {
        public bool Enabled { get; set; }
        public string ImagePath { get; set; } = "";
        public int VirtualDriveIndex { get; set; }
        public string ExpectedDriveLetter { get; set; } = "";
        public string ExpectedVolumeLabel { get; set; } = "";
        public string ReadinessFile { get; set; } = "";
        public int MountTimeoutMs { get; set; } = 10000;
        public bool UnmountAfterExit { get; set; } = true;
        public bool RestorePreviousImage { get; set; }
    }

    public sealed class DisplayMode
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int BitsPerPixel { get; set; }
        public int Frequency { get; set; }
        // Full native DEVMODE, including device-specific data, for exact recovery.
        public byte[] NativeData { get; set; }
        public override string ToString() => Width + " × " + Height + " / " + Frequency + " Hz / " + BitsPerPixel + " bit";
    }

    public sealed class TrackedProcess
    {
        public int Id { get; set; }
        public long StartTimeUtcTicks { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class RecoverySession
    {
        public DisplayMode OriginalDisplay { get; set; }
        public DiscSettings Disc { get; set; }
        public bool DiscChanged { get; set; }
        public string PreviousImage { get; set; }
        public bool ProcessStartPending { get; set; }
        public List<string> ProcessNames { get; set; } = new List<string>();
        public List<TrackedProcess> Processes { get; set; } = new List<TrackedProcess>();
    }

    public sealed class DesktopShortcut
    {
        public string Title { get; set; }
        public string ShortcutPath { get; set; }
        public string TargetPath { get; set; }
        public string Arguments { get; set; }
        public string WorkingDirectory { get; set; }
        public string IconPath { get; set; }
        public bool RunAsAdministrator { get; set; }
    }
}
