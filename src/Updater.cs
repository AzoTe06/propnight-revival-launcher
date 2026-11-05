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
        /// <summary>
        /// Anti-cheat (2026-10-30): the manifest's files a fresh Plan found changed since the launcher last read them
        /// (edited by hand: a mod script, the UE4SS settings); the update puts them back, the server hears of it.
        /// </summary>
        public readonly List<string> Changed = new List<string>();

        public Updater(Settings settings, Action<string, double> report) { this.settings = settings; this.report = report; }

        /// <summary>
        /// The game files of a manifest, in 12 hex digits: two games play together only with the same build
        /// (the server matches quick play by it, the launcher refuses to join another). Not the manifest's version,
        /// which also changes for a launcher-only release; optional content (DLC) is left out.
        /// </summary>
        public static string BuildOf(Dictionary<string, object> manifest)
        {
            if (manifest == null) return null;
            var files = manifest.Objs("files").Select(f => f.Str("path") + ":" + f.Str("sha256")).OrderBy(x => x, StringComparer.Ordinal);
            return Hash.Sha256(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", files))).Substring(0, 12).ToLowerInvariant();
        }

        public async Task LoadManifest()
        {
            report("Checking for updates…", -1);
            var basis = Config.UpdatesBase(settings.BetaCode);
            var data = await Api.Http.GetByteArrayAsync(basis + "manifest.json?t=" + DateTime.UtcNow.Ticks);
            var sig = Convert.FromBase64String((await Api.Http.GetStringAsync(basis + "manifest.sig?t=" + DateTime.UtcNow.Ticks)).Trim());
            if (!Hash.VerifyManifest(data, sig)) throw new Exception("The update manifest is not signed by Propnight Revival. Nothing was changed.");
            Manifest = Json.Parse(Encoding.UTF8.GetString(data));
            Log.Write("manifest " + Manifest.Str("version"));
        }

        /// <summary>Version of a newer launcher in the manifest, or null.</summary>
        public string LauncherUpdate()
        {
            var l = Manifest.Obj("launcher");
            if (l == null) return null;
            var v = new Version(l.Str("version"));
            // the test channel can hold a newer launcher: back on the public release, its own launcher is installed
            return v > new Version(Config.Version) || (!Config.IsBetaCode(settings.BetaCode) && settings.LeftBeta && v != new Version(Config.Version))
                ? l.Str("version") : null;
        }

        /// <summary>
        /// Downloads the newer launcher and puts it in place of this exe, then starts it (it waits for this one to exit).
        /// A running exe can be renamed: this one becomes "<name>.old" (deleted by the next start), the new one takes its
        /// name. No helper script (1.34.0: the hidden .cmd that waited, moved and deleted itself looked like malware to
        /// Windows Defender's heuristics). A failure leaves this exe as it was and the new one in the data folder (reported
        /// at the next start).
        /// </summary>
        public async Task InstallLauncherUpdate()
        {
            var l = Manifest.Obj("launcher");
            var current = Process.GetCurrentProcess().MainModule.FileName;
            var fresh = Path.Combine(Config.DataDir, "PropnightRevival.new.exe");
            await Download(l.Str("sha256"), l.Long("size"), fresh, "launcher " + l.Str("version"));
            var old = current + ".old";
            try { if (File.Exists(old)) File.Delete(old); }
            catch (Exception) { old = current + "." + DateTime.UtcNow.Ticks + ".old"; }   // an older one still held
            File.Move(current, old);
            try { File.Copy(fresh, current); }
            catch (Exception) { File.Move(old, current); throw; }
            try { File.Delete(fresh); } catch (Exception) { }
            Process.Start(new ProcessStartInfo(current, "--after-update " + Process.GetCurrentProcess().Id) { UseShellExecute = false });
            Log.Write("launcher updated to " + l.Str("version"));
        }

        /// <summary>
        /// Game files that differ from the manifest (nothing is changed). fresh (PLAY): the small files (scripts, UE4SS
        /// settings, DLLs, Blueprint paks: under 64 MB, a fraction of a second) are read again even when their size and
        /// date did not change: an edited file whose date was put back passed (UE4SS-settings.ini: the Live View that
        /// edits any value in the game). The maps' paks keep the cached hash (GB each).
        /// </summary>
        public async Task<List<Dictionary<string, object>>> Plan(string root, bool fresh = false)
        {
            var files = Manifest.Objs("files").ToList();
            var todo = new List<Dictionary<string, object>>();
            Changed.Clear();
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                if (!Wanted(f)) continue;   // optional content turned off
                var path = Local(root, f.Str("path"));
                var size = f.Long("size");
                report($"Checking files ({i + 1}/{files.Count})", (double)i / files.Count);
                var small = fresh && size < 64L << 20;
                string knownStamp = null;
                var known = small ? settings.KnownSha256(path, out knownStamp) : null;
                if (File.Exists(path) && new FileInfo(path).Length == size)
                {
                    var name = Path.GetFileName(path);
                    var sha = await Task.Run(() => settings.CachedSha256(path, done => report($"Checking {name}", (double)done / size), small));
                    if (sha == f.Str("sha256")) continue;
                    if (known != null && sha != known) Changed.Add(f.Str("path"));   // not what the launcher left there
                }
                else if (known != null && File.Exists(path))
                {
                    // another size: an older release's file is fine, as long as it is the one the launcher last read
                    var info = new FileInfo(path);
                    if ($"{info.Length}:{info.LastWriteTimeUtc.Ticks}" != knownStamp) Changed.Add(f.Str("path"));
                }
                todo.Add(f);
            }
            settings.Save();
            return todo;
        }

        public async Task UpdateGame(string root, List<Dictionary<string, object>> todo = null)
        {
            todo = todo ?? await Plan(root);
            // one progress for the whole update (MB of every file, speed, time left): per file, the classic
            // maps' 4 GB pak showed nothing of the rest, and its long name hid the numbers
            var total = todo.Sum(f => f.Long("size"));
            long before = 0;
            var watch = Stopwatch.StartNew();
            foreach (var f in todo)
            {
                var path = Local(root, f.Str("path"));
                var tmp = path + ".download";
                await Download(f.Str("sha256"), f.Long("size"), tmp, Path.GetFileName(path).Replace("Propnight-WindowsNoEditor_", ""),
                               before, total, watch);
                before += f.Long("size");
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
        /// <returns>true when the one classic maps pack of before the split was installed: its files were deleted, the
        /// three yearly packs are off, the launcher tells the player to pick the ones they want (2026-10-30)</returns>
        public bool DlcDefaults(string root)
        {
            var hadClassic = settings.Dlc.ContainsKey("classic-maps") && settings.DlcOn("classic-maps")
                             && Manifest.Objs("dlc").Any(p => (p.Str("id") ?? "").StartsWith("classic-2"));
            foreach (var pack in Manifest.Objs("dlc"))
            {
                var id = pack.Str("id");
                if (settings.Dlc.ContainsKey(id)) continue;
                // the one classic maps pack of before the split: the yearly packs start off (the player picks them)
                if (id.StartsWith("classic-") && settings.Dlc.ContainsKey("classic-maps"))
                {
                    settings.Dlc[id] = false;
                    Log.Write($"dlc {id}: off (the classic maps pack was split)");
                    continue;
                }
                var files = Manifest.Objs("files").Where(f => f.Str("dlc") == id && !f.ContainsKey("dlcAny")).ToList();
                settings.Dlc[id] = GameInstall.IsGameRoot(root) && files.Count > 0 && files.All(f => File.Exists(Local(root, f.Str("path"))));
                Log.Write($"dlc {id}: {(settings.DlcOn(id) ? "on (already installed)" : "off")}");
            }
            if (Manifest.Objs("dlc").Any(p => (p.Str("id") ?? "").StartsWith("classic-2")) && settings.Dlc.Remove("classic-maps") && hadClassic)
            {
                // the old pack's files go now (never loaded with the new ones): the paks the manifest lists as removed, and
                // the common files of the packs (the selector list) while none is on
                RemoveObsolete(root);
                foreach (var pack in Manifest.Objs("dlc").Where(p => (p.Str("id") ?? "").StartsWith("classic-")))
                    RemoveDlc(root, pack.Str("id"));
                settings.Save();
                return true;
            }
            settings.Save();
            return false;
        }

        /// <summary>The packs a manifest file belongs to (any of them installs it): "dlcAny", else "dlc"; none for the base files.</summary>
        public static List<string> PacksOf(Dictionary<string, object> f)
        {
            var any = f.Strs("dlcAny").ToList();
            if (any.Count > 0) return any;
            return f.Str("dlc") != null ? new List<string> { f.Str("dlc") } : new List<string>();
        }

        /// <summary>A manifest file this install wants: a base file, or one of a pack turned on.</summary>
        public bool Wanted(Dictionary<string, object> f)
        {
            var packs = PacksOf(f);
            return packs.Count == 0 || packs.Any(settings.DlcOn);
        }

        /// <summary>Deletes the files of an optional pack (they are downloaded again if it is turned back on), except the
        /// ones another pack still on needs (the classic packs' common files).</summary>
        public void RemoveDlc(string root, string id)
        {
            foreach (var f in Manifest.Objs("files").Where(x => PacksOf(x).Contains(id)))
            {
                if (PacksOf(f).Any(p => p != id && settings.DlcOn(p))) continue;
                var path = Local(root, f.Str("path"));
                if (File.Exists(path)) { File.Delete(path); Log.Write("removed " + f.Str("path")); }
            }
        }

        /// <summary>Files of older releases the manifest lists as removed ("removed": the one classic maps DLC's paks,
        /// replaced by the yearly packs' with the same paths inside: never loaded together). Deleted once the update is
        /// in, or before it when the disk lacks room for both (onlyIfShort). Returns the bytes freed.</summary>
        public long RemoveObsolete(string root, long needed = 0, bool onlyIfShort = false)
        {
            var paths = Manifest.Strs("removed").Select(p => Local(root, p)).Where(File.Exists).ToList();
            if (paths.Count == 0) return 0;
            if (onlyIfShort)
            {
                long free;
                try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))).AvailableFreeSpace; } catch { return 0; }
                if (free > needed + (1L << 30)) return 0;   // room enough: the old files stay until the new ones are in
                Log.Write($"not enough room for the update ({free >> 20} MB free, {needed >> 20} MB needed): older files removed first");
            }
            long freed = 0;
            foreach (var path in paths)
            {
                try { freed += new FileInfo(path).Length; File.Delete(path); Log.Write("removed (no longer in the release) " + path); }
                catch (Exception e) { Log.Write($"could not remove {path}: {e.Message}"); }
            }
            return freed;
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
            foreach (var dir in new[] { @"Mods\PropnightLocalMap", @"Mods\PropnightLoginBypasser", @"Mods\" + GameInstall.AdminMod, "RevivalRelay" })
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

        // the files come from Cloudflare R2, unreachable or throttled in some countries (Russia: the speed drops until the
        // connection dies, 2026-10-14). A download that fails or stalls resumes where it stopped (HTTP Range). R2 stays the
        // source; the API's host mirrors the files (/files/, nginx cache) as a fallback, kept only while it is fast (the
        // user's rule: never a slow VPS download): once R2 failed and the mirror proved fast, the next files start on it.
        static bool r2Failed, mirrorFast;
        const double MirrorMinBytesPerSecond = 10e6 / 8;   // 10 Mbit/s, measured over its first MirrorProbe
        static readonly TimeSpan MirrorProbe = TimeSpan.FromSeconds(8);
        const int Attempts = 10;   // in a row without any progress (a try that got further starts the count again)
        const long StallBytes = 256 * 1024;                          // less than this in StallWindow: stalled
        static readonly TimeSpan StallWindow = TimeSpan.FromSeconds(20);
        static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);   // nothing at all for this long: stalled

        class StalledException : Exception { public StalledException(string m) : base(m) { } }
        class SlowMirrorException : StalledException { public SlowMirrorException(string m) : base(m) { } }

        /// <summary>before / total / watch: the bytes and time of the whole update this file is part of.</summary>
        async Task Download(string sha, long size, string target, string label, long before = 0, long total = 0, Stopwatch watch = null)
        {
            if (total <= 0) total = size;
            watch = watch ?? Stopwatch.StartNew();
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var r2 = Manifest.Str("filesUrl") + sha;
            var mirror = Config.Api + "/files/" + sha;
            long done = 0, shown = -1000;
            var buffer = new byte[1 << 20];
            // what an earlier update left of this same file (its SHA-256 in the .sha beside it) goes on from there: on a
            // connection that keeps dropping, the 4 GB classic maps pak started from 0 at each UPDATE click and never ended
            var shaFile = target + ".sha";
            var resume = File.Exists(target) && File.Exists(shaFile) && File.ReadAllText(shaFile).Trim() == sha;
            if (!resume) File.WriteAllText(shaFile, sha);
            using (var dst = new FileStream(target, resume ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                if (resume && dst.Length <= size) { done = dst.Length; Log.Write($"download {label}: resuming at {done / 1e6:0}/{size / 1e6:0} MB"); }
                else dst.SetLength(0);
                long resumed = done, lastFailAt = -1;
                int failures = 0;
                var useMirror = r2Failed && mirrorFast;
                for (int attempt = 1; done < size; attempt++)
                {
                    var url = useMirror ? mirror : r2;
                    try
                    {
                        using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                        {
                            if (done > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(done, null);
                            using (var resp = await Api.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                            {
                                resp.EnsureSuccessStatusCode();
                                if (done > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                                {
                                    // this source ignored the range: from the start again
                                    dst.SetLength(0);
                                    done = 0;
                                }
                                dst.Position = done;
                                using (var src = await resp.Content.ReadAsStreamAsync())
                                {
                                    var window = Stopwatch.StartNew();
                                    long windowStart = done;
                                    var probe = url == mirror && !mirrorFast ? Stopwatch.StartNew() : null;
                                    long probeStart = done;
                                    while (true)
                                    {
                                        var read = src.ReadAsync(buffer, 0, buffer.Length);
                                        if (await Task.WhenAny(read, Task.Delay(ReadTimeout)) != read)
                                        {
                                            src.Dispose();   // ends the pending read
                                            var _ = read.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                                            throw new StalledException($"nothing received for {ReadTimeout.TotalSeconds:0} s");
                                        }
                                        int n = await read;
                                        if (n == 0) break;
                                        await dst.WriteAsync(buffer, 0, n);
                                        done += n;
                                        if (probe != null && probe.Elapsed > MirrorProbe)
                                        {
                                            var rate = (done - probeStart) / probe.Elapsed.TotalSeconds;
                                            probe = null;
                                            if (rate < MirrorMinBytesPerSecond) throw new SlowMirrorException($"mirror too slow ({rate * 8 / 1e6:0.0} Mbit/s)");
                                            mirrorFast = true;
                                            Log.Write($"downloads: the mirror is fast ({rate * 8 / 1e6:0} Mbit/s), used while R2 fails");
                                        }
                                        if (window.Elapsed > StallWindow)
                                        {
                                            if (done - windowStart < StallBytes)
                                                throw new StalledException($"{(done - windowStart) / 1024} KB in {StallWindow.TotalSeconds:0} s");
                                            window.Restart();
                                            windowStart = done;
                                        }
                                        if (watch.ElapsedMilliseconds - shown < 250 && done < size) continue;   // 4 refreshes a second
                                        shown = watch.ElapsedMilliseconds;
                                        var now = before + done;
                                        var speed = (now - resumed) / 1e6 / Math.Max(watch.Elapsed.TotalSeconds, 0.1);
                                        var left = speed > 0 ? (total - now) / 1e6 / speed : 0;
                                        var eta = left < 60 ? $"{left:0} s left" : $"{left / 60:0} min left";
                                        // the numbers first: the line is cut on the right when it is too long
                                        report($"{now / 1e6:0} / {total / 1e6:0} MB · {speed:0.0} MB/s · {eta} · {label}", (double)now / total);
                                    }
                                }
                            }
                        }
                        if (done < size) throw new IOException($"the connection ended at {done / 1e6:0} of {size / 1e6:0} MB");
                        break;
                    }
                    catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is IOException
                        || e is StalledException || e is ObjectDisposedException)
                    {
                        Log.Write($"download {label} from {(url == mirror ? "the mirror" : "R2")} at {done / 1e6:0}/{size / 1e6:0} MB (try {attempt}): {e.Message}");
                        if (done > lastFailAt) failures = 0;   // this try got further: not stuck
                        lastFailAt = done;
                        if (++failures >= Attempts)
                            throw new Exception($"{label}: the download keeps failing ({e.Message}). Check your connection and press UPDATE again: it goes on where it stopped.");
                        // R2 failed: the mirror for the next try (dropped at once if slow); the mirror failed or was slow: R2 again
                        if (url == r2) { r2Failed = true; useMirror = true; }
                        else { useMirror = false; if (e is SlowMirrorException) mirrorFast = false; }
                        report($"Connection lost, resuming at {(before + done) / 1e6:0} MB… · {label}", (double)(before + done) / total);
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * attempt, 10)));
                    }
                }
            }
            report($"Verifying {label}…", -1);
            var got = await Task.Run(() => Hash.Sha256File(target));
            File.Delete(shaFile);
            if (got != sha) { File.Delete(target); throw new Exception($"{label}: download corrupted (checksum mismatch). Try again."); }
        }

        /// <summary>True when Propnight.exe is not the manifest's patched one yet (it is patched at PLAY, nothing to download).</summary>
        public bool ExePatchPending(string root)
        {
            var p = Manifest.Obj("exePatch");
            if (p == null) return false;
            var exe = Local(root, p.Str("path"));
            return File.Exists(exe) && settings.CachedSha256(exe) != p.Str("patchedSha256");
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
