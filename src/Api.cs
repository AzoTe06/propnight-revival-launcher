using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace PropnightRevival
{
    class ApiException : Exception
    {
        public int Status;
        // a banned account (403 with "banned": {until, reason}): the end of the ban (UTC) and why
        public DateTime? BannedUntil;
        public string BanReason;
        public ApiException(int status, string message) : base(message) { Status = status; }
    }

    /// <summary>Revival server API (see revival/server/src/accounts.mjs).</summary>
    class Api
    {
        public static readonly HttpClient Http;
        static Api()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // TLS 1.2 + 1.3
            // .NET Framework allows 2 connections per server by default: the friends long poll holds one
            // for up to 20 s, and the update check, presence and profile calls (same server) queued behind it
            ServicePointManager.DefaultConnectionLimit = 16;
            Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("PropnightRevivalLauncher/" + Config.Version);
            var machine = MachineId();
            if (machine != null) Http.DefaultRequestHeaders.Add("X-Revival-Machine", machine);
        }

        /// <summary>
        /// This PC, for the bans: a SHA-256 of Windows' install id (MachineGuid), never the id itself. The same on every
        /// launch, nothing written or installed; null when it cannot be read.
        /// </summary>
        static string MachineId()
        {
            try
            {
                using (var hive = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    var guid = key?.GetValue("MachineGuid") as string;
                    if (string.IsNullOrWhiteSpace(guid)) return null;
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes("propnight-revival:" + guid.Trim().ToLowerInvariant())).Select(b => b.ToString("x2")));
                }
            }
            catch { return null; }
        }

        public string Token;
        /// <summary>Any call refused because the account is banned (raised on the calling thread, before the throw).</summary>
        public event Action<ApiException> Banned;

        async Task<Dictionary<string, object>> Send(HttpMethod method, string path, object body = null)
        {
            using (var req = new HttpRequestMessage(method, Config.Api + path))
            {
                if (Token != null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
                if (body != null) req.Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json");
                using (var resp = await Http.SendAsync(req).ConfigureAwait(false))
                {
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Dictionary<string, object> json = null;
                    try { json = string.IsNullOrEmpty(text) ? null : Json.Parse(text); } catch { }
                    if (!resp.IsSuccessStatusCode)
                    {
                        var error = new ApiException((int)resp.StatusCode, json.Str("error") ?? $"server error {(int)resp.StatusCode}");
                        var ban = resp.StatusCode == HttpStatusCode.Forbidden && json != null ? json.Obj("banned") : null;
                        if (ban != null && DateTime.TryParse(ban.Str("until"), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var until))
                        {
                            error.BannedUntil = until;
                            error.BanReason = ban.Str("reason") ?? "";
                            Banned?.Invoke(error);
                        }
                        throw error;
                    }
                    return json;
                }
            }
        }

        public Task<Dictionary<string, object>> Register(string user, string password) =>
            Send(HttpMethod.Post, "/revival/account/register", new Dictionary<string, object> { ["username"] = user, ["password"] = password });
        public Task<Dictionary<string, object>> Login(string user, string password) =>
            Send(HttpMethod.Post, "/revival/account/login", new Dictionary<string, object> { ["username"] = user, ["password"] = password });
        public Task<Dictionary<string, object>> Logout() => Send(HttpMethod.Post, "/revival/account/logout", new Dictionary<string, object>());
        public Task<Dictionary<string, object>> Me() => Send(HttpMethod.Get, "/revival/account/me");
        /// <summary>Servers › Create a game: {ok, map, role}, or {ok: false, near} (a public game near this player is open).</summary>
        public Task<Dictionary<string, object>> HostPublic(string role, List<string> packs) =>
            Send(HttpMethod.Post, "/revival/host/public", new Dictionary<string, object> { ["role"] = role ?? "any", ["classic"] = packs.Count > 0,
                ["classicPacks"] = packs });
        /// <summary>Settings › Progress, once per account: {ok, at} (the profile is maxed out on the server).</summary>
        public Task<Dictionary<string, object>> MaxProgress() => Send(HttpMethod.Post, "/revival/progress/max", new Dictionary<string, object>());
        /// <summary>A new username: once a week at most (the server says when it is possible again).</summary>
        public Task<Dictionary<string, object>> SetUsername(string username) =>
            Send(HttpMethod.Post, "/revival/account/username", new Dictionary<string, object> { ["username"] = username ?? "" });
        // getting an account back (server 2026-10-19): password, confirmed email, a reset link, signing in with Discord
        public Task<Dictionary<string, object>> SetPassword(string current, string password) =>
            Send(HttpMethod.Post, "/revival/account/password", new Dictionary<string, object> { ["current"] = current ?? "", ["password"] = password });
        public Task<Dictionary<string, object>> SetEmail(string email) =>
            Send(HttpMethod.Post, "/revival/account/email", new Dictionary<string, object> { ["email"] = email ?? "" });
        public Task<Dictionary<string, object>> ConfirmEmail(string code) =>
            Send(HttpMethod.Post, "/revival/account/email/confirm", new Dictionary<string, object> { ["code"] = code ?? "" });
        public Task<Dictionary<string, object>> Forgot(string login) =>
            Send(HttpMethod.Post, "/revival/account/forgot", new Dictionary<string, object> { ["login"] = login ?? "" });
        public Task<Dictionary<string, object>> DiscordLogin() => Send(HttpMethod.Post, "/revival/discord/login", new Dictionary<string, object>());
        public Task<Dictionary<string, object>> DiscordLoginPoll(string id) =>
            Send(HttpMethod.Post, "/revival/discord/login/poll", new Dictionary<string, object> { ["id"] = id });
        public Task<Dictionary<string, object>> DiscordLoginRegister(string id, string username) =>
            Send(HttpMethod.Post, "/revival/discord/login/register", new Dictionary<string, object> { ["id"] = id, ["username"] = username ?? "" });
        /// <summary>Asks the team to delete the account (Settings); an admin anonymizes it or refuses. Cancel: while it waits.</summary>
        public Task<Dictionary<string, object>> RequestDeletion() => Send(HttpMethod.Post, "/revival/account/delete-request", new Dictionary<string, object>());
        public Task<Dictionary<string, object>> CancelDeletion() => Send(HttpMethod.Post, "/revival/account/delete-cancel", new Dictionary<string, object>());
        /// <summary>Server status: {maintenance, message} (maintenance mode set from the admin).</summary>
        public Task<Dictionary<string, object>> Status() => Send(HttpMethod.Get, "/revival/status");
        public Task<Dictionary<string, object>> Sessions() => Send(HttpMethod.Get, "/revival/sessions");
        /// <summary>final: the save returned is the one written to disk (an admin edit is then in the player's save).</summary>
        public Task<Dictionary<string, object>> GetProfile(bool final) => Send(HttpMethod.Get, "/revival/profile?final=" + (final ? 1 : 0));
        public Task<Dictionary<string, object>> PostReport(Dictionary<string, object> report) => Send(HttpMethod.Post, "/revival/report", report);
        /// <summary>Discord sign-in page that links this account (Verified role on the Discord server).</summary>
        public Task<Dictionary<string, object>> DiscordLink() => Send(HttpMethod.Post, "/revival/discord/link", new Dictionary<string, object>());
        /// <summary>The published patch notes this account sees (admin › Patch notes): {notes: [the manifest's note format]}.</summary>
        /// <summary>The patch notes this player sees, in this language when translated (English otherwise).</summary>
        public Task<Dictionary<string, object>> PatchNotes(string lang = "en") => Send(HttpMethod.Get, "/revival/patchnotes" + (lang != "en" ? "?lang=" + lang : ""));
        public Task<Dictionary<string, object>> PostMatch(Dictionary<string, object> match) => Send(HttpMethod.Post, "/revival/match", match);
        /// <summary>category: game | crash | launcher | other; logs: zip (BugReport.CollectLogs) or null; clipSizes: the clips that follow
        /// (UploadClip to /revival/report-clip, one per ticket of the answer's tickets);
        /// images: the names of images uploaded before (UploadSuggestionImage), 4 at most.</summary>
        // matchRef: the match it happened in (the match history's ref, m:/d:/h:), null when not in a match;
        // tag: "ai", a report about the AI bots test (1.56: Report a bug › AI testing), null otherwise
        public Task<Dictionary<string, object>> PostBug(string category, string description, byte[] logs, List<long> clipSizes = null, List<string> images = null, string matchRef = null, string tag = null) =>
            Send(HttpMethod.Post, "/revival/bug", new Dictionary<string, object> { ["category"] = category, ["description"] = description,
                ["version"] = Config.Version, ["logs"] = logs == null ? null : Convert.ToBase64String(logs), ["clipSizes"] = Sizes(clipSizes),
                ["images"] = (images ?? new List<string>()).Cast<object>().ToList(), ["ref"] = string.IsNullOrEmpty(matchRef) ? null : matchRef, ["tag"] = tag });
        /// <summary>The logs sent with one of this account's bug reports (event: with one of its messages): {zip (base64)}.</summary>
        public Task<Dictionary<string, object>> BugLogs(long id, long eventId) =>
            Send(HttpMethod.Get, "/revival/bug/logs?id=" + id + (eventId > 0 ? "&event=" + eventId : ""));
        /// <summary>This account's bug reports (Settings › My reports): {reports: [{id, status, label, help, fixedIn, updated, messages…}], stamp}.</summary>
        public Task<Dictionary<string, object>> Bugs() => Send(HttpMethod.Get, "/revival/bugs");
        /// <summary>Details added to one of this account's reports (the team asked, or a fixed bug came back).</summary>
        /// logs: new logs (BugReport.CollectLogs) or null; images: names uploaded before (UploadSuggestionImage); clipSizes: as PostBug.
        public Task<Dictionary<string, object>> BugMessage(long id, string text, byte[] logs = null, List<string> images = null, List<long> clipSizes = null) =>
            Send(HttpMethod.Post, "/revival/bug/message", new Dictionary<string, object> { ["id"] = id, ["text"] = text,
                ["logs"] = logs == null ? null : Convert.ToBase64String(logs), ["images"] = (images ?? new List<string>()).Cast<object>().ToList(), ["clipSizes"] = Sizes(clipSizes) });
        static List<object> Sizes(List<long> sizes) => (sizes ?? new List<long>()).Cast<object>().ToList();
        /// <summary>This account's cheater reports: {reports, stamp, players (names of its recent matches)}.</summary>
        public Task<Dictionary<string, object>> CheatReports() => Send(HttpMethod.Get, "/revival/cheat-reports");
        /// <summary>A cheater report, checked: {ticket} (the report is made when its clip arrives: UploadClip).</summary>
        /// reason: cheating | glitching | toxic | teaming | afk | host_left (Report a player, 1.39+); no clip: the answer has the id.
        public Task<Dictionary<string, object>> PostCheatReport(string reported, string message, long clipSize, List<string> images = null, string matchRef = null, string reason = "cheating") =>
            Send(HttpMethod.Post, "/revival/cheat-report", new Dictionary<string, object> { ["reported"] = reported, ["message"] = message, ["reason"] = reason ?? "cheating",
                ["clipSize"] = clipSize, ["version"] = Config.Version, ["images"] = (images ?? new List<string>()).Cast<object>().ToList(),
                ["ref"] = string.IsNullOrEmpty(matchRef) ? null : matchRef });
        /// clipSizes: the clips that follow (as PostBug).
        public Task<Dictionary<string, object>> CheatMessage(long id, string text, List<string> images = null, List<long> clipSizes = null) =>
            Send(HttpMethod.Post, "/revival/cheat-report/message", new Dictionary<string, object> { ["id"] = id, ["text"] = text,
                ["images"] = (images ?? new List<string>()).Cast<object>().ToList(), ["clipSizes"] = Sizes(clipSizes) });
        // the clip goes raw (8 MB): its own client, a slow connection may need minutes
        static readonly HttpClient UploadHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        /// <summary>The video clip of a checked cheater report (the server checks its size and that it is a video): the report's id.</summary>
        public async Task<string> UploadClip(string ticket, string path, string endpoint = "/revival/cheat-report/clip")
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            var type = ext == ".mov" ? "video/quicktime" : ext == ".webm" || ext == ".mkv" ? "video/webm" : ext == ".avi" ? "video/x-msvideo" : "video/mp4";
            using (var file = System.IO.File.OpenRead(path))
            using (var req = new HttpRequestMessage(HttpMethod.Post, Config.Api + endpoint + "?ticket=" + Uri.EscapeDataString(ticket ?? "")))
            {
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
                req.Headers.UserAgent.ParseAdd("PropnightRevivalLauncher/" + Config.Version);
                req.Content = new StreamContent(file);
                req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
                using (var resp = await UploadHttp.SendAsync(req).ConfigureAwait(false))
                {
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Dictionary<string, object> json = null;
                    try { json = string.IsNullOrEmpty(text) ? null : Json.Parse(text); } catch { }
                    if (resp.IsSuccessStatusCode) return json.Str("id");
                    throw new ApiException((int)resp.StatusCode, json.Str("error") ?? $"server error {(int)resp.StatusCode}");
                }
            }
        }
        /// <summary>Community (suggestions): the page's own calls, passed through (ShellForm checks the path).</summary>
        public Task<Dictionary<string, object>> Call(HttpMethod method, string path, object body) => Send(method, path, body);
        /// <summary>A suggestion's image, raw (the server checks it is an image of 5 MB at most): {name, url}.</summary>
        public async Task<Dictionary<string, object>> UploadSuggestionImage(byte[] data, string type)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, Config.Api + "/revival/suggestion/image"))
            {
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
                req.Headers.UserAgent.ParseAdd("PropnightRevivalLauncher/" + Config.Version);
                req.Content = new ByteArrayContent(data);
                req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type ?? "image/jpeg");
                using (var resp = await UploadHttp.SendAsync(req).ConfigureAwait(false))
                {
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Dictionary<string, object> json = null;
                    try { json = string.IsNullOrEmpty(text) ? null : Json.Parse(text); } catch { }
                    if (resp.IsSuccessStatusCode && json != null) return json;
                    throw new ApiException((int)resp.StatusCode, json.Str("error") ?? $"server error {(int)resp.StatusCode}");
                }
            }
        }
        public Task<Dictionary<string, object>> Friends() => Send(HttpMethod.Get, "/revival/friends");
        /// <summary>Public games (server browser): map, players, phase, roles and ranks.</summary>
        public Task<Dictionary<string, object>> Servers() => Send(HttpMethod.Get, "/revival/servers");
        /// <summary>Whether the game on this relay port still takes newcomers: {ok, reason: started | gone, message}.</summary>
        public Task<Dictionary<string, object>> JoinCheck(int port) =>
            Send(HttpMethod.Post, "/revival/join-check", new Dictionary<string, object> { ["port"] = port });
        /// <summary>Long poll: the list once it differs from etag (the server waits up to 20 s).</summary>
        public Task<Dictionary<string, object>> FriendsWait(string etag) =>
            Send(HttpMethod.Post, "/revival/friends/wait", new Dictionary<string, object> { ["etag"] = etag });
        /// <summary>Friend request by friend code (XXXX-XXXX): usernames are not enough to add someone.</summary>
        public Task<Dictionary<string, object>> FriendRequest(string code) =>
            Send(HttpMethod.Post, "/revival/friends/request", new Dictionary<string, object> { ["code"] = code });
        /// <summary>decline-all</summary>
        public Task<Dictionary<string, object>> FriendsPost(string path) => Send(HttpMethod.Post, "/revival/friends/" + path, new Dictionary<string, object>());
        /// <summary>accept | decline | remove | invite (someone already in your lists)</summary>
        public Task<Dictionary<string, object>> Friend(string action, string username) =>
            Send(HttpMethod.Post, "/revival/friends/" + action, new Dictionary<string, object> { ["username"] = username });
        /// <summary>Squad: invite | accept | decline | kick (username), leave (none).</summary>
        public Task<Dictionary<string, object>> Squad(string action, string username) =>
            Send(HttpMethod.Post, "/revival/squad/" + action, new Dictionary<string, object> { ["username"] = username ?? "" });
        /// <summary>role: host | client | menu (what the mod reports); server: ip:port of the game joined as a client;
        /// channel: public | beta | diag (a server before 2026-10-13 reads anything but beta as public);
        /// classicMaps: the Classic maps installed (DLC tab): a squad leader's game keeps them off when a member has not;
        /// pings: round trip to each region's relay in ms (the server's choice of relay and quick play), null before measured;
        /// launched: the running game is the one PLAY started (server: the launch guard lets only such games host and join).</summary>
        /// <summary>The mod's anti-cheat summary of this match (revival-watch.txt), as it is.</summary>
        public Task<Dictionary<string, object>> Watch(string summary) =>
            Send(HttpMethod.Post, "/revival/watch", new Dictionary<string, object> { ["watch"] = summary });
        /// <summary>Game files found changed on this PC (anti-cheat): stage "repaired" (put back at PLAY) or "locked" (changed after the
        /// check: the game was not started).</summary>
        /// <summary>The host left the match this player was in: what they got ({result: null} until the server settled it).</summary>
        public Task<Dictionary<string, object>> HostLeft() => Send(HttpMethod.Get, "/revival/hostleft", null);
        /// <summary>This player, hosting, left a match in progress: {warning: {game, count}} once (the game warns them).</summary>
        public Task<Dictionary<string, object>> HostWarning() => Send(HttpMethod.Get, "/revival/hostleft/warning");
        public Task<Dictionary<string, object>> HostWarned(string game) =>
            Send(HttpMethod.Post, "/revival/hostleft/warned", new Dictionary<string, object> { ["game"] = game });
        public Task<Dictionary<string, object>> HostLeftSeen(string game) =>
            Send(HttpMethod.Post, "/revival/hostleft/seen", new Dictionary<string, object> { ["game"] = game });
        /// <summary>A cheat tool open on this PC (anti-cheat): stage "play" (the game was not started) or "game" (opened during one).</summary>
        public Task<Dictionary<string, object>> Tool(List<string> tools, string stage) =>
            Send(HttpMethod.Post, "/revival/tool", new Dictionary<string, object> { ["tools"] = tools, ["stage"] = stage });
        /// <summary>A file proof ticket for the game about to start (server proof.mjs): the game's RevivalGuard.dll uses it.</summary>
        public Task<Dictionary<string, object>> ProofTicket(string build) =>
            Send(HttpMethod.Post, "/revival/proof/ticket", new Dictionary<string, object> { ["build"] = build ?? "" });
        // 2026-11-04: the launcher asks the challenge and posts the game's answer (RevivalGuard.dll has no network any more)
        /// <summary>{nonce, files}: the nonce goes to the game, which hashes its scripts with it.</summary>
        public Task<Dictionary<string, object>> ProofChallenge(string ticket) =>
            Send(HttpMethod.Post, "/revival/proof/challenge", new Dictionary<string, object> { ["ticket"] = ticket });
        /// <summary>The game's answer (its files, scripts, mods) with the ticket: {ok, want} (the scripts to send).</summary>
        public Task<Dictionary<string, object>> ProofAnswer(string ticket, Dictionary<string, object> answer)
        {
            var body = new Dictionary<string, object>(answer) { ["ticket"] = ticket };
            return Send(HttpMethod.Post, "/revival/proof/answer", body);
        }
        public Task<Dictionary<string, object>> ProofContent(string ticket, Dictionary<string, object> files) =>
            Send(HttpMethod.Post, "/revival/proof/content", new Dictionary<string, object> { ["ticket"] = ticket, ["files"] = files });
        /// <summary>The signed token of this PC's test channel (server channel.mjs), for its flag file: {token, channel}.</summary>
        public Task<Dictionary<string, object>> ChannelToken(string code) =>
            Send(HttpMethod.Post, "/revival/channel/token", new Dictionary<string, object> { ["code"] = code ?? "" });
        public Task<Dictionary<string, object>> Tamper(List<string> files, string stage) =>
            Send(HttpMethod.Post, "/revival/tamper", new Dictionary<string, object> { ["files"] = files, ["stage"] = stage });

        public Task<Dictionary<string, object>> Presence(bool game, bool launched, string role, string server, string build, string channel,
            List<string> classicPacks, Dictionary<string, int> pings) =>
            Send(HttpMethod.Post, "/revival/presence", new Dictionary<string, object> { ["game"] = game, ["launched"] = launched, ["role"] = role,
                ["server"] = server, ["build"] = build,
                // classicMaps: any classic map pack (servers before the split, 2026-10-30); classicPacks: which ones
                ["channel"] = channel, ["classicMaps"] = classicPacks.Count > 0, ["classicPacks"] = classicPacks, ["pings"] = pings,
                // what this launcher handles (server: friends.mjs featuresOf): the quick play offer of a far game; "watch": it
                // sends the mod's anti-cheat summary (a game it started that sends none is a silent witness, watch.mjs)
                // "resume": it passes on the match resume orders (1.56.2+, ShellForm resumeOrder)
                ["features"] = new[] { "qpoffer", "watch", "resume" } });
        /// <summary>The answer to a quick play offer (server: app.mjs offers): join the far game, or host one.</summary>
        public Task<Dictionary<string, object>> QuickPlayChoice(string offer, bool join) =>
            Send(HttpMethod.Post, "/revival/quickplay/choice", new Dictionary<string, object> { ["offer"] = offer, ["choice"] = join ? "join" : "host" });
        /// <summary>The player confirmed a warning on their account ("I understand"): the launcher stops showing its window.</summary>
        public Task<Dictionary<string, object>> WarningSeen(long id) =>
            Send(HttpMethod.Post, "/revival/warning/seen", new Dictionary<string, object> { ["id"] = id });
        /// <summary>A code the player typed in the game's menu (Customization › Redeem code), checked by the server.</summary>
        public Task<Dictionary<string, object>> Redeem(string code) =>
            Send(HttpMethod.Post, "/revival/redeem", new Dictionary<string, object> { ["code"] = code });
        /// <summary>How the player left a match (the admin's Leavers page): menu | network (the mod), closed | crash (the game ended in one).</summary>
        public Task<Dictionary<string, object>> ReportLeave(string reason, string role) =>
            Send(HttpMethod.Post, "/revival/leave", new Dictionary<string, object> { ["reason"] = reason, ["role"] = role });
        public Task<Dictionary<string, object>> PostProfile(byte[] save) =>
            Send(HttpMethod.Post, "/revival/profile", new Dictionary<string, object> { ["save"] = Convert.ToBase64String(save) });
    }
}
