using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PropnightRevival
{
    /// <summary>
    /// Keeps the game's save (coins, unlocks, rank…) in step with the server copy.
    /// Before the game starts the local save is sent (the server checks it) and the
    /// server copy is written back; while the game runs every new save is sent;
    /// when the server refuses one, its own copy is written back at once and the mod puts it in the running game
    /// (coins, unlocks: an edited balance lasts seconds, not the session). The mod also gets the real balance after
    /// every answer ("profile COINS REFUSED RECENTGAME" in revival-map-command.txt; RECENTGAME: the server saw the player in a
    /// game lately): its coin guard puts it back as soon as the
    /// game shows far more (Cheat Engine), before anything is bought.
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

        /// <summary>
        /// The game's save slot: GI_Prophunt.LoadCreateUserDataSave names it "SaveUserData" + 6 characters of the
        /// Steam ID from index 11 (account 1 = 76561199900000001 → SaveUserData000001.sav). Before the 1.14.5
        /// launcher this was SaveUserData.sav, which the game stopped using once accounts got their own Steam
        /// IDs (2026-10-08): progress was no longer synced.
        /// </summary>
        string SaveName => "SaveUserData" + (settings.SteamId != null && settings.SteamId.Length >= 17 ? settings.SteamId.Substring(11, 6) : "") + ".sav";
        string SavePath => Path.Combine(Config.SaveGames, SaveName);

        byte[] ReadSave()
        {
            for (int i = 0; i < 10; i++)
            {
                try { return File.Exists(SavePath) ? File.ReadAllBytes(SavePath) : null; }
                catch (IOException) { Thread.Sleep(300); }   // the game is writing it
            }
            return null;
        }

        void BackupLocal(string reason)
        {
            if (!File.Exists(SavePath)) return;
            var dir = Path.Combine(Config.DataDir, "save-backups");
            Directory.CreateDirectory(dir);
            File.Copy(SavePath, Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{reason}.sav"), true);
            foreach (var old in new DirectoryInfo(dir).GetFiles("*.sav").OrderByDescending(f => f.LastWriteTimeUtc).Skip(20)) old.Delete();
        }

        /// <summary>The real balance (server answer's "coins", servers from 2026-10-18) for the mod; refused: reload the save.</summary>
        void TellMod(Dictionary<string, object> answer, bool refused)
        {
            if (!long.TryParse(answer?.Str("coins"), out var coins) && !refused) return;
            try
            {
                // also kept in a file the mod reads until it has a balance (a command line can be lost); deleted at launch
                if (answer?.Str("coins") != null)
                    File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-profile.txt"),
                        $"{coins} {(answer.Bool("recentGame") ? 1 : 0)}");
                // appended on its own line: another command the mod has not read yet stays (the mod reads every line)
                File.AppendAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-map-command.txt"),
                    $"\nprofile {(answer?.Str("coins") != null ? coins.ToString() : "-")} {(refused ? 1 : 0)} {(answer != null && answer.Bool("recentGame") ? 1 : 0)}\n");
            }
            catch (Exception e) { Log.Write("profile command for the mod: " + e.Message); }
        }

        /// <summary>The server's save in the game's slot. Windows may refuse to replace the file (access denied: a read-only
        /// file, or one the game made while run as administrator): the flag is taken off, the file is written over in place
        /// when it cannot be deleted, and only then PLAY stops with what to do (a Russian player, 2026-10-27).</summary>
        void WriteSave(byte[] data)
        {
            Directory.CreateDirectory(Config.SaveGames);
            try
            {
                if (File.Exists(SavePath))
                {
                    var attrs = File.GetAttributes(SavePath);
                    var locked = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;
                    if ((attrs & locked) != 0)
                    {
                        File.SetAttributes(SavePath, attrs & ~locked);
                        Log.Write($"save: {attrs & locked} taken off {SaveName}");
                    }
                }
                try
                {
                    File.WriteAllBytes(SavePath + ".tmp", data);
                    if (File.Exists(SavePath)) File.Delete(SavePath);
                    File.Move(SavePath + ".tmp", SavePath);
                }
                catch (UnauthorizedAccessException e) when (File.Exists(SavePath))
                {
                    // allowed to write the file but not to delete it: over it, in place
                    Log.Write($"save: replacing {SaveName} refused ({e.Message}), written in place");
                    File.WriteAllBytes(SavePath, data);
                    try { File.Delete(SavePath + ".tmp"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (UnauthorizedAccessException e)
            {
                Log.Write($"save: {SaveName} not written: {e.Message}");
                throw new Exception($"Windows refuses to let the launcher write your game save ({SavePath}). Close Propnight if it is still open, " +
                    "then right-click the file › Properties: untick Read-only, and in Security give your Windows account Full control. " +
                    "It happens when the game was once started as administrator: start Steam and the launcher normally.", e);
            }
        }

        public async Task BeforeLaunch()
        {
            status("Syncing your profile…");
            // the last session's balance for the mod's coin guard: only this launch's answer counts
            try { File.Delete(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-profile.txt")); } catch (IOException) { }
            var local = ReadSave();
            var mine = settings.SaveOwner == settings.SteamId;
            // when the local save is sent first, the save written back is the one of the second request
            var willSend = local != null && mine;
            var server = await api.GetProfile(final: !willSend);
            var serverSave = server.Str("save");
            if (serverSave != null)
            {
                // a starter profile the server just created (new or reset account) replaces the local save as is
                if (willSend && !server.Bool("fresh"))
                {
                    // progress made since the last sync (e.g. the launcher was closed during play): checked by the server
                    var r = await api.PostProfile(local);
                    if (!r.Bool("accepted")) Log.Write("local save refused by the server: " + string.Join(",", r.Strs("alerts")));
                    server = await api.GetProfile(final: true);
                    serverSave = server.Str("save") ?? serverSave;
                }
                TellMod(server, false);
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
            watcher = new FileSystemWatcher(Config.SaveGames, SaveName) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            // 2 s after the game's last write (a save is several writes): a refused one is put back quickly
            FileSystemEventHandler changed = (s, e) => debounce.Change(2000, Timeout.Infinite);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += (s, e) => debounce.Change(2000, Timeout.Infinite);
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
                if (r.Bool("accepted")) { restoreAfterExit = null; status("Profile saved on the server"); TellMod(r, false); }
                else
                {
                    var server = Convert.FromBase64String(r.Str("save"));
                    Log.Write("save refused: " + string.Join(",", r.Strs("alerts")));
                    try
                    {
                        // the server copy goes back now, the mod reloads it in the game
                        BackupLocal("refused");
                        WriteSave(server);
                        lastSent = Hash.Sha256(server);
                        restoreAfterExit = null;
                        TellMod(r, true);
                        status("Profile change refused by the server: your server profile was put back");
                    }
                    catch (IOException e)
                    {
                        // the game holds the file: written once it closes, as before
                        Log.Write("server copy not written now (" + e.Message + "): restored when the game closes");
                        restoreAfterExit = server;
                        status("Profile change refused by the server (it will be restored)");
                    }
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
