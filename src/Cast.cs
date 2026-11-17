using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PropnightRevival
{
    /// <summary>
    /// The cast HUD (2026-11-14, tournaments and streams; the canvas "Spectator &amp; Broadcast HUD"): the mod writes the match
    /// as a spectator sees it (Win64/revival-cast.json, broadcast.lua, only while this player is dead or escaped). This
    /// launcher serves ui/cast.html and that state on this PC only (http://127.0.0.1:47615/): a streamer adds it to OBS as a
    /// browser source over the game capture. The same page is shown over the game to the spectator (Overlay.CastWindow).
    /// </summary>
    sealed class CastServer : IDisposable
    {
        readonly Func<string> win64;
        readonly byte[] page;
        TcpListener listener;
        public int Port { get; private set; }
        public string Url => Port == 0 ? null : $"http://127.0.0.1:{Port}/";

        public CastServer(Func<string> win64)
        {
            this.win64 = win64;
            using (var stream = typeof(CastServer).Assembly.GetManifestResourceStream("PropnightRevival.ui.cast.html"))
            using (var ms = new MemoryStream()) { stream.CopyTo(ms); page = ms.ToArray(); }
            for (var port = 47615; port < 47625 && listener == null; port++)
            {
                try { var l = new TcpListener(IPAddress.Loopback, port); l.Start(); listener = l; Port = port; }
                catch (SocketException) { }
            }
            if (listener == null) { Log.Write("cast: no free port (47615-47624)"); return; }
            Log.Write("cast: " + Url);
            Task.Run(Accept);
        }

        /// <summary>The state file, if the mod wrote it lately (a spectator's game writes it every second).</summary>
        public bool Fresh()
        {
            try
            {
                var w = win64();
                if (w == null) return false;
                var f = new FileInfo(Path.Combine(w, "revival-cast.json"));
                return f.Exists && DateTime.UtcNow - f.LastWriteTimeUtc < TimeSpan.FromSeconds(4);
            }
            catch (Exception) { return false; }
        }

        string State()
        {
            for (var i = 0; i < 3; i++)
            {
                try { if (Fresh()) return File.ReadAllText(Path.Combine(win64(), "revival-cast.json")); break; }
                catch (IOException) { System.Threading.Thread.Sleep(20); }   // being replaced by the mod
                catch (Exception) { break; }
            }
            return "{\"v\":1,\"active\":false}";
        }

        async Task Accept()
        {
            while (listener != null)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch (Exception) { return; }   // stopped
                var _ = Task.Run(() => Serve(client));
            }
        }

        void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 3000; client.SendTimeout = 3000;
                    var stream = client.GetStream();
                    var buf = new byte[8192]; var len = 0;
                    while (len < buf.Length)
                    {
                        var n = stream.Read(buf, len, buf.Length - len);
                        if (n <= 0) break;
                        len += n;
                        if (Encoding.ASCII.GetString(buf, 0, len).Contains("\r\n\r\n")) break;
                    }
                    var request = Encoding.ASCII.GetString(buf, 0, len);
                    var line = request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0].Split(' ');
                    var path = line.Length > 1 ? line[1].Split('?')[0] : "/";
                    // this PC's pages only (a web page elsewhere pointing a name at 127.0.0.1 has another Host)
                    var host = "";
                    foreach (var h in request.Split(new[] { "\r\n" }, StringSplitOptions.None))
                        if (h.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) host = h.Substring(5).Trim();
                    if (!(host.StartsWith("127.0.0.1") || host.StartsWith("localhost"))) { Reply(stream, "403 Forbidden", "text/plain", Encoding.ASCII.GetBytes("forbidden")); return; }
                    if (line[0] != "GET") Reply(stream, "405 Method Not Allowed", "text/plain", Encoding.ASCII.GetBytes("GET only"));
                    else if (path == "/" || path == "/index.html") Reply(stream, "200 OK", "text/html; charset=utf-8", page);
                    else if (path == "/state") Reply(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(State()));
                    else Reply(stream, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("not found"));
                }
                catch (Exception) { }   // the page went away
            }
        }

        static void Reply(Stream stream, string status, string type, byte[] body)
        {
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
        }

        public void Dispose()
        {
            var l = listener; listener = null;
            try { l?.Stop(); } catch (Exception) { }
        }
    }

    partial class Overlay
    {
        // the cast HUD is set aside (2026-11-14): off in the builds until it is taken up again
        public const bool CastReady = false;
        readonly CastServer cast;
        CastWindow castWin;
        public bool CastOn = true;
        public string CastUrl => cast?.Url;

        void CastWatch(bool gameInFront, Rectangle? area)
        {
            if (cast == null || cast.Port == 0) return;
            var run = CastOn && gameInFront && !IsOpen && area.HasValue && cast.Fresh() && !CastExclusive();
            if (run && castWin == null) castWin = new CastWindow(cast.Url + "?ingame=1");
            castWin?.Place(run ? area : null);
        }

        // an exclusive fullscreen game is minimized by any window over it: no window then (OBS still has the page)
        DateTime exclusiveAt; bool exclusive;
        bool CastExclusive()
        {
            if (DateTime.UtcNow - exclusiveAt > TimeSpan.FromSeconds(20)) { exclusive = ExclusiveFullscreen(); exclusiveAt = DateTime.UtcNow; }
            return exclusive;
        }

        readonly Timer castTimer = new Timer { Interval = 500 };
        /// <summary>The cast HUD's own watch (the overlay's is not started while the overlay is off).</summary>
        public void StartCast()
        {
            castTimer.Tick += (s, e) =>
            {
                if (watch.Enabled) return;   // the overlay's watch does it
                var front = GetForegroundWindow();
                GetWindowThreadProcessId(front, out var pid);
                var inFront = pid != 0 && IsGameProcess(pid);
                if (inFront) gameWindow = front;
                else if (gameWindow != IntPtr.Zero && !IsWindow(gameWindow)) { gameWindow = IntPtr.Zero; CastGone(); }
                CastWatch(inFront, inFront ? GameArea() : null);
            };
            castTimer.Start();
        }

        void CastGone()
        {
            var w = castWin; castWin = null;
            try { w?.Dispose(); } catch (Exception e) { Log.Write("cast window: " + e.Message); }
        }

        /// <summary>
        /// The cast page over the game for the spectator: drawn by a hidden WebView2, captured (PNG with its alpha) after
        /// each change the page reports, shown by a layered window that never takes the focus nor a click (clicks go to the
        /// game: switching the followed player). A transparent WebView2 over the game cannot let clicks through.
        /// </summary>
        sealed class CastWindow : LayeredWindow
        {
            readonly string url;
            readonly HiddenHost host = new HiddenHost();
            WebView2 web;
            Rectangle? area;
            bool ready, capturing, again, active;
            Bitmap picture;

            sealed class HiddenHost : Form
            {
                public HiddenHost()
                {
                    FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
                    Bounds = new Rectangle(-32000, -32000, 1920, 1080);
                }
                protected override bool ShowWithoutActivation => true;
                protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; } }
            }

            public CastWindow(string url)
            {
                this.url = url;
                web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Transparent };
                host.Controls.Add(web);
                host.Show();
                var _ = Init();
            }

            async Task Init()
            {
                try
                {
                    var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Config.DataDir, "WebView2"));
                    await web.EnsureCoreWebView2Async(env);
                    var core = web.CoreWebView2;
                    core.Settings.AreDevToolsEnabled = false;
                    core.Settings.AreDefaultContextMenusEnabled = false;
                    core.Settings.IsStatusBarEnabled = false;
                    core.NewWindowRequested += (s, e) => e.Handled = true;
                    core.NavigationStarting += (s, e) => { if (!e.Uri.StartsWith(url.Split('?')[0])) e.Cancel = true; };
                    core.WebMessageReceived += (s, e) =>
                    {
                        var m = Json.Parse(e.WebMessageAsJson);
                        if (m.Str("cmd") != "frame") return;
                        active = m.Bool("active");
                        var __ = Snap();
                    };
                    core.Navigate(url);
                    ready = true;
                    Log.Write("cast window: ready");
                }
                catch (Exception e) { Log.Write("cast window: " + e.Message); }
            }

            /// <summary>Over this part of the screen (the game's), or hidden (null).</summary>
            public void Place(Rectangle? where)
            {
                var moved = where != area;
                area = where;
                if (where == null) { if (Visible) Hide(); return; }
                if (host.ClientSize != where.Value.Size) host.ClientSize = where.Value.Size;
                if (moved) { var _ = Snap(); }
            }

            async Task Snap()
            {
                if (!ready) return;
                if (capturing) { again = true; return; }
                capturing = true;
                try
                {
                    do
                    {
                        again = false;
                        if (area == null || !active) { if (Visible) Hide(); continue; }
                        Bitmap bmp;
                        using (var ms = new MemoryStream())
                        {
                            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                            ms.Position = 0;
                            bmp = new Bitmap(ms);
                        }
                        if (area == null || !active) { bmp.Dispose(); if (Visible) Hide(); continue; }
                        if (!Visible) Show();
                        Present(bmp, area.Value.Location, 1);
                        picture?.Dispose();
                        picture = bmp;
                    } while (again);
                }
                catch (Exception e) { Log.Write("cast capture: " + e.Message); }
                finally { capturing = false; }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { try { web?.Dispose(); host.Dispose(); } catch (Exception) { } picture?.Dispose(); }
                base.Dispose(disposing);
            }
        }
    }
}
