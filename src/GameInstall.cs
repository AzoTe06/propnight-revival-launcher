using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        public static bool IsRunning() => Process.GetProcessesByName("Propnight").Any();

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
