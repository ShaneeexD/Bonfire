using System.Text;
using FreeSync.Json;

namespace FreeSyncAdmin;

/// <summary>Today's (UTC) account-wide usage, as Cloudflare's GraphQL Analytics API reports it.</summary>
public sealed record AccountUsage(
    double WorkerRequests,
    double DoRequests,
    double DoRequestsUnbilled,
    double RowsWritten,
    double RowsRead,
    double DurationGbSeconds,
    double StoredBytes);

/// <summary>
/// Queries account-wide usage with an API token that has "Account Analytics: Read". The token is only sent to
/// api.cloudflare.com; the Worker never sees it. Field names were taken from the API's own schema
/// (introspection), because the documentation doesn't list rows or duration for Durable Objects.
/// </summary>
public static class AccountUsageModel
{
    // Workers Free plan daily limits (docs/LIMITS.md). Storage is an account total, not per day.
    public const double WorkerRequestsPerDay = 100_000;
    public const double DoRequestsPerDay = 100_000;
    public const double RowsWrittenPerDay = 100_000;
    public const double RowsReadPerDay = 5_000_000;
    public const double DurationGbSecondsPerDay = 13_000;
    public const double StorageBytes = 5_000_000_000;

    public const string Query = """
        query($a: string!, $d: Date!, $since: Time!) { viewer { accounts(filter: {accountTag: $a}) {
          workers: workersInvocationsAdaptive(limit: 10000, filter: {datetime_geq: $since}) { sum { requests } }
          doInvocations: durableObjectsInvocationsAdaptiveGroups(limit: 10000, filter: {date_geq: $d}) { sum { requests } dimensions { type } }
          doPeriodic: durableObjectsPeriodicGroups(limit: 10000, filter: {date_geq: $d}) { sum { rowsRead rowsWritten duration } }
          sqlStorage: durableObjectsSqlStorageGroups(limit: 10000, filter: {date_geq: $d}) { max { storedBytes } dimensions { namespaceId date } }
        } } }
        """;

    public static async Task<AccountUsage> FetchAsync(HttpClient http, string token, string accountId, DateTime nowUtc, CancellationToken ct = default)
    {
        var day = nowUtc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var body = FsJson.Serialize(new Dictionary<string, object?>
        {
            ["query"] = Query,
            ["variables"] = new Dictionary<string, object?> { ["a"] = accountId.Trim(), ["d"] = day, ["since"] = day + "T00:00:00Z" },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.cloudflare.com/client/v4/graphql")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token.Trim());
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if ((int)res.StatusCode is 401 or 403) throw new FormatException("Cloudflare refused the API token (it needs Account → Account Analytics → Read).");
        if (!res.IsSuccessStatusCode) throw new FormatException($"Cloudflare analytics returned HTTP {(int)res.StatusCode}.");
        return Parse(FsJson.Parse(text));
    }

    /// <summary>Reads the GraphQL response. Throws <see cref="FormatException"/> with the API's message on errors.</summary>
    public static AccountUsage Parse(object? json)
    {
        var root = json as Dictionary<string, object?> ?? throw new FormatException("unexpected analytics response");
        if (root.TryGetValue("errors", out var errs) && errs is List<object?> { Count: > 0 } list)
        {
            var messages = list.OfType<Dictionary<string, object?>>().Select(e => e.TryGetValue("message", out var m) ? m as string : null).Where(m => m != null);
            throw new FormatException("Cloudflare analytics: " + string.Join("; ", messages));
        }
        var accounts = Path(root, "data", "viewer", "accounts") as List<object?>;
        if (accounts is not { Count: > 0 } || accounts[0] is not Dictionary<string, object?> account)
            throw new FormatException("Cloudflare analytics returned no data for this account ID.");

        double workers = Rows(account, "workers").Sum(r => Num(r, "sum", "requests"));

        // Billing counts WebSocket messages handled by hibernatable sockets at 20:1; analytics counts every message.
        double billed = 0, unbilled = 0;
        foreach (var r in Rows(account, "doInvocations"))
        {
            var n = Num(r, "sum", "requests");
            if (Path(r, "dimensions", "type") as string is "hibernation" or "websocket")
            {
                billed += Math.Ceiling(n / 20);
                unbilled += n - Math.Ceiling(n / 20);
            }
            else
            {
                billed += n;
            }
        }

        var periodic = Rows(account, "doPeriodic").ToList();
        // Storage: each namespace's latest size, summed.
        var stored = Rows(account, "sqlStorage")
            .GroupBy(r => Path(r, "dimensions", "namespaceId") as string ?? "")
            .Sum(g => g.OrderBy(r => Path(r, "dimensions", "date") as string).Last() is var last ? Num(last, "max", "storedBytes") : 0);

        return new AccountUsage(
            workers,
            billed,
            unbilled,
            periodic.Sum(r => Num(r, "sum", "rowsWritten")),
            periodic.Sum(r => Num(r, "sum", "rowsRead")),
            periodic.Sum(r => Num(r, "sum", "duration")),
            stored);
    }

    public static IReadOnlyList<Meter> Meters(AccountUsage u)
    {
        string N(double n) => UsageModel.N(n);
        Meter Bar(string label, double value, double max, Func<double, string> fmt, string hint)
        {
            var pct = max > 0 ? value / max * 100 : 0;
            return new Meter(label, value, max, $"{fmt(value)} / {fmt(max)}  ({pct.ToString(pct < 10 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture)}%)", hint);
        }
        return new[]
        {
            Bar("Rows written (all rooms)", u.RowsWritten, RowsWrittenPerDay, N,
                "SQLite rows written by every Durable Object today. The usual first limit to hit; writes fail until 00:00 UTC."),
            Bar("Durable Object requests", u.DoRequests, DoRequestsPerDay, N,
                $"Connections, HTTP requests and WebSocket messages (counted 20:1 as billed). Raw messages not billed: {N(u.DoRequestsUnbilled)}."),
            Bar("Worker requests", u.WorkerRequests, WorkerRequestsPerDay, N,
                "Every HTTP request to the Worker: connection upgrades, snapshot and stats reads."),
            Bar("Rows read (all rooms)", u.RowsRead, RowsReadPerDay, N, "SQLite rows read by every Durable Object today."),
            Bar("Duration", u.DurationGbSeconds, DurationGbSecondsPerDay, v => v.ToString(v < 100 ? "0.##" : "#,0", System.Globalization.CultureInfo.InvariantCulture) + " GB-s",
                "Wall-clock time Durable Objects were awake × 128 MB. Hibernation keeps this low."),
            Bar("SQLite storage (total)", u.StoredBytes, StorageBytes, UsageModel.Bytes, "All Durable Object storage on the account (not a daily limit)."),
        };
    }

    private static IEnumerable<Dictionary<string, object?>> Rows(Dictionary<string, object?> account, string key) =>
        account.TryGetValue(key, out var v) && v is List<object?> l ? l.OfType<Dictionary<string, object?>>() : Enumerable.Empty<Dictionary<string, object?>>();

    private static object? Path(object? node, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (node is not Dictionary<string, object?> d || !d.TryGetValue(k, out node)) return null;
        }
        return node;
    }

    private static double Num(object? node, params string[] keys) => Path(node, keys) switch { long l => l, double d => d, _ => 0 };
}
