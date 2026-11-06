using System;
using System.Collections.Generic;
using FreeSync.Internal;
using FreeSync.Json;
using Xunit;

namespace FreeSync.Tests
{
    public class JsonTests
    {
        [Fact]
        public void ParsesAndWritesRoundTrip()
        {
            const string json = "{\"a\":1,\"b\":-2.5,\"c\":\"x\\\"y\\n\\u00e9\",\"d\":[true,false,null],\"e\":{}}";
            var tree = FsJson.Parse(json);
            var obj = Assert.IsType<Dictionary<string, object?>>(tree);
            Assert.Equal(1L, obj["a"]);
            Assert.Equal(-2.5, obj["b"]);
            Assert.Equal("x\"y\né", obj["c"]);
            Assert.Equal(new List<object?> { true, false, null }, obj["d"]);
            Assert.Equal(json.Replace("\\u00e9", "é"), FsJson.Serialize(tree));
        }

        [Fact]
        public void LargeIntegersStayExactAndDoublesRoundTrip()
        {
            Assert.Equal(9007199254740993L, FsJson.Parse("9007199254740993"));
            Assert.Equal(1e300, FsJson.Parse("1e300"));
            Assert.Equal("0.1", FsJson.Serialize(0.1));
            Assert.Equal("3", FsJson.Serialize(3.0));
        }

        [Theory]
        [InlineData("")]
        [InlineData("{")]
        [InlineData("[1,]")]
        [InlineData("{\"a\" 1}")]
        [InlineData("tru")]
        [InlineData("\"unterminated")]
        [InlineData("1 2")]
        public void RejectsInvalidJson(string json) => Assert.Throws<FormatException>(() => FsJson.Parse(json));

        [Fact]
        public void RejectsNonFiniteNumbers() => Assert.Throws<FormatException>(() => FsJson.Serialize(double.NaN));

        public enum Team { Red, Blue }

        public class Player
        {
            public string Name { get; set; } = "";
            public int Score { get; set; }
            public Team Team { get; set; }
            public List<string> Items { get; set; } = new List<string>();
            public Dictionary<string, double> Stats { get; set; } = new Dictionary<string, double>();
            public float[]? Position;
            public Player? Friend { get; set; }
        }

        [Fact]
        public void BuiltInSerializerHandlesPocos()
        {
            var s = BuiltInJsonSerializer.Instance;
            var p = new Player
            {
                Name = "Ann",
                Score = 42,
                Team = Team.Blue,
                Items = { "sword", "shield" },
                Stats = { ["hp"] = 9.5 },
                Position = new[] { 1f, 2.5f },
            };
            var json = s.Serialize(p);
            Assert.Contains("\"Team\":\"Blue\"", json);
            Assert.DoesNotContain("Friend", json); // nulls are omitted
            var back = s.Deserialize<Player>(json)!;
            Assert.Equal("Ann", back.Name);
            Assert.Equal(42, back.Score);
            Assert.Equal(Team.Blue, back.Team);
            Assert.Equal(new[] { "sword", "shield" }, back.Items);
            Assert.Equal(9.5, back.Stats["hp"]);
            Assert.Equal(new[] { 1f, 2.5f }, back.Position);
        }

        [Fact]
        public void BuiltInSerializerConvertsPrimitivesAndObjectsAsLists()
        {
            Assert.Equal(7, BuiltInJsonSerializer.FromTree(7L, typeof(int)));
            Assert.Equal(7.0f, BuiltInJsonSerializer.FromTree(7L, typeof(float)));
            Assert.Null(BuiltInJsonSerializer.FromTree(null, typeof(int?)));
            Assert.Equal(0, BuiltInJsonSerializer.FromTree(null, typeof(int)));
            // Firebase-style objects with numeric keys can be read as lists, in numeric order.
            var tree = FsJson.Parse("{\"10\":\"c\",\"2\":\"b\",\"1\":\"a\"}");
            Assert.Equal(new List<string> { "a", "b", "c" }, BuiltInJsonSerializer.FromTree(tree, typeof(List<string>)));
        }

        [Fact]
        public void PathsValidateLikeTheServer()
        {
            Assert.Empty(Paths.Parse(""));
            Assert.Equal(new[] { "a", "b" }, Paths.Parse("/a/b/"));
            foreach (var bad in new[] { "a//b", "a.b", "a#", "x/$y", "a[0]" })
                Assert.Throws<ArgumentException>(() => Paths.Parse(bad));
            Assert.True(Paths.IsPrefix(new[] { "a" }, new[] { "a", "b" }));
            Assert.False(Paths.IsPrefix(new[] { "a" }, new[] { "ab" }));
        }

        [Fact]
        public void TreeSetInCopiesAndPrunes()
        {
            var root = (Dictionary<string, object?>)FsJson.Parse("{\"a\":{\"b\":1,\"c\":2},\"z\":{\"y\":1}}")!;
            var next = (Dictionary<string, object?>)Tree.SetIn(root, new[] { "a", "b" }, null)!;
            Assert.Equal("{\"a\":{\"c\":2},\"z\":{\"y\":1}}", FsJson.Serialize(next));
            Assert.Equal(1L, ((Dictionary<string, object?>)root["a"]!)["b"]); // original untouched
            Assert.Same(root["z"], next["z"]);
            Assert.Null(Tree.SetIn(FsJson.Parse("{\"a\":{\"b\":1}}"), new[] { "a", "b" }, null));
            Assert.Null(Tree.Normalize(FsJson.Parse("{\"a\":null,\"b\":{},\"c\":[]}")));
        }

        [Fact]
        public void ClientIdMatchesTheServerDerivation()
        {
            // Same input as the server test; both must produce this exact id.
            Assert.Equal("jghcqgrt4btbgyfs", Ids.ClientIdFor("global", "my-secret-token-123456"));
        }

        [Fact]
        public void PushIdsSortByTime()
        {
            var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var a = Ids.PushId(t);
            var b = Ids.PushId(t);
            var c = Ids.PushId(t.AddMilliseconds(1));
            Assert.Equal(20, a.Length);
            Assert.True(string.CompareOrdinal(a, b) < 0);
            Assert.True(string.CompareOrdinal(b, c) < 0);
        }
    }
}
