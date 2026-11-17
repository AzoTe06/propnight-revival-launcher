using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PropnightRevival
{
    /// <summary>
    /// The players' avatars in the game's lobby list and the menu's squad / friend widgets (mod lobby.lua, social.lua,
    /// 2026-11-09). The mod writes
    /// Win64\revival-avatars-request.txt (one name a line) when the names change; the launcher asks the server for
    /// their avatars (GET /revival/social/avatars), saves each picture once as small round PNGs (Win64\revival-avatars\KEY_N.png,
    /// KEY from the picture's address: a new picture, a new key; an animated GIF gives up to 30 frames) and writes
    /// Win64\revival-avatars.txt, a line a name: "name \t key \t frames \t step ms" (key "-": no picture, the initial stays),
    /// then a line a squad among them (2026-11-09): "* \t name \t name...", and a line a flag a player chose to show
    /// (2026-11-10): "! \t name \t code", its picture saved once as Win64\revival-avatars\flag_CODE.png.
    /// </summary>
    sealed class LobbyAvatars
    {
        const int Size = 96, MaxFrames = 30, MinStep = 100, MaxNames = 40;   // the server answers 40 names at most
        const long MaxDownload = 8 << 20;
        static readonly Regex AvatarUrl = new Regex(@"^https://api\.propnightrevival\.online/revival/(social-img/c\d{1,3}|profile-img/[0-9a-f]{24})\.(webp|gif)$");
        static readonly TimeSpan UrlsKept = TimeSpan.FromMinutes(10);
        static readonly Regex FlagCode = new Regex(@"^[a-z]{2}(-[a-z]{3})?$");
        const string FlagUrl = "https://api.propnightrevival.online/revival/social-img/flag/";

        readonly Api api;
        readonly Func<string> win64;   // null: not signed in or no game folder
        readonly Dictionary<string, (string url, DateTime at)> urls = new Dictionary<string, (string, DateTime)>();
        readonly Dictionary<string, (int frames, int step)> made = new Dictionary<string, (int, int)>();
        bool busy, cleaned;

        public LobbyAvatars(Api api, Func<string> win64) { this.api = api; this.win64 = win64; }

        /// <summary>Every second (friends timer): a request from the mod, answered in the background.</summary>
        public void Tick()
        {
            if (busy) return;
            var dir = win64();
            if (dir == null) return;
            var path = Path.Combine(dir, "revival-avatars-request.txt");
            if (!File.Exists(path)) return;
            List<string> names;
            try
            {
                names = File.ReadAllLines(path).Select(Clean).Where(n => n.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxNames).ToList();
                File.Delete(path);
            }
            catch (IOException e) { Log.Write("lobby avatars: " + e.Message); return; }
            if (names.Count == 0) return;
            busy = true;
            Task.Run(async () =>
            {
                try { await Answer(dir, names).ConfigureAwait(false); }
                catch (Exception e) { Log.Write("lobby avatars: " + e.Message); }
                finally { busy = false; }
            });
        }

        /// <summary>The picture addresses already known (players seen in a lobby): the in-game overlay's avatars, no request.</summary>
        public Dictionary<string, string> KnownUrls()
        {
            lock (urls) return urls.Where(kv => kv.Value.url != null).ToDictionary(kv => kv.Key, kv => kv.Value.url);
        }

        static string Clean(string s) => new string((s ?? "").Trim().Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').Take(32).ToArray());

        async Task Answer(string dir, List<string> names)
        {
            var folder = Path.Combine(dir, "revival-avatars");
            Directory.CreateDirectory(folder);
            if (!cleaned) { cleaned = true; CleanUp(folder); }
            // every name asked each time (one request): the squads change; the kept addresses serve when the server is not reached
            Dictionary<string, object> r = null;
            try { r = await api.Avatars(names).ConfigureAwait(false); }
            catch (Exception e)
            {
                if (names.Any(n => !urls.TryGetValue(n.ToLowerInvariant(), out var u) || DateTime.UtcNow - u.at > UrlsKept)) throw;
                Log.Write("lobby avatars: " + e.Message + " (kept pictures used)");
            }
            if (r != null)
            {
                var players = r.Obj("players") ?? new Dictionary<string, object>();
                var found = players.ToDictionary(p => p.Key.ToLowerInvariant(), p => p.Value as string);
                foreach (var n in names)
                    lock (urls) urls[n.ToLowerInvariant()] = (found.TryGetValue(n.ToLowerInvariant(), out var url) && url != null && AvatarUrl.IsMatch(url) ? url : null, DateTime.UtcNow);
            }
            var lines = new StringBuilder();
            foreach (var n in names)
            {
                var url = urls.TryGetValue(n.ToLowerInvariant(), out var u) ? u.url : null;
                var key = url == null ? null : KeyOf(url);
                (int frames, int step) pic = (0, 0);
                if (key != null)
                {
                    try { pic = await Picture(folder, key, url).ConfigureAwait(false); }
                    catch (Exception e) { Log.Write("lobby avatar of " + n + ": " + e.Message); pic = (0, 0); }
                }
                lines.Append(n.ToLowerInvariant()).Append('\t').Append(pic.frames > 0 ? key : "-").Append('\t')
                     .Append(pic.frames).Append('\t').Append(pic.step).Append('\n');
            }
            // the asked players sharing a squad (server 2026-11-09): a line a squad, "*", then its names; an older mod
            // skips these lines (not "name, key, frames, step")
            var asked = new HashSet<string>(names.Select(n => n.ToLowerInvariant()));
            var squads = (r?.Obj("squads") ?? new Dictionary<string, object>())
                .Where(p => p.Value != null && asked.Contains(p.Key.ToLowerInvariant()))
                .GroupBy(p => Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture), p => p.Key.ToLowerInvariant())
                .Where(g => g.Count() > 1);
            foreach (var g in squads) lines.Append('*').Append('\t').Append(string.Join("\t", g)).Append('\n');
            // the flags the asked players chose to show (server 2026-11-10); an older mod skips these lines too
            var flags = 0;
            foreach (var p in r?.Obj("flags") ?? new Dictionary<string, object>())
            {
                var name = p.Key.ToLowerInvariant();
                var code = Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                if (!asked.Contains(name) || !FlagCode.IsMatch(code)) continue;
                var file = Path.Combine(folder, "flag_" + code + ".png");
                if (!File.Exists(file))
                {
                    try
                    {
                        var png = await Download(FlagUrl + code + ".png").ConfigureAwait(false);
                        if (png.Length < 8 || png[0] != 0x89 || png[1] != 0x50) throw new IOException("not a PNG");
                        File.WriteAllBytes(file, png);
                    }
                    catch (Exception e) { Log.Write("lobby flag " + code + ": " + e.Message); continue; }
                }
                lines.Append('!').Append('\t').Append(name).Append('\t').Append(code).Append('\n');
                flags++;
            }
            var index = Path.Combine(dir, "revival-avatars.txt");
            var tmp = index + ".tmp";
            File.WriteAllText(tmp, lines.ToString());
            for (var tries = 0; ; tries++)   // the game may be reading it at that moment
            {
                try { if (File.Exists(index)) File.Replace(tmp, index, null); else File.Move(tmp, index); break; }
                catch (IOException) when (tries < 4) { await Task.Delay(150).ConfigureAwait(false); }
            }
            Log.Write("lobby avatars: " + names.Count + " players, " + lines.ToString().Split('\n').Count(l => l.Length > 0 && l[0] != '*' && !l.Contains("\t-\t")) + " pictures, " + squads.Count() + " squads, " + flags + " flags");
        }

        static string KeyOf(string url)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
                return Hash.Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(url + "#2"))).Substring(0, 16);   // #2: frames made again (30, 100 ms)
        }

        /// <summary>The picture's frames on disk (made once; the sidecar KEY.txt says "frames step").</summary>
        async Task<(int frames, int step)> Picture(string folder, string key, string url)
        {
            if (made.TryGetValue(key, out var known)) return known;
            var side = Path.Combine(folder, key + ".txt");
            if (File.Exists(side) && File.Exists(Path.Combine(folder, key + "_0.png")))
            {
                var parts = File.ReadAllText(side).Trim().Split(' ');
                if (parts.Length == 2 && int.TryParse(parts[0], out var f) && int.TryParse(parts[1], out var s) && f >= 1 && f <= MaxFrames)
                {
                    File.SetLastWriteTimeUtc(side, DateTime.UtcNow);   // kept by the clean-up
                    return made[key] = (f, s);
                }
            }
            // a GIF raw (its frames); anything else as the server's small PNG copy (WebP is not readable here)
            var gif = url.EndsWith(".gif", StringComparison.Ordinal);
            var data = await Download(gif ? url : Regex.Replace(url, @"\.(webp|gif)$", ".png")).ConfigureAwait(false);
            var result = Make(data, gif, folder, key);
            File.WriteAllText(side, result.frames + " " + result.step);
            return made[key] = result;
        }

        /// <summary>Saves the picture's round frames (KEY_N.png): (frames, step ms; 0 when still).</summary>
        static (int frames, int step) Make(byte[] data, bool gif, string folder, string key)
        {
            var result = (frames: 1, step: 0);
            using (var ms = new MemoryStream(data))
            using (var img = Image.FromStream(ms))
            {
                var count = gif ? img.GetFrameCount(FrameDimension.Time) : 1;
                if (count <= 1) { using (var one = Round(img)) one.Save(Path.Combine(folder, key + "_0.png"), ImageFormat.Png); }
                else
                {
                    // the GIF's own timing, sampled every step (at least 100 ms: the mod's 100 ms rounds), 30 frames at most
                    var delays = new int[count];
                    var prop = img.PropertyIdList.Contains(0x5100) ? img.GetPropertyItem(0x5100).Value : null;
                    for (var i = 0; i < count; i++)
                    {
                        var d = prop != null && prop.Length >= (i + 1) * 4 ? BitConverter.ToInt32(prop, i * 4) * 10 : 100;
                        delays[i] = d <= 10 ? 100 : d;   // 0 / 1: browsers show 100 ms
                    }
                    var total = delays.Sum();
                    var step = Math.Max(MinStep, (int)Math.Ceiling(total / (double)MaxFrames));
                    var frames = Math.Max(1, Math.Min(MaxFrames, (int)Math.Round(total / (double)step)));
                    for (var k = 0; k < frames; k++)
                    {
                        int t = k * step, at = 0, idx = 0;
                        while (idx < count - 1 && at + delays[idx] <= t) at += delays[idx++];
                        img.SelectActiveFrame(FrameDimension.Time, idx);
                        using (var frame = Round(img)) frame.Save(Path.Combine(folder, key + "_" + k + ".png"), ImageFormat.Png);
                    }
                    result = (frames, frames > 1 ? step : 0);
                }
            }
            return result;
        }

        static async Task<byte[]> Download(string url)
        {
            using (var resp = await Api.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                if (resp.Content.Headers.ContentLength > MaxDownload) throw new IOException("picture too large");
                var data = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (data.Length > MaxDownload) throw new IOException("picture too large");
                return data;
            }
        }

        /// <summary>The picture cut to fill a square (centre kept), in a disc, transparent around it.</summary>
        static Bitmap Round(Image src)
        {
            var square = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(square))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                var r = Math.Max(Size / (double)src.Width, Size / (double)src.Height);
                float w = (float)(src.Width * r), h = (float)(src.Height * r);
                g.DrawImage(src, (Size - w) / 2, (Size - h) / 2, w, h);
            }
            var disc = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
            using (square)
            using (var brush = new TextureBrush(square))
            using (var g = Graphics.FromImage(disc))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillEllipse(brush, 0, 0, Size, Size);
            }
            return disc;
        }

        /// <summary>Pictures not used for 30 days go (a player who changed theirs leaves the old key behind).</summary>
        static void CleanUp(string folder)
        {
            try
            {
                var old = DateTime.UtcNow.AddDays(-30);
                foreach (var side in Directory.GetFiles(folder, "*.txt"))
                {
                    if (File.GetLastWriteTimeUtc(side) > old) continue;
                    var key = Path.GetFileNameWithoutExtension(side);
                    foreach (var f in Directory.GetFiles(folder, key + "_*.png")) File.Delete(f);
                    File.Delete(side);
                }
            }
            catch (Exception e) { Log.Write("lobby avatars clean-up: " + e.Message); }
        }
    }
}
