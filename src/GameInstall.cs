using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PropnightRevival
{
    /// <summary>The Propnight install: ...\steamapps\common\Propnight (contains Propnight\Binaries\Win64\Propnight.exe).</summary>
    static class GameInstall
    {
        public const string ExeRelative = @"Propnight\Binaries\Win64\Propnight.exe";

        public static bool IsGameRoot(string dir) => !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, ExeRelative));

        /// <summary>Accepts the game root, steamapps\common, or any folder inside the game.</summary>
        public static string Normalize(string picked)
        {
            if (string.IsNullOrEmpty(picked)) return null;
            if (File.Exists(picked)) picked = Path.GetDirectoryName(picked);
            if (IsGameRoot(Path.Combine(picked, "Propnight"))) return Path.Combine(picked, "Propnight");
            for (var dir = new DirectoryInfo(picked); dir != null; dir = dir.Parent)
                if (IsGameRoot(dir.FullName)) return dir.FullName;
            return null;
        }

        /// <summary>Looks for Propnight in every Steam library.</summary>
        public static string Detect()
        {
            foreach (var lib in SteamLibraries())
            {
                var root = Path.Combine(lib, "steamapps", "common", "Propnight");
                if (IsGameRoot(root)) return root;
            }
            return null;
        }

        static IEnumerable<string> SteamLibraries()
        {
            var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                     ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
            if (string.IsNullOrEmpty(steam)) yield break;
            steam = steam.Replace('/', '\\');
            yield return steam;
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) yield break;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                yield return m.Groups[1].Value.Replace(@"\\", @"\");
        }

        public static string Exe(string root) => Path.Combine(root, ExeRelative);
        public static string Win64(string root) => Path.GetDirectoryName(Exe(root));
        public static string SteamSettings(string root) => Path.Combine(root, @"Engine\Binaries\ThirdParty\Steamworks\Steamv147\Win64\steam_settings");

        public static bool IsRunning() => RunningCount() > 0;

        /// <summary>
        /// How many Propnight processes run (PushPresence: one read for "in game" and "the one PLAY started"). Only the game
        /// counts (its exe, or Steam's bootstrap next to it): another program named Propnight.exe made the launcher refuse
        /// PLAY, updates and sign-in on a PC where the game never ran (bugs 162, 173). One whose path cannot be read counts.
        /// </summary>
        public static int RunningCount()
        {
            var images = ProcessImages();
            List<int> ids;
            if (images != null) ids = images.Where(p => string.Equals(NameOf(p.Value), "Propnight", StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList();
            else
            {
                ids = new List<int>();
                foreach (var p in Process.GetProcessesByName("Propnight")) { ids.Add(p.Id); p.Dispose(); }
            }
            var count = 0;
            foreach (var id in ids)
            {
                var path = ImagePath(id);
                if (path == null || path.EndsWith(@"\" + ExeRelative, StringComparison.OrdinalIgnoreCase)
                    || File.Exists(Path.Combine(Path.GetDirectoryName(path), ExeRelative)))
                    count++;
                else
                    lock (notGame) if (notGame.Add(path)) Log.Write("not the game, ignored: Propnight process " + path);
            }
            return count;
        }

        static readonly HashSet<string> notGame = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The running processes: id and image path (Windows' own form, \Device\HarddiskVolumeN\...\x.exe), read without
        /// opening them nor building a Process for each. Performance audit 2026-11-05: Process.GetProcessesByName reads every
        /// thread of every process (~8 ms for 500 processes) and ran twice a second on the window's thread (presence, Discord):
        /// most of the launcher's idle CPU (~1.9 %). This: ~0.7 ms. Null when Windows refuses (then the old way).
        /// </summary>
        public static List<KeyValuePair<int, string>> ProcessImages()
        {
            try
            {
                var ids = new int[8192];
                if (!EnumProcesses(ids, ids.Length * 4, out var needed)) return null;
                var result = new List<KeyValuePair<int, string>>(needed / 4);
                var buffer = Marshal.AllocHGlobal(2048);
                try
                {
                    for (var i = 0; i < Math.Min(ids.Length, needed / 4); i++)
                    {
                        if (ids[i] == 0) continue;
                        var info = new ProcessIdInfo { Pid = (IntPtr)ids[i], MaxLength = 2048, Buffer = buffer };
                        if (NtQuerySystemInformation(88, ref info, Marshal.SizeOf(typeof(ProcessIdInfo)), out _) == 0)   // SystemProcessIdInformation
                            result.Add(new KeyValuePair<int, string>(ids[i], Marshal.PtrToStringUni(buffer, info.Length / 2)));
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
                return result.Count > 0 ? result : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>A process's name as Process.ProcessName gives it: the exe's file name without ".exe".</summary>
        public static string NameOf(string image)
        {
            var name = image.Substring(image.LastIndexOf('\\') + 1);
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
        }

        [DllImport("psapi.dll")] static extern bool EnumProcesses([Out] int[] ids, int size, out int needed);
        [StructLayout(LayoutKind.Sequential)] struct ProcessIdInfo { public IntPtr Pid; public ushort Length, MaxLength; public IntPtr Buffer; }
        [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, ref ProcessIdInfo info, int size, out int returned);

        /// <summary>Closes the game (its processes, as RunningCount counts them): a cheat tool opened during a game.</summary>
        public static int CloseGame()
        {
            var closed = 0;
            foreach (var p in Process.GetProcessesByName("Propnight"))
            {
                try
                {
                    var path = ImagePath(p.Id);
                    if (path == null || path.EndsWith(@"\" + ExeRelative, StringComparison.OrdinalIgnoreCase)
                        || File.Exists(Path.Combine(Path.GetDirectoryName(path), ExeRelative)))
                    {
                        p.Kill();
                        closed++;
                    }
                }
                catch (Exception e) { Log.Write("close game: " + e.Message); }
                finally { p.Dispose(); }
            }
            return closed;
        }

        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        /// <summary>A process's exe path (limited query: also works for a game run as administrator), null when unreadable.</summary>
        static string ImagePath(int pid)
        {
            var handle = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (handle == IntPtr.Zero) return null;
            try
            {
                var name = new StringBuilder(1024);
                var size = name.Capacity;
                return QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString(0, size) : null;
            }
            finally { CloseHandle(handle); }
        }

        /// <summary>
        /// Where the running game is, written by the mod when it changes (Win64\revival-presence.txt:
        /// role=host|client|menu, server=ip:port for a client). Friends behind one router share a public
        /// address, so the server needs this to tell which of them is in a game.
        /// </summary>
        public static string PresencePath(string root) => Path.Combine(Win64(root), "revival-presence.txt");
        public static Dictionary<string, string> Presence(string root)
        {
            var result = new Dictionary<string, string>();
            try
            {
                foreach (var line in File.ReadAllLines(PresencePath(root)))
                {
                    var i = line.IndexOf('=');
                    if (i > 0) result[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return result;
        }

        /// <summary>Identity given to the Steam emulator (Goldberg): in-game name and Steam ID come from the account.</summary>
        public static void SetIdentity(string root, string name, string steamId)
        {
            var dir = SteamSettings(root);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "force_account_name.txt"), name);
            File.WriteAllText(Path.Combine(dir, "force_steamid.txt"), steamId);
        }

        /// <summary>The admin menu mod of the test channels (release.py --beta / --diag only).</summary>
        public const string AdminMod = "PropnightAdmin";

        /// <summary>Deletes a mod's folder and its line in mods.txt (nothing when it is not there).</summary>
        public static void RemoveMod(string root, string mod)
        {
            var dir = Path.Combine(Win64(root), "Mods", mod);
            if (Directory.Exists(dir)) { Directory.Delete(dir, true); Log.Write("removed mod " + mod); }
            var path = Path.Combine(Win64(root), "Mods", "mods.txt");
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path).ToList();
            if (lines.RemoveAll(l => l.Split(':')[0].Trim().Equals(mod, StringComparison.OrdinalIgnoreCase)) > 0) File.WriteAllLines(path, lines);
        }

        /// <summary>
        /// Public release (2026-10-27, bugs 137 / 149: two players crashed and froze with an old community pack of Lua mods
        /// loaded next to ours, hooking the game's functions): only the mods of the manifest's "modsAllowed" load. UE4SS
        /// starts a mod listed "NAME : 1" in mods.txt or whose folder holds enabled.txt: any other is set to 0 and its
        /// enabled.txt renamed (kept, so it can be put back by hand). Nothing when the manifest has no list (older ones).
        /// </summary>
        public static void OnlyMods(string root, IList<string> allowed)
        {
            if (allowed == null || allowed.Count == 0) return;
            bool ok(string name) => allowed.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            var mods = Path.Combine(Win64(root), "Mods");
            if (!Directory.Exists(mods)) return;
            var off = new List<string>();
            try
            {
                foreach (var dir in Directory.GetDirectories(mods))
                {
                    var name = Path.GetFileName(dir);
                    // UE4SS also starts a C++ mod (dlls\main.dll) in an enabled mod's folder: ours have none
                    var dlls = Path.Combine(dir, "dlls");
                    if (ok(name) && Directory.Exists(dlls))
                    {
                        var keptDlls = dlls + ".off-by-revival";
                        if (Directory.Exists(keptDlls)) Directory.Delete(keptDlls, true);
                        Directory.Move(dlls, keptDlls);
                        off.Add(name + @"\dlls");
                    }
                    var enabled = Path.Combine(dir, "enabled.txt");
                    if (ok(name) || !File.Exists(enabled)) continue;
                    var kept = Path.Combine(dir, "enabled.txt.off-by-revival");
                    if (File.Exists(kept)) File.Delete(kept);
                    File.Move(enabled, kept);
                    off.Add(name);
                }
                var path = Path.Combine(mods, "mods.txt");
                if (File.Exists(path))
                {
                    var lines = File.ReadAllLines(path).ToList();
                    var changed = false;
                    for (var i = 0; i < lines.Count; i++)
                    {
                        var parts = lines[i].Split(':');
                        var name = parts[0].Trim();
                        if (parts.Length != 2 || name.StartsWith(";") || name.Length == 0 || ok(name) || parts[1].Trim() != "1") continue;
                        lines[i] = $"{name} : 0";
                        changed = true;
                        if (!off.Contains(name)) off.Add(name);
                    }
                    if (changed) File.WriteAllLines(path, lines);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Log.Write("mods: " + e.Message); }
            if (off.Count > 0) Log.Write("mods turned off (not the revival's): " + string.Join(", ", off));
        }

        /// <summary>
        /// Public release (2026-10-28): what loads with the game, past the Lua mods. A DLL next to Propnight.exe is loaded
        /// by Windows before the game (version.dll, winmm.dll…: another UE4SS or a trainer), a pak anywhere under
        /// Content\Paks by the game (the exe patch turns pak signing off: a changed Blueprint would load). Only the
        /// manifest's files (checked by Plan), the game's own DLLs ("gameDlls": name -> SHA-256 of each Steam build) and
        /// paks ("paksAllowed") stay; any other is renamed *.off-by-revival (kept, put back by hand). A game DLL whose hash
        /// is none of them cannot be renamed (the game needs it): returned, PLAY stops. Nothing with an older manifest.
        /// </summary>
        public static List<string> OnlyGameFiles(string root, Dictionary<string, object> manifest)
        {
            var changed = new List<string>();
            var gameDlls = manifest.Obj("gameDlls");
            if (gameDlls == null) return changed;
            var paks = new HashSet<string>(manifest.Strs("paksAllowed"), StringComparer.OrdinalIgnoreCase);
            var ours = new HashSet<string>(manifest.Objs("files").Select(f => Path.GetFullPath(Path.Combine(root, f.Str("path").Replace('/', '\\')))),
                                           StringComparer.OrdinalIgnoreCase);
            var off = new List<string>();
            var top = Path.GetFullPath(root).TrimEnd('\\');
            // a file that cannot be renamed or read (in use, no rights) would still load: PLAY stops on it too
            void check(string path, Func<bool> keep)
            {
                try
                {
                    if (keep()) return;
                    var kept = path + ".off-by-revival";
                    if (File.Exists(kept)) File.Delete(kept);
                    File.Move(path, kept);
                    off.Add(path.Substring(top.Length).TrimStart('\\'));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    Log.Write("game files: " + e.Message);
                    changed.Add(Path.GetFileName(path));
                }
            }
            foreach (var dll in Directory.GetFiles(Win64(root), "*.dll"))
                check(dll, () =>
                {
                    if (ours.Contains(Path.GetFullPath(dll))) return true;
                    var known = gameDlls.Keys.FirstOrDefault(k => k.Equals(Path.GetFileName(dll), StringComparison.OrdinalIgnoreCase));
                    if (known == null) return false;
                    var sha = Hash.Sha256File(dll);
                    if (!(gameDlls[known] is object[] shas && shas.Any(s => sha.Equals(Convert.ToString(s), StringComparison.OrdinalIgnoreCase))))
                        changed.Add(Path.GetFileName(dll));
                    return true;
                });
            var paksDir = Path.Combine(root, "Propnight", "Content", "Paks");
            if (Directory.Exists(paksDir))
                foreach (var file in Directory.GetFiles(paksDir, "*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext != ".pak" && ext != ".ucas" && ext != ".utoc") continue;
                    check(file, () => ours.Contains(Path.GetFullPath(file)) ||
                                      Path.GetDirectoryName(file).Equals(paksDir, StringComparison.OrdinalIgnoreCase) && paks.Contains(Path.GetFileName(file)));
                }
            if (off.Count > 0) Log.Write("files turned off (not the game's nor the revival's): " + string.Join(", ", off));
            if (changed.Count > 0) Log.Write("game DLLs changed: " + string.Join(", ", changed));
            return changed;
        }

        /// <summary>
        /// Anti-cheat (2026-10-30): the manifest's files, opened for reading and shared for reading only, for as long as the
        /// game runs: nothing can write, replace or delete them between the check and the game's start (UE4SS reads the
        /// scripts once, at start) nor during the game. Checked again once held: a small file must still be the manifest's,
        /// a large one (the maps) still the size and date the launcher last read. Returns the handles (to dispose when the
        /// game ends) and fills `changed` (manifest paths); a file held by another program counts as changed (an editor
        /// keeping it open for writing could change it after the check). Optional content turned off is skipped.
        /// </summary>
        public static List<FileStream> LockFiles(string root, Dictionary<string, object> manifest, Settings settings, List<string> changed)
        {
            var held = new List<FileStream>();
            foreach (var f in manifest.Objs("files"))
            {
                var packs = Updater.PacksOf(f);
                if (packs.Count > 0 && !packs.Any(settings.DlcOn)) continue;
                // not the relay agent (RevivalRelay.exe, started by the mod when hosting): the game does not load it, and
                // started while held, cmd's window stayed open seconds longer (2026-10-30); PLAY still checks its hash
                if (f.Str("path").EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var path = Path.GetFullPath(Path.Combine(root, f.Str("path").Replace('/', '\\')));
                if (!File.Exists(path)) { changed.Add(f.Str("path")); continue; }
                try
                {
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    held.Add(stream);
                    var size = f.Long("size");
                    if (stream.Length != size) { changed.Add(f.Str("path")); continue; }
                    if (size < 64L << 20)
                    {
                        stream.Position = 0;
                        using (var sha = System.Security.Cryptography.SHA256.Create())
                            if (!Hash.Hex(sha.ComputeHash(stream)).Equals(f.Str("sha256"), StringComparison.OrdinalIgnoreCase)) changed.Add(f.Str("path"));
                    }
                    else
                    {
                        var info = new FileInfo(path);
                        settings.KnownSha256(path, out var stamp);
                        if ($"{info.Length}:{info.LastWriteTimeUtc.Ticks}" != stamp) changed.Add(f.Str("path"));
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    Log.Write("lock " + f.Str("path") + ": " + e.Message);
                    changed.Add(f.Str("path"));
                }
            }
            return held;
        }

        /// <summary>
        /// Anti-cheat (2026-10-30): the mods UE4SS would start that are not the manifest's (a folder with enabled.txt, a
        /// "NAME : 1" line in mods.txt, a C++ mod's dlls folder in ours). Checked once mods.txt is held (LockModList) and
        /// again during the game: a Lua mod added between OnlyMods and the game's start loads (an outline of every player
        /// through walls is a few lines of Lua).
        /// </summary>
        public static List<string> ForeignMods(string root, IList<string> allowed)
        {
            var found = new List<string>();
            if (allowed == null || allowed.Count == 0) return found;
            bool ok(string name) => allowed.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            var mods = Path.Combine(Win64(root), "Mods");
            if (!Directory.Exists(mods)) return found;
            try
            {
                foreach (var dir in Directory.GetDirectories(mods))
                {
                    var name = Path.GetFileName(dir);
                    if (ok(name) ? Directory.Exists(Path.Combine(dir, "dlls")) : File.Exists(Path.Combine(dir, "enabled.txt"))) found.Add(name);
                }
                var path = Path.Combine(mods, "mods.txt");
                if (File.Exists(path))
                    using (var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                        for (string line; (line = reader.ReadLine()) != null;)
                        {
                            var parts = line.Split(':');
                            var name = parts[0].Trim();
                            if (parts.Length == 2 && name.Length > 0 && !name.StartsWith(";") && !ok(name) && parts[1].Trim() == "1" && !found.Contains(name)) found.Add(name);
                        }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Log.Write("mods check: " + e.Message); }
            return found;
        }

        /// <summary>mods.txt held read only until the game ends (with LockFiles), null when there is none.</summary>
        public static FileStream LockModList(string root)
        {
            var path = Path.Combine(Win64(root), "Mods", "mods.txt");
            return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        }

        /// <summary>Makes sure UE4SS loads the revival mods.</summary>
        public static void EnableMods(string root, IEnumerable<string> mods)
        {
            var path = Path.Combine(Win64(root), "Mods", "mods.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            foreach (var mod in mods)
            {
                var i = lines.FindIndex(l => l.Split(':')[0].Trim().Equals(mod, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) lines[i] = $"{mod} : 1"; else lines.Add($"{mod} : 1");
            }
            File.WriteAllLines(path, lines);
        }
    }
}
