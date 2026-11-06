namespace BonfireAdmin;

/// <summary>The Backups tab: export/import, the automatic before-submit backups, and undo.</summary>
internal sealed class BackupsView : UserControl
{
    public Button ExportButton { get; } = new() { Text = "Export…", AutoSize = true };
    public Button ImportButton { get; } = new() { Text = "Import / restore file…", AutoSize = true };
    public Button UndoButton { get; } = new() { Text = "Undo last submit", AutoSize = true };
    public Button RestoreButton { get; } = new() { Text = "Restore selected", AutoSize = true };
    public Button OpenFolderButton { get; } = new() { Text = "Open folder", AutoSize = true };
    public CheckBox AutoBackup { get; } = new() { Text = "Back up before every submit (keeps the last 50)", Checked = true, AutoSize = true, Padding = new Padding(8, 4, 0, 0) };
    public Label Status { get; } = new() { Dock = DockStyle.Top, Height = 40, ForeColor = Color.DimGray, Padding = new Padding(2, 4, 0, 0) };
    public ListView List { get; } = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, Font = new Font("Consolas", 9) };

    public BackupsView()
    {
        List.Columns.Add("Saved", 150);
        List.Columns.Add("Kind", 110);
        List.Columns.Add("Paths", 380);
        List.Columns.Add("Note", 260);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false };
        top.Controls.AddRange(new Control[] { ExportButton, ImportButton, UndoButton, AutoBackup });
        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, WrapContents = false };
        listButtons.Controls.AddRange(new Control[] { RestoreButton, OpenFolderButton });
        var listLabel = new Label
        {
            Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9, FontStyle.Bold), Padding = new Padding(2, 4, 0, 0),
            Text = "Automatic backups for this room (newest first). Restoring stages the old values; review them, then Submit.",
        };

        // Docked controls are laid out from the last added, so add bottom-most first.
        Controls.Add(List);
        Controls.Add(listButtons);
        Controls.Add(listLabel);
        Controls.Add(Status);
        Controls.Add(top);
    }

    public void ShowBackups(IEnumerable<(string File, BackupFile Backup)> backups)
    {
        List.BeginUpdate();
        List.Items.Clear();
        foreach (var (file, b) in backups)
        {
            var paths = b.Entries.Count == 1 ? "/" + JsonUtil.Join(b.Entries[0].Path) : string.Join(", ", b.Entries.Take(4).Select(e => "/" + JsonUtil.Join(e.Path))) + (b.Entries.Count > 4 ? $" … ({b.Entries.Count})" : "");
            var item = new ListViewItem(b.CreatedUtc == DateTime.MinValue ? "?" : b.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")) { Tag = file };
            item.SubItems.Add(b.Kind);
            item.SubItems.Add(paths);
            item.SubItems.Add(b.Note);
            List.Items.Add(item);
        }
        List.EndUpdate();
    }
}
