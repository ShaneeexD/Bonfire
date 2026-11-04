using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FreeSync.Json;
using Xunit;

namespace FreeSync.Tests
{
    public class ClientTests
    {
        private static (FreeSyncClient Client, MockServer Server) Create(Action<FreeSyncOptions>? configure = null)
        {
            var server = new MockServer();
            var opts = new FreeSyncOptions
            {
                SocketFactory = server.Create,
                EventDispatch = EventDispatchMode.Immediate,
                WriteInterval = TimeSpan.FromMilliseconds(20),
                ReconnectMinDelay = TimeSpan.FromMilliseconds(10),
                ReconnectMaxDelay = TimeSpan.FromMilliseconds(50),
            };
            configure?.Invoke(opts);
            return (new FreeSyncClient("wss://example.test", "global", "read-key", opts), server);
        }

        private static async Task Eventually(Func<bool> condition, int timeoutMs = 3000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
                await Task.Delay(5);
            }
        }

        private static long L(object? o) => Convert.ToInt64(o);

        [Fact]
        public async Task ConnectsWithKeyTokenAndVersion()
        {
            var (db, server) = Create(o => o.ClientToken = "my-secret-token-123456");
            using var _ = db;
            Assert.Equal("jghcqgrt4btbgyfs", db.ClientId); // known before connecting
            await server.Accept(cid: "jghcqgrt4btbgyfs");
            await Eventually(() => db.State == ConnectionState.Connected);
            Assert.Equal("Bearer read-key", server.LastHeaders!["Authorization"]);
            Assert.Equal("my-secret-token-123456", server.LastHeaders["X-FreeSync-Token"]);
            Assert.Equal("wss://example.test/db/global?v=1", server.LastUri!.ToString());
            Assert.Equal("public", db.Role);
        }

        [Fact]
        public async Task SubscribesAndAppliesSnapshotsAndPatches()
        {
            var (db, server) = Create();
            using var _ = db;
            var s = await server.Accept();
            var seen = new ConcurrentQueue<DataSnapshot>();
            db.Ref("scores").OnValue(seen.Enqueue);

            var sub = await s.NextOf("sub");
            Assert.Equal("scores", sub["p"]);
            Assert.False(sub.ContainsKey("since"));
            s.Push(new { t = "snap", id = sub["id"], p = "scores", v = new { p1 = 1 }, rev = 101 });
            await Eventually(() => seen.Count == 1);
            Assert.Equal("{\"p1\":1}", seen.Last().ToJson());
            Assert.False(seen.Last().IsFromCache);

            s.Push(new { t = "patch", rev = 102, ops = new object[] { new object[] { "scores/p2", 5 } } });
            await Eventually(() => seen.Count == 2);
            Assert.Equal(5, seen.Last().Child("p2").GetValue<int>());
            Assert.Equal(102, seen.Last().Rev);

            // A patch that doesn't change the visible value fires nothing.
            s.Push(new { t = "patch", rev = 103, ops = new object[] { new object[] { "scores/p2", 5 } } });
            await Task.Delay(50);
            Assert.Equal(2, seen.Count);
        }

        [Fact]
        public async Task WritesAreOptimisticAndConfirmedByAck()
        {
            var (db, server) = Create();
            using var _ = db;
            var s = await server.Accept();
            var values = new ConcurrentQueue<string>();
            db.Ref("a").OnValue(snap => values.Enqueue(snap.ToJson()));
            var sub = await s.NextOf("sub");
            s.Push(new { t = "snap", id = sub["id"], p = "a", v = (object?)null, rev = 100 });
            await Eventually(() => values.Count == 1);

            var task = db.Ref("a/x").SetAsync(1);
            await Eventually(() => values.Count == 2);
            Assert.Equal("{\"x\":1}", values.Last()); // visible before the server answers
            Assert.False(task.IsCompleted);

            var set = await s.NextOf("set");
            Assert.Equal("a/x", set["p"]);
            s.Push(new object[]
            {
                new { t = "patch", rev = 101, ops = new object[] { new object[] { "a/x", 1 } } },
                new { t = "ack", id = set["id"], rev = 101 },
            });
            Assert.Equal(101, await task);
            await Task.Delay(30);
            Assert.Equal(2, values.Count); // confirmation changed nothing visible
        }

        [Fact]
        public async Task RefusedWritesRevertAndFail()
        {
            var (db, server) = Create();
            using var _ = db;
            var s = await server.Accept();
            var values = new ConcurrentQueue<string>();
            db.Ref("a").OnValue(snap => values.Enqueue(snap.ToJson()));
            var sub = await s.NextOf("sub");
            s.Push(new { t = "snap", id = sub["id"], p = "a", v = new { x = 0 }, rev = 100 });
            await Eventually(() => values.Count == 1);

            var task = db.Ref("a/x").SetAsync(9);
            var set = await s.NextOf("set");
            s.Push(new { t = "err", id = set["id"], code = "permission_denied", msg = "no" });
            var ex = await Assert.ThrowsAsync<FreeSyncException>(() => task);
            Assert.Equal("permission_denied", ex.Code);
            await Eventually(() => values.Count == 3);
            Assert.Equal(new[] { "{\"x\":0}", "{\"x\":9}", "{\"x\":0}" }, values.ToArray());
        }

        [Fact]
        public async Task CoalescesWritesInOneFrame()
        {
            var (db, server) = Create(o => o.WriteInterval = TimeSpan.FromMilliseconds(100));
            using var _ = db;
            var s = await server.Accept();
            await Eventually(() => db.State == ConnectionState.Connected);
            var t1 = db.Ref("p/x").SetAsync(1);
            var t2 = db.Ref("p/x").SetAsync(2); // supersedes the first
            var t3 = db.Ref("p/y").SetAsync(3);
            var t4 = db.Ref("q").UpdateAsync(new Dictionary<string, object?> { ["a/b"] = 1, ["c"] = null });
            var frame = await s.NextFrame();
            Assert.Equal(3, frame.Count);
            Assert.Equal(new object?[] { "set", "set", "upd" }, frame.Select(m => m["t"]).ToArray());
            Assert.Equal(2L, frame[0]["v"]);
            s.Push(frame.Select(m => (object)new { t = "ack", id = m["id"], rev = 200 }).ToArray());
            Assert.Equal(200, await t1);
            Assert.Equal(200, await t2);
            await Task.WhenAll(t3, t4);
        }

        [Fact]
        public async Task ReconnectsAndResyncsWithRevisionsOnDisconnectAndUnconfirmedWrites()
        {
            var (db, server) = Create();
            using var _ = db;
            var s1 = await server.Accept();
            var values = new ConcurrentQueue<DataSnapshot>();
            db.Ref("room").OnValue(values.Enqueue);
            var sub = await s1.NextOf("sub");
            s1.Push(new { t = "snap", id = sub["id"], p = "room", v = new { a = 1 }, rev = 100 });
            s1.Push(new { t = "patch", rev = 150, ops = new object[] { new object[] { "other", 1 } } });
            var od = db.Ref("presence/me").OnDisconnect().Remove();
            var odMsg = await s1.NextOf("od");
            s1.Push(new { t = "ack", id = odMsg["id"], rev = 150 });
            await od;
            var write = db.Ref("room/b").SetAsync(2);
            await s1.NextOf("set"); // sent, never acknowledged

            var states = new ConcurrentQueue<ConnectionState>();
            db.StateChanged += states.Enqueue;
            s1.Drop();
            await Eventually(() => states.Contains(ConnectionState.Reconnecting));
            Assert.True(values.Last().Exists); // last known data stays

            var s2 = await server.Accept(rev: 160);
            var frame = await s2.NextFrame();
            var resub = frame.Single(m => (string?)m["t"] == "sub");
            Assert.Equal("room", resub["p"]);
            Assert.Equal(150L, resub["since"]); // newest revision seen on the old connection
            var reOd = frame.Single(m => (string?)m["t"] == "od");
            Assert.Equal("rm", reOd["op"]);
            Assert.Equal("presence/me", reOd["p"]);
            var resent = await s2.NextOf("set");
            Assert.Equal("room/b", resent["p"]);

            s2.Push(new { t = "snap", id = resub["id"], p = "room", nm = true, rev = 160 });
            s2.Push(new { t = "patch", rev = 161, ops = new object[] { new object[] { "room/b", 2 } } }); // the server sends the patch before the ack
            s2.Push(new { t = "ack", id = resent["id"], rev = 161 });
            Assert.Equal(161, await write);
            await Eventually(() => db.State == ConnectionState.Connected);
            Assert.Equal("{\"a\":1,\"b\":2}", values.Last().ToJson());
        }

        [Fact]
        public async Task QuotaRefusalSetsStateAndWaitsUntilProbe()
        {
            var (db, server) = Create(o =>
            {
                o.QuotaProbeInterval = TimeSpan.FromMilliseconds(200);
                o.AutoConnect = false;
            });
            using var _ = db;
            server.ConnectResults.Enqueue(429);
            db.GoOnline();
            await Eventually(() => db.State == ConnectionState.QuotaExceeded);
            Assert.True(db.LastError!.IsQuota);
            var attempts = server.ConnectAttempts;
            await Task.Delay(80);
            Assert.Equal(attempts, server.ConnectAttempts); // no reconnect spam
            await server.Accept();
            await Eventually(() => db.State == ConnectionState.Connected, 2000);
        }

        [Fact]
        public void QuotaDelayRunsToUtcMidnightWithoutProbe()
        {
            var now = new DateTime(2026, 11, 4, 23, 0, 0, DateTimeKind.Utc);
            var (db, _) = Create(o =>
            {
                o.QuotaProbeInterval = null;
                o.UtcNow = () => now;
                o.AutoConnect = false;
            });
            using var __ = db;
            var d = db.QuotaDelay();
            Assert.InRange(d, TimeSpan.FromHours(1), TimeSpan.FromHours(1) + TimeSpan.FromMinutes(15));
        }

        [Fact]
        public void BackoffGrowsWithJitterUpToTheMaximum()
        {
            var (db, _) = Create(o =>
            {
                o.ReconnectMinDelay = TimeSpan.FromSeconds(1);
                o.ReconnectMaxDelay = TimeSpan.FromSeconds(60);
                o.AutoConnect = false;
            });
            using var __ = db;
            Assert.InRange(db.Backoff(1), TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1));
            Assert.InRange(db.Backoff(4), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8));
            Assert.InRange(db.Backoff(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
        }

        [Fact]
        public async Task WriteQuotaErrorsBlockWritesUntilMidnight()
        {
            var (db, server) = Create();
            using var _ = db;
            var s = await server.Accept();
            var task = db.Ref("a").SetAsync(1);
            var set = await s.NextOf("set");
            s.Push(new { t = "err", id = set["id"], code = "write_quota", msg = "used up" });
            Assert.True((await Assert.ThrowsAsync<FreeSyncException>(() => task)).IsQuota);
            Assert.NotNull(db.WritesBlockedUntil);
            var ex = await Assert.ThrowsAsync<FreeSyncException>(() => db.Ref("a").SetAsync(2));
            Assert.Equal("write_quota", ex.Code);
            Assert.False(s.HasPendingFrames);
        }

        [Fact]
        public async Task RateLimitedWritesAreRetried()
        {
            var (db, server) = Create();
            using var _ = db;
            var s = await server.Accept();
            var task = db.Ref("a").SetAsync(1);
            var first = await s.NextOf("set");
            s.Push(new { t = "err", id = first["id"], code = "rate_limited", msg = "slow down", retryMs = 50 });
            var retry = await s.NextOf("set");
            Assert.Equal(first["id"], retry["id"]);
            s.Push(new { t = "ack", id = retry["id"], rev = 5 });
            Assert.Equal(5, await task);
        }

        [Fact]
        public async Task QueuedModeDeliversOnlyOnDispatch()
        {
            var (db, server) = Create(o => o.EventDispatch = EventDispatchMode.Queued);
            using var _ = db;
            var s = await server.Accept();
            var count = 0;
            db.Ref("x").OnValue(_ => count++);
            var sub = await s.NextOf("sub");
            s.Push(new { t = "snap", id = sub["id"], p = "x", v = 1, rev = 1 });
            await Task.Delay(50);
            Assert.Equal(0, count);
            await Eventually(() => db.Dispatch() > 0 || count > 0);
            Assert.Equal(1, count);
        }

        [Fact]
        public async Task AwaitingWritesDoesNotNeedDispatchInQueuedMode()
        {
            var (db, server) = Create(o => o.EventDispatch = EventDispatchMode.Queued);
            using var _ = db;
            var s = await server.Accept();
            var task = db.Ref("a").SetAsync(1);
            var set = await s.NextOf("set");
            s.Push(new { t = "ack", id = set["id"], rev = 7 });
            Assert.Equal(7, await task.WaitAsync(TimeSpan.FromSeconds(2))); // no Dispatch() call anywhere
        }

        [Fact]
        public async Task SubscriptionErrorsReachOnError()
        {
            var (db, server) = Create();
            using var _ = db;
            var s = await server.Accept();
            FreeSyncException? error = null;
            db.Ref("secret").OnValue(_ => { }, e => error = e);
            var sub = await s.NextOf("sub");
            s.Push(new { t = "err", id = sub["id"], code = "permission_denied", msg = "no" });
            await Eventually(() => error != null);
            Assert.Equal("permission_denied", error!.Code);
        }

        [Fact]
        public async Task UnsubscribesWhenTheLastListenerIsDisposed()
        {
            var (db, server) = Create();
            using var _ = db;
            var s = await server.Accept();
            var l1 = db.Ref("x").OnValue(_ => { });
            var l2 = db.Ref("x").OnValue(_ => { });
            await s.NextOf("sub");
            l1.Dispose();
            await Task.Delay(30);
            Assert.False(s.HasPendingFrames);
            l2.Dispose();
            Assert.Equal("x", (await s.NextOf("unsub"))["p"]);
        }

        [Fact]
        public async Task DiskCacheServesColdStartsAndSuppliesSince()
        {
            var dir = Path.Combine(Path.GetTempPath(), "freesync-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var (db1, server1) = Create(o => o.CacheDirectory = dir);
                var s1 = await server1.Accept();
                db1.Ref("cfg").OnValue(_ => { });
                var sub = await s1.NextOf("sub");
                s1.Push(new { t = "snap", id = sub["id"], p = "cfg", v = new { motd = "hi" }, rev = 300 });
                await Task.Delay(50);
                db1.Dispose();

                var (db2, server2) = Create(o => o.CacheDirectory = dir);
                using var _ = db2;
                DataSnapshot? first = null;
                db2.Ref("cfg").OnValue(snap => first ??= snap);
                await Eventually(() => first != null);
                Assert.True(first!.IsFromCache);
                Assert.Equal("hi", first.Child("motd").GetValue<string>());
                var s2 = await server2.Accept();
                var resub = await s2.NextOf("sub");
                Assert.Equal(300L, resub["since"]);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task ValidatesPathsAndUpdatesLocally()
        {
            var (db, _) = Create(o => o.AutoConnect = false);
            using var __ = db;
            Assert.Throws<ArgumentException>(() => db.Ref("a.b"));
            Assert.Throws<ArgumentException>(() => { _ = db.Ref("a").UpdateAsync(new Dictionary<string, object?> { ["x"] = 1, ["x/y"] = 2 }); });
            Assert.Equal("a/b", db.Ref("a").Child("b").Path);
            Assert.Equal("a", db.Ref("a/b").Parent!.Path);
            Assert.Equal(20, db.Ref("chat").Push().Key!.Length);
            await Task.CompletedTask;
        }
    }
}
