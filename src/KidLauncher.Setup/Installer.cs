using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace KidLauncher.Setup
{
    internal static class Installer
    {
        private static readonly HashSet<string> ApplicationFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "KidLauncher.exe", "KidLauncher.exe.config", "KidLauncher.pdb",
            "KidLauncher.Configurator.exe", "KidLauncher.Configurator.exe.config", "KidLauncher.Configurator.pdb",
            "KidLauncher.Core.dll", "KidLauncher.Core.pdb", "README.md", "LICENSE"
        };

        internal static string NormalizeDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory) ||
                directory.Length < 3 || directory[1] != ':' || (directory[2] != '\\' && directory[2] != '/'))
                throw new ArgumentException("The installation directory must be an absolute local drive path.");
            string full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Length <= 3) throw new ArgumentException("Choose a folder, not a drive root.");
            RejectReparsePoints(full);
            return full;
        }

        internal static void Install(string directory)
        {
            RejectReparsePoints(directory);
            CheckRunningApplications(directory);
            using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("KidLauncher.Payload.zip"))
            {
                if (payload == null) throw new InvalidDataException("The embedded portable ZIP is missing.");
                using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
                {
                    var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string relative = ValidateEntry(entry);
                        if (relative.Length == 0) continue;
                        if (!seen.Add(relative)) throw new InvalidDataException("Duplicate ZIP path: " + relative);
                        if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\")) continue;
                        if (ApplicationFiles.Contains(relative) || relative.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) ||
                            relative.Equals("games.json", StringComparison.OrdinalIgnoreCase))
                            files.Add(relative, entry);
                    }
                    foreach (string required in new[] { "KidLauncher.exe", "KidLauncher.Configurator.exe", "KidLauncher.Core.dll", "games.json" })
                        if (!files.ContainsKey(required)) throw new InvalidDataException("Portable ZIP is missing " + required);

                    // Preflight all destinations before writing anything. This is not a multi-file transaction:
                    // a later lock or disk error can still require a retry after some files were updated.
                    foreach (var file in files)
                    {
                        string target = Destination(directory, file.Key);
                        RejectReparsePoints(target);
                        if (Directory.Exists(target)) throw new IOException("A folder occupies the required file path: " + target);
                        if (file.Key.Equals("games.json", StringComparison.OrdinalIgnoreCase) && File.Exists(target)) continue;
                        if (File.Exists(target))
                        {
                            using (File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                        }
                    }
                    Directory.CreateDirectory(directory);
                    foreach (string name in new[] { "Assets", "Disc Images", "Data", "Logs", "Backups" })
                    {
                        string folder = Path.Combine(directory, name);
                        if (!Directory.Exists(folder) && !File.Exists(folder))
                        {
                            RejectReparsePoints(folder);
                            Directory.CreateDirectory(folder);
                        }
                    }
                    foreach (var file in files)
                    {
                        string target = Destination(directory, file.Key);
                        RejectReparsePoints(target);
                        bool initialConfiguration = file.Key.Equals("games.json", StringComparison.OrdinalIgnoreCase);
                        if (initialConfiguration && File.Exists(target)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        string temporary = Path.Combine(Path.GetDirectoryName(target), ".KidLauncher.Setup-" + Guid.NewGuid().ToString("N") + ".tmp");
                        try
                        {
                            using (Stream input = file.Value.Open())
                            using (Stream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                input.CopyTo(output);
                            RejectReparsePoints(target);
                            if (initialConfiguration || !File.Exists(target))
                            {
                                // Move never overwrites a configuration created since the preflight.
                                try { File.Move(temporary, target); }
                                catch (IOException) when (initialConfiguration && File.Exists(target)) { }
                            }
                            else File.Replace(temporary, target, null);
                        }
                        catch (Exception ex)
                        {
                            throw new IOException("Could not install " + target + ". Close programs using this folder and check permissions/free space. Some files may already be updated; retry Setup. " + ex.Message, ex);
                        }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                }
            }
        }

        private static string ValidateEntry(ZipArchiveEntry entry)
        {
            string path = entry.FullName.Replace('\\', '/');
            string[] parts = path.TrimEnd('/').Split('/');
            if (parts.Length == 0 || !parts[0].Equals("KidLauncher", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ZIP entries must be inside KidLauncher/.");
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ") ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.IndexOf(':') >= 0)
                    throw new InvalidDataException("Unsafe ZIP path: " + path);
                string device = part.Split('.')[0].ToUpperInvariant();
                if (device == "CON" || device == "PRN" || device == "AUX" || device == "NUL" ||
                    (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && device[3] >= '1' && device[3] <= '9'))
                    throw new InvalidDataException("Reserved ZIP path: " + path);
            }
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("ZIP links are not supported: " + path);
            return parts.Length == 1 ? "" : string.Join("/", parts, 1, parts.Length - 1);
        }

        private static string Destination(string directory, string relative)
        {
            string target = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ZIP path escapes the installation directory.");
            return target;
        }

        private static void RejectReparsePoints(string path)
        {
            for (string current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Installation paths cannot contain symbolic links or junctions: " + current);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }

        private static void CheckRunningApplications(string directory)
        {
            foreach (string name in new[] { "KidLauncher", "KidLauncher.Configurator" })
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        string executable;
                        try { executable = process.MainModule.FileName; }
                        catch (InvalidOperationException) { continue; } // Process exited during enumeration.
                        catch (Exception ex)
                        {
                            throw new IOException("Cannot check running " + name + ". Close it before installing. " + ex.Message, ex);
                        }
                        if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(executable)), directory, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Close " + name + " running from " + directory + " before installing.");
                    }
                }
            }
        }
    }
}
