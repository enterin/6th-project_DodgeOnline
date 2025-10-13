using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;           // Rectangle / RectangleF
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DodgeServer
{
    // 길이(4바이트, Big-Endian) + UTF-8 JSON 문자열
    static class Wire
    {
        public static void SendJson(NetworkStream ns, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] len = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(body.Length));
            ns.Write(len, 0, len.Length);
            ns.Write(body, 0, body.Length);
        }

        public static string RecvJson(NetworkStream ns, byte[] lenBuf, byte[] bodyBuf)
        {
            int read = 0;
            while (read < 4)
            {
                int r = ns.Read(lenBuf, read, 4 - read);
                if (r <= 0) return null;
                read += r;
            }
            int bodyLen = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lenBuf, 0));
            if (bodyLen <= 0 || bodyLen > bodyBuf.Length) return null;

            int got = 0;
            while (got < bodyLen)
            {
                int r = ns.Read(bodyBuf, got, bodyLen - got);
                if (r <= 0) return null;
                got += r;
            }
            return Encoding.UTF8.GetString(bodyBuf, 0, bodyLen);
        }
    }

    class Player
    {
        public string Id;
        public string Name;
        public TcpClient Tcp;
        public NetworkStream Ns;
        public float X, Y, VX, VY;
        public bool Left, Right, Up;
        public bool Alive = true;
        public int Score = 0;
        public DateTime LastPing = DateTime.UtcNow;

        public readonly byte[] LenBuf = new byte[4];
        public readonly byte[] BodyBuf = new byte[64 * 1024];
    }

    enum Phase { Playing, AwaitingRestart }

    class GameServer
    {
        // ===== 게임 상수 =====
        const int TickHz = 60;
        const float Gravity = 1200f;
        const float MoveSpeed = 320f;
        const float JumpVel = 520f;
        const int WorldMargin = 24;
        const int GroundMargin = 84;
        const int ObstacleW = 24, ObstacleH = 24;
        const int SnapshotHz = 20;
        const int SpawnMs = 750;

        // ===== 월드 크기 (클라와 합의) =====
        readonly int _worldW = 900;
        readonly int _worldH = 600;

        // ===== 서버 필드 =====
        readonly string _host;
        readonly int _port;
        TcpListener _listener;
        volatile bool _running;

        readonly object _lock = new object();
        readonly Dictionary<string, Player> _players = new Dictionary<string, Player>();
        readonly List<RectangleF> _obstacles = new List<RectangleF>();

        readonly Stopwatch _sw = new Stopwatch();
        long _prevMs;
        int _spawnAccumMs;
        int _tick;

        readonly int _seed = Environment.TickCount;
        Random _rng;

        Thread _acceptThread;
        Thread _gameThread;
        Thread _snapshotThread;

        Phase _phase = Phase.Playing;
        readonly HashSet<string> _respawnVotes = new HashSet<string>();

        public GameServer(string host, int port) { _host = host; _port = port; }

        public void Start()
        {
            _rng = new Random(_seed);
            _listener = new TcpListener(IPAddress.Parse(_host), _port);
            _listener.Start();
            _running = true;

            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _gameThread = new Thread(GameLoop) { IsBackground = true };
            _snapshotThread = new Thread(SnapshotLoop) { IsBackground = true };

            _sw.Start();
            _prevMs = _sw.ElapsedMilliseconds;

            _acceptThread.Start();
            _gameThread.Start();
            _snapshotThread.Start();

            Console.WriteLine("[INFO] seed=" + _seed);
        }

        public void Stop()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
        }

        void AcceptLoop()
        {
            Console.WriteLine("[INFO] AcceptLoop started");
            while (_running)
            {
                try
                {
                    var tcp = _listener.AcceptTcpClient();
                    tcp.NoDelay = true;
                    var ns = tcp.GetStream();

                    var p = new Player
                    {
                        Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                        Name = "guest",
                        Tcp = tcp,
                        Ns = ns
                    };

                    lock (_lock) _players[p.Id] = p;

                    string welcome = "{"
                        + "\"cmd\":\"WELCOME\","
                        + "\"id\":\"" + p.Id + "\","
                        + "\"seed\":" + _seed + ","
                        + "\"tick_hz\":" + TickHz + ","
                        + "\"snapshot_hz\":" + SnapshotHz
                        + "}";
                    Wire.SendJson(ns, welcome);

                    Console.WriteLine("[JOIN] " + p.Id + " connected");

                    var th = new Thread(() => RecvLoop(p)) { IsBackground = true };
                    th.Start();
                }
                catch (SocketException) { if (!_running) break; }
                catch (Exception ex) { Console.WriteLine("[ACCEPT ERR] " + ex.Message); }
            }
        }

        void RecvLoop(Player p)
        {
            try
            {
                while (_running && p.Tcp.Connected)
                {
                    string json = Wire.RecvJson(p.Ns, p.LenBuf, p.BodyBuf);
                    if (json == null) break;

                    if (json.IndexOf("\"cmd\":\"JOIN\"") >= 0)
                    {
                        string nm = ExtractString(json, "name");
                        if (!string.IsNullOrEmpty(nm)) p.Name = nm;
                        Console.WriteLine("[JOIN] name=" + p.Name + " id=" + p.Id);
                    }
                    else if (json.IndexOf("\"cmd\":\"INPUT\"") >= 0)
                    {
                        bool left = ExtractBool(json, "left");
                        bool right = ExtractBool(json, "right");
                        bool up = ExtractBool(json, "up");
                        lock (_lock)
                        {
                            p.Left = left; p.Right = right; p.Up = up;
                            p.LastPing = DateTime.UtcNow;
                        }
                    }
                    else if (json.IndexOf("\"cmd\":\"RESPAWN\"") >= 0)
                    {
                        lock (_lock)
                        {
                            if (_phase == Phase.AwaitingRestart)
                            {
                                _respawnVotes.Add(p.Id);
                                Console.WriteLine("[VOTE] " + p.Id + " -> " + _respawnVotes.Count + "/" + _players.Count);
                                if (_players.Count > 0 && _respawnVotes.Count >= _players.Count)
                                    RestartRound_Locked();
                            }
                            // Playing 중엔 무시(원하면 개인 리스폰 로직을 여기 넣을 수도 있음)
                        }
                    }
                }
            }
            catch { }
            finally { Disconnect(p); }
        }

        void Disconnect(Player p)
        {
            lock (_lock)
            {
                if (_players.Remove(p.Id))
                {
                    _respawnVotes.Remove(p.Id);
                    Console.WriteLine("[LEAVE] " + p.Id);
                    try { p.Ns.Close(); } catch { }
                    try { p.Tcp.Close(); } catch { }

                    // 투표 대기 중이고 남은 인원 수와 투표 수가 맞으면 즉시 재시작
                    if (_phase == Phase.AwaitingRestart && _players.Count > 0 && _respawnVotes.Count >= _players.Count)
                        RestartRound_Locked();
                }
            }
        }

        void GameLoop()
        {
            Console.WriteLine("[INFO] GameLoop started @" + TickHz + "Hz");
            while (_running)
            {
                long now = _sw.ElapsedMilliseconds;
                float dt = (now - _prevMs) / 1000f;
                if (dt <= 0f) { Thread.Sleep(1); continue; }
                _prevMs = now;

                float fixedDt = 1f / (float)TickHz;
                float acc = dt;
                while (acc > 0f)
                {
                    float step = (acc > fixedDt) ? fixedDt : acc;
                    Step(step);
                    acc -= step;
                }
                Thread.Sleep(0);
            }
        }

        void Step(float dt)
        {
            _tick++;

            lock (_lock)
            {
                if (_phase == Phase.Playing)
                {
                    // 스폰
                    _spawnAccumMs += (int)(dt * 1000);
                    while (_spawnAccumMs >= SpawnMs)
                    {
                        _spawnAccumMs -= SpawnMs;
                        SpawnObstacle();
                    }

                    // 플레이어 물리/충돌
                    foreach (var kv in _players)
                    {
                        var p = kv.Value;
                        if (!p.Alive) continue;

                        p.VX = 0f;
                        if (p.Left) p.VX -= MoveSpeed;
                        if (p.Right) p.VX += MoveSpeed;
                        if (p.Up && IsOnGround(p)) p.VY = -JumpVel;

                        p.VY += Gravity * dt;
                        p.X += p.VX * dt;
                        p.Y += p.VY * dt;

                        ApplyBounds(p);

                        Rectangle pr = Rectangle.Round(new RectangleF(p.X, p.Y, 40, 40));
                        for (int i = 0; i < _obstacles.Count; i++)
                        {
                            if (pr.IntersectsWith(Rectangle.Round(_obstacles[i])))
                            {
                                p.Alive = false;
                                break;
                            }
                        }
                    }

                    // 장애물 하강/소거 + 점수
                    for (int i = _obstacles.Count - 1; i >= 0; i--)
                    {
                        RectangleF r = _obstacles[i];
                        r.Y += 320f * dt;
                        _obstacles[i] = r;

                        if (r.Top > (_worldH + 8))
                        {
                            _obstacles.RemoveAt(i);
                            foreach (var kv in _players)
                                if (kv.Value.Alive) kv.Value.Score += 5;
                        }
                    }

                    // 모두 사망 감지 → 투표 대기 상태로 전환
                    int aliveCount = 0;
                    foreach (var kv in _players)
                        if (kv.Value.Alive) aliveCount++;

                    if (_players.Count > 0 && aliveCount == 0)
                    {
                        _phase = Phase.AwaitingRestart;
                        _respawnVotes.Clear();
                        Console.WriteLine("[ROUND] All dead -> AwaitingRestart (press R to vote)");
                    }
                }
                else // AwaitingRestart
                {
                    // 월드 업데이트 정지(입력은 RecvLoop에서 계속 누적)
                }
            }
        }

        void RestartRound_Locked()
        {
            _respawnVotes.Clear();
            _phase = Phase.Playing;

            _obstacles.Clear();
            _spawnAccumMs = 0;
            _tick = 0;

            // 같은 패턴 유지하려면 _seed 그대로, 새 패턴 원하면 새 시드 사용
            _rng = new Random(_seed);

            foreach (var kv in _players)
            {
                var p = kv.Value;
                p.Alive = true;
                p.VX = p.VY = 0f;

                float startX = (_worldW / 2f) - 20f;
                float groundY = _worldH - GroundMargin - 40f;
                p.X = startX;
                p.Y = groundY;

                // 라운드 점수 초기화 원하면 활성화
                // p.Score = 0;
            }

            Console.WriteLine("[ROUND] Restarted");
            BroadcastSnapshot(); // 즉시 한 번 쏴서 UI 갱신 빠르게
        }

        void ApplyBounds(Player p)
        {
            float left = WorldMargin;
            float right = _worldW - WorldMargin - 40;
            float groundY = _worldH - GroundMargin - 40;

            if (p.X < left) p.X = left;
            if (p.X > right) p.X = right;

            if (p.Y >= groundY) { p.Y = groundY; p.VY = 0f; }
        }

        bool IsOnGround(Player p)
        {
            float groundY = _worldH - GroundMargin - 40;
            return Math.Abs(p.Y - groundY) < 0.5f;
        }

        void SpawnObstacle()
        {
            int x = _rng.Next(WorldMargin, _worldW - WorldMargin - ObstacleW);
            _obstacles.Add(new RectangleF(x, -ObstacleH, ObstacleW, ObstacleH));
        }

        void SnapshotLoop()
        {
            int intervalMs = 1000 / SnapshotHz;
            Console.WriteLine("[INFO] SnapshotLoop @" + SnapshotHz + "Hz");
            while (_running)
            {
                Thread.Sleep(intervalMs);
                BroadcastSnapshot();
            }
        }

        void BroadcastSnapshot()
        {
            StringBuilder sb = new StringBuilder(2048);
            sb.Append("{\"cmd\":\"SNAPSHOT\",\"tick\":").Append(_tick).Append(",")
              .Append("\"phase\":\"").Append(_phase == Phase.Playing ? "playing" : "await").Append("\",")
              .Append("\"vote_count\":").Append(_respawnVotes.Count).Append(",")
              .Append("\"need_count\":").Append(_players.Count).Append(",");

            lock (_lock)
            {
                sb.Append("\"players\":[");
                bool first = true;
                foreach (var kv in _players)
                {
                    var p = kv.Value;
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"id\":\"").Append(p.Id).Append("\",")
                      .Append("\"name\":\"").Append(Escape(p.Name)).Append("\",")
                      .Append("\"x\":").Append(p.X.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(",")
                      .Append("\"y\":").Append(p.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(",")
                      .Append("\"alive\":").Append(p.Alive ? "true" : "false").Append(",")
                      .Append("\"score\":").Append(p.Score).Append("}");
                }
                sb.Append("],");

                sb.Append("\"obstacles\":[");
                for (int i = 0; i < _obstacles.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var r = _obstacles[i];
                    sb.Append("{\"x\":")
                      .Append(r.X.ToString(System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",\"y\":")
                      .Append(r.Y.ToString(System.Globalization.CultureInfo.InvariantCulture))
                      .Append("}");
                }
                sb.Append("]}");

                string json = sb.ToString();

                foreach (var kv in _players)
                {
                    try { Wire.SendJson(kv.Value.Ns, json); }
                    catch { /* 끊어진 경우는 Disconnect에서 정리 */ }
                }
            }
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

        static bool ExtractBool(string json, string key)
        {
            int i = json.IndexOf("\"" + key + "\"");
            if (i < 0) return false;
            i = json.IndexOf(':', i);
            if (i < 0) return false;
            int j = i + 1;
            while (j < json.Length && Char.IsWhiteSpace(json[j])) j++;
            if (json.IndexOf("true", j) == j) return true;
            return false;
        }
    }
}
