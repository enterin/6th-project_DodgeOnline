using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
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
        const int ObstacleW = 24, ObstacleH = 24;
        const int SpawnMs = 750;
        readonly SizeF PlayerSize = new SizeF(40, 40);

        // 스프라이트(방향별)
        Image _imgPlayerRightRaw, _imgPlayerLeftRaw;
        Image _imgPlayerRight, _imgPlayerLeft;
        //장애물 스프라이트
        Image _imgFire_SwordRaw, _imgFire_Sword;

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

        List<RectangleF> _obsOnline = new List<RectangleF>();
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

            try
            {
                _imgPlayerRightRaw = Image.FromFile("Assets/player_right.png");
                _imgPlayerLeftRaw = Image.FromFile("Assets/player_left.png");

                // 픽셀 아트 보존: 비율 유지 + 중앙 배치 스케일
                _imgPlayerRight = ScaleToKeepRatio(_imgPlayerRightRaw, Size.Round(PlayerSize));
                _imgPlayerLeft = ScaleToKeepRatio(_imgPlayerLeftRaw, Size.Round(PlayerSize));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("player sprite load fail: " + ex.Message);
            }

            try
            {
                _imgFire_SwordRaw = Image.FromFile("Assets/fire_sword.png");

                // 세로 긴 느낌: 20x60 픽셀로 리사이즈
                _imgFire_Sword = ScaleToKeepRatio(_imgFire_SwordRaw, new Size(20, 60));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("knife load fail: " + ex.Message);
            }

        }
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

        // 가운데를 기준으로 가로/세로 비율만큼 축소된 사각형 반환
        static RectangleF DeflateAroundCenter(RectangleF r, float scaleX, float scaleY)
        {
            // 0~1 범위로 제한
            if (scaleX < 0f) scaleX = 0f; if (scaleX > 1f) scaleX = 1f;
            if (scaleY < 0f) scaleY = 0f; if (scaleY > 1f) scaleY = 1f;

            float newW = r.Width * scaleX;
            float newH = r.Height * scaleY;
            float cx = r.X + r.Width / 2f;
            float cy = r.Y + r.Height / 2f;
            return new RectangleF(cx - newW / 2f, cy - newH / 2f, newW, newH);
        }

        void Step(float dt)
        {
            if (_online && _net != null)
            {
                var snap = _net.TryGetSnapshot();
                if (snap != null)
                {
                    _obsOnline.Clear();
                    for (int i = 0; i < snap.Obstacles.Count; i++)
                    {
                        var p = snap.Obstacles[i];
                        _obsOnline.Add(new RectangleF(p.X, p.Y, ObstacleW, ObstacleH));
                    }

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
                Rectangle p = Rectangle.Round(_local.Rect);
                for (int i = 0; i < _obstacles.Count; i++)
                {
                    // ★ 칼(장애물) 히트박스 축소: 가로 70%, 세로 60%
                    var hb = Rectangle.Round(DeflateAroundCenter(_obstacles[i], 0.7f, 0.7f));
                    if (p.IntersectsWith(hb))
                    {
                        _local.Alive = false;
                        break;
                    }
                }
            }
        }

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
                if (_imgFire_Sword != null)
                {
                    for (int i = 0; i < _obsOnline.Count; i++)
                    {
                        var r = Rectangle.Round(_obsOnline[i]);

                        // 세로로 긴 칼 느낌 (가로 0.8배, 세로 2.5배)
                        float w = r.Width * 0.8f;
                        float h = r.Height * 2.5f;
                        float x = r.X + (r.Width - w) / 2f;
                        float y = r.Y - (h - r.Height) / 2f;

                        g.DrawImage(_imgFire_Sword, x, y, w, h);
                    }
                }
                else
                {
                    using (var obs = new SolidBrush(Color.OrangeRed))
                    {
                        for (int i = 0; i < _obsOnline.Count; i++)
                            g.FillRectangle(obs, Rectangle.Round(_obsOnline[i]));
                    }
                }

                foreach (var kv in _playersOnline)
                {
                    var id = kv.Key;
                    var rect = kv.Value;

                    // 기본값 유지
                    bool faceRight = true;
                    bool prevKnown = _prevRectOnline.TryGetValue(id, out var prevRect);

                    if (prevKnown)
                    {
                        float dx = rect.X - prevRect.X;
                        if (Math.Abs(dx) > 0.5f) // 작은 흔들림 무시
                            faceRight = dx >= 0;
                        else if (_facingRightOnline.TryGetValue(id, out var prevDir))
                            faceRight = prevDir; // 정지 중엔 이전 방향 유지
                    }

                    _prevRectOnline[id] = rect;
                    _facingRightOnline[id] = faceRight;

                    bool alive = _aliveOnline.Contains(id);
                    bool me = (_net != null && id == _net.MyId);

                    // 내 캐릭터는 로컬 입력 기준으로 확정
                    if (me) faceRight = _facingRight;

                    DrawPlayerSprite(e.Graphics, rect, alive, highlight: me, facingRight: faceRight);
                }


                using (var white = new SolidBrush(Color.White))
                using (var font = new Font("Segoe UI", 10))
                {
                    int myScore = (_net != null && _scoreOnline.ContainsKey(_net.MyId)) ? _scoreOnline[_net.MyId] : 0;
                    g.DrawString("ONLINE", font, white, 12, 12);

                    var snap = _net.TryGetSnapshot();
                    if (snap != null && snap.Phase == "await")
                    {
                        using (var big = new Font("Segoe UI", 18, FontStyle.Bold))
                        {
                            string msg = string.Format("ROUND OVER - Press R to restart  ({0}/{1})",
                                                       snap.VoteCount, snap.NeedCount);
                            SizeF sz = g.MeasureString(msg, big);
                            g.DrawString(msg, big, white,
                                (ClientSize.Width - sz.Width) / 2f,
                                (ClientSize.Height - sz.Height) / 2f);
                        }
                    }
                }

                // ----- HUD: ROUND / SCOREBOARD -----
                var snapForHud = _net.TryGetSnapshot();
                if (snapForHud != null)
                {
                    using (var panelBg = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
                    using (var white = new SolidBrush(Color.White))
                    using (var gray = new SolidBrush(Color.Gainsboro))
                    using (var titleFont = new Font("Segoe UI", 16, FontStyle.Bold))
                    using (var smallFont = new Font("Segoe UI", 10, FontStyle.Regular))
                    using (var headFont = new Font("Segoe UI", 11, FontStyle.Bold))
                    {
                        // 1) 상단 중앙: ROUND
                        string roundText = "ROUND " + (snapForHud.Round > 0 ? snapForHud.Round : 1);
                        SizeF roundSz = e.Graphics.MeasureString(roundText, titleFont);
                        float roundX = (ClientSize.Width - roundSz.Width) / 2f;
                        float roundY = 8f;

                        RectangleF roundPanel = new RectangleF(roundX - 12, roundY - 6, roundSz.Width + 24, roundSz.Height + 12);
                        e.Graphics.FillRectangle(panelBg, roundPanel);
                        e.Graphics.DrawString(roundText, titleFont, white, roundX, roundY);

                        // 2) 우측 상단: SCOREBOARD
                        var sorted = new List<NetPlayer>(snapForHud.Players);
                        sorted.Sort(delegate (NetPlayer a, NetPlayer b)
                        {
                            int cmp = b.Score.CompareTo(a.Score);
                            if (cmp != 0) return cmp;
                            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                        });

                        int show = Math.Min(6, sorted.Count);
                        float sbWidth = 220f;
                        float sbRowH = 15f;           // 촘촘한 행 간격
                        float sbHeadH = 28f;           // 헤더 높이
                        float sbX = ClientSize.Width - sbWidth - 12;
                        float sbY = 8f;

                        // 패널 배경
                        RectangleF sbRect = new RectangleF(sbX, sbY, sbWidth, sbHeadH + show * sbRowH + 12);
                        e.Graphics.FillRectangle(panelBg, sbRect);

                        // 헤더
                        e.Graphics.DrawString("SCORE BOARD", headFont, white, sbX + 10, sbY + 6);

                        // 컬럼 헤더
                        float colNameX = sbX + 10;            // 헤더용 최초 위치
                        float colScoreX = sbX + sbWidth - 60;
                        e.Graphics.DrawString("Player", smallFont, gray, colNameX, sbY + sbHeadH + 2);
                        e.Graphics.DrawString("Score", smallFont, gray, colScoreX, sbY + sbHeadH + 2);

                        // 목록 (★ 겹침 방지: 첫 행 시작 Y를 확 내려줌)
                        float rowStartY = sbY + sbHeadH + 16f; // 헤더 아래 여백
                        for (int i = 0; i < show; i++)
                        {
                            var p = sorted[i];
                            float rowY = rowStartY + (i * sbRowH);

                            // 랭크 배경(메달색)
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

                            // 이름 색상: 나(하늘색 Bold) / 생존(흰색) / 사망(회색)
                            bool me = (_net != null && p.Id == _net.MyId);
                            Brush rowBrush = me
                                ? (Brush)new SolidBrush(Color.LightSkyBlue)
                                : (p.Alive ? (Brush)new SolidBrush(Color.White) : (Brush)new SolidBrush(Color.Gray));

                            // 닉네임 축약
                            string name = string.IsNullOrEmpty(p.Name) ? p.Id : p.Name;
                            if (name.Length > 12) name = name.Substring(0, 12) + "…";

                            // ── 랭크 뱃지(숫자)
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

                            // 이름/점수 X 위치 (★ 이름은 뱃지 오른쪽으로 충분히 밀기)
                            float namePad = 12f;
                            float nameX = badgeX + badgeW + namePad;   // ← 여기서부터 이름
                            float scoreX = colScoreX;

                            // 이름/점수 그리기
                            using (rowBrush)
                            using (var textFont = new Font("Segoe UI", 10, me ? FontStyle.Bold : FontStyle.Regular))
                            {
                                e.Graphics.DrawString(name, textFont, rowBrush, nameX, rowY);
                                e.Graphics.DrawString(p.Score.ToString(), textFont, rowBrush, scoreX, rowY);
                            }
                        }

                        // 3) 중앙 오버레이: 카운트다운 또는 투표 안내  (★ panelBg/white 재사용)
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
                                e.Graphics.DrawString(msg, big, white,
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
                                e.Graphics.DrawString(msg, big, white,
                                    (ClientSize.Width - sz.Width) / 2f,
                                    (ClientSize.Height - sz.Height) / 2f);
                            }
                        }
                    }
                }
                return;
            }

            // ====== 오프라인 렌더링 ======
            foreach (var obs in _obstacles)
            {
                if (_imgFire_Sword != null)
                {
                    var r = Rectangle.Round(obs);
                    // 가로는 약간 좁게, 세로는 길게
                    float w = r.Width * 0.8f;
                    float h = r.Height * 2.5f;
                    float x = r.X + (r.Width - w) / 2f;
                    float y = r.Y - (h - r.Height) / 2f;

                    g.DrawImage(_imgFire_Sword, x, y, w, h);
                }
                else
                {
                    // 이미지가 없으면 기존처럼 사각형으로 표시
                    using (var obs2 = new SolidBrush(Color.OrangeRed))
                    {
                        g.FillRectangle(obs2, Rectangle.Round(obs));
                    }
                }
            }


            DrawPlayerSprite(e.Graphics, _local.Rect, _local.Alive, highlight: true, facingRight: _facingRight);
            foreach (var kv in _remotes)
            {
                var p = kv.Value;
                // 간단히: 로컬 기준과 동일 논리(왼/오 입력 상태로 추정) 또는 p.vx>0 여부 이용 가능
                bool facingRight = p.vx >= 0; // 오프라인 봇용
                DrawPlayerSprite(e.Graphics, p.Rect, p.Alive, highlight: false, facingRight: facingRight);
            }

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
