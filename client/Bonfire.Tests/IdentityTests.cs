using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Bonfire.Tests
{
    public class IdentityTests
    {
        // Same inputs and expected token as server/test/identity.test.ts: both implementations must agree.
        private const string Vector =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJwbGF5ZXItNDIiLCJpYXQiOjE4MDAwMDAwMDAsImV4cCI6MTgwMDA4NjQwMCwicm9vbSI6Imdsb2JhbCJ9.v2yC4brhKZy3b65rj4SO5TJEycquTVk5npg0mMkbK94";

        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void CreatesTheSameTokenAsTheServer()
        {
            var token = BonfireIdentity.Create("vector-secret", "player-42", Epoch.AddSeconds(1800000000), Epoch.AddSeconds(1800086400), "global");
            Assert.Equal(Vector, token);
            Assert.Equal("player-42", BonfireIdentity.ReadPlayerId(token));
            Assert.Equal(Epoch.AddSeconds(1800086400), BonfireIdentity.ReadExpiry(token));
        }

        [Fact]
        public void RefusesUnusablePlayerIdsAndUnreadableTokens()
        {
            Assert.Throws<ArgumentException>(() => BonfireIdentity.Create("s", "a/b", TimeSpan.FromHours(1)));
            Assert.Throws<ArgumentException>(() => BonfireIdentity.Create("", "p1", TimeSpan.FromHours(1)));
            Assert.Null(BonfireIdentity.ReadPlayerId("not a token"));
            Assert.Null(BonfireIdentity.ReadExpiry("a.b.c"));
        }

        [Fact]
        public async Task ClientSendsTheTokenAndUsesItsPlayerId()
        {
            var server = new MockServer();
            var token = BonfireIdentity.Create("secret", "steam-123", TimeSpan.FromHours(1));
            using var db = new BonfireClient("wss://example.test", "global", "read-key", new BonfireOptions
            {
                SocketFactory = server.Create,
                EventDispatch = EventDispatchMode.Immediate,
                IdentityToken = token,
                ClientToken = "ignored-because-identity-wins-123",
            });
            Assert.Equal("steam-123", db.ClientId); // known before connecting
            var s = await server.NextConnection();
            Assert.Equal(token, server.LastHeaders!["X-Bonfire-Identity"]);
            s.Push(new { t = "welcome", v = 1, cid = "steam-123", role = "public", verified = true, rev = 1, lim = new { frame = 65536, ops = 100 } });
            for (var i = 0; i < 100 && !db.IsVerified; i++) await Task.Delay(10);
            Assert.True(db.IsVerified);
        }

        [Fact]
        public async Task ProviderIsAskedForAFreshTokenOnEveryConnection()
        {
            var server = new MockServer();
            var calls = 0;
            using var db = new BonfireClient("wss://example.test", "global", "read-key", new BonfireOptions
            {
                SocketFactory = server.Create,
                EventDispatch = EventDispatchMode.Immediate,
                ReconnectMinDelay = TimeSpan.FromMilliseconds(10),
                ReconnectMaxDelay = TimeSpan.FromMilliseconds(30),
                IdentityTokenProvider = () =>
                {
                    var n = Interlocked.Increment(ref calls);
                    return Task.FromResult<string?>(BonfireIdentity.Create("secret", "p" + n, TimeSpan.FromMinutes(5)));
                },
            });
            var first = await server.NextConnection();
            Assert.Equal("p1", BonfireIdentity.ReadPlayerId(server.LastHeaders!["X-Bonfire-Identity"]));
            first.Drop();
            await server.NextConnection();
            Assert.Equal("p2", BonfireIdentity.ReadPlayerId(server.LastHeaders!["X-Bonfire-Identity"]));
            Assert.Equal("p2", db.ClientId);
        }
    }
}
