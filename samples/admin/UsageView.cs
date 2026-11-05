namespace FreeSyncAdmin;

/// <summary>A horizontal bar with its text drawn on top; green, then orange from 70%, red from 90%.</summary>
public sealed class MeterBar : Control
{
    private double _value;
    private double? _max;

    public MeterBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Height = 22;
        Font = new Font("Segoe UI", 9);
    }

    public void Set(double value, double? max, string text)
    {
        if (_value == value && _max == max && Text == text) return;
        _value = value;
        _max = max;
        Text = text;
        Invalidate();
    }

    public static Color ColorFor(double fraction) =>
        fraction >= 0.9 ? Color.FromArgb(214, 69, 65) : fraction >= 0.7 ? Color.FromArgb(230, 145, 56) : Color.FromArgb(76, 160, 92);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var bg = new SolidBrush(Color.FromArgb(236, 238, 242))) g.FillRectangle(bg, rect);
        if (_max is > 0)
        {
            var fraction = Math.Clamp(_value / _max.Value, 0, 1);
            var fill = (int)Math.Round(rect.Width * fraction);
            if (_value > 0) fill = Math.Max(fill, 2); // a sliver, so "some usage" is visible
            using var brush = new SolidBrush(ColorFor(_value / _max.Value));
            g.FillRectangle(brush, 0, 0, fill, rect.Height + 1);
        }
        using (var border = new Pen(Color.FromArgb(200, 204, 212))) g.DrawRectangle(border, rect);
        TextRenderer.DrawText(g, Text, Font, rect, Color.FromArgb(30, 30, 30), TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.LeftAndRightPadding);
    }
}

/// <summary>Label + bar rows for a list of meters, with tooltips.</summary>
internal sealed class MeterTable : TableLayoutPanel
{
    private readonly ToolTip _tips = new();
    private readonly List<(Label Label, MeterBar Bar)> _rows = new();

    public MeterTable()
    {
        Dock = DockStyle.Top;
        AutoSize = true;
        ColumnCount = 2;
        Padding = new Padding(0, 4, 8, 4);
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    }

    public void SetMeters(IReadOnlyList<Meter> meters)
    {
        SuspendLayout();
        while (_rows.Count < meters.Count)
        {
            var label = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Height = 24 };
            var bar = new MeterBar { Dock = DockStyle.Fill, Margin = new Padding(3) };
            RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            Controls.Add(label, 0, _rows.Count);
            Controls.Add(bar, 1, _rows.Count);
            _rows.Add((label, bar));
        }
        for (var i = 0; i < meters.Count; i++)
        {
            var m = meters[i];
            var (label, bar) = _rows[i];
            label.Text = m.Label;
            bar.Set(m.Value, m.Max, m.Text);
            _tips.SetToolTip(label, m.Hint);
            _tips.SetToolTip(bar, m.Hint);
        }
        ResumeLayout();
    }
}

/// <summary>The Usage tab: this room's meters, optional account-wide meters, and a 7-day history.</summary>
internal sealed class UsageView : UserControl
{
    private readonly Label _title = new() { Dock = DockStyle.Top, Height = 24, Font = new Font("Segoe UI", 10, FontStyle.Bold), Padding = new Padding(2, 4, 0, 0) };
    private readonly MeterTable _meters = new();
    private readonly Label _note = new() { Dock = DockStyle.Top, Height = 34, ForeColor = Color.DimGray, Padding = new Padding(2, 4, 0, 0) };
    private readonly Label _accountTitle = new()
    {
        Dock = DockStyle.Top, Height = 26, Font = new Font("Segoe UI", 10, FontStyle.Bold), Padding = new Padding(2, 6, 0, 0),
        Text = "Whole account today: all rooms (Cloudflare analytics)",
    };
    private readonly MeterTable _accountMeters = new();
    private readonly Label _accountNote = new() { Dock = DockStyle.Top, Height = 34, ForeColor = Color.DimGray, Padding = new Padding(2, 4, 0, 0) };
    private readonly DataGridView _history = new()
    {
        Dock = DockStyle.Top, Height = 200, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window,
    };

    public Button RefreshButton { get; } = new() { Text = "Refresh", AutoSize = true };
    public Label Status { get; } = new() { AutoSize = true, Padding = new Padding(6, 6, 0, 0), ForeColor = Color.DimGray };
    public TextBox AccountId { get; } = new() { Width = 250 };
    public TextBox ApiToken { get; } = new() { Width = 250, UseSystemPasswordChar = true };

    public UsageView()
    {
        AutoScroll = true; // two meter sections plus a week of history are taller than the tab
        foreach (var name in new[] { "Day (UTC)", "Writes", "Rows written", "Connections", "Messages", "Refused", "Quota hits" })
            _history.Columns.Add(name.Replace(" ", ""), name);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, WrapContents = false };
        top.Controls.Add(RefreshButton);
        top.Controls.Add(Status);
        var accountConfig = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, WrapContents = false };
        accountConfig.Controls.AddRange(new Control[]
        {
            new Label { Text = "Account ID", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, AccountId,
            new Label { Text = "API token (Account Analytics: Read)", AutoSize = true, Padding = new Padding(8, 6, 0, 0) }, ApiToken,
        });
        var historyLabel = new Label { Text = "This room, last 7 days", Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9, FontStyle.Bold), Padding = new Padding(2, 4, 0, 0) };

        // Docked controls are laid out from the last added, so add bottom-most first.
        Controls.Add(_history);
        Controls.Add(historyLabel);
        Controls.Add(_accountNote);
        Controls.Add(_accountMeters);
        Controls.Add(accountConfig);
        Controls.Add(_accountTitle);
        Controls.Add(_note);
        Controls.Add(_meters);
        Controls.Add(_title);
        Controls.Add(top);
        ShowMessage("Connect with the write key to see usage.");
        ShowAccountMessage("Enter your account ID and an API token with Account Analytics: Read to see exact account-wide usage.");
    }

    public bool HasAccountSettings => AccountId.Text.Trim().Length > 0 && ApiToken.Text.Trim().Length > 0;

    public void ShowMessage(string message)
    {
        _title.Text = "";
        _note.Text = message;
        _meters.Visible = false;
    }

    public void Show(Usage usage)
    {
        _title.Text = usage.Title;
        _note.Text = usage.Note;
        _meters.Visible = true;
        _meters.SetMeters(usage.Meters);
        _history.Rows.Clear();
        foreach (var row in usage.History) _history.Rows.Add(row);
    }

    public void ShowAccountMessage(string message)
    {
        _accountMeters.Visible = false;
        _accountNote.Text = message;
        _accountNote.ForeColor = Color.DimGray;
    }

    public void ShowAccount(AccountUsage usage, DateTime fetchedLocal)
    {
        _accountMeters.Visible = true;
        _accountMeters.SetMeters(AccountUsageModel.Meters(usage));
        _accountNote.ForeColor = Color.DimGray;
        _accountNote.Text = $"From Cloudflare at {fetchedLocal:HH:mm:ss}; analytics run a few minutes behind. Limits are the Workers Free plan's (reset 00:00 UTC; storage is a total).";
    }

    public void ShowAccountError(string message)
    {
        _accountNote.Text = message;
        _accountNote.ForeColor = Color.Firebrick;
    }
}
