using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace PropnightRevival
{
    /// <summary>
    /// Logs attached to a bug report (Settings > Report a bug): the end of the launcher's log,
    /// the mod's log (UE4SS.log), the game's log (%LOCALAPPDATA%\Propnight\Saved) and the newest crash report of the last week
    /// (without its minidump), zipped. Tails shrink until the zip fits the server's limit.
    /// </summary>
    static class BugReport
    {
        const int MaxZip = 150 * 1024;

        public static byte[] CollectLogs(string root)
        {
            for (var tail = 512 * 1024; tail >= 8 * 1024; tail /= 2)
            {
                var zip = Build(root, tail);
                if (zip.Length <= MaxZip) return zip;
            }
            return null;
        }

        static byte[] Build(string root, int tail)
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
                        // the game writes to %LOCALAPPDATA%\Propnight\Saved (older builds: next to the exe)
                        var saved = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Propnight", "Saved");
                        if (!Directory.Exists(saved)) saved = Path.GetFullPath(Path.Combine(win64, @"..\..\Saved"));
                        Add(zip, "Propnight.log", Path.Combine(saved, "Logs", "Propnight.log"), tail);
                        var crashes = new DirectoryInfo(Path.Combine(saved, "Crashes"));
                        var last = crashes.Exists ? crashes.GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).FirstOrDefault() : null;
                        if (last != null && DateTime.UtcNow - last.LastWriteTimeUtc < TimeSpan.FromDays(7))
                            foreach (var f in last.GetFiles().Where(f => !f.Name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)))
                                Add(zip, "crash/" + f.Name, f.FullName, tail);
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
