using FreeSync.Json;
using FreeSyncAdmin;
using Xunit;

namespace FreeSyncAdmin.Tests;

public class SqlTests
{
    /// <summary>In-memory document; writes go through a ChangeSet like in the editor.</summary>
    private sealed class Doc : ISqlData
    {
        public object? Tree;
        public readonly ChangeSet Changes = new();
        private int _n;

        public Doc(string json) => Tree = FsJson.Parse(json);

        public object? Read(string[] path) => JsonUtil.GetIn(Changes.ApplyTo(Tree), path);
        public void Write(string[] path, object? value) => Changes.Add(path, value);
        public string NewKey() => $"k{++_n:D3}";
        public string Json => FsJson.Serialize(Changes.ApplyTo(Tree));
    }

    private const string Players = """
        {"players":{
            "a":{"name":"Ann","score":5,"pos":{"x":1}},
            "b":{"name":"Bob","score":12},
            "c":{"name":"Cy","score":7,"banned":true}},
         "chat":{"m1":{"from":"Ann","text":"hi"},"m2":{"from":"Bob","text":"buy spam now"}},
         "motd":"hello"}
        """;

    private static SqlResult Run(Doc doc, string sql)
    {
        SqlResult last = new();
        foreach (var stmt in SqlParser.Parse(sql)) last = SqlExecutor.Execute(stmt, doc);
        return last;
    }

    private static List<string[]> Rows(SqlResult r) =>
        r.Table!.Rows.Cast<System.Data.DataRow>().Select(row => row.ItemArray.Select(v => (string)v!).ToArray()).ToList();

    [Fact]
    public void SelectStarFlattensFieldsAndOrders()
    {
        var r = Run(new Doc(Players), "SELECT * FROM players ORDER BY score DESC");
        Assert.Equal(new[] { "key", "name", "score", "pos", "banned" }, r.Table!.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName));
        Assert.Equal(new[] { "b", "c", "a" }, Rows(r).Select(x => x[0]));
        Assert.Equal("{\"x\":1}", Rows(r)[2][3]);
    }

    [Fact]
    public void WhereSupportsComparisonsLogicLikeInAndNull()
    {
        var doc = new Doc(Players);
        Assert.Equal(new[] { "b", "c" }, Rows(Run(doc, "SELECT key FROM players WHERE score > 5 ORDER BY key")).Select(x => x[0]));
        Assert.Equal(new[] { "a", "b" }, Rows(Run(doc, "SELECT key FROM players WHERE banned IS NULL")).Select(x => x[0]));
        Assert.Equal(new[] { "c" }, Rows(Run(doc, "SELECT key FROM players WHERE NOT (score < 7) AND name LIKE 'c%'")).Select(x => x[0]));
        Assert.Equal(new[] { "a", "c" }, Rows(Run(doc, "SELECT key FROM players WHERE name IN ('Ann', 'Cy')")).Select(x => x[0]));
        Assert.Equal(new[] { "a" }, Rows(Run(doc, "SELECT key FROM players WHERE pos.x = 1")).Select(x => x[0]));
        Assert.Equal("3", Rows(Run(doc, "SELECT COUNT(*) FROM players"))[0][0]);
        Assert.Equal(new[] { "24" }, Rows(Run(doc, "SELECT score * 2 AS double FROM players WHERE key = 'b'")).Select(x => x[0]));
    }

    [Fact]
    public void LimitOffsetAndQuotedNames()
    {
        var doc = new Doc("""{"rooms":{"lobby":{"players":{"x":{"n":1},"y":{"n":2},"z":{"n":3}}}}}""");
        var r = Run(doc, "SELECT key FROM \"rooms/lobby/players\" ORDER BY n LIMIT 2 OFFSET 1");
        Assert.Equal(new[] { "y", "z" }, Rows(r).Select(x => x[0]));
        Assert.Equal("2 of 3 row(s)", r.Message);
        Assert.Equal(3, Rows(Run(doc, "SELECT * FROM rooms.lobby.players")).Count);
    }

    [Fact]
    public void UpdateUsesRowValuesAndNullDeletesFields()
    {
        var doc = new Doc(Players);
        var r = Run(doc, "UPDATE players SET score = score + 1, banned = NULL WHERE score >= 7");
        Assert.Equal("2 row(s) updated", r.Message);
        Assert.Contains("\"b\":{\"name\":\"Bob\",\"score\":13}", doc.Json);
        Assert.Contains("\"c\":{\"name\":\"Cy\",\"score\":8}", doc.Json);
        Assert.Contains("\"a\":{\"name\":\"Ann\",\"score\":5", doc.Json);
        // Changes are minimal paths, not whole rows.
        Assert.Contains(doc.Changes.Entries, e => JsonUtil.Join(e.Path) == "players/b/score");
    }

    [Fact]
    public void InsertWithKeysValuesAndGeneratedKeys()
    {
        var doc = new Doc(Players);
        Run(doc, "INSERT INTO players (key, name, score, pos.x) VALUES ('d', 'Di', 1, 4.5)");
        Run(doc, "INSERT INTO settings (key, value) VALUES ('motd', 'Welcome!'), ('max', 16)");
        Run(doc, "INSERT INTO chat (from, text) VALUES ('admin', 'it''s me')");
        Assert.Contains("\"d\":{\"name\":\"Di\",\"score\":1,\"pos\":{\"x\":4.5}}", doc.Json);
        Assert.Contains("\"settings\":{\"motd\":\"Welcome!\",\"max\":16}", doc.Json);
        Assert.Contains("\"k001\":{\"from\":\"admin\",\"text\":\"it's me\"}", doc.Json);
        Assert.Throws<SqlException>(() => Run(doc, "INSERT INTO players (key, name) VALUES ('a', 'dup')"));
    }

    [Fact]
    public void DeleteRemovesMatchingRows()
    {
        var doc = new Doc(Players);
        Assert.Equal("1 row(s) deleted", Run(doc, "DELETE FROM chat WHERE text LIKE '%spam%'").Message);
        Assert.DoesNotContain("spam", doc.Json);
        Assert.Equal("1 row(s)", Run(doc, "SELECT * FROM chat").Message); // later statements see earlier changes
        Run(doc, "DELETE FROM chat");
        Assert.DoesNotContain("chat", doc.Json);
    }

    [Fact]
    public void FunctionsAndNoOpUpdates()
    {
        var doc = new Doc(Players);
        Assert.Equal("3 row(s) updated", Run(doc, "UPDATE players SET level = COALESCE(level, 0) + 10").Message);
        Assert.Contains("\"level\":10", doc.Json);
        Assert.Equal("3 row(s) matched, 0 changed", Run(doc, "UPDATE players SET level = 10").Message);
        Assert.Equal("3 row(s) matched, 0 changed", Run(doc, "UPDATE players SET missing = missing + 1").Message);
        var r = Run(doc, "SELECT UPPER(name) AS n, LENGTH(name) AS len, ROUND(score / 3.0, 1) AS third FROM players WHERE key = 'b'");
        Assert.Equal(new[] { "BOB", "3", "4" }, Rows(r).Single());
        Assert.Throws<SqlException>(() => Run(doc, "SELECT NOPE(name) FROM players"));
    }

    [Fact]
    public void KeywordsWorkAsFieldNames()
    {
        var doc = new Doc(Players);
        var r = Run(doc, "SELECT key, from, text FROM chat WHERE from = 'Ann' ORDER BY key DESC LIMIT 10");
        Assert.Equal(new[] { "m1", "Ann", "hi" }, Rows(r).Single());
        Run(doc, "UPDATE chat SET from = 'Annie' WHERE from = 'Ann'");
        Assert.Contains("\"m1\":{\"from\":\"Annie\"", doc.Json);
    }

    [Fact]
    public void ShowTablesListsObjectsAndValues()
    {
        var rows = Rows(Run(new Doc(Players), "SHOW TABLES"));
        Assert.Equal(new[] { "chat", "motd", "players" }, rows.Select(r => r[0]));
        Assert.Equal("3", rows[2][1]);
        Assert.StartsWith("value", rows[1][2]);
    }

    [Theory]
    [InlineData("SELEC * FROM x")]
    [InlineData("SELECT * FROM")]
    [InlineData("SELECT * FROM players WHERE")]
    [InlineData("INSERT INTO t (a, b) VALUES (1)")]
    [InlineData("UPDATE players SET key = 'x'")]
    [InlineData("SELECT * FROM motd")]
    [InlineData("SELECT * FROM 'bad.path'")]
    [InlineData("SELECT * FROM players WHERE name = 'unterminated")]
    public void ReportsErrorsClearly(string sql)
    {
        var e = Assert.Throws<SqlException>(() => Run(new Doc(Players), sql));
        Assert.False(string.IsNullOrWhiteSpace(e.Message));
    }

    [Fact]
    public void ChangeSetKeepsPathsDisjoint()
    {
        var cs = new ChangeSet();
        cs.Add(new[] { "players", "a", "score" }, 1L);
        cs.Add(new[] { "players", "b", "score" }, 2L);
        cs.Add(new[] { "players", "a" }, FsJson.Parse("{\"name\":\"Ann\"}")); // replaces the deeper change
        cs.Add(new[] { "players", "a", "score" }, 9L);                          // folds into the ancestor
        cs.Add(new[] { "chat" }, null);
        Assert.Equal(new[] { "players/b/score", "players/a", "chat" }, cs.Entries.Select(e => JsonUtil.Join(e.Path)));
        Assert.Equal("{\"name\":\"Ann\",\"score\":9}", FsJson.Serialize(cs.Entries[1].Value));
        var updates = cs.ToUpdates();
        Assert.Single(updates);
        Assert.Equal(3, updates[0].Count);
        Assert.True(cs.Touches(new[] { "players", "a", "name" }));
        Assert.True(cs.ContainsBelow(new[] { "players" }));
        Assert.False(cs.Touches(new[] { "players" }));
    }

    [Fact]
    public void ChangeSetSplitsOversizedBatches()
    {
        var cs = new ChangeSet();
        for (var i = 0; i < 50; i++) cs.Add(new[] { "big", "k" + i }, new string('x', 2000));
        var updates = cs.ToUpdates(maxBytes: 56_000);
        Assert.True(updates.Count >= 2);
        Assert.Equal(50, updates.Sum(u => u.Count));
        Assert.All(updates, u => Assert.True(FsJson.Serialize(u).Length < 60_000));
    }

    [Theory]
    // force, sameNode, valueChanged, userBusy -> expected
    [InlineData(true, true, false, true, EditorRefresh.Reload)]      // Reload button / node picked: always load
    [InlineData(false, false, true, true, EditorRefresh.Reload)]     // first load of a node
    [InlineData(false, true, false, false, EditorRefresh.None)]      // live update elsewhere: editor untouched
    [InlineData(false, true, false, true, EditorRefresh.None)]
    [InlineData(false, true, true, false, EditorRefresh.Reload)]     // idle editor follows live data
    [InlineData(false, true, true, true, EditorRefresh.ShowNotice)]  // user is editing: never clobber, just tell them
    public void LiveUpdatesNeverResetAnEditorInUse(bool force, bool sameNode, bool changed, bool busy, EditorRefresh expected) =>
        Assert.Equal(expected, EditorRefreshPolicy.Decide(force, sameNode, changed, busy));

    [Fact]
    public void RefreshSelectionChangesAreIgnored()
    {
        string[] bob = { "players", "bob" }, ann = { "players", "ann" };
        Assert.True(EditorRefreshPolicy.IsUserSelection(false, bob, ann));
        Assert.True(EditorRefreshPolicy.IsUserSelection(false, null, ann));
        Assert.False(EditorRefreshPolicy.IsUserSelection(true, bob, ann));   // caused by the tree refreshing
        Assert.False(EditorRefreshPolicy.IsUserSelection(false, bob, new[] { "players", "bob" })); // same node again
    }
}
