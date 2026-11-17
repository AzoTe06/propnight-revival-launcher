using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PropnightRevival
{
    /// <summary>
    /// A sound bank addon (revival tools/fmod/ezaddon.py, "EZADDON1"): a few changed sounds and metadata bytes of the
    /// game's FMOD Master.bank, instead of the whole 257 MB bank. Applied to the original bank it gives the modified
    /// bank, checked against the SHA-256 the addon records; the original is kept in revival-backups.
    /// </summary>
    static class BankAddon
    {
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("EZADDON1");

        public static Dictionary<string, object> Header(byte[] addon, out int dataAt)
        {
            if (addon.Length < 12 || !addon.Take(8).SequenceEqual(Magic)) throw new Exception("not a sound bank addon");
            int n = BitConverter.ToInt32(addon, 8);
            dataAt = 12 + n;
            return Json.Parse(Encoding.UTF8.GetString(addon, 12, n));
        }

        class Entry { public ulong Mode; public byte[] Extra; public byte[] Data; }

        // FSB5 at the end of the bank: header, sample headers, name table, then the samples' data (offsets in 32 bytes)
        static (byte[] head, List<Entry> ents, byte[] names) ParseFsb(byte[] b, int at)
        {
            uint ver = BitConverter.ToUInt32(b, at + 4);
            int n = BitConverter.ToInt32(b, at + 8), shs = BitConverter.ToInt32(b, at + 12);
            int nts = BitConverter.ToInt32(b, at + 16), ds = BitConverter.ToInt32(b, at + 20);
            int hs = ver == 1 ? 0x3C : 0x40;
            int q = at + hs;
            var ents = new List<Entry>();
            var offs = new List<long>();
            for (int i = 0; i < n; i++)
            {
                ulong m = BitConverter.ToUInt64(b, q);
                int start = q + 8;
                q += 8;
                bool next = (m & 1) != 0;
                while (next)
                {
                    uint c = BitConverter.ToUInt32(b, q);
                    next = (c & 1) != 0;
                    q += 4 + (int)((c >> 1) & 0xFFFFFF);
                }
                ents.Add(new Entry { Mode = m, Extra = Sub(b, start, q - start) });
                offs.Add((long)((m >> 7) & 0x7FFFFFF) * 32);
            }
            long data = at + hs + shs + nts;
            for (int i = 0; i < n; i++)
            {
                long from = data + offs[i], to = data + (i + 1 < n ? offs[i + 1] : ds);
                ents[i].Data = Sub(b, (int)from, (int)(to - from));
            }
            return (Sub(b, at, hs), ents, Sub(b, at + hs + shs, nts));
        }

        static byte[] Sub(byte[] b, int at, int len) { var r = new byte[len]; Buffer.BlockCopy(b, at, r, 0, len); return r; }

        const ulong OffsetMask = 0x7FFFFFFUL << 7;

        public static byte[] Apply(byte[] bank, byte[] addon)
        {
            var h = Header(addon, out int q);
            if (bank.LongLength != h.Long("base_size") || Hash.Sha256(bank) != h.Str("base_sha256"))
                throw new Exception("not the original Master.bank this addon was made for");
            int fsbAt = (int)h.Long("fsb_at");
            var meta = new byte[fsbAt];
            Buffer.BlockCopy(bank, 0, meta, 0, fsbAt);
            foreach (var run in ((object[])h["meta"]).Cast<object[]>())
            {
                var bytes = Hash.FromHex((string)run[1]);
                Buffer.BlockCopy(bytes, 0, meta, Convert.ToInt32(run[0]), bytes.Length);
            }
            var (head, ents, names) = ParseFsb(bank, fsbAt);
            foreach (var s in h.Objs("sounds"))
            {
                var e = ents[(int)s.Long("index")];
                e.Mode = (e.Mode & OffsetMask) | Convert.ToUInt64(s.Str("mode"), 16);
                e.Extra = Hash.FromHex(s.Str("extra"));
                int size = (int)s.Long("size");
                e.Data = Sub(addon, q, size);
                q += size;
            }
            using (var outp = new MemoryStream((int)h.Long("out_size")))
            {
                var sh = new MemoryStream();
                var data = new MemoryStream();
                foreach (var e in ents)
                {
                    ulong m = (e.Mode & ~OffsetMask) | ((ulong)(data.Length / 32) << 7);
                    sh.Write(BitConverter.GetBytes(m), 0, 8);
                    sh.Write(e.Extra, 0, e.Extra.Length);
                    data.Write(e.Data, 0, e.Data.Length);
                    int pad = (int)(-e.Data.Length & 31);
                    if (pad > 0) data.Write(new byte[pad], 0, pad);
                }
                Buffer.BlockCopy(BitConverter.GetBytes((int)sh.Length), 0, head, 12, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(names.Length), 0, head, 16, 4);
                Buffer.BlockCopy(BitConverter.GetBytes((int)data.Length), 0, head, 20, 4);
                outp.Write(meta, 0, meta.Length);
                outp.Write(head, 0, head.Length);
                sh.WriteTo(outp);
                outp.Write(names, 0, names.Length);
                data.WriteTo(outp);
                var result = outp.ToArray();
                if (Hash.Sha256(result) != h.Str("out_sha256")) throw new Exception("the rebuilt bank does not match");
                return result;
            }
        }
    }
}
