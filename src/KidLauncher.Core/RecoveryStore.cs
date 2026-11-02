using System;
using System.IO;
using System.Web.Script.Serialization;

namespace KidLauncher.Core
{
    internal sealed class RecoveryStore
    {
        private readonly string path;
        public RecoveryStore(string dataDirectory) { path = Path.Combine(dataDirectory, "display-session.json"); }
        public RecoverySession Load()
        {
            if (!File.Exists(path)) return null;
            var session = new JavaScriptSerializer().Deserialize<RecoverySession>(File.ReadAllText(path));
            if (session == null || session.Processes == null || session.OriginalDisplay == null)
                throw new InvalidDataException("Recovery state is invalid. Preserve the file and use the manual recovery instructions.");
            return session;
        }
        public void Save(RecoverySession session)
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                ConfigurationStore.WriteDurable(temp, new JavaScriptSerializer().Serialize(session));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public void Clear() { if (File.Exists(path)) File.Delete(path); }
    }
}
