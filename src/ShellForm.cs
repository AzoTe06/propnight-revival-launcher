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
        string lastHostOrder, lastQpOffer, lastResumeOrder;
        DateTime lastPresence = DateTime.MinValue;
        string lastPresenceKey, lastFriendsKey;
        FormWindowState lastWindowState = FormWindowState.Normal;
        int? pendingJoinPort;
        string pendingHostCommand;   // Servers › Create a game with the game closed: "hostpublic MAP ROLE" written at its launch
        string pendingJoinHost;   // the relay of the game to join (region), null: the default relay
        // regions: the relays the server lists (account view) and this PC's round trip to each, sent with the presence
        List<RelayPings.Relay> relays = new List<RelayPings.Relay>();
        Dictionary<string, int> relayPings;
        DateTime relayPingsAt = DateTime.MinValue;
        bool measuringPings;
        readonly DiscordRpc discord = new DiscordRpc();
        Dictionary<string, object> lastFriendsView;   // the latest friends view (your game, for Discord)
        string activityKey;                          // what the Discord activity shows: its timer restarts when it changes
        DateTime activitySince = DateTime.UtcNow;
        bool activityShown;
        readonly Dictionary<int, string> buildsByPort = new Dictionary<int, string>();   // game port -> host's build
        string outdatedShown;          // newest build already announced in the running game
        bool refreshingFriends, pushingPresence;
        string friendsEtag;
        string addressProof; int addressProofPort; DateTime addressProofSent;
        readonly HashSet<string> seenSquadInvites = new HashSet<string>();
        string followSession; DateTime followAt; int followTries; string lastSocial;
        // the friends of the last friends answer: the in-game overlay's Join (Overlay.cs) joins one's game
        List<Dictionary<string, object>> overlayFriends = new List<Dictionary<string, object>>();
        // the in-game overlay (Overlay.cs, Shift + F1): the game invites it shows (key "from|at"), the friends invited from it,
        // the friends' last statuses (a friend coming online: a notification), this session's matches (read once per change)
        Overlay overlay;
        readonly Dictionary<string, Dictionary<string, object>> overlayInvites = new Dictionary<string, Dictionary<string, object>>();
        readonly Dictionary<string, DateTime> overlayInvited = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        // the overlay's Join waiting (the launcher page's "Joining…"): the friend, their game's port, since when
        string overlayJoinName; int overlayJoinPort; DateTime overlayJoinAt;
        // the game settings just changed from the overlay (field -> value, when): shown as set until the mod's answer has them
        readonly Dictionary<string, KeyValuePair<object, DateTime>> overlaySet = new Dictionary<string, KeyValuePair<object, DateTime>>();
        Dictionary<string, string> overlayStatuses;
        readonly Dictionary<string, Dictionary<string, object>> overlaySeenMatches = new Dictionary<string, Dictionary<string, object>>();   // startedAt|map -> record
        string overlayClip; Dictionary<string, object> overlayReport;   // the overlay's report: its clip, its result
        readonly HashSet<string> reachedSessions = new HashSet<string>();   // leader's games already joined (or left)
        int friendsLoop;   // generation of the friends long poll (a new one stops the old)
        static readonly Dictionary<string, int> HitTests = new Dictionary<string, int> {
            ["left"] = 10, ["right"] = 11, ["top"] = 12, ["topleft"] = 13, ["topright"] = 14, ["bottom"] = 15, ["bottomleft"] = 16, ["bottomright"] = 17 };
        Updater updater;
        List<Dictionary<string, object>> gamePlan;
        bool exePatchPending;   // Propnight.exe gets a new fix at the next PLAY (no download)
        string launcherUpdate, manifestVersion;
        bool updatingLauncher;   // "Update now" pressed: its button loads until the launcher restarts
        bool busy, playing, signedIn, closeConfirmed;
        string maintenance;   // the server's maintenance message, null when the game can be played
        string discordName;   // linked Discord account (server), null when not linked
        string deletionStatus;   // the account's deletion request (Settings): pending | refused | cancelled, null when none
        DateTime? bannedUntil;   // the account's ban (UTC), seen in a refused call; null when not known to be banned
        // signing in with Discord: the server's sign-in id while it waits (browser, then maybe a username to choose); a
        // Discord sign-in lets the player set a new password without the old one for a while (server: 30 min)
        string dcLoginId; int dcGeneration; DateTime freshUntil = DateTime.MinValue;
        Dictionary<string, object> lastMe;   // the account as /me last said (the email announcement reads it)
        // the notification area (taskbar corner), when the player chose it (Settings > Game): minimizing sends the launcher
        // there, and PLAY too (the end of the game brings it back); otherwise it minimizes to the taskbar and stays open in game
        readonly NotifyIcon tray = new NotifyIcon { Text = "Propnight Revival" };
        bool trayTipShown;

        public ShellForm()
        {
            Text = "Propnight Revival";
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(7, 7, 12);
            Controls.Add(web);
            api.Banned += OnBanned;
            serverTimer.Tick += async (s, e) => await RefreshServer();
            updateTimer.Tick += async (s, e) =>
            {
                if (signedIn) await RefreshMaintenance();
                if (signedIn && !busy && !playing) await CheckUpdates(quiet: true);
                else if (signedIn && playing) await CheckWhilePlaying();
            };
            friendsTimer.Tick += async (s, e) => { var _ = MeasurePings(); await PushPresence(); await ModSocialActions(); UpdateDiscord(); };
            WatchNetwork();
            overlay = new Overlay(OverlayState, OverlayAction, OverlayWin64, text => { Log.Write("overlay: " + text); Toast("warn", "In-game overlay", text, 9000); })
                { TimerOn = settings.OverlayTimer, ToastsRight = settings.OverlayRight, FpsOn = settings.OverlayFps };
            if (OverlayOn) overlay.Start();
            overlay.GameStarted = () =>
            {
                if (settings.OverlayFps) OverlayGameCommand("fps", "1", 0);
                // at each launch: how to open the overlay (its first notification, before any friend's)
                overlay.Notify("Shift + Tab", "opens the overlay", null, true, 6);
            };
            serversTimer.Tick += async (s, e) => await RefreshServers();
            Load += async (s, e) => await InitWebView();
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            tray.Icon = Icon ?? SystemIcons.Application;
            var menu = new ContextMenuStrip();
            var open = menu.Items.Add("Open Propnight Revival", null, (s, e) => ShowFromTray());
            open.Font = new Font(open.Font, FontStyle.Bold);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, (s, e) => Close());
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
            tray.BalloonTipClicked += (s, e) => ShowFromTray();
            tray.Visible = true;
            EventHandler maxState = (s, e) => { if (web.CoreWebView2 != null && WindowState != FormWindowState.Minimized) State(new Dictionary<string, object> { ["maximized"] = WindowState == FormWindowState.Maximized }); };
            Resize += maxState;
            SizeChanged += maxState;
            Resize += (s, e) =>
            {
                if (lastWindowState == FormWindowState.Minimized && WindowState != FormWindowState.Minimized) Restored();
                lastWindowState = WindowState;
                // minimized from the page, the taskbar or Win+D: into the notification area when the player chose it
                // (Settings > Game), else an ordinary minimize that stays on the taskbar
                if (WindowState == FormWindowState.Minimized && Visible && settings.MinimizeOnPlay) BeginInvoke((Action)HideToTray);
                Still();
            };
            VisibleChanged += (s, e) => Still();
            Activated += (s, e) => { active = true; Still(); };
            Deactivate += (s, e) => { active = false; Still(); };
        }

        // ---- the page draws nothing nobody sees (performance audit 2026-11-05: players saw slowdowns "because of the
        // launcher"; its GPU process used ~15 % of a core on average). Minimized: the page is hidden (WebView2 stops drawing
        // it; hidden in the notification area, the same happens by itself) and uses less memory. Behind the game (the
        // launcher not the active window while the game runs): its animations wait (.still: the IN GAME spinner), the
        // page stays drawn for a second screen. Events only, no polling.
        bool active, still;
        void Still()
        {
            var core = web.CoreWebView2;
            if (core == null) return;
            var minimized = WindowState == FormWindowState.Minimized;
            if (web.Visible == minimized) web.Visible = !minimized;
            try { core.MemoryUsageTargetLevel = minimized || !Visible ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal; }
            catch (Exception) { }
            var now = !active && playing;
            if (now == still) return;
            still = now;
            var _ = core.ExecuteScriptAsync("document.documentElement.classList.toggle('still', " + (now ? "true" : "false") + ")");
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
            ShowFromTray();
            if (File.Exists(LinkFile)) { var _ = HandleLink(); return; }   // a Join link: no "already open" message
            Toast("info", "Already open", "The launcher was already running: here it is.", 3500);
        }

        // ---- "Join" links (the Discord live games: server/src/discordgames.mjs -> site page /join/<game> -> this link)
        public const string LinkScheme = "propnightrevival";
        public static string LinkFile => Path.Combine(Config.DataDir, "pending-link.txt");

        /// <summary>propnightrevival:// opens this exe (per user, no admin): written at each start (the exe may have moved).</summary>
        static void RegisterLinkScheme()
        {
            try
            {
                var exe = Application.ExecutablePath;
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + LinkScheme))
                {
                    key.SetValue("", "URL:Propnight Revival");
                    key.SetValue("URL Protocol", "");
                    using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
                    using (var command = key.CreateSubKey(@"shell\open\command")) command.SetValue("", $"\"{exe}\" \"%1\"");
                }
            }
            catch (Exception e) { Log.Write("link scheme: " + e.Message); }
        }

        /// <summary>
        /// A Join link left by Program (this start, or a second launcher started by the browser): the game is looked up in
        /// the public games (gone, private, in a match, full: said, not joined), then joined like the SERVERS tab's Join.
        /// Before sign-in it waits (EnterHome calls this again).
        /// </summary>
        async Task HandleLink()
        {
            if (!signedIn || !File.Exists(LinkFile)) return;
            string link;
            try { link = File.ReadAllText(LinkFile).Trim(); File.Delete(LinkFile); } catch (IOException) { return; }
            var m = System.Text.RegularExpressions.Regex.Match(link, @"^propnightrevival://join/([0-9a-f-]{36})/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) { Log.Write("link ignored: " + link); return; }
            var id = m.Groups[1].Value.ToLowerInvariant();
            Log.Write("join link: " + id);
            try
            {
                var game = (await api.Servers()).Objs("servers").FirstOrDefault(s => s.Str("id") == id);
                if (game == null) { Toast("warn", "Game not found", "This game is over, or it is not public anymore.", 7000); return; }
                RememberBuild(game);
                if (game.Bool("mine")) { Toast("info", "This is your game", "You are hosting it.", 5000); return; }
                if (game.Bool("joined")) { Toast("info", "Already in this game", "You are playing in it.", 5000); return; }
                if (game.Str("phase") != "lobby") { Toast("warn", "In a match", "This game is in a match: join it once it is back in its lobby.", 7000); return; }
                if (game.Long("players") >= game.Long("max")) { Toast("warn", "Game full", "This lobby is full right now.", 7000); return; }
                await JoinFriend((int)game.Long("port"), "a game from Discord", game.Bool("dlc"), MapName(game.Str("map")), game.Str("host"));
            }
            catch (Exception e) { Log.Write("join link: " + e.Message); Toast("bad", "Could not join", e.Message, 7000); }
        }

        void HideToTray()
        {
            if (!Visible || WindowState != FormWindowState.Minimized) return;
            Hide();
            if (!trayTipShown && !playing)
            {
                trayTipShown = true;
                tray.ShowBalloonTip(4000, "Propnight Revival is still running", "Click its icon here (arrow next to the clock) to open it.", ToolTipIcon.Info);
            }
        }

        /// <summary>Back from the notification area (or from minimized), in front of the other windows.</summary>
        void ShowFromTray()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;   // Resize -> Restored()
            TopMost = true; TopMost = false;
            Activate();
        }

        /// <summary>The page, with the chosen language's dictionary in it (ui/i18n/&lt;lang&gt;.json, embedded): translated from its
        /// first paint. English: no dictionary.</summary>
        string ReadUi()
        {
            using (var stream = typeof(ShellForm).Assembly.GetManifestResourceStream("PropnightRevival.ui.index.html"))
            using (var reader = new StreamReader(stream))
            {
                var page = reader.ReadToEnd();
                var dict = ReadDictionary(UiLanguage());
                return dict == null ? page : page.Replace("/*I18N_DICT*/null", dict);
            }
        }

        // ---- the launcher's languages (Settings > Game > Language): Windows' own when it is one of them, else English
        static readonly string[] Languages = { "en", "fr", "ru", "es", "de" };
        string UiLanguage()
        {
            if (settings.Language != null && Array.IndexOf(Languages, settings.Language) >= 0) return settings.Language;
            var windows = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return Array.IndexOf(Languages, windows) >= 0 ? windows : "en";
        }
        static string ReadDictionary(string lang)
        {
            if (lang == "en") return null;
            using (var stream = typeof(ShellForm).Assembly.GetManifestResourceStream("PropnightRevival.ui.i18n." + lang + ".json"))
            {
                if (stream == null) return null;
                using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8)) return reader.ReadToEnd();
            }
        }
        void SetLanguage(string lang)
        {
            settings.Language = Array.IndexOf(Languages, lang) >= 0 ? lang : null;   // "auto": Windows' own
            settings.Save();
            var dict = ReadDictionary(UiLanguage());
            Log.Write("language: " + (settings.Language ?? "auto") + " -> " + UiLanguage());
            State(new Dictionary<string, object> { ["type"] = "i18n", ["language"] = settings.Language ?? "auto",
                ["dict"] = dict == null ? new Dictionary<string, object> { ["lang"] = "en" } : Json.Parse(dict) });
            serverNotesAt = DateTime.MinValue;   // the patch notes in the new language
            var _ = SendNotes();
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
                    // after a bug report: the report number for the support team, and the project's Discord
                    case "copyBugNumber":
                        var number = (m.Str("id") ?? "").Trim();
                        if (number.Length > 0 && number.Length < 12 && number.All(char.IsDigit)) { Clipboard.SetText("#" + number); Toast("good", $"#{number} copied", "Paste it to the support team on Discord."); }
                        break;
                    case "openDiscord": Process.Start(new ProcessStartInfo(DiscordInvite) { UseShellExecute = true }); break;
                    // Settings › Help › What the revival installs: the site's list of every file (its #files section), in the launcher's language
                    case "openFilesList":
                        var siteLang = UiLanguage();
                        Process.Start(new ProcessStartInfo("https://propnightrevival.online/" + (siteLang == "en" ? "" : siteLang + "/") + "#files") { UseShellExecute = true });
                        break;
                    case "updateLauncher": await UpdateLauncher(); break;
                    case "play": await Play(); break;
                    case "warnSeen": await WarningSeen(m.Long("id")); break;
                    case "serversTab":
                        if (m.Bool("on")) { serversTimer.Start(); await RefreshServers(); } else serversTimer.Stop();
                        break;
                    case "friend": await FriendAction(m.Str("action"), m.Str("username"), m.Str("code")); break;
                    case "hostPublic": await HostPublic(m.Str("role")); break;
                    case "join": await JoinFriend((int)m.Long("port"), m.Str("from"), m.Bool("dlc"), m.Str("map"), m.Str("host")); break;
                    case "pageError": Log.Write("page error: " + m.Str("message")); break;
                    case "dlc": await SetDlc(m.Str("id"), m.Bool("on")); break;
                    case "dlcAll": await QueueDlc(updater?.Manifest?.Objs("dlc").Select(p => p.Str("id")).Where(id => !settings.DlcOn(id)).ToList()); break;
                    case "dlcUnqueue": dlcQueue.Remove(m.Str("id")); SendDlcQueue(); break;
                    case "dlcStop": dlcQueue.Clear(); SendDlcQueue(); break;
                    case "uninstall": await UninstallRevival(); break;
                    case "verify": await VerifyFiles(); break;
                    case "deleteAccount": await AccountDeletion(true); break;
                    case "deleteCancel": await AccountDeletion(false); break;
                    case "testVersion":
                        settings.TestVersion = m.Bool("on");
                        settings.Save();
                        await ApplyTestChannel(null, true);
                        break;
                    case "discordActivity":
                        settings.DiscordActivity = m.Bool("on");
                        settings.Save();
                        State(new Dictionary<string, object> { ["discordActivity"] = settings.DiscordActivity });
                        UpdateDiscord();
                        break;
                    case "dx11": SetDx11(m.Bool("on")); break;
                    case "setLanguage": SetLanguage(m.Str("lang")); break;
                    case "minimizeOnPlay":
                        settings.MinimizeOnPlay = m.Bool("on");
                        settings.Save();
                        Log.Write("minimize on play: " + (settings.MinimizeOnPlay ? "on" : "off"));
                        State(new Dictionary<string, object> { ["minimizeOnPlay"] = settings.MinimizeOnPlay });
                        break;
                    case "dx11On": SetDx11(true); Toast("good", "DirectX 11 on", "The game starts with DirectX 11 from now on: press PLAY."); break;
                    case "discordLink": await LinkDiscord(); break;
                    case "discordSignIn": await DiscordSignIn(); break;
                    case "discordSignInCancel": dcLoginId = null; dcGeneration++; State(new Dictionary<string, object> { ["dcSignIn"] = "idle" }); break;
                    case "discordRegister": await DiscordRegister(m.Str("username")); break;
                    case "forgot": await ForgotPassword(m.Str("login")); break;
                    case "setEmail": await SetEmail(m.Str("email")); break;
                    case "confirmEmail": await ConfirmEmail(m.Str("code")); break;
                    case "setPassword": await SetPassword(m.Str("current"), m.Str("password")); break;
                    case "setUsername": await SetUsername(m.Str("username")); break;
                    case "maxProgress": await MaxProgress(); break;
                    case "bug": await SendBug(m.Str("category"), m.Str("text"), m.Bool("logs"), m.Strs("images").ToList(), m.Str("ref"), m.Str("tag")); break;
                    case "openBugs":
                        bugsOpen = true;
                        State(new Dictionary<string, object> { ["type"] = "openBugs" });
                        await LoadReports(false);
                        break;
                    case "bugsClosed": bugsOpen = false; break;
                    case "bugMessage": await SendBugMessage(m.Long("id"), m.Str("text"), m.Bool("logs"), m.Strs("images").ToList()); break;
                    case "openCheats":
                        cheatsOpen = true;
                        State(new Dictionary<string, object> { ["type"] = "openCheats" });
                        await LoadReports(false);
                        break;
                    case "cheatsClosed": cheatsOpen = false; break;
                    case "pickClip": PickClip("cheat"); break;
                    case "pickReportClip": PickClip("report"); break;
                    case "pickBugClip": PickClip("bug"); break;
                    case "clearBugClip": RemoveClip(bugClips, m, "bugClips"); break;
                    case "clearReportClip": reportClip = null; break;
                    case "reportClipUpload": await ReportClipUpload(m); break;
                    case "clearClip": cheatClip = null; break;
                    case "cheat": await SendCheat(m.Str("reported"), m.Str("text"), m.Strs("images").ToList(), m.Str("ref"), m.Str("reason") ?? "cheating"); break;
                    case "viewLogs": await ViewLogs(m.Long("id"), m.Long("event")); break;
                    case "cheatMessage": await SendCheatMessage(m.Long("id"), m.Str("text"), m.Strs("images").ToList()); break;
                    case "pickMsgClip": PickClip("msg"); break;
                    case "clearMsgClip": RemoveClip(msgClips, m, "msgClips"); break;
                    case "community": await CommunityCall(m); break;
                    case "communityImage": await CommunityImage(m); break;
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
        void Toast(string level, string title, string text = "", int ms = 5200, Dictionary<string, object> action = null)
        {
            State(new Dictionary<string, object> { ["type"] = "toast", ["level"] = level, ["title"] = title, ["text"] = text, ["ms"] = ms, ["action"] = action });
            // the overlay's join refused (not in a game anymore, version, classic maps, busy): its Join comes back, the reason
            // over the game (the launcher's toast is behind it)
            if (overlayJoinName != null && level != "good" && level != "info" && DateTime.UtcNow - overlayJoinAt < TimeSpan.FromSeconds(15))
            {
                overlayJoinName = null;
                Action show = () => { overlay?.Notify(title, "", null, false, 5); overlay?.Push(false); };
                if (InvokeRequired) BeginInvoke(show); else show();
            }
        }
        void PlayButton(string kind, string title, string sub, double? progress = null, bool busy = false, bool disabled = false) =>
            State(new Dictionary<string, object> { ["play"] = new Dictionary<string, object> {
                ["kind"] = kind, ["title"] = title, ["sub"] = sub, ["progress"] = progress, ["busy"] = busy, ["disabled"] = disabled } });

        /// <summary>Maintenance mode (admin): PLAY is replaced by the server's message until it ends.</summary>
        async Task RefreshMaintenance()
        {
            try
            {
                var status = await api.Status();
                var now = status.Bool("maintenance") ? (status.Str("message") ?? "Propnight Revival is under maintenance.") : null;
                if (now == maintenance) return;
                maintenance = now;
                Log.Write("maintenance: " + (now ?? "off"));
                if (!busy && !playing) ShowIdleButton();
            }
            catch (Exception) { }   // server unreachable: nothing changes
        }

        void ShowIdleButton()
        {
            if (playing) return;
            if (maintenance != null) { PlayButton("launcher", "MAINTENANCE", maintenance, null, false, true); return; }
            if (!GameInstall.IsGameRoot(settings.GameRoot)) PlayButton("play", "CHOOSE FOLDER", "Propnight was not found. Pick steamapps\\common\\Propnight in Settings.", null, false, false);
            // the PLAY button itself installs it (UPDATE): the launcher restarts by itself
            else if (launcherUpdate != null) PlayButton("launcher", $"Launcher update {launcherUpdate}", "Needed to play. The launcher restarts by itself, then checks the game files.", null, false, false);
            else if (gamePlan == null) PlayButton("play", "PLAY", "Checking for updates…", -1, true, true);
            else if (gamePlan.Count > 0)
            {
                var mb = gamePlan.Sum(f => f.Long("size")) / 1e6;
                PlayButton("update", "Game update", $"{gamePlan.Count} file{(gamePlan.Count == 1 ? "" : "s")} to download ({mb:0.#} MB). The game starts with PLAY afterwards.");
            }
            else if (exePatchPending) PlayButton("play", "PLAY", "A game fix is ready: Propnight.exe is patched when you press PLAY.");
            else PlayButton("play", "PLAY", "Everything is up to date.");
        }

        /// <summary>Patch notes for the home page and the News tab: the server's (admin › Patch notes, the ones this account sees:
        /// a note can be shown to the team or the test channels first), asked again every 5 min or when the account changes;
        /// the manifest's (release.py) when the server cannot be reached. NEW until seen once; sent to the page only when
        /// they changed.</summary>
        List<Dictionary<string, object>> serverNotes;
        DateTime serverNotesAt = DateTime.MinValue;
        string serverNotesToken, notesSent;
        static string NotesCachePath => Path.Combine(Config.DataDir, "patchnotes-cache.json");
        /// <summary>The notes of the last start, shown at once on the home page (the server's come a moment later).</summary>
        void SendCachedNotes()
        {
            if (notesSent != null) return;
            try
            {
                if (!File.Exists(NotesCachePath)) return;
                var notes = Json.Parse(File.ReadAllText(NotesCachePath)).Objs("notes").ToList();
                if (notes.Count == 0) return;
                State(new Dictionary<string, object> { ["notes"] = new Dictionary<string, object> {
                    ["releases"] = notes.Cast<object>().ToList(), ["unseen"] = notes[0].Str("version") != settings.NotesSeen } });
            }
            catch (Exception e) { Log.Write("patch notes cache: " + e.Message); }
        }

        async Task SendNotes()
        {
            if (serverNotes == null || DateTime.UtcNow - serverNotesAt > TimeSpan.FromMinutes(5) || serverNotesToken != api.Token)
            {
                serverNotesAt = DateTime.UtcNow;
                serverNotesToken = api.Token;
                try
                {
                    serverNotes = (await api.PatchNotes(UiLanguage())).Objs("notes").ToList();
                    // kept on this PC: the next start shows them at once (SendCachedNotes), before the server answers
                    if (serverNotes.Count > 0)
                        try { File.WriteAllText(NotesCachePath, Json.Write(new Dictionary<string, object> { ["notes"] = serverNotes.Cast<object>().ToList() })); } catch (Exception) { }
                }
                catch (Exception e) { if (serverNotes == null) Log.Write("patch notes from the server: " + e.Message); }
            }
            var notes = serverNotes != null && serverNotes.Count > 0 ? serverNotes : updater?.Manifest?.Objs("notes").ToList();
            if (notes == null || notes.Count == 0) return;
            var key = Json.Write(notes);
            if (key == notesSent) return;
            notesSent = key;
            var newest = notes[0].Str("version");
            State(new Dictionary<string, object> { ["notes"] = new Dictionary<string, object> {
                ["releases"] = notes.Cast<object>().ToList(), ["unseen"] = newest != settings.NotesSeen } });
            if (newest != settings.NotesSeen) { settings.NotesSeen = newest; settings.Save(); }
        }

        void SendUpdateState(bool checking = false, bool error = false)
        {
            State(new Dictionary<string, object> { ["update"] = new Dictionary<string, object> {
                ["checking"] = checking, ["error"] = error, ["launcher"] = launcherUpdate, ["launcherUpdating"] = updatingLauncher, ["version"] = manifestVersion,
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
                ClearBan();
                await ApplyTestChannel(me, false);   // before EnterHome's update check, which reads it
                await EnterHome();
            }
            catch (ApiException e) when (e.Status == 401 || e.Status == 403)
            {
                settings.Token = null; settings.Save(); api.Token = null;
                ShowAuth(e.Status == 403 ? AuthMessage(e) : "Your session expired, please sign in again.");
            }
            catch (Exception e) { ShowAuth("Server unreachable: " + e.Message); }
            await server;
        }

        void Remember(Dictionary<string, object> account)
        {
            settings.Username = account.Str("username");
            settings.SteamId = account.Str("steamId");
            if (account.ContainsKey("discord")) discordName = account.Str("discord");
            if (account.ContainsKey("deletion")) { deletionStatus = account.Obj("deletion")?.Str("status"); SendDeletion(); }
            var relay = account.Str("relayHost");
            if (relay != null && System.Text.RegularExpressions.Regex.IsMatch(relay, @"^\d{1,3}(\.\d{1,3}){3}$")) settings.RelayHost = relay;
            if (account.ContainsKey("relays"))
                relays = account.Objs("relays").Where(r => r.Str("id") != null && r.Str("host") != null)
                    .Select(r => new RelayPings.Relay { Id = r.Str("id"), Name = r.Str("name"), Host = r.Str("host"), Port = r.ContainsKey("agentPort") ? (int)r.Long("agentPort") : 7790 })
                    .ToList();
            settings.Save();
        }

        void ShowAuth(string error)
        {
            signedIn = false;
            State(new Dictionary<string, object> { ["view"] = "auth", ["authError"] = error, ["username"] = settings.Username ?? "", ["dcSignIn"] = "idle" });
        }

        /// <summary>Email is new (1.22): told once per account and PC, when the server has email and the account none yet.
        /// Not on a launcher about to update (the new one tells it).</summary>
        void AnnounceEmail()
        {
            var me = lastMe;
            if (me == null || launcherUpdate != null || !signedIn) return;
            var who = (settings.Username ?? "").ToLowerInvariant();
            var told = (settings.EmailPrompted ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (!me.Bool("emailEnabled") || me.Str("email") != null || me.Str("emailPending") != null || who.Length == 0 || told.Contains(who)) return;
            settings.EmailPrompted = string.Join(",", told.Concat(new[] { who }));
            settings.Save();
            State(new Dictionary<string, object> { ["emailPrompt"] = true });
        }

        // ---- getting an account back: Discord sign-in (no password), a forgotten password, email and password in Settings
        /// <summary>Settings › Account: the confirmed email, the one waiting for its link, whether there is a password.</summary>
        Dictionary<string, object> AccountState(Dictionary<string, object> me) => new Dictionary<string, object> {
            ["email"] = me.Str("email"), ["emailPending"] = me.Str("emailPending"), ["hasPassword"] = !me.ContainsKey("hasPassword") || me.Bool("hasPassword"),
            ["emailEnabled"] = me.ContainsKey("emailEnabled") && me.Bool("emailEnabled"), ["fresh"] = DateTime.UtcNow < freshUntil,
            ["usernameNext"] = me.Str("usernameNext"), ["progressMaxedAt"] = me.Str("progressMaxedAt") };

        // ---- Settings › Progress: the player maxes out their progress, once (account level, every character at level 10, the
        // 1,000-coin characters; no skin): the server makes the new profile, the game gets it at the next PLAY
        async Task MaxProgress()
        {
            var done = new Dictionary<string, object> { ["type"] = "progressMaxed", ["ok"] = false };
            // the game must be closed: a running game would keep its old save (the user, 2026-10-29)
            if (playing || GameInstall.IsRunning())
            {
                Toast("warn", "Close the game first", "Propnight is running: close it, then max out your progress.", 8000);
                done["running"] = true;
                State(done);
                return;
            }
            try
            {
                var r = await api.MaxProgress();
                done["ok"] = true;
                done["at"] = r.Str("at");
                Log.Write("progress maxed out");
                Toast("good", "Progress maxed out", "Press PLAY: your levels and characters are in the game.", 8000);
            }
            catch (ApiException e) { Toast("bad", "Not done", Capital(e.Message) + ".", 8000); }
            catch (Exception e) { Log.Write("max progress failed: " + e.Message); Toast("bad", "Not done", "The server could not be reached. Try again later.", 8000); }
            State(done);
        }

        async Task RefreshAccount()
        {
            try { State(new Dictionary<string, object> { ["account"] = AccountState(await api.Me()) }); }
            catch (Exception e) { Log.Write("account refresh failed: " + e.Message); }
        }

        /// <summary>Signs in with Discord: the browser asks Discord, the launcher waits for the server's answer (2 s steps,
        /// 10 min at most): signed in, a username to choose (a Discord with no account yet), or an error.</summary>
        async Task DiscordSignIn()
        {
            var generation = ++dcGeneration;
            try
            {
                var start = await api.DiscordLogin();
                var url = start.Str("url");
                if (url == null || !url.StartsWith("https://discord.com/")) throw new Exception("unexpected answer from the server");
                dcLoginId = start.Str("id");
                // the code the browser asks when it is not on this PC's address (server 2026-11-01: a sign-in link sent to
                // someone else no longer signs the sender in)
                State(new Dictionary<string, object> { ["dcSignIn"] = "waiting", ["dcCode"] = start.Str("code") ?? "", ["authError"] = "" });
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                for (var i = 0; i < 300 && generation == dcGeneration; i++)
                {
                    await Task.Delay(2000);
                    if (generation != dcGeneration) return;
                    var r = await api.DiscordLoginPoll(dcLoginId);
                    var status = r.Str("status");
                    if (status == "done") { await DiscordSignedIn(r); return; }
                    if (status == "username")
                    {
                        State(new Dictionary<string, object> { ["dcSignIn"] = "username", ["dcSuggestion"] = r.Str("suggestion") ?? "", ["dcDiscord"] = r.Str("discord") ?? "" });
                        return;
                    }
                    if (status == "error")
                    {
                        dcLoginId = null;
                        State(new Dictionary<string, object> { ["dcSignIn"] = "idle", ["authError"] = r.Str("message") ?? "Discord sign-in failed." });
                        return;
                    }
                }
                if (generation == dcGeneration) { dcLoginId = null; State(new Dictionary<string, object> { ["dcSignIn"] = "idle", ["authError"] = "Discord sign-in timed out: try again." }); }
            }
            catch (Exception e)
            {
                if (generation != dcGeneration) return;
                dcLoginId = null;
                Log.Write("discord sign-in: " + e.Message);
                State(new Dictionary<string, object> { ["dcSignIn"] = "idle", ["authError"] = e is ApiException ? e.Message : "Could not reach the server: " + e.Message });
            }
        }

        /// <summary>A new account for a Discord linked to none: the username the player chose.</summary>
        async Task DiscordRegister(string username)
        {
            if (dcLoginId == null) { State(new Dictionary<string, object> { ["dcSignIn"] = "idle", ["authError"] = "This sign-in expired: start again." }); return; }
            try { await DiscordSignedIn(await api.DiscordLoginRegister(dcLoginId, (username ?? "").Trim())); }
            catch (ApiException e) when (e.Status == 400 && e.Message.Contains("expired"))
            { dcLoginId = null; State(new Dictionary<string, object> { ["dcSignIn"] = "idle", ["authError"] = e.Message }); }
            catch (Exception e) { State(new Dictionary<string, object> { ["dcError"] = e is ApiException ? e.Message : "Could not reach the server: " + e.Message }); }
        }

        async Task DiscordSignedIn(Dictionary<string, object> r)
        {
            dcLoginId = null;
            settings.Token = api.Token = r.Str("token");
            Remember(r.Obj("account"));
            freshUntil = DateTime.UtcNow.AddMinutes(25);
            State(new Dictionary<string, object> { ["dcSignIn"] = "idle" });
            Toast("good", "Signed in with Discord", "Welcome, " + settings.Username + "!");
            ClearBan();
            await EnterHome();
        }

        async Task ForgotPassword(string login)
        {
            try
            {
                await api.Forgot((login ?? "").Trim());
                State(new Dictionary<string, object> { ["forgot"] = new Dictionary<string, object> { ["ok"] = true,
                    ["text"] = "If this account has a confirmed email, a link is on its way: check your inbox (and spam). It works for 1 hour." } });
            }
            catch (Exception e)
            {
                State(new Dictionary<string, object> { ["forgot"] = new Dictionary<string, object> { ["ok"] = false,
                    ["text"] = e is ApiException ? e.Message : "Could not reach the server: " + e.Message } });
            }
        }

        /// <summary>An email for the account: a 6-digit code is sent to it ('' removes the email).</summary>
        async Task SetEmail(string email)
        {
            try
            {
                var r = await api.SetEmail((email ?? "").Trim());
                var pending = r.Str("pending");
                if (pending == null) { Toast("good", "Email removed", "Your account has no email anymore."); await RefreshAccount(); return; }
                State(new Dictionary<string, object> { ["accountForm"] = new Dictionary<string, object> { ["form"] = "email", ["ok"] = true, ["pending"] = pending } });
            }
            catch (Exception e)
            {
                State(new Dictionary<string, object> { ["accountForm"] = new Dictionary<string, object> { ["form"] = "email", ["ok"] = false,
                    ["text"] = e is ApiException ? e.Message : "Could not reach the server: " + e.Message } });
            }
        }

        /// <summary>The code from the email: the address becomes the account's.</summary>
        async Task ConfirmEmail(string code)
        {
            try
            {
                var r = await api.ConfirmEmail((code ?? "").Trim());
                Toast("good", "Email confirmed", "If you forget your password, the launcher can send you a link at " + r.Str("email") + ".", 8000);
                State(new Dictionary<string, object> { ["accountForm"] = new Dictionary<string, object> { ["form"] = "emailCode", ["ok"] = true, ["email"] = r.Str("email") } });
                await RefreshAccount();
            }
            catch (Exception e)
            {
                State(new Dictionary<string, object> { ["accountForm"] = new Dictionary<string, object> { ["form"] = "emailCode", ["ok"] = false,
                    ["text"] = e is ApiException ? e.Message : "Could not reach the server: " + e.Message } });
            }
        }

        // the game takes its name when it starts (force_account_name.txt): not while it runs
        async Task SetUsername(string username)
        {
            void Fail(string text) => State(new Dictionary<string, object> { ["accountForm"] = new Dictionary<string, object> { ["form"] = "username", ["ok"] = false, ["text"] = text } });
            if (GameInstall.IsRunning()) { Fail("Close the game first: it takes your name when it starts."); return; }
            try
            {
                var r = await api.SetUsername(username);
                settings.Username = r.Str("username") ?? username;
                settings.Save();
                State(new Dictionary<string, object> { ["username"] = settings.Username,
                    ["accountForm"] = new Dictionary<string, object> { ["form"] = "username", ["ok"] = true } });
                Toast("good", "Username changed", "You are now " + settings.Username + ". Your friends see it right away.");
                await RefreshAccount();
            }
            catch (Exception e) { Fail(e is ApiException ? e.Message : "Could not reach the server: " + e.Message); }
        }

        async Task SetPassword(string current, string password)
        {
            try
            {
                await api.SetPassword(current, password);
                freshUntil = DateTime.MinValue;
                Toast("good", "Password saved", "Your other launchers were signed out.");
                State(new Dictionary<string, object> { ["accountForm"] = new Dictionary<string, object> { ["form"] = "password", ["ok"] = true } });
                await RefreshAccount();
            }
            catch (Exception e)
            {
                State(new Dictionary<string, object> { ["accountForm"] = new Dictionary<string, object> { ["form"] = "password", ["ok"] = false,
                    ["text"] = e is ApiException ? e.Message : "Could not reach the server: " + e.Message } });
            }
        }

        /// <summary>The update channel shown in the banner: "" (public), "diag" or "beta".</summary>
        string ChannelName() =>
            Config.IsDiagCode(settings.BetaCode) ? "diag" : Config.IsBetaCode(settings.BetaCode) ? "beta" : "";

        async Task EnterHome()
        {
            signedIn = true;
            RegisterLinkScheme();
            SendChannelState();
            State(new Dictionary<string, object> { ["beta"] = Config.IsBetaCode(settings.BetaCode), ["channel"] = ChannelName(), ["discordActivity"] = settings.DiscordActivity, ["dx11"] = settings.DirectX11, ["minimizeOnPlay"] = settings.MinimizeOnPlay, ["language"] = settings.Language ?? "auto" });
            State(new Dictionary<string, object> { ["view"] = "home", ["username"] = settings.Username, ["steamId"] = settings.SteamId, ["gameRoot"] = settings.GameRoot ?? "",
                ["discord"] = discordName ?? "" });
            SendCachedNotes();
            ShowIdleButton();
            updateTimer.Start();
            friendsTimer.Start();
            var _ = RefreshFriends();
            var _loop = FriendsLoop(++friendsLoop);
            var _link = HandleLink();   // a Join link that started the launcher
            var discordRefresh = RefreshDiscord();
            await RefreshMaintenance();
            await CheckUpdates(quiet: false);
            await discordRefresh;
            AnnounceEmail();
            // a launcher update that could not replace this exe leaves the new one behind (see InstallLauncherUpdate)
            var fresh = Path.Combine(Config.DataDir, "PropnightRevival.new.exe");
            if (launcherUpdate != null && File.Exists(fresh) && DateTime.UtcNow - File.GetLastWriteTimeUtc(fresh) > TimeSpan.FromSeconds(20))
            {
                Log.Write("update: previous launcher update left " + fresh);
                Toast("warn", "The last launcher update could not install",
                    "Windows or your antivirus kept the launcher file locked. Quit the launcher (tray icon > Quit), then download it again from propnightrevival.online, or move it to another folder (not Program Files).", 20000);
            }
        }

        // ---- Discord (Settings): the server sends back a Discord sign-in page; once accepted the
        // bot gives the Verified role. The launcher watches the account until the link shows up.
        async Task RefreshDiscord()
        {
            try
            {
                var me = await api.Me();
                discordName = me.Str("discord");
                State(new Dictionary<string, object> { ["discord"] = discordName ?? "", ["account"] = AccountState(me) });
                lastMe = me;
                await ApplyTestChannel(me, true);   // another account signed in: its own channel
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
                var link = await api.DiscordLink();
                var url = link.Str("url");
                var linkCode = link.Str("code");   // asked by the browser when it is not on this PC's address
                if (url == null || !url.StartsWith("https://discord.com/")) throw new Exception("unexpected answer from the server");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                Toast("info", "Link Discord", "Accept in your browser, then come back here." + (string.IsNullOrEmpty(linkCode) ? "" : " If it asks for a code: " + linkCode), 20000);
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
        // anti-cheat: what this game saw of the other players in its match (the mod's Win64/revival-watch.txt, written every
        // 15 s in a match), sent when it changed; the server compares the witnesses of a game (anticheat watch)
        DateTime lastWatchWrite = DateTime.MinValue;
        // The host left the match (2026-10-30): once the mod's presence says the match is over (match=1 -> anything else), the
        // server is asked every 2 s for 40 s what the player got (GET /revival/hostleft: settled 8 s after the game closed);
        // a result is written for the mod (Win64/revival-hostleft.txt, read by hostleft.lua: the screen back in the menu)
        bool wasInMatch, wasHost;
        DateTime hostLeftUntil, hostLeftAskedAt, hostWarnUntil, hostWarnAskedAt;
        async Task CheckHostLeft(Dictionary<string, string> where)
        {
            var inMatch = where.TryGetValue("match", out var m) && m == "1";
            if (inMatch) { wasInMatch = true; wasHost = where.TryGetValue("role", out var r0) && r0 == "host"; return; }
            if (wasInMatch)
            {
                wasInMatch = false;
                hostLeftUntil = DateTime.UtcNow.AddSeconds(40);
                if (wasHost) hostWarnUntil = DateTime.UtcNow.AddSeconds(30);   // a host who left its match: its warning, once
            }
            // every second: the warning shows in the menu as the game's own "left a match" message does (2026-10-30: 3 s)
            if (DateTime.UtcNow < hostWarnUntil && DateTime.UtcNow - hostWarnAskedAt >= TimeSpan.FromSeconds(1))
            {
                hostWarnAskedAt = DateTime.UtcNow;
                if (await CheckHostWarning()) hostWarnUntil = DateTime.MinValue;
            }
            if (DateTime.UtcNow > hostLeftUntil || DateTime.UtcNow - hostLeftAskedAt < TimeSpan.FromSeconds(2)) return;
            hostLeftAskedAt = DateTime.UtcNow;
            try
            {
                var r = (await api.HostLeft()).Obj("result");
                if (r == null) return;
                hostLeftUntil = DateTime.MinValue;
                var players = string.Join(",", r.Objs("players").Select(p => $"{p.Str("name")}:{p.Str("role")}"));
                var lines = new[] { "kind=" + r.Str("kind"), "mmr=" + r.Long("mmr"), "coins=" + r.Long("coins"), "win=" + (r.Bool("win") ? "1" : "0"),
                    "map=" + r.Str("map"), "secs=" + r.Long("secs"), "host=" + r.Str("host"),
                    "hostleaves=" + (r.ContainsKey("hostLeaves") ? r.Long("hostLeaves") : 0), "players=" + players,
                    "rpnow=" + (r.ContainsKey("rpNow") && r["rpNow"] != null ? r.Long("rpNow").ToString() : "") };
                File.WriteAllLines(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-hostleft.txt"), lines);
                Log.Write($"host left: {r.Str("kind")} +{r.Long("mmr")} MMR +{r.Long("coins")} coins, told to the game");
                await api.HostLeftSeen(r.Str("game"));
            }
            catch (Exception e) { Log.Write("host left: " + e.Message); }
        }

        // A host who left a match in progress (counted: 2 minutes, more than 2 players): warned once in the game, with how many
        // times in the last 30 days (Win64/revival-hostwarning.txt, shown by the mod in the menu: hostleft.lua). Asked at PLAY
        // (the game closed in a match last time, Alt+F4) and after its hosted match ended.
        async Task<bool> CheckHostWarning()
        {
            try
            {
                var w = (await api.HostWarning()).Obj("warning");
                if (w == null) return false;
                var until = w.Str("restrictedUntil");
                File.WriteAllLines(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-hostwarning.txt"), new[] { "count=" + w.Long("count"),
                    "map=" + w.Str("map"), "others=" + (w.ContainsKey("others") ? w.Long("others") : 0),
                    "until=" + (string.IsNullOrEmpty(until) ? "" : until.Substring(0, Math.Min(10, until.Length))) });
                Log.Write($"host warning: left {w.Long("count")} match(es) in progress, told to the game");
                await api.HostWarned(w.Str("game"));
                return true;
            }
            catch (Exception e) { Log.Write("host warning: " + e.Message); return false; }
        }

        // anti-cheat: a memory editor opened during a game started by PLAY, checked every 10 s, each one reported once a game
        readonly HashSet<string> toolsReported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTime toolsCheckedAt;
        List<string> modsAllowedNow;   // public release: the mods that may load (a Lua mod added during the game is reported)
        async Task CheckTools()
        {
            if (DateTime.UtcNow - toolsCheckedAt < TimeSpan.FromSeconds(10)) return;
            toolsCheckedAt = DateTime.UtcNow;
            var allowed = modsAllowedNow;
            var fresh = (await Task.Run(() => CheatTools.Open().Concat(GameInstall.ForeignMods(settings.GameRoot, allowed).Select(m => "Lua mod " + m)).ToList()))
                .Where(t => toolsReported.Add(t)).ToList();
            if (fresh.Count == 0) return;
            Log.Write("cheat tool opened during the game: " + string.Join(", ", fresh));
            await ReportTool(fresh, "game");
            // a memory editor (not a Lua mod added during the game: UE4SS loads the mods at the start only): the game is
            // closed, as PLAY would not have started it
            var editors = fresh.Where(t => !t.StartsWith("Lua mod ")).ToList();
            if (editors.Count == 0) return;
            var closed = await Task.Run(() => GameInstall.CloseGame());
            Log.Write($"game closed ({closed} process(es)): {string.Join(", ", editors)} open during the game");
            Toast("error", "Propnight was closed", $"{string.Join(", ", editors)} is open. Close it to play.", 9000);
        }

        async Task ReportTool(List<string> tools, string stage)
        {
            try { await api.Tool(tools, stage); }
            catch (Exception e) { Log.Write("tool report: " + e.Message); }
        }

        /// <summary>Anti-cheat: game files found changed on this PC (paths of the manifest), for the moderators.</summary>
        async Task ReportTamper(List<string> files, string stage)
        {
            try { await api.Tamper(files.Select(f => f.Replace("Propnight/Binaries/Win64/", "")).ToList(), stage); }
            catch (Exception e) { Log.Write("tamper report: " + e.Message); }
        }

        async Task PushWatch()
        {
            try
            {
                var path = Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-watch.txt");
                if (!File.Exists(path)) return;
                var at = File.GetLastWriteTimeUtc(path);
                if (at == lastWatchWrite) return;
                lastWatchWrite = at;
                var text = File.ReadAllText(path);
                if (text.Length > 0 && text.Length < 65536) await api.Watch(text);
            }
            catch (ApiException e) when (e.Status == 401 || e.Status == 403 || e.Status == 404) { }
            catch (Exception e) { Log.Write("watch: " + e.Message); }
        }

        async Task PushPresence(bool force = false)
        {
            if (!signedIn || pushingPresence) return;
            pushingPresence = true;
            try
            {
                var running = GameInstall.RunningCount();
                var inGame = playing || running > 0;
                // the game PLAY started, alone: a Propnight started outside the launcher (Steam, the exe) had no file
                // check, sends no profile nor match (account 189, 2026-10-27): the server's launch guard refuses it
                var launched = playing && running == 1;
                var where = inGame && GameInstall.IsGameRoot(settings.GameRoot) ? GameInstall.Presence(settings.GameRoot) : new Dictionary<string, string>();
                where.TryGetValue("role", out var role);
                where.TryGetValue("server", out var server);
                role = role ?? "menu";
                var build = inGame ? settings.GameBuild : null;
                // the diagnostic channel plays with the test channel, apart from the public (server: lists, quick play, relay);
                // the server tells them apart for the admin only
                var channel = Config.IsDiagCode(settings.BetaCode) ? "diag" : Config.IsBetaCode(settings.BetaCode) ? "beta" : "public";
                var classic = ClassicPacks();
                var presenceKey = $"{inGame}|{launched}|{role}|{server}|{build}|{channel}|{string.Join(",", classic)}";
                if (force || presenceKey != lastPresenceKey || DateTime.UtcNow - lastPresence > TimeSpan.FromSeconds(30))
                {
                    await api.Presence(inGame, launched, role, role == "client" ? server : null, build, channel, classic, relayPings);
                    lastPresence = DateTime.UtcNow;
                    lastPresenceKey = presenceKey;
                }
                if (inGame) await PushWatch();
                if (playing) await CheckTools();
                if (playing) await CheckHostLeft(where);
            }
            catch (ApiException e) when (e.Status == 401 || e.Status == 403) { }
            catch (Exception e) { Log.Write("presence: " + e.Message); }
            finally { pushingPresence = false; }
        }

        /// <summary>
        /// This PC's round trip to each region's relay, measured at sign-in and every 2 minutes (a few UDP packets): the
        /// server picks the host's relay, the servers tab shows each game's ping and quick play joins the lowest one.
        /// </summary>
        // a network change (a VPN switched on or off, Wi-Fi to cable): measured again at once and sent, not 2 minutes later
        // (2026-10-29: a US VPN kept the European 39 ms; the server also forgets the pings of an older address)
        bool networkChanged;
        void WatchNetwork() => System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (s, e) =>
        {
            networkChanged = true;
            relayPingsAt = DateTime.MinValue;
        };

        async Task MeasurePings()
        {
            if (!signedIn || measuringPings || relays.Count == 0 || DateTime.UtcNow - relayPingsAt < TimeSpan.FromMinutes(2)) return;
            measuringPings = true;
            try
            {
                var first = relayPings == null || networkChanged;
                networkChanged = false;
                relayPings = await RelayPings.Measure(relays);
                relayPingsAt = DateTime.UtcNow;
                Log.Write("relay pings: " + string.Join(", ", relays.Select(r => r.Id + " " + (relayPings.TryGetValue(r.Id, out var ms) ? ms + " ms" : "no answer"))));
                if (first) await PushPresence(true);
            }
            catch (Exception e) { Log.Write("relay pings: " + e.Message); relayPingsAt = DateTime.UtcNow; }
            finally { measuringPings = false; }
        }

        async Task RefreshServers()
        {
            if (!signedIn) return;
            try
            {
                var reply = await api.Servers();
                var servers = reply.Objs("servers").ToList();
                foreach (var s in servers) RememberBuild(s);
                // live numbers (every game, private ones only counted): shown above the list
                State(new Dictionary<string, object> { ["servers"] = servers, ["serverStats"] = reply.Obj("stats") });
            }
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
            if (addressProof == null || addressProofPort <= 0 || (settings.RelayHost == null && relays.Count == 0)) return;
            if (!now && DateTime.UtcNow - addressProofSent < TimeSpan.FromSeconds(20)) return;
            addressProofSent = DateTime.UtcNow;
            try
            {
                var bytes = new byte[17];
                bytes[0] = (byte)'P';
                for (int i = 0; i < 16; i++) bytes[i + 1] = Convert.ToByte(addressProof.Substring(i * 2, 2), 16);
                // every region's relay: a friend's game may be on any of them
                var targets = relays.Select(r => new { r.Host, r.Port }).ToList();
                if (targets.Count == 0) targets.Add(new { Host = settings.RelayHost, Port = addressProofPort });
                using (var udp = new System.Net.Sockets.UdpClient(System.Net.Sockets.AddressFamily.InterNetwork))
                    foreach (var t in targets)
                        for (int i = 0; i < 2; i++) udp.Send(bytes, bytes.Length, t.Host, t.Port);
            }
            catch (Exception e) { Log.Write("address proof: " + e.Message); }
        }

        /// <summary>
        /// Test channel (1.56): the admin gives an account beta or diag (server channel.mjs); GET /revival/account/me says
        /// which (testChannel {channel, code}: the code the release script published for it), read at start and at each
        /// sign-in (no request of its own). Given one, Settings shows a Test version switch: on, the channel's code picks
        /// the updates (updates/beta-<code>/) and the token at PLAY, as the code typed by hand did before; none, or switched
        /// off, the public release (a PC still on a channel goes back to it). A server before 1.56 says nothing: unchanged.
        /// </summary>
        Dictionary<string, object> testChannel;   // the account's {channel, code}; null: none, or not known yet
        async Task ApplyTestChannel(Dictionary<string, object> me, bool recheck)
        {
            if (me != null)
            {
                if (!me.ContainsKey("testChannel")) return;
                testChannel = me.Obj("testChannel");
            }
            var code = testChannel?.Str("channel") != null ? testChannel.Str("code") : null;
            var want = settings.TestVersion && Config.IsBetaCode(code) ? code.Trim() : null;
            if ((want ?? "") == (settings.BetaCode ?? "").Trim()) { SendChannelState(); return; }
            var was = Config.IsBetaCode(settings.BetaCode);
            settings.BetaCode = want;
            if (want != null) settings.LeftBeta = false;
            else if (was) settings.LeftBeta = true;   // back on the public release: its launcher is installed even if older
            settings.Save();
            Log.Write("update channel: " + (ChannelName() == "" ? "public" : ChannelName()));
            SendChannelState();
            if (want != null) Toast("good", "Test version on", "This PC now gets the test version: press PLAY to update.");
            else if (was) Toast("good", "Test version off", "Back to the public version: press PLAY to update.");
            updater = null;                  // the next check reads the new channel
            if (recheck) await CheckUpdates(true);
        }

        /// <summary>The channel for the page: the banner (channel), the Test version switch (shown with testAccess).</summary>
        void SendChannelState() => State(new Dictionary<string, object> { ["beta"] = Config.IsBetaCode(settings.BetaCode), ["channel"] = ChannelName(),
            ["testAccess"] = testChannel?.Str("channel") != null && Config.IsBetaCode(testChannel.Str("code")) ? testChannel.Str("channel") : "",
            ["testOn"] = settings.TestVersion });

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
        /// Win64\revival-social-actions.txt: "invite NAME", "accept NAME", "decline NAME", "leave"; "joincheck PORT" before a join).
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
                if (parts[0] == "joincheck" && parts.Length > 1 && int.TryParse(parts[1], out var port)) { await AnswerJoinCheck(port); continue; }
                if (parts[0] == "redeem" && parts.Length > 1) { await AnswerRedeem(parts[1].Trim()); continue; }
                if (parts[0] == "left" && parts.Length > 1)
                {
                    var how = parts[1].Trim().Split(' ');
                    await ReportLeave(how[0], how.Length > 1 ? how[1] : "client");
                    continue;
                }
                if ((parts[0] == "qpjoin" || parts[0] == "qphost") && parts.Length > 1)
                {
                    try { await api.QuickPlayChoice(parts[1].Trim(), parts[0] == "qpjoin"); }
                    catch (Exception e) { Log.Write("quick play choice: " + e.Message); }
                    continue;
                }
                await SquadAction(parts[0].ToLowerInvariant(), parts.Length > 1 ? parts[1] : null);
            }
        }

        /// <summary>
        /// The mod asks right before it joins a game on the relay: a game in a match refuses newcomers at the relay, and the
        /// failed travel sent the client back to the intro, stuck on "Initializing matchmaking" (bug 72). Refused here, the
        /// game stays in its menu and shows the server's message. The server unreachable: "joinok", the relay still decides.
        /// </summary>
        async Task AnswerJoinCheck(int port)
        {
            var answer = "joinok " + port;
            try
            {
                var r = await api.JoinCheck(port);
                if (!r.Bool("ok"))
                {
                    var message = r.Str("message") ?? "This game has already started.";
                    answer = "joinrefused " + port + " " + message.Replace("\r", "").Replace("\n", "\\n");
                    Toast("warn", "Can't join this game", message.Replace("\n", " "), 9000);
                }
            }
            catch (ApiException e) when (e.BannedUntil != null)
            {
                // banned: no game to join (the server unreachable stays "joinok": the relay still decides)
                answer = "joinrefused " + port + " " + BanText(e.BannedUntil.Value, e.BanReason).Replace("\n", "\\n");
            }
            catch (Exception e) { Log.Write("join check: " + e.Message); }
            Log.Write("join check, port " + port + ": " + answer.Split(' ')[0]);
            try { File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-map-command.txt"), answer); }
            catch (IOException e) { Log.Write("join check answer: " + e.Message); }
        }

        /// <summary>
        /// A code typed in the game's menu (the mod: "redeem CODE"): the server checks it and gives its items to the profile;
        /// the mod gets "redeemok COINS KIND:ID,… LABEL; LABEL" (adds the items to the running game) or "redeemfail MESSAGE".
        /// </summary>
        async Task AnswerRedeem(string code)
        {
            code = new string(code.Where(char.IsDigit).Take(15).ToArray());
            string answer;
            try
            {
                var r = await api.Redeem(code);
                if (r.Bool("ok"))
                {
                    var items = r.Objs("items").ToList();
                    var coins = r.TryGetValue("coins", out var c) ? Convert.ToInt64(c) : 0;
                    var ids = items.Select(i => i.Str("kind") + ":" + i.Str("id")).ToList();
                    var labels = items.Select(i => (i.Str("label") ?? "").Replace(";", ",")).ToList();
                    if (coins > 0) labels.Add(coins + " coins");
                    answer = $"redeemok {coins} {(ids.Count > 0 ? string.Join(",", ids) : "-")} {string.Join("; ", labels)}";
                    Toast("good", "Code " + code, "You got " + string.Join(", ", labels) + ".", 9000);
                }
                else answer = "redeemfail " + (r.Str("message") ?? "This code does not work.");
            }
            catch (ApiException e) { answer = "redeemfail " + (e.BannedUntil != null ? "Your account is banned." : "The server did not answer: try again in a moment."); Log.Write("redeem: " + e.Message); }
            catch (Exception e) { answer = "redeemfail The server did not answer: try again in a moment."; Log.Write("redeem: " + e.Message); }
            Log.Write("redeem " + code + ": " + answer.Split(' ')[0]);
            try { File.AppendAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-map-command.txt"), answer.Replace("\r", "").Replace("\n", " ") + "\n"); }
            catch (IOException e) { Log.Write("redeem answer: " + e.Message); }
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
                // no toast: the invite is in the Friends card (SQUAD INVITES) and, game running, in the game's dialog (in the
                // menu) or the overlay's notification and card (in a match: no dialog over the match)
                if (running && OverlayTakesInvites())
                {
                    var from = Clean(invite.Str("from"));
                    if (settings.OverlayNotifyInvites)
                        overlay.Notify(from, "invites you to their squad", null, true, 8,
                            new Dictionary<string, object> { ["cmd"] = "squad", ["action"] = "accept", ["name"] = from, ["label"] = "Accept" },
                            new Dictionary<string, object> { ["cmd"] = "later", ["label"] = "Later" });
                    overlay.Push(false);
                }
                else if (running)
                    try { File.WriteAllText(Path.Combine(win64, "revival-map-command.txt"), "squadinvite " + Clean(invite.Str("from"))); }
                    catch (IOException e) { Log.Write("squad invite to game: " + e.Message); }
                else
                {
                    // the game closed: the launcher's own message (2026-11-01: an invite from a friend's overlay went unseen,
                    // only the Friends card had it), and the notification area's when the launcher is out of sight
                    var from = Clean(invite.Str("from"));
                    Toast("info", from + " invites you to their squad", "You will follow them into their games.", 30_000,
                        new Dictionary<string, object> { ["label"] = "Accept", ["cmd"] = "friend", ["action"] = "squadAccept", ["username"] = from });
                    if (!Visible || WindowState == FormWindowState.Minimized)
                        tray.ShowBalloonTip(10_000, from + " invites you to their squad", "Open the launcher to accept.", ToolTipIcon.Info);
                }
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
                    var _ = JoinFriend((int)follow.Long("port"), squad.Str("leader"), follow.Bool("dlc"), MapName(follow.Str("map")), follow.Str("host"));
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
            // members without the Classic maps (their launcher says so): the leader's game keeps those maps off
            // (a line of its own: an older mod ignores it); "dlc": the packs a member has ("2021,2022"; since the split)
            foreach (var m in squad.Objs("members"))
            {
                if (m.TryGetValue("classicMaps", out var has) && has is bool b && !b)
                    sb.Append("nodlc\t").Append(Clean(m.Str("username"))).Append('\n');
                if (m.ContainsKey("classicPacks"))
                    sb.Append("dlc\t").Append(Clean(m.Str("username"))).Append('\t').Append(string.Join(",", m.Strs("classicPacks"))).Append('\n');
            }
            var text = sb.ToString();
            if (text == lastSocial) return;
            try { File.WriteAllText(Path.Combine(win64, "revival-social.txt"), text); lastSocial = text; }
            catch (IOException e) { Log.Write("social file: " + e.Message); }
        }

        void RememberBuild(Dictionary<string, object> session)
        {
            if (session == null || !session.ContainsKey("port")) return;
            buildsByPort[(int)session.Long("port")] = session.Str("build");
        }

        /// <summary>
        /// Another build than the game this PC runs (or will run after PLAY's update): no join, and why, in the
        /// launcher and in the running game. True if blocked. Unknown builds (older launchers) never block.
        /// </summary>
        bool VersionBlocked(int port, string from)
        {
            buildsByPort.TryGetValue(port, out var theirs);
            var running = GameInstall.IsRunning();
            var latest = Updater.BuildOf(updater?.Manifest);
            var mine = running ? settings.GameBuild : latest;
            if (theirs == null || mine == null || theirs == mine) return false;
            var self = running && latest != null && mine != latest && theirs == latest;
            Log.Write($"join refused: {from} plays build {theirs}, this game {mine} (newest {latest})");
            if (self) Toast("warn", "Your game is out of date", $"{from ?? "Your friend"} plays the new version. Close Propnight and press PLAY to update, then join again.", 10_000);
            else Toast("warn", "Different version", $"{from ?? "Your friend"} plays another version of the game. They have to close Propnight and press PLAY in the launcher to update.", 10_000);
            if (running && GameInstall.IsGameRoot(settings.GameRoot))
                try { File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-map-command.txt"), $"outdated {(self ? "self" : "other")} {Clean(from)}"); }
                catch (IOException e) { Log.Write("version message to game: " + e.Message); }
            return true;
        }

        /// <summary>While the game runs: a newer build published for this channel is announced once in the game.</summary>
        async Task CheckWhilePlaying()
        {
            if (!GameInstall.IsRunning() || !GameInstall.IsGameRoot(settings.GameRoot) || settings.GameBuild == null) return;
            try
            {
                var check = new Updater(settings, (t, f) => { });
                await check.LoadManifest();
                updater = updater ?? check;
                if (updater != check && updater.Manifest != null) updater.Manifest = check.Manifest;
                var latest = Updater.BuildOf(check.Manifest);
                if (latest == null || latest == settings.GameBuild || latest == outdatedShown) return;
                outdatedShown = latest;
                Log.Write($"game update published while playing: {settings.GameBuild} -> {latest}");
                File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-map-command.txt"), "outdated update");
                Toast("info", "Game update available", "Close Propnight and press PLAY to install it.", 9000);
            }
            catch (Exception e) { Log.Write("update check while playing: " + e.Message); }
        }

        // ---- Discord activity (Rich Presence): launcher, menu, lobby or match, map and players; a Settings switch.
        static readonly Dictionary<string, string> MapImages = new Dictionary<string, string>
        {
            ["Abbey"] = "abbey", ["Farm (Night)"] = "farm-night", ["Circus"] = "circus", ["Fantasticville"] = "fantasticville", ["Egypt"] = "egypt",
            ["Camp"] = "camp", ["Dark Swamp"] = "dark-swamp", ["Kitchen"] = "kitchen", ["Goldhorn Mine"] = "golden-mine", ["House"] = "mansion",
            ["Summer Camp"] = "camp-2022", ["Farm (Day)"] = "farm-day", ["School"] = "school", ["Castle"] = "castle", ["Village"] = "village",
        };
        // a map's other versions ("House (Reborn)", "School (2022)") show its picture
        static bool MapImage(string map, out string image) =>
            MapImages.TryGetValue(map, out image) || MapImages.TryGetValue(System.Text.RegularExpressions.Regex.Replace(map ?? "", @" \((Reborn|2022)\)$", ""), out image);
        const string SiteUrl = "https://propnightrevival.online";
        const string DiscordInvite = "https://discord.gg/propnight-revival";   // the project's server (site footer)

        bool DiscordOn => signedIn && settings.DiscordActivity;

        void UpdateDiscord()
        {
            if (!DiscordOn)
            {
                if (activityShown) { activityShown = false; activityKey = null; var _ = discord.Set(null); }
                return;
            }
            activityShown = true;
            var running = playing || GameInstall.IsRunning();
            var you = lastFriendsView?.Obj("you");
            var session = running ? you?.Obj("session") : null;
            var assets = new Dictionary<string, object> { ["large_image"] = SiteUrl + "/img/discord-icon.png", ["large_text"] = "Propnight Revival" };
            var activity = new Dictionary<string, object> { ["assets"] = assets,
                ["buttons"] = new object[] { new Dictionary<string, object> { ["label"] = "Get Propnight Revival", ["url"] = SiteUrl } } };
            string key;
            if (!running) { activity["details"] = "In the launcher"; key = "launcher"; }
            else if (session == null) { activity["details"] = "In the menu"; key = "menu"; }
            else
            {
                var map = MapName(session.Str("map"));
                var match = session.Str("phase") == "match";
                activity["details"] = (match ? "In a match" : "In the lobby") + " · " + map;
                activity["state"] = (you.Bool("hosting") ? "Hosting a " : "In a ") + (session.Bool("public") ? "public" : "private") + " game";
                activity["party"] = new Dictionary<string, object> { ["id"] = session.Str("id"),
                    ["size"] = new[] { (int)Math.Max(1, Math.Min(5, session.Long("players"))), 5 } };
                if (MapImage(map, out var image))
                {
                    assets["large_image"] = SiteUrl + "/img/maps/" + image + ".jpg";
                    assets["large_text"] = map;
                    assets["small_image"] = SiteUrl + "/img/discord-icon.png";
                    assets["small_text"] = "Propnight Revival";
                }
                key = "game:" + session.Str("id") + ":" + (match ? "match" : "lobby");
            }
            if (key != activityKey) { activityKey = key; activitySince = DateTime.UtcNow; }
            activity["timestamps"] = new Dictionary<string, object> { ["start"] = (long)(activitySince - new DateTime(1970, 1, 1)).TotalSeconds };
            var __ = discord.Set(activity);
        }

        void ApplyFriends(Dictionary<string, object> f)
        {
            friendsEtag = f.Str("etag");
            lastFriendsView = f;
            foreach (var fr in f.Objs("friends")) RememberBuild(fr.Obj("session"));
            foreach (var inv in f.Objs("invites")) RememberBuild(inv.Obj("session"));
            RememberBuild(f.Obj("squad")?.Obj("follow"));
            f["inGame"] = playing || GameInstall.IsRunning();
            State(new Dictionary<string, object> { ["friends"] = f });
            CheckBugs(f);
            CheckPatchNotes(f);
            CheckWarnings();
            var friendsKey = string.Join(",", f.Objs("friends").Select(x => x.Str("username") + ":" + x.Str("status")))
                + " | in " + string.Join(",", f.Strs("incoming")) + " | out " + string.Join(",", f.Strs("outgoing"));
            if (friendsKey != lastFriendsKey) { Log.Write("friends: " + friendsKey); lastFriendsKey = friendsKey; }
            WriteGameVisibility(f.Obj("you")?.Obj("session"));
            overlayFriends = f.Objs("friends").ToList();
            OverlayFriendsSeen(f);
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
            // quick play found only games over 200 ms: the mod asks the player (JOIN / CREATE), the answer comes back
            // through its social actions (qpjoin / qphost)
            var offer = f.Obj("qpOffer");
            if (offer != null && offer.Str("id") != lastQpOffer && GameInstall.IsRunning() && GameInstall.IsGameRoot(settings.GameRoot))
            {
                lastQpOffer = offer.Str("id");
                var id = new string((offer.Str("id") ?? "").Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
                var map = new string(MapName(offer.Str("map")).Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '(' || c == ')').ToArray());
                try
                {
                    File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-map-command.txt"),
                        $"qpoffer {id} {offer.Long("ping")} {offer.Long("players")} {map}");
                    Log.Write($"quick play: far game offered ({offer.Long("ping")} ms)");
                }
                catch (IOException e) { Log.Write("quick play offer: " + e.Message); }
            }
            // match resume (server app.mjs): the survivor hosting this player's match left it; this game hosts the match again
            // (the server's snapshot of it in revival-resume.txt) or joins the player who does. The mod waits for its menu.
            var resume = f.Obj("resumeOrder");
            if (resume != null && resume.Str("id") + resume.Str("role") != lastResumeOrder && GameInstall.IsRunning() && GameInstall.IsGameRoot(settings.GameRoot))
            {
                lastResumeOrder = resume.Str("id") + resume.Str("role");
                var snap = new string((resume.Str("snap") ?? "").Where(c => char.IsLetterOrDigit(c) || "|,.=:_-".IndexOf(c) >= 0).ToArray());
                var map = new string((resume.Str("map") ?? "").Where(char.IsLetterOrDigit).ToArray());
                var host = resume.Str("host") ?? "";
                string command = null;
                if (resume.Str("role") == "host" && map.Length > 0)
                    command = $"resumehost {map} {(resume.Str("visibility") == "public" ? "public" : "private")}";
                else if (resume.Str("role") == "join" && System.Text.RegularExpressions.Regex.IsMatch(host, @"^\d{1,3}(\.\d{1,3}){3}$"))
                    command = $"resumejoin {host} {resume.Long("port")} {new string((resume.Str("hostName") ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray())}".TrimEnd();
                if (command != null)
                {
                    try
                    {
                        var win64 = GameInstall.Win64(settings.GameRoot);
                        File.WriteAllText(Path.Combine(win64, "revival-resume.txt"), snap);
                        File.WriteAllText(Path.Combine(win64, "revival-map-command.txt"), command);
                        if (resume.Str("role") == "host")
                            Toast("info", "The host left the match", $"You host it now on {MapName(map)}: the others are coming back.", 8000);
                        else
                            Toast("info", "The host left the match", "The match goes on at another player's game: joining it.", 8000);
                        Log.Write($"match resume: {command}");
                    }
                    catch (IOException e) { Log.Write("match resume: " + e.Message); }
                }
            }
            foreach (var invite in f.Objs("invites"))
            {
                var key = invite.Str("from") + "|" + invite.Str("at");
                if (!seenInvites.Add(key)) continue;
                var session = invite.Obj("session");
                Toast("info", invite.Str("from") + " invites you", $"{MapName(session.Str("map"))} · {session.Long("players")}/5 players", 30_000,
                    new Dictionary<string, object> { ["label"] = "Join", ["cmd"] = "join", ["port"] = session.Long("port"), ["from"] = invite.Str("from"),
                        ["dlc"] = session.Bool("dlc"), ["map"] = MapName(session.Str("map")) });
                if ((!Visible || WindowState == FormWindowState.Minimized) && !playing)
                    tray.ShowBalloonTip(10_000, invite.Str("from") + " invites you", "Open the launcher to join.", ToolTipIcon.Info);
                // the game is running: the overlay's notification (and its card, Shift + F1); the game's own dialog when the
                // overlay's invite notifications are off (in a match: only the card) or the overlay cannot show (exclusive fullscreen)
                var gameRunning = GameInstall.IsRunning() && GameInstall.IsGameRoot(settings.GameRoot);
                if (gameRunning && OverlayTakesInvites())
                {
                    if (VersionBlocked((int)session.Long("port"), invite.Str("from"))) continue;   // said in the game by VersionBlocked
                    overlayInvites[key] = new Dictionary<string, object> { ["from"] = invite.Str("from"), ["map"] = MapName(session.Str("map")),
                        ["players"] = session.Long("players"), ["port"] = session.Long("port"), ["dlc"] = session.Bool("dlc"), ["host"] = session.Str("host"), ["at"] = DateTime.UtcNow };
                    if (settings.OverlayNotifyInvites)
                        overlay.Notify(invite.Str("from"), "invites you", $"{MapName(session.Str("map"))} {session.Long("players")}/5", true, 8,
                            new Dictionary<string, object> { ["cmd"] = "joinInvite", ["key"] = key, ["label"] = "Join" },
                            new Dictionary<string, object> { ["cmd"] = "laterInvite", ["key"] = key, ["label"] = "Later" });
                    overlay.Push(false);
                }
                else if (gameRunning && !VersionBlocked((int)session.Long("port"), invite.Str("from")))
                {
                    var name = new string((invite.Str("from") ?? "").Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
                    var map = new string(MapName(session.Str("map")).Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '(' || c == ')').ToArray());
                    try
                    {
                        File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-map-command.txt"),
                            $"invite {session.Long("port")} {(session.Bool("dlc") ? (ClassicPackOf(session.Str("map")) ?? "1") : "0")} {session.Long("players")} {name}|{map}{(IsIPv4(session.Str("host")) ? "|" + session.Str("host") : "")}");
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

        // ---- verify game files (Settings): every revival file read again (no cached hash) and the wrong or missing ones
        // downloaded, like Steam's "Verify integrity" (PLAY already fixes a missing file or one whose size or date changed)
        async Task VerifyFiles()
        {
            if (busy || playing || GameInstall.IsRunning()) { Toast("warn", "Not now", "Close the game and wait for the current task to finish."); return; }
            if (!GameInstall.IsGameRoot(settings.GameRoot)) { PickFolder(); return; }
            busy = true;
            try
            {
                var work = new Updater(settings, (text, f) => PlayButton("update", "VERIFYING", text, f, true, true));
                await work.LoadManifest();
                if (work.LauncherUpdate() != null) { updater = work; launcherUpdate = work.LauncherUpdate(); SendUpdateState(); Toast("warn", "Update required", "Install the new launcher first: press UPDATE."); return; }
                settings.ForgetHashes(settings.GameRoot);
                var plan = await work.Plan(settings.GameRoot);
                var total = work.Manifest.Objs("files").Count(work.Wanted);
                await UpdateAndClean(work, plan);
                gamePlan = new List<Dictionary<string, object>>();
                exePatchPending = false;
                manifestVersion = work.Manifest.Str("version");
                Log.Write($"verify: {total} file(s) checked, {plan.Count} repaired" + (plan.Count > 0 ? ": " + string.Join(", ", plan.Select(f => f.Str("path"))) : ""));
                if (plan.Count == 0) Toast("good", "Game files OK", $"All {total} revival files are intact.", 7000);
                else Toast("good", "Game files repaired", $"{plan.Count} of {total} file(s) were missing or damaged and were downloaded again.", 9000);
            }
            catch (Exception e) { Log.Write("verify failed: " + e.Message); Toast("bad", "Verify failed", e.Message, 9000); }
            finally { busy = false; SendUpdateState(); ShowIdleButton(); }
        }

        // ---- account deletion (Settings): asked to the team, who anonymize the account (privacy policy); cancel while it waits
        void SendDeletion() => State(new Dictionary<string, object> { ["type"] = "deletion", ["status"] = deletionStatus });

        async Task AccountDeletion(bool ask)
        {
            try
            {
                deletionStatus = (ask ? await api.RequestDeletion() : await api.CancelDeletion()).Obj("deletion")?.Str("status");
                if (ask) Toast("good", "Deletion requested", "The team will delete your account within a month. Until then you can still play, or cancel it in Settings.", 10000);
                else Toast("good", "Request cancelled", "Your account stays as it is.", 6000);
            }
            catch (ApiException e) { Toast("bad", "Not sent", e.Message, 8000); }
            catch (Exception e) { Log.Write("account deletion: " + e.Message); Toast("bad", "Not sent", "The server could not be reached. Try again later.", 8000); }
            SendDeletion();
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

        // An update's files, then the files of older releases the manifest dropped (the one classic maps DLC's paks, the
        // yearly packs' hold the same paths: never loaded together): removed after the new ones are in, or first when the
        // disk lacks room for both
        async Task UpdateAndClean(Updater work, List<Dictionary<string, object>> plan)
        {
            work.RemoveObsolete(settings.GameRoot, plan.Sum(f => f.Long("size")), onlyIfShort: true);
            await work.UpdateGame(settings.GameRoot, plan);
            work.RemoveObsolete(settings.GameRoot);
        }

        /// <summary>The map pack of a map, by its key or its name ("2021" / "2022" / "2023", "rb": the Reborn Beta maps), null when not known.</summary>
        static string ClassicPackOf(string map)
        {
            if (string.IsNullOrEmpty(map)) return null;
            switch (map)
            {
                case "Abbey2021": case "Camp2021": case "House2021": return "2021";
                case "Farm": case "Castle": case "Swamp": case "School": case "MansionReborn": case "House": case "Village": return "2022";
                case "SchoolMerged": case "RebornNewMansion": return "2023";
                case "CampPreReborn": case "FarmPreReborn": case "FarmPreRebornDay": return "rb";
            }
            if (map.Contains("(Reborn Beta")) return "rb";
            if (map.Contains("(2021)")) return "2021";
            if (map.Contains("(Reborn)")) return "2023";
            if (map.Contains("(2022)") || map.Contains("(Day)") || map.Contains("(day)")) return "2022";
            return null;
        }

        /// <summary>The map packs turned on, as the server and the mod name them ("2021", "2022", "2023"; "rb": the Reborn Beta
        /// maps, DLC "reborn-beta", 2026-11-03).</summary>
        List<string> ClassicPacks() => new[] { "2021", "2022", "2023" }.Where(y => settings.DlcOn("classic-" + y))
            .Concat(settings.DlcOn("reborn-beta") ? new[] { "rb" } : new string[0]).ToList();

        async Task SetDlc(string id, bool on)
        {
            if (updater?.Manifest == null || !updater.Manifest.Objs("dlc").Any(p => p.Str("id") == id)) return;
            if (on) { await QueueDlc(new List<string> { id }); return; }
            if (busy || playing || GameInstall.IsRunning()) { Toast("warn", "Not now", "Close the game and wait for the current task to finish."); SendDlcState(); return; }
            if (!GameInstall.IsGameRoot(settings.GameRoot)) { PickFolder(); SendDlcState(); return; }
            var title = updater.Manifest.Objs("dlc").First(p => p.Str("id") == id).Str("title");
            settings.Dlc[id] = false;
            settings.Save();
            try { updater.RemoveDlc(settings.GameRoot, id); Toast("good", title + " removed", "You can install them again at any time."); }
            catch (Exception e) { Toast("bad", "Could not remove " + title, e.Message); }
            SendDlcState();
            await CheckUpdates(quiet: true);
        }

        // The packs to download, one after the other (Install all, or Install while another pack downloads, 2026-10-30): the
        // DLC tab shows the one downloading and the ones after it; "Cancel the rest" empties the queue (the pack downloading
        // finishes: the updater has no stop)
        readonly List<string> dlcQueue = new List<string>();
        string dlcCurrent;
        double dlcProgress = -1;
        DateTime dlcSentAt;

        void SendDlcQueue() => State(new Dictionary<string, object> { ["dlcQueue"] = new Dictionary<string, object> {
            ["current"] = dlcCurrent, ["progress"] = dlcProgress, ["next"] = dlcQueue.ToArray() } });

        async Task QueueDlc(List<string> ids)
        {
            if (updater?.Manifest == null || ids == null) return;
            var known = updater.Manifest.Objs("dlc").Select(p => p.Str("id")).ToList();
            ids = ids.Where(id => known.Contains(id) && id != dlcCurrent && !dlcQueue.Contains(id) && !settings.DlcOn(id)).ToList();
            if (dlcCurrent != null) { dlcQueue.AddRange(ids); SendDlcQueue(); return; }   // the running queue takes them
            if (ids.Count == 0) { SendDlcState(); return; }
            if (busy || playing || GameInstall.IsRunning()) { Toast("warn", "Not now", "Close the game and wait for the current task to finish."); SendDlcState(); return; }
            if (!GameInstall.IsGameRoot(settings.GameRoot)) { PickFolder(); SendDlcState(); return; }
            dlcQueue.AddRange(ids);
            busy = true;
            try
            {
                while (dlcQueue.Count > 0)
                {
                    var id = dlcQueue[0];
                    dlcQueue.RemoveAt(0);
                    var title = updater.Manifest.Objs("dlc").First(p => p.Str("id") == id).Str("title");
                    dlcCurrent = id;
                    dlcProgress = -1;
                    settings.Dlc[id] = true;
                    settings.Save();
                    SendDlcState();
                    SendDlcQueue();
                    Toast("info", "Downloading " + title, "You can follow the download in the Maps & DLC tab and under the PLAY button.");
                    try
                    {
                        var work = new Updater(settings, (text, f) =>
                        {
                            PlayButton("update", "DOWNLOADING", text, f, true, true);
                            dlcProgress = f;
                            if ((DateTime.UtcNow - dlcSentAt).TotalMilliseconds < 400) return;   // the tab's percent, not every block
                            dlcSentAt = DateTime.UtcNow;
                            if (InvokeRequired) BeginInvoke((Action)SendDlcQueue);   // the list is the UI thread's
                            else SendDlcQueue();
                        }) { Manifest = updater.Manifest };
                        var plan = (await work.Plan(settings.GameRoot)).Where(f => Updater.PacksOf(f).Contains(id)).ToList();
                        await UpdateAndClean(work, plan);
                        Toast("good", title + " installed", "They are in the map list of Custom Game.");
                    }
                    catch (Exception e)
                    {
                        Log.Write($"dlc install failed ({id}): " + e.Message);
                        Toast("bad", title + ": download failed", e.Message, 9000);
                        dlcQueue.Clear();   // the next ones would fail the same way (network, disk)
                    }
                }
            }
            finally
            {
                busy = false;
                dlcCurrent = null;
                dlcProgress = -1;
                dlcQueue.Clear();
                SendDlcQueue();
                SendDlcState();
            }
            await CheckUpdates(quiet: true);
        }

        // map keys sent by the mod (hostMaps) -> names shown in the game's map selector
        static readonly Dictionary<string, string> MapNames = new Dictionary<string, string> {
            ["BigAbbey"] = "Abbey", ["BigFarm"] = "Farm (Night)", ["BigCircus"] = "Circus", ["SnowMain"] = "Fantasticville", ["BigEgypt"] = "Egypt", ["SummerCampMRG"] = "Camp", ["YagaMap"] = "Dark Swamp", ["Kitchen"] = "Kitchen", ["GoldenMine"] = "Goldhorn Mine", ["MansionReborn"] = "House (2022)", ["CampArena"] = "Summer Camp", ["Farm"] = "Farm (Day)", ["School"] = "School (2022)",
            ["House"] = "House (2022)", ["Castle"] = "Castle", ["Swamp"] = "Village", ["SchoolMerged"] = "School (Reborn)", ["RebornNewMansion"] = "House (Reborn)",
            ["Abbey2021"] = "Abbey (2021)", ["Camp2021"] = "Camp (2021)", ["House2021"] = "House (2021)",
            ["CampPreReborn"] = "Camp (Reborn Beta)", ["FarmPreReborn"] = "Farm (Reborn Beta)", ["FarmPreRebornDay"] = "Farm (Reborn Beta, day)" };
        static string MapName(string map) => map != null && MapNames.TryGetValue(map, out var name) ? name : (map ?? "").Replace("_Main", "").Replace('_', ' ').Trim();

        // ---- bug report (Settings): what happened + the logs, and up to 3 video clips if the player picked some, to the admin panel.
        // The report is made first; each clip follows raw with its own ticket (a clip that fails leaves the report sent).
        async Task SendBug(string category, string text, bool withLogs, List<string> images, string matchRef = null, string tag = null)
        {
            var done = new Dictionary<string, object> { ["type"] = "bugSent", ["ok"] = false };
            try
            {
                var logs = withLogs ? await Task.Run(() => BugReport.CollectLogs(settings.GameRoot)) : null;
                var clips = bugClips.Where(File.Exists).ToList();
                var sent = await api.PostBug(category, text, logs, clips.Select(c => new FileInfo(c).Length).ToList(), images, matchRef, tag == "ai" ? "ai" : null);
                var id = sent.Str("id");
                Log.Write($"bug report {id} sent ({category}{(tag == "ai" ? ", ai" : "")}{(string.IsNullOrEmpty(matchRef) ? "" : ", match " + matchRef)}, logs {(logs == null ? "none" : logs.Length + " B")}{(clips.Count > 0 ? $", {clips.Count} clip(s) to come" : "")})");
                done["ok"] = true;
                done["id"] = id;
                done["logs"] = logs != null;
                bugClips.Clear();   // used once, sent or not (the form shows no clip any more)
                done["clips"] = await UploadClips(clips, sent, $"bug report {id}", $"Your report #{id} was sent");
                // the dialog shows the number (the support team finds the report and its logs with it)
                if (id == null) Toast("good", "Bug report sent", "Thanks! It reached the team" + (logs != null ? " with your logs." : "."), 7000);
            }
            catch (ApiException e) { Toast("bad", "Report not sent", e.Message, 8000); }
            catch (Exception e) { Log.Write("bug report failed: " + e.Message); Toast("bad", "Report not sent", "The server could not be reached. Try again later.", 8000); }
            State(done);
            // the window lists the player's reports under the form: the new one shows at the top
            if (done.Bool("ok") && bugsOpen) await LoadReports(false);
        }

        // ---- my reports (Settings › Help): the bug reports and the cheater reports, what became of each, the team's replies.
        // The friends view (long poll) carries the server's stamp of the last change this player should see (both kinds):
        // the lists are fetched when it moves.
        long? bugsFetched;   // the stamp whose lists were fetched (once per change)
        bool bugsOpen;       // the bug window is on screen (its list is seen)
        bool cheatsOpen;     // the cheater report window is on screen
        string cheatClip;    // the clip picked for the next cheater report (path)
        string reportClip;   // the clip picked for a report from a match (match history › a player's card › Report)
        readonly List<string> bugClips = new List<string>();   // the clips picked for the next bug report (optional, MaxClips)
        readonly List<string> msgClips = new List<string>();   // the clips picked for a message on a report (Report › a report)
        const long MaxClip = 8L * 1024 * 1024;
        const int MaxClips = 3;   // per bug report and per message (the server's MAX_CLIPS)

        long Seen(Dictionary<string, object> store) => settings.Username != null && store.TryGetValue(settings.Username.ToLowerInvariant(), out var v) && v != null
            ? Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) : 0;
        void SetSeen(Dictionary<string, object> store, long stamp)
        {
            if (settings.Username == null || Seen(store) >= stamp) return;
            store[settings.Username.ToLowerInvariant()] = stamp;
            settings.Save();
        }
        static long? StampOf(Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v != null ? Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) : (long?)null;

        /// <summary>The patch notes changed on the server (admin › Patch notes; the friends sync's stamp): asked again now,
        /// not at the next 5 min round.</summary>
        long? patchNotesStamp;
        void CheckPatchNotes(Dictionary<string, object> f)
        {
            var stamp = StampOf(f, "patchnotes");
            if (stamp == null || stamp == patchNotesStamp) return;
            var first = patchNotesStamp == null;
            patchNotesStamp = stamp;
            if (first) return;   // the launcher's start asks them anyway
            serverNotesAt = DateTime.MinValue;
            var _ = SendNotes();
        }

        void CheckBugs(Dictionary<string, object> f)
        {
            var stamp = StampOf(f, "bugs");
            if (stamp == null || stamp <= Math.Min(Seen(settings.BugsSeen), Seen(settings.CheatsSeen)) || stamp == bugsFetched) return;
            bugsFetched = stamp;
            var _ = LoadReports(true);
        }

        /// <summary>Both lists to the page (badges: reports changed since their list was last shown); a toast for news while closed.</summary>
        async Task LoadReports(bool notify)
        {
            var news = new List<string>();
            var bugsTask = api.Bugs();
            var cheatsTask = api.CheatReports();
            news.AddRange(await ShowReports("bugs", bugsTask, settings.BugsSeen, bugsOpen, "Bug report"));
            news.AddRange(await ShowReports("cheats", cheatsTask, settings.CheatsSeen, cheatsOpen, "Player report"));
            if (notify && news.Count > 0)
            {
                var open = new Dictionary<string, object> { ["label"] = news.Count == 1 ? "See it" : "See them", ["cmd"] = news[0].StartsWith("Player") ? "openCheats" : "openBugs" };
                if (news.Count == 1) Toast("good", news[0], "", 12000, open);
                else Toast("good", $"{news.Count} of your reports have news", "See what the team did with them.", 12000, open);
                Log.Write("reports: " + string.Join(", ", news));
            }
        }

        /// <summary>One list ("bugs" or "cheats") to the page; seen when its window is open. The news, as toast titles.</summary>
        async Task<List<string>> ShowReports(string kind, Task<Dictionary<string, object>> fetch, Dictionary<string, object> seenStore, bool open, string title)
        {
            var news = new List<string>();
            try
            {
                var r = await fetch;
                var seen = Seen(seenStore);
                var reports = r.Objs("reports").ToList();
                var changed = reports.Where(x => (StampOf(x, "updated") ?? 0) > seen).ToList();
                State(new Dictionary<string, object> { ["type"] = kind, ["reports"] = reports.Cast<object>().ToList(), ["seen"] = seen,
                    ["news"] = open ? 0 : changed.Count, ["players"] = r.TryGetValue("players", out var pl) ? pl : null });
                // shown, or nothing to show: seen (a player without reports of this kind is not fetched again at each start)
                var stamp = StampOf(r, "stamp");
                if (open || reports.Count == 0) SetSeen(seenStore, stamp ?? bugsFetched ?? 0);
                else news.AddRange(changed.Select(x => $"{title} #{x.Str("id")}: {x.Str("label")}"));
            }
            catch (Exception e)
            {
                Log.Write($"{kind}: " + e.Message);
                if (open) State(new Dictionary<string, object> { ["type"] = kind, ["error"] = e is ApiException ? e.Message : "The server could not be reached." });
            }
            return news;
        }

        async Task SendBugMessage(long id, string text, bool withLogs = false, List<string> images = null)
        {
            try
            {
                var logs = withLogs ? await Task.Run(() => BugReport.CollectLogs(settings.GameRoot)) : null;
                if (withLogs && logs == null) Toast("warn", "No logs found", "Your message goes without them: start the game once, then send them again.", 8000);
                var clips = msgClips.Where(File.Exists).ToList();
                var sent = await api.BugMessage(id, (text ?? "").Trim(), logs, images, clips.Select(c => new FileInfo(c).Length).ToList());
                msgClips.Clear();
                Log.Write($"bug report {id}: details sent (logs {(logs == null ? "none" : logs.Length + " B")}, {images?.Count ?? 0} image(s), {clips.Count} clip(s))");
                await UploadClips(clips, sent, $"bug report {id}", "Your message was sent");
                Toast("good", "Sent", $"Added to report #{id}{(logs != null ? " with your logs" : "")}. The team will see it.");
                State(new Dictionary<string, object> { ["type"] = "bugMessageSent", ["id"] = id, ["ok"] = true });
                await LoadReports(false);
            }
            catch (Exception e)
            {
                Toast("bad", "Not sent", e is ApiException ? e.Message : "The server could not be reached. Try again later.", 8000);
                State(new Dictionary<string, object> { ["type"] = "bugMessageSent", ["id"] = id, ["ok"] = false });
            }
        }

        async Task SendCheatMessage(long id, string text, List<string> images = null)
        {
            try
            {
                var clips = msgClips.Where(File.Exists).ToList();
                var sent = await api.CheatMessage(id, (text ?? "").Trim(), images, clips.Select(c => new FileInfo(c).Length).ToList());
                msgClips.Clear();
                await UploadClips(clips, sent, $"cheat report {id}", "Your message was sent");
                Toast("good", "Sent", $"Added to player report #{id}. The team will see it.");
                State(new Dictionary<string, object> { ["type"] = "cheatMessageSent", ["id"] = id, ["ok"] = true });
                await LoadReports(false);
            }
            catch (Exception e)
            {
                Toast("bad", "Not sent", e is ApiException ? e.Message : "The server could not be reached. Try again later.", 8000);
                State(new Dictionary<string, object> { ["type"] = "cheatMessageSent", ["id"] = id, ["ok"] = false });
            }
        }

        void SetDx11(bool on)
        {
            settings.DirectX11 = on;
            settings.Save();
            Log.Write("directx 11: " + (on ? "on" : "off"));
            State(new Dictionary<string, object> { ["dx11"] = on });
        }

        /// <summary>How the player left a match, for the admin's Leavers page; nothing shown to the player.</summary>
        async Task ReportLeave(string reason, string role)
        {
            if (!new[] { "menu", "network", "closed", "crash" }.Contains(reason)) return;
            try { await api.ReportLeave(reason, role == "host" ? "host" : "client"); Log.Write("match left: " + reason + " (" + role + ")"); }
            catch (Exception e) { Log.Write("match left report: " + e.Message); }
        }

        /// <summary>Did the game write a crash report since `since` (the newest folder of %LOCALAPPDATA%\Propnight\Saved\Crashes)?</summary>
        static bool CrashSince(DateTime since)
        {
            try
            {
                var crashes = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Propnight", "Saved", "Crashes"));
                return crashes.Exists && crashes.GetDirectories().Any(d => d.LastWriteTimeUtc >= since);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>Did the game write a crash report since `since` for its graphics card in DirectX 12 (DXGI_ERROR_DEVICE_REMOVED /
        /// DEVICE_HUNG, a GPU or driver hang: bugs 112, 135)? The newest folder of %LOCALAPPDATA%\Propnight\Saved\Crashes.</summary>
        static bool GpuCrashSince(DateTime since)
        {
            try
            {
                var crashes = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Propnight", "Saved", "Crashes"));
                if (!crashes.Exists) return false;
                var last = crashes.GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).FirstOrDefault();
                if (last == null || last.LastWriteTimeUtc < since) return false;
                var context = Path.Combine(last.FullName, "CrashContext.runtime-xml");
                if (!File.Exists(context)) return false;
                var text = File.ReadAllText(context);
                var gpu = text.Contains("DXGI_ERROR_DEVICE_REMOVED") || text.Contains("DXGI_ERROR_DEVICE_HUNG") || text.Contains("GPUCrash");
                if (gpu) Log.Write("game crash on the graphics card (DirectX 12): " + last.Name);
                return gpu && text.Contains("D3D12");
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>The clip of a report just made from a match ({rid, ticket}): uploaded raw; the answer goes back with rid.</summary>
        async Task ReportClipUpload(Dictionary<string, object> m)
        {
            var reply = new Dictionary<string, object> { ["type"] = "community", ["rid"] = m.Str("rid") };
            try
            {
                var clip = reportClip != null && File.Exists(reportClip) ? reportClip : throw new ApiException(400, "the clip is gone: pick it again");
                await api.UploadClip(m.Str("ticket"), clip, "/revival/match/report/clip");
                reportClip = null;
                reply["ok"] = true;
                reply["data"] = new Dictionary<string, object>();
            }
            catch (Exception e)
            {
                reply["ok"] = false;
                reply["error"] = e is ApiException ? e.Message : "The clip could not be sent. Try again later.";
                Log.Write("report clip: " + e.Message);
            }
            State(reply);
        }

        // ---- community (suggestions): the page calls the server through here, its token stays in the launcher. Only these
        // paths; the answer goes back with the page's request id.
        static readonly System.Text.RegularExpressions.Regex CommunityPath =
            new System.Text.RegularExpressions.Regex(@"^/revival/((suggestions(/seen)?|suggestion(/(vote|comment|delete|edit))?)(\?id=\d+)?|polls|poll/vote|notifications(/read)?|matches|match/report|match\?ref=[mdh](%3A|:)[\w-]{1,64})$");
        async Task CommunityCall(Dictionary<string, object> m)
        {
            var path = m.Str("path") ?? "";
            var post = m.Bool("post");
            var reply = new Dictionary<string, object> { ["type"] = "community", ["rid"] = m.Str("rid") };
            try
            {
                if (!CommunityPath.IsMatch(path)) throw new ApiException(400, "bad request");
                reply["data"] = await api.Call(post ? System.Net.Http.HttpMethod.Post : System.Net.Http.HttpMethod.Get, path,
                    post ? (m.Obj("body") ?? new Dictionary<string, object>()) : null);
                reply["ok"] = true;
            }
            catch (Exception e)
            {
                reply["ok"] = false;
                reply["error"] = e is ApiException ? e.Message : "The server could not be reached. Try again later.";
                if (!(e is ApiException)) Log.Write("community: " + e.Message);
            }
            State(reply);
        }

        /// <summary>An image of a suggestion being written (the page made it a JPEG / PNG, base64): uploaded raw.</summary>
        async Task CommunityImage(Dictionary<string, object> m)
        {
            var reply = new Dictionary<string, object> { ["type"] = "community", ["rid"] = m.Str("rid") };
            try
            {
                var data = Convert.FromBase64String(m.Str("data") ?? "");
                if (data.Length > 5 * 1024 * 1024) throw new ApiException(413, "the image is over 5 MB");
                reply["data"] = await api.UploadSuggestionImage(data, m.Str("mime"));
                reply["ok"] = true;
            }
            catch (Exception e)
            {
                reply["ok"] = false;
                reply["error"] = e is ApiException ? e.Message : e is FormatException ? "this image could not be read" : "The server could not be reached. Try again later.";
            }
            State(reply);
        }

        // ---- report a cheater: a player, what they saw and a video clip (required, 8 MB at most). The server checks the report
        // and gives a ticket; the report is made when its clip arrives (no report without a clip).
        void PickClip(string kind)
        {
            var list = kind == "bug" ? bugClips : kind == "msg" ? msgClips : null;   // several clips (up to MaxClips)
            if (list != null && list.Count >= MaxClips) { Toast("warn", "3 clips at most", "Remove one to add another."); return; }
            using (var dialog = new OpenFileDialog { Title = list != null ? "Choose video clips (8 MB each at most)" : "Choose a video clip (8 MB at most)",
                Filter = "Videos|*.mp4;*.mov;*.webm;*.mkv;*.avi|All files|*.*", Multiselect = list != null,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (list != null)
                {
                    var tooLarge = dialog.FileNames.Where(f => new FileInfo(f).Length > MaxClip).ToList();
                    var fit = dialog.FileNames.Except(tooLarge).Where(f => !list.Contains(f)).ToList();
                    var added = fit.Take(MaxClips - list.Count).ToList();
                    list.AddRange(added);
                    if (tooLarge.Count > 0)
                        Toast("warn", tooLarge.Count == 1 ? "Clip too large" : "Clips too large", $"{string.Join(", ", tooLarge.Select(Path.GetFileName))}: 8 MB at most. Cut it to the moment that shows the {(kind == "bug" ? "problem" : "cheat")}, or lower its quality.", 9000);
                    else if (added.Count < fit.Count) Toast("warn", "3 clips at most", "Only the first ones were added.");
                    SendClips(list, kind + "Clips");
                    return;
                }
                var size = new FileInfo(dialog.FileName).Length;
                if (size > MaxClip)
                {
                    Toast("warn", "Clip too large", $"It is {size / 1048576.0:0.0} MB: 8 MB at most. Cut it to the moment that shows the {(kind == "bug" ? "problem" : "cheat")}, or lower its quality.", 9000);
                    return;
                }
                if (kind == "report") reportClip = dialog.FileName; else cheatClip = dialog.FileName;
                State(new Dictionary<string, object> { ["type"] = kind + "Clip", ["name"] = Path.GetFileName(dialog.FileName), ["size"] = size });
            }
        }

        /// <summary>The picked clips to the page: {type, clips: [{name, size}]}.</summary>
        void SendClips(List<string> list, string type) => State(new Dictionary<string, object> { ["type"] = type,
            ["clips"] = list.Select(f => (object)new Dictionary<string, object> { ["name"] = Path.GetFileName(f), ["size"] = File.Exists(f) ? new FileInfo(f).Length : 0 }).ToList() });

        /// <summary>One clip taken out of the list (m.i), or all of them (no index).</summary>
        void RemoveClip(List<string> list, Dictionary<string, object> m, string type)
        {
            if (m.TryGetValue("i", out var v) && v != null)
            {
                var i = Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
                if (i >= 0 && i < list.Count) list.RemoveAt(i);
            }
            else list.Clear();
            SendClips(list, type);
        }

        /// <summary>Each clip raw with its ticket (the answer's tickets, one per clip, in order): how many arrived. A clip that
        /// fails leaves the rest (and the report or message) sent.</summary>
        async Task<int> UploadClips(List<string> clips, Dictionary<string, object> sent, string what, string sentText)
        {
            var tickets = sent.Strs("tickets").ToList();
            int ok = 0;
            string error = null;
            for (int i = 0; i < clips.Count; i++)
            {
                try
                {
                    if (i >= tickets.Count) throw new ApiException(503, "the server did not take it");
                    await api.UploadClip(tickets[i], clips[i], "/revival/report-clip");
                    ok++;
                }
                catch (Exception e)
                {
                    Log.Write($"{what}: clip {i + 1} failed: {e.Message}");
                    error = e is ApiException ? e.Message : null;
                }
            }
            if (clips.Count > 0) Log.Write($"{what}: {ok}/{clips.Count} clip(s) sent");
            if (ok < clips.Count)
                Toast("warn", ok == 0 && clips.Count == 1 ? "Clip not sent" : "Clips not all sent", $"{sentText}, but {(ok == 0 ? (clips.Count == 1 ? "not its clip" : "not its clips") : $"only {ok} of its {clips.Count} clips")}"
                    + (error != null ? $" ({error})." : ". Try a smaller clip, or send it to the support team on Discord."), 10000);
            return ok;
        }

        // ---- report a player (1.39: the cheater report and the match history's report in one): a reason, a clip needed
        // for cheating and glitching only; with a clip the report is made once the clip is in (ticket), without it at once
        async Task<bool> SendCheat(string reported, string text, List<string> images, string matchRef = null, string reason = "cheating")
        {
            var done = new Dictionary<string, object> { ["type"] = "cheatSent", ["ok"] = false };
            try
            {
                var clip = cheatClip != null && File.Exists(cheatClip) ? cheatClip : null;
                if (clip == null && (reason == "cheating" || reason == "glitching")) throw new ApiException(400, "add a clip that shows it");
                var size = clip != null ? new FileInfo(clip).Length : 0;
                if (size > MaxClip) throw new ApiException(413, "the clip is over 8 MB");
                var sent = await api.PostCheatReport((reported ?? "").Trim(), (text ?? "").Trim(), size, images, matchRef, reason);
                var id = clip != null ? await api.UploadClip(sent.Str("ticket"), clip) : sent.Str("id");
                done["ok"] = true;
                done["id"] = id;
                done["clip"] = clip != null;
                cheatClip = null;
                Log.Write($"player report {id} sent ({reason}{(clip != null ? $", {size} B clip" : "")})");
            }
            catch (ApiException e) { Toast("bad", "Report not sent", Capital(e.Message) + ".", 9000); }
            catch (Exception e) { Log.Write("cheat report failed: " + e.Message); Toast("bad", "Report not sent", "The server could not be reached. Try again later.", 8000); }
            State(done);
            if (done.Bool("ok")) await LoadReports(false);
            return done.Bool("ok");
        }
        /// <summary>What the player sent with a bug report (or a message): the zip's files to the page, the text ones readable
        /// (their last 300 KB), the others by name and size.</summary>
        async Task ViewLogs(long id, long eventId)
        {
            var view = new Dictionary<string, object> { ["type"] = "logsView", ["id"] = id, ["event"] = eventId };
            try
            {
                var zip = Convert.FromBase64String((await api.BugLogs(id, eventId)).Str("zip") ?? "");
                var files = new List<object>();
                using (var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip), System.IO.Compression.ZipArchiveMode.Read))
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith("/")) continue;
                        string text = null;
                        var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                        if (ext != ".dmp" && entry.Length < 64L * 1024 * 1024)
                            using (var s = entry.Open())
                            using (var ms = new MemoryStream())
                            {
                                s.CopyTo(ms);
                                var bytes = ms.ToArray();
                                const int Max = 300 * 1024;
                                var from = Math.Max(0, bytes.Length - Max);
                                if (Array.IndexOf(bytes, (byte)0, from, Math.Min(4096, bytes.Length - from)) < 0)
                                    text = (from > 0 ? "… (the start is cut: the last 300 KB)\n" : "") + System.Text.Encoding.UTF8.GetString(bytes, from, bytes.Length - from);
                            }
                        files.Add(new Dictionary<string, object> { ["name"] = entry.FullName, ["size"] = entry.Length, ["text"] = text });
                    }
                view["files"] = files;
            }
            catch (Exception e)
            {
                Log.Write($"bug report {id}: logs view failed: {e.Message}");
                view["error"] = e is ApiException ? Capital(e.Message) + "." : "The logs could not be read.";
            }
            State(view);
        }
        static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

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
        /// Joins a game on its relay: the mod reads "joinrelay HOST PORT" from its command file once the game's menu is
        /// ready (host: the game's region relay; without it, "joinrelay PORT" and the address in Win64/revival-relay.txt).
        /// Game ports: 7800-7999 Europe, the other regions' relays above (8000-8999).
        /// </summary>
        static bool IsIPv4(string s) => s != null && System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d{1,3}(\.\d{1,3}){3}$");

        /// <summary>The in-game overlay's Join (Overlay.cs): the friend's game from the last friends answer.</summary>
        async Task JoinFriendByName(string name)
        {
            var fr = overlayFriends.FirstOrDefault(x => string.Equals(Clean(x.Str("username")), Clean(name), StringComparison.OrdinalIgnoreCase));
            var s = fr?.Obj("session");
            if (s == null || !s.Bool("joinable"))
            {
                Toast("warn", "Can't join " + Clean(name), s == null ? "They are not in a game." : "Their game is full or in a match.");
                return;
            }
            overlayJoinPort = (int)s.Long("port");
            Log.Write("overlay: joining " + Clean(name));
            await JoinFriend((int)s.Long("port"), Clean(name), s.Bool("dlc"), MapName(s.Str("map")), s.Str("host"));
        }

        // ---- the in-game overlay (Overlay.cs): what its page shows, what it asks
        /// <summary>
        /// The in-game overlay, off for now (2026-11-01, user): it cannot show over a game in exclusive fullscreen; it comes back
        /// once drawn in the game's own picture (as Steam's). Off: no Shift + Tab, no notification or corner pill, invites by the
        /// game's dialog as before.
        /// </summary>
        const bool OverlayOn = false;
        string OverlayWin64() => GameInstall.IsGameRoot(settings.GameRoot) ? GameInstall.Win64(settings.GameRoot) : Config.DataDir;

        /// <summary>The game is in a match (the mod's presence file: match=1).</summary>
        bool GameInMatch()
        {
            if (!GameInstall.IsGameRoot(settings.GameRoot)) return false;
            return GameInstall.Presence(settings.GameRoot).TryGetValue("match", out var m) && m == "1";
        }

        /// <summary>Invites shown by the overlay (notification + card) rather than the game's dialog.</summary>
        bool OverlayTakesInvites() => OverlayOn && !Overlay.ExclusiveFullscreen() && (settings.OverlayNotifyInvites || GameInMatch());

        /// <summary>A friend coming online (offline before, in the last friends answer): a notification over the game.</summary>
        void OverlayFriendsSeen(Dictionary<string, object> f)
        {
            if (!OverlayOn) return;
            var now = new Dictionary<string, string>();
            foreach (var fr in f.Objs("friends")) now[fr.Str("username") ?? ""] = fr.Str("status") ?? "offline";
            // offline -> the launcher: "is online"; anything else -> the game ("playing", "in_game", "hosting"): "launched Propnight"
            var inGame = new[] { "playing", "in_game", "hosting" };
            if (overlayStatuses != null && settings.OverlayNotifyOnline)
                foreach (var kv in now)
                {
                    if (!overlayStatuses.TryGetValue(kv.Key, out var was) || was == kv.Value) continue;
                    if (inGame.Contains(kv.Value) && !inGame.Contains(was)) overlay.Notify(kv.Key, "launched Propnight", null, false, 4);
                    else if (kv.Value == "online" && was == "offline") overlay.Notify(kv.Key, "is online", null, false, 4);
                }
            overlayStatuses = now;
            overlay.Push(false);
        }

        /// <summary>The friend the overlay is joining, until the friends list shows you in their game, a refusal or 60 s.</summary>
        string OverlayJoining()
        {
            if (overlayJoinName == null) return null;
            var you = lastFriendsView?.Obj("you")?.Obj("session");
            var fr = lastFriendsView?.Objs("friends").FirstOrDefault(x => string.Equals(Clean(x.Str("username")), overlayJoinName, StringComparison.OrdinalIgnoreCase));
            if (DateTime.UtcNow - overlayJoinAt > TimeSpan.FromSeconds(60)
                || (overlayJoinPort != 0 && you != null && you.Long("port") == overlayJoinPort) || (fr != null && fr.Bool("sameGame")))
                overlayJoinName = null;
            return overlayJoinName;
        }

        /// <summary>The overlay page's state: friends, squad, invites, this session, this game (the mod's file), settings.</summary>
        Dictionary<string, object> OverlayState()
        {
            var f = lastFriendsView;
            var squad = f?.Obj("squad");
            var you = f?.Obj("you");
            var friends = new List<object>();
            foreach (var fr in f?.Objs("friends") ?? Enumerable.Empty<Dictionary<string, object>>())
            {
                var d = new Dictionary<string, object> { ["name"] = fr.Str("username"), ["status"] = fr.Str("status") ?? "offline", ["sameGame"] = fr.Bool("sameGame") };
                var s = fr.Obj("session");
                if (s != null)
                    d["session"] = new Dictionary<string, object> { ["map"] = MapName(s.Str("map")), ["players"] = s.ContainsKey("players") ? s.Long("players") : 0,
                        ["joinable"] = s.Bool("joinable"), ["phase"] = s.Str("phase") };
                friends.Add(d);
            }
            var start = overlay.GameStart;
            var state = new Dictionary<string, object>
            {
                ["you"] = squad?.Str("you") ?? settings.Username,
                ["friends"] = friends,
                ["squad"] = (squad?.Objs("members") ?? Enumerable.Empty<Dictionary<string, object>>())
                    .Select(m => (object)new Dictionary<string, object> { ["name"] = m.Str("username"), ["leader"] = m.Bool("leader"), ["online"] = m.Bool("online") }).ToList(),
                ["squadInvites"] = (squad?.Objs("invites") ?? Enumerable.Empty<Dictionary<string, object>>()).Select(i => (object)Clean(i.Str("from"))).Distinct().ToList(),
                ["gameInvites"] = overlayInvites.Where(kv => DateTime.UtcNow - (DateTime)kv.Value["at"] < TimeSpan.FromMinutes(10))
                    .Select(kv => (object)new Dictionary<string, object> { ["key"] = kv.Key, ["from"] = kv.Value["from"], ["map"] = kv.Value["map"], ["players"] = kv.Value["players"] }).ToList(),
                // "Invited" for 3 s after the click, then Invite again
                ["invited"] = overlayInvited.Where(kv => DateTime.UtcNow - kv.Value < TimeSpan.FromSeconds(3)).Select(kv => (object)kv.Key).ToList(),
                // Join: "Joining…" on that friend, the others off; refused from a hosted game or a match, asked twice from a lobby
                ["joining"] = OverlayJoining(),
                ["youHosting"] = you?.Bool("hosting") ?? false,
                ["youPhase"] = you?.Obj("session") != null ? (you.Obj("session").Str("phase") ?? "lobby") : null,
                ["sessionStart"] = start.HasValue ? (object)(long)(start.Value - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds : null,
                ["session"] = OverlaySession(start),
                ["prefs"] = new Dictionary<string, object> { ["notifyOnline"] = settings.OverlayNotifyOnline, ["notifyInvites"] = settings.OverlayNotifyInvites,
                    ["timer"] = settings.OverlayTimer, ["right"] = settings.OverlayRight },
                ["fps"] = settings.OverlayFps,
                ["report"] = overlayReport,
            };
            // this game: the mod's answer (overlay.lua, once a second while the overlay is open)
            Dictionary<string, object> game = null;
            try
            {
                var path = Path.Combine(OverlayWin64(), "revival-overlay-state.json");
                if (GameInstall.IsRunning() && File.Exists(path)) game = Json.Parse(File.ReadAllText(path));
            }
            catch (Exception) { }   // being written: the next second
            if (game != null)
            {
                // the mod's map key ("BigAbbey", hosting.lua's list: the name and picture players know), else the world's file
                game["map"] = MapName(game.Str("map") ?? (game.Str("world") ?? "").Replace("_Main", ""));
                // back in the menu after a game joined: the mod's role stays "client" ("TEST0 MAIN MENU 3 · in the lobby", 2026-11-01)
                if ((game.Str("world") ?? "").IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) >= 0) { game["role"] = "menu"; game["map"] = ""; }
                state["game"] = game;
                var set = game.Obj("settings");
                if (set != null)
                {
                    // a value just set from the overlay wins for 4 s (the mod applies it within a second)
                    foreach (var kv in overlaySet.Where(kv => DateTime.UtcNow - kv.Value.Value < TimeSpan.FromSeconds(4)).ToList()) set[kv.Key] = kv.Value.Key;
                    state["settings"] = set;
                    state["hasSettings"] = true;
                }
            }
            // no answer of the mod yet (just opened): the page keeps the values it had
            var notes = serverNotes != null && serverNotes.Count > 0 ? serverNotes : updater?.Manifest?.Objs("notes").ToList();
            var newest = notes?.FirstOrDefault();
            if (newest != null)
            {
                var date = newest.Str("date");
                if (DateTime.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
                    date = d.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
                state["note"] = new Dictionary<string, object> { ["title"] = newest.Str("title") ?? newest.Str("version"), ["date"] = date };
            }
            return state;
        }

        /// <summary>
        /// This session's public matches (ranked: the only ones with MMR) since the game started: played, won, MMR. The mod's
        /// records (revival-matches.jsonl) leave the file once sent to the server (Reports.Matches): each one is kept here as
        /// it is seen in the file or sent (2026-11-01: a win showed, then went back to 0 when its line was sent).
        /// </summary>
        Dictionary<string, object> OverlaySession(DateTime? start)
        {
            var result = new Dictionary<string, object> { ["matches"] = 0, ["won"] = 0, ["mmr"] = 0 };
            if (start == null || !GameInstall.IsGameRoot(settings.GameRoot)) return result;
            var path = Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-matches.jsonl");
            try
            {
                if (File.Exists(path))
                    foreach (var line in File.ReadLines(path))
                        if (line.Length > 2) try { OverlayMatchSeen(Json.Parse(line)); } catch (Exception) { }
            }
            catch (IOException) { }   // being written or sent: the next second
            var since = (long)(start.Value - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds - 5;
            int matches = 0, won = 0; long mmr = 0;
            lock (overlaySeenMatches)
                foreach (var m in overlaySeenMatches.Values)
                {
                    if (!m.ContainsKey("startedAt") || m.Long("startedAt") < since || !m.Bool("ranked")) continue;
                    matches++;
                    if (m.Bool("won")) won++;
                    if (m.TryGetValue("mmrBefore", out var a) && a != null && m.TryGetValue("mmrAfter", out var b) && b != null)
                        mmr += Convert.ToInt64(b) - Convert.ToInt64(a);
                }
            result["matches"] = matches; result["won"] = won; result["mmr"] = mmr;
            return result;
        }

        /// <summary>A match record of the mod (in the file, or just sent to the server): kept for this session's counts.</summary>
        void OverlayMatchSeen(Dictionary<string, object> m)
        {
            if (m == null || !m.ContainsKey("startedAt")) return;
            lock (overlaySeenMatches) overlaySeenMatches[m.Str("startedAt") + "|" + m.Str("map")] = m;
        }
        /// <summary>The overlay page's actions, and its notifications' buttons.</summary>
        async Task OverlayAction(Dictionary<string, object> m)
        {
            var name = Clean(m.Str("name"));
            switch (m.Str("cmd"))
            {
                case "invite":
                    if (name.Length == 0) return;
                    overlayInvited[name] = DateTime.UtcNow;
                    // as the launcher's Friends list: in a game (lobby or match), to that game; in the menu, to the squad
                    // (the overlay sent squad invites in a game too, 2026-11-01)
                    if (lastFriendsView?.Obj("you")?.Obj("session") != null) await FriendAction("invite", name);
                    else await SquadAction("invite", name);
                    break;
                case "join":
                {
                    // as the launcher page: one join at a time, never from a hosted game or a match (the page says why; a spam
                    // of clicks, 2026-11-01, gets here once). "Joining…" a moment on the page, then back to the game
                    if (name.Length == 0 || OverlayJoining() != null) return;
                    var me = lastFriendsView?.Obj("you");
                    if (me != null && (me.Bool("hosting") || me.Obj("session")?.Str("phase") == "match")) return;
                    overlayJoinName = name; overlayJoinPort = 0; overlayJoinAt = DateTime.UtcNow;
                    overlay.Push(false);
                    await JoinFriendByName(name);
                    if (overlayJoinName == null) return;   // refused: the page shows Join again
                    await Task.Delay(1200);
                    if (overlay.IsOpen) overlay.Close(true);
                    break;
                }
                case "squad":
                    await SquadAction(m.Str("action"), name);
                    break;
                case "joinInvite":
                    var key = m.Str("key") ?? "";
                    if (!overlayInvites.TryGetValue(key, out var inv)) return;
                    overlayInvites.Remove(key);
                    overlay.Close(true);
                    await JoinFriend((int)inv.Long("port"), inv.Str("from"), inv.Bool("dlc"), inv.Str("map"), inv.Str("host"));
                    break;
                case "dismissInvite":
                    overlayInvites.Remove(m.Str("key") ?? "");
                    break;
                case "gameSetting":
                    OverlayGameCommand(m.Str("verb"), m.Str("field"), m.ContainsKey("value") ? Convert.ToDouble(m["value"], System.Globalization.CultureInfo.InvariantCulture) : 0);
                    break;
                case "pref":
                    var on = m.Bool("on");
                    switch (m.Str("key"))
                    {
                        case "notify": settings.OverlayNotifyOnline = settings.OverlayNotifyInvites = on; break;
                        case "notifyOnline": settings.OverlayNotifyOnline = on; break;
                        case "notifyInvites": settings.OverlayNotifyInvites = on; break;
                        case "timer": settings.OverlayTimer = overlay.TimerOn = on; break;
                        case "right": settings.OverlayRight = overlay.ToastsRight = on; break;
                        case "fps": settings.OverlayFps = overlay.FpsOn = on; OverlayGameCommand("fps", on ? "1" : "0", 0); break;
                        default: return;
                    }
                    settings.Save();
                    break;
                case "launcher":
                    // the launcher in front, on the page asked (its own functions)
                    overlay.Close(false);
                    ShowFromTray();
                    var script = new Dictionary<string, string> { ["home"] = "showTab('home')", ["news"] = "showTab('news')", ["report"] = "openCheat()", ["bug"] = "openBug()" };
                    if (script.TryGetValue(m.Str("page") ?? "", out var js) && web.CoreWebView2 != null) await web.CoreWebView2.ExecuteScriptAsync(js);
                    break;
                case "addFriend":
                    await FriendAction("request", null, m.Str("code"));
                    break;
                // a player of this game reported from the overlay: the launcher's report (reasons, a clip for cheating and
                // glitching, the same server call as Match history's), the clip picked over the game
                case "reportClip":
                    using (var dialog = new OpenFileDialog { Title = "Choose a video clip (8 MB at most)", Filter = "Videos|*.mp4;*.mov;*.webm;*.mkv;*.avi|All files|*.*",
                        InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) })
                    {
                        if (dialog.ShowDialog(overlay.Owner) != DialogResult.OK) return;
                        var size = new FileInfo(dialog.FileName).Length;
                        overlayReport = new Dictionary<string, object> { ["clip"] = Path.GetFileName(dialog.FileName), ["size"] = size,
                            ["error"] = size > MaxClip ? $"This clip is {size / 1048576.0:0.0} MB: 8 MB at most." : "" };
                        overlayClip = size > MaxClip ? null : dialog.FileName;
                    }
                    break;
                case "report":
                    var reported = Clean(m.Str("name"));
                    var reason = new[] { "cheating", "glitching", "toxic", "teaming", "afk" }.Contains(m.Str("reason")) ? m.Str("reason") : null;
                    if (reported.Length < 2 || reason == null) return;
                    cheatClip = overlayClip;
                    var ok = await SendCheat(reported, m.Str("text") ?? "", null, null, reason);
                    overlayReport = new Dictionary<string, object> { ["sent"] = ok, ["name"] = reported,
                        ["error"] = ok ? "" : (reason == "cheating" || reason == "glitching") && overlayClip == null ? "Add a clip that shows it." : "Not sent: see the launcher's message." };
                    if (ok) overlayClip = null;
                    break;
                case "reportReset":
                    overlayReport = null; overlayClip = null;
                    break;
                case "copyCode":
                    await FriendAction("copyCode", null);
                    break;
            }
        }

        /// <summary>A line for the mod (overlay.lua): the game's sound settings, its FPS counter, leaving the game.</summary>
        void OverlayGameCommand(string verb, string field, double value)
        {
            string line;
            if (verb == "fps") line = "fps " + (field == "1" ? "1" : "0");

            else if ((verb == "vol" || verb == "mute") && field != null && System.Text.RegularExpressions.Regex.IsMatch(field, @"^[A-Za-z]+\??$"))
            {
                value = Math.Max(0, Math.Min(1, value));
                line = $"{verb} {field} {value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}";
                // the page shows it at once (the mod's answer comes within a second): its key in the mod's state
                var key = verb == "vol" ? field : field == "IsVoiceChatMuted?" ? "voiceMuted" : field == "IsVoiceLinesMuted?" ? "linesMuted" : null;
                if (key != null) overlaySet[key] = new KeyValuePair<object, DateTime>(verb == "vol" ? (object)value : value >= 0.5, DateTime.UtcNow);
            }
            else return;
            if (!GameInstall.IsGameRoot(settings.GameRoot)) return;
            try { File.AppendAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-overlay-command.txt"), line + "\n"); }
            catch (IOException e) { Log.Write("overlay command: " + e.Message); }
        }

        async Task JoinFriend(int port, string from, bool classicMap = false, string map = null, string host = null)
        {
            if (port < 7800 || port > 8999) return;
            if (!IsIPv4(host)) host = null;
            var command = host != null ? $"joinrelay {host} {port}" : "joinrelay " + port;
            if (!GameInstall.IsGameRoot(settings.GameRoot)) { PickFolder(); return; }
            if (VersionBlocked(port, from)) return;
            var pack = ClassicPackOf(map);
            if (classicMap && (pack == null ? ClassicPacks().Count == 0 : !ClassicPacks().Contains(pack)))
            {
                // the game would fail to load the map: say why, here and in the game's own message box
                var packName = pack == "rb" ? "Reborn Beta" : pack != null ? pack + " Classic" : "Classic maps";
                Toast("warn", packName + " needed", $"{from ?? "Your friend"} plays on {map ?? "a classic map"}. Install the {packName} in the DLC tab to join.", 9000);
                State(new Dictionary<string, object> { ["tab"] = "dlc" });
                if (GameInstall.IsRunning())
                    File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-map-command.txt"), "missingmaps " + (map ?? ""));
                return;
            }
            SendAddressProof(true);   // the friend's private game must know this PC's address first
            if (GameInstall.IsRunning())
            {
                File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-map-command.txt"), command);
                Toast("good", "Joining " + (from ?? "your friend"), "Propnight is connecting to the game.");
                return;
            }
            if (busy || playing) { Toast("warn", "Not now", "Wait until the current task is finished."); return; }
            pendingJoinPort = port;
            pendingJoinHost = host;
            pendingHostCommand = null;
            Toast("info", "Joining " + (from ?? "your friend"), "Propnight starts, then joins the game.");
            await Play();
        }

        /// <summary>Servers › Create a game (roadmap 21): the server says yes (a map, the role) unless a public game near this player
        /// waits in its lobby with room ({near}: the page offers to join it); the game hosts it ("hostpublic MAP ROLE"), at once
        /// when it runs (in its menu), else at its launch.</summary>
        async Task HostPublic(string role)
        {
            if (!GameInstall.IsGameRoot(settings.GameRoot)) { PickFolder(); return; }
            var done = new Dictionary<string, object> { ["type"] = "hostPublic", ["ok"] = false };
            try
            {
                var r = await api.HostPublic(role, ClassicPacks());
                if (!r.Bool("ok"))
                {
                    done["near"] = r.Obj("near");
                    State(done);
                    return;
                }
                var map = new string((r.Str("map") ?? "").Where(char.IsLetterOrDigit).ToArray());
                var chosen = new string((r.Str("role") ?? "any").Where(char.IsLetter).ToArray());
                var command = $"hostpublic {map} {chosen}";
                done["ok"] = true;
                done["map"] = MapName(map);
                Log.Write($"servers: creating a public game on {map} as {chosen}");
                if (GameInstall.IsRunning())
                {
                    File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-map-command.txt"), command);
                    Toast("good", "Creating your game", $"Propnight opens a public game on {MapName(map)}. Go back to its main menu if you are in a game.", 9000);
                    State(done);
                    return;
                }
                if (busy || playing) { Toast("warn", "Not now", "Wait until the current task is finished."); State(done); return; }
                pendingJoinPort = null;
                pendingJoinHost = null;
                pendingHostCommand = command;
                Toast("info", "Creating your game", $"Propnight starts, then opens a public game on {MapName(map)}.", 8000);
                State(done);
                await Play();
            }
            catch (ApiException e) { Toast("bad", "Game not created", Capital(e.Message) + (e.Message.EndsWith(".") ? "" : "."), 9000); State(done); }
            catch (Exception e) { Log.Write("host public failed: " + e.Message); Toast("bad", "Game not created", "The server could not be reached. Try again later.", 8000); State(done); }
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
            // live numbers for the home page too (every game, private ones only counted)
            if (signedIn)
                try { State(new Dictionary<string, object> { ["serverStats"] = (await api.Servers()).Obj("stats") }); } catch { }
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
                await SendNotes();
                if (launcherUpdate != null && launcherUpdate != previousLauncher)
                    Toast("warn", $"Launcher {launcherUpdate} available", "Update the launcher to keep playing.", 9000);
                // the classic maps pack split in three (2026-10-30): the old pack's files deleted, the player told to pick
                // the packs they want (DLC tab); the files of older releases never stay next to the new ones (PLAY too)
                if (GameInstall.IsGameRoot(settings.GameRoot) && !GameInstall.IsRunning())
                {
                    if (updater.DlcDefaults(settings.GameRoot)) State(new Dictionary<string, object> { ["classicSplit"] = true });
                    updater.RemoveObsolete(settings.GameRoot);
                }
                SendDlcState();
                if (GameInstall.IsGameRoot(settings.GameRoot) && launcherUpdate == null)
                {
                    gamePlan = await updater.Plan(settings.GameRoot);
                    var exeWas = exePatchPending;
                    exePatchPending = updater.ExePatchPending(settings.GameRoot);
                    if (exePatchPending && !exeWas && gamePlan.Count == 0 && previousVersion != manifestVersion)
                        Toast("info", "Game fix available", "Propnight.exe gets it when you press PLAY.", 7000);
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

        // ---- ban: any call the server refuses because the account is banned (Api.Banned). The running game shows it
        // through the mod (Win64\revival-ban.txt: a message in the menu, a red banner at its bottom, hosting refused);
        // the file goes at the next PLAY or sign-in that the server accepts.
        static string BanEnd(DateTime untilUtc) =>
            untilUtc.Year >= 9000 ? null : untilUtc.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        static string BanText(DateTime untilUtc, string reason)
        {
            var end = BanEnd(untilUtc);
            return (end == null ? "Your account is banned permanently." : "Your account is banned until " + end + ".")
                + (string.IsNullOrWhiteSpace(reason) ? "" : "\nReason: " + reason.Trim());
        }
        static string AuthMessage(ApiException e) => e.BannedUntil != null ? BanText(e.BannedUntil.Value, e.BanReason).Replace("\n", " ") : e.Message;

        // the game's text is plain ASCII: accents dropped, line breaks and anything else left out
        static string GameText(string text)
        {
            var plain = new string((text ?? "").Normalize(System.Text.NormalizationForm.FormD)
                .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
            return new string(plain.Select(c => c == '\r' || c == '\n' ? ' ' : c).Where(c => c >= 32 && c < 127).ToArray()).Trim();
        }

        void OnBanned(ApiException e)
        {
            if (IsHandleCreated && InvokeRequired) { BeginInvoke(new Action(() => OnBanned(e))); return; }
            var first = bannedUntil != e.BannedUntil;
            bannedUntil = e.BannedUntil;
            if (GameInstall.IsGameRoot(settings.GameRoot))
            {
                var until = e.BannedUntil.Value;
                var unix = until.Year >= 9000 ? 0 : (long)(until - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                try
                {
                    File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-ban.txt"),
                        $"until={unix}\nend={GameText(BanEnd(until) ?? "permanent")}\nreason={GameText(e.BanReason)}\n");
                }
                catch (Exception x) when (x is IOException || x is UnauthorizedAccessException) { Log.Write("ban file: " + x.Message); }
            }
            if (!first) return;
            Log.Write("account banned until " + e.BannedUntil.Value.ToString("o") + ": " + e.BanReason);
            if (signedIn) Toast("bad", "Account banned", BanText(e.BannedUntil.Value, e.BanReason).Replace("\n", " "), 30000);
        }

        void ClearBan()
        {
            bannedUntil = null;
            if (!GameInstall.IsGameRoot(settings.GameRoot)) return;
            try { File.Delete(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-ban.txt")); }
            catch (Exception x) when (x is IOException || x is UnauthorizedAccessException) { Log.Write("ban file: " + x.Message); }
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
            catch (Exception e) { State(new Dictionary<string, object> { ["authError"] = e is ApiException a ? AuthMessage(a) : e.Message }); }
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
            if (busy || playing || launcherUpdate == null) { SendUpdateState(); return; }
            busy = true;
            updatingLauncher = true;
            SendUpdateState();
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
                if (launcherUpdate == null) { busy = false; updatingLauncher = false; ShowIdleButton(); SendUpdateState(); return; }
                PlayButton("launcher", "UPDATING", $"Downloading launcher {launcherUpdate}…", -1, true, true);
                await reporting.InstallLauncherUpdate();
                Toast("good", "Launcher updated", "Restarting…", 3000);
                await Task.Delay(1200);
                closeConfirmed = true;   // no "game still running" question: the update must let this exe go
                Close();
                // the new launcher waits for this process to exit; anything still running after 5 s must not hold it
                var _ = Task.Delay(5000).ContinueWith(t => Environment.Exit(0));
            }
            catch (Exception e) { busy = false; updatingLauncher = false; SendUpdateState(); Toast("bad", "Launcher update failed", e.Message, 8000); ShowIdleButton(); }
        }

        async Task Play()
        {
            if (busy || playing) return;
            if (!GameInstall.IsGameRoot(settings.GameRoot)) { PickFolder(); return; }
            if (launcherUpdate != null) { await UpdateLauncher(); return; }   // UPDATE on the button: the new launcher, then a restart
            if (GameInstall.IsRunning()) { Toast("warn", "Propnight is already running", "Close it first, then press PLAY."); return; }
            if (ShowWarning()) { warnResumePlay = true; return; }   // PLAY again once the player confirmed it
            await RefreshMaintenance();
            if (maintenance != null) { Toast("warn", "Maintenance", maintenance, 9000); ShowIdleButton(); return; }
            // the button showed UPDATE: install and stop there, the player presses PLAY when they want to play
            // (joining a friend still starts the game)
            var updateOnly = pendingJoinPort == null && gamePlan != null && gamePlan.Count > 0;
            busy = true;
            ProfileSync sync = null;
            Reports reports = null, matches = null;
            List<FileStream> held = null;
            try
            {
                var work = new Updater(settings, (text, f) => PlayButton("update", "UPDATING", text, f, true, true));
                await work.LoadManifest();
                if (work.LauncherUpdate() != null) { updater = work; launcherUpdate = work.LauncherUpdate(); SendUpdateState(); Toast("warn", "Update required", "A new launcher was just published."); return; }
                var plan = await work.Plan(settings.GameRoot, fresh: true);
                await UpdateAndClean(work, plan);
                // anti-cheat: game files changed by hand were just put back; the moderators hear of it
                if (work.Changed.Count > 0)
                {
                    Log.Write("files changed outside the launcher, put back: " + string.Join(", ", work.Changed));
                    var _tamper = ReportTamper(work.Changed, "repaired");
                }
                if (plan.Count > 0) Toast("good", "Game updated", $"{plan.Count} file(s) installed." + (updateOnly ? " Press PLAY to start." : ""));
                gamePlan = new List<Dictionary<string, object>>();
                exePatchPending = false;
                manifestVersion = work.Manifest.Str("version");
                SendUpdateState();
                settings.GameBuild = Updater.BuildOf(work.Manifest);
                settings.Save();
                outdatedShown = null;
                // test channel (revival-beta.txt): the mod makes hosted games need 1 machine (quick end-of-match tests);
                // diagnostic channel (revival-diag.txt): the mod times its ticks and the frame rate (UE4SS.log, sent with a
                // bug report). The file holds the token the server signed for this channel's code (server channel.mjs, 48 h):
                // the mod takes the PC for a test channel only on it (channel.lua; 2026-11-02: an empty file made by hand
                // turned on the test channel's tools). The server unreachable: the token already there is kept.
                var betaFlag = Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-beta.txt");
                var diagFlag = Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-diag.txt");
                var channelFlag = Config.IsDiagCode(settings.BetaCode) ? diagFlag : Config.IsBetaCode(settings.BetaCode) ? betaFlag : null;
                foreach (var flag in new[] { betaFlag, diagFlag })
                    if (flag != channelFlag && File.Exists(flag)) File.Delete(flag);
                if (channelFlag != null)
                {
                    try
                    {
                        var token = (await api.ChannelToken(settings.BetaCode.Trim())).Str("token");
                        if (!string.IsNullOrEmpty(token)) File.WriteAllText(channelFlag, token);
                    }
                    catch (ApiException e) when (e.Status == 403)
                    {
                        // the account no longer has this channel (admin): no token, and the public release from now on
                        Log.Write("channel token: " + e.Message + ", back to the public release");
                        File.WriteAllText(channelFlag, "no token");
                        testChannel = null;
                        await ApplyTestChannel(null, false);
                    }
                    catch (Exception e) { Log.Write("channel token: " + e.Message); }
                    if (!File.Exists(channelFlag)) File.WriteAllText(channelFlag, "no token");
                }
                // the in-game admin menu (F10) is only published on the test channels: gone from a PC back on the public
                // release (the launcher never removes a file the manifest dropped; the mod also checks the flags above)
                if (!Config.IsBetaCode(settings.BetaCode)) GameInstall.RemoveMod(settings.GameRoot, GameInstall.AdminMod);
                // public release: no Lua mod but ours (the test channels keep whatever is installed: test mods)
                if (!Config.IsBetaCode(settings.BetaCode)) GameInstall.OnlyMods(settings.GameRoot, work.Manifest.Strs("modsAllowed")?.ToList());
                // public release: no DLL nor pak but the game's and ours; a game DLL changed stops here (Steam puts it back)
                if (!Config.IsBetaCode(settings.BetaCode))
                {
                    var changed = GameInstall.OnlyGameFiles(settings.GameRoot, work.Manifest);
                    if (changed.Count > 0)
                        throw new Exception($"Modified game file: {string.Join(", ", changed)}. In Steam: Propnight › Properties › Installed files › Verify integrity, then PLAY.");
                }
                if (updateOnly) return;

                // anti-cheat: the game's files held (read only) until the game ends, and checked once more: nothing changes
                // them between this check and the game reading them (UE4SS reads the scripts once, at start)
                PlayButton("update", "CHECKING", "Checking the game files…", -1, true, true);
                var lockChanged = new List<string>();
                held = await Task.Run(() => GameInstall.LockFiles(settings.GameRoot, work.Manifest, settings, lockChanged));
                if (lockChanged.Count > 0)
                {
                    Log.Write("files changed after the check, game not started: " + string.Join(", ", lockChanged));
                    await ReportTamper(lockChanged, "locked");
                    throw new Exception($"A game file changed or is open in another program: {string.Join(", ", lockChanged.Select(Path.GetFileName))}. Close that program before starting the game (the launcher puts the file back).");
                }
                // anti-cheat, public release: mods.txt held too, and no Lua mod but ours added since OnlyMods (an outline
                // of every player through walls is a few lines of Lua)
                modsAllowedNow = null;
                if (!Config.IsBetaCode(settings.BetaCode))
                {
                    modsAllowedNow = work.Manifest.Strs("modsAllowed")?.ToList();
                    try { var modList = GameInstall.LockModList(settings.GameRoot); if (modList != null) held.Add(modList); }
                    catch (IOException) { throw new Exception("mods.txt is open in another program. Close it before starting the game."); }
                    var foreign = GameInstall.ForeignMods(settings.GameRoot, modsAllowedNow);
                    if (foreign.Count > 0)
                    {
                        Log.Write("mods added after the check, game not started: " + string.Join(", ", foreign));
                        await ReportTool(foreign.Select(m => "Lua mod " + m).ToList(), "play");
                        throw new Exception($"Mods that are not the revival's were added: {string.Join(", ", foreign)}. Remove them before starting the game.");
                    }
                }
                // anti-cheat: no game while a memory editor is open (CheatTools)
                var tools = CheatTools.Open();
                if (tools.Count > 0)
                {
                    Log.Write("cheat tool open, game not started: " + string.Join(", ", tools));
                    await ReportTool(tools, "play");
                    throw new Exception($"Close {string.Join(", ", tools)} before starting the game.");
                }
                toolsReported.Clear();
                toolsCheckedAt = DateTime.UtcNow;

                GameInstall.SetIdentity(settings.GameRoot, settings.Username, settings.SteamId);
                ClearBan();
                sync = new ProfileSync(api, settings, t => PlayButton("play", "SYNCING", t, -1, true, true));
                await sync.BeforeLaunch();
                await CheckHostWarning();   // a match left in progress last time (Alt+F4 while hosting): the game warns at its menu

                PlayButton("play", "STARTING", "Launching Propnight…", -1, true, true);
                // where friends' games are (joinrelay): given by the server, so no published file holds it
                if (settings.RelayHost != null)
                    File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-relay.txt"), settings.RelayHost);
                if (pendingJoinPort != null)
                    File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-map-command.txt"),
                        pendingJoinHost != null ? $"joinrelay {pendingJoinHost} {pendingJoinPort}" : "joinrelay " + pendingJoinPort);
                pendingJoinPort = null;
                pendingJoinHost = null;
                if (pendingHostCommand != null)
                    File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-map-command.txt"), pendingHostCommand);
                pendingHostCommand = null;
                try { File.Delete(GameInstall.PresencePath(settings.GameRoot)); } catch (IOException) { }   // the mod writes it again
                // UE4SS starts a new UE4SS.log at each launch: the last session's one is kept for bug reports
                // (a player who crashed and played again lost the log of the crash, bug 14)
                try
                {
                    var ue4ssLog = Path.Combine(GameInstall.Win64(settings.GameRoot), "UE4SS.log");
                    if (File.Exists(ue4ssLog)) File.Copy(ue4ssLog, Path.Combine(GameInstall.Win64(settings.GameRoot), "UE4SS-previous.log"), true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                // the launcher has just checked the maintenance mode: the login mod skips its own check (no console)
                try { File.WriteAllText(Path.Combine(GameInstall.Win64(settings.GameRoot), "revival-launch.txt"), DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                // file proof (server proof.mjs): a ticket and its challenge; the challenge's nonce goes to the game, whose
                // RevivalGuard.dll hashes the scripts it loaded with it (and a secret this launcher does not have), then
                // ProofAnswer posts what it wrote. 2026-11-04: the DLL no longer posts it itself (antivirus took a DLL
                // with network for a trojan). Never stops the game.
                string proofTicket = null;
                var win64 = GameInstall.Win64(settings.GameRoot);
                var challengePath = Path.Combine(win64, "revival-proof-challenge.txt");
                var answerPath = Path.Combine(win64, "revival-proof-answer.txt");
                try
                {
                    foreach (var stale in new[] { Path.Combine(win64, "revival-proof.txt"), challengePath, answerPath })
                        if (File.Exists(stale)) File.Delete(stale);
                    var ticket = (await api.ProofTicket(settings.GameBuild)).Str("ticket");
                    var nonce = string.IsNullOrEmpty(ticket) ? null : (await api.ProofChallenge(ticket)).Str("nonce");
                    if (nonce != null && nonce.Length == 64) { File.WriteAllText(challengePath, nonce); proofTicket = ticket; }
                }
                catch (Exception e) { Log.Write("proof ticket: " + e.Message); }
                var exe = GameInstall.Exe(settings.GameRoot);
                var started = DateTime.UtcNow;
                // DirectX 11 (Settings > Game): the game's own switch, for a graphics card that crashes in DirectX 12
                var args = settings.DirectX11 ? "-dx11" : "";
                Process game;
                try { game = Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false }); }
                catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 740)
                {
                    // ERROR_ELEVATION_REQUIRED: Propnight.exe is set to "Run as administrator"; the shell shows the UAC prompt
                    Log.Write("play: Propnight.exe requires elevation, asking Windows");
                    game = Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true });
                }
                playing = true;
                Still();
                busy = false;
                if (proofTicket != null) { var _proof = ProofAnswer(proofTicket, answerPath, game); }
                lastPresence = DateTime.MinValue;   // friends see "playing" right away
                var _presence = RefreshFriends();
                sync.StartWatching();
                reports = Reports.PlayerReports(api, settings.GameRoot);
                reports.Start();
                matches = Reports.Matches(api, settings.GameRoot);
                matches.Sent = OverlayMatchSeen;   // the overlay's session counts keep the matches sent
                matches.Start();
                PlayButton("play", "IN GAME", "Keep the launcher open while you play: it saves your profile and brings your friends' invites into the game.", null, true, true);
                if (settings.MinimizeOnPlay) WindowState = FormWindowState.Minimized;   // -> the notification area; off by default: it stays open
                await Task.Run(() => game.WaitForExit());
                playing = false;
                // the game ended in a match (the mod's "match=1"): closed (Alt+F4, the task ended) or a crash
                var lastWhere = GameInstall.Presence(settings.GameRoot);
                if (lastWhere.TryGetValue("match", out var inMatch) && inMatch == "1")
                    await ReportLeave(CrashSince(started) ? "crash" : "closed", lastWhere.TryGetValue("role", out var lastRole) ? lastRole : "client");
                lastPresence = DateTime.MinValue;
                ShowFromTray();
                PlayButton("play", "SAVING", "Saving your profile…", -1, true, true);
                busy = true;
                await sync.AfterExit();
                await matches.Flush();
                var sentReports = await reports.Flush();
                if (sentReports > 0) Toast("good", "Reports sent", $"{sentReports} report(s) sent to the moderators.");
                Toast("good", "Profile saved", "See you next game!");
                if (!settings.DirectX11 && GpuCrashSince(started))
                    Toast("warn", "The game crashed on your graphics card", "Starting it with DirectX 11 usually fixes this (also in Settings › Game).", 20000,
                        new Dictionary<string, object> { ["label"] = "Use DirectX 11", ["cmd"] = "dx11On" });
            }
            catch (ApiException e) when (e.Status == 401 || e.Status == 403)
            {
                settings.Token = null; api.Token = null; settings.Save();
                ShowAuth(AuthMessage(e));
            }
            catch (Exception e)
            {
                Log.Write("play failed: " + e);
                Toast("bad", "Could not start", e.Message, 9000);
            }
            finally
            {
                if (held != null) foreach (var stream in held) stream.Dispose();
                sync?.Dispose();
                reports?.Dispose();
                matches?.Dispose();
                pendingJoinPort = null;
                playing = false;
                Still();
                busy = false;
                ShowIdleButton();
                warnDue = true;
                CheckWarnings();
            }
        }

        /// <summary>The file proof's answer: RevivalGuard.dll writes it when the mod starts (seconds after the game); waited
        /// for with a file watcher (no polling), up to 8 minutes (the server takes it up to 10 after the challenge), then
        /// posted; the scripts the server wants to see (changed or added, 5 at most, 64 KB each) are sent from the disk.</summary>
        async Task ProofAnswer(string ticket, string answerPath, Process game)
        {
            try
            {
                var written = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (var watcher = new FileSystemWatcher(Path.GetDirectoryName(answerPath), Path.GetFileName(answerPath)))
                {
                    watcher.Created += (s, e) => written.TrySetResult(true);
                    watcher.Renamed += (s, e) => written.TrySetResult(true);   // the DLL writes a .tmp, then renames it
                    watcher.EnableRaisingEvents = true;
                    var exited = Task.Run(() => game.WaitForExit());
                    if (!File.Exists(answerPath))
                        await Task.WhenAny(written.Task, exited, Task.Delay(TimeSpan.FromMinutes(8)));
                }
                if (!File.Exists(answerPath)) { Log.Write("proof: the game wrote no answer"); return; }
                Dictionary<string, object> answer = null;
                for (var tries = 0; answer == null && tries < 5; tries++)
                {
                    try { answer = Json.Parse(File.ReadAllText(answerPath)); }
                    catch (IOException) { await Task.Delay(200); }   // the rename's event can come before the file is free
                }
                File.Delete(answerPath);
                if (answer == null) { Log.Write("proof: answer unreadable"); return; }
                var reply = await api.ProofAnswer(ticket, answer);
                var want = reply?.Strs("want")?.ToList() ?? new List<string>();
                if (want.Count == 0) { Log.Write("proof: answer sent"); return; }
                var files = new Dictionary<string, object>();
                foreach (var path in want.Take(5))
                {
                    if (!path.StartsWith("Propnight/Binaries/Win64/Mods/") || !path.EndsWith(".lua") || path.Contains("..")) continue;
                    var full = Path.Combine(settings.GameRoot, path.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        using (var f = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            var bytes = new byte[Math.Min(f.Length, 65536)];
                            var got = 0;
                            while (got < bytes.Length) { var n = f.Read(bytes, got, bytes.Length - got); if (n <= 0) break; got += n; }
                            files[path] = System.Text.Encoding.UTF8.GetString(bytes, 0, got);
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                if (files.Count > 0) await api.ProofContent(ticket, files);
                Log.Write($"proof: answer sent, {files.Count} file(s) asked sent");
            }
            catch (Exception e) { Log.Write("proof answer: " + e.Message); }
        }

        // ---- warnings on the account (server: warnings.mjs), in the friends view: a window the player must confirm
        // ("I understand", after 10 s), then a bar for as long as one counts. Shown when the launcher opens, when the
        // game closes (never over the game) and when the player presses PLAY or joins a game.
        bool warnDue = true, warnResumePlay, lastInGame;
        long? warnShown;

        List<Dictionary<string, object>> UnseenWarnings() =>
            (lastFriendsView?.Obj("warnings")?.Objs("items") ?? Enumerable.Empty<Dictionary<string, object>>()).Where(w => !w.Bool("seen")).ToList();

        void CheckWarnings()
        {
            var inGame = playing || GameInstall.IsRunning();
            if (lastInGame && !inGame) warnDue = true;   // the game closed (also one started from Steam)
            lastInGame = inGame;
            if (!warnDue || inGame || busy || lastFriendsView == null) return;
            warnDue = false;
            ShowWarning();
        }

        /// <summary>The first warning not confirmed yet, in its window (true: one is shown).</summary>
        bool ShowWarning()
        {
            if (warnShown != null) return true;
            var w = UnseenWarnings().FirstOrDefault();
            if (w == null) return false;
            var all = lastFriendsView.Obj("warnings");
            warnShown = w.Long("id");
            ShowFromTray();
            State(new Dictionary<string, object> { ["type"] = "warning", ["warning"] = w, ["count"] = all.Long("count"), ["max"] = all.Long("max") });
            Log.Write($"warning {warnShown} shown ({all.Long("count")}/{all.Long("max")})");
            return true;
        }

        async Task WarningSeen(long id)
        {
            if (warnShown != id) return;
            try { await api.WarningSeen(id); }
            catch (Exception e) { Log.Write("warning seen: " + e.Message); }   // shown again at the next launch
            foreach (var w in lastFriendsView?.Obj("warnings")?.Objs("items") ?? Enumerable.Empty<Dictionary<string, object>>())
                if (w.Long("id") == id) w["seen"] = true;
            warnShown = null;
            Log.Write($"warning {id} confirmed");
            if (lastFriendsView != null) State(new Dictionary<string, object> { ["friends"] = lastFriendsView });
            if (ShowWarning()) return;
            if (warnResumePlay) { warnResumePlay = false; await Play(); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // closing the launcher never closes the game, but what it does while the game runs stops:
            // the page explains it and asks (window brought back if it was minimized)
            if ((playing || GameInstall.IsRunning()) && !closeConfirmed && e.CloseReason == CloseReason.UserClosing && web.CoreWebView2 != null)
            {
                e.Cancel = true;
                ShowFromTray();
                State(new Dictionary<string, object> { ["type"] = "confirmClose" });
                return;
            }
            base.OnFormClosing(e);
            if (!e.Cancel) { discord.Dispose(); overlay?.Dispose(); tray.Visible = false; tray.Dispose(); }
        }
    }
}
