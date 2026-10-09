using System;
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
        public ApiException(int status, string message) : base(message) { Status = status; }
    }

    /// <summary>Revival server API (see revival/server/src/accounts.mjs).</summary>
    class Api
    {
        public static readonly HttpClient Http;
        static Api()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // TLS 1.2 + 1.3
            Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("PropnightRevivalLauncher/" + Config.Version);
        }

        public string Token;

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
                        throw new ApiException((int)resp.StatusCode, json.Str("error") ?? $"server error {(int)resp.StatusCode}");
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
        public Task<Dictionary<string, object>> Sessions() => Send(HttpMethod.Get, "/revival/sessions");
        public Task<Dictionary<string, object>> GetProfile() => Send(HttpMethod.Get, "/revival/profile");
        public Task<Dictionary<string, object>> PostReport(Dictionary<string, object> report) => Send(HttpMethod.Post, "/revival/report", report);
        /// <summary>Discord sign-in page that links this account (Verified role on the Discord server).</summary>
        public Task<Dictionary<string, object>> DiscordLink() => Send(HttpMethod.Post, "/revival/discord/link", new Dictionary<string, object>());
        public Task<Dictionary<string, object>> PostMatch(Dictionary<string, object> match) => Send(HttpMethod.Post, "/revival/match", match);
        /// <summary>category: game | crash | launcher | other; logs: zip (BugReport.CollectLogs) or null.</summary>
        public Task<Dictionary<string, object>> PostBug(string category, string description, byte[] logs) =>
            Send(HttpMethod.Post, "/revival/bug", new Dictionary<string, object> { ["category"] = category, ["description"] = description,
                ["version"] = Config.Version, ["logs"] = logs == null ? null : Convert.ToBase64String(logs) });
        public Task<Dictionary<string, object>> Friends() => Send(HttpMethod.Get, "/revival/friends");
        /// <summary>request | accept | decline | remove | invite</summary>
        public Task<Dictionary<string, object>> Friend(string action, string username) =>
            Send(HttpMethod.Post, "/revival/friends/" + action, new Dictionary<string, object> { ["username"] = username });
        /// <summary>role: host | client | menu (what the mod reports); server: ip:port of the game joined as a client.</summary>
        public Task<Dictionary<string, object>> Presence(bool game, string role, string server) =>
            Send(HttpMethod.Post, "/revival/presence", new Dictionary<string, object> { ["game"] = game, ["role"] = role, ["server"] = server });
        public Task<Dictionary<string, object>> PostProfile(byte[] save) =>
            Send(HttpMethod.Post, "/revival/profile", new Dictionary<string, object> { ["save"] = Convert.ToBase64String(save) });
    }
}
