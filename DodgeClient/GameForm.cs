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
                    if (p.IntersectsWith(Rectangle.Round(_obstacles[i])))
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
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.A) _local.Left = true;
            if (e.KeyCode == Keys.Right || e.KeyCode == Keys.D) _local.Right = true;
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
                using (var obs = new SolidBrush(Color.OrangeRed))
                {
                    for (int i = 0; i < _obsOnline.Count; i++)
                        g.FillRectangle(obs, Rectangle.Round(_obsOnline[i]));
                }

                foreach (var kv in _playersOnline)
                {
                    var id = kv.Key;
                    var r = kv.Value;
                    bool alive = _aliveOnline.Contains(id);
                    using (var br = new SolidBrush(alive
                        ? (id == (_net != null ? _net.MyId : "") ? Color.DeepSkyBlue : Color.MediumPurple)
                        : Color.Gray))
                    {
                        g.FillRectangle(br, Rectangle.Round(r));
                    }
                }

                using (var white = new SolidBrush(Color.White))
                using (var font = new Font("Segoe UI", 10))
                {
                    int myScore = (_net != null && _scoreOnline.ContainsKey(_net.MyId)) ? _scoreOnline[_net.MyId] : 0;
                    g.DrawString("ONLINE  Score: " + myScore, font, white, 12, 12);

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
                return;
            }

            // ====== 오프라인 렌더링 ======
            using (var obs2 = new SolidBrush(Color.OrangeRed))
            {
                for (int i = 0; i < _obstacles.Count; i++)
                    g.FillRectangle(obs2, Rectangle.Round(_obstacles[i]));
            }

            DrawPlayer(g, _local);
            foreach (var kv in _remotes)
                DrawPlayer(g, kv.Value);

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
