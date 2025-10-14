using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;           // Rectangle / RectangleF
using System.Globalization;
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

    class GameServer
    {
        // ===== 게임 상수 =====
        const int TickHz = 60;
        const float Gravity = 1200f;
        const float MoveSpeed = 320f;
        const float JumpVel = 520f;
        const int WorldMargin = 24;
        const int GroundMargin = 84;
        const int SnapshotHz = 20;

        const int PlayerW = 40;
        const int PlayerH = 40;

        // 라운드/카운트다운/페이즈
        enum Phase { Countdown, Playing, AwaitingRestart }
        int _round = 1;
        const int CountdownMs = 3000; // 3초
        int _countdownMsLeft = 0;
        int _roundElapsedMs = 0;      // 라운드 경과시간(ms)

        // 장애물 타입
        public enum ObKind { Knife = 0, Rock = 1, Fire = 2 }

        public struct Ob
        {
            public RectangleF Rect;
            public ObKind Kind;
            public Ob(float x, float y, float w, float h, ObKind k)
            {
                Rect = new RectangleF(x, y, w, h);
                Kind = k;
            }
        }

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
        readonly List<Ob> _obstacles = new List<Ob>();

        readonly Stopwatch _sw = new Stopwatch();
        long _prevMs;
        int _tick;

        readonly int _seed = Environment.TickCount;
        Random _rng;

        Thread _acceptThread;
        Thread _gameThread;
        Thread _snapshotThread;

        Phase _phase = Phase.Playing;
        readonly HashSet<string> _respawnVotes = new HashSet<string>();

        // ====== 종류별 스폰 간격 ======
        const int KnifeSpawnMs = 750;
        const int FireSpawnMs = 600;
        const int RockSpawnMs = 750;

        int _spawnAccumKnifeMs;
        int _spawnAccumFireMs;
        int _spawnAccumRockMs;

        // 가속/버스트 방지용: 이전 유효주기 기억 + 해금 상태 + 틱당 컷
        int _prevKnifeEffMs = KnifeSpawnMs;
        int _prevFireEffMs = FireSpawnMs;
        int _prevRockEffMs = RockSpawnMs;
        bool _prevKnifeUnlocked = false;
        bool _prevFireUnlocked = false;
        bool _prevRockUnlocked = false;
        const int MaxSpawnPerTickPerKind = 2;     // 틱당 종류별 최대 스폰 수
        const int MinEffMs = 120;                 // 가속 최소 주기(너무 급격한 폭주 방지)

        public GameServer(string host, int port) { _host = host; _port = port; }

        public void Start()
        {
            _phase = Phase.Countdown; _countdownMsLeft = CountdownMs;
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

        // ====== 장애물 스폰/업데이트 파라미터 ======

        // 시간 경과에 따라 해금된 종류 목록을 돌려줌
        List<ObKind> GetUnlockedKinds()
        {
            // 0~10s: Knife만, 10~20s: Knife+Fire, 20s~: Knife+Rock+Fire
            if (_roundElapsedMs < 10000) return new List<ObKind> { ObKind.Knife };
            if (_roundElapsedMs < 20000) return new List<ObKind> { ObKind.Knife, ObKind.Fire };
            return new List<ObKind> { ObKind.Knife, ObKind.Rock, ObKind.Fire };
        }

        // 종류별 크기 (스냅샷에도 그대로 전달)
        void GetSize(ObKind k, out float w, out float h)
        {
            if (k == ObKind.Knife) { w = 24; h = 24; }
            else if (k == ObKind.Rock) { w = 30; h = 30; }
            else { w = 20; h = 20; } // Fire
        }

        // 종류별 낙하 속도
        float GetFallSpeed(ObKind k)
        {
            if (k == ObKind.Knife) return 320f;
            if (k == ObKind.Rock) return 260f;
            return 380f; // Fire
        }

        // === 특정 종류만 스폰 ===
        void SpawnObstacleOfKind(ObKind kind)
        {
            float w, h; GetSize(kind, out w, out h);
            int x = _rng.Next(WorldMargin, _worldW - WorldMargin - (int)w);
            _obstacles.Add(new Ob(x, -h, w, h, kind));
            Console.WriteLine($"[SPAWN] {kind} x={x}");
        }

        void UpdateObstacles(float dt)
        {
            for (int i = _obstacles.Count - 1; i >= 0; i--)
            {
                var ob = _obstacles[i];
                var r = ob.Rect;
                r.Y += GetFallSpeed(ob.Kind) * dt;
                ob.Rect = r;
                _obstacles[i] = ob;

                if (r.Top > _worldH + 8)
                {
                    _obstacles.RemoveAt(i);
                    // 생존 보너스: 살아있는 모든 플레이어 +5
                    foreach (var kv in _players)
                        if (kv.Value.Alive) kv.Value.Score += 5;
                }
            }
        }

        // === 30초마다 스폰 속도 ×1.2 → 주기 ÷1.2 ===
        float GetSpawnScale()
        {
            int stages = _roundElapsedMs / 30000;          // 0,1,2,...
            double scale = Math.Pow(1.2, stages);          // 1.0, 1.2, 1.44, ...
            return (float)scale;
        }

        // ===== 메인 루프 =====
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
                if (_phase == Phase.Countdown)
                {
                    _countdownMsLeft -= (int)(dt * 1000f);
                    if (_countdownMsLeft <= 0)
                    {
                        _countdownMsLeft = 0;
                        _phase = Phase.Playing;
                        _roundElapsedMs = 0;
                        Console.WriteLine("[ROUND] Round " + _round + " START");
                    }
                    return;
                }

                if (_phase == Phase.Playing)
                {
                    _roundElapsedMs += (int)(dt * 1000f);

                    // ===== 해금 상태/유효 주기 계산 =====
                    var unlocked = GetUnlockedKinds();
                    bool knifeUnl = unlocked.Contains(ObKind.Knife);
                    bool fireUnl = unlocked.Contains(ObKind.Fire);
                    bool rockUnl = unlocked.Contains(ObKind.Rock);

                    float spawnScale = GetSpawnScale();
                    int knifeMsEff = (int)Math.Max(MinEffMs, KnifeSpawnMs / spawnScale);
                    int fireMsEff = (int)Math.Max(MinEffMs, FireSpawnMs / spawnScale);
                    int rockMsEff = (int)Math.Max(MinEffMs, RockSpawnMs / spawnScale);

                    // ===== 해금 전 누적 금지 & 해금 순간 0으로 초기화 =====
                    int addMs = (int)(dt * 1000);

                    if (knifeUnl)
                    {
                        if (!_prevKnifeUnlocked) { _spawnAccumKnifeMs = 0; _prevKnifeEffMs = knifeMsEff; }
                        _spawnAccumKnifeMs += addMs;
                    }
                    else _spawnAccumKnifeMs = 0;

                    if (fireUnl)
                    {
                        if (!_prevFireUnlocked) { _spawnAccumFireMs = 0; _prevFireEffMs = fireMsEff; }
                        _spawnAccumFireMs += addMs;
                    }
                    else _spawnAccumFireMs = 0;

                    if (rockUnl)
                    {
                        if (!_prevRockUnlocked) { _spawnAccumRockMs = 0; _prevRockEffMs = rockMsEff; }
                        _spawnAccumRockMs += addMs;
                    }
                    else _spawnAccumRockMs = 0;

                    // ===== 가속 단계 변화 시 누적 리스케일(폭주 방지) =====
                    if (knifeMsEff != _prevKnifeEffMs)
                    {
                        _spawnAccumKnifeMs = (int)Math.Min(_spawnAccumKnifeMs * (double)knifeMsEff / _prevKnifeEffMs, knifeMsEff - 1);
                        _prevKnifeEffMs = knifeMsEff;
                    }
                    if (fireMsEff != _prevFireEffMs)
                    {
                        _spawnAccumFireMs = (int)Math.Min(_spawnAccumFireMs * (double)fireMsEff / _prevFireEffMs, fireMsEff - 1);
                        _prevFireEffMs = fireMsEff;
                    }
                    if (rockMsEff != _prevRockEffMs)
                    {
                        _spawnAccumRockMs = (int)Math.Min(_spawnAccumRockMs * (double)rockMsEff / _prevRockEffMs, rockMsEff - 1);
                        _prevRockEffMs = rockMsEff;
                    }

                    // ===== 종류별 스폰 (틱당 최대 개수 제한) =====
                    if (knifeUnl)
                    {
                        int spawned = 0;
                        while (_spawnAccumKnifeMs >= knifeMsEff && spawned < MaxSpawnPerTickPerKind)
                        {
                            _spawnAccumKnifeMs -= knifeMsEff;
                            SpawnObstacleOfKind(ObKind.Knife);
                            spawned++;
                        }
                    }
                    if (fireUnl)
                    {
                        int spawned = 0;
                        while (_spawnAccumFireMs >= fireMsEff && spawned < MaxSpawnPerTickPerKind)
                        {
                            _spawnAccumFireMs -= fireMsEff;
                            SpawnObstacleOfKind(ObKind.Fire);
                            spawned++;
                        }
                    }
                    if (rockUnl)
                    {
                        int spawned = 0;
                        while (_spawnAccumRockMs >= rockMsEff && spawned < MaxSpawnPerTickPerKind)
                        {
                            _spawnAccumRockMs -= rockMsEff;
                            SpawnObstacleOfKind(ObKind.Rock);
                            spawned++;
                        }
                    }

                    // ===== 이전 해금 상태 갱신 =====
                    _prevKnifeUnlocked = knifeUnl;
                    _prevFireUnlocked = fireUnl;
                    _prevRockUnlocked = rockUnl;

                    // ===== 플레이어 물리/충돌 =====
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

                        // 플레이어 히트박스 (살짝 축소)
                        var pRectRaw = new RectangleF(p.X, p.Y, PlayerW, PlayerH);
                        var pHit = Rectangle.Round(DeflateAroundCenter(pRectRaw, 0.90f, 0.90f));

                        // 장애물 충돌
                        for (int i = 0; i < _obstacles.Count; i++)
                        {
                            var obRect = _obstacles[i];
                            float scale = 0.7f;
                            if (_obstacles[i].Kind == ObKind.Fire) scale = 0.5f;
                            var obHit = Rectangle.Round(DeflateAroundCenter(obRect.Rect, scale, scale));

                            if (pHit.IntersectsWith(obHit))
                            {
                                p.Alive = false;
                                break;
                            }
                        }
                    }

                    // 장애물 업데이트/제거 + 생존점수
                    UpdateObstacles(dt);

                    // 모두 사망 감지 → 투표 대기
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
                else
                {
                    // AwaitingRestart: 월드 업데이트 정지
                }
            }
        }

        void ApplyBounds(Player p)
        {
            float left = WorldMargin;
            float right = _worldW - WorldMargin - PlayerW;
            float groundY = _worldH - GroundMargin - PlayerH;

            if (p.X < left) p.X = left;
            if (p.X > right) p.X = right;

            if (p.Y >= groundY) { p.Y = groundY; p.VY = 0f; }
        }

        bool IsOnGround(Player p)
        {
            float groundY = _worldH - GroundMargin - PlayerH;
            return Math.Abs(p.Y - groundY) < 0.5f;
        }

        static RectangleF DeflateAroundCenter(RectangleF r, float scaleX, float scaleY)
        {
            if (scaleX < 0f) scaleX = 0f; if (scaleX > 1f) scaleX = 1f;
            if (scaleY < 0f) scaleY = 0f; if (scaleY > 1f) scaleY = 1f;

            float newW = r.Width * scaleX;
            float newH = r.Height * scaleY;
            float cx = r.X + r.Width / 2f;
            float cy = r.Y + r.Height / 2f;
            return new RectangleF(cx - newW / 2f, cy - newH / 2f, newW, newH);
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

        void RestartRound_Locked()
        {
            _respawnVotes.Clear();
            _phase = Phase.Countdown;          // 카운트다운 시작
            _countdownMsLeft = CountdownMs;

            _obstacles.Clear();

            // 종류별 스폰 누적/이전주기/해금상태 리셋
            _spawnAccumKnifeMs = 0;
            _spawnAccumFireMs = 0;
            _spawnAccumRockMs = 0;
            _prevKnifeEffMs = KnifeSpawnMs;
            _prevFireEffMs = FireSpawnMs;
            _prevRockEffMs = RockSpawnMs;
            _prevKnifeUnlocked = false;
            _prevFireUnlocked = false;
            _prevRockUnlocked = false;

            _tick = 0;

            _round += 1;
            _rng = new Random(_seed);   // 같은 패턴 유지. 새 패턴 원하면 _seed 대신 Environment.TickCount

            foreach (var kv in _players)
            {
                var p = kv.Value;
                p.Alive = true;
                p.VX = 0f;
                p.VY = 0f;

                float startX = (_worldW / 2f) - (PlayerW / 2f);
                float groundY = _worldH - GroundMargin - PlayerH;
                p.X = startX;
                p.Y = groundY;

                // 라운드마다 초기화
                p.Score = 0;
            }

            Console.WriteLine("[ROUND] Restarted -> Round " + _round);
            BroadcastSnapshot(); // 즉시 1회 전송해 UI 빠르게 갱신
        }

        void BroadcastSnapshot()
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("{\"cmd\":\"SNAPSHOT\",\"tick\":").Append(_tick).Append(",")
              .Append("\"round\":").Append(_round).Append(",")
              .Append("\"phase\":\"")
                 .Append(_phase == Phase.Playing ? "playing" :
                         _phase == Phase.AwaitingRestart ? "await" : "countdown")
              .Append("\",")
              .Append("\"countdown_ms\":").Append(_countdownMsLeft).Append(",")
              .Append("\"vote_count\":").Append(_respawnVotes.Count).Append(",")
              .Append("\"need_count\":").Append(_players.Count).Append(",");

            lock (_lock)
            {
                // players
                sb.Append("\"players\":[");
                bool first = true;
                foreach (var kv in _players)
                {
                    var p = kv.Value;
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"id\":\"").Append(p.Id).Append("\",")
                      .Append("\"name\":\"").Append(Escape(p.Name)).Append("\",")
                      .Append("\"x\":").Append(p.X.ToString(CultureInfo.InvariantCulture)).Append(",")
                      .Append("\"y\":").Append(p.Y.ToString(CultureInfo.InvariantCulture)).Append(",")
                      .Append("\"alive\":").Append(p.Alive ? "true" : "false").Append(",")
                      .Append("\"score\":").Append(p.Score).Append("}");
                }
                sb.Append("],");

                // obstacles (x,y,w,h,k)
                sb.Append("\"obstacles\":[");
                for (int i = 0; i < _obstacles.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var o = _obstacles[i];
                    sb.Append("{\"x\":").Append(o.Rect.X.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"y\":").Append(o.Rect.Y.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"w\":").Append(o.Rect.Width.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"h\":").Append(o.Rect.Height.ToString(CultureInfo.InvariantCulture))
                      .Append(",\"k\":").Append((int)o.Kind)
                      .Append("}");
                }
                sb.Append("]}");

                string json = sb.ToString();

                foreach (var kv in _players)
                {
                    try { Wire.SendJson(kv.Value.Ns, json); }
                    catch { /* 끊어진 연결은 Recv/Disconnect에서 정리 */ }
                }
            }
        }
    }
}
