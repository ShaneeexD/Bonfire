using System.Collections.Generic;
using Bonfire.Adapters;
using Bonfire.Json;
using Xunit;

namespace Bonfire.Tests
{
    public class AdapterTests
    {
        public class Player
        {
            public string Name { get; set; } = "";
            public int Score { get; set; }
            public List<string> Items { get; set; } = new List<string>();
            public string? Nickname { get; set; }
        }

        public static IEnumerable<object[]> Serializers() => new[]
        {
            new object[] { BuiltInJsonSerializer.Instance },
            new object[] { new SystemTextJsonSerializer() },
            new object[] { new NewtonsoftJsonSerializer() },
        };

        [Theory]
        [MemberData(nameof(Serializers))]
        public void RoundTripsThroughTheTreeModel(IJsonSerializer serializer)
        {
            var original = new Player { Name = "Ann", Score = 42, Items = { "sword" } };
            // What Bonfire does on write: serialize, parse into its tree, normalise.
            var tree = Internal.Tree.Normalize(BonfireJson.Parse(serializer.Serialize(original)));
            var snap = new DataSnapshot(new[] { "players", "ann" }, tree, 1, false, serializer);
            var back = snap.GetValue<Player>()!;
            Assert.Equal("Ann", back.Name);
            Assert.Equal(42, back.Score);
            Assert.Equal(new[] { "sword" }, back.Items);
            Assert.Null(back.Nickname);
            Assert.Equal(42, snap.Child(serializer is SystemTextJsonSerializer ? "score" : "Score").GetValue<int>());
        }
    }
}
