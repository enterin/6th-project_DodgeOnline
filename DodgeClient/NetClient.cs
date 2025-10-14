using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DodgeBattleStarter
{
    public class NetPlayer
    {
        public string Id;
        public string Name;
        public float X, Y;
        public bool Alive;
        public int Score;
    }
    public class NetSnapshot
    {
        public int Tick;
        public int Round = 1;
        public string Phase = "playing"; // "playing" / "await" / "countdown"
        public int CountdownMs = 0;      // ★ 남은 ms
        public int VoteCount = 0;
        public int NeedCount = 0;
        public List<NetPlayer> Players = new List<NetPlayer>();
        public List<PointF> Obstacles = new List<PointF>();
    }


    public class NetClient
    {
        TcpClient _tcp;
        NetworkStream _ns;
        Thread _recvThread;
        volatile bool _running;

        readonly byte[] _lenBuf = new byte[4];
        readonly byte[] _bodyBuf = new byte[64 * 1024];

        public string MyId { get; private set; }
        public int Seed { get; private set; }
        public int TickHz { get; private set; }
        public int SnapshotHz { get; private set; }

        readonly object _lock = new object();
        NetSnapshot _latest = null;

        public bool Connect(string host, int port, string name)
        {
            try
            {
                _tcp = new TcpClient();
                _tcp.NoDelay = true;
                _tcp.Connect(host, port);
                _ns = _tcp.GetStream();

                _running = true;
                _recvThread = new Thread(RecvLoop) { IsBackground = true };
                _recvThread.Start();

                SendJson("{\"cmd\":\"JOIN\",\"name\":\"" + Escape(name) + "\"}");
                return true;
            }
            catch { return false; }
        }

        public void Close()
        {
            _running = false;
            try { _ns.Close(); } catch { }
            try { _tcp.Close(); } catch { }
        }

        public void SendInput(bool left, bool right, bool up)
        {
            if (_ns == null) return;
            string json = "{"
                + "\"cmd\":\"INPUT\","
                + "\"left\":" + (left ? "true" : "false") + ","
                + "\"right\":" + (right ? "true" : "false") + ","
                + "\"up\":" + (up ? "true" : "false")
                + "}";
            SendJson(json);
        }

        public void SendRespawn()
        {
            if (_ns == null) return;
            SendJson("{\"cmd\":\"RESPAWN\"}");
        }

        void SendJson(string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] len = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(body.Length));
            try
            {
                _ns.Write(len, 0, len.Length);
                _ns.Write(body, 0, body.Length);
            }
            catch { }
        }

        void RecvLoop()
        {
            try
            {
                while (_running)
                {
                    string json = RecvJson();
                    if (json == null) break;

                    if (json.IndexOf("\"cmd\":\"WELCOME\"") >= 0)
                    {
                        MyId = ExtractString(json, "id");
                        Seed = ExtractInt(json, "seed");
                        TickHz = ExtractInt(json, "tick_hz");
                        SnapshotHz = ExtractInt(json, "snapshot_hz");
                    }
                    else if (json.IndexOf("\"cmd\":\"SNAPSHOT\"") >= 0)
                    {
                        var snap = ParseSnapshot(json);
                        lock (_lock) _latest = snap;
                    }
                }
            }
            catch { }
            finally { Close(); }
        }

        string RecvJson()
        {
            int read = 0;
            while (read < 4)
            {
                int r = _ns.Read(_lenBuf, read, 4 - read);
                if (r <= 0) return null;
                read += r;
            }
            int bodyLen = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(_lenBuf, 0));
            if (bodyLen <= 0 || bodyLen > _bodyBuf.Length) return null;

            int got = 0;
            while (got < bodyLen)
            {
                int r = _ns.Read(_bodyBuf, got, bodyLen - got);
                if (r <= 0) return null;
                got += r;
            }
            return Encoding.UTF8.GetString(_bodyBuf, 0, bodyLen);
        }

        public NetSnapshot TryGetSnapshot()
        {
            lock (_lock) return _latest;
        }

        // ===== 파서 =====
        static NetSnapshot ParseSnapshot(string json)
        {
            var snap = new NetSnapshot();
            snap.Tick = ExtractInt(json, "tick");
            snap.Round = ExtractInt(json, "round");
            string phase = ExtractString(json, "phase");
            if (!string.IsNullOrEmpty(phase)) snap.Phase = phase;
            snap.CountdownMs = ExtractInt(json, "countdown_ms");
            snap.VoteCount = ExtractInt(json, "vote_count");
            snap.NeedCount = ExtractInt(json, "need_count");

            int pArrStart = json.IndexOf("\"players\":[");
            if (pArrStart >= 0)
            {
                int pArrEnd = json.IndexOf("]", pArrStart);
                if (pArrEnd > pArrStart)
                {
                    string arr = json.Substring(pArrStart, pArrEnd - pArrStart + 1);
                    int idx = 0;
                    while (true)
                    {
                        int idStart = arr.IndexOf("\"id\":\"", idx);
                        if (idStart < 0) break;
                        int idEnd = arr.IndexOf("\"", idStart + 6);
                        string id = arr.Substring(idStart + 6, idEnd - (idStart + 6));

                        int nameStart = arr.IndexOf("\"name\":\"", idEnd);
                        int nameEnd = arr.IndexOf("\"", nameStart + 8);
                        string name = (nameStart > 0 && nameEnd > nameStart) ? arr.Substring(nameStart + 8, nameEnd - (nameStart + 8)) : "guest";

                        float x = ExtractFloatAfter(arr, "\"x\":", nameEnd);
                        float y = ExtractFloatAfter(arr, "\"y\":", nameEnd);
                        bool alive = ExtractBoolAfter(arr, "\"alive\":", nameEnd);
                        int score = (int)ExtractFloatAfter(arr, "\"score\":", nameEnd);

                        snap.Players.Add(new NetPlayer { Id = id, Name = name, X = x, Y = y, Alive = alive, Score = score });
                        idx = nameEnd + 1;
                    }
                }
            }

            int oArrStart = json.IndexOf("\"obstacles\":[");
            if (oArrStart >= 0)
            {
                int oArrEnd = json.IndexOf("]", oArrStart);
                if (oArrEnd > oArrStart)
                {
                    string arr = json.Substring(oArrStart, oArrEnd - oArrStart + 1);
                    int idx = 0;
                    while (true)
                    {
                        int xPos = arr.IndexOf("\"x\":", idx);
                        if (xPos < 0) break;
                        float x = ExtractFloatAfter(arr, "\"x\":", xPos);
                        float y = ExtractFloatAfter(arr, "\"y\":", xPos);
                        snap.Obstacles.Add(new PointF(x, y));
                        idx = xPos + 4;
                    }
                }
            }
            return snap;
        }

        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        static string ExtractString(string json, string key)
        {
            int i = json.IndexOf("\"" + key + "\"");
            if (i < 0) return null;
            i = json.IndexOf(':', i);
            if (i < 0) return null;
            i = json.IndexOf('"', i);
            if (i < 0) return null;
            int j = json.IndexOf('"', i + 1);
            if (j < 0) return null;
            return json.Substring(i + 1, j - i - 1);
        }
        static int ExtractInt(string json, string key)
        {
            int i = json.IndexOf("\"" + key + "\"");
            if (i < 0) return 0;
            i = json.IndexOf(':', i);
            if (i < 0) return 0;
            int j = i + 1;
            while (j < json.Length && (json[j] == ' ' || json[j] == '\t')) j++;
            int k = j;
            while (k < json.Length && "-0123456789".IndexOf(json[k]) >= 0) k++;
            int.TryParse(json.Substring(j, k - j), out int v);
            return v;
        }
        static float ExtractFloatAfter(string s, string key, int start)
        {
            int i = s.IndexOf(key, start);
            if (i < 0) return 0f;
            i += key.Length;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            int k = i;
            while (k < s.Length && "-0123456789.eE".IndexOf(s[k]) >= 0) k++;
            float.TryParse(s.Substring(i, k - i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v);
            return v;
        }
        static bool ExtractBoolAfter(string s, string key, int start)
        {
            int i = s.IndexOf(key, start);
            if (i < 0) return false;
            i += key.Length;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            return s.IndexOf("true", i) == i;
        }
    }
}
