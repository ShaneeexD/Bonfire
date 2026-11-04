// Optional adapter: copy this file into your project to use Newtonsoft.Json (Json.NET) with FreeSync.
//
//   var db = new FreeSyncClient(url, room, key, new FreeSyncOptions { Serializer = new NewtonsoftJsonSerializer() });
//
// Many Unity games already ship Newtonsoft.Json (or Unity's com.unity.nuget.newtonsoft-json package);
// reuse the game's copy rather than shipping a second one with your mod.

using System;
using FreeSync.Json;
using Newtonsoft.Json;

namespace FreeSync.Adapters
{
    /// <summary>An <see cref="IJsonSerializer"/> backed by Newtonsoft.Json.</summary>
    public sealed class NewtonsoftJsonSerializer : IJsonSerializer
    {
        private readonly JsonSerializerSettings _settings;

        /// <summary>Creates the adapter. Null values are omitted by default (they mean "absent" in FreeSync).</summary>
        public NewtonsoftJsonSerializer(JsonSerializerSettings? settings = null)
        {
            _settings = settings ?? new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
        }

        /// <inheritdoc />
        public string Serialize(object? value) => JsonConvert.SerializeObject(value, _settings);

        /// <inheritdoc />
        public object? Deserialize(string json, Type type) => JsonConvert.DeserializeObject(json, type, _settings);
    }
}
