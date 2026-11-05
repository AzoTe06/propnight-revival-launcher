using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PropnightRevival
{
    /// <summary>
    /// Anti-cheat (2026-10-30): a memory editor open on this PC (Cheat Engine). PLAY refuses to start the game while one is
    /// open; during a game it is reported to the moderators and, since 2026-11-01, the game is closed (a tester opened it
    /// after PLAY and changed his coins and the anti-cheat in memory: reporting alone let the match go on; the others
    /// keep playing when he is a client, the match ends when he hosts). Its process name, or its main window's title ("Cheat Engine 7.5": a browser tab
    /// about it ends with the browser's name, so only the exact title counts). A renamed copy passes: this stops the
    /// casual cheater, the witnesses see the rest. One process list and one pass over the top windows: a few ms.
    /// </summary>
    static class CheatTools
    {
        // the names looked for, written plainly (2026-11-01: hidden reversed while the launcher was obfuscated; both were
        // withdrawn, Microsoft Defender's machine-learning guess flagged the launcher as Wacatac)
        const string Tool = "Cheat Engine", Lower = "cheatengine", Spaced = "cheat engine";
        static readonly Regex Title = new Regex("^" + Tool + @" \d+(\.\d+)*$");

        public static List<string> Open()
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> check = n => { var name = n.ToLowerInvariant(); if (name.StartsWith(Lower) || name.StartsWith(Spaced)) found.Add(Tool); };
            // the process names without a Process each (GameInstall.ProcessImages: ~0.7 ms instead of ~8)
            var images = GameInstall.ProcessImages();
            if (images != null) foreach (var p in images) check(GameInstall.NameOf(p.Value));
            else
                foreach (var p in Process.GetProcesses())
                {
                    try { check(p.ProcessName); }
                    catch (InvalidOperationException) { }
                    finally { p.Dispose(); }
                }
            EnumWindows((window, _) =>
            {
                if (!IsWindowVisible(window)) return true;
                var length = GetWindowTextLength(window);
                if (length < 14 || length > 40) return true;
                var text = new StringBuilder(length + 1);
                GetWindowText(window, text, text.Capacity);
                var title = text.ToString().Trim();
                if (Title.IsMatch(title)) { found.Remove(Tool); found.Add(title); }
                return true;
            }, IntPtr.Zero);
            return found.ToList();
        }

        delegate bool EnumWindowsProc(IntPtr window, IntPtr param);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
    }
}
