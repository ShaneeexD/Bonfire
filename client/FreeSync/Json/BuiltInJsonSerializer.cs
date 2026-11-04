using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace FreeSync.Json
{
    /// <summary>
    /// The default serializer. Handles primitives, enums (as names), strings, <see cref="DateTime"/>, <see cref="Guid"/>,
    /// arrays and lists, string-keyed dictionaries, and plain classes/structs through their public fields and
    /// properties (a parameterless constructor is needed to read them back). For attributes, custom converters or
    /// constructors with parameters, use one of the adapters instead.
    /// </summary>
    /// <remarks>
    /// Under Unity IL2CPP with managed code stripping, keep your data classes from being stripped
    /// (for example with <c>[UnityEngine.Scripting.Preserve]</c> or a link.xml file).
    /// </remarks>
    public sealed class BuiltInJsonSerializer : IJsonSerializer
    {
        /// <summary>A shared instance; the serializer is stateless and thread-safe.</summary>
        public static readonly BuiltInJsonSerializer Instance = new BuiltInJsonSerializer();

        /// <inheritdoc />
        public string Serialize(object? value) => FsJson.Serialize(ToTree(value, 0));

        /// <inheritdoc />
        public object? Deserialize(string json, Type type) => FromTree(FsJson.Parse(json), type);

        /// <summary>Converts any supported .NET value into the plain tree used by <see cref="FsJson"/>.</summary>
        public static object? ToTree(object? value, int depth = 0)
        {
            if (depth > 64) throw new FormatException("value nested too deeply (cyclic reference?)");
            switch (value)
            {
                case null: return null;
                case string or bool or long or double: return value;
                case int or short or byte or sbyte or ushort or uint: return Convert.ToInt64(value, CultureInfo.InvariantCulture);
                case ulong u: return u <= long.MaxValue ? (object)(long)u : (double)u;
                case float f: return (double)f;
                case decimal m: return (double)m;
                case char c: return c.ToString();
                case Enum e: return e.ToString();
                case DateTime dt: return dt.ToString("o", CultureInfo.InvariantCulture);
                case DateTimeOffset dto: return dto.ToString("o", CultureInfo.InvariantCulture);
                case Guid g: return g.ToString();
                case TimeSpan ts: return ts.ToString("c", CultureInfo.InvariantCulture);
                case IDictionary dict:
                {
                    var obj = new Dictionary<string, object?>();
                    foreach (DictionaryEntry entry in dict)
                        obj[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? ""] = ToTree(entry.Value, depth + 1);
                    return obj;
                }
                case IEnumerable list:
                {
                    var arr = new List<object?>();
                    foreach (var item in list) arr.Add(ToTree(item, depth + 1));
                    return arr;
                }
            }

            var result = new Dictionary<string, object?>();
            foreach (var member in Members(value.GetType()))
            {
                var v = member.Get(value);
                if (v != null) result[member.Name] = ToTree(v, depth + 1);
            }
            return result;
        }

        /// <summary>Converts a plain tree (as produced by <see cref="FsJson.Parse"/>) to an instance of <paramref name="type"/>.</summary>
        public static object? FromTree(object? tree, Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            if (tree == null)
                return type.IsValueType && underlying == null ? Activator.CreateInstance(type) : null;
            if (underlying != null) type = underlying;
            if (type == typeof(object)) return tree;
            if (type.IsInstanceOfType(tree) && !(tree is IDictionary) && !(tree is IList)) return tree;

            if (type == typeof(string)) return tree is string s ? s : FsJson.Serialize(tree);
            if (type.IsEnum)
                return tree is string es ? Enum.Parse(type, es, true) : Enum.ToObject(type, Convert.ToInt64(tree, CultureInfo.InvariantCulture));
            if (type == typeof(Guid)) return Guid.Parse((string)tree);
            if (type == typeof(DateTime)) return DateTime.Parse((string)tree, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (type == typeof(DateTimeOffset)) return DateTimeOffset.Parse((string)tree, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (type == typeof(TimeSpan)) return TimeSpan.Parse((string)tree, CultureInfo.InvariantCulture);
            if (type.IsPrimitive || type == typeof(decimal))
            {
                if (tree is string ns) return Convert.ChangeType(ns, type, CultureInfo.InvariantCulture);
                return Convert.ChangeType(tree, type, CultureInfo.InvariantCulture);
            }

            // Dictionaries with string keys.
            var dictType = FindGeneric(type, typeof(IDictionary<,>)) ?? FindGeneric(type, typeof(IReadOnlyDictionary<,>));
            if (dictType != null && tree is IDictionary<string, object?> srcDict)
            {
                var args = dictType.GetGenericArguments();
                var target = type.IsInterface ? typeof(Dictionary<,>).MakeGenericType(args) : type;
                var dict = (IDictionary)Activator.CreateInstance(target)!;
                foreach (var kv in srcDict) dict[ConvertKey(kv.Key, args[0])] = FromTree(kv.Value, args[1]);
                return dict;
            }

            // Arrays and lists. An object with keys is accepted too (values in key order).
            var elemType = type.IsArray ? type.GetElementType() : FindGeneric(type, typeof(IEnumerable<>))?.GetGenericArguments()[0];
            if (elemType != null && (tree is IList || tree is IDictionary<string, object?>))
            {
                IEnumerable<object?> items = tree is IList l
                    ? l.Cast<object?>()
                    : ((IDictionary<string, object?>)tree).OrderBy(kv => kv.Key, KeyComparer.Instance).Select(kv => kv.Value);
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elemType))!;
                foreach (var item in items) list.Add(FromTree(item, elemType));
                if (type.IsArray)
                {
                    var arr = Array.CreateInstance(elemType, list.Count);
                    list.CopyTo(arr, 0);
                    return arr;
                }
                if (type.IsAssignableFrom(list.GetType())) return list;
                var collection = (IList)Activator.CreateInstance(type)!;
                foreach (var item in list) collection.Add(item);
                return collection;
            }

            // Plain classes and structs.
            if (tree is IDictionary<string, object?> props)
            {
                var instance = Activator.CreateInstance(type)!;
                var members = Members(type).ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var kv in props)
                    if (members.TryGetValue(kv.Key, out var member) && member.CanSet)
                        member.Set(instance, FromTree(kv.Value, member.Type));
                return instance;
            }
            throw new FormatException($"cannot convert JSON {tree.GetType().Name} to {type.FullName}");
        }

        private static object ConvertKey(string key, Type keyType)
        {
            if (keyType == typeof(string) || keyType == typeof(object)) return key;
            if (keyType.IsEnum) return Enum.Parse(keyType, key, true);
            return Convert.ChangeType(key, keyType, CultureInfo.InvariantCulture);
        }

        private static Type? FindGeneric(Type type, Type generic)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == generic) return type;
            foreach (var i in type.GetInterfaces())
                if (i.IsGenericType && i.GetGenericTypeDefinition() == generic) return i;
            return null;
        }

        /// <summary>Orders "2" before "10" so Firebase-style numeric keys come back in order.</summary>
        private sealed class KeyComparer : IComparer<string>
        {
            public static readonly KeyComparer Instance = new KeyComparer();
            public int Compare(string? x, string? y)
            {
                var xn = long.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var xl);
                var yn = long.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var yl);
                if (xn && yn) return xl.CompareTo(yl);
                if (xn != yn) return xn ? -1 : 1;
                return string.CompareOrdinal(x, y);
            }
        }

        private sealed class Member
        {
            public string Name = "";
            public Type Type = typeof(object);
            public Func<object, object?> Get = _ => null;
            public Action<object, object?> Set = (_, _) => { };
            public bool CanSet;
        }

        private static readonly Dictionary<Type, Member[]> MemberCache = new Dictionary<Type, Member[]>();

        private static Member[] Members(Type type)
        {
            lock (MemberCache)
            {
                if (MemberCache.TryGetValue(type, out var cached)) return cached;
                var list = new List<Member>();
                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                    var prop = p;
                    list.Add(new Member
                    {
                        Name = p.Name,
                        Type = p.PropertyType,
                        Get = o => prop.GetValue(o),
                        Set = (o, v) => prop.SetValue(o, v),
                        CanSet = p.CanWrite && p.GetSetMethod() != null,
                    });
                }
                foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    var field = f;
                    list.Add(new Member
                    {
                        Name = f.Name,
                        Type = f.FieldType,
                        Get = o => field.GetValue(o),
                        Set = (o, v) => field.SetValue(o, v),
                        CanSet = !f.IsInitOnly,
                    });
                }
                var result = list.ToArray();
                MemberCache[type] = result;
                return result;
            }
        }
    }
}
