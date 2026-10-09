using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PropnightRevival
{
    /// <summary>%APPDATA%\PropnightRevival\settings.json. The login token is encrypted for the Windows user (DPAPI).</summary>
    class Settings
    {
        public string GameRoot;
        public string Username;
        public string SteamId;
        public string RelayHost;   // revival relay address (server, account view)
        public string ProtectedToken;
        public string SaveOwner;            // Steam ID of the account whose save is on disk
        public Dictionary<string, object> HashCache = new Dictionary<string, object>();
        public Dictionary<string, object> Dlc = new Dictionary<string, object>();   // optional content id -> installed (bool)

        public bool DlcOn(string id) => Dlc.TryGetValue(id, out var v) && v is bool b && b;

        static string FilePath => Path.Combine(Config.DataDir, "settings.json");

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var d = Json.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
                s.GameRoot = d.Str("GameRoot");
                s.Username = d.Str("Username");
                s.SteamId = d.Str("SteamId");
                s.ProtectedToken = d.Str("ProtectedToken");
                s.SaveOwner = d.Str("SaveOwner");
                if (d.Obj("HashCache") is Dictionary<string, object> cache) s.HashCache = cache;
                if (d.Obj("Dlc") is Dictionary<string, object> dlc) s.Dlc = dlc;
            }
            catch (Exception e) { Log.Write("settings unreadable: " + e.Message); }
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(Config.DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, Json.Write(new Dictionary<string, object>
            {
                ["GameRoot"] = GameRoot, ["Username"] = Username, ["SteamId"] = SteamId,
                ["ProtectedToken"] = ProtectedToken, ["SaveOwner"] = SaveOwner, ["HashCache"] = HashCache, ["Dlc"] = Dlc,
            }), Encoding.UTF8);
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
        }

        public string Token
        {
            get
            {
                if (string.IsNullOrEmpty(ProtectedToken)) return null;
                try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedToken), null, DataProtectionScope.CurrentUser)); }
                catch { return null; }
            }
            set => ProtectedToken = value == null ? null
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        }

        /// <summary>SHA-256 of a file, reusing the cached value while its size and date are unchanged.</summary>
        public string CachedSha256(string path, Action<long> progress = null)
        {
            var info = new FileInfo(path);
            var stamp = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            if (HashCache.TryGetValue(path, out var v) && v is Dictionary<string, object> hit && hit.Str("stamp") == stamp)
                return hit.Str("sha256");
            var sha = Hash.Sha256File(path, progress);
            HashCache[path] = new Dictionary<string, object> { ["stamp"] = stamp, ["sha256"] = sha };
            return sha;
        }
    }
}
