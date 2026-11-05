using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;

namespace PropnightRevival
{
    /// <summary>
    /// Discord Rich Presence through the Discord app's local pipe (\\.\pipe\discord-ipc-N): a handshake with the
    /// project's Discord application, then SET_ACTIVITY. Frames are an opcode and a length (little-endian int32)
    /// followed by JSON. Nothing happens when Discord is not running; the pipe is opened again on the next update.
    /// Images are https URLs (the site's map pictures), so the Discord application needs no uploaded assets.
    /// </summary>
    sealed class DiscordRpc : IDisposable
    {
        const string ClientId = "1558224227671220317";   // the Discord application of Propnight Revival (OAuth linking too)
        NamedPipeClientStream pipe;
        string lastSent;
        DateTime nextTry = DateTime.MinValue;
        readonly object gate = new object();

        /// <summary>Shows this activity (null clears it). Only sent when it changed.</summary>
        public Task Set(Dictionary<string, object> activity) => Task.Run(() =>
        {
            lock (gate)
            {
                var json = activity == null ? "null" : Json.Write(activity);
                if (json == lastSent && pipe != null) return;
                try
                {
                    if (pipe == null && !Connect()) return;
                    Send(1, new Dictionary<string, object>
                    {
                        ["cmd"] = "SET_ACTIVITY", ["nonce"] = Guid.NewGuid().ToString(),
                        ["args"] = new Dictionary<string, object> { ["pid"] = Process.GetCurrentProcess().Id, ["activity"] = activity },
                    });
                    Receive();
                    if (lastSent == null || (json == "null") != (lastSent == "null")) Log.Write("discord activity " + (activity == null ? "cleared" : "shown"));
                    lastSent = json;
                }
                catch (Exception e)
                {
                    Log.Write("discord: " + e.Message);
                    Close();
                }
            }
        });

        bool Connect()
        {
            if (DateTime.UtcNow < nextTry) return false;
            nextTry = DateTime.UtcNow.AddSeconds(30);   // Discord closed: look again a little later
            for (int i = 0; i < 10; i++)
            {
                var p = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.None);
                try { p.Connect(150); }
                catch { p.Dispose(); continue; }
                pipe = p;
                Send(0, new Dictionary<string, object> { ["v"] = 1, ["client_id"] = ClientId });
                Receive();   // READY
                return true;
            }
            return false;
        }

        void Send(int op, object payload)
        {
            var body = Encoding.UTF8.GetBytes(Json.Write(payload));
            var frame = new byte[8 + body.Length];
            BitConverter.GetBytes(op).CopyTo(frame, 0);
            BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
            body.CopyTo(frame, 8);
            pipe.Write(frame, 0, frame.Length);
            pipe.Flush();
        }

        void Receive()
        {
            var head = ReadExactly(8);
            int op = BitConverter.ToInt32(head, 0), length = BitConverter.ToInt32(head, 4);
            if (length < 0 || length > 1 << 20) throw new IOException("bad frame");
            var body = Encoding.UTF8.GetString(ReadExactly(length));
            if (op == 2) throw new IOException("closed by Discord: " + body);   // CLOSE
            if (body.Contains("\"evt\":\"ERROR\"")) throw new IOException(body);
        }

        byte[] ReadExactly(int count)
        {
            var buffer = new byte[count];
            for (int read = 0; read < count;)
            {
                int n = pipe.Read(buffer, read, count - read);
                if (n <= 0) throw new IOException("pipe closed");
                read += n;
            }
            return buffer;
        }

        void Close()
        {
            try { pipe?.Dispose(); } catch { }
            pipe = null;
            lastSent = null;
        }

        /// <summary>Not under the lock: a Set waiting for Discord's answer would block the launcher's exit.
        /// Closing the pipe ends that wait.</summary>
        public void Dispose() { try { pipe?.Dispose(); } catch { } }
    }
}
