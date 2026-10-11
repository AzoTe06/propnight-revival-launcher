using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PropnightRevival
{
    /// <summary>
    /// Borderless window hosting the HTML interface (ui/index.html, embedded) in WebView2.
    /// The page sends commands ({cmd: ...}); the window answers with state updates and toasts.
    /// </summary>
    class ShellForm : Form
    {
        readonly Settings settings = Settings.Load();
        readonly Api api = new Api();
        readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(7, 7, 12) };
        readonly Timer serverTimer = new Timer { Interval = 30_000 };
        // a launcher left open learns about a new release within a minute (small signed manifest, quiet check)
        readonly Timer updateTimer = new Timer { Interval = 60_000 };
        // presence: the mod's file is read every second and sent when it changes (or every 30 s)
        readonly Timer friendsTimer = new Timer { Interval = 1_000 };
        readonly HashSet<string> seenInvites = new HashSet<string>();
        // SERVERS tab: the public games, fetched every 5 s while the tab is shown
        readonly Timer serversTimer = new Timer { Interval = 5_000 };
        string lastHostOrder;
        DateTime lastPresence = DateTime.MinValue;
        string lastPresenceKey, lastFriendsKey;
        FormWindowState lastWindowState = FormWindowState.Normal;
        int? pendingJoinPort;
        bool refreshingFriends, pushingPresence;
        string friendsEtag;
        string addressProof; int addressProofPort; DateTime addressProofSent;
        readonly HashSet<string> seenSquadInvites = new HashSet<string>();
        string followSession; DateTime followAt; int followTries; string lastSocial;
        readonly HashSet<string> reachedSessions = new HashSet<string>();   // leader's games already joined (or left)
        int friendsLoop;   // generation of the friends long poll (a new one stops the old)
        static readonly Dictionary<string, int> HitTests = new Dictionary<string, int> {
            ["left"] = 10, ["right"] = 11, ["top"] = 12, ["topleft"] = 13, ["topright"] = 14, ["bottom"] = 15, ["bottomleft"] = 16, ["bottomright"] = 17 };
        Updater updater;
        List<Dictionary<string, object>> gamePlan;
        string launcherUpdate, manifestVersion;
        bool busy, playing, signedIn, closeConfirmed;
        string discordName;   // linked Discord account (server), null when not linked

        public ShellForm()
        {
            Text = "Propnight Revival";
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(7, 7, 12);
            Controls.Add(web);
            serverTimer.Tick += async (s, e) => await RefreshServer();
            updateTimer.Tick += async (s, e) => { if (signedIn && !busy && !playing) await CheckUpdates(quiet: true); };
            friendsTimer.Tick += async (s, e) => { await PushPresence(); await ModSocialActions(); };
            serversTimer.Tick += async (s, e) => await RefreshServers();
            Load += async (s, e) => await InitWebView();
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            EventHandler maxState = (s, e) => { if (web.CoreWebView2 != null && WindowState != FormWindowState.Minimized) State(new Dictionary<string, object> { ["maximized"] = WindowState == FormWindowState.Maximized }); };
            Resize += maxState;
            SizeChanged += maxState;
            Resize += (s, e) =>
            {
                if (lastWindowState == FormWindowState.Minimized && WindowState != FormWindowState.Minimized) Restored();
                lastWindowState = WindowState;
            };
        }

        // ---- window: rounded corners + shadow on a borderless window
        protected override CreateParams CreateParams
        {
            // CS_DROPSHADOW; WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_THICKFRAME-less sizing (done through the page's grips)
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; cp.Style |= 0x00020000 | 0x00010000; return cp; }
        }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }   // DWMWA_WINDOW_CORNER_PREFERENCE (Windows 11)
            try { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }    // dark mode frame
            // 1180x720 layout pixels at the monitor's scaling (WebView2 zooms the page the same way)
            var scale = DeviceDpi / 96f;
            var size = new Size((int)(1180 * scale), (int)(720 * scale));
            MinimumSize = new Size((int)(960 * scale), (int)(600 * scale));
            MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;   // maximize without covering the taskbar
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Bounds = new Rectangle(area.Left + Math.Max(0, (area.Width - size.Width) / 2), area.Top + Math.Max(0, (area.Height - size.Height) / 2),
                Math.Min(size.Width, area.Width), Math.Min(size.Height, area.Height));
        }

        async Task InitWebView()
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Config.DataDir, "WebView2"));
                await web.EnsureCoreWebView2Async(env);
            }
            catch (Exception e)
            {
                Log.Write("WebView2 unavailable: " + e.Message);
                MessageBox.Show("This launcher needs the Microsoft Edge WebView2 Runtime (included in Windows 10/11 updates).\n\n" +
                    "Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and start the launcher again.", "Propnight Revival");
                Close();
                return;
            }
            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.NewWindowRequested += (s, e) => e.Handled = true;
            core.NavigationStarting += (s, e) => { if (!e.Uri.StartsWith("data:") && e.Uri != "about:blank") e.Cancel = true; };
            core.WebMessageReceived += async (s, e) => await OnMessage(Json.Parse(e.WebMessageAsJson));
            web.NavigateToString(ReadUi());
            // development aid: PROPNIGHT_LAUNCHER_SCREENSHOT=<png path> captures the page after a few seconds
            var shot = Environment.GetEnvironmentVariable("PROPNIGHT_LAUNCHER_SCREENSHOT");
            if (!string.IsNullOrEmpty(shot))
            {
                await Task.Delay(6000);
                var script = Environment.GetEnvironmentVariable("PROPNIGHT_LAUNCHER_SCRIPT");   // e.g. render({...}); toast(...)
                if (!string.IsNullOrEmpty(script)) { await core.ExecuteScriptAsync(script); await Task.Delay(700); }
                using (var file = File.Create(shot)) await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                Log.Write($"screenshot: client {ClientSize.Width}x{ClientSize.Height}, dpi {DeviceDpi}, zoom {web.ZoomFactor}, rasterization {core.Environment?.BrowserVersionString}");
            }
        }

        /// <summary>Another launcher was started: this one comes to the front instead (Program.RunUi).</summary>
        public void ShowFromOtherInstance()
        {
            if (WindowState == FormWindowState.Minimized) { WindowState = FormWindowState.Normal; Restored(); }
            Show();
            TopMost = true; TopMost = false;   // in front of the window that started the second launcher
            Activate();
            Toast("info", "Already open", "The launcher was already running: here it is.", 3500);
        }

        static string ReadUi()
        {
            using (var stream = typeof(ShellForm).Assembly.GetManifestResourceStream("PropnightRevival.ui.index.html"))
            using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
        }

        // ---- page -> launcher
        async Task OnMessage(Dictionary<string, object> m)
        {
            try
            {
                switch (m.Str("cmd"))
                {
                    case "ready": await Startup(); break;
                    case "drag": ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); break;   // WM_NCLBUTTONDOWN, HTCAPTION
                    case "minimize": WindowState = FormWindowState.Minimized; break;
                    case "maximize": WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
                    case "resize":
                        if (WindowState == FormWindowState.Normal && HitTests.TryGetValue(m.Str("edge") ?? "", out var hit))
                        { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)hit, IntPtr.Zero); }   // WM_NCLBUTTONDOWN on a sizing border
                        break;
                    case "close": Close(); break;
                    case "closeAnyway": closeConfirmed = true; Close(); break;
                    case "login": case "register": await SignIn(m.Str("cmd") == "register", m.Str("username"), m.Str("password")); break;
                    case "logout": await SignOut(); break;
                    case "pickFolder": PickFolder(); break;
                    case "openData": Directory.CreateDirectory(Config.DataDir); Process.Start("explorer.exe", Config.DataDir); break;
                    case "updateLauncher": await UpdateLauncher(); break;
                    case "play": await Play(); break;
                    case "serversTab":
                        if (m.Bool("on")) { serversTimer.Start(); await RefreshServers(); } else serversTimer.Stop();
                        break;
                    case "friend": await FriendAction(m.Str("action"), m.Str("username"), m.Str("code")); break;
                    case "join": await JoinFriend((int)m.Long("port"), m.Str("from"), m.Bool("dlc"), m.Str("map")); break;
                    case "pageError": Log.Write("page error: " + m.Str("message")); break;
                    case "dlc": await SetDlc(m.Str("id"), m.Bool("on")); break;
                    case "uninstall": await UninstallRevival(); break;
                    case "discordLink": await LinkDiscord(); break;
                    case "bug": await SendBug(m.Str("category"), m.Str("text"), m.Bool("logs")); break;
                }
            }
            catch (Exception e)
            {
                Log.Write("error: " + e);
                Toast("bad", "Something went wrong", e.Message);
            }
        }

        // ---- launcher -> page
        void State(Dictionary<string, object> s)
        {
            if (!s.ContainsKey("type")) s["type"] = "state";
            if (InvokeRequired) BeginInvoke((Action)(() => web.CoreWebView2?.PostWebMessageAsJson(Json.Write(s))));
            else web.CoreWebView2?.PostWebMessageAsJson(Json.Write(s));
        }
        void Toast(string level, string title, string text = "", int ms = 5200, Dictionary<string, object> action = null) =>
            State(new Dictionary<string, object> { ["type"] = "toast", ["level"] = level, ["title"] = title, ["text"] = text, ["ms"] = ms, ["action"] = action });
        void PlayButton(string kind, string title, string sub, double? progress = null, bool busy = false, bool disabled = false) =>
            State(new Dictionary<string, object> { ["play"] = new Dictionary<string, object> {
                ["kind"] = kind, ["title"] = title, ["sub"] = sub, ["progress"] = progress, ["busy"] = busy, ["disabled"] = disabled } });

        void ShowIdleButton()
        {
            if (playing) return;
            if (!GameInstall.IsGameRoot(settings.GameRoot)) PlayButton("play", "CHOOSE FOLDER", "Propnight was not found. Pick steamapps\\common\\Propnight in Settings.", null, false, false);
            else if (launcherUpdate != null) PlayButton("launcher", "UPDATE REQUIRED", $"Launcher {launcherUpdate} is required to play. Use the banner above.", null, false, true);
            else if (gamePlan == null) PlayButton("play", "PLAY", "Checking for updates…", -1, true, true);
            else if (gamePlan.Count > 0)
            {
                var mb = gamePlan.Sum(f => f.Long("size")) / 1e6;
                PlayButton("update", "UPDATE", $"{gamePlan.Count} file{(gamePlan.Count == 1 ? "" : "s")} to download ({mb:0.#} MB), then the game starts.");
            }
            else PlayButton("play", "PLAY", "Everything is up to date.");
        }

        void SendUpdateState(bool checking = false, bool error = false)
        {
            State(new Dictionary<string, object> { ["update"] = new Dictionary<string, object> {
                ["checking"] = checking, ["error"] = error, ["launcher"] = launcherUpdate, ["version"] = manifestVersion,
                ["gameBytes"] = gamePlan?.Sum(f => f.Long("size")) ?? 0 } });
        }

        // ---- flow
        /// <summary>
        /// Background art: the game's Steam library images from the player's own Steam cache when present
        /// (otherwise the page loads them from Steam's CDN). Nothing of the game's art ships with the launcher.
        /// </summary>
        static Dictionary<string, object> LocalArt()
        {
            var art = new Dictionary<string, object>();
            try
            {
                var steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                var cache = steam == null ? null : Path.Combine(steam.Replace('/', '\\'), "appcache", "librarycache");
                if (cache == null || !Directory.Exists(cache)) return art;
                string Find(params string[] names) => names.SelectMany(n =>
                        Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories)
                            .Where(f => f.IndexOf("1549180", StringComparison.Ordinal) >= 0 && Path.GetFileName(f).Equals(n, StringComparison.OrdinalIgnoreCase)))
                    .FirstOrDefault()
                    ?? Directory.EnumerateFiles(cache, "1549180_" + names[0]).FirstOrDefault();
                string DataUri(string path, string mime) => path == null ? null : $"data:{mime};base64," + Convert.ToBase64String(File.ReadAllBytes(path));
                var hero = Find("library_hero.jpg");
                var logo = Find("logo.png");
                if (hero != null) art["art"] = DataUri(hero, "image/jpeg");
                if (logo != null) art["logo"] = DataUri(logo, "image/png");
            }
            catch (Exception e) { Log.Write("steam art: " + e.Message); }
            return art;
        }

        async Task Startup()
        {
            State(new Dictionary<string, object> { ["launcherVersion"] = Config.Version, ["splash"] = "Signing in…", ["maximized"] = WindowState == FormWindowState.Maximized });
            var art = LocalArt();
            if (art.Count > 0) State(art);
            serverTimer.Start();
            var server = RefreshServer();
            if (!GameInstall.IsGameRoot(settings.GameRoot)) { settings.GameRoot = GameInstall.Detect(); settings.Save(); }
            api.Token = settings.Token;
            if (api.Token == null) { ShowAuth(""); await server; return; }
            try
            {
                var me = await api.Me();
                Remember(me);
                await EnterHome();
            }
            catch (ApiException e) when (e.Status == 401 || e.Status == 403)
            {
                settings.Token = null; settings.Save(); api.Token = null;
                ShowAuth(e.Status == 403 ? e.Message : "Your session expired, please sign in again.");
            }
            catch (Exception e) { ShowAuth("Server unreachable: " + e.Message); }
            await server;
        }

        void Remember(Dictionary<string, object> account)
        {
            settings.Username = account.Str("username");
            settings.SteamId = account.Str("steamId");
            if (account.ContainsKey("discord")) discordName = account.Str("discord");
            var relay = account.Str("relayHost");
            if (relay != null && System.Text.RegularExpressions.Regex.IsMatch(relay, @"^\d{1,3}(\.\d{1,3}){3}$")) settings.RelayHost = relay;
            settings.Save();
        }

        void ShowAuth(string error)
        {
            signedIn = false;
            State(new Dictionary<string, object> { ["view"] = "auth", ["authError"] = error, ["username"] = settings.Username ?? "" });
        }

        async Task EnterHome()
        {
            signedIn = true;
            State(new Dictionary<string, object> { ["view"] = "home", ["username"] = settings.Username, ["steamId"] = settings.SteamId, ["gameRoot"] = settings.GameRoot ?? "",
                ["discord"] = discordName ?? "" });
            ShowIdleButton();
            updateTimer.Start();
            friendsTimer.Start();
            var _ = RefreshFriends();
            var _loop = FriendsLoop(++friendsLoop);
            var __ = RefreshDiscord();
            await CheckUpdates(quiet: false);
        }

        // ---- Discord (Settings): the server sends back a Discord sign-in page; once accepted the
        // bot gives the Verified role. The launcher watches the account until the link shows up.
        async Task RefreshDiscord()
        {
            try
            {
                var me = await api.Me();
                discordName = me.Str("discord");
                State(new Dictionary<string, object> { ["discord"] = discordName ?? "" });
            }
            catch (Exception e) { Log.Write("account refresh failed: " + e.Message); }
        }

        bool linkingDiscord;
        async Task LinkDiscord()
        {
            if (linkingDiscord) return;
            linkingDiscord = true;
            try
            {
                var url = (await api.DiscordLink()).Str("url");
                if (url == null || !url.StartsWith("https://discord.com/")) throw new Exception("unexpected answer from the server");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                Toast("info", "Link Discord", "Accept in your browser, then come back here.", 9000);
                var before = discordName;
                for (var i = 0; i < 60; i++)   // 5 minutes
                {
                    await Task.Delay(5000);
                    var me = await api.Me();
                    var now = me.Str("discord");
                    if (now != null && now != before)
                    {
                        discordName = now;
                        State(new Dictionary<string, object> { ["discord"] = now });
                        Toast("good", "Discord linked", $"{now} has the Verified role on the Propnight Revival Discord.", 9000);
                        return;
                    }
                }
            }
            catch (ApiException e) { Toast("bad", "Discord", e.Message, 8000); }
            catch (Exception e) { Log.Write("discord link failed: " + e.Message); Toast("bad", "Discord", "Could not start the link. Try again later.", 8000); }
            finally { linkingDiscord = false; }
        }

        // ---- friends (server: revival/server/src/friends.mjs)
        // The list comes from a long poll (POST /revival/friends/wait): the server answers as soon as
        // something changes (a friend hosting or joining, an invite), so it shows within a second.

        /// <summary>Sends where the game is (mod's presence file) when it changes, and every 30 s.</summary>
        async Task PushPresence(bool force = false)
        {
            if (!signedIn || pushingPresence) return;
            pushingPresence = true;
            try
            {
                var inGame = playing || GameInstall.IsRunning();
                var where = inGame && GameInstall.IsGameRoot(settings.GameRoot) ? GameInstall.Presence(settings.GameRoot) : new Dictionary<string, string>();
                where.TryGetValue("role", out var role);
                where.TryGetValue("server", out var server);
                role = role ?? "menu";
                var presenceKey = $"{inGame}|{role}|{server}";
                if (force || presenceKey != lastPresenceKey || DateTime.UtcNow - lastPresence > TimeSpan.FromSeconds(30))
                {
                    await api.Presence(inGame, role, role == "client" ? server : null);
                    lastPresence = DateTime.UtcNow;
                    lastPresenceKey = presenceKey;
                }
            }
            catch (ApiException e) when (e.Status == 401 || e.Status == 403) { }
            catch (Exception e) { Log.Write("presence: " + e.Message); }
            finally { pushingPresence = false; }
        }

        async Task RefreshServers()
        {
            if (!signedIn) return;
            try { State(new Dictionary<string, object> { ["servers"] = (await api.Servers()).Objs("servers").ToList() }); }
            catch (Exception e) { Log.Write("servers: " + e.Message); }
        }

        /// <summary>Fetches the list at once (after an action, back from minimized).</summary>
        async Task RefreshFriends()
        {
            if (!signedIn || refreshingFriends) return;
            refreshingFriends = true;
            try
            {
                await PushPresence();
                ApplyFriends(await api.Friends());
            }
            catch (ApiException e) when (e.Status == 401 || e.Status == 403) { }
            catch (Exception e) { Log.Write("friends: " + e.Message); }
            finally { refreshingFriends = false; }
        }

        async Task FriendsLoop(int generation)
        {
            while (signedIn && generation == friendsLoop)
            {
                try
                {
                    var f = await api.FriendsWait(friendsEtag ?? "");
                    if (generation != friendsLoop) break;
                    ApplyFriends(f);
                }
                catch (ApiException e) when (e.Status == 401 || e.Status == 403) { await Task.Delay(10_000); }
                catch (Exception e) { Log.Write("friends wait: " + e.Message); await Task.Delay(3_000); }
            }
        }

        /// <summary>
        /// Whether the game the player is in is public, for the mod (Win64\revival-game.txt): only public
        /// games count for MMR, calibration and the leave penalty. Rewritten on every friends answer (at
        /// least every 20 s, the long poll), with the time: the mod ignores an old file, and a missing or
        /// old one means a private game.
        /// </summary>
        void WriteGameVisibility(Dictionary<string, object> session)
        {
            if (session == null || !GameInstall.IsRunning() || !GameInstall.IsGameRoot(settings.GameRoot)) return;
            var at = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            try
            {
                File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-game.txt"),
                    $"visibility={(session.Bool("public") ? "public" : "private")}\nport={session.Long("port")}\nat={at}\n");
            }
            catch (IOException e) { Log.Write("game visibility: " + e.Message); }
        }

        /// <summary>
        /// Shows the relay the address this PC's game will use. The API is reached through Cloudflare,
        /// often over IPv6, while the game reaches the relay over IPv4: a friend's private game only lets
        /// in addresses the server knows for the host's friends, so without this the game was refused
        /// (the invite "did nothing") whenever the launcher's last call went over IPv6. A UDP datagram
        /// "P" + the account's secret proof to the relay's agent port, from the same machine as the game.
        /// </summary>
        void SendAddressProof(bool now)
        {
            if (addressProof == null || addressProofPort <= 0 || settings.RelayHost == null) return;
            if (!now && DateTime.UtcNow - addressProofSent < TimeSpan.FromSeconds(20)) return;
            addressProofSent = DateTime.UtcNow;
            try
            {
                var bytes = new byte[17];
                bytes[0] = (byte)'P';
                for (int i = 0; i < 16; i++) bytes[i + 1] = Convert.ToByte(addressProof.Substring(i * 2, 2), 16);
                using (var udp = new System.Net.Sockets.UdpClient(System.Net.Sockets.AddressFamily.InterNetwork))
                    for (int i = 0; i < 2; i++) udp.Send(bytes, bytes.Length, settings.RelayHost, addressProofPort);
            }
            catch (Exception e) { Log.Write("address proof: " + e.Message); }
        }

        /// <summary>invite | accept | decline | kick (username), leave: from the page or from the game (the mod).</summary>
        async Task SquadAction(string action, string username)
        {
            if (!new[] { "invite", "accept", "decline", "leave", "kick" }.Contains(action)) return;
            if (action != "leave" && string.IsNullOrWhiteSpace(username)) return;
            try
            {
                await api.Squad(action, username?.Trim());
                if (action == "invite") Toast("good", "Squad invite sent", username + " can join your squad.");
                else if (action == "accept") Toast("good", "Squad joined", "You now follow " + username + " into their games.");
                else if (action == "leave") Toast("good", "Squad left", "You play on your own again.");
            }
            catch (ApiException e) { Toast("bad", "Squad", e.Message); }
            await RefreshFriends();
        }

        /// <summary>
        /// Requests from the game's own friends list and squad widget (the mod writes
        /// Win64\revival-social-actions.txt: "invite NAME", "accept NAME", "decline NAME", "leave").
        /// </summary>
        async Task ModSocialActions()
        {
            if (!signedIn || !GameInstall.IsGameRoot(settings.GameRoot)) return;
            var path = Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-social-actions.txt");
            if (!File.Exists(path)) return;
            string[] lines;
            try
            {
                var taken = path + ".taken";
                if (File.Exists(taken)) File.Delete(taken);
                File.Move(path, taken);
                lines = File.ReadAllLines(taken);
                File.Delete(taken);
            }
            catch (IOException e) { Log.Write("social actions: " + e.Message); return; }
            foreach (var line in lines.Take(10))
            {
                var parts = line.Trim().Split(new[] { ' ' }, 2);
                if (parts.Length == 0 || parts[0].Length == 0) continue;
                Log.Write("game social action: " + parts[0]);
                await SquadAction(parts[0].ToLowerInvariant(), parts.Length > 1 ? parts[1] : null);
            }
        }

        static string Clean(string s) => new string((s ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());

        /// <summary>
        /// The squad part of the friends view: invites (toast, and the game's dialog), following the leader
        /// into its game, and Win64\revival-social.txt for the game's friends list and squad widget.
        /// </summary>
        void ApplySquad(Dictionary<string, object> f)
        {
            var squad = f.Obj("squad");
            if (squad == null) return;
            var running = GameInstall.IsRunning() && GameInstall.IsGameRoot(settings.GameRoot);
            var win64 = running ? GameInstall.Win64(settings.GameRoot) : null;
            foreach (var invite in squad.Objs("invites"))
            {
                var key = invite.Str("from") + "|" + invite.Str("at");
                if (!seenSquadInvites.Add(key)) continue;
                // no toast: the invite is in the Friends card (SQUAD INVITES) and, game running, in the game's dialog
                if (running)
                    try { File.WriteAllText(Path.Combine(win64, "revival-map-command.txt"), "squadinvite " + Clean(invite.Str("from"))); }
                    catch (IOException e) { Log.Write("squad invite to game: " + e.Message); }
            }
            // follow the leader: once per game, again after 45 s if still not in it (3 tries). Once in it
            // (the server stops asking), that game is never joined again by itself: a member who leaves
            // the leader's game goes back to the menu and stays there until the leader is in another game
            var follow = squad.Obj("follow");
            if (follow == null)
            {
                if (followSession != null) reachedSessions.Add(followSession);
                followSession = null;
            }
            else if (running && !reachedSessions.Contains(follow.Str("id")))
            {
                var id = follow.Str("id");
                var again = id == followSession && followTries < 3 && DateTime.UtcNow - followAt > TimeSpan.FromSeconds(45);
                if (id != followSession || again)
                {
                    followTries = id == followSession ? followTries + 1 : 1;
                    followSession = id;
                    followAt = DateTime.UtcNow;
                    Log.Write("squad: following " + squad.Str("leader") + " (try " + followTries + ")");
                    var _ = JoinFriend((int)follow.Long("port"), squad.Str("leader"), follow.Bool("dlc"), MapName(follow.Str("map")));
                }
            }
            if (!running) return;
            var sb = new System.Text.StringBuilder();
            sb.Append("you\t").Append(Clean(squad.Str("you"))).Append('\n');
            foreach (var fr in f.Objs("friends"))
                // online in the game's list = in the game (playing, hosting, in a game): a friend with only the
                // launcher open cannot follow a squad, so the game shows them offline
                sb.Append("friend\t").Append(Clean(fr.Str("username"))).Append('\t')
                  .Append(new[] { "playing", "hosting", "in_game" }.Contains(fr.Str("status")) ? 1 : 0)
                  .Append('\t').Append(Clean(fr.Str("status"))).Append('\n');
            foreach (var m in squad.Objs("members"))
                sb.Append("squad\t").Append(Clean(m.Str("username"))).Append('\t').Append(m.Bool("leader") ? 1 : 0)
                  .Append('\t').Append(m.Bool("online") ? 1 : 0).Append('\n');
            var text = sb.ToString();
            if (text == lastSocial) return;
            try { File.WriteAllText(Path.Combine(win64, "revival-social.txt"), text); lastSocial = text; }
            catch (IOException e) { Log.Write("social file: " + e.Message); }
        }

        void ApplyFriends(Dictionary<string, object> f)
        {
            friendsEtag = f.Str("etag");
            f["inGame"] = playing || GameInstall.IsRunning();
            State(new Dictionary<string, object> { ["friends"] = f });
            var friendsKey = string.Join(",", f.Objs("friends").Select(x => x.Str("username") + ":" + x.Str("status")))
                + " | in " + string.Join(",", f.Strs("incoming")) + " | out " + string.Join(",", f.Strs("outgoing"));
            if (friendsKey != lastFriendsKey) { Log.Write("friends: " + friendsKey); lastFriendsKey = friendsKey; }
            WriteGameVisibility(f.Obj("you")?.Obj("session"));
            ApplySquad(f);
            var you = f.Obj("you");
            if (you != null && you.Str("proof") != null && you.ContainsKey("proofPort") && you["proofPort"] != null)
            {
                addressProof = you.Str("proof");
                addressProofPort = (int)you.Long("proofPort");
                SendAddressProof(false);
            }
            // quick play found no game: the server picked this player to host a public one (the mod does it)
            var order = f.Obj("hostOrder");
            if (order != null && order.Str("id") != lastHostOrder && GameInstall.IsRunning() && GameInstall.IsGameRoot(settings.GameRoot))
            {
                lastHostOrder = order.Str("id");
                var map = new string((order.Str("map") ?? "").Where(char.IsLetterOrDigit).ToArray());
                var role = new string((order.Str("role") ?? "any").Where(char.IsLetter).ToArray());
                try
                {
                    File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-map-command.txt"), $"hostpublic {map} {role}");
                    Toast("info", "No game found", $"Creating a public game on {MapName(map)}: other players searching will join you.", 8000);
                    Log.Write($"quick play: hosting a public game on {map} as {role}");
                }
                catch (IOException e) { Log.Write("host order: " + e.Message); }
            }
            foreach (var invite in f.Objs("invites"))
            {
                var key = invite.Str("from") + "|" + invite.Str("at");
                if (!seenInvites.Add(key)) continue;
                var session = invite.Obj("session");
                Toast("info", invite.Str("from") + " invites you", $"{MapName(session.Str("map"))} · {session.Long("players")}/5 players", 30_000,
                    new Dictionary<string, object> { ["label"] = "Join", ["cmd"] = "join", ["port"] = session.Long("port"), ["from"] = invite.Str("from"),
                        ["dlc"] = session.Bool("dlc"), ["map"] = MapName(session.Str("map")) });
                if (WindowState == FormWindowState.Minimized && !playing) FlashWindow();
                // the game is running: the mod shows the invite in the game
                if (GameInstall.IsRunning() && GameInstall.IsGameRoot(settings.GameRoot))
                {
                    var name = new string((invite.Str("from") ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
                    var map = new string(MapName(session.Str("map")).Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '(' || c == ')').ToArray());
                    try
                    {
                        File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-map-command.txt"),
                            $"invite {session.Long("port")} {(session.Bool("dlc") ? 1 : 0)} {session.Long("players")} {name}|{map}");
                    }
                    catch (IOException e) { Log.Write("invite to game: " + e.Message); }
                }
            }
        }

        /// <summary>
        /// Back from minimized (the game was running): WebView2 can keep showing the page as it was
        /// when the window was minimized (a friend request still "pending"), so it is shown again
        /// and the friends list is fetched at once.
        /// </summary>
        void Restored()
        {
            if (web.CoreWebView2 != null) { web.Visible = false; web.Visible = true; }
            var _ = RefreshFriends();
        }

        // ---- uninstall (Settings): the game folder goes back to the Steam files
        async Task UninstallRevival()
        {
            if (busy || playing || GameInstall.IsRunning()) { Toast("warn", "Not now", "Close the game and wait for the current task to finish."); return; }
            if (!GameInstall.IsGameRoot(settings.GameRoot)) { Toast("info", "Nothing to uninstall", "No Propnight folder is set."); return; }
            busy = true;
            try
            {
                if (updater?.Manifest == null) { updater = new Updater(settings, (t, f) => { }); await updater.LoadManifest(); }
                var missing = await Task.Run(() => updater.Uninstall(settings.GameRoot));
                gamePlan = null;
                if (missing.Count == 0)
                    Toast("good", "Revival uninstalled", "Propnight is back to its Steam files. Press PLAY to install the revival again.", 9000);
                else
                    Toast("warn", "Revival uninstalled", "Some original files had no backup: in Steam, Propnight › Properties › Installed files › Verify integrity.", 12000);
            }
            catch (Exception e) { Log.Write("uninstall failed: " + e.Message); Toast("bad", "Uninstall failed", e.Message, 9000); }
            finally { busy = false; }
            await CheckUpdates(quiet: true);
        }

        // ---- optional content (DLC tab)
        void SendDlcState()
        {
            if (updater?.Manifest == null) return;
            var packs = updater.Manifest.Objs("dlc").Select(p => new Dictionary<string, object>
            {
                ["id"] = p.Str("id"), ["title"] = p.Str("title"), ["description"] = p.Str("description"), ["size"] = p.Long("size"),
                ["on"] = settings.DlcOn(p.Str("id")),
                ["maps"] = p.Objs("maps").Select(m => new Dictionary<string, object> {
                    ["name"] = m.Str("name"), ["from"] = m.Str("from"),
                    ["image"] = updater.Manifest.Str("filesUrl") + m.Obj("image")?.Str("sha256") }).ToArray(),
            }).ToArray();
            State(new Dictionary<string, object> { ["dlc"] = packs });
        }

        async Task SetDlc(string id, bool on)
        {
            if (updater?.Manifest == null || !updater.Manifest.Objs("dlc").Any(p => p.Str("id") == id)) return;
            if (busy || playing || GameInstall.IsRunning()) { Toast("warn", "Not now", "Close the game and wait for the current task to finish."); SendDlcState(); return; }
            if (!GameInstall.IsGameRoot(settings.GameRoot)) { PickFolder(); SendDlcState(); return; }
            var title = updater.Manifest.Objs("dlc").First(p => p.Str("id") == id).Str("title");
            settings.Dlc[id] = on;
            settings.Save();
            if (!on)
            {
                try { updater.RemoveDlc(settings.GameRoot, id); Toast("good", title + " removed", "You can install them again at any time."); }
                catch (Exception e) { Toast("bad", "Could not remove " + title, e.Message); }
                SendDlcState();
                await CheckUpdates(quiet: true);
                return;
            }
            SendDlcState();
            State(new Dictionary<string, object> { ["tab"] = "home" });   // progress shows on the PLAY button
            Toast("info", "Downloading " + title, "You can follow the download under the PLAY button.");
            busy = true;
            try
            {
                var work = new Updater(settings, (text, f) => PlayButton("update", "DOWNLOADING", text, f, true, true)) { Manifest = updater.Manifest };
                var plan = (await work.Plan(settings.GameRoot)).Where(f => f.Str("dlc") == id).ToList();
                await work.UpdateGame(settings.GameRoot, plan);
                Toast("good", title + " installed", "They are in the map list of Custom Game.");
            }
            catch (Exception e)
            {
                Log.Write("dlc install failed: " + e.Message);
                Toast("bad", title + ": download failed", e.Message, 9000);
            }
            finally { busy = false; }
            await CheckUpdates(quiet: true);
        }

        // map keys sent by the mod (hostMaps) -> names shown in the game's map selector
        static readonly Dictionary<string, string> MapNames = new Dictionary<string, string> {
            ["BigAbbey"] = "Abbey", ["BigFarm"] = "Farm (Night)", ["BigCircus"] = "Circus", ["SnowMain"] = "Fantasticville", ["BigEgypt"] = "Egypt", ["SummerCampMRG"] = "Camp", ["YagaMap"] = "Dark Swamp", ["Kitchen"] = "Kitchen", ["GoldenMine"] = "Goldhorn Mine", ["MansionReborn"] = "House", ["CampArena"] = "Summer Camp", ["Farm"] = "Farm (Day)", ["School"] = "School" };
        static string MapName(string map) => map != null && MapNames.TryGetValue(map, out var name) ? name : (map ?? "").Replace("_Main", "").Replace('_', ' ').Trim();

        [DllImport("user32.dll")] static extern bool FlashWindow(IntPtr hwnd, bool invert);
        void FlashWindow() { try { FlashWindow(Handle, true); } catch { } }

        // ---- bug report (Settings): what happened + the logs, to the admin panel
        async Task SendBug(string category, string text, bool withLogs)
        {
            var done = new Dictionary<string, object> { ["type"] = "bugSent", ["ok"] = false };
            try
            {
                var logs = withLogs ? await Task.Run(() => BugReport.CollectLogs(settings.GameRoot)) : null;
                await api.PostBug(category, text, logs);
                Log.Write($"bug report sent ({category}, logs {(logs == null ? "none" : logs.Length + " B")})");
                done["ok"] = true;
                Toast("good", "Bug report sent", "Thanks! It reached the team" + (logs != null ? " with your logs." : "."), 7000);
            }
            catch (ApiException e) { Toast("bad", "Report not sent", e.Message, 8000); }
            catch (Exception e) { Log.Write("bug report failed: " + e.Message); Toast("bad", "Report not sent", "The server could not be reached. Try again later.", 8000); }
            State(done);
        }

        async Task FriendAction(string action, string username, string code = null)
        {
            try
            {
                switch (action)
                {
                    case "request":
                        if (string.IsNullOrWhiteSpace(code)) return;
                        var sent = await api.FriendRequest(code.Trim());
                        Toast("good", sent.Str("status") == "friends" ? "You are now friends" : "Friend request sent", sent.Str("username") ?? "");
                        break;
                    case "copyCode":
                        // the code comes from the last friends list (never shown on screen for this)
                        var mine = (await api.Friends()).Str("code");
                        if (string.IsNullOrEmpty(mine)) return;
                        Clipboard.SetText(mine);
                        Toast("good", "Friend code copied", "Paste it to a friend: it is not shown on screen.");
                        return;
                    case "declineAll":
                        await api.FriendsPost("decline-all");
                        Toast("good", "Requests declined", "Every pending friend request was declined.");
                        break;
                }
            }
            catch (ApiException e) { Toast("bad", "Friends", e.Message); }
            if (new[] { "request", "declineAll" }.Contains(action)) { await RefreshFriends(); return; }
            if (action != null && action.StartsWith("squad"))
            {
                await SquadAction(action.Substring(5).ToLowerInvariant(), username);
                return;
            }
            if (!new[] { "accept", "decline", "remove", "invite" }.Contains(action) || string.IsNullOrWhiteSpace(username)) return;
            try
            {
                var r = await api.Friend(action, username.Trim());
                if (action == "accept") Toast("good", "You are now friends", username);
                else if (action == "invite") Toast("good", "Invite sent", $"{username} can join your game from the launcher.");
            }
            catch (ApiException e) { Toast("bad", action == "invite" ? "Could not invite" : "Friends", e.Message); }
            await RefreshFriends();
        }

        /// <summary>
        /// Joins a friend's game on the relay: the mod reads "joinrelay PORT" from its command file
        /// (the relay address from Win64/revival-relay.txt, written at launch) once the game's menu is ready.
        /// </summary>
        async Task JoinFriend(int port, string from, bool classicMap = false, string map = null)
        {
            if (port < 7800 || port > 7999) return;
            if (!GameInstall.IsGameRoot(settings.GameRoot)) { PickFolder(); return; }
            if (classicMap && !settings.DlcOn("classic-maps"))
            {
                // the game would fail to load the map: say why, here and in the game's own message box
                Toast("warn", "Classic maps needed", $"{from ?? "Your friend"} plays on {map ?? "a classic map"}. Install the Classic maps in the DLC tab to join.", 9000);
                State(new Dictionary<string, object> { ["tab"] = "dlc" });
                if (GameInstall.IsRunning())
                    File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-map-command.txt"), "missingmaps " + (map ?? ""));
                return;
            }
            SendAddressProof(true);   // the friend's private game must know this PC's address first
            if (GameInstall.IsRunning())
            {
                File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-map-command.txt"), "joinrelay " + port);
                Toast("good", "Joining " + (from ?? "your friend"), "Propnight is connecting to the game.");
                return;
            }
            if (busy || playing) { Toast("warn", "Not now", "Wait until the current task is finished."); return; }
            pendingJoinPort = port;
            Toast("info", "Joining " + (from ?? "your friend"), "Propnight starts, then joins the game.");
            await Play();
        }

        async Task RefreshServer()
        {
            var server = new Dictionary<string, object> { ["online"] = false };
            try
            {
                var s = await api.Sessions();
                var games = s.Objs("sessions").ToList();
                server["online"] = true;
                server["games"] = games.Count;
                server["players"] = games.Sum(g => (int)g.Long("players"));
            }
            catch { }
            State(new Dictionary<string, object> { ["server"] = server });
        }

        /// <summary>Looks at the signed manifest and notifies; installs nothing.</summary>
        async Task CheckUpdates(bool quiet)
        {
            if (busy) return;
            busy = true;
            if (!quiet) SendUpdateState(checking: true);
            try
            {
                updater = new Updater(settings, (text, f) => { if (!quiet && gamePlan == null) PlayButton("play", "PLAY", text, f < 0 ? -1 : f, true, true); });
                await updater.LoadManifest();
                var previousLauncher = launcherUpdate;
                var previousVersion = manifestVersion;
                launcherUpdate = updater.LauncherUpdate();
                manifestVersion = updater.Manifest.Str("version");
                if (launcherUpdate != null && launcherUpdate != previousLauncher)
                    Toast("warn", $"Launcher {launcherUpdate} available", "Update the launcher to keep playing.", 9000);
                if (GameInstall.IsGameRoot(settings.GameRoot)) updater.DlcDefaults(settings.GameRoot);
                SendDlcState();
                if (GameInstall.IsGameRoot(settings.GameRoot) && launcherUpdate == null)
                {
                    gamePlan = await updater.Plan(settings.GameRoot);
                    if (gamePlan.Count > 0 && (previousVersion != manifestVersion || !quiet))
                        Toast("info", "Game update available", $"{gamePlan.Count} file(s), {gamePlan.Sum(f => f.Long("size")) / 1e6:0.#} MB. Press UPDATE.", 7000);
                }
                SendUpdateState();
            }
            catch (Exception e)
            {
                Log.Write("update check failed: " + e.Message);
                SendUpdateState(error: true);
                if (!quiet) Toast("bad", "Update check failed", e.Message, 8000);
                gamePlan = gamePlan ?? new List<Dictionary<string, object>>();
            }
            finally
            {
                busy = false;
                ShowIdleButton();
            }
        }

        async Task SignIn(bool register, string user, string password)
        {
            try
            {
                var r = register ? await api.Register(user, password) : await api.Login(user, password);
                settings.Token = api.Token = r.Str("token");
                Remember(r.Obj("account"));
                Toast("good", register ? "Account created" : "Signed in", "Welcome, " + settings.Username + "!");
                await EnterHome();
            }
            catch (Exception e) { State(new Dictionary<string, object> { ["authError"] = e.Message }); }
        }

        async Task SignOut()
        {
            if (busy || playing) { Toast("warn", "Not now", "Wait until the current task is finished."); return; }
            try { await api.Logout(); } catch { }
            settings.Token = null; api.Token = null; settings.Save();
            updateTimer.Stop();
            friendsTimer.Stop();
            friendsLoop++;
            friendsEtag = null;
            seenInvites.Clear();
            ShowAuth("");
        }

        void PickFolder()
        {
            if (busy || playing) { Toast("warn", "Not now", "Wait until the current task is finished."); return; }
            // Explorer-style picker, opened where the game is now (or in the Steam library)
            var start = GameInstall.IsGameRoot(settings.GameRoot) ? Path.GetDirectoryName(settings.GameRoot) : GameInstall.Detect();
            var picked = FolderPicker.Pick(Handle, "Select the Propnight folder (steamapps\\common\\Propnight)", start);
            if (picked == null) return;
            var root = GameInstall.Normalize(picked);
            if (root == null) { Toast("bad", "Propnight not found", "Propnight\\Binaries\\Win64\\Propnight.exe is not in that folder."); return; }
            if (string.Equals(root, settings.GameRoot, StringComparison.OrdinalIgnoreCase)) { Toast("info", "Same folder", root); return; }
            settings.GameRoot = root;
            settings.Save();
            gamePlan = null;
            State(new Dictionary<string, object> { ["gameRoot"] = root });
            Toast("good", "Game folder changed", root);
            var _ = CheckUpdates(quiet: false);
        }

        async Task UpdateLauncher()
        {
            if (busy || playing || launcherUpdate == null) return;
            busy = true;
            try
            {
                PlayButton("launcher", "UPDATING", "Downloading the new launcher…", -1, true, true);
                updater = updater ?? new Updater(settings, (t, f) => { });
                // the manifest is read again: the one from the last check (up to 10 min old) could name an
                // older launcher than the latest one published since (1.13 -> 1.14 -> 1.15 instead of 1.16)
                var reporting = new Updater(settings, (t, f) => PlayButton("launcher", "UPDATING", t, f, true, true));
                await reporting.LoadManifest();
                updater.Manifest = reporting.Manifest;
                launcherUpdate = reporting.LauncherUpdate();
                if (launcherUpdate == null) { busy = false; ShowIdleButton(); SendUpdateState(); return; }
                PlayButton("launcher", "UPDATING", $"Downloading launcher {launcherUpdate}…", -1, true, true);
                await reporting.InstallLauncherUpdate();
                Toast("good", "Launcher updated", "Restarting…", 3000);
                await Task.Delay(1200);
                Close();
            }
            catch (Exception e) { busy = false; Toast("bad", "Launcher update failed", e.Message, 8000); ShowIdleButton(); }
        }

        async Task Play()
        {
            if (busy || playing) return;
            if (!GameInstall.IsGameRoot(settings.GameRoot)) { PickFolder(); return; }
            if (launcherUpdate != null) { Toast("warn", "Update required", $"Install launcher {launcherUpdate} first (banner above)."); return; }
            if (GameInstall.IsRunning()) { Toast("warn", "Propnight is already running", "Close it first, then press PLAY."); return; }
            busy = true;
            ProfileSync sync = null;
            Reports reports = null, matches = null;
            try
            {
                var work = new Updater(settings, (text, f) => PlayButton("update", "UPDATING", text, f, true, true));
                await work.LoadManifest();
                if (work.LauncherUpdate() != null) { updater = work; launcherUpdate = work.LauncherUpdate(); SendUpdateState(); Toast("warn", "Update required", "A new launcher was just published."); return; }
                var plan = await work.Plan(settings.GameRoot);
                await work.UpdateGame(settings.GameRoot, plan);
                if (plan.Count > 0) Toast("good", "Game updated", $"{plan.Count} file(s) installed.");
                gamePlan = new List<Dictionary<string, object>>();
                manifestVersion = work.Manifest.Str("version");
                SendUpdateState();

                GameInstall.SetIdentity(settings.GameRoot, settings.Username, settings.SteamId);
                sync = new ProfileSync(api, settings, t => PlayButton("play", "SYNCING", t, -1, true, true));
                await sync.BeforeLaunch();

                PlayButton("play", "STARTING", "Launching Propnight…", -1, true, true);
                // where friends' games are (joinrelay): given by the server, so no published file holds it
                if (settings.RelayHost != null)
                    File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-relay.txt"), settings.RelayHost);
                if (pendingJoinPort != null)
                    File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-map-command.txt"), "joinrelay " + pendingJoinPort);
                pendingJoinPort = null;
                try { File.Delete(GameInstall.PresencePath(settings.GameRoot)); } catch (IOException) { }   // the mod writes it again
                var exe = GameInstall.Exe(settings.GameRoot);
                var game = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false });
                playing = true;
                busy = false;
                lastPresence = DateTime.MinValue;   // friends see "playing" right away
                var _presence = RefreshFriends();
                sync.StartWatching();
                reports = Reports.PlayerReports(api, settings.GameRoot);
                reports.Start();
                matches = Reports.Matches(api, settings.GameRoot);
                matches.Start();
                PlayButton("play", "IN GAME", "Keep the launcher open while you play: it saves your profile and brings your friends' invites into the game.", null, true, true);
                WindowState = FormWindowState.Minimized;
                await Task.Run(() => game.WaitForExit());
                playing = false;
                lastPresence = DateTime.MinValue;
                WindowState = FormWindowState.Normal;
                Activate();
                PlayButton("play", "SAVING", "Saving your profile…", -1, true, true);
                busy = true;
                await sync.AfterExit();
                await matches.Flush();
                var sentReports = await reports.Flush();
                if (sentReports > 0) Toast("good", "Reports sent", $"{sentReports} report(s) sent to the moderators.");
                Toast("good", "Profile saved", "See you next game!");
            }
            catch (ApiException e) when (e.Status == 401 || e.Status == 403)
            {
                settings.Token = null; api.Token = null; settings.Save();
                ShowAuth(e.Message);
            }
            catch (Exception e)
            {
                Log.Write("play failed: " + e);
                Toast("bad", "Could not start", e.Message, 9000);
            }
            finally
            {
                sync?.Dispose();
                reports?.Dispose();
                matches?.Dispose();
                pendingJoinPort = null;
                playing = false;
                busy = false;
                ShowIdleButton();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // closing the launcher never closes the game, but what it does while the game runs stops:
            // the page explains it and asks (window brought back if it was minimized)
            if ((playing || GameInstall.IsRunning()) && !closeConfirmed && e.CloseReason == CloseReason.UserClosing && web.CoreWebView2 != null)
            {
                e.Cancel = true;
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
                State(new Dictionary<string, object> { ["type"] = "confirmClose" });
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
