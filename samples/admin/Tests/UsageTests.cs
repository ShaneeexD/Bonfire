using Bonfire.Json;
using BonfireAdmin;
using Xunit;

namespace BonfireAdmin.Tests;

public class UsageTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 18, 30, 0, DateTimeKind.Utc);

    private const string Stats = """
        {"room":"visual-demo","rev":2000005,"chunkDepth":2,"databaseBytes":2500000,"connections":3,"writeQuotaUntil":null,
         "limits":{"roomDailyWriteRows":25000,"maxRoomBytes":50000000},"resetsAt":"2026-11-06T00:00:00.000Z",
         "today":{"day":"2026-11-05","writes":120,"rows":20000,"connects":40,"frames":2001,"denied":2,"quota":1},
         "days":[{"day":"2026-11-05","writes":120,"rows":20000,"connects":40,"frames":2001,"denied":2,"quota":1},
                 {"day":"2026-11-04","writes":7,"rows":12,"connects":3,"frames":9,"denied":0,"quota":0}]}
        """;

    private static Meter Find(Usage u, string label) => u.Meters.Single(m => m.Label.StartsWith(label));

    [Fact]
    public void MetersCompareCountersWithRoomAndAccountLimits()
    {
        var u = UsageModel.From(BonfireJson.Parse(Stats), Now);
        Assert.Equal("Room 'visual-demo' · 2026-11-05 (UTC) · resets in 5h 30m", u.Title);

        var budget = u.Headline;
        Assert.Equal(20000, budget.Value);
        Assert.Equal(25000, budget.Max);
        Assert.Equal("20,000 / 25,000  (80%)", budget.Text);

        Assert.Equal("20,000 / 100,000  (20%)", Find(u, "Share of the account").Text);
        Assert.Equal("2.5 MB / 50 MB  (5.0%)", Find(u, "Room storage").Text);
        // 40 connections + 2001 messages / 20 (rounded up) = 141 billed requests.
        Assert.Equal(141, Find(u, "≈ Durable Object requests").Value);
        Assert.Null(Find(u, "Write operations").Max);
        Assert.Equal("2 by rules/limits · 1 by quotas", Find(u, "Refused").Text);
        Assert.Equal(2, u.History.Count);
        Assert.Equal(new[] { "2026-11-04", "7", "12", "3", "9", "0", "0" }, u.History[1]);
        Assert.Contains("approximate", u.Note);
    }

    [Fact]
    public void OlderServersWithoutLimitsFallBackToDefaults()
    {
        var u = UsageModel.From(BonfireJson.Parse("""{"room":"r","databaseBytes":0,"connections":0,"today":{"rows":5}}"""), Now);
        Assert.Equal(25000, u.Headline.Max);
        Assert.Equal(50_000_000, Find(u, "Room storage").Max);
        Assert.Contains("resets in 5h 30m", u.Title); // next UTC midnight when the server doesn't say
        Assert.Empty(u.History);
    }

    [Fact]
    public void ExhaustedWriteQuotaIsExplained()
    {
        var json = Stats.Replace("\"writeQuotaUntil\":null", "\"writeQuotaUntil\":\"2026-11-06T00:00:00.000Z\"");
        Assert.Contains("read-only until", UsageModel.From(BonfireJson.Parse(json), Now).Note);
    }

    [Theory]
    [InlineData(0.5, 76)]   // green
    [InlineData(0.75, 230)] // orange
    [InlineData(0.95, 214)] // red
    public void BarColourFollowsTheFraction(double fraction, int red) => Assert.Equal(red, MeterBar.ColorFor(fraction).R);

    [Fact]
    public void RejectsUnexpectedResponses() => Assert.Throws<FormatException>(() => UsageModel.From(BonfireJson.Parse("[1]"), Now));

    // Shape of a real response from this account on 2026-11-05.
    private const string Analytics = """
        {"data":{"viewer":{"accounts":[{
          "doInvocations":[{"dimensions":{"type":"http"},"sum":{"requests":21}},{"dimensions":{"type":"hibernation"},"sum":{"requests":6638}}],
          "doPeriodic":[{"sum":{"duration":1.6645785599999998,"rowsRead":210,"rowsWritten":1495}}],
          "sqlStorage":[{"dimensions":{"date":"2026-11-04","namespaceId":"a"},"max":{"storedBytes":90000}},
                        {"dimensions":{"date":"2026-11-05","namespaceId":"a"},"max":{"storedBytes":102400}},
                        {"dimensions":{"date":"2026-11-05","namespaceId":"b"},"max":{"storedBytes":4096}}],
          "workers":[{"sum":{"requests":22}}]}]}},"errors":null}
        """;

    [Fact]
    public void AccountUsageAppliesBillingRulesAndSumsStorage()
    {
        var u = AccountUsageModel.Parse(BonfireJson.Parse(Analytics));
        Assert.Equal(22, u.WorkerRequests);
        Assert.Equal(21 + 332, u.DoRequests);          // hibernated WebSocket messages bill at 20:1 (6638/20 rounded up)
        Assert.Equal(6638 - 332, u.DoRequestsUnbilled);
        Assert.Equal(1495, u.RowsWritten);
        Assert.Equal(210, u.RowsRead);
        Assert.Equal(1.66, u.DurationGbSeconds, 2);
        Assert.Equal(102400 + 4096, u.StoredBytes);    // latest size per namespace, summed

        var meters = AccountUsageModel.Meters(u);
        Assert.Equal("Rows written (all rooms)", meters[0].Label);
        Assert.Equal("1,495 / 100,000  (1.5%)", meters[0].Text);
        Assert.Equal("1.66 GB-s / 13,000 GB-s  (0.0%)", meters.Single(m => m.Label == "Duration").Text);
    }

    [Fact]
    public void AccountUsageReportsApiErrors()
    {
        var e = Assert.Throws<FormatException>(() => AccountUsageModel.Parse(BonfireJson.Parse("""{"data":null,"errors":[{"message":"not authorized for that account"}]}""")));
        Assert.Contains("not authorized for that account", e.Message);
        Assert.Throws<FormatException>(() => AccountUsageModel.Parse(BonfireJson.Parse("""{"data":{"viewer":{"accounts":[]}},"errors":null}""")));
    }
}
