using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
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
    /// The in-game overlay (2026-11-01, the validated canvas "In-game overlay"): Shift + Tab over the game shows a window of
    /// this launcher on top of it (ui/overlay.html in WebView2: friends, invites, this session, this game, quick settings,
    /// shortcuts), with a blurred capture of the game behind. The first try drew it inside the game (mod UMG widgets): plain
    /// boxes, a freeze at each opening. Closed, small notifications show one at a time in a top corner of the game (a friend
    /// online, an invite), and the session timer in a corner if chosen: layered windows that never take the focus.
    /// The game must be in windowed or borderless fullscreen (an exclusive fullscreen game would be minimized by any window).
    /// The game side (mod overlay.lua) gives the match's state and applies the game's settings through two files.
    /// Cost: one foreground window check every 400 ms while the launcher runs; the hotkey is registered only while the game
    /// (or this overlay) is in front, so Shift + Tab stays free for the other programs.
    /// </summary>
    class Overlay : IDisposable
    {
        readonly Func<Dictionary<string, object>> state;
        readonly Func<Dictionary<string, object>, Task> action;
        readonly Func<string> win64;
        readonly Action<string> warn;
        readonly Timer watch = new Timer { Interval = 400 };
        readonly Timer refresh = new Timer { Interval = 1000 };
        readonly HotkeyWindow hotkey;
        readonly Toaster toaster;
        readonly CornerTimer corner;
        OverlayForm form;
        IntPtr gameWindow;
        int gamePid;
        public DateTime? GameStart { get; private set; }
        public bool IsOpen => form != null && form.Visible;
        public bool TimerOn, ToastsRight, FpsOn;
        public Action GameStarted;   // a new game process in front (its FPS counter is asked again)

        public Overlay(Func<Dictionary<string, object>> state, Func<Dictionary<string, object>, Task> action, Func<string> win64, Action<string> warn)
        {
            this.state = state; this.action = action; this.win64 = win64; this.warn = warn;
            hotkey = new HotkeyWindow(() => Toggle());
            toaster = new Toaster(a => { var _ = action(a); });
            corner = new CornerTimer();
            watch.Tick += (s, e) => Watch();
            refresh.Tick += (s, e) => Push(false);
        }

        /// <summary>Watches the game (hotkey, notifications, corner): not started while the overlay is off (ShellForm.OverlayOn).</summary>
        public void Start() => watch.Start();

        // ---- the game's window: in front, its process start (the session's time)
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        int lastPid = -1; bool lastIsGame;
        bool liveFailed;   // the live window could not start once: the capture from then on
        DateTime openedAt;

        bool IsGameProcess(int pid)
        {
            if (pid == lastPid) return lastIsGame;
            lastPid = pid;
            try { using (var p = Process.GetProcessById(pid)) lastIsGame = string.Equals(p.ProcessName, "Propnight", StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { lastIsGame = false; }
            return lastIsGame;
        }

        void Watch()
        {
            var front = GetForegroundWindow();
            GetWindowThreadProcessId(front, out var pid);
            var gameInFront = pid != 0 && IsGameProcess(pid);
            if (gameInFront)
            {
                gameWindow = front;
                if (pid != gamePid)
                {
                    gamePid = pid;
                    try { using (var p = Process.GetProcessById(pid)) GameStart = p.StartTime.ToUniversalTime(); } catch (Exception) { GameStart = DateTime.UtcNow; }
                    var a = GameArea();
                    Log.Write("overlay: game in front (" + (ExclusiveFullscreen() ? "exclusive fullscreen: no overlay" : "windowed / borderless")
                        + (a.HasValue ? $", {a.Value.Width} x {a.Value.Height}" : ", no window area") + ")");
                    GameStarted?.Invoke();
                }
            }
            else if (gameWindow != IntPtr.Zero && (!IsWindow(gameWindow) || GameExited()))
                GameGone();
            // never left over the desktop (2026-11-01: the game closed with the overlay open, its invisible full-screen
            // window took every click until the launcher was closed): no game, no overlay
            if (IsOpen && (gameWindow == IntPtr.Zero || !IsWindow(gameWindow) || GameExited()))
                GameGone();
            // the game in front again (Alt + Tab, the taskbar) with the overlay open: it is behind the game now, closed
            // (Shift + Tab opens it again over the game)
            if (IsOpen && gameInFront && DateTime.UtcNow - openedAt > TimeSpan.FromSeconds(1)) Close(false);
            hotkey.Enabled = gameInFront || IsOpen;
            var area = GameArea();
            toaster.Showing = gameInFront && !IsOpen && area.HasValue;
            if (area.HasValue) toaster.Place(area.Value, ToastsRight);
            corner.Update((TimerOn || FpsOn) && gameInFront && !IsOpen && area.HasValue ? area : null, CornerText());
        }

        [DllImport("user32.dll")] static extern IntPtr GetCapture();

        /// <summary>
        /// The game closed: the overlay's window is destroyed, not only hidden, and the notification and corner windows hidden.
        /// 2026-11-01, still after 1.54.8: the desktop took no click (no icon, no selection) after the game closed, the overlay
        /// already closed, until the launcher was closed. The overlay's window takes the mouse capture at each press (a slider
        /// drag): a capture left on it (hidden or not) is the likely cause; destroying the window ends it, as quitting did.
        /// </summary>
        void GameGone()
        {
            var capture = GetCapture();
            Log.Write("overlay: game closed (overlay " + (IsOpen ? "open" : "closed") + ", mouse capture "
                + (capture == IntPtr.Zero ? "none" : form != null && !form.IsDisposed && capture == form.Handle ? "the overlay's" : "another window's")
                + ", notification " + (toaster.Visible ? "shown" : "hidden") + ", corner " + (corner.Visible ? "shown" : "hidden") + ")");
            gameWindow = IntPtr.Zero; gamePid = 0; GameStart = null;
            if (IsOpen) Close(false);
            toaster.Clear();
            corner.Update(null, null);
            if (form != null)
            {
                var old = form;
                form = null;
                try { old.Capture = false; old.Dispose(); } catch (Exception e) { Log.Write("overlay dispose: " + e.Message); }
            }
        }

        /// <summary>The game's process ended (its window may outlive it a moment, or belong to a dialog that stays).</summary>
        bool GameExited()
        {
            if (gamePid == 0) return true;
            try { using (var p = Process.GetProcessById(gamePid)) return p.HasExited; }
            catch (ArgumentException) { return true; }   // no such process any more
            catch (Exception) { return false; }
        }

        /// <summary>The corner pill: the session's time and / or the game's FPS (the mod's revival-overlay-fps.txt, fresh).</summary>
        string CornerText()
        {
            var parts = new List<string>();
            if (TimerOn && GameStart.HasValue)
            {
                var t = DateTime.UtcNow - GameStart.Value;
                parts.Add($"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}");
            }
            if (FpsOn)
                try
                {
                    var file = new FileInfo(Path.Combine(win64(), "revival-overlay-fps.txt"));
                    if (file.Exists && DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromSeconds(4))
                        parts.Add(File.ReadAllText(file.FullName).Trim() + " fps");
                }
                catch (Exception) { }
            return parts.Count > 0 ? string.Join("  ·  ", parts) : null;
        }

        Rectangle? GameArea()
        {
            if (gameWindow == IntPtr.Zero || !IsWindow(gameWindow) || IsIconic(gameWindow) || !GetWindowRect(gameWindow, out var r)) return null;
            var area = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            return area.Width < 200 || area.Height < 200 ? (Rectangle?)null : area;
        }

        // ---- open / close
        public void Toggle()
        {
            if (IsOpen) Close(true); else Open();
        }

        void Open()
        {
            var area = GameArea();
            Log.Write("overlay: Shift + Tab pressed");
            if (!area.HasValue)
            {
                Log.Write("overlay: not opened, no game window (" + (gameWindow == IntPtr.Zero ? "none in front" : IsIconic(gameWindow) ? "minimized" : "too small") + ")");
                return;
            }
            if (ExclusiveFullscreen())
            {
                warn("The overlay needs the game in Windowed fullscreen: Esc › Settings › Graphics › Window mode.");
                // said over the game too (the launcher's message is behind it)
                Notify("Overlay", "needs Windowed fullscreen", "Esc › Settings › Graphics › Window mode", true, 7);
                return;
            }
            string backdrop = null;
            if (liveFailed)
                try { backdrop = Capture(area.Value); } catch (Exception e) { Log.Write("overlay capture: " + e.Message); }
            try { File.WriteAllText(Path.Combine(win64(), "revival-overlay-open.txt"), "1"); } catch (Exception) { }
            // stays open until Shift + Tab, Esc or X (a click on another screen does not close it: 2026-11-01)
            if (form == null || form.IsDisposed) form = new OverlayForm(!liveFailed, m => OnMessage(m), LiveFailed);
            form.Bounds = area.Value;
            toaster.Showing = false;
            corner.Update(null, null);
            openedAt = DateTime.UtcNow;
            form.Show();
            form.Activate();
            form.Post(new Dictionary<string, object> { ["open"] = true, ["backdrop"] = backdrop ?? "" }, state);
            refresh.Start();
            Log.Write("overlay opened");
        }

        public void Close(bool backToGame)
        {
            refresh.Stop();
            if (form != null)
            {
                form.Capture = false;   // never kept by the hidden window (the desktop's clicks)
                if (form.Visible) form.Hide();
            }
            try { File.Delete(Path.Combine(win64(), "revival-overlay-open.txt")); } catch (Exception) { }
            // the next opening reads a fresh answer of the mod, not this one (a volume changed just before closing)
            try { File.Delete(Path.Combine(win64(), "revival-overlay-state.json")); } catch (Exception) { }
            if (backToGame && gameWindow != IntPtr.Zero && IsWindow(gameWindow)) SetForegroundWindow(gameWindow);
            Log.Write("overlay closed");
        }

        /// <summary>The live window could not start: closed, opened again with the game's capture behind.</summary>
        void LiveFailed()
        {
            liveFailed = true;
            var broken = form;
            form = null;
            refresh.Stop();
            broken?.BeginInvoke((Action)(() => { broken.Hide(); broken.Dispose(); Open(); }));
        }

        async Task OnMessage(Dictionary<string, object> m)
        {
            switch (m.Str("cmd"))
            {
                case "ready": Push(true); return;
                case "close": Close(true); return;
            }
            try { await action(m); }
            catch (Exception e) { Log.Write("overlay action " + m.Str("cmd") + ": " + e.Message); }
            Push(false);
        }

        /// <summary>The page's state again (each second while open, after an action, or when the launcher learns something).</summary>
        public void Push(bool full)
        {
            if (!IsOpen) return;
            form.Post(null, state);
        }

        /// <summary>A notification (a friend online, an invite): one at a time, over the game only.</summary>
        public void Notify(string name, string text, string sub, bool accent, double holdSeconds, Dictionary<string, object> accept = null, Dictionary<string, object> later = null)
        {
            toaster.Add(new Toaster.Item { Name = name, Text = text, Sub = sub, Accent = accent, Hold = holdSeconds, Accept = accept, Later = later, At = DateTime.UtcNow });
        }

        /// <summary>The overlay's window (a file dialog opened from it shows on top).</summary>
        public IWin32Window Owner => form;

        public bool GameInFront => GetForegroundWindow() == gameWindow && gameWindow != IntPtr.Zero;

        /// <summary>The game's window mode (its GameUserSettings.ini): 0 is exclusive fullscreen.</summary>
        public static bool ExclusiveFullscreen()
        {
            try
            {
                var ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Propnight\Saved\Config\WindowsNoEditor\GameUserSettings.ini");
                return File.Exists(ini) && File.ReadAllLines(ini).Any(l => l.Trim() == "FullscreenMode=0");
            }
            catch (Exception) { return false; }
        }

        /// <summary>The game's picture at the opening, small (it is blurred behind the overlay): a JPEG data URL.</summary>
        static string Capture(Rectangle area)
        {
            using (var full = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(full)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                var k = Math.Min(1.0, 960.0 / area.Width);
                using (var small = new Bitmap(full, Math.Max(1, (int)(area.Width * k)), Math.Max(1, (int)(area.Height * k))))
                using (var ms = new MemoryStream())
                {
                    var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    var p = new EncoderParameters(1);
                    p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 72L);
                    small.Save(ms, jpeg, p);
                    return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        public void Dispose()
        {
            watch.Dispose(); refresh.Dispose(); hotkey.Dispose(); toaster.Dispose(); corner.Dispose();
            try { File.Delete(Path.Combine(win64(), "revival-overlay-open.txt")); } catch (Exception) { }
            form?.Dispose();
        }

        // ---- the window over the game. Live (2026-11-01, the player wants the game moving behind): the page is drawn with a
        // transparent background through DirectComposition (WebView2's visual hosting: the window has no picture of its own,
        // WS_EX_NOREDIRECTIONBITMAP, the mouse is passed to the page by hand), Windows blurs what is behind the window
        // (acrylic accent), so the game keeps running, blurred, under the panels. If that cannot start: the first version,
        // a capture of the game taken at the opening as the page's background.
        class OverlayForm : Form
        {
            readonly bool live;
            readonly Func<Dictionary<string, object>, Task> onMessage;
            readonly Action failed;
            WebView2 web;                                 // the fallback (windowed hosting)
            CoreWebView2CompositionController comp;       // live
            IDCompositionDevice dcomp; IDCompositionTarget target; IDCompositionVisual visual;
            CoreWebView2 core;
            bool ready, tracking;
            Dictionary<string, object> pending;
            Func<Dictionary<string, object>> pendingState;
            public bool Live => live;

            public OverlayForm(bool live, Func<Dictionary<string, object>, Task> onMessage, Action failed)
            {
                this.live = live; this.onMessage = onMessage; this.failed = failed;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                // not on top of everything: just over the game, like a window of it (2026-11-01: the Task Manager opened
                // behind it and could not be clicked). Shown above the game when opened; the game in front again: closed
                TopMost = false;
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.FromArgb(8, 11, 20);
                Text = "Propnight Revival overlay";
                if (!live)
                {
                    web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(8, 11, 20) };
                    Controls.Add(web);
                }
                Load += async (s, e) => await Init();
                Resize += (s, e) => { if (comp != null) comp.Bounds = ClientRectangle; };
                Activated += (s, e) => { try { comp?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); } catch (Exception) { } };
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x80;                       // WS_EX_TOOLWINDOW: not in Alt + Tab
                    if (live) cp.ExStyle |= 0x00200000;       // WS_EX_NOREDIRECTIONBITMAP: only the page's visual is drawn
                    return cp;
                }
            }

            async Task Init()
            {
                CoreWebView2Environment env;
                try { env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Config.DataDir, "WebView2")); }
                catch (Exception e) { Log.Write("overlay WebView2: " + e.Message); return; }
                if (live)
                {
                    try
                    {
                        var iid = typeof(IDCompositionDevice).GUID;
                        Marshal.ThrowExceptionForHR(DCompositionCreateDevice2(IntPtr.Zero, ref iid, out var device));
                        dcomp = (IDCompositionDevice)device;
                        dcomp.CreateTargetForHwnd(Handle, true, out target);
                        dcomp.CreateVisual(out visual);
                        target.SetRoot(visual);
                        comp = await env.CreateCoreWebView2CompositionControllerAsync(Handle);
                        comp.DefaultBackgroundColor = Color.Transparent;
                        comp.RootVisualTarget = visual;
                        comp.Bounds = ClientRectangle;
                        comp.IsVisible = true;
                        dcomp.Commit();
                        comp.CursorChanged += (s, e) => { try { Cursor = new Cursor(comp.Cursor); } catch (Exception) { } };
                        core = comp.CoreWebView2;
                        Blur(true);
                        Log.Write("overlay: live (composition)");
                    }
                    catch (Exception e)
                    {
                        Log.Write("overlay: live window unavailable, the game's capture instead (" + e.Message + ")");
                        failed();
                        return;
                    }
                }
                else
                {
                    try { await web.EnsureCoreWebView2Async(env); }
                    catch (Exception e) { Log.Write("overlay WebView2: " + e.Message); return; }
                    core = web.CoreWebView2;
                }
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.NewWindowRequested += (s, e) => e.Handled = true;
                core.NavigationStarting += (s, e) => { if (!e.Uri.StartsWith("data:") && e.Uri != "about:blank") e.Cancel = true; };
                core.WebMessageReceived += async (s, e) =>
                {
                    var m = Json.Parse(e.WebMessageAsJson);
                    if (m.Str("cmd") == "ready") { ready = true; Flush(); }
                    await onMessage(m);
                };
                using (var stream = typeof(Overlay).Assembly.GetManifestResourceStream("PropnightRevival.ui.overlay.html"))
                using (var reader = new StreamReader(stream)) core.NavigateToString(reader.ReadToEnd());
                try { comp?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); } catch (Exception) { }
            }

            /// <summary>Windows' blur of what is behind the window (the game), with a dark tint: the canvas's blurred game.</summary>
            void Blur(bool on)
            {
                try
                {
                    // ACCENT_ENABLE_ACRYLICBLURBEHIND, tint AABBGGRR: #05070C at 38 %
                    var accent = new AccentPolicy { AccentState = on ? 4 : 0, GradientColor = 0x600C0705 };
                    var size = Marshal.SizeOf(accent);
                    var ptr = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.StructureToPtr(accent, ptr, false);
                        var data = new CompositionAttributeData { Attribute = 19, Data = ptr, SizeOfData = size };   // WCA_ACCENT_POLICY
                        SetWindowCompositionAttribute(Handle, ref data);
                    }
                    finally { Marshal.FreeHGlobal(ptr); }
                }
                catch (Exception e) { Log.Write("overlay blur: " + e.Message); }
            }

            /// <summary>Sends extra fields (opening, backdrop) and the state; kept until the page is ready.</summary>
            public void Post(Dictionary<string, object> extra, Func<Dictionary<string, object>> state)
            {
                if (extra != null) pending = pending == null ? extra : pending.Concat(extra).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value);
                pendingState = state;
                if (ready) Flush();
            }

            void Flush()
            {
                if (core == null || pendingState == null) return;
                Dictionary<string, object> s;
                try { s = pendingState(); } catch (Exception e) { Log.Write("overlay state: " + e.Message); return; }
                if (pending != null) foreach (var p in pending) s[p.Key] = p.Value;
                pending = null;
                core.PostWebMessageAsJson(Json.Write(s));
            }

            // the mouse, passed to the page (visual hosting has no window of its own to receive it). The message numbers are
            // CoreWebView2MouseEventKind's values (WM_MOUSEMOVE 0x200 ... WM_MOUSEHWHEEL 0x20E, WM_MOUSELEAVE 0x2A3)
            [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TrackMouse tme);
            [StructLayout(LayoutKind.Sequential)] struct TrackMouse { public int Size, Flags; public IntPtr Window; public int HoverTime; }
            protected override void WndProc(ref Message m)
            {
                if (comp != null && ((m.Msg >= 0x200 && m.Msg <= 0x20E) || m.Msg == 0x2A3))
                {
                    try { Mouse(ref m); } catch (Exception e) { Log.Write("overlay mouse: " + e.Message); }
                    m.Result = IntPtr.Zero;
                    return;
                }
                base.WndProc(ref m);
            }

            void Mouse(ref Message m)
            {
                var msg = m.Msg;
                if (msg == 0x2A3) { tracking = false; comp.SendMouseInput(CoreWebView2MouseEventKind.Leave, CoreWebView2MouseEventVirtualKeys.None, 0, Point.Empty); return; }
                if (!tracking)
                {
                    var t = new TrackMouse { Size = Marshal.SizeOf(typeof(TrackMouse)), Flags = 0x2, Window = Handle };   // TME_LEAVE
                    tracking = TrackMouseEvent(ref t);
                }
                var w = m.WParam.ToInt64(); var l = m.LParam.ToInt64();
                var point = new Point((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF));
                uint data = 0;
                if (msg == 0x20A || msg == 0x20E) { point = PointToClient(point); data = (uint)(short)((w >> 16) & 0xFFFF); }   // wheels: screen point
                else if (msg >= 0x20B && msg <= 0x20D) data = (uint)((w >> 16) & 0xFFFF);                                       // X buttons
                if (msg == 0x201 || msg == 0x204 || msg == 0x207) Capture = true;    // a drag (a slider) goes on outside the window
                if (msg == 0x202 || msg == 0x205 || msg == 0x208) Capture = false;
                comp.SendMouseInput((CoreWebView2MouseEventKind)msg, (CoreWebView2MouseEventVirtualKeys)(w & 0xFFFF), data, point);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { try { comp?.Close(); } catch (Exception) { } web?.Dispose(); }
                base.Dispose(disposing);
            }

            // DirectComposition: the device, a target on this window, a root visual for the page
            [DllImport("dcomp.dll")] static extern int DCompositionCreateDevice2(IntPtr renderingDevice, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object device);
            [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionAttributeData data);
            [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int AccentState, AccentFlags; public uint GradientColor; public int AnimationId; }
            [StructLayout(LayoutKind.Sequential)] struct CompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }
        }

        [ComImport, Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDCompositionDevice
        {
            void Commit();
            void WaitForCommitCompletion();
            void GetFrameStatistics(IntPtr statistics);
            void CreateTargetForHwnd(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IDCompositionTarget target);
            void CreateVisual(out IDCompositionVisual visual);
        }
        [ComImport, Guid("eacdd04c-117e-4e17-88f4-d1b12b0e3d89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDCompositionTarget { void SetRoot(IDCompositionVisual visual); }
        [ComImport, Guid("4d93059d-097b-4651-9a60-f0f25116e2f3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDCompositionVisual { }

        // ---- Shift + Tab, registered only while the game (or the overlay) is in front
        class HotkeyWindow : NativeWindow, IDisposable
        {
            [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
            [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
            const int Id = 0x5052;
            readonly Action pressed;
            bool on, failLogged;

            public HotkeyWindow(Action pressed)
            {
                this.pressed = pressed;
                CreateHandle(new CreateParams());
            }

            public bool Enabled
            {
                set
                {
                    if (value == on) return;
                    if (value)
                    {
                        on = RegisterHotKey(Handle, Id, 0x4 | 0x4000, 0x09);   // MOD_SHIFT | MOD_NOREPEAT, VK_TAB
                        // refused (another program holds Shift + Tab: error 1409): said once until it works (tried every 400 ms)
                        if (!on && !failLogged) { failLogged = true; Log.Write("overlay: Shift + Tab refused by Windows (error " + Marshal.GetLastWin32Error() + ", another program uses it?)"); }
                        if (on && failLogged) { failLogged = false; Log.Write("overlay: Shift + Tab registered"); }
                    }
                    else { UnregisterHotKey(Handle, Id); on = false; }
                }
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0312 && (int)m.WParam == Id) { try { pressed(); } catch (Exception e) { Log.Write("overlay hotkey: " + e.Message); } }
                base.WndProc(ref m);
            }

            public void Dispose() { Enabled = false; DestroyHandle(); }
        }

        // ---- windows drawn with per-pixel alpha that never take the focus (notifications, the corner timer)
        abstract class LayeredWindow : Form
        {
            [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pprSrc, int crKey, ref Blend pblend, int dwFlags);
            [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
            [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
            [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
            [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
            [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
            [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
            [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Blend { public byte Op, Flags, Alpha, Format; }

            protected LayeredWindow()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
            }
            protected override bool ShowWithoutActivation => true;
            protected virtual bool ClickThrough => true;
            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    // WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST (| WS_EX_TRANSPARENT: clicks go to the game)
                    cp.ExStyle |= 0x80000 | 0x80 | 0x08000000 | 0x8 | (ClickThrough ? 0x20 : 0);
                    return cp;
                }
            }
            protected const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
                base.WndProc(ref m);
            }

            [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
            [StructLayout(LayoutKind.Sequential)] struct BitmapInfo { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant; }
            Bitmap shownBmp; IntPtr shownHbmp;

            /// <summary>
            /// A picture as UpdateLayeredWindow wants it: a top-down 32-bit DIB with premultiplied alpha (GetHbitmap gave
            /// straight alpha: the half-transparent edges and the text's smoothing came out light, a white rim, 2026-11-01).
            /// </summary>
            static IntPtr PremultipliedDib(Bitmap bmp, IntPtr dc)
            {
                var data = bmp.LockBits(new Rectangle(Point.Empty, bmp.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var bytes = new byte[bmp.Width * 4 * bmp.Height];
                for (var y = 0; y < bmp.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * bmp.Width * 4, bmp.Width * 4);
                bmp.UnlockBits(data);
                for (var i = 0; i < bytes.Length; i += 4)
                {
                    var a = bytes[i + 3];
                    if (a == 255) continue;
                    bytes[i] = (byte)(bytes[i] * a / 255); bytes[i + 1] = (byte)(bytes[i + 1] * a / 255); bytes[i + 2] = (byte)(bytes[i + 2] * a / 255);
                }
                var info = new BitmapInfo { Size = 40, Width = bmp.Width, Height = -bmp.Height, Planes = 1, BitCount = 32 };
                var hbmp = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
                Marshal.Copy(bytes, 0, bits, bytes.Length);
                return hbmp;
            }

            /// <summary>Puts a 32-bit ARGB picture on screen at a place, with an overall opacity (0..1).</summary>
            protected void Present(Bitmap bmp, Point at, double opacity)
            {
                if (!IsHandleCreated) CreateHandle();
                var screen = GetDC(IntPtr.Zero);
                // converted once per picture (the notification's animation shows the same one 60 times a second)
                if (!ReferenceEquals(bmp, shownBmp))
                {
                    if (shownHbmp != IntPtr.Zero) DeleteObject(shownHbmp);
                    shownHbmp = PremultipliedDib(bmp, screen);
                    shownBmp = bmp;
                }
                var mem = CreateCompatibleDC(screen);
                var old = SelectObject(mem, shownHbmp);
                try
                {
                    var size = bmp.Size; var src = Point.Empty;
                    var blend = new Blend { Op = 0, Flags = 0, Alpha = (byte)Math.Max(0, Math.Min(255, opacity * 255)), Format = 1 };
                    UpdateLayeredWindow(Handle, screen, ref at, ref size, mem, ref src, 0, ref blend, 2);
                }
                finally
                {
                    SelectObject(mem, old); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
                }
            }

            protected static GraphicsPath Rounded(RectangleF r, float radius)
            {
                var p = new GraphicsPath(); var d = radius * 2;
                p.AddArc(r.Left, r.Top, d, d, 180, 90); p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }
            protected static readonly Color Ink = Color.FromArgb(0xF4, 0xF2, 0xEF), Muted = Color.FromArgb(0xC3, 0xC1, 0xCF), Faint = Color.FromArgb(0x9C, 0x9D, 0xB0),
                Accent = Color.FromArgb(0xEF, 0x45, 0x50), Good = Color.FromArgb(0x4A, 0xDE, 0x80);
        }

        // ---- the notifications: one at a time, 3 s apart, sliding in and fading out
        class Toaster : LayeredWindow
        {
            public class Item
            {
                public string Name, Text, Sub; public bool Accent; public double Hold; public DateTime At;
                public Dictionary<string, object> Accept, Later;
            }
            const double In = 0.28, Out = 0.22, Gap = 3.0;
            readonly Queue<Item> queue = new Queue<Item>();
            readonly Timer frame = new Timer { Interval = 16 };
            readonly Action<Dictionary<string, object>> act;
            readonly Stopwatch clock = Stopwatch.StartNew();
            Item current; double shownAt, nextAt;
            Bitmap picture; Rectangle area; bool right, showing;
            RectangleF acceptBox, laterBox; float scale = 1;

            public Toaster(Action<Dictionary<string, object>> act)
            {
                this.act = act;
                frame.Tick += (s, e) => Step();
                MouseClick += (s, e) => Clicked(e.Location);
            }
            protected override bool ClickThrough => false;   // its buttons (Accept / Later) take the click, never the focus

            public bool Showing
            {
                set
                {
                    if (value == showing) return;
                    showing = value;
                    if (!value && current != null) { Hide(); current = null; nextAt = clock.Elapsed.TotalSeconds + Gap; }
                    if (value && queue.Count > 0) frame.Start();
                }
            }

            public void Place(Rectangle game, bool onRight) { area = game; right = onRight; scale = Math.Max(0.6f, game.Height / 1080f); }

            public void Add(Item item)
            {
                if (queue.Count >= 6) queue.Dequeue();
                queue.Enqueue(item);
                if (showing) frame.Start();
            }

            public void Clear() { queue.Clear(); if (current != null) { Hide(); current = null; } frame.Stop(); }

            void Step()
            {
                var now = clock.Elapsed.TotalSeconds;
                if (current == null)
                {
                    if (!showing) { frame.Stop(); return; }
                    // stale ones (the player was away from the game) are dropped
                    while (queue.Count > 0 && DateTime.UtcNow - queue.Peek().At > TimeSpan.FromSeconds(90)) queue.Dequeue();
                    if (queue.Count == 0) { frame.Stop(); return; }
                    if (now < nextAt) return;
                    current = queue.Dequeue();
                    shownAt = now;
                    picture?.Dispose();
                    picture = Draw(current);
                    Show();
                }
                var age = now - shownAt;
                double dx, alpha;
                if (age < In) { var k = 1 - Math.Pow(1 - age / In, 3); dx = -40 * (1 - k); alpha = k; }
                else if (age < In + current.Hold) { dx = 0; alpha = 1; }
                else if (age < In + current.Hold + Out) { var k = (age - In - current.Hold) / Out; dx = -16 * k; alpha = 1 - k; }
                else { Hide(); current = null; nextAt = now + Gap; return; }
                if (right) dx = -dx;
                var margin = (int)(20 * scale);
                var x = right ? area.Right - margin - picture.Width : area.Left + margin;
                Present(picture, new Point(x + (int)(dx * scale), area.Top + margin), alpha);
            }

            void Clicked(Point p)
            {
                if (current == null) return;
                var item = current;
                if (acceptBox.Contains(p) && item.Accept != null) act(item.Accept);
                else if (laterBox.Contains(p) && item.Later != null) act(item.Later);
                else return;
                shownAt = clock.Elapsed.TotalSeconds - In - item.Hold;   // fades out now
            }

            Bitmap Draw(Item item)
            {
                var s = scale;
                var buttons = item.Accept != null;
                var w = (int)(320 * s); var h = (int)((buttons ? 88 : 48) * s);
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp))
                using (var name = new Font("Segoe UI Semibold", (float)Math.Round(15 * s), GraphicsUnit.Pixel))
                using (var body = new Font("Segoe UI", (float)Math.Round(15 * s), GraphicsUnit.Pixel))
                using (var small = new Font("Segoe UI", (float)Math.Round(12 * s), GraphicsUnit.Pixel))
                using (var btn = new Font("Segoe UI Semibold", (float)Math.Round(13 * s), GraphicsUnit.Pixel))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;   // crisp: the background under the text is opaque
                    var box = new RectangleF(0.5f, 0.5f, w - 1, h - 1);
                    using (var path = Rounded(box, 12 * s))
                    {
                        using (var fill = new SolidBrush(Color.FromArgb(255, 12, 14, 22))) g.FillPath(fill, path);
                    }
                    var cy = (buttons ? 24 : 22) * s;
                    var av = new RectangleF(12 * s, cy - 13 * s, 26 * s, 26 * s);
                    using (var b = new SolidBrush(item.Accent ? Color.FromArgb(77, Accent) : Color.FromArgb(18, 255, 255, 255))) g.FillEllipse(b, av);
                    var letter = string.IsNullOrEmpty(item.Name) ? "?" : item.Name.Substring(0, 1).ToUpperInvariant();
                    using (var b = new SolidBrush(Ink))
                    using (var f = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(letter, btn, b, av, f);
                    var dot = new RectangleF(av.Right - 8 * s, av.Bottom - 8 * s, 9 * s, 9 * s);
                    using (var b = new SolidBrush(item.Accent ? Accent : Good)) g.FillEllipse(b, dot);
                    using (var p = new Pen(Color.FromArgb(255, 12, 15, 26), 2 * s)) g.DrawEllipse(p, dot);
                    var x = 48 * s;
                    var nameSize = g.MeasureString(item.Name, name);
                    var textTop = cy - nameSize.Height / 2;
                    using (var b = new SolidBrush(Ink)) g.DrawString(item.Name, name, b, x, textTop);
                    using (var b = new SolidBrush(Muted))
                    using (var f = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                        g.DrawString(item.Text, body, b, new RectangleF(x + nameSize.Width - 2 * s, textTop, w - x - nameSize.Width - 8 * s, nameSize.Height), f);
                    if (buttons)
                    {
                        var by = 48 * s; var bh = 26 * s;
                        acceptBox = DrawButton(g, item.Accept.Str("label") ?? "Accept", btn, new PointF(x, by), bh, true, s);
                        laterBox = DrawButton(g, item.Later?.Str("label") ?? "Later", btn, new PointF(acceptBox.Right + 6 * s, by), bh, false, s);
                        using (var b = new SolidBrush(Faint))
                        using (var f = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
                            g.DrawString(item.Sub ?? "Shift + Tab", small, b, new RectangleF(laterBox.Right, by, w - 12 * s - laterBox.Right, bh), f);
                    }
                    else { acceptBox = laterBox = RectangleF.Empty; }
                }
                return bmp;
            }

            static RectangleF DrawButton(Graphics g, string text, Font font, PointF at, float height, bool primary, float s)
            {
                var size = g.MeasureString(text, font);
                var r = new RectangleF(at.X, at.Y, size.Width + 20 * s, height);
                using (var path = Rounded(r, 7 * s))
                {
                    using (var b = new SolidBrush(primary ? Color.FromArgb(41, 255, 255, 255) : Color.FromArgb(18, 255, 255, 255))) g.FillPath(b, path);
                    using (var p = new Pen(primary ? Color.FromArgb(66, 255, 255, 255) : Color.FromArgb(31, 255, 255, 255), 1)) g.DrawPath(p, path);
                }
                using (var b = new SolidBrush(Ink))
                using (var f = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(text, font, b, r, f);
                return r;
            }

            protected override void Dispose(bool disposing) { if (disposing) { frame.Dispose(); picture?.Dispose(); } base.Dispose(disposing); }
        }

        // ---- the corner pill, bottom left (options): the session's time, the FPS
        class CornerTimer : LayeredWindow
        {
            string shown; Rectangle shownArea;

            public void Update(Rectangle? area, string text)
            {
                if (area == null || string.IsNullOrEmpty(text)) { if (Visible) Hide(); shown = null; return; }
                if (text == shown && area.Value == shownArea && Visible) return;
                shown = text; shownArea = area.Value;
                var s = Math.Max(0.6f, area.Value.Height / 1080f);
                using (var font = new Font("Segoe UI Semibold", (float)Math.Round(14 * s), GraphicsUnit.Pixel))
                using (var measure = new Bitmap(1, 1))
                using (var mg = Graphics.FromImage(measure))
                {
                    var size = mg.MeasureString(text, font);
                    var w = (int)(size.Width + 44 * s); var h = (int)(34 * s);
                    using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;   // crisp: the background under the text is opaque
                            using (var path = Rounded(new RectangleF(0.5f, 0.5f, w - 1, h - 1), 10 * s))
                            using (var fill = new SolidBrush(Color.FromArgb(255, 10, 12, 20))) g.FillPath(fill, path);
                            using (var pen = new Pen(Color.FromArgb(0xFF, 0x8A, 0x91), 1.8f * s))
                            {
                                var c = new RectangleF(10 * s, h / 2f - 7 * s, 14 * s, 14 * s);
                                g.DrawEllipse(pen, c);
                                g.DrawLines(pen, new[] { new PointF(c.X + 7 * s, c.Y + 3.5f * s), new PointF(c.X + 7 * s, c.Y + 7 * s), new PointF(c.X + 9.5f * s, c.Y + 8.5f * s) });
                            }
                            using (var b = new SolidBrush(Ink)) g.DrawString(text, font, b, 30 * s, (h - size.Height) / 2);
                        }
                        if (!Visible) Show();
                        Present(bmp, new Point(area.Value.Left + (int)(24 * s), area.Value.Bottom - (int)(24 * s) - h), 1);
                    }
                }
            }
        }
    }
}
