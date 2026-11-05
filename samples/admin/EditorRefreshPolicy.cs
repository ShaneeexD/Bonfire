namespace FreeSyncAdmin;

/// <summary>What a live data refresh should do to the value editor.</summary>
public enum EditorRefresh
{
    /// <summary>Nothing changed for the shown node; leave the editor (and hide the "changed" notice).</summary>
    None,
    /// <summary>The value changed but the user is working in the editor: keep their state, show a notice.</summary>
    ShowNotice,
    /// <summary>Load the value (keeping scroll position and caret when it's the same node).</summary>
    Reload,
}

/// <summary>Rules that keep live updates from clobbering the user's work in the editor.</summary>
public static class EditorRefreshPolicy
{
    /// <param name="force">The user asked for it: picked a node or pressed Reload.</param>
    /// <param name="sameNode">The editor already shows this node.</param>
    /// <param name="valueChanged">The node's value differs from what the editor loaded.</param>
    /// <param name="userBusy">The user edited the text or type, or the editor/dropdown has focus.</param>
    public static EditorRefresh Decide(bool force, bool sameNode, bool valueChanged, bool userBusy)
    {
        if (force || !sameNode) return EditorRefresh.Reload;
        if (!valueChanged) return EditorRefresh.None;
        return userBusy ? EditorRefresh.ShowNotice : EditorRefresh.Reload;
    }

    /// <summary>
    /// Whether a tree selection change should load a node. Selection also changes when a refresh moves or removes
    /// nodes; those, and re-selecting the node already shown, must not reset the editor.
    /// </summary>
    public static bool IsUserSelection(bool refreshing, IReadOnlyList<string>? shown, IReadOnlyList<string> selected) =>
        !refreshing && (shown == null || !shown.SequenceEqual(selected));
}
