// Optional adapter: copy this file into your project to use System.Text.Json with Bonfire.
//
//   var db = new BonfireClient(url, room, key, new BonfireOptions { Serializer = new SystemTextJsonSerializer() });
//
// System.Text.Json is built into .NET Core 3.0+ / .NET 5+. On Unity or .NET Framework it needs the
// System.Text.Json package, so the built-in serializer or the Newtonsoft adapter is usually simpler there.

using System;
using System.Text.Json;
using Bonfire.Json;

namespace Bonfire.Adapters
{
    /// <summary>An <see cref="IJsonSerializer"/> backed by System.Text.Json.</summary>
    public sealed class SystemTextJsonSerializer : IJsonSerializer
    {
        private readonly JsonSerializerOptions _options;

        /// <summary>Creates the adapter. Defaults to <see cref="JsonSerializerDefaults.Web"/> (camelCase, case-insensitive reads).</summary>
        public SystemTextJsonSerializer(JsonSerializerOptions? options = null)
        {
            _options = options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        }

        /// <inheritdoc />
        public string Serialize(object? value) =>
            value == null ? "null" : JsonSerializer.Serialize(value, value.GetType(), _options);

        /// <inheritdoc />
        public object? Deserialize(string json, Type type) => JsonSerializer.Deserialize(json, type, _options);
    }
}
