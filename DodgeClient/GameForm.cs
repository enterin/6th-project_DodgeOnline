using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using System.Xml.Linq;
using static DodgeBattleStarter.NetClient;

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
        Dictionary<string, Color> _colorById = new Dictionary<string, Color>();

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

        // ==== LOBBY UI ====
        private Panel _pLobby;
        private ListView _lvLobby;
        private TextBox _tbName;
        private Panel _pColorPreview;
        private Button _btnPickColor;
        private Button _btnReady;

        private bool _readyLocal = false;
        private string _colorHtml = "#39A9F9";

        // GameForm 필드 추가
        long _lastLobbyTsServer = -1;
        string _lastLobbySig = null;   // 로비 스냅샷 중복 반영 방지용 시그니처
        string _myColorHex;

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
            public int Kind; // 서버 ObKind: 0=Knife, 1=Boom, 2=Fire, 3=Explosion
            public OnlineOb(RectangleF r, int k) { Rect = r; Kind = k; }
        }
        List<OnlineOb> _obsOnline = new List<OnlineOb>();

        Dictionary<string, RectangleF> _playersOnline = new Dictionary<string, RectangleF>();
        HashSet<string> _aliveOnline = new HashSet<string>();
        Dictionary<string, int> _scoreOnline = new Dictionary<string, int>();

        // Ready 전송 디바운스 & 최신 LOBBY 판별용
        DateTime _lastReadySend = DateTime.MinValue;

        public GameForm()
        {
            Text = "Dodge Battle (← →, ↑/Space: 점프 | R: 재시작/투표 | T: 봇 on/off | Esc: 종료)";
            ClientSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            this.KeyPreview = true;  // ★ 폼이 키 입력을 먼저 받게 함 (중요)

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

            // 로비 UI 빌드
            BuildLobbyUi();

            // 내 닉네임 초기값(원하면 바꿔도 무방)
            _tbName.Text = _nickname;

            this.KeyDown += OnKeyDown;   // ★ 키 이벤트 연결
        }

        private void BuildLobbyUi()
        {
            // 오른쪽 사이드 패널 (폭/패딩 넉넉히)
            _pLobby = new Panel
            {
                Dock = DockStyle.Right,
                Width = 320,                         // ← 280 → 320
                Padding = new Padding(8),
                BackColor = Color.FromArgb(30, 30, 34)
            };
            Controls.Add(_pLobby);

            // 플레이어 리스트 (왼쪽 본문)
            _lvLobby = new ListView
            {
                View = View.Details,
                Dock = DockStyle.Fill,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BackColor = Color.FromArgb(22, 24, 28),
                ForeColor = Color.White
            };
            _lvLobby.Columns.Add("#", 36);
            _lvLobby.Columns.Add("Name", 150);
            _lvLobby.Columns.Add("Ready", 70);

            // 리스트를 담을 중앙 패널
            Panel center = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 24, 28) };
            center.Controls.Add(_lvLobby);
            Controls.Add(center);

            // 우측 편집/버튼 영역 (아래 고정 → 전체 채우기 + 스크롤)
            FlowLayoutPanel pnl = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,               // ★ Fill
                AutoScroll = true,                   // ★ 스크롤 허용
                WrapContents = false,                // ★ 세로 스택
                FlowDirection = FlowDirection.TopDown,
                Padding = new Padding(0, 8, 0, 8)
            };
            _pLobby.Controls.Add(pnl);

            // 컨트롤들
            Label lb1 = new Label { Text = "Name", ForeColor = Color.White, AutoSize = true };
            _tbName = new TextBox { Width = 240 };
            Button btnApplyName = new Button { Text = "Apply Name", Width = 240 };
            btnApplyName.Click += (s, e) =>
            {
                if (_online && _net != null) _net.SendSetName(_tbName.Text);
            };

            // Enter 로 즉시 적용
            _tbName.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    if (_online && _net != null) _net.SendSetName(_tbName.Text);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };

            Label lb2 = new Label { Text = "Color", ForeColor = Color.White, AutoSize = true };
            _pColorPreview = new Panel { Width = 240, Height = 20, BackColor = ColorTranslator.FromHtml(_colorHtml) };
            _btnPickColor = new Button { Text = "Pick Color", Width = 240 };
            _btnPickColor.Click += OnPickColorClicked;

            _btnReady = new Button { Text = "READY", Width = 240, Height = 32 };
            _btnReady.Click += (s, e) =>
            {
                _readyLocal = !_readyLocal;
                _btnReady.Text = _readyLocal ? "UNREADY" : "READY";
                if (_online && _net != null) _net.SendReady(_readyLocal);
            };

            // === 다크테마 가시성 보정 ===
            Action<Button> StyleBtn = b =>
            {
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = Color.DimGray;
                b.UseVisualStyleBackColor = false;
                b.BackColor = Color.FromArgb(45, 47, 51);
                b.ForeColor = Color.Gainsboro;
            };

            Action<TextBox> StyleTb = t =>
            {
                t.BorderStyle = BorderStyle.FixedSingle;
                t.BackColor = Color.FromArgb(36, 38, 41);
                t.ForeColor = Color.Gainsboro;
            };

            Action<Label> StyleLb = l =>
            {
                l.ForeColor = Color.Gainsboro;
            };

            StyleLb(lb1);
            StyleTb(_tbName);
            StyleBtn(btnApplyName);

            StyleLb(lb2);
            _pColorPreview.BackColor = Color.FromArgb(60, 140, 200); // 기본 미리보기 톤
            StyleBtn(_btnPickColor);

            StyleBtn(_btnReady);

            // 레이아웃에 추가
            pnl.Controls.Add(lb1);
            pnl.Controls.Add(_tbName);
            pnl.Controls.Add(btnApplyName);
            pnl.Controls.Add(lb2);
            pnl.Controls.Add(_pColorPreview);
            pnl.Controls.Add(_btnPickColor);
            pnl.Controls.Add(_btnReady);

            // 패널 리사이즈 시 자식 폭 자동 조정 (오른쪽 짤림 방지)
            pnl.Resize += (s, e) =>
            {
                int w = pnl.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8; // 여유
                if (w < 120) w = 120;
                _tbName.Width = w;
                btnApplyName.Width = w;
                _pColorPreview.Width = w;
                _btnPickColor.Width = w;
                _btnReady.Width = w;
            };

            // 기본은 숨겨두고(온라인+로비일 때만 보임)
            _pLobby.Visible = false;
            center.Visible = false;

            // Z-Order 보정
            _pLobby.BringToFront();
            if (_lvLobby.Parent != null) _lvLobby.Parent.BringToFront();
        }

        bool _strongHighlight = true;   // H키로 토글

        private void UpdateLobbyUI(NetLobby lobby)
        {
            bool show = (lobby != null);
            _pLobby.Visible = show;
            if (_lvLobby.Parent != null) _lvLobby.Parent.Visible = show;

            if (!show) return;

            _lvLobby.BeginUpdate();
            _lvLobby.Items.Clear();
            for (int i = 0; i < lobby.Players.Count; i++)
            {
                var pl = lobby.Players[i];
                var it = new ListViewItem((i + 1).ToString());
                string nm = string.IsNullOrEmpty(pl.Name) ? pl.Id : pl.Name;
                if (nm.Length > 18) nm = nm.Substring(0, 18) + "…";
                it.SubItems.Add(nm);
                it.SubItems.Add(pl.Ready ? "READY" : "");
                _lvLobby.Items.Add(it);
            }
            _lvLobby.EndUpdate();

            // ★ 혹시나 Z-Order 꼬임 방지
            _pLobby.BringToFront();
            if (_lvLobby.Parent != null) _lvLobby.Parent.BringToFront();
        }

        // 간단 서명 만들기: 인원수/레디수/각 플레이어 id+ready 묶어서 문자열
        string MakeLobbySignature(NetLobby lb)
        {
            if (lb == null || lb.Players == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.Append(lb.Need).Append('|').Append(lb.Ready).Append('|');
            for (int i = 0; i < lb.Players.Count; i++)
            {
                var p = lb.Players[i];
                sb.Append(p.Id).Append(':').Append(p.Ready ? '1' : '0').Append('|');
            }
            return sb.ToString();
        }

        // 내 Ready 상태와 버튼 텍스트를 동시에 맞추는 헬퍼
        void SetMyReady(bool v)
        {
            _readyLocal = v;
            if (_btnReady != null)
                _btnReady.Text = v ? "UNREADY" : "READY";
            // 서버로 굳이 보낼 필요는 없음(서버는 로비 진입 시 Ready=false로 초기화함)
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

            // 고정 스텝
            float fixedDt = 1f / TargetFps;
            float acc = dt;
            while (acc > 0f)
            {
                float step = (acc > fixedDt) ? fixedDt : acc;
                Step(step);
                acc -= step;
            }

            // GameForm.TickFrame()의 로비 처리 부분 교체
            if (_online && _net != null)
            {
                NetLobby lb = _net.TryGetLobby();
                if (lb != null)
                {
                    // ① 변경 여부 판단 (ts 또는 signature가 달라질 때만)
                    string sig = MakeLobbySignature(lb);
                    bool changed = (lb.Ts != _lastLobbyTsServer) || (sig != _lastLobbySig);

                    if (changed)
                    {
                        _lastLobbyTsServer = lb.Ts;
                        _lastLobbySig = sig;

                        UpdateLobbyUI(lb);

                        // 내 Ready 버튼 동기화 (서버 기준)
                        bool myReady = false;
                        if (lb.Players != null && _net != null)
                            foreach (var pl in lb.Players)
                                if (pl.Id == _net.MyId) { myReady = pl.Ready; break; }
                        if (myReady != _readyLocal)
                        {
                            _readyLocal = myReady;
                            if (_btnReady != null) _btnReady.Text = _readyLocal ? "UNREADY" : "READY";
                        }

                        // 게임 캐시 클리어(로비 진입 시 1회만)
                        _obsOnline.Clear();
                        _playersOnline.Clear();
                        _aliveOnline.Clear();
                        _scoreOnline.Clear();

                        Invalidate(); // 변경이 있을 때만 다시 그리기
                    }

                    return; // 로비 프레임 종료
                }

                // 로비가 아니라면 UI 숨김 (한 번만)
                if (_pLobby.Visible) UpdateLobbyUI(null);
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

                    if (_net != null && _myColorHex != null)
                    {
                        var meId = _net.MyId;
                        if (!_colorById.ContainsKey(meId))
                            _colorById[meId] = ColorTranslator.FromHtml(_myColorHex);
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
            if (e.KeyCode == Keys.H) _strongHighlight = !_strongHighlight;

            if (_online && _net != null)
                _net.SendInput(_local.Left, _local.Right, _local.Up);

            if (e.KeyCode == Keys.R)
            {
                if (_online && _net != null)
                {
                    NetLobby lobby = _net.TryGetLobby();
                    if (lobby != null)
                    {
                        // 로비면 READY 토글
                        _btnReady.PerformClick();
                        return;
                    }
                }

                // 로비가 아니면 기존 동작 유지 (온라인: RESPAWN, 오프라인: Reset)
                if (_online && _net != null) _net.SendRespawn();
                else ResetGame();
            }

            if (_online && _net != null && e.KeyCode == Keys.L)
            {
                // 로비가 아닐 때만 개인 로비 이동 요청
                var snap = _net.TryGetSnapshot();
                if (snap == null || snap.Phase != "lobby")
                {
                    _net.SendLeaveToLobby();
                    e.Handled = true;
                }
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
        void DrawPlayerSprite(Graphics g, RectangleF rect, bool alive, bool highlight, bool facingRight, Color? accent = null)
        {
            var r = Rectangle.Round(rect);
            Image img = facingRight ? _imgPlayerRight : _imgPlayerLeft;

            if (img != null)
            {
                if (accent.HasValue)
                {
                    // 틴트 적용 함수 사용
                    DrawTintedImage(g, img, r, accent.Value);
                }
                else
                {
                    g.DrawImage(img, r);
                }
            }
            else
            {
                using (var br = new SolidBrush(accent ?? (alive ? Color.DeepSkyBlue : Color.Gray)))
                    g.FillRectangle(br, r);
            }

            // 죽었으면 어둡게
            if (!alive)
            {
                using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                    g.FillRectangle(dim, r);
            }

            if (!highlight) return;

            // ===== 강조(펄스 링 없음) =====
            // 1) 얇은 외곽선
            using (var pen = new Pen(Color.FromArgb(220, 80, 200, 255), 2))
                g.DrawRectangle(pen, r);

            // 2) 바닥 하이라이트(그림자형 원)
            Rectangle shadow = new Rectangle(r.X - 8, r.Bottom - 6, r.Width + 16, 10);
            using (var sh = new SolidBrush(Color.FromArgb(70, 80, 200, 255)))
                g.FillEllipse(sh, shadow);

            // 3) 머리 위 "YOU" 배지
            using (var font = new Font("Segoe UI", 9, FontStyle.Bold))
            {
                string tag = "YOU";
                SizeF sz = g.MeasureString(tag, font);
                RectangleF label = new RectangleF(
                    r.X + (r.Width - sz.Width) / 2f - 6,
                    r.Y - sz.Height - 8,
                    sz.Width + 12, sz.Height + 6);

                using (var bg = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                using (var pen2 = new Pen(Color.FromArgb(220, 80, 200, 255)))
                {
                    g.FillRectangle(bg, label);
                    g.DrawRectangle(pen2, Rectangle.Round(label));
                }
                g.DrawString(tag, font, Brushes.White, label.X + 6, label.Y + 3);
            }
        }

        // =============== 그리기 ===============
        protected override void OnPaint(PaintEventArgs e)
        {
            if (_online && _net != null)
            {
                NetLobby lobby = _net.TryGetLobby();
                if (lobby != null)
                {
                    // 배경
                    e.Graphics.Clear(Color.FromArgb(22, 24, 28));

                    using (Font f = new Font("Segoe UI", 14, FontStyle.Bold))
                    using (Brush br = new SolidBrush(Color.White))
                    {
                        string s = string.Format("LOBBY   Ready {0}/{1}", lobby.Ready, lobby.Need);
                        e.Graphics.DrawString(s, f, br, 12, 12);
                    }
                    // 리스트/우측 패널은 컨트롤로 이미 표시되고 있으니 여기서 더 그릴 필요 없음.
                    return;
                }
            }

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
                RectangleF? myRectForLater = null;

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
                    if (me)
                    {
                        myRectForLater = rect;           // ★ 내 캐릭터는 나중에(최상단) 그리기
                        continue;
                    }

                    Color? accent = null;
                    if (_colorById.TryGetValue(id, out var c)) accent = c;
                    DrawPlayerSprite(e.Graphics, rect, alive, highlight: false, facingRight: faceRight, accent);

                }

                // ★ 마지막에 내 캐릭터를 최상단으로 강조 그리기
                if (myRectForLater.HasValue)
                {
                    Color? myAccent = null;
                    if (_colorById.TryGetValue(_net.MyId, out var meC)) myAccent = meC;
                    DrawPlayerSprite(e.Graphics, myRectForLater.Value,
                                     _aliveOnline.Contains(_net.MyId),
                                     highlight: _strongHighlight,
                                     facingRight: _facingRight,
                                     accent: myAccent);
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
                            var scoresSorted = new List<NetPlayer>(snapForHud.Players);
                            scoresSorted.Sort(delegate (NetPlayer a, NetPlayer b)
                            {
                                int cmp = b.Score.CompareTo(a.Score);
                                if (cmp != 0) return cmp;
                                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                            });

                            int showScores = Math.Min(6, scoresSorted.Count);
                            float sbWidth = 220f;
                            float sbRowH = 15f;
                            float sbHeadH = 28f;
                            float sbX = ClientSize.Width - sbWidth - 12;
                            float sbY = 8f;

                            RectangleF sbRect = new RectangleF(sbX, sbY, sbWidth, sbHeadH + showScores * sbRowH + 12);
                            e.Graphics.FillRectangle(panelBg, sbRect);

                            e.Graphics.DrawString("SCORE BOARD", headFont, Brushes.White, sbX + 10, sbY + 6);

                            float colNameX = sbX + 10;
                            float colScoreX = sbX + sbWidth - 60;
                            e.Graphics.DrawString("Player", smallFont, gray, colNameX, sbY + sbHeadH + 2);
                            e.Graphics.DrawString("Score", smallFont, gray, colScoreX, sbY + sbHeadH + 2);

                            float rowStartY = sbY + sbHeadH + 16f;
                            for (int i = 0; i < showScores; i++)
                            {
                                var p = scoresSorted[i];
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

                                Color rowColor = Color.White;
                                if (_colorById.TryGetValue(p.Id, out var picked))
                                    rowColor = picked;

                                using (var textFont = new Font("Segoe UI", 10, me ? FontStyle.Bold : FontStyle.Regular))
                                using (var nameBrush = new SolidBrush(rowColor))
                                using (var scoreBrush = new SolidBrush(me ? Color.LightSkyBlue : (p.Alive ? Color.White : Color.Gray)))
                                {
                                    // 색 칩(원) 표시
                                    float chipR = 5f;
                                    e.Graphics.FillEllipse(nameBrush, nameX - 12 - chipR, rowY + 4, chipR * 2, chipR * 2);

                                    // 이름/점수
                                    e.Graphics.DrawString(name, textFont, nameBrush, nameX, rowY);
                                    e.Graphics.DrawString(p.Score.ToString(), textFont, scoreBrush, scoreX, rowY);
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
                            else if (snapForHud.Phase == "match_over")
                            {
                                using (var big = new Font("Segoe UI", 22, FontStyle.Bold))
                                using (var head = new Font("Segoe UI", 12, FontStyle.Bold))
                                using (var small = new Font("Segoe UI", 11))
                                {
                                    // 타이틀
                                    string title = $"MATCH OVER  ({snapForHud.MatchRound}/{snapForHud.MatchTotal})";
                                    SizeF tsz = e.Graphics.MeasureString(title, big);
                                    float w = Math.Max(360, tsz.Width + 80);
                                    float h = 160 + Math.Min(6, snapForHud.Totals.Count) * 22;
                                    RectangleF box = new RectangleF((ClientSize.Width - w) / 2f, (ClientSize.Height - h) / 2f, w, h);
                                    e.Graphics.FillRectangle(panelBg, box);
                                    e.Graphics.DrawString(title, big, Brushes.White, box.X + 20, box.Y + 16);

                                    // 합계 정렬
                                    var totalsSorted = new List<NetTotal>(snapForHud.Totals);
                                    totalsSorted.Sort((a, b) => b.Total.CompareTo(a.Total));
                                    
                                    float y = box.Y + 64;
                                    e.Graphics.DrawString("Player", head, Brushes.Gainsboro, box.X + 24, y);
                                    e.Graphics.DrawString("Total", head, Brushes.Gainsboro, box.Right - 90, y);
                                    y += 26;

                                    int showTotals = Math.Min(6, totalsSorted.Count);
                                    for (int i = 0; i < showTotals; i++)
                                    {
                                        var t = totalsSorted[i];
                                        string name = string.IsNullOrEmpty(t.Name) ? t.Id : t.Name;
                                        if (name.Length > 16) name = name.Substring(0, 16) + "…";
                                        // 색 칩
                                        Color chip = (_colorById.TryGetValue(t.Id, out var c) ? c : Color.White);
                                        using (var chipBr = new SolidBrush(chip))
                                            e.Graphics.FillEllipse(chipBr, box.X + 24, y + 4, 10, 10);
                                        e.Graphics.DrawString(name, small, Brushes.White, box.X + 40, y);
                                        e.Graphics.DrawString(t.Total.ToString(), small, Brushes.White, box.Right - 90, y);
                                        y += 22;
                                    }
                                    
                                    // 안내
                                    string hint = "Returning to Lobby… (or press R to start a new match)";
                                    SizeF hsz = e.Graphics.MeasureString(hint, small);
                                    e.Graphics.DrawString(hint, small, Brushes.Gainsboro, box.X + (w - hsz.Width) / 2f, box.Bottom - 28);
                                }
                            }
                            else if (snapForHud.Phase == "playing")
                            {
                                using (var big = new Font("Segoe UI", 12, FontStyle.Bold))
                                {
                                    string msg = "Press L to leave to Lobby (solo)";
                                    SizeF sz = e.Graphics.MeasureString(msg, big);
                                    // 좌측 상단 HUD 아래에 반투명 패널로 안내
                                    RectangleF rect = new RectangleF(12, 36, sz.Width + 12, sz.Height + 8);
                                    e.Graphics.FillRectangle(panelBg, rect);
                                    e.Graphics.DrawString(msg, big, Brushes.White, 18, 40);
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

    // ===== Pick Color 처리 =====
    void OnPickColorClicked(object sender, EventArgs e)
    {
        using (var dlg = new ColorDialog())
        {
            dlg.FullOpen = true;
            dlg.Color = _pColorPreview.BackColor;
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
            _pColorPreview.BackColor = dlg.Color;
                            // #RRGGBB 형식으로 전송
            string hex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
            _net?.SendSetColor(hex);
                            // 선택적으로, UI에 즉시 반영될 수 있도록 로컬 캐시 색도 업데이트
            _myColorHex = hex; // (필드가 없으면 string _myColorHex; 하나 추가해도 OK)

            _colorById[_net?.MyId ?? "local"] = dlg.Color;   // 내 색 기억
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

        static void DrawTintedImage(Graphics g, Image img, Rectangle dest, Color tint)
        {
            if (img == null) return;

            float tr = tint.R / 255f, tg = tint.G / 255f, tb = tint.B / 255f;

            var cm = new System.Drawing.Imaging.ColorMatrix(new float[][]
            {
                new float[] { tr, 0,  0,  0, 0 },
                new float[] { 0,  tg, 0,  0, 0 },
                new float[] { 0,  0,  tb, 0, 0 },
                new float[] { 0,  0,  0,  1, 0 },
                new float[] { 0,  0,  0,  0, 1 },
            });

            var ia = new System.Drawing.Imaging.ImageAttributes();
            ia.SetColorMatrix(cm, System.Drawing.Imaging.ColorMatrixFlag.Default, System.Drawing.Imaging.ColorAdjustType.Bitmap);
            g.DrawImage(img, dest, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
        }

    }
}
