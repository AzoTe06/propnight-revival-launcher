using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace PropnightRevival
{
    /// <summary>
    /// Logs attached to a bug report (Settings > Report a bug): the end of the launcher's log,
    /// the mod's log (UE4SS.log), the game's log (%LOCALAPPDATA%\Propnight\Saved) and the newest crash report of the last week
    /// with its minidump (the stack of a native crash: the only way to see where the game itself failed), zipped. The mod log of
    /// the session before is added (the launcher keeps it at PLAY). Tails shrink until the zip fits the server's limit (4 MB);
    /// a minidump that cannot fit is left out.
    /// </summary>
    static class BugReport
    {
        const int MaxZip = 4 * 1024 * 1024 - 64 * 1024;   // server: 4 MB of logs

        public static byte[] CollectLogs(string root)
        {
            foreach (var dump in new[] { true, false })
                for (var tail = 512 * 1024; tail >= 8 * 1024; tail /= 2)
                {
                    var zip = Build(root, tail, dump);
                    if (zip.Length <= MaxZip) return zip;
                }
            return null;
        }

        static byte[] Build(string root, int tail, bool dump)
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    Add(zip, "launcher.log", Path.Combine(Config.DataDir, "launcher.log"), tail);
                    if (GameInstall.IsGameRoot(root))
                    {
                        var win64 = GameInstall.Win64(root);
                        Add(zip, "UE4SS.log", Path.Combine(win64, "UE4SS.log"), tail);
                        Add(zip, "UE4SS-previous.log", Path.Combine(win64, "UE4SS-previous.log"), tail);   // the session before
                        // the game writes to %LOCALAPPDATA%\Propnight\Saved (older builds: next to the exe): the folder whose
                        // Propnight.log is the newest (bug 143 came without it); none found: where it was looked for
                        var places = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Propnight", "Saved"),
                                             Path.GetFullPath(Path.Combine(win64, @"..\..\Saved")) };
                        var saved = places.Where(p => File.Exists(Path.Combine(p, "Logs", "Propnight.log")))
                            .OrderByDescending(p => File.GetLastWriteTimeUtc(Path.Combine(p, "Logs", "Propnight.log"))).FirstOrDefault();
                        if (saved != null) Add(zip, "Propnight.log", Path.Combine(saved, "Logs", "Propnight.log"), tail);
                        else
                        {
                            saved = places.FirstOrDefault(Directory.Exists) ?? places[0];
                            using (var w = new StreamWriter(zip.CreateEntry("Propnight.log-missing.txt").Open()))
                                w.Write("The game's log (Propnight.log) was not found. Looked in:\r\n" +
                                        string.Join("\r\n", places.Select(p => Path.Combine(p, "Logs") + (Directory.Exists(Path.Combine(p, "Logs")) ? " (folder exists)" : " (no folder)"))) + "\r\n");
                        }
                        var crashes = new DirectoryInfo(Path.Combine(saved, "Crashes"));
                        var last = crashes.Exists ? crashes.GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).FirstOrDefault() : null;
                        if (last != null && DateTime.UtcNow - last.LastWriteTimeUtc < TimeSpan.FromDays(7))
                            foreach (var f in last.GetFiles())
                            {
                                var isDump = f.Name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase);
                                if (isDump && !dump)
                                {
                                    // too big to send: where it is, so the support team can ask the player for it
                                    using (var w = new StreamWriter(zip.CreateEntry("crash/minidump-not-sent.txt").Open()))
                                        w.Write($"The crash's minidump was too big to send with the report ({f.Length / 1024} KB).\r\n" +
                                                $"It is on the player's PC: {f.FullName}\r\n" +
                                                "(%LOCALAPPDATA%\\Propnight\\Saved\\Crashes\\<newest folder>\\" + f.Name + ")\r\n");
                                    continue;
                                }
                                Add(zip, "crash/" + f.Name, f.FullName, isDump ? int.MaxValue : tail);   // a minidump is kept whole
                            }
                    }
                }
                return ms.ToArray();
            }
        }

        /// <summary>Adds the last <paramref name="tail"/> bytes of a file (the game may still be writing it).</summary>
        static void Add(ZipArchive zip, string name, string path, int tail)
        {
            try
            {
                if (!File.Exists(path)) return;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length > tail) fs.Seek(-tail, SeekOrigin.End);
                    using (var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open()) fs.CopyTo(entry);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
