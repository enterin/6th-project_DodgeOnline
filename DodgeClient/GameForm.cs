using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;  // 리플렉션으로 스냅샷 장애물 필드 접근
using System.Windows.Forms;

namespace DodgeBattleStarter
{
    public class GameForm : Form
    {
        // ====== 게임 설정 ======
        const int TargetFps = 60;
        const float Gravity = 1200f;
        const float MoveSpeed = 320f;
        const float JumpVel = 520f;
        const int WorldMargin = 24;
        const int GroundMargin = 84;
        const int ObstacleW = 24, ObstacleH = 24; // 서버 기본과 일치
        const int SpawnMs = 750;
        readonly SizeF PlayerSize = new SizeF(40, 40);

        // 스프라이트(방향별)
        Image _imgPlayerRightRaw, _imgPlayerLeftRaw;
        Image _imgPlayerRight, _imgPlayerLeft;

        // 장애물 스프라이트
        Image _imgFire_SwordRaw, _imgFire_Sword;
        Image _imgFireRaw1, _imgFire1;
        Image _imgFireRaw2, _imgFire2;
        Image _imgBoomRaw, _imgBoom;
        // 폭탄(boom) 4프레임 (애니메)
        Image[] _boomExplosionRaw = new Image[4];
        Image[] _boomExplosion = new Image[4];
        int _boomExplosionFrame = 0;        // 0~3
        int _boomExplosionAnimMsAccum = 0;  // 누적 ms
        const int BoomExplosionAnimMs = 90; // 프레임 전환 간격(ms)


        // 불 애니메이션
        int _fireFrame = 0;           // 0 or 1
        int _fireAnimMsAccum = 0;     // 누적 ms
        const int FireAnimMs = 120;   // 120ms마다 프레임 전환(취향대로 80~150 조절)

        // 로컬 플레이어 바라보는 방향 (기본: 오른쪽)
        bool _facingRight = true;

        // 온라인에서 다른 플레이어의 바라보는 방향 추정
        Dictionary<string, bool> _facingRightOnline = new Dictionary<string, bool>();
        Dictionary<string, RectangleF> _prevRectOnline = new Dictionary<string, RectangleF>();

        // ====== 타이밍/시드 ======
        readonly Stopwatch _sw = new Stopwatch();
        long _prevMs;
        int _spawnAccumMs;
        public int TickCount { get; private set; }
        public int Seed { get; private set; } = Environment.TickCount;
        Random _rng;

        // ====== 오프라인용 ======
        class Player
        {
            public string Id = "local";
            public Color Color = Color.DeepSkyBlue;
            public RectangleF Rect;
            public float vx, vy;
            public bool Left, Right, Up;
            public bool Alive = true;
            public int Score = 0;
        }
        Player _local = new Player();
        readonly Dictionary<string, Player> _remotes = new Dictionary<string, Player>();
        bool _botEnabled = false;

        readonly List<RectangleF> _obstacles = new List<RectangleF>();
        readonly Timer _timer = new Timer { Interval = 1000 / TargetFps };

        // ====== 온라인 모드 ======
        NetClient _net;
        bool _online = false;
        string _serverHost = "127.0.0.1";
        int _serverPort = 5055;
        string _nickname = "player1";

        // 온라인 장애물: Rect + Kind(k)
        struct OnlineOb
        {
            public RectangleF Rect;
            public int Kind; // 서버 ObKind (0=Knife, 1=Rock, 2=Fire)
            public OnlineOb(RectangleF r, int k) { Rect = r; Kind = k; }
        }
        List<OnlineOb> _obsOnline = new List<OnlineOb>();

        Dictionary<string, RectangleF> _playersOnline = new Dictionary<string, RectangleF>();
        HashSet<string> _aliveOnline = new HashSet<string>();
        Dictionary<string, int> _scoreOnline = new Dictionary<string, int>();

        public GameForm()
        {
            Text = "Dodge Battle (← →, ↑/Space: 점프 | R: 재시작/투표 | T: 봇 on/off | Esc: 종료)";
            ClientSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            KeyDown += OnKeyDown;
            KeyUp += OnKeyUp;

            TryConnectOnline();       // 온라인 접속 시도 (실패 시 오프라인)
            ResetGame();

            _timer.Tick += delegate { TickFrame(); };
            _sw.Start();
            _timer.Start();

            // ---- 플레이어 이미지 로드 ----
            try
            {
                _imgPlayerRightRaw = Image.FromFile("Assets/player_right.png");
                _imgPlayerLeftRaw = Image.FromFile("Assets/player_left.png");

                _imgPlayerRight = ScaleToKeepRatio(_imgPlayerRightRaw, Size.Round(PlayerSize));
                _imgPlayerLeft = ScaleToKeepRatio(_imgPlayerLeftRaw, Size.Round(PlayerSize));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("player sprite load fail: " + ex.Message);
            }

            // ---- 장애물 이미지 로드 ----
            try  //fire_sword
            {
                _imgFire_SwordRaw = Image.FromFile("Assets/fire_sword.png");
                // 세로 긴 느낌으로 스케일
                _imgFire_Sword = ScaleToKeepRatio(_imgFire_SwordRaw, new Size(20, 60));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("knife load fail: " + ex.Message);
            }

            try // boom
            {
                _imgBoomRaw = Image.FromFile("Assets/boom.png");
                _imgBoom = ScaleToKeepRatio(_imgBoomRaw, new Size(36, 36));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("boom sprite load fail: " + ex.Message);
            }

            try // fire 1, fire 2 (애니메이션)
            {
                _imgFireRaw1 = Image.FromFile("Assets/fire_1.png");
                _imgFireRaw2 = Image.FromFile("Assets/fire_2.png");

                // 🔸 2:3 직사각형 비율 (조금 넓게)
                _imgFire1 = ScaleToKeepRatio(_imgFireRaw1, new Size(30, 20));
                _imgFire2 = ScaleToKeepRatio(_imgFireRaw2, new Size(30, 20));   
            }
            catch (Exception ex)
            {
                Debug.WriteLine("fire sprites load fail: " + ex.Message);
            }

            try
            {
                _boomExplosionRaw[0] = Image.FromFile("Assets/explosion_1.png");
                _boomExplosionRaw[1] = Image.FromFile("Assets/explosion_2.png");
                _boomExplosionRaw[2] = Image.FromFile("Assets/explosion_3.png");
                _boomExplosionRaw[3] = Image.FromFile("Assets/explosion_4.png");

                for (int i = 0; i < 4; i++)
                    _boomExplosion[i] = ScaleToKeepRatio(_boomExplosionRaw[i], new Size(36, 36));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("boom frames load fail: " + ex.Message);
            }
        }

        // ================= 유틸: 이미지 스케일(비율 유지) =================
        static Image ScaleToKeepRatio(Image src, Size dst)
        {
            if (src == null) return null;
            float scale = Math.Min(dst.Width / (float)src.Width, dst.Height / (float)src.Height);
            int w = Math.Max(1, (int)(src.Width * scale));
            int h = Math.Max(1, (int)(src.Height * scale));

            var bmp = new Bitmap(dst.Width, dst.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor; // 픽셀 느낌 유지
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                g.DrawImage(src, (dst.Width - w) / 2, (dst.Height - h) / 2, w, h);
            }
            return bmp;
        }

        void TryConnectOnline()
        {
            _net = new NetClient();
            if (_net.Connect(_serverHost, _serverPort, _nickname))
            {
                _online = true;
                Text += "  [ONLINE]";
                _botEnabled = false;
            }
            else
            {
                _online = false;
            }
        }

        void TickFrame()
        {
            long now = _sw.ElapsedMilliseconds;
            float dt = (now - _prevMs) / 1000f;
            if (dt <= 0) return;
            _prevMs = now;

            float fixedDt = 1f / TargetFps;
            float acc = dt;
            while (acc > 0f)
            {
                float step = (acc > fixedDt) ? fixedDt : acc;
                Step(step);
                acc -= step;
            }
            Invalidate();
        }

        // =============== 메인 업데이트 ===============
        void Step(float dt)
        {
            // ---- 폭탄 애니메 프레임 업데이트 ----
            _boomExplosionAnimMsAccum += (int)(dt * 1000f);
            while (_boomExplosionAnimMsAccum >= BoomExplosionAnimMs)
            {
                _boomExplosionAnimMsAccum -= BoomExplosionAnimMs;
                _boomExplosionFrame = (_boomExplosionFrame + 1) & 3;  // 0→1→2→3→0
            }

            // ---- 불 애니메 프레임 업데이트 ----
            _fireAnimMsAccum += (int)(dt * 1000f);
            while (_fireAnimMsAccum >= FireAnimMs)
            {
                _fireAnimMsAccum -= FireAnimMs;
                _fireFrame ^= 1; // 0<->1 토글
            }

            // ====== 온라인 게임 로직 ======
            if (_online && _net != null)
            {
                var snap = _net.TryGetSnapshot();
                if (snap != null)
                {
                    _obsOnline.Clear();
                    for (int i = 0; i < snap.Obstacles.Count; i++)
                    {
                        var ob = snap.Obstacles[i];
                        _obsOnline.Add(new OnlineOb(new RectangleF(ob.X, ob.Y, ob.W, ob.H), ob.K));
                    }

                    // ★ 디버그: 첫 장애물 Y 출력
                    if (_obsOnline.Count > 0)
                        Debug.WriteLine($"[CLIENT] firstY={_obsOnline[0].Rect.Y:F1}  count={_obsOnline.Count}");

                    // ---- 온라인 플레이어, 생존, 점수 ----
                    _playersOnline.Clear();
                    _aliveOnline.Clear();
                    _scoreOnline.Clear();
                    for (int i = 0; i < snap.Players.Count; i++)
                    {
                        var pl = snap.Players[i];
                        _playersOnline[pl.Id] = new RectangleF(pl.X, pl.Y, PlayerSize.Width, PlayerSize.Height);
                        if (pl.Alive) _aliveOnline.Add(pl.Id);
                        _scoreOnline[pl.Id] = pl.Score;
                    }
                }
                return; // 온라인은 서버 스냅샷만 렌더
            }

            // ===== 오프라인 게임 로직 =====
            TickCount++;

            if (_botEnabled) RunBotLogic(dt);

            _local.vx = 0f;
            if (_local.Left) _local.vx -= MoveSpeed;
            if (_local.Right) _local.vx += MoveSpeed;
            if (_local.Up && IsOnGround(_local)) _local.vy = -JumpVel;

            _local.vy += Gravity * dt;
            _local.Rect = new RectangleF(_local.Rect.X + _local.vx * dt,
                                         _local.Rect.Y + _local.vy * dt,
                                         _local.Rect.Width, _local.Rect.Height);

            ApplyBounds(_local);

            _spawnAccumMs += (int)(dt * 1000);
            while (_spawnAccumMs >= SpawnMs)
            {
                _spawnAccumMs -= SpawnMs;
                SpawnObstacle();
            }

            for (int i = _obstacles.Count - 1; i >= 0; i--)
            {
                RectangleF r = _obstacles[i];
                r.Y += 320f * dt;
                _obstacles[i] = r;

                if (r.Top > ClientSize.Height + 8)
                {
                    _obstacles.RemoveAt(i);
                    if (_local.Alive) _local.Score += 5;
                }
            }

            if (_local.Alive)
            {
                // 히트박스: 칼을 조금 관대하게(가로/세로 축소), 플레이어는 그대로
                Rectangle p = Rectangle.Round(_local.Rect);
                for (int i = 0; i < _obstacles.Count; i++)
                {
                    var hb = Rectangle.Round(DeflateAroundCenter(_obstacles[i], 0.7f, 0.6f));
                    if (p.IntersectsWith(hb))
                    {
                        _local.Alive = false;
                        break;
                    }
                }
            }
        }

        // 가운데 기준 축소 유틸(클라 오프라인 판정용)
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

        // =============== 온라인 스냅샷 리플렉션 유틸 ===============
        static float GetFloatMember(Type t, object o, string name, float defVal)
        {
            // 1) 딕셔너리 먼저
            object dv;
            if (TryGetFromDict(o, name, out dv))
            {
                try { return Convert.ToSingle(dv, System.Globalization.CultureInfo.InvariantCulture); } catch { }
            }

            // 2) 프로퍼티/필드 (대/소문자 모두 시도)
            string[] names = { name, name.ToLowerInvariant() };
            foreach (var nm in names)
            {
                var pi = t.GetProperty(nm, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (pi != null)
                {
                    object v = pi.GetValue(o, null);
                    if (v is IConvertible)
                        try { return Convert.ToSingle(v, System.Globalization.CultureInfo.InvariantCulture); } catch { }
                }
                var fi = t.GetField(nm, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (fi != null)
                {
                    object v = fi.GetValue(o);
                    if (v is IConvertible)
                        try { return Convert.ToSingle(v, System.Globalization.CultureInfo.InvariantCulture); } catch { }
                }
            }
            return defVal;
        }

        static bool TryGetFromDict(object o, string key, out object val)
        {
            val = null;
            // 비제네릭 IDictionary
            var dict = o as System.Collections.IDictionary;
            if (dict != null)
            {
                if (dict.Contains(key)) { val = dict[key]; return true; }
                // 키 대소문자 변형도 시도
                string lk = key.ToLowerInvariant(), uk = key.ToUpperInvariant();
                foreach (var k in dict.Keys)
                {
                    if (k is string ks &&
                        (ks == key || ks == lk || ks == uk))
                    { val = dict[k]; return true; }
                }
                return false;
            }

            // 제네릭 IDictionary<string, object>
            var gen = o as IDictionary<string, object>;
            if (gen != null)
            {
                object tmp;
                if (gen.TryGetValue(key, out tmp)) { val = tmp; return true; }
                if (gen.TryGetValue(key.ToLowerInvariant(), out tmp)) { val = tmp; return true; }
                if (gen.TryGetValue(key.ToUpperInvariant(), out tmp)) { val = tmp; return true; }
            }
            return false;
        }

        static int GetIntMember(Type t, object o, string name, int defVal)
        {
            // 1) 딕셔너리 먼저
            object dv;
            if (TryGetFromDict(o, name, out dv))
            {
                try { return Convert.ToInt32(dv, System.Globalization.CultureInfo.InvariantCulture); } catch { }
            }

            // 2) 프로퍼티/필드 (대/소문자 모두 시도)
            string[] names = { name, name.ToLowerInvariant() };
            foreach (var nm in names)
            {
                var pi = t.GetProperty(nm, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (pi != null)
                {
                    object v = pi.GetValue(o, null);
                    if (v is IConvertible)
                        try { return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture); } catch { }
                }
                var fi = t.GetField(nm, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (fi != null)
                {
                    object v = fi.GetValue(o);
                    if (v is IConvertible)
                        try { return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture); } catch { }
                }
            }
            return defVal;
        }

        // =============== 경계/땅/스폰 ===============
        void ApplyBounds(Player p)
        {
            float left = WorldMargin;
            float right = ClientSize.Width - WorldMargin - p.Rect.Width;
            float groundY = ClientSize.Height - GroundMargin - p.Rect.Height;

            if (p.Rect.X < left) p.Rect = new RectangleF(left, p.Rect.Y, p.Rect.Width, p.Rect.Height);
            if (p.Rect.X > right) p.Rect = new RectangleF(right, p.Rect.Y, p.Rect.Width, p.Rect.Height);

            if (p.Rect.Y >= groundY)
            {
                p.Rect = new RectangleF(p.Rect.X, groundY, p.Rect.Width, p.Rect.Height);
                p.vy = 0f;
            }
        }
        bool IsOnGround(Player p)
        {
            float groundY = ClientSize.Height - GroundMargin - p.Rect.Height;
            return Math.Abs(p.Rect.Y - groundY) < 0.5f;
        }
        void SpawnObstacle()
        {
            int x = _rng.Next(WorldMargin, ClientSize.Width - WorldMargin - ObstacleW);
            _obstacles.Add(new RectangleF(x, -ObstacleH, ObstacleW, ObstacleH));
        }

        // =============== 입력 처리 ===============
        void OnKeyDown(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.A) { _local.Left = true; _facingRight = false; }
            if (e.KeyCode == Keys.Right || e.KeyCode == Keys.D) { _local.Right = true; _facingRight = true; }
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.W || e.KeyCode == Keys.Space) _local.Up = true;

            if (_online && _net != null)
                _net.SendInput(_local.Left, _local.Right, _local.Up);

            if (e.KeyCode == Keys.R)
            {
                if (_online && _net != null)
                    _net.SendRespawn();  // 투표
                else
                    ResetGame();         // 오프라인 리셋
            }

            if (e.KeyCode == Keys.Escape) Close();
            if (e.KeyCode == Keys.T && !_online) _botEnabled = !_botEnabled;
        }

        void OnKeyUp(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.A) _local.Left = false;
            if (e.KeyCode == Keys.Right || e.KeyCode == Keys.D) _local.Right = false;
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.W || e.KeyCode == Keys.Space) _local.Up = false;

            if (_online && _net != null)
                _net.SendInput(_local.Left, _local.Right, _local.Up);
        }

        void ResetGame()
        {
            _rng = new Random(Seed);
            TickCount = 0;
            _prevMs = _sw.ElapsedMilliseconds;
            _spawnAccumMs = 0;
            _obstacles.Clear();

            _local = new Player
            {
                Id = "local",
                Color = Color.DeepSkyBlue,
                Rect = new RectangleF(
                    ClientSize.Width / 2f - PlayerSize.Width / 2f,
                    ClientSize.Height - GroundMargin - PlayerSize.Height,
                    PlayerSize.Width, PlayerSize.Height),
                Alive = true,
                Score = 0
            };
            _remotes.Clear();
        }

        // =============== 플레이어 렌더 ===============
        void DrawPlayerSprite(Graphics g, RectangleF rect, bool alive, bool highlight, bool facingRight)
        {
            var r = Rectangle.Round(rect);
            Image img = facingRight ? _imgPlayerRight : _imgPlayerLeft;

            if (img != null)
            {
                g.DrawImage(img, r);

                if (!alive)
                {
                    using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                    {
                        g.FillRectangle(dim, r);
                    }
                }
            }
            else
            {
                using (var br = new SolidBrush(alive ? Color.DeepSkyBlue : Color.Gray))
                {
                    g.FillRectangle(br, r);
                }
            }
        }

        // =============== 그리기 ===============
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.FromArgb(22, 24, 28));

            using (var border = new Pen(Color.DimGray, 2))
            {
                g.DrawRectangle(border,
                    WorldMargin, WorldMargin,
                    ClientSize.Width - WorldMargin * 2,
                    ClientSize.Height - GroundMargin - WorldMargin);
            }

            if (_online)
            {
                // ---- 온라인 장애물 렌더 (종류별로 개별 판단) ----
                for (int i = 0; i < _obsOnline.Count; i++)
                {
                    var o = _obsOnline[i];
                    var r = Rectangle.Round(o.Rect);

                    if (o.Kind == 0) // Knife
                    {
                        if (_imgFire_Sword != null)
                        {
                            float w = r.Width * 0.8f, h = r.Height * 2.5f;
                            float x = r.X + (r.Width - w) / 2f;
                            float y = r.Y - (h - r.Height) / 2f;
                            g.DrawImage(_imgFire_Sword, x, y, w, h);
                        }
                        else
                        {
                            using (var b = new SolidBrush(Color.OrangeRed)) g.FillRectangle(b, r);
                        }
                    }
                    else if (o.Kind == 1) // Boom (폭탄)
                    {
                        var rectB = Rectangle.Round(o.Rect);

                        // 단일 폭탄 아이콘 사용
                        Image bombIcon = _imgBoom;
                        if (bombIcon != null)
                        {
                            // 살짝 크게 (취향에 맞게)
                            float w = rectB.Width * 1.4f, h = rectB.Height * 1.4f;
                            float x = rectB.X + (rectB.Width - w) / 2f;
                            float y = rectB.Y + (rectB.Height - h) / 2f;
                            g.DrawImage(bombIcon, x, y, w, h);
                        }
                        else
                        {
                            using (var b = new SolidBrush(Color.DarkSlateGray))
                                g.FillEllipse(b, rectB);
                        }
                    }

                    else if (o.Kind == 2) // Fire
                    {
                        Image fireImg = (_fireFrame == 0 ? _imgFire1 : _imgFire2);
                        if (fireImg != null)
                        {
                            float w = r.Width * 1.5f, h = r.Height;    // 2:3 느낌
                            float x = r.X + (r.Width - w) / 2f, y = r.Y;
                            g.DrawImage(fireImg, x, y, w, h);
                        }
                        else
                        {
                            using (var b = new SolidBrush(Color.Lime)) g.FillRectangle(b, r); // 이미지 실패시 눈에 띄게
                        }
                    }
                    else if (o.Kind == 3) // Explosion(넉백 이펙트)
                    {
                        var rectB = Rectangle.Round(o.Rect);

                        Image boomFx = (_boomExplosion != null && _boomExplosion[_boomExplosionFrame] != null)
                                       ? _boomExplosion[_boomExplosionFrame]
                                       : null;

                        if (boomFx != null)
                        {
                            float w = rectB.Width * 1.8f, h = rectB.Height * 1.8f;
                            float x = rectB.X + (rectB.Width - w) / 2f;
                            float y = rectB.Y + (rectB.Height - h) / 2f;
                            g.DrawImage(boomFx, x, y, w, h);
                        }
                        else
                        {
                            using (var b = new SolidBrush(Color.OrangeRed))
                                g.FillEllipse(b, rectB);
                        }
                    }
                    else 
                    {
                        using (var b = new SolidBrush(Color.OrangeRed)) g.FillRectangle(b, r);
                    }

                }


                // ---- 온라인 플레이어 렌더 ----
                foreach (var kv in _playersOnline)
                {
                    var id = kv.Key;
                    var rect = kv.Value;

                    bool faceRight = true;
                    bool prevKnown = _prevRectOnline.TryGetValue(id, out var prevRect);

                    if (prevKnown)
                    {
                        float dx = rect.X - prevRect.X;
                        if (Math.Abs(dx) > 0.5f)
                            faceRight = dx >= 0;
                        else if (_facingRightOnline.TryGetValue(id, out var prevDir))
                            faceRight = prevDir;
                    }

                    _prevRectOnline[id] = rect;
                    _facingRightOnline[id] = faceRight;

                    bool alive = _aliveOnline.Contains(id);
                    bool me = (_net != null && id == _net.MyId);
                    if (me) faceRight = _facingRight;

                    DrawPlayerSprite(e.Graphics, rect, alive, highlight: me, facingRight: faceRight);
                }

                // ---- 상단 작은 텍스트 + 라운드/스코어보드/HUD ----
                using (var white = new SolidBrush(Color.White))
                using (var font = new Font("Segoe UI", 10))
                {
                    var snapForHud = _net.TryGetSnapshot();
                    int netTick = (snapForHud != null) ? snapForHud.Tick : -1;

                    // ★ 첫 장애물 Y 디버그(스냅샷 기반)
                    float firstY = -9999f;
                    if (_obsOnline.Count > 0) firstY = _obsOnline[0].Rect.Y;

                    g.DrawString($"ONLINE  tick={netTick}  firstY={firstY:F1}", font, white, 12, 12);

                    if (snapForHud != null)
                    {
                        using (var panelBg = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
                        using (var gray = new SolidBrush(Color.Gainsboro))
                        using (var titleFont = new Font("Segoe UI", 16, FontStyle.Bold))
                        using (var smallFont = new Font("Segoe UI", 10, FontStyle.Regular))
                        using (var headFont = new Font("Segoe UI", 11, FontStyle.Bold))
                        {
                            // 1) 라운드
                            string roundText = "ROUND " + (snapForHud.Round > 0 ? snapForHud.Round : 1);
                            SizeF roundSz = e.Graphics.MeasureString(roundText, titleFont);
                            float roundX = (ClientSize.Width - roundSz.Width) / 2f;
                            float roundY = 8f;

                            RectangleF roundPanel = new RectangleF(roundX - 12, roundY - 6, roundSz.Width + 24, roundSz.Height + 12);
                            e.Graphics.FillRectangle(panelBg, roundPanel);
                            e.Graphics.DrawString(roundText, titleFont, Brushes.White, roundX, roundY);

                            // 2) 스코어보드
                            var sorted = new List<NetPlayer>(snapForHud.Players);
                            sorted.Sort(delegate (NetPlayer a, NetPlayer b)
                            {
                                int cmp = b.Score.CompareTo(a.Score);
                                if (cmp != 0) return cmp;
                                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                            });

                            int show = Math.Min(6, sorted.Count);
                            float sbWidth = 220f;
                            float sbRowH = 15f;
                            float sbHeadH = 28f;
                            float sbX = ClientSize.Width - sbWidth - 12;
                            float sbY = 8f;

                            RectangleF sbRect = new RectangleF(sbX, sbY, sbWidth, sbHeadH + show * sbRowH + 12);
                            e.Graphics.FillRectangle(panelBg, sbRect);

                            e.Graphics.DrawString("SCORE BOARD", headFont, Brushes.White, sbX + 10, sbY + 6);

                            float colNameX = sbX + 10;
                            float colScoreX = sbX + sbWidth - 60;
                            e.Graphics.DrawString("Player", smallFont, gray, colNameX, sbY + sbHeadH + 2);
                            e.Graphics.DrawString("Score", smallFont, gray, colScoreX, sbY + sbHeadH + 2);

                            float rowStartY = sbY + sbHeadH + 16f;
                            for (int i = 0; i < show; i++)
                            {
                                var p = sorted[i];
                                float rowY = rowStartY + (i * sbRowH);

                                Color bgColor =
                                    (i == 0) ? Color.FromArgb(60, 255, 215, 0) :
                                    (i == 1) ? Color.FromArgb(50, 192, 192, 192) :
                                    (i == 2) ? Color.FromArgb(50, 205, 127, 50) :
                                               Color.FromArgb(0, 0, 0, 0);
                                if (bgColor.A > 0)
                                {
                                    using (var rankBg = new SolidBrush(bgColor))
                                        e.Graphics.FillRectangle(rankBg, new RectangleF(sbX + 4, rowY - 2, sbWidth - 8, sbRowH + 2));
                                }

                                bool me = (_net != null && p.Id == _net.MyId);
                                Brush rowBrush = me
                                    ? (Brush)new SolidBrush(Color.LightSkyBlue)
                                    : (p.Alive ? (Brush)new SolidBrush(Color.White) : (Brush)new SolidBrush(Color.Gray));

                                string name = string.IsNullOrEmpty(p.Name) ? p.Id : p.Name;
                                if (name.Length > 12) name = name.Substring(0, 12) + "…";

                                float badgeW = 18f;
                                float badgeH = sbRowH + 2;
                                float badgeX = sbX + 6;
                                float badgeY = rowY - 2;

                                using (var badgeBrush = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                                using (var badgePen = new Pen(Color.DimGray, 1))
                                using (var rankFont = new Font("Segoe UI", 9, FontStyle.Bold))
                                {
                                    e.Graphics.FillRectangle(badgeBrush, badgeX, badgeY, badgeW, badgeH);
                                    e.Graphics.DrawRectangle(badgePen, badgeX, badgeY, badgeW, badgeH);

                                    string rankStr = (i + 1).ToString();
                                    var sz = e.Graphics.MeasureString(rankStr, rankFont);
                                    e.Graphics.DrawString(rankStr, rankFont, Brushes.White,
                                        badgeX + (badgeW - sz.Width) / 2f,
                                        badgeY + (badgeH - sz.Height) / 2f);
                                }

                                float namePad = 12f;
                                float nameX = badgeX + badgeW + namePad;
                                float scoreX = colScoreX;

                                using (rowBrush)
                                using (var textFont = new Font("Segoe UI", 10, me ? FontStyle.Bold : FontStyle.Regular))
                                {
                                    e.Graphics.DrawString(name, textFont, rowBrush, nameX, rowY);
                                    e.Graphics.DrawString(p.Score.ToString(), textFont, rowBrush, scoreX, rowY);
                                }
                            }

                            // 3) 중앙 오버레이(카운트다운/투표)
                            if (snapForHud.Phase == "countdown")
                            {
                                int sec = (snapForHud.CountdownMs + 999) / 1000;
                                string msg = (sec > 0) ? sec.ToString() : "START!";
                                using (var big = new Font("Segoe UI", 28, FontStyle.Bold))
                                {
                                    SizeF sz = e.Graphics.MeasureString(msg, big);
                                    RectangleF mid = new RectangleF(
                                        (ClientSize.Width - sz.Width) / 2f - 20,
                                        (ClientSize.Height - sz.Height) / 2f - 12,
                                        sz.Width + 40, sz.Height + 24);
                                    e.Graphics.FillRectangle(panelBg, mid);
                                    e.Graphics.DrawString(msg, big, Brushes.White,
                                        (ClientSize.Width - sz.Width) / 2f,
                                        (ClientSize.Height - sz.Height) / 2f);
                                }
                            }
                            else if (snapForHud.Phase == "await")
                            {
                                using (var big = new Font("Segoe UI", 18, FontStyle.Bold))
                                {
                                    string msg = string.Format("ROUND OVER - Press R to restart  ({0}/{1})",
                                                               snapForHud.VoteCount, snapForHud.NeedCount);
                                    SizeF sz = e.Graphics.MeasureString(msg, big);
                                    RectangleF midPanel = new RectangleF(
                                        (ClientSize.Width - sz.Width) / 2f - 16,
                                        (ClientSize.Height - sz.Height) / 2f - 10,
                                        sz.Width + 32, sz.Height + 20);
                                    e.Graphics.FillRectangle(panelBg, midPanel);
                                    e.Graphics.DrawString(msg, big, Brushes.White,
                                        (ClientSize.Width - sz.Width) / 2f,
                                        (ClientSize.Height - sz.Height) / 2f);
                                }
                            }
                        }
                    }
                }

                return; // 온라인 렌더 끝
            }

            // ====== 오프라인 렌더링 ======
            // 장애물: 칼 이미지로 세로 길게, 폴백은 사각형
            if (_imgFire_Sword != null)
            {
                foreach (var obs in _obstacles)
                {
                    var r = Rectangle.Round(obs);
                    float w = r.Width * 0.8f;
                    float h = r.Height * 2.5f;  // 세로 길게
                    float x = r.X + (r.Width - w) / 2f;
                    float y = r.Y - (h - r.Height) / 2f;
                    g.DrawImage(_imgFire_Sword, x, y, w, h);
                }
            }
            else
            {
                using (var obs2 = new SolidBrush(Color.OrangeRed))
                {
                    for (int i = 0; i < _obstacles.Count; i++)
                        g.FillRectangle(obs2, Rectangle.Round(_obstacles[i]));
                }
            }

            // 플레이어
            DrawPlayerSprite(e.Graphics, _local.Rect, _local.Alive, highlight: true, facingRight: _facingRight);
            foreach (var kv in _remotes)
            {
                var p = kv.Value;
                bool facingRight = p.vx >= 0;
                DrawPlayerSprite(e.Graphics, p.Rect, p.Alive, highlight: false, facingRight: facingRight);
            }

            // 간단 HUD(오프라인)
            using (var white2 = new SolidBrush(Color.White))
            using (var font2 = new Font("Segoe UI", 10, FontStyle.Regular))
            {
                g.DrawString("Score: " + _local.Score + "   " + (_local.Alive ? "ALIVE" : "DEAD"),
                             font2, white2, 12, 12);
                g.DrawString(string.Format("Seed:{0} Tick:{1}  (T: 봇 {2})",
                             Seed, TickCount, _botEnabled ? "ON" : "OFF"),
                             font2, white2, 12, 30);
            }

            if (!_local.Alive)
            {
                using (var white3 = new SolidBrush(Color.White))
                using (var big = new Font("Segoe UI", 24, FontStyle.Bold))
                {
                    string s = "GAME OVER  (R: 재시작)";
                    SizeF sz = e.Graphics.MeasureString(s, big);
                    g.DrawString(s, big, white3,
                        (ClientSize.Width - sz.Width) / 2f,
                        (ClientSize.Height - sz.Height) / 2f);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _imgPlayerRight?.Dispose();
                _imgPlayerLeft?.Dispose();
                _imgPlayerRightRaw?.Dispose();
                _imgPlayerLeftRaw?.Dispose();

                _imgFire_Sword?.Dispose();
                _imgFire_SwordRaw?.Dispose();

                _imgFire1?.Dispose();
                _imgFire2?.Dispose();
                _imgFireRaw1?.Dispose();
                _imgFireRaw2?.Dispose();
                // 폭탄 단일 이미지(사용 중이면)
                _imgBoom?.Dispose();
                _imgBoomRaw?.Dispose();

                // 폭탄 4프레임
                for (int i = 0; i < 4; i++)
                {
                    _boomExplosion[i]?.Dispose();
                    _boomExplosionRaw[i]?.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        void DrawPlayer(Graphics g, Player p)
        {
            using (var br = new SolidBrush(p.Alive ? p.Color : Color.Gray))
                g.FillRectangle(br, Rectangle.Round(p.Rect));
        }

        // ── 오프라인 테스트용 봇 ──
        void RunBotLogic(float dt)
        {
            Player bot;
            if (!_remotes.TryGetValue("bot1", out bot))
            {
                bot = new Player
                {
                    Id = "bot1",
                    Color = Color.MediumSpringGreen,
                    Rect = new RectangleF(
                        ClientSize.Width * 0.25f - PlayerSize.Width / 2f,
                        ClientSize.Height - GroundMargin - PlayerSize.Height,
                        PlayerSize.Width, PlayerSize.Height),
                    Alive = true
                };
                _remotes["bot1"] = bot;
            }
            if (!bot.Alive) return;

            RectangleF target = RectangleF.Empty;
            for (int i = 0; i < _obstacles.Count; i++)
            {
                var o = _obstacles[i];
                if (o.Y < bot.Rect.Y)
                {
                    if (target == RectangleF.Empty || o.Y > target.Y) target = o;
                }
            }

            float safeX = ClientSize.Width / 2f;
            if (target != RectangleF.Empty)
            {
                safeX = (target.X < ClientSize.Width / 2f)
                    ? ClientSize.Width - WorldMargin - bot.Rect.Width
                    : WorldMargin;
            }

            float dir = Math.Sign(safeX - bot.Rect.X);
            bot.vx = dir * (MoveSpeed * 0.8f);
            bot.vy += Gravity * dt;

            if (NeedJump(bot)) bot.vy = -JumpVel * 0.9f;

            bot.Rect = new RectangleF(bot.Rect.X + bot.vx * dt,
                                      bot.Rect.Y + bot.vy * dt,
                                      bot.Rect.Width, bot.Rect.Height);
            ApplyBounds(bot);

            for (int i = 0; i < _obstacles.Count; i++)
            {
                if (Rectangle.Round(_obstacles[i]).IntersectsWith(Rectangle.Round(bot.Rect)))
                {
                    bot.Alive = false;
                    break;
                }
            }
        }

        bool NeedJump(Player p)
        {
            for (int i = 0; i < _obstacles.Count; i++)
            {
                RectangleF o = _obstacles[i];
                bool closeX = Math.Abs((o.X + o.Width / 2f) - (p.Rect.X + p.Rect.Width / 2f)) < (p.Rect.Width * 0.8f);
                bool above = o.Y < p.Rect.Y && (p.Rect.Y - o.Y) < 140;
                if (closeX && above && IsOnGround(p)) return true;
            }
            return false;
        }
    }
}
