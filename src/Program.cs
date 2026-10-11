using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PropnightRevival
{
    static class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [STAThread]
        static int Main(string[] args)
        {
            // crisp rendering at any Windows scaling (125%, 150%…): per-monitor DPI aware v2
            try { SetProcessDpiAwarenessContext((IntPtr)(-4)); } catch { }
            // The WebView2 SDK assemblies are embedded in the exe (single file to download).
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                using (var stream = typeof(Program).Assembly.GetManifestResourceStream("PropnightRevival.deps." + name + ".dll"))
                {
                    if (stream == null) return null;
                    var bytes = new byte[stream.Length];
                    stream.Read(bytes, 0, bytes.Length);
                    return Assembly.Load(bytes);
                }
            };
            // --check: verify the signed manifest and list what PLAY would change, without changing anything
            // (result in %APPDATA%\PropnightRevival\check.txt).
            if (args.Length > 0 && args[0] == "--check") return Check();
            RunUi();
            return 0;
        }

        static int Check()
        {
            var report = new System.Text.StringBuilder();
            int code = 0;
            try
            {
                var settings = Settings.Load();
                var root = GameInstall.IsGameRoot(settings.GameRoot) ? settings.GameRoot : GameInstall.Detect();
                report.AppendLine("game: " + (root ?? "not found"));
                var updater = new Updater(settings, (t, f) => { });
                updater.LoadManifest().GetAwaiter().GetResult();
                report.AppendLine("manifest " + updater.Manifest.Str("version") + ": signature OK");
                report.AppendLine("launcher update: " + (updater.LauncherUpdate() ?? "none"));
                if (root != null)
                    foreach (var f in updater.Manifest.Objs("files"))
                    {
                        var path = Path.Combine(root, f.Str("path").Replace('/', '\\'));
                        var ok = File.Exists(path) && new FileInfo(path).Length == f.Long("size") && settings.CachedSha256(path) == f.Str("sha256");
                        report.AppendLine((ok ? "up to date  " : "to download ") + f.Str("path"));
                    }
                settings.Save();
            }
            catch (Exception e) { report.AppendLine("ERROR: " + e.Message); code = 1; }
            Directory.CreateDirectory(Config.DataDir);
            File.WriteAllText(Path.Combine(Config.DataDir, "check.txt"), report.ToString());
            return code;
        }

        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

        static void RunUi()
        {
            // a second launcher wakes the first one up (brought to the front) and exits: no dialog
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, "PropnightRevivalLauncher.Show"))
            using (var single = new Mutex(true, "PropnightRevivalLauncher", out var first))
            {
                if (!first) { AllowSetForegroundWindow(-1); show.Set(); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => { Log.Write("crash: " + e.Exception); MessageBox.Show(e.Exception.Message, "Propnight Revival"); };
                PrepareWebView2Loader();
                var form = new ShellForm();
                var waiter = ThreadPool.RegisterWaitForSingleObject(show, (state, timedOut) =>
                {
                    try { if (form.IsHandleCreated) form.BeginInvoke((Action)form.ShowFromOtherInstance); } catch (InvalidOperationException) { }
                }, null, -1, false);
                Application.Run(form);
                waiter.Unregister(null);
            }
        }

        /// <summary>WebView2Loader.dll (native) is embedded too: written once next to the launcher data.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void PrepareWebView2Loader()
        {
            using (var stream = typeof(Program).Assembly.GetManifestResourceStream("PropnightRevival.deps.WebView2Loader.dll"))
            {
                var bytes = new byte[stream.Length];
                stream.Read(bytes, 0, bytes.Length);
                var dir = Path.Combine(Config.DataDir, "webview2-loader", Hash.Sha256(bytes).Substring(0, 12));
                var path = Path.Combine(dir, "WebView2Loader.dll");
                if (!File.Exists(path)) { Directory.CreateDirectory(dir); File.WriteAllBytes(path, bytes); }
                Microsoft.Web.WebView2.Core.CoreWebView2Environment.SetLoaderDllFolderPath(dir);
            }
        }
    }
}
