using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace PropnightRevival
{
    /// <summary>
    /// Logs attached to a bug report (Settings > Report a bug): the end of the launcher's log,
    /// the mod's log (UE4SS.log), the game's log (%LOCALAPPDATA%\Propnight\Saved), the newest crash report of the last 2 days
    /// with its minidump (the stack of a native crash: the only way to see where the game itself failed) and the game closings
    /// Windows recorded, zipped. The mod log of the session before is added (the launcher keeps it at PLAY). Tails shrink
    /// until the zip fits the server's limit (4 MB); a minidump that cannot fit is left out.
    /// </summary>
    static class BugReport
    {
        const int MaxZip = 4 * 1024 * 1024 - 64 * 1024;   // server: 4 MB of logs
        static readonly TimeSpan Recent = TimeSpan.FromDays(2);

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
                        // (2 days, its date written: an older one was taken for the crash being reported, bugs 265 / 266)
                        if (last != null && DateTime.UtcNow - last.LastWriteTimeUtc < Recent)
                        {
                            using (var w = new StreamWriter(zip.CreateEntry("crash/when.txt").Open()))
                                w.Write($"Crash report written {last.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC ({last.Name})\r\n");
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
                        OtherCrashes(zip, win64);
                    }
                }
                return ms.ToArray();
            }
        }

        /// <summary>
        /// A game closed without the game's own crash report (heap corruption, a native crash under UE4SS: bugs 265 / 266):
        /// Windows' record of it (the Application log's "Application Error" events for Propnight.exe: exception code, module,
        /// offset, read from the event's fields so any Windows language works) and where the dumps are (UE4SS's
        /// Win64\crash_*.dmp, Windows' %LOCALAPPDATA%\CrashDumps\Propnight.exe.*.dmp: tens of MB, too big to send; the
        /// support team asks the player for one). Last 2 days only.
        /// </summary>
        static void OtherCrashes(ZipArchive zip, string win64)
        {
            var lines = new List<string>();
            try
            {
                var query = new EventLogQuery("Application", PathType.LogName,
                    "*[System[Provider[@Name='Application Error'] and (EventID=1000) and TimeCreated[timediff(@SystemTime) <= " +
                    (long)Recent.TotalMilliseconds + "]]]") { ReverseDirection = true };
                using (var reader = new EventLogReader(query))
                    for (var e = reader.ReadEvent(); e != null && lines.Count < 20; e = reader.ReadEvent())
                        using (e)
                        {
                            var p = e.Properties.Select(x => Convert.ToString(x.Value)).ToArray();
                            if (p.Length < 8 || !p[0].Equals("Propnight.exe", StringComparison.OrdinalIgnoreCase)) continue;
                            var code = p[6];
                            if (uint.TryParse(code, out var c)) code = "0x" + c.ToString("x8");
                            lines.Add($"{e.TimeCreated?.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC  Propnight.exe closed: exception {code} in {p[3]} " +
                                      $"{p[4]} at offset {p[7]}" + (p.Length > 10 ? $" ({p[10]})" : ""));
                        }
            }
            catch (Exception ex) { lines.Add("Windows event log not read: " + ex.Message); }
            var dumps = new List<FileInfo>();
            var crashDumps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps");
            foreach (var (dir, pattern) in new[] { (win64, "crash_*.dmp"), (crashDumps, "Propnight.exe.*.dmp") })
                try { if (Directory.Exists(dir)) dumps.AddRange(new DirectoryInfo(dir).GetFiles(pattern)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            dumps = dumps.Where(f => DateTime.UtcNow - f.LastWriteTimeUtc < Recent).OrderByDescending(f => f.LastWriteTimeUtc).Take(10).ToList();
            if (lines.Count == 0 && dumps.Count == 0) return;
            using (var w = new StreamWriter(zip.CreateEntry("crash/windows-crashes.txt").Open()))
            {
                w.Write("Game closings Windows recorded (last 2 days, newest first):\r\n");
                w.Write(lines.Count > 0 ? string.Join("\r\n", lines) + "\r\n" : "(none)\r\n");
                w.Write("\r\nCrash dumps on the player's PC (not sent: too big; ask the player for the one at the crash's time):\r\n");
                w.Write(dumps.Count > 0
                    ? string.Join("\r\n", dumps.Select(f => $"{f.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC  {f.Length / (1024 * 1024)} MB  {f.FullName}")) + "\r\n"
                    : "(none)\r\n");
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
