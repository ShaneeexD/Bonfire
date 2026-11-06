using System.Globalization;

namespace BonfireAdmin;

/// <summary>One bar on the usage panel. <see cref="Max"/> null means "just a count".</summary>
public sealed record Meter(string Label, double Value, double? Max, string Text, string Hint);

/// <summary>The usage panel's content, built from the room's /stats response.</summary>
public sealed record Usage(string Title, IReadOnlyList<Meter> Meters, IReadOnlyList<string[]> History, string Note)
{
    /// <summary>The bar shown in the top bar: the room's daily write budget.</summary>
    public Meter Headline => Meters[0];
}

/// <summary>Turns the server's per-room counters into meters against the room and free-plan limits.</summary>
public static class UsageModel
{
    // Workers Free plan, per account per UTC day (see docs/LIMITS.md).
    public const double AccountRowsWrittenPerDay = 100_000;
    public const double AccountRequestsPerDay = 100_000;
    public const double DefaultRoomDailyWriteRows = 25_000;
    public const double DefaultMaxRoomBytes = 50_000_000;

    public static Usage From(object? stats, DateTime nowUtc)
    {
        var s = stats as Dictionary<string, object?> ?? throw new FormatException("unexpected stats response");
        var today = Dict(s, "today");
        var limits = Dict(s, "limits");
        var room = Str(s, "room") ?? "?";

        var rows = Num(today, "rows");
        var writes = Num(today, "writes");
        var connects = Num(today, "connects");
        var frames = Num(today, "frames");
        var denied = Num(today, "denied");
        var quota = Num(today, "quota");
        var rowBudget = limits.Count > 0 ? Num(limits, "roomDailyWriteRows") : DefaultRoomDailyWriteRows;
        var maxBytes = limits.Count > 0 ? Num(limits, "maxRoomBytes") : DefaultMaxRoomBytes;
        var bytes = Num(s, "databaseBytes");
        // Billing counts incoming WebSocket messages at 20:1; each connection costs one request too.
        var doRequests = connects + Math.Ceiling(frames / 20);

        var resetsAt = DateTime.TryParse(Str(s, "resetsAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var r)
            ? r
            : nowUtc.Date.AddDays(1);
        var left = resetsAt - nowUtc;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        var day = Str(today, "day") ?? nowUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var meters = new List<Meter>
        {
            Bar("Rows written today (room budget)", rows, rowBudget, n => N(n),
                "Stored rows this room wrote today. At the budget, writes fail with room_quota until reset (ROOM_DAILY_WRITE_ROWS)."),
            Bar("Share of the account's rows written", rows, AccountRowsWrittenPerDay, n => N(n),
                "The free plan allows 100,000 rows written per day for the WHOLE account, shared by every room."),
            Bar("Room storage", bytes, maxBytes, Bytes,
                "Database size of this room. At the cap, writes that add data fail with room_full (MAX_ROOM_BYTES)."),
            Bar("≈ Durable Object requests from this room", doRequests, AccountRequestsPerDay, n => N(n),
                "Connections + incoming messages ÷ 20, against the account's 100,000/day. Snapshot and stats reads are not counted."),
            new Meter("Write operations today", writes, null, N(writes), "Accepted set/update/remove operations (including ephemeral and onDisconnect)."),
            new Meter("Connections today", connects, null, N(connects), "WebSocket connections opened to this room today."),
            new Meter("Incoming messages today", frames, null, N(frames), "Frames received (each can carry many operations)."),
            new Meter("Live connections now", Num(s, "connections"), null, N(Num(s, "connections")), "Clients connected right now."),
            new Meter("Refused today", denied + quota, null, $"{N(denied)} by rules/limits · {N(quota)} by quotas", "Operations the server refused."),
        };

        var history = new List<string[]>();
        if (s.TryGetValue("days", out var daysObj) && daysObj is List<object?> days)
        {
            foreach (var d in days.OfType<Dictionary<string, object?>>())
                history.Add(new[] { Str(d, "day") ?? "", N(Num(d, "writes")), N(Num(d, "rows")), N(Num(d, "connects")), N(Num(d, "frames")), N(Num(d, "denied")), N(Num(d, "quota")) });
        }

        var quotaUntil = Str(s, "writeQuotaUntil");
        var note = quotaUntil != null
            ? $"The account's storage-write quota is used up; this room is read-only until {quotaUntil}."
            : "Counters are approximate: they can undercount slightly when the room sleeps. Cloudflare's dashboard has exact account totals.";
        var title = $"Room '{room}' · {day} (UTC) · resets in {(int)left.TotalHours}h {left.Minutes:D2}m";
        return new Usage(title, meters, history, note);
    }

    private static Meter Bar(string label, double value, double max, Func<double, string> fmt, string hint)
    {
        var pct = max > 0 ? value / max * 100 : 0;
        return new Meter(label, value, max, $"{fmt(value)} / {fmt(max)}  ({pct.ToString(pct < 10 ? "0.0" : "0", CultureInfo.InvariantCulture)}%)", hint);
    }

    private static Dictionary<string, object?> Dict(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is Dictionary<string, object?> x ? x : new Dictionary<string, object?>();

    private static string? Str(Dictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) ? v as string : null;

    private static double Num(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) ? v switch { long l => l, double x => x, _ => 0 } : 0;

    public static string N(double n) => n.ToString("#,0", CultureInfo.InvariantCulture);

    public static string Bytes(double b) => b switch
    {
        >= 1_000_000_000 => (b / 1_000_000_000).ToString("0.##", CultureInfo.InvariantCulture) + " GB",
        >= 1_000_000 => (b / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        >= 1_000 => (b / 1_000).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => b.ToString("0", CultureInfo.InvariantCulture) + " B",
    };
}
