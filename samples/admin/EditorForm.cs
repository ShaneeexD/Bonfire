using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FreeSync;
using FreeSync.Json;

namespace FreeSyncAdmin;

/// <summary>
/// Admin editor for one room. The left tree shows the live document (or the part under "Path"), with staged
/// changes applied and marked. Edit nodes on the right, or run SQL in the SQL tab. With batch mode on (the
/// default), changes are staged and Submit sends them all as one atomic multi-path update.
/// </summary>
internal sealed class EditorForm : Form
{
    private const string Placeholder = "\u0001loading";
    private static readonly string[] TypeNames = { "JSON (auto)", "Text", "Number", "True / False", "Delete (null)" };

    private readonly AdminSettings _settings;

    // Top bar
    private readonly TextBox _url = new() { Width = 220 };
    private readonly TextBox _room = new() { Width = 90 };
    private readonly TextBox _key = new() { Width = 80, UseSystemPasswordChar = true };
    private readonly TextBox _path = new() { Width = 100, PlaceholderText = "(whole room)" };
    private readonly Button _connect = new() { Text = "Connect", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(6, 6, 0, 0), Font = new Font("Segoe UI", 9, FontStyle.Bold) };
    private readonly CheckBox _batch = new() { Text = "Batch mode", Checked = true, AutoSize = true, Padding = new Padding(10, 3, 0, 0) };
    private readonly Button _submit = new() { Text = "Submit (0)", AutoSize = true, Enabled = false };
    private readonly Button _discard = new() { Text = "Discard", AutoSize = true, Enabled = false };

    // Tree and node editor
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, Font = new Font("Consolas", 10) };
    private readonly Label _nodePath = new() { Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9, FontStyle.Bold), AutoEllipsis = true };
    private readonly Label _changedNotice = new()
    {
        Dock = DockStyle.Top, Height = 22, Visible = false, BackColor = Color.LightYellow, ForeColor = Color.SaddleBrown, Cursor = Cursors.Hand,
        Text = "  This value changed on the server since you opened it. Click here or Reload to see the latest; Save overwrites it.",
    };
    private readonly TextBox _value = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10), AcceptsTab = true };
    private readonly ComboBox _type = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly Button _save = new() { Text = "Save (Ctrl+S)", AutoSize = true };
    private readonly Button _addChild = new() { Text = "Add child…", AutoSize = true };
    private readonly Button _rename = new() { Text = "Rename / move… (F2)", AutoSize = true };
    private readonly Button _delete = new() { Text = "Delete (Del)", AutoSize = true };
    private readonly Button _revert = new() { Text = "Reload", AutoSize = true };
    private readonly Label _modeHint = new() { Dock = DockStyle.Bottom, Height = 20, ForeColor = Color.DimGray };

    // Tabs
    private readonly TextBox _sql = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 10), AcceptsTab = true };
    private readonly Button _run = new() { Text = "Run (F5 / Ctrl+Enter)", AutoSize = true };
    private readonly ComboBox _examples = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    private readonly Label _sqlStatus = new() { AutoSize = true, Padding = new Padding(6, 6, 0, 0) };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells, RowHeadersVisible = false, BackgroundColor = SystemColors.Window,
    };
    private readonly ListView _pending = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, Font = new Font("Consolas", 9) };
    private readonly Button _removeChange = new() { Text = "Remove selected change", AutoSize = true };
    private readonly ListBox _log = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 9), HorizontalScrollbar = true, IntegralHeight = false };

    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 200 };

    // Usage
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly UsageView _usageView = new() { Dock = DockStyle.Fill };
    private readonly MeterBar _footerMeter = new() { Width = 360, Cursor = Cursors.Hand, Margin = new Padding(3, 3, 3, 3) };
    private readonly MeterBar _footerAccount = new() { Width = 300, Cursor = Cursors.Hand, Margin = new Padding(8, 3, 3, 3), Visible = false };
    private readonly Label _footerText = new() { AutoSize = true, Padding = new Padding(4, 6, 0, 0), ForeColor = Color.DimGray, Cursor = Cursors.Hand };
    private readonly System.Windows.Forms.Timer _usageTimer = new() { Interval = 60_000 };
    private TabPage? _usageTab;
    private bool _usageBusy;

    private FreeSyncClient? _db;
    private IDisposable? _subscription;
    private string[] _scope = Array.Empty<string>();
    private object? _scopeValue;
    private bool _loaded;
    private bool _viewDirty = true;
    private readonly ChangeSet _staged = new();
    private string[]? _selected;
    /// <summary>The user changed the editor's text or type since it was loaded.</summary>
    private bool _editorDirty;
    private bool _settingEditor;
    /// <summary>Tree is being synced with new data; selection events then aren't the user's.</summary>
    private bool _refreshing;
    /// <summary>The node and value currently loaded in the editor, to tell real server changes from no-ops.</summary>
    private string[]? _loadedPath;
    private string? _loadedJson;
    /// <summary>The root was expanded and selected after the first data arrived.</summary>
    private bool _initialised;

    private static readonly string[] Examples =
    {
        "SHOW TABLES",
        "SELECT * FROM players ORDER BY score DESC",
        "SELECT key, name, score FROM players WHERE score > 0",
        "SELECT COUNT(*) FROM chat",
        "SELECT key, from, text FROM chat ORDER BY key DESC LIMIT 10",
        "UPDATE players SET score = COALESCE(score, 0) + 1 WHERE name IS NOT NULL",
        "UPDATE players SET score = 0",
        "INSERT INTO settings (key, value) VALUES ('motd', 'Welcome!'), ('maxPlayers', 16)",
        "INSERT INTO chat (from, text) VALUES ('admin', 'Hello from the admin editor')",
        "DELETE FROM chat WHERE text LIKE '%spam%'",
        "DELETE FROM players WHERE name IS NULL",
    };

    public EditorForm(AdminSettings settings)
    {
        _settings = settings;
        Text = "FreeSync admin editor";
        ClientSize = new Size(1280, 780);
        MinimumSize = new Size(900, 560);
        Font = new Font("Segoe UI", 9);
        KeyPreview = true;

        _url.Text = settings.Url;
        _room.Text = settings.Room;
        _key.Text = settings.Key;
        _path.Text = settings.RootPath;
        // Names double as UI Automation ids (accessibility tools and UI tests).
        _tree.Name = "tree";
        _nodePath.Name = "nodePath";
        _changedNotice.Name = "changedNotice";
        _value.Name = "valueEditor";
        _type.Name = "valueType";
        _examples.Name = "examples";
        _type.Items.AddRange(TypeNames);
        _type.SelectedIndex = 0;
        _examples.Items.Add("Insert an example query…");
        _examples.Items.AddRange(Examples);
        _examples.SelectedIndex = 0;
        _sql.Text = settings.Sql ?? "SELECT * FROM players ORDER BY score DESC";

        BuildLayout();
        WireEvents();
        SetStatus("Not connected", Color.Gray);
        UpdateBatchUi();
        _refresh.Start();
        Log($"Defaults from {settings.Source}. Press Connect.");
        if (settings.Connect) Shown += (_, _) => ToggleConnection();
    }

    /// <summary>With --run: runs the SQL box once the document has loaded.</summary>
    private async void RunWhenLoaded()
    {
        for (var i = 0; i < 100 && !_loaded; i++) await Task.Delay(100);
        if (_loaded) await RunSql();
    }

    private void BuildLayout()
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(6, 6, 6, 0), WrapContents = false };
        top.Controls.AddRange(new Control[]
        {
            Caption("URL"), _url, Caption("Room"), _room, Caption("Key"), _key, Caption("Path"), _path, _connect, _status,
            _batch, _submit, _discard,
        });

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, WrapContents = false };
        buttons.Controls.AddRange(new Control[] { Caption("Type"), _type, _save, _addChild, _rename, _delete, _revert });
        var editor = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        editor.Controls.Add(_value);
        editor.Controls.Add(_changedNotice);
        editor.Controls.Add(_nodePath);
        editor.Controls.Add(buttons);
        editor.Controls.Add(_modeHint);

        var sqlTop = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false };
        sqlTop.Controls.AddRange(new Control[] { _run, _examples, _sqlStatus });
        var sqlSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 90 };
        sqlSplit.Panel1.Controls.Add(_sql);
        sqlSplit.Panel2.Controls.Add(_grid);
        var sqlTab = new TabPage("SQL");
        sqlTab.Controls.Add(sqlSplit);
        sqlTab.Controls.Add(sqlTop);

        _pending.Columns.Add("Change", 70);
        _pending.Columns.Add("Path", 260);
        _pending.Columns.Add("New value", 500);
        var pendingTab = new TabPage("Pending changes");
        var pendingButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34 };
        pendingButtons.Controls.Add(_removeChange);
        pendingTab.Controls.Add(_pending);
        pendingTab.Controls.Add(pendingButtons);

        var logTab = new TabPage("Log");
        logTab.Controls.Add(_log);
        _usageTab = new TabPage("Usage");
        _usageTab.Controls.Add(_usageView);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.AddRange(new[] { sqlTab, pendingTab, _usageTab, logTab });
        _pendingTab = pendingTab;
        _tabs = tabs;

        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        right.Panel1.Controls.Add(editor);
        right.Panel2.Controls.Add(tabs);

        var main = new SplitContainer { Dock = DockStyle.Fill };
        main.Panel1.Controls.Add(_tree);
        main.Panel2.Controls.Add(right);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 30, WrapContents = false, Padding = new Padding(4, 0, 0, 0) };
        footer.Controls.Add(new Label { Text = "Room writes today:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        footer.Controls.Add(_footerMeter);
        footer.Controls.Add(_footerAccount);
        footer.Controls.Add(_footerText);
        _footerMeter.Set(0, null, "-");
        _footerText.Text = "click for details";

        Controls.Add(main);
        Controls.Add(footer);
        Controls.Add(top);
        Load += (_, _) =>
        {
            main.SplitterDistance = 400;
            right.SplitterDistance = 300;
            sqlSplit.SplitterDistance = 110;
        };
    }

    private TabPage? _pendingTab;
    private TabControl? _tabs;

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(4, 6, 0, 0) };

    private void WireEvents()
    {
        _connect.Click += (_, _) => ToggleConnection();
        _batch.CheckedChanged += (_, _) => UpdateBatchUi();
        _submit.Click += async (_, _) => await SubmitStaged();
        _discard.Click += (_, _) =>
        {
            if (_staged.Count == 0 || Confirm($"Discard {_staged.Count} staged change(s)?")) DiscardStaged();
        };
        _tree.BeforeExpand += (_, e) => SyncChildren(e.Node!.Nodes, ValueAt((string[])e.Node.Tag!), (string[])e.Node.Tag!);
        _tree.AfterSelect += (_, e) =>
        {
            // Nodes moved or removed by a data refresh change the selection too; only react to real choices.
            var path = (string[])e.Node!.Tag!;
            if (EditorRefreshPolicy.IsUserSelection(_refreshing, _selected, path)) SelectPath(path);
        };
        _tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
            if (e.KeyCode == Keys.F2) { RenameSelected(); e.Handled = true; }
        };
        _value.TextChanged += (_, _) => { if (!_settingEditor) _editorDirty = true; };
        _type.SelectedIndexChanged += (_, _) => { if (!_settingEditor) _editorDirty = true; };
        _changedNotice.Click += (_, _) => LoadEditor(force: true);
        _save.Click += (_, _) => SaveEditor();
        _addChild.Click += (_, _) => AddChild();
        _rename.Click += (_, _) => RenameSelected();
        _delete.Click += (_, _) => DeleteSelected();
        _revert.Click += (_, _) => LoadEditor(force: true);
        _run.Click += async (_, _) => await RunSql();
        _examples.SelectedIndexChanged += (_, _) =>
        {
            if (_examples.SelectedIndex <= 0) return;
            _sql.Text = Examples[_examples.SelectedIndex - 1];
            _examples.SelectedIndex = 0;
            _sql.Focus();
        };
        _pending.DoubleClick += (_, _) =>
        {
            if (_pending.SelectedItems.Count == 1) RevealPath((string[])_pending.SelectedItems[0].Tag!);
        };
        _removeChange.Click += (_, _) =>
        {
            foreach (var index in _pending.SelectedIndices.Cast<int>().OrderByDescending(i => i)) _staged.RemoveAt(index);
            StagedChanged();
        };
        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.F5 || (e.Control && e.KeyCode == Keys.Enter && ActiveControl == _sql))
            {
                e.SuppressKeyPress = true;
                await RunSql();
            }
            else if (e.Control && e.KeyCode == Keys.S)
            {
                e.SuppressKeyPress = true;
                SaveEditor();
            }
        };
        _usageView.AccountId.Text = _settings.AccountId;
        _usageView.ApiToken.Text = _settings.AnalyticsToken;
        _usageView.RefreshButton.Click += async (_, _) => await RefreshUsage();
        _footerMeter.Click += (_, _) => ShowUsageTab();
        _footerAccount.Click += (_, _) => ShowUsageTab();
        _footerText.Click += (_, _) => ShowUsageTab();
        // Only while this window is active: every refresh costs a Worker request and a Durable Object request.
        _usageTimer.Tick += async (_, _) => { if (ActiveForm == this) await RefreshUsage(); };
        _usageTimer.Start();
        _refresh.Tick += (_, _) =>
        {
            if (!_viewDirty) return;
            _viewDirty = false;
            RefreshView();
        };
    }

    // ---- connection ------------------------------------------------------------------------

    private void ToggleConnection()
    {
        if (_db != null)
        {
            if (_staged.Count > 0 && !Confirm($"Disconnect and discard {_staged.Count} staged change(s)?")) return;
            Disconnect();
            return;
        }
        try
        {
            _scope = JsonUtil.ParsePath(_path.Text);
            _db = new FreeSyncClient(_url.Text.Trim(), _room.Text.Trim(), _key.Text.Trim(), new FreeSyncOptions
            {
                // An admin tool must not mangle data, so writes go out promptly and nothing is cached on disk.
                WriteInterval = TimeSpan.FromMilliseconds(20),
            });
        }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "Cannot connect");
            _db = null;
            return;
        }
        _db.StateChanged += state =>
        {
            SetStatus(state == ConnectionState.Connected ? $"Connected ({_db?.Role ?? "?"})" : state.ToString(),
                state switch
                {
                    ConnectionState.Connected => Color.SeaGreen,
                    ConnectionState.QuotaExceeded => Color.DarkOrange,
                    ConnectionState.Offline => Color.Gray,
                    _ => Color.Goldenrod,
                });
            if (state == ConnectionState.Connected && _db?.Role == "public")
                Log("Connected with the READ key: most writes will be refused by the rules. Use the write key to edit freely.");
        };
        _db.ErrorOccurred += e => Log($"error: {e.Code}: {e.Message}");
        foreach (var t in new[] { _url, _room, _key, _path }) t.Enabled = false;
        _connect.Text = "Disconnect";
        Text = $"FreeSync admin editor - {_room.Text}{(_scope.Length > 0 ? "/" + JsonUtil.Join(_scope) : "")}";
        Subscribe();
        _ = RefreshUsage();
        if (_settings.RunSql) RunWhenLoaded();
    }

    private void Subscribe()
    {
        _subscription?.Dispose();
        _loaded = false;
        _initialised = false;
        _scopeValue = null;
        _selected = null;
        _loadedPath = null;
        _tree.Nodes.Clear();
        _subscription = _db!.Ref(JsonUtil.Join(_scope)).OnValue(snap =>
        {
            _scopeValue = snap.Value;
            _loaded = true;
            _viewDirty = true;
        }, e =>
        {
            Log($"subscription refused: {e.Code}: {e.Message}");
            MessageBox.Show(this, e.Message, "Cannot read this path");
        });
        _viewDirty = true;
    }

    private void Disconnect()
    {
        _subscription?.Dispose();
        _subscription = null;
        _db?.Dispose();
        _db = null;
        _loaded = false;
        _scopeValue = null;
        _staged.Clear();
        _tree.Nodes.Clear();
        _selected = null;
        foreach (var t in new[] { _url, _room, _key, _path }) t.Enabled = true;
        _connect.Text = "Connect";
        SetStatus("Not connected", Color.Gray);
        _usageView.ShowMessage("Connect with the write key to see usage.");
        _footerMeter.Set(0, null, "-");
        StagedChanged();
    }

    // ---- data views ------------------------------------------------------------------------

    /// <summary>The server data we know about (the subscribed scope plus anything fetched for SQL).</summary>
    private object? ServerTree(Dictionary<string, object?>? extra = null)
    {
        object? tree = null;
        if (extra != null)
            foreach (var (path, value) in extra.OrderBy(kv => kv.Key.Length))
                tree = JsonUtil.SetIn(tree, JsonUtil.ParsePath(path), value);
        return JsonUtil.SetIn(tree, _scope, _scopeValue);
    }

    /// <summary>Server data with staged changes applied: what the document will look like after Submit.</summary>
    private object? CurrentView() => _staged.ApplyTo(ServerTree());

    private object? ValueAt(string[] path) => JsonUtil.GetIn(CurrentView(), path);

    // ---- tree --------------------------------------------------------------------------------

    private void RefreshView()
    {
        if (_db == null) return;
        _refreshing = true;
        _tree.BeginUpdate();
        try
        {
            if (_tree.Nodes.Count == 0)
            {
                var root = new TreeNode { Name = "", Tag = _scope };
                _tree.Nodes.Add(root);
                root.Expand();
            }
            var rootNode = _tree.Nodes[0];
            UpdateNode(rootNode, _scope.Length == 0 ? $"{_room.Text} (room root)" : "/" + JsonUtil.Join(_scope), ValueAt(_scope), _scope, isRoot: true);
            if (!_loaded) rootNode.Text = "loading…";
        }
        finally
        {
            _tree.EndUpdate();
        }

        // Keep the tree's highlight and the editor on the same node. The tree picks a node by itself (first
        // load, or when the selected node was moved or removed); adopt that only if nothing is shown yet.
        var adopt = false;
        if (_loaded && !_initialised && _tree.Nodes.Count > 0)
        {
            // First data: open the root and show it in the editor.
            _initialised = true;
            _tree.Nodes[0].Expand();
            _tree.SelectedNode = _tree.Nodes[0];
            if (_selected == null)
            {
                _selected = _scope;
                adopt = true;
            }
        }
        else if (_selected == null)
        {
            if (_tree.SelectedNode?.Tag is string[] picked && _loaded)
            {
                _selected = picked;
                adopt = true;
            }
        }
        else if (FindNode(_selected) is { } node && _tree.SelectedNode != node)
        {
            _tree.SelectedNode = node;
        }
        _refreshing = false;
        if (_selected != null) LoadEditor(force: adopt);
    }

    /// <summary>The tree node for a path, if it is currently in the tree (its ancestors are expanded).</summary>
    private TreeNode? FindNode(string[] path)
    {
        if (_tree.Nodes.Count == 0 || !JsonUtil.IsPrefix(_scope, path)) return null;
        var node = _tree.Nodes[0];
        for (var i = _scope.Length; i < path.Length; i++)
        {
            var idx = node.Nodes.IndexOfKey(path[i]);
            if (idx < 0) return null;
            node = node.Nodes[idx];
        }
        return node;
    }

    private void SyncChildren(TreeNodeCollection nodes, object? value, string[] path)
    {
        var dict = value as Dictionary<string, object?>;
        var keys = dict?.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList() ?? new List<string>();
        var wanted = new HashSet<string>(keys);
        for (var i = nodes.Count - 1; i >= 0; i--)
            if (!wanted.Contains(nodes[i].Name)) nodes.RemoveAt(i);
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            var childPath = path.Append(key).ToArray();
            var index = nodes.IndexOfKey(key);
            TreeNode node;
            if (index < 0)
            {
                node = new TreeNode { Name = key, Tag = childPath };
                nodes.Insert(i, node);
            }
            else
            {
                node = nodes[index];
                if (index != i)
                {
                    nodes.RemoveAt(index);
                    nodes.Insert(i, node);
                }
            }
            UpdateNode(node, key, dict![key], childPath, isRoot: false);
        }
    }

    private void UpdateNode(TreeNode node, string label, object? value, string[] path, bool isRoot)
    {
        var text = value switch
        {
            Dictionary<string, object?> d => $"{label}   {{{d.Count}}}",
            List<object?> l => $"{label}: [{l.Count}] {JsonUtil.Cell(l, 60)}",
            null when isRoot => $"{label}   (empty)",
            _ => $"{label}: {JsonUtil.Cell(value is string ? FsJson.Serialize(value) : value, 100)}",
        };
        var touched = _staged.Touches(path);
        var below = !touched && _staged.ContainsBelow(path);
        if (touched || below) text += "   *";
        if (node.Text != text) node.Text = text;
        var color = touched ? Color.DarkOrange : below ? Color.Peru : SystemColors.WindowText;
        if (node.ForeColor != color) node.ForeColor = color;

        if (value is Dictionary<string, object?> children && children.Count > 0)
        {
            if (node.IsExpanded || isRoot) SyncChildren(node.Nodes, value, path);
            else if (node.Nodes.Count == 0) node.Nodes.Add(new TreeNode("…") { Name = Placeholder, Tag = path });
        }
        else if (node.Nodes.Count > 0)
        {
            node.Nodes.Clear();
        }
    }

    private void RevealPath(string[] path)
    {
        if (!JsonUtil.IsPrefix(_scope, path) || _tree.Nodes.Count == 0)
        {
            SelectPath(path);
            return;
        }
        var node = _tree.Nodes[0];
        for (var i = _scope.Length; i < path.Length; i++)
        {
            node.Expand();
            var idx = node.Nodes.IndexOfKey(path[i]);
            if (idx < 0) break;
            node = node.Nodes[idx];
        }
        _tree.SelectedNode = node;
        SelectPath(path);
    }

    // ---- node editor -------------------------------------------------------------------------

    private void SelectPath(string[] path)
    {
        if (_editorDirty && _selected != null && !JsonUtil.IsPrefix(_selected, path) && !path.SequenceEqual(_selected))
            Log($"Unsaved edit of /{JsonUtil.Join(_selected)} was dropped.");
        _selected = path;
        LoadEditor(force: true);
    }

    /// <summary>
    /// Shows the selected node's value. On live data refreshes (<paramref name="force"/> false) it never disturbs the
    /// user: if they have edited the text or type, or are working in the editor, it only shows the "changed on the
    /// server" notice. Otherwise it refreshes in place, keeping the scroll position and caret.
    /// </summary>
    private void LoadEditor(bool force)
    {
        if (_selected == null) return;
        var value = ValueAt(_selected);
        var header = "/" + JsonUtil.Join(_selected) + (value == null ? "   (no value)" : "") + (_staged.Touches(_selected) ? "   - staged" : "");
        if (_nodePath.Text != header) _nodePath.Text = header;

        var json = FsJson.Serialize(value);
        var sameNode = _loadedPath != null && _loadedPath.SequenceEqual(_selected);
        var busy = _editorDirty || _value.ContainsFocus || _type.ContainsFocus || _type.DroppedDown;
        switch (EditorRefreshPolicy.Decide(force, sameNode, json != _loadedJson, busy))
        {
            case EditorRefresh.None:
                _changedNotice.Visible = false;
                return;
            case EditorRefresh.ShowNotice:
                _changedNotice.Visible = true;
                return;
        }

        var text = value == null ? "" : value is string str ? str : JsonUtil.Pretty(value);
        var firstLine = sameNode ? FirstVisibleLine(_value) : 0;
        var caret = sameNode ? _value.SelectionStart : 0;
        _settingEditor = true;
        if (_value.Text != text) _value.Text = text;
        _type.SelectedIndex = value is string ? 1 : 0;
        _value.SelectionStart = Math.Min(caret, _value.TextLength);
        _value.SelectionLength = 0;
        ScrollToLine(_value, firstLine);
        _settingEditor = false;
        _loadedPath = _selected;
        _loadedJson = json;
        _editorDirty = false;
        _changedNotice.Visible = false;
    }

    private const int EmGetFirstVisibleLine = 0x00CE, EmLineScroll = 0x00B6;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static int FirstVisibleLine(TextBox box) =>
        box.IsHandleCreated ? (int)SendMessage(box.Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero) : 0;

    private static void ScrollToLine(TextBox box, int line)
    {
        if (!box.IsHandleCreated) return;
        var delta = line - FirstVisibleLine(box);
        if (delta != 0) SendMessage(box.Handle, EmLineScroll, IntPtr.Zero, delta);
    }

    /// <summary>Parses editor text according to the chosen type. JSON (auto) falls back to plain text.</summary>
    private static object? ParseValue(string text, int typeIndex)
    {
        switch (typeIndex)
        {
            case 1:
                return text;
            case 2:
                if (long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                throw new FormatException($"\"{text}\" is not a number");
            case 3:
                return text.Trim().ToLowerInvariant() switch
                {
                    "true" or "1" or "yes" => true,
                    "false" or "0" or "no" => false,
                    _ => throw new FormatException("type true or false"),
                };
            case 4:
                return null;
            default:
                var trimmed = text.Trim();
                if (trimmed.Length == 0) return null;
                try
                {
                    return FsJson.Parse(trimmed);
                }
                catch (FormatException) when (!trimmed.StartsWith('{') && !trimmed.StartsWith('[') && !trimmed.StartsWith('"'))
                {
                    return text; // not JSON: store it as text
                }
        }
    }

    private void SaveEditor()
    {
        if (_selected == null || _db == null) return;
        object? value;
        try
        {
            value = ParseValue(_value.Text, _type.SelectedIndex);
        }
        catch (FormatException e)
        {
            MessageBox.Show(this, e.Message, "Invalid value");
            return;
        }
        if (value == null && !Confirm($"Delete /{JsonUtil.Join(_selected)} and everything below it?")) return;
        _editorDirty = false;
        _loadedPath = _selected;
        _loadedJson = FsJson.Serialize(JsonUtil.Normalize(value));
        _changedNotice.Visible = false;
        Apply(Single(_selected, value), $"set /{JsonUtil.Join(_selected)}");
    }

    private void AddChild()
    {
        if (_db == null) return;
        var parent = _selected ?? _scope;
        if (ValueAt(parent) is { } existing and not Dictionary<string, object?> &&
            !Confirm($"/{JsonUtil.Join(parent)} holds a single value. Adding a child replaces it with an object. Continue?")) return;
        using var dialog = new AddChildDialog(JsonUtil.Join(parent), TypeNames, () => _db.Ref("x").Push().Key!);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string[] path;
        object? value;
        try
        {
            path = parent.Concat(JsonUtil.ParsePath(dialog.Key)).ToArray();
            if (path.Length == parent.Length) throw new FormatException("enter a key");
            value = ParseValue(dialog.ValueText, dialog.TypeIndex);
            if (value == null) throw new FormatException("a new child needs a value");
        }
        catch (FormatException e)
        {
            MessageBox.Show(this, e.Message, "Cannot add");
            return;
        }
        Apply(Single(path, value), $"add /{JsonUtil.Join(path)}");
        RevealPath(path);
    }

    private void RenameSelected()
    {
        if (_selected == null || _selected.Length == 0 || _db == null) return;
        var value = ValueAt(_selected);
        if (value == null) return;
        var target = Prompt("Rename / move", "New path for this value:", JsonUtil.Join(_selected));
        if (target == null) return;
        string[] to;
        try
        {
            to = JsonUtil.ParsePath(target);
        }
        catch (FormatException e)
        {
            MessageBox.Show(this, e.Message, "Invalid path");
            return;
        }
        if (to.Length == 0 || to.SequenceEqual(_selected)) return;
        if (JsonUtil.IsPrefix(_selected, to) || JsonUtil.IsPrefix(to, _selected))
        {
            MessageBox.Show(this, "The new path can't be inside the old one or contain it.", "Cannot move");
            return;
        }
        if (ValueAt(to) != null && !Confirm($"/{JsonUtil.Join(to)} already has a value. Overwrite it?")) return;
        var cs = new ChangeSet();
        cs.Add(to, value);
        cs.Add(_selected, null);
        Apply(cs, $"move /{JsonUtil.Join(_selected)} -> /{JsonUtil.Join(to)}");
        RevealPath(to);
    }

    private void DeleteSelected()
    {
        if (_selected == null || _db == null || ValueAt(_selected) == null) return;
        var what = _selected.Length == 0 ? "EVERYTHING in this room" : $"/{JsonUtil.Join(_selected)} and everything below it";
        if (!Confirm($"Delete {what}?")) return;
        Apply(Single(_selected, null), $"delete /{JsonUtil.Join(_selected)}");
    }

    private static ChangeSet Single(string[] path, object? value)
    {
        var cs = new ChangeSet();
        cs.Add(path, value);
        return cs;
    }

    // ---- staging and sending -------------------------------------------------------------------

    /// <summary>Batch mode: stage. Otherwise: send now as one request.</summary>
    private async void Apply(ChangeSet changes, string description)
    {
        if (changes.Count == 0) return;
        if (_batch.Checked)
        {
            _staged.AddAll(changes);
            StagedChanged();
            Log($"staged: {description}");
        }
        else
        {
            await Send(changes, description);
        }
    }

    private async Task SubmitStaged()
    {
        if (_staged.Count == 0) return;
        var snapshot = new ChangeSet();
        snapshot.AddAll(_staged);
        _submit.Enabled = false;
        if (await Send(snapshot, $"batch of {snapshot.Count} change(s)"))
        {
            _staged.Clear();
            StagedChanged();
        }
        else
        {
            UpdateBatchUi();
        }
    }

    /// <summary>Sends changes as one atomic multi-path update (split only if it exceeds the frame size). Returns success.</summary>
    private async Task<bool> Send(ChangeSet changes, string description)
    {
        if (_db == null) return false;
        var sw = Stopwatch.StartNew();
        try
        {
            if (changes.Entries.Any(e => e.Path.Length == 0))
            {
                // Replacing the whole room is a plain set at the root.
                var rev = await _db.Ref("").SetAsync(changes.Entries.First(e => e.Path.Length == 0).Value);
                Log($"sent {description}: 1 request (root replaced), rev {rev}, {sw.ElapsedMilliseconds} ms");
                return true;
            }
            var updates = changes.ToUpdates();
            long last = 0;
            foreach (var update in updates) last = await _db.Ref("").UpdateAsync(update);
            var bytes = updates.Sum(u => FsJson.Serialize(u).Length);
            _ = RefreshUsage();
            Log($"sent {description}: {changes.Count} path(s) in {updates.Count} request(s){(updates.Count > 1 ? " (too big for one frame; each part is atomic)" : " (atomic)")}, {bytes} bytes, rev {last}, {sw.ElapsedMilliseconds} ms");
            return true;
        }
        catch (Exception e) when (e is FreeSyncException or ArgumentException)
        {
            var code = e is FreeSyncException fe ? fe.Code : "invalid";
            Log($"FAILED {description}: {code}: {e.Message}");
            MessageBox.Show(this, $"{code}: {e.Message}", "The server refused the change");
            return false;
        }
    }

    private void DiscardStaged()
    {
        _staged.Clear();
        StagedChanged();
        Log("discarded staged changes");
    }

    private void StagedChanged()
    {
        _pending.BeginUpdate();
        _pending.Items.Clear();
        foreach (var (path, value) in _staged.Entries)
        {
            var item = new ListViewItem(value == null ? "DELETE" : "SET") { Tag = path };
            item.SubItems.Add("/" + JsonUtil.Join(path));
            item.SubItems.Add(value == null ? "" : JsonUtil.Cell(value is string ? FsJson.Serialize(value) : value, 400));
            item.ForeColor = value == null ? Color.Firebrick : Color.DarkOrange;
            _pending.Items.Add(item);
        }
        _pending.EndUpdate();
        if (_pendingTab != null) _pendingTab.Text = $"Pending changes ({_staged.Count})";
        UpdateBatchUi();
        _viewDirty = true;
    }

    private void UpdateBatchUi()
    {
        _submit.Text = $"Submit ({_staged.Count})";
        _submit.Enabled = _db != null && _staged.Count > 0;
        _discard.Enabled = _staged.Count > 0;
        _submit.BackColor = _staged.Count > 0 ? Color.Moccasin : SystemColors.Control;
        _modeHint.Text = _batch.Checked
            ? "Batch mode: edits and SQL changes are staged (orange, *) until you press Submit, then sent as one atomic request."
            : "Batch mode off: every edit and every SQL statement is sent immediately.";
        if (!_batch.Checked && _staged.Count > 0) _modeHint.Text += $" {_staged.Count} change(s) are still staged.";
    }

    // ---- SQL -----------------------------------------------------------------------------------

    private sealed class SqlData : ISqlData
    {
        private readonly Func<object?> _base;
        private readonly Func<string> _newKey;
        public readonly ChangeSet All = new();
        public ChangeSet Unsent = new();

        public SqlData(Func<object?> baseTree, Func<string> newKey)
        {
            _base = baseTree;
            _newKey = newKey;
        }

        public object? Read(string[] path) => JsonUtil.GetIn(All.ApplyTo(_base()), path);

        public void Write(string[] path, object? value)
        {
            All.Add(path, value);
            Unsent.Add(path, value);
        }

        public string NewKey() => _newKey();
    }

    private async Task RunSql()
    {
        if (_db == null)
        {
            SqlStatus("Connect first.", true);
            return;
        }
        List<Statement> statements;
        try
        {
            statements = SqlParser.Parse(_sql.SelectionLength > 0 ? _sql.SelectedText : _sql.Text);
        }
        catch (SqlException e)
        {
            SqlStatus(e.Message, true);
            return;
        }
        if (statements.Count == 0) return;

        // Tables outside the subscribed path are fetched once before running.
        var extra = new Dictionary<string, object?>();
        foreach (var table in statements.Select(TableOf).Where(t => !JsonUtil.IsPrefix(_scope, t)).Select(JsonUtil.Join).Distinct())
        {
            try
            {
                extra[table] = (await _db.Ref(table).GetAsync()).Value;
            }
            catch (FreeSyncException e)
            {
                SqlStatus($"could not read {table}: {e.Message}", true);
                return;
            }
        }

        var data = new SqlData(() => _staged.ApplyTo(ServerTree(extra)), () => _db.Ref("x").Push().Key!);
        var messages = new List<string>();
        SqlResult? lastTable = null;
        try
        {
            foreach (var stmt in statements)
            {
                var result = SqlExecutor.Execute(stmt, data);
                messages.Add(result.Message);
                if (result.Table != null) lastTable = result;
                if (!_batch.Checked && data.Unsent.Count > 0)
                {
                    var toSend = data.Unsent;
                    data.Unsent = new ChangeSet();
                    if (!await Send(toSend, $"SQL: {Describe(stmt)}")) break;
                }
            }
        }
        catch (SqlException e)
        {
            messages.Add("error: " + e.Message);
            SqlStatus(string.Join(" · ", messages), true);
            if (lastTable != null) _grid.DataSource = lastTable.Table;
            return; // nothing from a failed script is staged
        }

        if (_batch.Checked && data.All.Count > 0)
        {
            _staged.AddAll(data.All);
            StagedChanged();
            messages.Add($"{data.All.Count} change(s) staged - press Submit to send");
            Log($"staged from SQL: {data.All.Count} change(s)");
        }
        if (lastTable != null) _grid.DataSource = lastTable.Table;
        SqlStatus(string.Join(" · ", messages), false);
    }

    private static string[] TableOf(Statement s) => s switch
    {
        SelectStmt x => x.Table,
        InsertStmt x => x.Table,
        UpdateStmt x => x.Table,
        DeleteStmt x => x.Table,
        ShowTablesStmt x => x.Path,
        _ => Array.Empty<string>(),
    };

    private static string Describe(Statement s) => s.GetType().Name.Replace("Stmt", "").ToUpperInvariant() + " " + JsonUtil.Join(TableOf(s));

    private void SqlStatus(string text, bool error)
    {
        _sqlStatus.Text = text;
        _sqlStatus.ForeColor = error ? Color.Firebrick : Color.DarkGreen;
    }

    // ---- usage ------------------------------------------------------------------------------

    private void ShowUsageTab()
    {
        if (_tabs != null && _usageTab != null) _tabs.SelectedTab = _usageTab;
        _ = RefreshUsage();
    }

    /// <summary>Fetches the room's counters from GET /db/&lt;room&gt;/stats (write key only) and updates the meters.</summary>
    private async Task RefreshUsage()
    {
        if (_db == null || _usageBusy) return;
        _usageBusy = true;
        try
        {
            var baseUrl = _url.Text.Trim().TrimEnd('/');
            if (baseUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) baseUrl = "https://" + baseUrl[6..];
            else if (baseUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) baseUrl = "http://" + baseUrl[5..];
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/db/{Uri.EscapeDataString(_room.Text.Trim())}/stats");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _key.Text.Trim());
            using var res = await Http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            if ((int)res.StatusCode == 403)
            {
                _usageView.ShowMessage("Usage needs the write key (the read key can't see room stats).");
                _footerMeter.Set(0, null, "write key needed");
                return;
            }
            if (!res.IsSuccessStatusCode)
            {
                _usageView.Status.Text = $"Refresh failed: HTTP {(int)res.StatusCode}";
                return;
            }
            var accountTask = RefreshAccountUsage();
            var usage = UsageModel.From(FsJson.Parse(body), DateTime.UtcNow);
            _usageView.Show(usage);
            _usageView.Status.Text = $"Updated {DateTime.Now:HH:mm:ss} · refreshes every 60 s while this window is active (each refresh costs 2 requests)";
            var h = usage.Headline;
            _footerMeter.Set(h.Value, h.Max, h.Text);
            _footerText.Text = usage.Title.Split('·').Last().Trim() + " · click for details";
            await accountTask;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or FormatException)
        {
            _usageView.Status.Text = "Refresh failed: " + e.Message;
        }
        finally
        {
            _usageBusy = false;
        }
    }

    /// <summary>Account-wide usage from Cloudflare's analytics API (optional; needs an analytics token).</summary>
    private async Task RefreshAccountUsage()
    {
        if (!_usageView.HasAccountSettings)
        {
            _usageView.ShowAccountMessage("Enter your account ID and an API token with Account Analytics: Read to see exact account-wide usage.");
            return;
        }
        try
        {
            var usage = await AccountUsageModel.FetchAsync(Http, _usageView.ApiToken.Text, _usageView.AccountId.Text, DateTime.UtcNow);
            _usageView.ShowAccount(usage, DateTime.Now);
            // The account's rows written is what actually stops writes, so show it in the footer too.
            var rows = AccountUsageModel.Meters(usage)[0];
            _footerAccount.Set(rows.Value, rows.Max, "account: " + rows.Text);
            _footerAccount.Visible = true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or FormatException)
        {
            _usageView.ShowAccountError(e.Message);
        }
    }

    // ---- misc --------------------------------------------------------------------------------

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }

    private void Log(string line)
    {
        _log.Items.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        _log.TopIndex = _log.Items.Count - 1;
    }

    private bool Confirm(string question) =>
        MessageBox.Show(this, question, "FreeSync admin editor", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;

    private string? Prompt(string title, string label, string initial)
    {
        using var form = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(460, 110), MinimizeBox = false, MaximizeBox = false };
        var text = new TextBox { Left = 12, Top = 36, Width = 436, Text = initial };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 292, Top = 72, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 373, Top = 72, Width = 75 };
        form.Controls.AddRange(new Control[] { new Label { Text = label, Left = 12, Top = 12, AutoSize = true }, text, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(this) == DialogResult.OK ? text.Text : null;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_staged.Count > 0 && !Confirm($"Close and discard {_staged.Count} staged change(s)?"))
        {
            e.Cancel = true;
            return;
        }
        _refresh.Stop();
        _usageTimer.Stop();
        _subscription?.Dispose();
        _db?.Dispose();
        base.OnFormClosing(e);
    }
}

/// <summary>Asks for a child key (or a generated push id) and its value.</summary>
internal sealed class AddChildDialog : Form
{
    private readonly TextBox _key = new() { Left = 12, Top = 32, Width = 300 };
    private readonly TextBox _valueBox = new() { Left = 12, Top = 82, Width = 456, Height = 120, Multiline = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 10) };
    private readonly ComboBox _type = new() { Left = 12, Top = 228, Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };

    public AddChildDialog(string parent, string[] typeNames, Func<string> newKey)
    {
        Text = $"Add child under /{parent}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(480, 270);
        _type.Items.AddRange(typeNames.Take(4).ToArray());
        _type.SelectedIndex = 0;
        var generate = new Button { Text = "Generate key", Left = 320, Top = 31, Width = 148 };
        generate.Click += (_, _) => _key.Text = newKey();
        var ok = new Button { Text = "Add", DialogResult = DialogResult.OK, Left = 312, Top = 228, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 393, Top = 228, Width = 75 };
        Controls.AddRange(new Control[]
        {
            new Label { Text = "Key (may contain '/' to add deeper, e.g. stats/hp):", Left = 12, Top = 12, AutoSize = true }, _key, generate,
            new Label { Text = "Value (JSON such as {\"name\":\"Ann\",\"score\":3}, a number, true/false, or plain text):", Left = 12, Top = 62, AutoSize = true }, _valueBox,
            new Label { Text = "Type", Left = 160, Top = 231, AutoSize = true }, _type, ok, cancel,
        });
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public string Key => _key.Text.Trim();
    public string ValueText => _valueBox.Text;
    public int TypeIndex => _type.SelectedIndex;
}
