using System;
using System.IO;

namespace KidLauncher.Core
{
    public sealed class FileLog : ILog
    {
        private readonly string path;
        private readonly object gate = new object();
        public FileLog(string dataDirectory)
        {
            var directory = Path.Combine(dataDirectory, "Logs");
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "launcher.log");
        }
        public void Write(string message)
        {
            lock (gate)
            {
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    {
                        for (int i = 3; i >= 1; i--)
                        {
                            var old = i == 1 ? path : path + "." + (i - 1);
                            var next = path + "." + i;
                            if (File.Exists(next)) File.Delete(next);
                            if (File.Exists(old)) File.Move(old, next);
                        }
                    }
                    File.AppendAllText(path, DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                { System.Diagnostics.Trace.WriteLine("Log write failed: " + e.Message); }
            }
        }
    }
}
