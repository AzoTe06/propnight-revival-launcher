using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PropnightRevival
{
    /// <summary>
    /// Keeps SaveUserData.sav (coins, unlocks, rank…) in step with the server copy.
    /// Before the game starts the local save is sent (the server checks it) and the
    /// server copy is written back; while the game runs every new save is sent;
    /// when the server refuses one, its own copy is restored once the game closes.
    /// </summary>
    class ProfileSync : IDisposable
    {
        readonly Api api;
        readonly Settings settings;
        readonly Action<string> status;
        FileSystemWatcher watcher;
        Timer debounce;
        string lastSent;
        byte[] restoreAfterExit;

        public ProfileSync(Api api, Settings settings, Action<string> status) { this.api = api; this.settings = settings; this.status = status; }

        static string SavePath => Path.Combine(Config.SaveGames, "SaveUserData.sav");

        static byte[] ReadSave()
        {
            for (int i = 0; i < 10; i++)
            {
                try { return File.Exists(SavePath) ? File.ReadAllBytes(SavePath) : null; }
                catch (IOException) { Thread.Sleep(300); }   // the game is writing it
            }
            return null;
        }

        static void BackupLocal(string reason)
        {
            if (!File.Exists(SavePath)) return;
            var dir = Path.Combine(Config.DataDir, "save-backups");
            Directory.CreateDirectory(dir);
            File.Copy(SavePath, Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{reason}.sav"), true);
            foreach (var old in new DirectoryInfo(dir).GetFiles("*.sav").OrderByDescending(f => f.LastWriteTimeUtc).Skip(20)) old.Delete();
        }

        static void WriteSave(byte[] data)
        {
            Directory.CreateDirectory(Config.SaveGames);
            File.WriteAllBytes(SavePath + ".tmp", data);
            if (File.Exists(SavePath)) File.Delete(SavePath);
            File.Move(SavePath + ".tmp", SavePath);
        }

        public async Task BeforeLaunch()
        {
            status("Syncing your profile…");
            var local = ReadSave();
            var mine = settings.SaveOwner == settings.SteamId;
            var server = await api.GetProfile();
            var serverSave = server.Str("save");
            if (serverSave != null)
            {
                // a starter profile the server just created (new or reset account) replaces the local save as is
                if (local != null && mine && !server.Bool("fresh"))
                {
                    // progress made since the last sync (e.g. the launcher was closed during play): checked by the server
                    var r = await api.PostProfile(local);
                    if (!r.Bool("accepted")) Log.Write("local save refused by the server: " + string.Join(",", r.Strs("alerts")));
                    serverSave = (await api.GetProfile()).Str("save") ?? serverSave;
                }
                var data = Convert.FromBase64String(serverSave);
                if (local == null || !local.SequenceEqual(data))
                {
                    BackupLocal(mine ? "replaced" : "other-account");
                    WriteSave(data);
                }
                lastSent = Hash.Sha256(data);
            }
            else if (local != null)
            {
                // (older servers) no profile on the server: fresh start.
                // The old local save is kept in %APPDATA%\PropnightRevival\save-backups; the game creates a new one.
                BackupLocal("fresh-start");
                File.Delete(SavePath);
                Log.Write("no server profile: starting from a fresh save");
            }
            settings.SaveOwner = settings.SteamId;
            settings.Save();
        }

        public void StartWatching()
        {
            Directory.CreateDirectory(Config.SaveGames);
            debounce = new Timer(_ => { var _ignored = Upload(); }, null, Timeout.Infinite, Timeout.Infinite);
            watcher = new FileSystemWatcher(Config.SaveGames, "SaveUserData.sav") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            FileSystemEventHandler changed = (s, e) => debounce.Change(5000, Timeout.Infinite);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += (s, e) => debounce.Change(5000, Timeout.Infinite);
            watcher.EnableRaisingEvents = true;
        }

        async Task Upload()
        {
            try
            {
                var data = ReadSave();
                if (data == null) return;
                var sha = Hash.Sha256(data);
                if (sha == lastSent) return;
                lastSent = sha;
                var r = await api.PostProfile(data);
                if (r.Bool("accepted")) { restoreAfterExit = null; status("Profile saved on the server"); }
                else
                {
                    restoreAfterExit = Convert.FromBase64String(r.Str("save"));
                    status("Profile change refused by the server (it will be restored)");
                    Log.Write("save refused: " + string.Join(",", r.Strs("alerts")));
                }
            }
            catch (Exception e) { Log.Write("profile upload failed: " + e.Message); }
        }

        public async Task AfterExit()
        {
            watcher?.Dispose();
            debounce?.Dispose();
            await Upload();
            if (restoreAfterExit != null)
            {
                BackupLocal("refused");
                WriteSave(restoreAfterExit);
                lastSent = Hash.Sha256(restoreAfterExit);
                restoreAfterExit = null;
                status("Your profile was restored from the server");
            }
        }

        public void Dispose() { watcher?.Dispose(); debounce?.Dispose(); }
    }
}
