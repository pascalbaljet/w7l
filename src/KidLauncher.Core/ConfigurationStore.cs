using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace KidLauncher.Core
{
    public sealed class ConfigurationStore
    {
        public string ApplicationDirectory { get; }
        public string DataDirectory { get; }
        public string ConfigurationPath { get; }

        public ConfigurationStore(string applicationDirectory = null)
        {
            ApplicationDirectory = Path.GetFullPath(applicationDirectory ?? AppDomain.CurrentDomain.BaseDirectory);
            var storage = ApplicationDirectory;
            try
            {
                var probe = Path.Combine(storage, ".write-test-" + Guid.NewGuid().ToString("N"));
                using (File.Create(probe)) { }
                File.Delete(probe);
            }
            catch (Exception e) when (e is UnauthorizedAccessException || e is IOException)
            {
                storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KidLauncher");
            }
            DataDirectory = Path.Combine(storage, "Data");
            Directory.CreateDirectory(DataDirectory);
            ConfigurationPath = Path.Combine(storage, "games.json");
            var portableConfiguration = Path.Combine(ApplicationDirectory, "games.json");
            if (!File.Exists(ConfigurationPath) && File.Exists(portableConfiguration) && storage != ApplicationDirectory)
                File.Copy(portableConfiguration, ConfigurationPath);
        }

        public string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ApplicationDirectory, path));
        }

        public LauncherConfiguration Load()
        {
            if (!File.Exists(ConfigurationPath)) return new LauncherConfiguration();
            try { return ReadConfiguration(ConfigurationPath); }
            catch (Exception primary) when (primary is IOException || primary is ArgumentException || primary is InvalidOperationException)
            {
                if (!File.Exists(ConfigurationPath + ".bak")) throw;
                try { return ReadConfiguration(ConfigurationPath + ".bak"); }
                catch (Exception backup) { throw new InvalidDataException("Neither games.json nor its backup is valid.", new AggregateException(primary, backup)); }
            }
        }

        private LauncherConfiguration ReadConfiguration(string path)
        {
            var configuration = new JavaScriptSerializer().Deserialize<LauncherConfiguration>(File.ReadAllText(path));
            if (configuration != null && configuration.SchemaVersion != 1)
                throw new NotSupportedException("This configuration schema requires a different KidLauncher version. The original file has not been replaced.");
            var errors = Validate(configuration);
            if (errors.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
            return configuration;
        }

        public void Save(LauncherConfiguration configuration)
        {
            var errors = Validate(configuration);
            if (errors.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
            var temporary = ConfigurationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteDurable(temporary, new JavaScriptSerializer().Serialize(configuration));
                ReadConfiguration(temporary);
                if (File.Exists(ConfigurationPath))
                {
                    // Do not replace a valid backup with a corrupt primary after backup recovery.
                    bool primaryValid;
                    try { ReadConfiguration(ConfigurationPath); primaryValid = true; }
                    catch (Exception e) when (e is IOException || e is ArgumentException || e is InvalidOperationException) { primaryValid = false; }
                    File.Replace(temporary, ConfigurationPath, primaryValid ? ConfigurationPath + ".bak" : null);
                }
                else File.Move(temporary, ConfigurationPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static void WriteDurable(string path, string content)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 4096, true))
                { writer.Write(content); writer.Flush(); }
                stream.Flush(true);
            }
        }

        public IReadOnlyList<string> Validate(LauncherConfiguration configuration, bool checkFiles = false)
        {
            var errors = new List<string>();
            if (configuration == null) { errors.Add("Configuration is empty."); return errors; }
            if (configuration.SchemaVersion != 1) errors.Add("Unsupported configuration schema. Use a compatible KidLauncher version.");
            if (configuration.Appearance == null || configuration.Controls == null || configuration.Games == null)
            { errors.Add("Configuration sections are missing."); return errors; }
            var appearance = configuration.Appearance;
            if (appearance.Columns < 1 || appearance.Rows < 1 || appearance.Columns > 12 || appearance.Rows > 12 || appearance.Columns * appearance.Rows > 12)
                errors.Add("The grid must contain between 1 and 12 tiles.");
            if (appearance.Spacing < 0 || appearance.Spacing > 100 || appearance.CornerRadius < 0 || appearance.CornerRadius > 100 || appearance.CursorHideSeconds < 0 || appearance.CursorHideSeconds > 600)
                errors.Add("Appearance spacing, radius, or cursor delay is out of range.");
            if (configuration.Controls.ExitVirtualKey < 1 || configuration.Controls.ExitVirtualKey > 254)
                errors.Add("Choose a valid exit key.");
            if (string.IsNullOrEmpty(configuration.Controls.PinHash) != string.IsNullOrEmpty(configuration.Controls.PinSalt))
                errors.Add("The adult PIN hash and salt must both be present.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var game in configuration.Games)
            {
                if (game == null) { errors.Add("A game is empty."); continue; }
                if (string.IsNullOrWhiteSpace(game.Id) || !ids.Add(game.Id)) errors.Add("Each game needs a unique ID.");
                var title = string.IsNullOrWhiteSpace(game.Title) ? "Untitled game" : game.Title;
                if (string.IsNullOrWhiteSpace(game.Title)) errors.Add(title + ": enter a title.");
                if (game.Disc == null || game.ProcessNames == null) { errors.Add(title + ": settings are missing."); continue; }
                if (game.StartupGraceMs < 0 || game.StartupGraceMs > 120000 || game.GracefulExitTimeoutMs < 0 || game.GracefulExitTimeoutMs > 120000)
                    errors.Add(title + ": timeouts must be between 0 and 120000 milliseconds.");
                if (game.ProcessNames.Any(n => string.IsNullOrWhiteSpace(n) || n.IndexOfAny(new[] { '\\', '/', ':' }) >= 0))
                    errors.Add(title + ": process names must be executable names, not paths.");
                if (game.Resolution != null && (game.Resolution.Width <= 0 || game.Resolution.Height <= 0 || game.Resolution.BitsPerPixel <= 0 || game.Resolution.Frequency < 0))
                    errors.Add(title + ": invalid resolution.");
                var disc = game.Disc;
                if (disc.Enabled)
                {
                    if (disc.VirtualDriveIndex < 0 || disc.VirtualDriveIndex > 14) errors.Add(title + ": invalid Virtual CloneDrive unit.");
                    if (string.IsNullOrWhiteSpace(disc.ImagePath)) errors.Add(title + ": select a disc image.");
                    var letter = disc.ExpectedDriveLetter ?? "";
                    if (letter.Length != 2 || !char.IsLetter(letter[0]) || letter[1] != ':') errors.Add(title + ": specify the expected drive letter, such as V:.");
                    if (disc.MountTimeoutMs < 100 || disc.MountTimeoutMs > 120000) errors.Add(title + ": invalid mount timeout.");
                    if (!string.IsNullOrEmpty(disc.ReadinessFile) && (Path.IsPathRooted(disc.ReadinessFile) || disc.ReadinessFile.Split('\\', '/').Any(p => p == "..")))
                        errors.Add(title + ": readiness file must be relative to the CD drive.");
                }
                if (!checkFiles) continue;
                try
                {
                    if (!File.Exists(ResolvePath(game.ExecutablePath))) errors.Add(title + ": executable not found.");
                    if (!string.IsNullOrEmpty(game.WorkingDirectory) && !Directory.Exists(ResolvePath(game.WorkingDirectory))) errors.Add(title + ": working directory not found.");
                    if (disc.Enabled && !File.Exists(ResolvePath(disc.ImagePath))) errors.Add(title + ": disc image not found.");
                }
                catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
                { errors.Add(title + ": invalid path: " + e.Message); }
            }
            return errors;
        }
    }
}
