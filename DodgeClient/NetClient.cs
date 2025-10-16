using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DodgeBattleStarter
{
    public class NetClient
    {
        // ====== DTOs ======
        public class NetPlayer { public string Id; public string Name; public float X, Y; public bool Alive; public int Score; }
        public class NetObstacle { public float X, Y, W, H; public int K; }

        //NEW: match_over 합계용
        public class NetTotal { public string Id; public string Name; public int Total; }

        public class NetSnapshot
        {
            public int Tick, Round;
            public string Phase;         // "lobby" | "countdown" | "playing" | "await"
            public int CountdownMs, VoteCount, NeedCount;
            public List<NetPlayer> Players = new List<NetPlayer>();
            public List<NetObstacle> Obstacles = new List<NetObstacle>();
            public int MatchRound;                       // 서버: "match_round"
            public int MatchTotal;                       // 서버: "match_total"
            public List<NetTotal> Totals = new List<NetTotal>();  // 서버: "totals"
        }

        public class NetLobbyPlayer { public string Id; public string Name; public string Color; public bool Ready; }
        public class NetLobby
        {
            public List<NetLobbyPlayer> Players = new List<NetLobbyPlayer>();
            public int Need;
            public int Ready;
            public int Ts;   // ★ 추가
        }

        // ====== State ======
        TcpClient _tcp;
        NetworkStream _ns;
        Thread _recvThread;
        volatile bool _running;

        public string MyId { get; private set; }
        int _tickHz = 60, _snapshotHz = 20;

        readonly object _snapLock = new object();
        NetSnapshot _snapshotLatest;

        readonly object _lobbyLock = new object();
        NetLobby _lobbyLatest;

        readonly byte[] _lenBuf = new byte[4];
        readonly byte[] _bodyBuf = new byte[128 * 1024];

        // ====== Public API ======
        public bool Connect(string host, int port, string nickname)
        {
            try
            {
                _tcp = new TcpClient();
                _tcp.NoDelay = true;
                _tcp.Connect(IPAddress.Parse(host), port);
                _ns = _tcp.GetStream();

                _running = true;
                _recvThread = new Thread(RecvLoop) { IsBackground = true };
                _recvThread.Start();

                // JOIN (닉네임 전송)
                SendJson("{\"cmd\":\"JOIN\",\"name\":\"" + Escape(nickname) + "\"}");
                return true;
            }
            catch
            {
                Cleanup();
                return false;
            }
        }

        public void Close()
        {
            _running = false;
            Cleanup();
        }

        public NetSnapshot TryGetSnapshot()
        {
            lock (_snapLock) return _snapshotLatest;
        }

        public NetLobby TryGetLobby()
        {
            lock (_lobbyLock) return _lobbyLatest;
        }

        public void SendInput(bool left, bool right, bool up)
        {
            SendJson("{\"cmd\":\"INPUT\",\"left\":" + (left ? "true" : "false")
                + ",\"right\":" + (right ? "true" : "false")
                + ",\"up\":" + (up ? "true" : "false") + "}");
        }

        public void SendRespawn() => SendJson("{\"cmd\":\"RESPAWN\"}");

        public void SendSetName(string name)
            => SendJson("{\"cmd\":\"SET_NAME\",\"name\":\"" + Escape(name) + "\"}");

        public void SendSetColor(string html)
            => SendJson("{\"cmd\":\"SET_COLOR\",\"color\":\"" + Escape(html) + "\"}");

        public void SendReady(bool ready)
            => SendJson("{\"cmd\":\"READY\",\"ready\":" + (ready ? "true" : "false") + "}");

        public void SendLeaveToLobby()  // ★ NEW
            => SendJson("{\"cmd\":\"LEAVE_TO_LOBBY\"}");

        // ====== Wire ======
        void SendJson(string json)
        {
            try
            {
                if (_ns == null) return;
                byte[] body = Encoding.UTF8.GetBytes(json);
                byte[] len = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(body.Length));
                _ns.Write(len, 0, 4);
                _ns.Write(body, 0, body.Length);
            }
            catch
            {
                // ignore
            }
        }

        string RecvJson()
        {
            try
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
            catch { return null; }
        }

        // ====== Recv ======
        void RecvLoop()
        {
            try
            {
                while (_running && _tcp != null && _tcp.Connected)
                {
                    string json = RecvJson();
                    if (json == null) break;

                    if (json.IndexOf("\"cmd\":\"WELCOME\"") >= 0)
                    {
                        MyId = ExtractString(json, "id");
                        int hz = ExtractInt(json, "tick_hz");
                        int shz = ExtractInt(json, "snapshot_hz");
                        if (hz > 0) _tickHz = hz;
                        if (shz > 0) _snapshotHz = shz;
                    }
                    else if (json.IndexOf("\"cmd\":\"SNAPSHOT\"") >= 0)
                    {
                        // 스냅샷 파싱 + 저장, 로비 해제
                        NetSnapshot snap = ParseSnapshot(json);

                        lock (_lobbyLock) _lobbyLatest = null; // ★ 로비 종료
                        lock (_snapLock) _snapshotLatest = snap;
                    }
                    else if (json.IndexOf("\"cmd\":\"LOBBY\"") >= 0)
                    {
                        NetLobby lb = ParseLobby(json);
                        lock (_lobbyLock) _lobbyLatest = lb;
                    }
                    // (그 외 cmd는 현재 없음)
                }
            }
            catch { }
            finally
            {
                Cleanup();
            }
        }

        void Cleanup()
        {
            try { if (_ns != null) _ns.Close(); } catch { }
            try { if (_tcp != null) _tcp.Close(); } catch { }
            _ns = null; _tcp = null; _running = false;

            lock (_snapLock) _snapshotLatest = null;
            lock (_lobbyLock) _lobbyLatest = null;
        }

        // ====== Parsers ======
        NetSnapshot ParseSnapshot(string json)
        {
            NetSnapshot s = new NetSnapshot();
            s.Tick = ExtractInt(json, "tick");
            s.Round = ExtractInt(json, "round");
            s.Phase = ExtractString(json, "phase") ?? "playing";
            s.CountdownMs = ExtractInt(json, "countdown_ms");
            s.VoteCount = ExtractInt(json, "vote_count");
            s.NeedCount = ExtractInt(json, "need_count");
            s.MatchRound = ExtractInt(json, "match_round");
            s.MatchTotal = ExtractInt(json, "match_total");

            // players
            int pArrS = json.IndexOf("\"players\":[");
            if (pArrS >= 0)
            {
                int pArrE = json.IndexOf("]", pArrS);
                if (pArrE > pArrS)
                {
                    string arr = json.Substring(pArrS, pArrE - pArrS + 1);
                    int idx = 0;
                    while (true)
                    {
                        int idS = arr.IndexOf("\"id\":\"", idx);
                        if (idS < 0) break;
                        int idE = arr.IndexOf("\"", idS + 6);
                        string id = arr.Substring(idS + 6, idE - (idS + 6));

                        string name = ExtractStringAfter(arr, "\"name\":\"", idE);
                        float x = ExtractFloatAfter(arr, "\"x\":", idE);
                        float y = ExtractFloatAfter(arr, "\"y\":", idE);
                        bool alive = ExtractBoolAfter(arr, "\"alive\":", idE);
                        int score = ExtractIntAfter(arr, "\"score\":", idE);

                        NetPlayer np = new NetPlayer { Id = id, Name = name, X = x, Y = y, Alive = alive, Score = score };
                        s.Players.Add(np);

                        int close = arr.IndexOf("}", idE);
                        idx = (close >= 0 ? close : idE) + 1;
                    }
                }
            }

            // obstacles
            int oArrS = json.IndexOf("\"obstacles\":[");
            if (oArrS >= 0)
            {
                int oArrE = json.IndexOf("]", oArrS);
                if (oArrE > oArrS)
                {
                    string arr = json.Substring(oArrS, oArrE - oArrS + 1);
                    int idx = 0;
                    while (true)
                    {
                        int xS = arr.IndexOf("\"x\":", idx);
                        if (xS < 0) break;

                        float x = ExtractFloatAfter(arr, "\"x\":", xS);
                        float y = ExtractFloatAfter(arr, "\"y\":", xS);
                        float w = ExtractFloatAfter(arr, "\"w\":", xS);
                        float h = ExtractFloatAfter(arr, "\"h\":", xS);
                        int k = ExtractIntAfter(arr, "\"k\":", xS);

                        s.Obstacles.Add(new NetObstacle { X = x, Y = y, W = w, H = h, K = k });

                        int close = arr.IndexOf("}", xS);
                        idx = (close >= 0 ? close : xS) + 1;
                    }
                }
            }
            int tArrS = json.IndexOf("\"totals\":[");
            if (tArrS >= 0)
            {
                int tArrE = json.IndexOf("]", tArrS);
                if (tArrE > tArrS)
                {
                    string arr = json.Substring(tArrS, tArrE - tArrS + 1);
                    int idx = 0;
                    while (true)
                    {
                        int idS = arr.IndexOf("\"id\":\"", idx);
                        if (idS < 0) break;
                        int idE = arr.IndexOf("\"", idS + 6);
                        string id = arr.Substring(idS + 6, idE - (idS + 6));

                        string name = ExtractStringAfter(arr, "\"name\":\"", idE);
                        int total = ExtractIntAfter(arr, "\"total\":", idE);

                        s.Totals.Add(new NetTotal { Id = id, Name = name, Total = total });

                        int close = arr.IndexOf("}", idE);
                        idx = (close >= 0 ? close : idE) + 1;
                    }
                }
            }

            return s;
        }

        NetLobby ParseLobby(string json)
        {
            NetLobby lb = new NetLobby();
            lb.Need = ExtractInt(json, "need_count");
            lb.Ready = ExtractInt(json, "ready_count");

            int arrStart = json.IndexOf("\"players\":[");
            if (arrStart >= 0)
            {
                int arrEnd = json.IndexOf("]", arrStart);
                if (arrEnd > arrStart)
                {
                    string arr = json.Substring(arrStart, arrEnd - arrStart + 1);
                    int idx = 0;
                    while (true)
                    {
                        int idS = arr.IndexOf("\"id\":\"", idx);
                        if (idS < 0) break;
                        int idE = arr.IndexOf("\"", idS + 6);
                        string id = arr.Substring(idS + 6, idE - (idS + 6));

                        string name = ExtractStringAfter(arr, "\"name\":\"", idE);
                        string color = ExtractStringAfter(arr, "\"color\":\"", idE);
                        bool ready = ExtractBoolAfter(arr, "\"ready\":", idE);

                        NetLobbyPlayer p = new NetLobbyPlayer { Id = id, Name = name, Color = color, Ready = ready };
                        lb.Players.Add(p);

                        int close = arr.IndexOf("}", idE);
                        idx = (close >= 0 ? close : idE) + 1;
                    }
                }
            }
            // ★ 수정: 서버에서 보낸 ts 사용 (기존: Environment.TickCount)
            lb.Ts = ExtractInt(json, "ts");
            return lb;
        }

        // ====== Helpers ======
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
            i++;
            while (i < json.Length && Char.IsWhiteSpace(json[i])) i++;
            int j = i;
            while (j < json.Length && (Char.IsDigit(json[j]) || json[j] == '-')) j++;
            int val;
            if (int.TryParse(json.Substring(i, j - i), NumberStyles.Integer, CultureInfo.InvariantCulture, out val))
                return val;
            return 0;
        }

        static string ExtractStringAfter(string s, string token, int offset)
        {
            int i = s.IndexOf(token, offset);
            if (i < 0) return null;
            i += token.Length;
            int j = s.IndexOf('"', i);
            if (j < 0) return null;
            return s.Substring(i, j - i);
        }

        static bool ExtractBoolAfter(string s, string token, int offset)
        {
            int i = s.IndexOf(token, offset);
            if (i < 0) return false;
            i += token.Length;
            while (i < s.Length && Char.IsWhiteSpace(s[i])) i++;
            return s.IndexOf("true", i, StringComparison.Ordinal) == i;
        }

        static int ExtractIntAfter(string s, string token, int offset)
        {
            int i = s.IndexOf(token, offset);
            if (i < 0) return 0;
            i += token.Length;
            while (i < s.Length && Char.IsWhiteSpace(s[i])) i++;
            int j = i;
            while (j < s.Length && (Char.IsDigit(s[j]) || s[j] == '-')) j++;
            int v;
            if (int.TryParse(s.Substring(i, j - i), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }

        static float ExtractFloatAfter(string s, string token, int offset)
        {
            int i = s.IndexOf(token, offset);
            if (i < 0) return 0f;
            i += token.Length;
            while (i < s.Length && Char.IsWhiteSpace(s[i])) i++;
            int j = i;
            while (j < s.Length && (Char.IsDigit(s[j]) || s[j] == '-' || s[j] == '+' || s[j] == '.' || s[j] == 'e' || s[j] == 'E')) j++;
            float v;
            if (float.TryParse(s.Substring(i, j - i), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return 0f;
        }
    }
}
