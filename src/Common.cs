using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace PropnightRevival
{
    /// <summary>Constants shared by the launcher. Everything else comes from the signed manifest.</summary>
    static class Config
    {
        public const string Api = "https://api.propnightrevival.online";
        public const string ManifestUrl = Api + "/updates/manifest.json";
        public const string SignatureUrl = Api + "/updates/manifest.sig";
        // ECDSA P-256 public key (X||Y) of the update signing key (revival/release/signing-public-key.txt).
        public const string UpdatePublicKey = "DAShBnxG+tOeg2yvXcmhm85O8Myyv37T7HVVd7ld/YEViijaCNPNj8CFT+zovWcvrj9JdV8RKJga8HNS3o+zkg==";
        public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PropnightRevival");
        public static readonly string SaveGames = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Propnight", "Saved", "SaveGames");
        public static string Version => typeof(Config).Assembly.GetName().Version.ToString(3);
    }

    static class Json
    {
        static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
        public static Dictionary<string, object> Parse(string text) => (Dictionary<string, object>)Serializer.DeserializeObject(text);
        public static string Write(object value) => Serializer.Serialize(value);
        public static string Str(this Dictionary<string, object> d, string key) => d != null && d.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;
        public static long Long(this Dictionary<string, object> d, string key) => Convert.ToInt64(d[key], System.Globalization.CultureInfo.InvariantCulture);
        public static bool Bool(this Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) && v is bool b && b;
        public static Dictionary<string, object> Obj(this Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;
        public static IEnumerable<Dictionary<string, object>> Objs(this Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out var v) && v is object[] a ? a.OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();
        public static IEnumerable<string> Strs(this Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out var v) && v is object[] a ? a.Select(x => Convert.ToString(x)) : Enumerable.Empty<string>();
    }

    static class Hash
    {
        public static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));
        public static byte[] FromHex(string hex) => Enumerable.Range(0, hex.Length / 2).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();

        public static string Sha256File(string path, Action<long> progress = null)
        {
            using (var sha = SHA256.Create())
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                var buffer = new byte[4 << 20];
                long done = 0;
                int n;
                while ((n = f.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    done += n;
                    progress?.Invoke(done);
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                return Hex(sha.Hash);
            }
        }

        public static string Sha256(byte[] data) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(data)); }

        /// <summary>Verifies the manifest signature (ECDSA P-256, SHA-256, raw r||s).</summary>
        public static bool VerifyManifest(byte[] data, byte[] signature)
        {
            var xy = Convert.FromBase64String(Config.UpdatePublicKey);
            var blob = new byte[8 + 64];
            BitConverter.GetBytes(0x31534345).CopyTo(blob, 0);   // BCRYPT_ECDSA_PUBLIC_P256_MAGIC "ECS1"
            BitConverter.GetBytes(32).CopyTo(blob, 4);
            xy.CopyTo(blob, 8);
            using (var key = CngKey.Import(blob, CngKeyBlobFormat.EccPublicBlob))
            using (var ecdsa = new ECDsaCng(key))
                return signature.Length == 64 && ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
    }

    static class Log
    {
        static readonly object Lock = new object();
        public static void Write(string message)
        {
            lock (Lock)
            {
                try
                {
                    Directory.CreateDirectory(Config.DataDir);
                    File.AppendAllText(Path.Combine(Config.DataDir, "launcher.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}", Encoding.UTF8);
                }
                catch { }
            }
        }
    }
}
