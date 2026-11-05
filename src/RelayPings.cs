using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace PropnightRevival
{
    /// <summary>
    /// Round trip to each region's relay (server: relaypool.mjs). The server puts a hosted game on the relay its host
    /// reaches fastest, shows each player its ping to every public game and quick play joins the lowest one under
    /// 200 ms. A UDP probe "Q" + 8-byte nonce to the relay's agent port comes back as sent (the path the game's
    /// packets take); the median of 3, or an ICMP ping when the relay does not answer it.
    /// </summary>
    static class RelayPings
    {
        public class Relay { public string Id, Name, Host; public int Port; }

        /// <summary>Region id -> ms, for the relays that answered.</summary>
        public static async Task<Dictionary<string, int>> Measure(IEnumerable<Relay> relays)
        {
            var list = relays.ToList();
            var results = await Task.WhenAll(list.Select(r => Task.Run(() => One(r))));
            var pings = new Dictionary<string, int>();
            for (int i = 0; i < list.Count; i++)
                if (results[i] != null) pings[list[i].Id] = results[i].Value;
            return pings;
        }

        static int? One(Relay r)
        {
            try
            {
                var address = IPAddress.TryParse(r.Host, out var ip) ? ip
                    : Dns.GetHostAddresses(r.Host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (address == null) return null;
                return Udp(address, r.Port) ?? Icmp(address);
            }
            catch (Exception e) { Log.Write($"ping {r.Id}: {e.Message}"); return null; }
        }

        static int? Udp(IPAddress address, int port)
        {
            var times = new List<double>();
            var random = new Random();
            var reply = new byte[64];
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveTimeout = 1000 })
            {
                socket.Connect(address, port);
                for (int i = 0; i < 3; i++)
                {
                    var probe = new byte[9];
                    random.NextBytes(probe);
                    probe[0] = (byte)'Q';
                    var clock = Stopwatch.StartNew();
                    try
                    {
                        socket.Send(probe);
                        // an older relay ignores the probe: the receive times out (or the port is closed: refused)
                        while (clock.ElapsedMilliseconds < 1000)
                        {
                            int n = socket.Receive(reply);
                            if (n == probe.Length && reply.Take(n).SequenceEqual(probe)) { times.Add(clock.Elapsed.TotalMilliseconds); break; }
                        }
                    }
                    catch (SocketException) { if (times.Count == 0 && i == 0) return null; }
                }
            }
            return Median(times);
        }

        static int? Icmp(IPAddress address)
        {
            var times = new List<double>();
            using (var ping = new Ping())
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        var r = ping.Send(address, 1000);
                        if (r.Status == IPStatus.Success) times.Add(r.RoundtripTime);
                    }
                    catch (PingException) { }
                }
            return Median(times);
        }

        static int? Median(List<double> times)
        {
            if (times.Count == 0) return null;
            times.Sort();
            return Math.Max(1, (int)Math.Round(times[times.Count / 2]));
        }
    }
}
