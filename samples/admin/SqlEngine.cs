using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FreeSyncAdmin;

// A small SQL dialect over the JSON document. A "table" is any object in the document (players, chat,
// rooms/lobby/players...): its children are rows, each row's key is the `key` column, and fields of
// the row object are columns (nested fields with dots: pos.x). `value` is the whole row value.
//
//   SELECT * | expr [AS name], ... | COUNT(*) FROM table [WHERE cond] [ORDER BY expr [ASC|DESC], ...] [LIMIT n [OFFSET m]]
//   INSERT INTO table (key, col, ...) VALUES (v, ...), (...)      -- no key column: a time-ordered push id
//   UPDATE table SET col = expr, ... [WHERE cond]                   -- col = NULL deletes the field
//   DELETE FROM table [WHERE cond]
//   SHOW TABLES [IN path]
//
// Expressions: literals ('text', 42, 1.5, TRUE, FALSE, NULL), columns, + - * / %, || (concat),
// = != <> < <= > >=, [NOT] LIKE 'a%', IS [NOT] NULL, [NOT] IN (...), AND, OR, NOT, parentheses,
// and functions COALESCE/IFNULL, LOWER, UPPER, LENGTH, ABS, ROUND(x[, digits]), NOW() (Unix ms).

/// <summary>What the engine needs from the app: reading the current view and writing changes.</summary>
public interface ISqlData
{
    object? Read(string[] path);
    void Write(string[] path, object? value);
    string NewKey();
}

public sealed class SqlException : Exception
{
    public SqlException(string message) : base(message) { }
}

public sealed class SqlResult
{
    public string Message = "";
    public DataTable? Table;
}

// ---- AST -------------------------------------------------------------------------------------

public abstract record Statement;
public sealed record SelectItem(Expr? Expr, string? Alias, bool Star, bool CountStar);
public sealed record SelectStmt(List<SelectItem> Items, string[] Table, Expr? Where, List<(Expr Expr, bool Desc)> OrderBy, int? Limit, int Offset) : Statement;
public sealed record InsertStmt(string[] Table, List<string> Columns, List<List<Expr>> Rows) : Statement;
public sealed record UpdateStmt(string[] Table, List<(string Column, Expr Value)> Sets, Expr? Where) : Statement;
public sealed record DeleteStmt(string[] Table, Expr? Where) : Statement;
public sealed record ShowTablesStmt(string[] Path) : Statement;

public abstract record Expr;
public sealed record Lit(object? Value) : Expr;
public sealed record Col(string Name) : Expr;
public sealed record Unary(string Op, Expr Operand) : Expr;
public sealed record Binary(string Op, Expr Left, Expr Right) : Expr;
public sealed record IsNull(Expr Operand, bool Negate) : Expr;
public sealed record Like(Expr Operand, Expr Pattern, bool Negate) : Expr;
public sealed record In(Expr Operand, List<Expr> Items, bool Negate) : Expr;
public sealed record Call(string Name, List<Expr> Args) : Expr;

// ---- tokenizer ---------------------------------------------------------------------------------

internal enum Tk { Ident, QuotedIdent, Str, Num, Sym, End }

internal sealed record Token(Tk Kind, string Text, object? Value, int Pos)
{
    public bool Is(string keyword) => Kind == Tk.Ident && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);
    public bool IsSym(string s) => Kind == Tk.Sym && Text == s;
}

internal static class Lexer
{
    public static List<Token> Lex(string sql)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            var start = i;
            if (char.IsLetter(c) || c == '_')
            {
                // Unquoted identifiers may contain '/' and '.', so table paths like rooms/lobby and columns like pos.x work.
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '/' or '.')) i++;
                tokens.Add(new Token(Tk.Ident, sql[start..i], null, start));
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < sql.Length && char.IsDigit(sql[i + 1])))
            {
                while (i < sql.Length && (char.IsDigit(sql[i]) || sql[i] == '.')) i++;
                if (i < sql.Length && sql[i] is 'e' or 'E')
                {
                    i++;
                    if (i < sql.Length && sql[i] is '+' or '-') i++;
                    while (i < sql.Length && char.IsDigit(sql[i])) i++;
                }
                var text = sql[start..i];
                // Cast to object first: a plain conditional would widen the long to double.
                object num = long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var l)
                    ? (object)l
                    : double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                tokens.Add(new Token(Tk.Num, text, num, start));
                continue;
            }
            if (c is '\'' or '"' or '`')
            {
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= sql.Length) throw new SqlException($"unterminated {(c == '\'' ? "string" : "identifier")} at {start}");
                    if (sql[i] == c)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == c) { sb.Append(c); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(sql[i++]);
                }
                tokens.Add(new Token(c == '\'' ? Tk.Str : Tk.QuotedIdent, sb.ToString(), sb.ToString(), start));
                continue;
            }
            var two = i + 1 < sql.Length ? sql.Substring(i, 2) : "";
            if (two is "<=" or ">=" or "<>" or "!=" or "||")
            {
                tokens.Add(new Token(Tk.Sym, two, null, start));
                i += 2;
                continue;
            }
            if ("=<>,()*+-/%;".IndexOf(c) >= 0)
            {
                tokens.Add(new Token(Tk.Sym, c.ToString(), null, start));
                i++;
                continue;
            }
            throw new SqlException($"unexpected character '{c}' at {i}");
        }
        tokens.Add(new Token(Tk.End, "", null, sql.Length));
        return tokens;
    }
}

// ---- parser ------------------------------------------------------------------------------------

public static class SqlParser
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "ORDER", "BY", "LIMIT", "OFFSET", "INSERT", "INTO", "VALUES", "UPDATE", "SET",
        "DELETE", "AND", "OR", "NOT", "LIKE", "IS", "NULL", "IN", "AS", "ASC", "DESC", "TRUE", "FALSE", "SHOW", "TABLES",
    };

    /// <summary>Parses one or more statements separated by semicolons.</summary>
    public static List<Statement> Parse(string sql)
    {
        var p = new Parser(Lexer.Lex(sql));
        var list = new List<Statement>();
        while (true)
        {
            while (p.Accept(";")) { }
            if (p.AtEnd) break;
            list.Add(p.Statement());
            if (!p.AtEnd && !p.Peek.IsSym(";")) throw p.Error("expected ';' or end of input");
        }
        return list;
    }

    private sealed class Parser
    {
        private readonly List<Token> _t;
        private int _i;

        public Parser(List<Token> tokens) => _t = tokens;

        public Token Peek => _t[_i];
        public bool AtEnd => Peek.Kind == Tk.End;

        public SqlException Error(string message) =>
            new($"{message} near {(AtEnd ? "end of input" : $"'{Peek.Text}'")} (position {Peek.Pos})");

        public bool Accept(string sym)
        {
            if (!Peek.IsSym(sym)) return false;
            _i++;
            return true;
        }

        private bool AcceptKw(string kw)
        {
            if (!Peek.Is(kw)) return false;
            _i++;
            return true;
        }

        private void Expect(string sym)
        {
            if (!Accept(sym)) throw Error($"expected '{sym}'");
        }

        private void ExpectKw(string kw)
        {
            if (!AcceptKw(kw)) throw Error($"expected {kw}");
        }

        private string Name()
        {
            var t = Peek;
            if (t.Kind == Tk.QuotedIdent || (t.Kind == Tk.Ident && !Reserved.Contains(t.Text)))
            {
                _i++;
                return t.Text;
            }
            throw Error("expected a name");
        }

        /// <summary>A column name in INSERT and SET lists, where even keywords such as `from` are allowed.</summary>
        private string ColumnName()
        {
            var t = Peek;
            if (t.Kind is not (Tk.Ident or Tk.QuotedIdent)) throw Error("expected a column name");
            _i++;
            return t.Text;
        }

        private string[] TableName()
        {
            var name = Name();
            try
            {
                return JsonUtil.ParsePath(name, allowDots: true);
            }
            catch (FormatException e)
            {
                throw new SqlException(e.Message);
            }
        }

        public Statement Statement()
        {
            if (AcceptKw("SELECT")) return Select();
            if (AcceptKw("INSERT")) return Insert();
            if (AcceptKw("UPDATE")) return Update();
            if (AcceptKw("DELETE")) return Delete();
            if (AcceptKw("SHOW"))
            {
                ExpectKw("TABLES");
                return new ShowTablesStmt(AcceptKw("IN") ? TableName() : Array.Empty<string>());
            }
            throw Error("expected SELECT, INSERT, UPDATE, DELETE or SHOW TABLES");
        }

        private SelectStmt Select()
        {
            var items = new List<SelectItem>();
            do
            {
                if (Accept("*")) items.Add(new SelectItem(null, null, true, false));
                else if (Peek.Is("COUNT") && _t[_i + 1].IsSym("("))
                {
                    _i += 2;
                    Expect("*");
                    Expect(")");
                    items.Add(new SelectItem(null, AcceptKw("AS") ? Name() : "count", false, true));
                }
                else
                {
                    var e = Expr();
                    items.Add(new SelectItem(e, AcceptKw("AS") ? Name() : null, false, false));
                }
            } while (Accept(","));
            ExpectKw("FROM");
            var table = TableName();
            Expr? where = AcceptKw("WHERE") ? Expr() : null;
            var order = new List<(Expr, bool)>();
            if (AcceptKw("ORDER"))
            {
                ExpectKw("BY");
                do
                {
                    var e = Expr();
                    var desc = AcceptKw("DESC");
                    if (!desc) AcceptKw("ASC");
                    order.Add((e, desc));
                } while (Accept(","));
            }
            int? limit = null;
            var offset = 0;
            if (AcceptKw("LIMIT")) limit = Int();
            if (AcceptKw("OFFSET")) offset = Int();
            return new SelectStmt(items, table, where, order, limit, offset);
        }

        private int Int()
        {
            if (Peek.Kind != Tk.Num || Peek.Value is not long l) throw Error("expected a whole number");
            _i++;
            return (int)l;
        }

        private InsertStmt Insert()
        {
            ExpectKw("INTO");
            var table = TableName();
            Expect("(");
            var cols = new List<string>();
            do cols.Add(ColumnName());
            while (Accept(","));
            Expect(")");
            ExpectKw("VALUES");
            var rows = new List<List<Expr>>();
            do
            {
                Expect("(");
                var row = new List<Expr>();
                do row.Add(Expr());
                while (Accept(","));
                Expect(")");
                if (row.Count != cols.Count) throw Error($"{cols.Count} columns but {row.Count} values");
                rows.Add(row);
            } while (Accept(","));
            return new InsertStmt(table, cols, rows);
        }

        private UpdateStmt Update()
        {
            var table = TableName();
            ExpectKw("SET");
            var sets = new List<(string, Expr)>();
            do
            {
                var col = ColumnName();
                Expect("=");
                sets.Add((col, Expr()));
            } while (Accept(","));
            return new UpdateStmt(table, sets, AcceptKw("WHERE") ? Expr() : null);
        }

        private DeleteStmt Delete()
        {
            ExpectKw("FROM");
            var table = TableName();
            return new DeleteStmt(table, AcceptKw("WHERE") ? Expr() : null);
        }

        /// <summary>Whether the reserved word at the cursor is followed by something only a value can be followed by.</summary>
        private bool LooksLikeColumn()
        {
            var next = _t[_i + 1];
            return next.Kind == Tk.End || next.Is("FROM") || next.Is("AS") || next.Is("IS") || next.Is("LIKE") || next.Is("IN")
                || (next.Kind == Tk.Sym && next.Text is "," or ")" or "=" or "!=" or "<" or "<=" or ">" or ">=" or "+" or "-" or "*" or "/" or "%" or "||" or ";");
        }

        // Precedence: OR < AND < NOT < comparison < + - || < * / % < unary minus < primary
        public Expr Expr() => Or();

        private Expr Or()
        {
            var e = And();
            while (AcceptKw("OR")) e = new Binary("OR", e, And());
            return e;
        }

        private Expr And()
        {
            var e = Not();
            while (AcceptKw("AND")) e = new Binary("AND", e, Not());
            return e;
        }

        private Expr Not() => AcceptKw("NOT") ? new Unary("NOT", Not()) : Comparison();

        private Expr Comparison()
        {
            var e = Additive();
            while (true)
            {
                foreach (var op in new[] { "=", "!=", "<>", "<=", ">=", "<", ">" })
                {
                    if (!Accept(op)) continue;
                    e = new Binary(op == "<>" ? "!=" : op, e, Additive());
                    goto next;
                }
                if (AcceptKw("IS"))
                {
                    var neg = AcceptKw("NOT");
                    ExpectKw("NULL");
                    e = new IsNull(e, neg);
                    continue;
                }
                var negate = false;
                if (Peek.Is("NOT") && (_t[_i + 1].Is("LIKE") || _t[_i + 1].Is("IN")))
                {
                    _i++;
                    negate = true;
                }
                if (AcceptKw("LIKE"))
                {
                    e = new Like(e, Additive(), negate);
                    continue;
                }
                if (AcceptKw("IN"))
                {
                    Expect("(");
                    var items = new List<Expr>();
                    do items.Add(Expr());
                    while (Accept(","));
                    Expect(")");
                    e = new In(e, items, negate);
                    continue;
                }
                return e;
            next:;
            }
        }

        private Expr Additive()
        {
            var e = Multiplicative();
            while (true)
            {
                if (Accept("+")) e = new Binary("+", e, Multiplicative());
                else if (Accept("-")) e = new Binary("-", e, Multiplicative());
                else if (Accept("||")) e = new Binary("||", e, Multiplicative());
                else return e;
            }
        }

        private Expr Multiplicative()
        {
            var e = UnaryMinus();
            while (true)
            {
                if (Accept("*")) e = new Binary("*", e, UnaryMinus());
                else if (Accept("/")) e = new Binary("/", e, UnaryMinus());
                else if (Accept("%")) e = new Binary("%", e, UnaryMinus());
                else return e;
            }
        }

        private Expr UnaryMinus() => Accept("-") ? new Unary("-", UnaryMinus()) : Primary();

        private Expr Primary()
        {
            var t = Peek;
            if (Accept("("))
            {
                var e = Expr();
                Expect(")");
                return e;
            }
            switch (t.Kind)
            {
                case Tk.Num:
                case Tk.Str:
                    _i++;
                    return new Lit(t.Value);
                case Tk.QuotedIdent:
                    _i++;
                    return new Col(t.Text);
                case Tk.Ident:
                    if (AcceptKw("NULL")) return new Lit(null);
                    if (AcceptKw("TRUE")) return new Lit(true);
                    if (AcceptKw("FALSE")) return new Lit(false);
                    if (!Reserved.Contains(t.Text) && _t[_i + 1].IsSym("("))
                    {
                        _i += 2;
                        var args = new List<Expr>();
                        if (!Accept(")"))
                        {
                            do args.Add(Expr());
                            while (Accept(","));
                            Expect(")");
                        }
                        return new Call(t.Text.ToUpperInvariant(), args);
                    }
                    if (Reserved.Contains(t.Text) && LooksLikeColumn())
                    {
                        // A keyword used as a field name, e.g. `SELECT from, text FROM chat` or `WHERE from = 'Ann'`.
                        _i++;
                        return new Col(t.Text);
                    }
                    return new Col(Name());
                default:
                    throw Error("expected a value or column");
            }
        }
    }
}

// ---- executor ----------------------------------------------------------------------------------

public static class SqlExecutor
{
    private sealed record Row(string Key, object? Value);

    public static SqlResult Execute(Statement stmt, ISqlData data) => stmt switch
    {
        SelectStmt s => Select(s, data),
        InsertStmt s => Insert(s, data),
        UpdateStmt s => Update(s, data),
        DeleteStmt s => Delete(s, data),
        ShowTablesStmt s => ShowTables(s, data),
        _ => throw new SqlException("unsupported statement"),
    };

    private static List<Row> Rows(string[] table, ISqlData data)
    {
        var value = data.Read(table);
        return value switch
        {
            null => new List<Row>(),
            Dictionary<string, object?> d => d.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new Row(kv.Key, kv.Value)).ToList(),
            _ => throw new SqlException($"\"{JsonUtil.Join(table)}\" is a single value, not a table (an object of rows)"),
        };
    }

    private static List<Row> Where(List<Row> rows, Expr? where) =>
        where == null ? rows : rows.Where(r => Eval(where, r) is true).ToList();

    private static SqlResult Select(SelectStmt s, ISqlData data)
    {
        var rows = Where(Rows(s.Table, data), s.Where);
        // `*` columns come from all matching rows in key order, so they don't depend on ORDER BY or LIMIT.
        var starFields = rows.Where(r => r.Value is Dictionary<string, object?>)
            .SelectMany(r => ((Dictionary<string, object?>)r.Value!).Keys).Distinct().ToList();
        var anyScalar = rows.Any(r => r.Value is not Dictionary<string, object?>);
        if (s.Items.Any(i => i.CountStar))
        {
            if (s.Items.Count != 1) throw new SqlException("COUNT(*) can't be combined with other columns (there is no GROUP BY)");
            var t = new DataTable();
            t.Columns.Add(s.Items[0].Alias ?? "count");
            t.Rows.Add(rows.Count.ToString(CultureInfo.InvariantCulture));
            return new SqlResult { Table = t, Message = "1 row" };
        }
        if (s.OrderBy.Count > 0)
        {
            rows.Sort((a, b) =>
            {
                foreach (var (e, desc) in s.OrderBy)
                {
                    var c = CompareForSort(Eval(e, a), Eval(e, b));
                    if (c != 0) return desc ? -c : c;
                }
                return 0;
            });
        }
        var total = rows.Count;
        rows = rows.Skip(s.Offset).Take(s.Limit ?? int.MaxValue).ToList();

        // Columns: `*` expands to key + the union of the rows' top-level fields (or key + value for scalar rows).
        var columns = new List<(string Name, Func<Row, object?> Get)>();
        foreach (var item in s.Items)
        {
            if (item.Star)
            {
                columns.Add(("key", r => r.Key));
                foreach (var f in starFields) columns.Add((f, r => JsonUtil.GetIn(r.Value, new[] { f })));
                if (anyScalar) columns.Add(("value", r => r.Value));
            }
            else
            {
                var expr = item.Expr!;
                columns.Add((item.Alias ?? (expr is Col c ? c.Name : "expr"), r => Eval(expr, r)));
            }
        }
        var table = new DataTable();
        foreach (var (name, _) in columns)
        {
            var unique = name;
            for (var n = 2; table.Columns.Contains(unique); n++) unique = $"{name}_{n}";
            table.Columns.Add(unique);
        }
        foreach (var r in rows) table.Rows.Add(columns.Select(c => (object)JsonUtil.Cell(c.Get(r), 1000)).ToArray());
        return new SqlResult { Table = table, Message = rows.Count == total ? $"{rows.Count} row(s)" : $"{rows.Count} of {total} row(s)" };
    }

    private static SqlResult Insert(InsertStmt s, ISqlData data)
    {
        var existing = data.Read(s.Table);
        if (existing != null && existing is not Dictionary<string, object?>)
            throw new SqlException($"\"{JsonUtil.Join(s.Table)}\" is a single value, not a table");
        var used = new HashSet<string>(existing is Dictionary<string, object?> d ? d.Keys : Enumerable.Empty<string>());
        var empty = new Row("", null);
        foreach (var values in s.Rows)
        {
            string? key = null;
            object? row = null;
            for (var i = 0; i < s.Columns.Count; i++)
            {
                var col = s.Columns[i];
                var v = Eval(values[i], empty);
                if (col.Equals("key", StringComparison.OrdinalIgnoreCase))
                    key = v switch { null => null, string str => str, _ => JsonUtil.Cell(v) };
                else if (col.Equals("value", StringComparison.OrdinalIgnoreCase))
                    row = v;
                else
                    row = JsonUtil.SetIn(row, ColumnPath(col), v);
            }
            key ??= data.NewKey();
            JsonUtil.ParsePath(key); // validates the key
            if (!used.Add(key)) throw new SqlException($"a row with key \"{key}\" already exists in {JsonUtil.Join(s.Table)} (use UPDATE)");
            if (row == null) throw new SqlException("a row needs at least one non-null column");
            data.Write(s.Table.Append(key).ToArray(), row);
        }
        return new SqlResult { Message = $"{s.Rows.Count} row(s) inserted" };
    }

    private static SqlResult Update(UpdateStmt s, ISqlData data)
    {
        var rows = Where(Rows(s.Table, data), s.Where);
        var changed = 0;
        foreach (var r in rows)
        {
            // Evaluate every SET against the row as it was, then write only what actually changes.
            var writes = s.Sets.Select(set => (set.Column, Value: Eval(set.Value, r))).ToList();
            var rowChanged = false;
            foreach (var (col, value) in writes)
            {
                if (col.Equals("key", StringComparison.OrdinalIgnoreCase)) throw new SqlException("the key column can't be updated (insert a new row and delete the old one)");
                var isWhole = col.Equals("value", StringComparison.OrdinalIgnoreCase);
                var rel = isWhole ? Array.Empty<string>() : ColumnPath(col);
                if (JsonUtil.DeepEquals(JsonUtil.GetIn(r.Value, rel), JsonUtil.Normalize(value))) continue;
                data.Write(s.Table.Append(r.Key).Concat(rel).ToArray(), value);
                rowChanged = true;
            }
            if (rowChanged) changed++;
        }
        return new SqlResult { Message = changed == rows.Count ? $"{rows.Count} row(s) updated" : $"{rows.Count} row(s) matched, {changed} changed" };
    }

    private static SqlResult Delete(DeleteStmt s, ISqlData data)
    {
        var rows = Where(Rows(s.Table, data), s.Where);
        foreach (var r in rows) data.Write(s.Table.Append(r.Key).ToArray(), null);
        return new SqlResult { Message = $"{rows.Count} row(s) deleted" };
    }

    private static SqlResult ShowTables(ShowTablesStmt s, ISqlData data)
    {
        var t = new DataTable();
        t.Columns.Add("table");
        t.Columns.Add("rows");
        t.Columns.Add("kind");
        if (data.Read(s.Path) is Dictionary<string, object?> d)
        {
            foreach (var (k, v) in d.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var path = JsonUtil.Join(s.Path.Append(k));
                if (v is Dictionary<string, object?> rows) t.Rows.Add(path, rows.Count.ToString(CultureInfo.InvariantCulture), "table");
                else t.Rows.Add(path, "", "value: " + JsonUtil.Cell(v, 60));
            }
        }
        return new SqlResult { Table = t, Message = $"{t.Rows.Count} entr{(t.Rows.Count == 1 ? "y" : "ies")}" };
    }

    private static string[] ColumnPath(string column)
    {
        try
        {
            return JsonUtil.ParsePath(column, allowDots: true);
        }
        catch (FormatException e)
        {
            throw new SqlException(e.Message);
        }
    }

    // ---- expression evaluation (SQL-style: comparisons with NULL are unknown, i.e. null) ----

    private static object? Eval(Expr e, Row r)
    {
        switch (e)
        {
            case Lit l:
                return l.Value;
            case Col c:
                if (c.Name.Equals("key", StringComparison.OrdinalIgnoreCase)) return r.Key;
                if (c.Name.Equals("value", StringComparison.OrdinalIgnoreCase)) return r.Value;
                return JsonUtil.GetIn(r.Value, ColumnPath(c.Name));
            case Unary { Op: "NOT" } u:
                return Eval(u.Operand, r) switch { true => false, false => true, _ => null };
            case Unary { Op: "-" } u:
                return Eval(u.Operand, r) switch
                {
                    long l => -l,
                    double d => -d,
                    null => null,
                    var v => throw new SqlException($"can't negate {JsonUtil.Cell(v)}"),
                };
            case IsNull n:
                return (Eval(n.Operand, r) == null) != n.Negate;
            case Like like:
            {
                if (Eval(like.Operand, r) is not { } v || Eval(like.Pattern, r) is not string pattern) return null;
                var regex = "^" + Regex.Escape(pattern).Replace("%", ".*").Replace("_", ".") + "$";
                return Regex.IsMatch(JsonUtil.Cell(v, int.MaxValue), regex, RegexOptions.IgnoreCase | RegexOptions.Singleline) != like.Negate;
            }
            case In inExpr:
            {
                var v = Eval(inExpr.Operand, r);
                if (v == null) return null;
                var found = inExpr.Items.Any(i => Compare(v, Eval(i, r)) == 0);
                return found != inExpr.Negate;
            }
            case Call call:
                return CallFunction(call, r);
            case Binary { Op: "AND" } b:
            {
                var l = Eval(b.Left, r);
                if (l is false) return false;
                var rv = Eval(b.Right, r);
                if (rv is false) return false;
                return l is true && rv is true ? true : null;
            }
            case Binary { Op: "OR" } b:
            {
                var l = Eval(b.Left, r);
                if (l is true) return true;
                var rv = Eval(b.Right, r);
                if (rv is true) return true;
                return l is false && rv is false ? false : null;
            }
            case Binary b:
            {
                var l = Eval(b.Left, r);
                var rv = Eval(b.Right, r);
                switch (b.Op)
                {
                    case "=": return Compare(l, rv) is int c0 ? c0 == 0 : null;
                    case "!=": return Compare(l, rv) is int c1 ? c1 != 0 : null;
                    case "<": return Compare(l, rv) is int c2 ? c2 < 0 : null;
                    case "<=": return Compare(l, rv) is int c3 ? c3 <= 0 : null;
                    case ">": return Compare(l, rv) is int c4 ? c4 > 0 : null;
                    case ">=": return Compare(l, rv) is int c5 ? c5 >= 0 : null;
                    case "||": return l == null || rv == null ? null : JsonUtil.Cell(l, int.MaxValue) + JsonUtil.Cell(rv, int.MaxValue);
                    default: return Arithmetic(b.Op, l, rv);
                }
            }
            default:
                throw new SqlException("unsupported expression");
        }
    }

    private static object? CallFunction(Call call, Row r)
    {
        var args = call.Args.Select(a => Eval(a, r)).ToList();
        void Arity(int min, int max)
        {
            if (args.Count < min || args.Count > max)
                throw new SqlException(min == max ? $"{call.Name} takes {min} argument(s)" : $"{call.Name} takes {min} to {max} arguments");
        }
        switch (call.Name)
        {
            case "COALESCE":
            case "IFNULL":
                if (args.Count == 0) throw new SqlException($"{call.Name} needs at least one argument");
                return args.FirstOrDefault(a => a != null);
            case "LOWER":
                Arity(1, 1);
                return args[0] == null ? null : JsonUtil.Cell(args[0], int.MaxValue).ToLowerInvariant();
            case "UPPER":
                Arity(1, 1);
                return args[0] == null ? null : JsonUtil.Cell(args[0], int.MaxValue).ToUpperInvariant();
            case "LENGTH":
                Arity(1, 1);
                return args[0] switch
                {
                    null => null,
                    string s => (long)s.Length,
                    Dictionary<string, object?> d => (long)d.Count,
                    List<object?> l => (long)l.Count,
                    var v => (long)JsonUtil.Cell(v, int.MaxValue).Length,
                };
            case "ABS":
                Arity(1, 1);
                return args[0] switch
                {
                    null => null,
                    long l => Math.Abs(l),
                    double d => Math.Abs(d),
                    var v => throw new SqlException($"ABS needs a number, got {JsonUtil.Cell(v)}"),
                };
            case "ROUND":
            {
                Arity(1, 2);
                if (args[0] == null) return null;
                if (!JsonUtil.IsNumber(args[0])) throw new SqlException($"ROUND needs a number, got {JsonUtil.Cell(args[0])}");
                var digits = args.Count > 1 && args[1] is long dl ? (int)dl : 0;
                var rounded = Math.Round(JsonUtil.ToDouble(args[0]), digits, MidpointRounding.AwayFromZero);
                return digits <= 0 ? (long)rounded : rounded;
            }
            case "NOW":
                Arity(0, 0);
                return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            default:
                throw new SqlException($"unknown function {call.Name}");
        }
    }

    private static object? Arithmetic(string op, object? l, object? r)
    {
        if (l == null || r == null) return null;
        if (op == "+" && (l is string || r is string)) return JsonUtil.Cell(l, int.MaxValue) + JsonUtil.Cell(r, int.MaxValue);
        if (!JsonUtil.IsNumber(l) || !JsonUtil.IsNumber(r))
            throw new SqlException($"'{op}' needs numbers, got {JsonUtil.Cell(l)} and {JsonUtil.Cell(r)}");
        if (l is long a && r is long b && op != "/")
        {
            return op switch
            {
                "+" => a + b,
                "-" => a - b,
                "*" => a * b,
                "%" => b == 0 ? null : a % b,
                _ => throw new SqlException($"unknown operator {op}"),
            };
        }
        double x = JsonUtil.ToDouble(l), y = JsonUtil.ToDouble(r);
        object? result = op switch
        {
            "+" => x + y,
            "-" => x - y,
            "*" => x * y,
            "/" => y == 0 ? null : x / y,
            "%" => y == 0 ? null : x % y,
            _ => throw new SqlException($"unknown operator {op}"),
        };
        // Keep whole results as integers (e.g. 10 / 2 = 5) so they don't turn into 5.0 in the document.
        return result is double dr && dr == Math.Floor(dr) && Math.Abs(dr) < 9e15 ? (long)dr : result;
    }

    /// <summary>Compares two values; null when either is null or the types can't be compared.</summary>
    private static int? Compare(object? a, object? b)
    {
        if (a == null || b == null) return null;
        if (JsonUtil.IsNumber(a) && JsonUtil.IsNumber(b)) return JsonUtil.ToDouble(a).CompareTo(JsonUtil.ToDouble(b));
        if (a is string sa && b is string sb) return string.CompareOrdinal(sa, sb);
        if (a is bool ba && b is bool bb) return ba.CompareTo(bb);
        if (a is string || b is string) return string.CompareOrdinal(JsonUtil.Cell(a, int.MaxValue), JsonUtil.Cell(b, int.MaxValue));
        return JsonUtil.DeepEquals(a, b) ? 0 : null;
    }

    /// <summary>Total order for ORDER BY: nulls first, then numbers, strings, booleans, objects.</summary>
    private static int CompareForSort(object? a, object? b)
    {
        static int Rank(object? v) => v switch { null => 0, long or double => 1, string => 2, bool => 3, _ => 4 };
        var ra = Rank(a);
        var rb = Rank(b);
        if (ra != rb) return ra.CompareTo(rb);
        return Compare(a, b) ?? string.CompareOrdinal(JsonUtil.Cell(a, int.MaxValue), JsonUtil.Cell(b, int.MaxValue));
    }
}
