using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public string BetaCode;             // test channel: /updates/beta-<code>/ (empty = the public release); since 1.56 the code the server gives the account
        public bool TestVersion;            // Settings › Test version (1.56): on, an account given a test channel plays its build
        public bool LeftBeta;               // back from the test channel: the public launcher is installed even if older
        public bool DiscordActivity = true; // Discord Rich Presence, on by default, Settings switch
        public bool DirectX11;              // start the game with -dx11 (Settings > Game): for a graphics card that crashes in DirectX 12
        public bool MinimizeOnPlay;         // PLAY sends the launcher to the notification area (Settings > Game); off: it stays open
        // the in-game overlay (Shift + F1, Overlay.cs): its notifications, their corner, the session timer in a corner
        public bool OverlayNotifyOnline = true, OverlayNotifyInvites = true, OverlayTimer, OverlayRight, OverlayFps;
        public string Language;             // the launcher's language (Settings > Game): en, fr, ru, es, de; null: Windows' own
        public string NotesSeen;            // newest patch notes entry shown on the home page (NEW until then)
        public string GameBuild;            // Updater.BuildOf the files installed at the last launch (what the game runs)
        // accounts (lower case, comma separated) already shown "add an email" (1.22.3+: a new key, so a tester who saw the
        // first version of it on an older launcher sees this one)
        public string EmailPrompted;
        public Dictionary<string, object> HashCache = new Dictionary<string, object>();
        public Dictionary<string, object> Dlc = new Dictionary<string, object>();   // optional content id -> installed (bool)
        // account (lower case) -> the server's stamp of its bug reports (ms) last shown in Settings › My reports
        public Dictionary<string, object> BugsSeen = new Dictionary<string, object>();
        public Dictionary<string, object> CheatsSeen = new Dictionary<string, object>();   // the same for the cheater reports

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
                s.BetaCode = d.Str("BetaCode");
                // a PC on a test channel before 1.56 (a code typed in Settings) keeps it on
                s.TestVersion = d.ContainsKey("TestVersion") ? d.Bool("TestVersion") : Config.IsBetaCode(s.BetaCode);
                s.LeftBeta = d.Bool("LeftBeta");
                s.GameBuild = d.Str("GameBuild");
                s.EmailPrompted = d.Str("EmailAnnounced");
                s.DirectX11 = d.Bool("DirectX11");
                s.MinimizeOnPlay = d.Bool("MinimizeOnPlay");
                s.Language = d.Str("Language");
                // "DiscordPresence": 1.16.1 saved "DiscordActivity" off for everyone (off by default then): a new key, on again
                s.DiscordActivity = !d.ContainsKey("DiscordPresence") || d.Bool("DiscordPresence");
                if (d.ContainsKey("OverlayNotifyOnline")) s.OverlayNotifyOnline = d.Bool("OverlayNotifyOnline");
                if (d.ContainsKey("OverlayNotifyInvites")) s.OverlayNotifyInvites = d.Bool("OverlayNotifyInvites");
                s.OverlayTimer = d.Bool("OverlayTimer");
                s.OverlayRight = d.Bool("OverlayRight");
                s.OverlayFps = d.Bool("OverlayFps");
                if (d.Obj("HashCache") is Dictionary<string, object> cache) s.HashCache = cache;
                if (d.Obj("Dlc") is Dictionary<string, object> dlc) s.Dlc = dlc;
                if (d.Obj("BugsSeen") is Dictionary<string, object> bugsSeen) s.BugsSeen = bugsSeen;
                if (d.Obj("CheatsSeen") is Dictionary<string, object> cheatsSeen) s.CheatsSeen = cheatsSeen;
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
                ["BetaCode"] = BetaCode, ["TestVersion"] = TestVersion, ["LeftBeta"] = LeftBeta, ["GameBuild"] = GameBuild, ["DiscordPresence"] = DiscordActivity,
                ["EmailAnnounced"] = EmailPrompted, ["BugsSeen"] = BugsSeen, ["CheatsSeen"] = CheatsSeen,
                ["DirectX11"] = DirectX11, ["MinimizeOnPlay"] = MinimizeOnPlay, ["Language"] = Language,
                ["OverlayNotifyOnline"] = OverlayNotifyOnline, ["OverlayNotifyInvites"] = OverlayNotifyInvites, ["OverlayTimer"] = OverlayTimer, ["OverlayRight"] = OverlayRight, ["OverlayFps"] = OverlayFps,
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

        /// <summary>Forgets the cached hashes of the files under a folder (Verify game files: every file is read again).</summary>
        public void ForgetHashes(string root)
        {
            var prefix = System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\";
            foreach (var key in HashCache.Keys.Where(k => System.IO.Path.GetFullPath(k).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                HashCache.Remove(key);
        }

        /// <summary>What the launcher last read of a file (SHA-256 and "size:date"), null when it never read it.</summary>
        public string KnownSha256(string path, out string stamp)
        {
            stamp = null;
            if (!(HashCache.TryGetValue(path, out var v) && v is Dictionary<string, object> hit)) return null;
            stamp = hit.Str("stamp");
            return hit.Str("sha256");
        }

        /// <summary>SHA-256 of a file, reusing the cached value while its size and date are unchanged (fresh: read again).</summary>
        public string CachedSha256(string path, Action<long> progress = null, bool fresh = false)
        {
            var info = new FileInfo(path);
            var stamp = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            if (!fresh && HashCache.TryGetValue(path, out var v) && v is Dictionary<string, object> hit && hit.Str("stamp") == stamp)
                return hit.Str("sha256");
            var sha = Hash.Sha256File(path, progress);
            HashCache[path] = new Dictionary<string, object> { ["stamp"] = stamp, ["sha256"] = sha };
            return sha;
        }
    }
}
