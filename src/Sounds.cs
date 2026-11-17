using System;
using System.Collections.Generic;
using System.IO;

namespace PropnightRevival
{
    /// <summary>
    /// The launcher's sounds (ui/sounds/*.wav, 16-bit PCM, in the exe), at the volume of Settings › Launcher › Sounds:
    /// SoundPlayer has no volume, so the samples are scaled once per volume and the scaled file kept for the next plays.
    /// </summary>
    static class Sounds
    {
        // 100 % plays at 2.5 × the files' level (80 %, the default: 2 ×, +6 dB over 1.65.33); the files peak at 0.34
        // (message) and 0.20 (vote), so 100 % stays under full scale, and samples are clamped anyway
        const double MaxGain = 2.5;
        static readonly Dictionary<string, byte[]> source = new Dictionary<string, byte[]>();
        static readonly Dictionary<string, KeyValuePair<int, System.Media.SoundPlayer>> players = new Dictionary<string, KeyValuePair<int, System.Media.SoundPlayer>>();

        /// <summary>Plays "message" or "vote" at the volume (0-100).</summary>
        public static void Play(string name, int volume)
        {
            volume = Math.Max(0, Math.Min(100, volume));
            if (volume == 0) return;
            if (!players.TryGetValue(name, out var p) || p.Key != volume)
            {
                // the previous player's sound stopped first: its buffer may still be read by Windows (SND_ASYNC)
                p.Value?.Stop();
                p.Value?.Dispose();
                var player = new System.Media.SoundPlayer(new MemoryStream(Scaled(Source(name), volume / 100.0 * MaxGain)));
                player.Load();
                players[name] = p = new KeyValuePair<int, System.Media.SoundPlayer>(volume, player);
            }
            p.Value.Play();
        }

        static byte[] Source(string name)
        {
            if (source.TryGetValue(name, out var b)) return b;
            using (var s = typeof(Sounds).Assembly.GetManifestResourceStream("PropnightRevival.ui.sounds." + name + ".wav"))
            using (var m = new MemoryStream())
            {
                s.CopyTo(m);
                return source[name] = m.ToArray();
            }
        }

        /// <summary>A copy of the RIFF file with its "data" chunk's 16-bit samples multiplied by the gain.</summary>
        static byte[] Scaled(byte[] wav, double gain)
        {
            var o = (byte[])wav.Clone();
            if (o.Length < 12 || BitConverter.ToUInt32(o, 0) != 0x46464952 || BitConverter.ToUInt32(o, 8) != 0x45564157) return o;   // RIFF, WAVE
            int bits = 0;
            for (int at = 12; at + 8 <= o.Length;)
            {
                var id = BitConverter.ToUInt32(o, at);
                var size = (int)BitConverter.ToUInt32(o, at + 4);
                var body = at + 8;
                if (id == 0x20746d66 && size >= 16) bits = BitConverter.ToUInt16(o, body + 14);   // "fmt ": 16-bit PCM only
                else if (id == 0x61746164 && bits == 16)   // "data"
                {
                    var end = Math.Min(o.Length, body + size) & ~1;
                    for (int i = body; i + 1 < end; i += 2)
                    {
                        var v = (int)Math.Round((short)(o[i] | o[i + 1] << 8) * gain);
                        v = Math.Max(short.MinValue, Math.Min(short.MaxValue, v));
                        o[i] = (byte)v;
                        o[i + 1] = (byte)(v >> 8);
                    }
                    break;
                }
                at = body + size + (size & 1);
            }
            return o;
        }
    }
}
