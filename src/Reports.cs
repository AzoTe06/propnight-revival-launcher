using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PropnightRevival
{
    /// <summary>
    /// Sends what the PropnightLocalMap mod records in Win64 as JSON lines to the revival
    /// server, with the player's account: in-game reports (Tab > Report,
    /// revival-reports.jsonl) and match records (revival-matches.jsonl).
    /// Lines that could not be sent stay in the file and are retried.
    /// </summary>
    class Reports : IDisposable
    {
        readonly string file;
        readonly Func<Dictionary<string, object>, Task> post;
        readonly Func<Dictionary<string, object>, string> describe;
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        Timer timer;

        Reports(string file, Func<Dictionary<string, object>, Task> post, Func<Dictionary<string, object>, string> describe)
        {
            this.file = file;
            this.post = post;
            this.describe = describe;
        }

        public static Reports PlayerReports(Api api, string gameRoot) =>
            new Reports(Path.Combine(GameInstall.Win64(gameRoot), "revival-reports.jsonl"), api.PostReport,
                r => $"report sent: {r.Str("reported")} ({r.Str("reason")})");

        public static Reports Matches(Api api, string gameRoot) =>
            new Reports(Path.Combine(GameInstall.Win64(gameRoot), "revival-matches.jsonl"), api.PostMatch,
                m => $"match sent: {m.Str("map")}, MMR {m.Str("mmrBefore")} -> {m.Str("mmrAfter")}");

        public void Start() => timer = new Timer(_ => { var _ignored = Flush(); }, null, 5000, 5000);

        public async Task<int> Flush()
        {
            if (!await gate.WaitAsync(0)) return 0;
            int sent = 0;
            try
            {
                if (!File.Exists(file)) return 0;
                string[] lines;
                try { lines = File.ReadAllLines(file, Encoding.UTF8); }
                catch (IOException) { return 0; }   // the mod is writing
                var left = new List<string>();
                foreach (var line in lines.Where(l => l.Trim().Length > 0))
                {
                    try
                    {
                        var record = Json.Parse(line);
                        await post(record);
                        sent++;
                        Log.Write(describe(record));
                    }
                    catch (ApiException e) when (e.Status == 400) { Log.Write("record dropped: " + e.Message); }
                    catch (Exception e) { left.Add(line); Log.Write("record kept for later: " + e.Message); }
                }
                // keep what failed plus anything the mod appended meanwhile
                var now = File.ReadAllLines(file, Encoding.UTF8);
                left.AddRange(now.Skip(lines.Length));
                if (left.Count == 0) File.Delete(file); else File.WriteAllLines(file, left, Encoding.UTF8);
            }
            catch (Exception e) { Log.Write("reports: " + e.Message); }
            finally { gate.Release(); }
            return sent;
        }

        public void Dispose() => timer?.Dispose();
    }
}
