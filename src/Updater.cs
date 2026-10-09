using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace PropnightRevival
{
    /// <summary>
    /// Brings the game install to the signed manifest (revival/release/release.py):
    /// only files whose SHA-256 differs are downloaded, each download is checked
    /// against the manifest before it replaces anything, and game files are backed
    /// up once in &lt;game&gt;\revival-backups before they are first replaced.
    /// </summary>
    class Updater
    {
        readonly Settings settings;
        readonly Action<string, double> report;   // status text, progress 0..1 (-1 = indeterminate)
        public Dictionary<string, object> Manifest;

        public Updater(Settings settings, Action<string, double> report) { this.settings = settings; this.report = report; }

        public async Task LoadManifest()
        {
            report("Checking for updates…", -1);
            var data = await Api.Http.GetByteArrayAsync(Config.ManifestUrl + "?t=" + DateTime.UtcNow.Ticks);
            var sig = Convert.FromBase64String((await Api.Http.GetStringAsync(Config.SignatureUrl + "?t=" + DateTime.UtcNow.Ticks)).Trim());
            if (!Hash.VerifyManifest(data, sig)) throw new Exception("The update manifest is not signed by Propnight Revival. Nothing was changed.");
            Manifest = Json.Parse(Encoding.UTF8.GetString(data));
            Log.Write("manifest " + Manifest.Str("version"));
        }

        /// <summary>Version of a newer launcher in the manifest, or null.</summary>
        public string LauncherUpdate()
        {
            var l = Manifest.Obj("launcher");
            return l != null && new Version(l.Str("version")) > new Version(Config.Version) ? l.Str("version") : null;
        }

        /// <summary>Downloads the newer launcher and starts the script that swaps it in once this one exits.</summary>
        public async Task InstallLauncherUpdate()
        {
            var l = Manifest.Obj("launcher");
            var current = Process.GetCurrentProcess().MainModule.FileName;
            var fresh = Path.Combine(Config.DataDir, "PropnightRevival.new.exe");
            await Download(l.Str("sha256"), l.Long("size"), fresh, "launcher " + l.Str("version"));
            var script = Path.Combine(Config.DataDir, "update-launcher.cmd");
            File.WriteAllText(script, string.Join("\r\n", new[] {
                "@echo off", "ping -n 3 127.0.0.1 >nul",
                $"move /y \"{fresh}\" \"{current}\" >nul", $"start \"\" \"{current}\"", "del \"%~f0\"", "" }));
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false });
            Log.Write("launcher updated to " + l.Str("version"));
        }

        /// <summary>Game files that differ from the manifest (nothing is changed).</summary>
        public async Task<List<Dictionary<string, object>>> Plan(string root)
        {
            var files = Manifest.Objs("files").ToList();
            var todo = new List<Dictionary<string, object>>();
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                if (f.Str("dlc") != null && !settings.DlcOn(f.Str("dlc"))) continue;   // optional content turned off
                var path = Local(root, f.Str("path"));
                var size = f.Long("size");
                report($"Checking files ({i + 1}/{files.Count})", (double)i / files.Count);
                if (File.Exists(path) && new FileInfo(path).Length == size)
                {
                    var name = Path.GetFileName(path);
                    var sha = await Task.Run(() => settings.CachedSha256(path, done => report($"Checking {name}", (double)done / size)));
                    if (sha == f.Str("sha256")) continue;
                }
                todo.Add(f);
            }
            settings.Save();
            return todo;
        }

        public async Task UpdateGame(string root, List<Dictionary<string, object>> todo = null)
        {
            todo = todo ?? await Plan(root);
            foreach (var f in todo)
            {
                var path = Local(root, f.Str("path"));
                var tmp = path + ".download";
                await Download(f.Str("sha256"), f.Long("size"), tmp, Path.GetFileName(path));
                Backup(root, f.Str("path"));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                settings.CachedSha256(path);
                Log.Write("updated " + f.Str("path"));
            }
            settings.Save();
            PatchExe(root);
            GameInstall.EnableMods(root, Manifest.Strs("modsEnabled"));
        }

        /// <summary>
        /// Optional content (launcher DLC tab). A pack not chosen yet counts as on when its files are
        /// already on disk (installs from before the DLC tab), off otherwise (the 4 GB maps are not
        /// downloaded unless asked for).
        /// </summary>
        public void DlcDefaults(string root)
        {
            foreach (var pack in Manifest.Objs("dlc"))
            {
                var id = pack.Str("id");
                if (settings.Dlc.ContainsKey(id)) continue;
                var files = Manifest.Objs("files").Where(f => f.Str("dlc") == id).ToList();
                settings.Dlc[id] = GameInstall.IsGameRoot(root) && files.Count > 0 && files.All(f => File.Exists(Local(root, f.Str("path"))));
                Log.Write($"dlc {id}: {(settings.DlcOn(id) ? "on (already installed)" : "off")}");
            }
            settings.Save();
        }

        /// <summary>Deletes the files of an optional pack (they are downloaded again if it is turned back on).</summary>
        public void RemoveDlc(string root, string id)
        {
            foreach (var f in Manifest.Objs("files").Where(x => x.Str("dlc") == id))
            {
                var path = Local(root, f.Str("path"));
                if (File.Exists(path)) { File.Delete(path); Log.Write("removed " + f.Str("path")); }
            }
        }

        /// <summary>
        /// Takes the revival out of the game folder: every file of the manifest is put back from
        /// revival-backups when the launcher replaced it (Propnight.exe, the Steam API), deleted
        /// otherwise; then the mods' folders, the relay agent, the mod's work files and the
        /// backups go. Returns the files that could not be restored (no backup).
        /// </summary>
        public List<string> Uninstall(string root)
        {
            var missing = new List<string>();
            var backups = Path.Combine(root, "revival-backups");
            var paths = Manifest.Objs("files").Select(f => f.Str("path")).ToList();
            var exe = Manifest.Obj("exePatch")?.Str("path");
            if (exe != null) paths.Add(exe);
            foreach (var relative in paths.Distinct())
            {
                var path = Local(root, relative);
                var backup = Path.Combine(backups, relative.Replace('/', '\\'));
                if (File.Exists(backup))
                {
                    File.Copy(backup, path, true);
                    Log.Write("restored " + relative);
                }
                else if (relative == exe || relative.EndsWith("steam_api64.dll", StringComparison.OrdinalIgnoreCase))
                {
                    missing.Add(relative);   // no original copy: Steam's "Verify integrity" puts it back
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                    Log.Write("removed " + relative);
                }
                settings.HashCache.Remove(path);
            }
            var win64 = GameInstall.Win64(root);
            foreach (var dir in new[] { @"Mods\PropnightLocalMap", @"Mods\PropnightLoginBypasser", "RevivalRelay" })
            {
                var full = Path.Combine(win64, dir);
                if (Directory.Exists(full)) Directory.Delete(full, true);
            }
            foreach (var pattern in new[] { "revival-*.txt", "revival-*.jsonl", "UE4SS*.log", "UE4SS_ObjectDump.txt" })
                foreach (var file in Directory.GetFiles(win64, pattern)) File.Delete(file);
            var mods = Path.Combine(win64, "Mods");
            if (Directory.Exists(mods) && !Directory.EnumerateDirectories(mods).Any()) Directory.Delete(mods, true);
            if (missing.Count == 0 && Directory.Exists(backups)) Directory.Delete(backups, true);
            settings.Dlc.Clear();
            settings.Save();
            return missing;
        }

        static string Local(string root, string relative)
        {
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', '\\')));
            if (!full.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Manifest path outside the game folder: " + relative);
            return full;
        }

        void Backup(string root, string relative)
        {
            var path = Local(root, relative);
            var backup = Path.Combine(root, "revival-backups", relative.Replace('/', '\\'));
            if (!File.Exists(path) || File.Exists(backup)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(backup));
            File.Copy(path, backup);
        }

        async Task Download(string sha, long size, string target, string label)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            using (var resp = await Api.Http.GetAsync(Manifest.Str("filesUrl") + sha, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                using (var src = await resp.Content.ReadAsStreamAsync())
                using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var buffer = new byte[1 << 20];
                    long done = 0;
                    var watch = Stopwatch.StartNew();
                    int n;
                    while ((n = await src.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await dst.WriteAsync(buffer, 0, n);
                        done += n;
                        var speed = done / 1e6 / Math.Max(watch.Elapsed.TotalSeconds, 0.1);
                        report($"Downloading {label}: {done / 1e6:0} / {size / 1e6:0} MB ({speed:0.0} MB/s)", (double)done / size);
                    }
                }
            }
            report($"Verifying {label}…", -1);
            var got = await Task.Run(() => Hash.Sha256File(target));
            if (got != sha) { File.Delete(target); throw new Exception($"{label}: download corrupted (checksum mismatch). Try again."); }
        }

        void PatchExe(string root)
        {
            var p = Manifest.Obj("exePatch");
            if (p == null) return;
            var exe = Local(root, p.Str("path"));
            var sha = settings.CachedSha256(exe);
            if (sha == p.Str("patchedSha256")) return;
            if (!p.Strs("acceptedSha256").Contains(sha))
                throw new Exception("Your Propnight.exe is not the latest Steam version. Verify the game files in Steam, then try again.");
            report("Patching Propnight.exe…", -1);
            var bytes = File.ReadAllBytes(exe);
            foreach (var patch in p.Objs("patches"))
            {
                int offset = (int)patch.Long("offset");
                var original = Hash.FromHex(patch.Str("original"));
                var replacement = Hash.FromHex(patch.Str("replacement"));
                var current = bytes.Skip(offset).Take(original.Length).ToArray();
                if (!current.SequenceEqual(original) && !current.SequenceEqual(replacement))
                    throw new Exception("Unexpected bytes in Propnight.exe (" + patch.Str("name") + ").");
                replacement.CopyTo(bytes, offset);
            }
            if (Hash.Sha256(bytes) != p.Str("patchedSha256")) throw new Exception("Propnight.exe patch check failed. Nothing was changed.");
            Backup(root, p.Str("path"));
            File.WriteAllBytes(exe + ".download", bytes);
            File.Delete(exe);
            File.Move(exe + ".download", exe);
            settings.CachedSha256(exe);
            Log.Write("Propnight.exe patched");
        }
    }
}
