using System;
using System.Collections.Generic;

namespace KidLauncher.Core
{
    public sealed class LauncherConfiguration
    {
        public int SchemaVersion { get; set; } = 1;
        public string Language { get; set; } = "nl";
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
        public bool IdleAnimations { get; set; } = true;
        public bool AllGamesOnOneScreen { get; set; } = true;
    }

    public static class TileGrid
    {
        // Beyond this many games, one screen would make cards too small; pages of this size follow.
        public const int MaxOnOneScreen = 30;

        // Card height divided by width: a square picture, plus a one-line title band when titles show.
        public static double CardRatio(bool showTitles) { return showTitles ? 1.18 : 1.0; }

        // The column count that gives the largest cards when count cards of the given ratio fill width × height.
        public static int FitColumns(int count, double width, double height, double ratio)
        {
            int best = 1; double bestSize = 0;
            for (int columns = 1; columns <= Math.Max(1, count); columns++)
            {
                int rows = (count + columns - 1) / columns;
                double size = Math.Min(width / columns, height / rows / ratio);
                if (size > bestSize + 0.5) { best = columns; bestSize = size; }
            }
            return best;
        }
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
    }

    public sealed class DisplayMode
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int BitsPerPixel { get; set; }
        public int Frequency { get; set; }
        public int VariantNumber { get; set; }
        public int? EnumerationIndex { get; set; }
        // Full native DEVMODE, including device-specific data, for exact recovery.
        public byte[] NativeData { get; set; }
        public override string ToString()
        {
            var scaling = "Unspecified";
            if (NativeData != null && NativeData.Length >= 220)
            {
                // dmDisplayFixedOutput is meaningful only when its dmFields bit is set.
                uint output = (BitConverter.ToUInt32(NativeData, 72) & 0x20000000) != 0
                    ? BitConverter.ToUInt32(NativeData, 88) : 0;
                scaling = output == 0 ? "Default" : output == 1 ? "Stretch" : output == 2 ? "Center" : "Driver value " + output;
            }
            return (VariantNumber > 0 ? "(" + VariantNumber + ") " : "") +
                Width + " × " + Height + " / " + Frequency + " Hz / " + BitsPerPixel + " bit / " + scaling;
        }
        public string DescribeNative() => ToString() + "; enumeration index=" +
            (EnumerationIndex.HasValue ? EnumerationIndex.Value.ToString() : "unknown") +
            "; native DEVMODE base64=" + (NativeData == null ? "none" : Convert.ToBase64String(NativeData));
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
