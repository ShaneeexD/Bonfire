using System;

namespace Bonfire.Json
{
    /// <summary>
    /// Converts between your own types and JSON text. Bonfire uses this whenever you write a value or read one
    /// back with <see cref="DataSnapshot.GetValue{T}"/>. The default is <see cref="BuiltInJsonSerializer"/>; the
    /// <c>adapters</c> folder has drop-in implementations for Newtonsoft.Json and System.Text.Json.
    /// </summary>
    public interface IJsonSerializer
    {
        /// <summary>Serialises a value (which may be null) to JSON text.</summary>
        string Serialize(object? value);

        /// <summary>Deserialises JSON text to an instance of <paramref name="type"/>.</summary>
        object? Deserialize(string json, Type type);
    }

    /// <summary>Generic helpers for <see cref="IJsonSerializer"/>.</summary>
    public static class JsonSerializerExtensions
    {
        /// <summary>Deserialises JSON text to <typeparamref name="T"/>.</summary>
        public static T? Deserialize<T>(this IJsonSerializer serializer, string json) => (T?)serializer.Deserialize(json, typeof(T));
    }
}
