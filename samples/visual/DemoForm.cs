using System.Diagnostics;
using System.Drawing.Drawing2D;
using FreeSync;

namespace FreeSyncVisualDemo;

/// <summary>
/// One window = one FreeSync client. Every connected player is a dot on a shared 640x480 board.
/// Positions go to players/{cid}/pos (an ephemeral path: broadcast, never stored); names and scores to
/// players/{cid}; who is online to presence/{cid} (removed by the server when the window closes); chat to chat/.
/// </summary>
internal sealed class DemoForm : Form
{
    private const float BoardW = 640, BoardH = 480, Speed = 220; // board units per second
    private static readonly TimeSpan SendInterval = TimeSpan.FromMilliseconds(100);

    private readonly DemoSettings _settings;
    private readonly TextBox _name = new() { Width = 110 };
    private readonly TextBox _url = new() { Width = 260 };
    private readonly TextBox _room = new() { Width = 100 };
    private readonly TextBox _key = new() { Width = 120, UseSystemPasswordChar = true };
    private readonly Button _connect = new() { Text = "Connect", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(8, 6, 0, 0), Font = new Font("Segoe UI", 9, FontStyle.Bold) };
    private readonly BoardPanel _board = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 26, 32) };
    private readonly ListBox _scores = new() { Dock = DockStyle.Fill, IntegralHeight = false, Font = new Font("Consolas", 10) };
    private readonly ListBox _chat = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
    private readonly TextBox _chatInput = new() { Dock = DockStyle.Fill, PlaceholderText = "Type a message and press Enter" };
    private readonly Button _plusOne = new() { Text = "+1 score", Dock = DockStyle.Top, Height = 32 };
    private readonly CheckBox _auto = new() { Text = "Auto-move (walk in a circle)", Dock = DockStyle.Top, Height = 26 };
    private readonly Label _info = new() { Dock = DockStyle.Bottom, Height = 54, ForeColor = Color.DimGray };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };

    private FreeSyncClient? _db;
    private readonly List<IDisposable> _listeners = new();
    private string _me = "";
    private readonly Dictionary<string, Player> _players = new();
    private readonly HashSet<string> _online = new();
    private readonly HashSet<Keys> _held = new();
    private PointF _pos = new(BoardW / 2, BoardH / 2);
    private PointF? _clickTarget;
    private PointF _lastSent = new(-1, -1);
    private DateTime _lastSend;
    private float _angle;
    private long _lastRtt = -1;
    private int _myScore;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTick;

    private sealed class Player
    {
        public string Name = "?";
        public int Score;
        public PointF? Target;
        public PointF Shown;
        public bool Placed;
    }

    public DemoForm(DemoSettings settings)
    {
        _settings = settings;
        Text = $"FreeSync visual demo - {settings.Name}";
        ClientSize = new Size(960, 600);
        MinimumSize = new Size(760, 480);
        KeyPreview = true;
        Font = new Font("Segoe UI", 9);

        _name.Text = settings.Name;
        _url.Text = settings.Url;
        _room.Text = settings.Room;
        _key.Text = settings.Key;
        _auto.Checked = settings.Auto;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(6, 6, 6, 0), WrapContents = false };
        top.Controls.AddRange(new Control[]
        {
            Caption("Name"), _name, Caption("URL"), _url, Caption("Room"), _room, Caption("Key"), _key, _connect, _status,
        });

        var chatBox = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        chatBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        chatBox.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        chatBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        chatBox.Controls.Add(Header("Chat"), 0, 0);
        chatBox.Controls.Add(_chat, 0, 1);
        chatBox.Controls.Add(_chatInput, 0, 2);

        var scoreBox = new Panel { Dock = DockStyle.Top, Height = 220 };
        scoreBox.Controls.Add(_scores);
        scoreBox.Controls.Add(_plusOne);
        scoreBox.Controls.Add(Header("Players (online first) - score"));

        var side = new Panel { Dock = DockStyle.Right, Width = 290, Padding = new Padding(6) };
        side.Controls.Add(chatBox);
        side.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 });
        side.Controls.Add(scoreBox);
        side.Controls.Add(_auto);
        side.Controls.Add(_info);

        Controls.Add(_board);
        Controls.Add(side);
        Controls.Add(top);

        _connect.Click += (_, _) => ToggleConnection();
        _plusOne.Click += async (_, _) => await Timed(() => _db?.Ref($"players/{_me}/score").SetAsync(_myScore + 1));
        _chatInput.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || _db == null || string.IsNullOrWhiteSpace(_chatInput.Text)) return;
            e.SuppressKeyPress = true;
            var text = _chatInput.Text.Trim();
            _chatInput.Clear();
            await Timed(() => _db.Ref("chat").Push().SetAsync(new Dictionary<string, object?> { ["from"] = _name.Text, ["text"] = text }));
        };
        _board.MouseDown += (_, e) =>
        {
            _board.Focus();
            _clickTarget = ToBoard(e.Location);
            _auto.Checked = false;
        };
        _board.Paint += PaintBoard;
        KeyDown += (_, e) => { if (BoardHasFocus) _held.Add(e.KeyCode); };
        KeyUp += (_, e) => _held.Remove(e.KeyCode);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        SetStatus("Not connected", Color.Gray);
        UpdateInfo();
        if (settings.AutoConnect) Shown += (_, _) => ToggleConnection();
    }

    private bool BoardHasFocus => ActiveControl == _board || ActiveControl == null;

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(4, 6, 0, 0) };

    private static Label Header(string text) => new() { Text = text, Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9, FontStyle.Bold) };

    // ---- connection --------------------------------------------------------------------------

    private void ToggleConnection()
    {
        if (_db != null)
        {
            Disconnect();
            return;
        }
        // A secret token per name gives a stable client id ($cid) across restarts of the same name.
        var tokenFile = Path.Combine(Path.GetTempPath(), "freesync-visual-demo", _name.Text + ".token");
        try
        {
            _db = new FreeSyncClient(_url.Text.Trim(), _room.Text.Trim(), _key.Text.Trim(), new FreeSyncOptions
            {
                ClientToken = FreeSyncClient.LoadOrCreateToken(tokenFile),
                // Created on the UI thread, so Auto posts every callback to this window's thread.
                EventDispatch = EventDispatchMode.Auto,
            });
        }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "Cannot connect");
            return;
        }
        _me = _db.ClientId!;
        Text = $"FreeSync visual demo - {_name.Text} ({_me})";
        _connect.Text = "Disconnect";
        foreach (var t in new[] { _name, _url, _room, _key }) t.Enabled = false;

        _db.StateChanged += OnState;
        _db.ErrorOccurred += e => AddChatLine($"[error] {e.Code}: {e.Message}");
        _listeners.Add(_db.Ref("players").OnValue(OnPlayers, e => AddChatLine($"[players] {e.Message}")));
        _listeners.Add(_db.Ref("presence").OnValue(OnPresence, e => AddChatLine($"[presence] {e.Message}")));
        _listeners.Add(_db.Ref("chat").OnValue(OnChat, e => AddChatLine($"[chat] {e.Message}")));
        OnState(_db.State);
        _board.Focus();
    }

    private void Disconnect()
    {
        foreach (var l in _listeners) l.Dispose();
        _listeners.Clear();
        var db = _db;
        _db = null;
        db?.Dispose(); // the server now runs our onDisconnect writes, so others see us leave
        _players.Clear();
        _online.Clear();
        _scores.Items.Clear();
        _connect.Text = "Connect";
        foreach (var t in new[] { _name, _url, _room, _key }) t.Enabled = true;
        SetStatus("Not connected", Color.Gray);
    }

    private async void OnState(ConnectionState state)
    {
        var color = state switch
        {
            ConnectionState.Connected => Color.SeaGreen,
            ConnectionState.QuotaExceeded => Color.DarkOrange,
            ConnectionState.Offline => Color.Gray,
            _ => Color.Goldenrod,
        };
        SetStatus(state.ToString(), color);
        if (state != ConnectionState.Connected || _db == null) return;

        // The server deletes presence when a connection drops, so (re)announce on every connect.
        try
        {
            await _db.Ref($"presence/{_me}").OnDisconnect().Remove();
            await _db.Ref($"presence/{_me}").SetAsync(_name.Text);
            await _db.Ref($"players/{_me}/name").SetAsync(_name.Text);
            _lastSent = new PointF(-1, -1); // resend our position
        }
        catch (FreeSyncException e)
        {
            AddChatLine($"[error] {e.Code}: {e.Message}");
        }
    }

    // ---- data from the server -------------------------------------------------------------

    private void OnPlayers(DataSnapshot snap)
    {
        var seen = new HashSet<string>();
        foreach (var child in snap.Children)
        {
            var id = child.Key!;
            seen.Add(id);
            if (!_players.TryGetValue(id, out var p)) _players[id] = p = new Player();
            p.Name = child.Child("name").GetValue<string>() ?? id;
            p.Score = child.Child("score").GetValue<int>();
            var pos = child.Child("pos");
            if (pos.Exists)
            {
                var target = new PointF(pos.Child("x").GetValue<float>(), pos.Child("y").GetValue<float>());
                p.Target = target;
                if (!p.Placed) { p.Shown = target; p.Placed = true; }
            }
            if (id == _me) _myScore = p.Score;
        }
        foreach (var gone in _players.Keys.Where(k => !seen.Contains(k)).ToList()) _players.Remove(gone);
        RefreshScores();
    }

    private void OnPresence(DataSnapshot snap)
    {
        _online.Clear();
        foreach (var child in snap.Children) _online.Add(child.Key!);
        RefreshScores();
    }

    private void OnChat(DataSnapshot snap)
    {
        // Push() keys sort by time, so the last children are the newest messages.
        var lines = snap.Children.TakeLast(100).Select(m => $"<{m.Child("from").GetValue<string>()}> {m.Child("text").GetValue<string>()}").ToList();
        _chat.BeginUpdate();
        _chat.Items.Clear();
        foreach (var l in lines) _chat.Items.Add(l);
        _chat.EndUpdate();
        if (_chat.Items.Count > 0) _chat.TopIndex = _chat.Items.Count - 1;
    }

    private void RefreshScores()
    {
        var rows = _players
            .OrderByDescending(kv => _online.Contains(kv.Key))
            .ThenByDescending(kv => kv.Value.Score)
            .Select(kv => $"{(_online.Contains(kv.Key) ? "●" : "○")} {Trim(kv.Value.Name, 14),-14} {kv.Value.Score,4}{(kv.Key == _me ? "  (you)" : "")}")
            .ToList();
        _scores.BeginUpdate();
        _scores.Items.Clear();
        foreach (var r in rows) _scores.Items.Add(r);
        _scores.EndUpdate();
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private void AddChatLine(string line)
    {
        _chat.Items.Add(line);
        _chat.TopIndex = _chat.Items.Count - 1;
    }

    // ---- movement and rendering -----------------------------------------------------------

    private void Tick()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = (float)Math.Min(0.1, now - _lastTick);
        _lastTick = now;

        var dir = new PointF(0, 0);
        if (BoardHasFocus)
        {
            if (_held.Contains(Keys.Left) || _held.Contains(Keys.A)) dir.X -= 1;
            if (_held.Contains(Keys.Right) || _held.Contains(Keys.D)) dir.X += 1;
            if (_held.Contains(Keys.Up) || _held.Contains(Keys.W)) dir.Y -= 1;
            if (_held.Contains(Keys.Down) || _held.Contains(Keys.S)) dir.Y += 1;
        }
        if (dir.X != 0 || dir.Y != 0)
        {
            _clickTarget = null;
            _auto.Checked = false;
            var len = MathF.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
            _pos = new PointF(_pos.X + dir.X / len * Speed * dt, _pos.Y + dir.Y / len * Speed * dt);
        }
        else if (_auto.Checked)
        {
            _angle += dt * 1.2f;
            var target = new PointF(BoardW / 2 + MathF.Cos(_angle) * 170, BoardH / 2 + MathF.Sin(_angle) * 130);
            _pos = MoveToward(_pos, target, Speed * dt);
        }
        else if (_clickTarget is PointF t)
        {
            _pos = MoveToward(_pos, t, Speed * dt);
            if (Dist(_pos, t) < 0.5f) _clickTarget = null;
        }
        _pos = new PointF(Math.Clamp(_pos.X, 12, BoardW - 12), Math.Clamp(_pos.Y, 12, BoardH - 12));

        // Send our position at most every 100 ms, and only when it changed.
        if (_db?.State == ConnectionState.Connected && DateTime.UtcNow - _lastSend >= SendInterval && Dist(_pos, _lastSent) > 0.5f)
        {
            _lastSend = DateTime.UtcNow;
            _lastSent = _pos;
            _ = Timed(() => _db.Ref($"players/{_me}/pos").SetAsync(new { x = MathF.Round(_pos.X, 1), y = MathF.Round(_pos.Y, 1) }));
        }

        // Others glide toward their latest reported position.
        var k = 1 - MathF.Exp(-dt * 12);
        foreach (var p in _players.Values)
            if (p.Target is PointF tp) p.Shown = new PointF(p.Shown.X + (tp.X - p.Shown.X) * k, p.Shown.Y + (tp.Y - p.Shown.Y) * k);

        UpdateInfo();
        _board.Invalidate();
    }

    private static PointF MoveToward(PointF from, PointF to, float maxStep)
    {
        var d = Dist(from, to);
        if (d <= maxStep || d == 0) return to;
        return new PointF(from.X + (to.X - from.X) / d * maxStep, from.Y + (to.Y - from.Y) / d * maxStep);
    }

    private static float Dist(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private (float Scale, float OffX, float OffY) Fit()
    {
        var scale = Math.Min(_board.ClientSize.Width / BoardW, _board.ClientSize.Height / BoardH);
        return (scale, (_board.ClientSize.Width - BoardW * scale) / 2, (_board.ClientSize.Height - BoardH * scale) / 2);
    }

    private PointF ToBoard(Point p)
    {
        var (s, ox, oy) = Fit();
        return new PointF((p.X - ox) / s, (p.Y - oy) / s);
    }

    private void PaintBoard(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var (s, ox, oy) = Fit();
        g.TranslateTransform(ox, oy);
        g.ScaleTransform(s, s);

        using (var bg = new SolidBrush(Color.FromArgb(32, 35, 44))) g.FillRectangle(bg, 0, 0, BoardW, BoardH);
        using (var grid = new Pen(Color.FromArgb(44, 48, 60)))
        {
            for (var x = 0; x <= BoardW; x += 40) g.DrawLine(grid, x, 0, x, BoardH);
            for (var y = 0; y <= BoardH; y += 40) g.DrawLine(grid, 0, y, BoardW, y);
        }
        using var label = new Font("Segoe UI", 9);

        foreach (var (id, p) in _players)
        {
            if (id == _me || !_online.Contains(id) || !p.Placed) continue;
            DrawDot(g, label, p.Shown, ColorFor(id), $"{p.Name} ({p.Score})", false);
        }
        if (_db != null)
            DrawDot(g, label, _pos, ColorFor(_me), $"{_name.Text} (you)", true);

        g.ResetTransform();
        using var hint = new SolidBrush(Color.FromArgb(150, 160, 180));
        g.DrawString(_db == null
                ? "Press Connect, then run a second copy of this app."
                : "Click the board or use arrow keys / WASD to move. Other windows see you move live.",
            label, hint, 8, 6);
    }

    private static void DrawDot(Graphics g, Font font, PointF at, Color color, string text, bool mine)
    {
        const float r = 12;
        using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillEllipse(shadow, at.X - r + 2, at.Y - r + 3, r * 2, r * 2);
        using (var fill = new SolidBrush(color)) g.FillEllipse(fill, at.X - r, at.Y - r, r * 2, r * 2);
        if (mine) using (var ring = new Pen(Color.White, 3)) g.DrawEllipse(ring, at.X - r - 3, at.Y - r - 3, (r + 3) * 2, (r + 3) * 2);
        var size = g.MeasureString(text, font);
        using var textBrush = new SolidBrush(Color.Gainsboro);
        g.DrawString(text, font, textBrush, at.X - size.Width / 2, at.Y + r + 4);
    }

    private static Color ColorFor(string id)
    {
        var hue = (uint)id.Aggregate(17, (h, c) => h * 31 + c) % 360;
        return FromHsv(hue, 0.65, 0.95);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    // ---- status ---------------------------------------------------------------------------

    /// <summary>Runs a write and records how long the server took to confirm it.</summary>
    private async Task Timed(Func<Task<long>?> write)
    {
        var task = write();
        if (task == null) return;
        var sw = Stopwatch.StartNew();
        try
        {
            await task;
            _lastRtt = sw.ElapsedMilliseconds;
        }
        catch (FreeSyncException e)
        {
            AddChatLine($"[refused] {e.Code}: {e.Message}");
        }
    }

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }

    private void UpdateInfo()
    {
        var text = _db == null
            ? $"Defaults from {_settings.Source}.\nRun two copies with the same room to see them sync."
            : $"Client id: {_me}\nLast write confirmed in: {(_lastRtt < 0 ? "-" : _lastRtt + " ms")}   Pending: {_db.PendingWriteCount}\nOnline: {_online.Count}   Players: {_players.Count}";
        if (_info.Text != text) _info.Text = text;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        Disconnect();
        base.OnFormClosing(e);
    }

    /// <summary>A double-buffered, focusable panel so drawing doesn't flicker and arrow keys reach the form.</summary>
    private sealed class BoardPanel : Panel
    {
        public BoardPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            TabStop = true;
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);
    }
}
