using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FreeSync.Json
{
    /// <summary>
    /// A small, dependency-free JSON reader and writer working on a plain object tree:
    /// <see cref="Dictionary{TKey,TValue}"/> (string → object?) for objects, <see cref="List{T}"/> of object? for arrays,
    /// and <see cref="string"/>, <see cref="long"/>, <see cref="double"/>, <see cref="bool"/> or <c>null</c> for scalars.
    /// </summary>
    public static class FsJson
    {
        /// <summary>Parses JSON text into an object tree.</summary>
        /// <exception cref="FormatException">The text is not valid JSON.</exception>
        public static object? Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var reader = new Reader(json);
            reader.SkipWhitespace();
            var value = reader.ReadValue(0);
            reader.SkipWhitespace();
            if (!reader.AtEnd) throw reader.Error("unexpected trailing characters");
            return value;
        }

        /// <summary>
        /// Writes an object tree as compact JSON. Accepts the tree types produced by <see cref="Parse"/>
        /// plus any <see cref="IDictionary"/> with string keys, any <see cref="IEnumerable"/>, and all numeric primitives.
        /// </summary>
        public static string Serialize(object? value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            return sb.ToString();
        }

        private const int MaxDepth = 64;

        private static void Write(StringBuilder sb, object? value, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("value nested too deeply");
            switch (value)
            {
                case null:
                    sb.Append("null");
                    return;
                case string s:
                    WriteString(sb, s);
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case char c:
                    WriteString(sb, c.ToString());
                    return;
                case double d:
                    WriteDouble(sb, d);
                    return;
                case float f:
                    WriteDouble(sb, f);
                    return;
                case decimal m:
                    sb.Append(m.ToString(CultureInfo.InvariantCulture));
                    return;
                case int or long or short or byte or sbyte or ushort or uint or ulong:
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                case Enum e:
                    WriteString(sb, e.ToString());
                    return;
                case IDictionary dict:
                {
                    sb.Append('{');
                    var first = true;
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "");
                        sb.Append(':');
                        Write(sb, entry.Value, depth + 1);
                    }
                    sb.Append('}');
                    return;
                }
                case IEnumerable list:
                {
                    sb.Append('[');
                    var first = true;
                    foreach (var item in list)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Write(sb, item, depth + 1);
                    }
                    sb.Append(']');
                    return;
                }
                default:
                    throw new FormatException($"FsJson cannot write values of type {value.GetType().FullName}; use an IJsonSerializer");
            }
        }

        private static void WriteDouble(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) throw new FormatException("JSON numbers must be finite");
            if (Math.Abs(d) < 9007199254740992 && d == Math.Floor(d))
                sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
            else
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        internal static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class Reader
        {
            private readonly string _s;
            private int _i;

            public Reader(string s) => _s = s;

            public bool AtEnd => _i >= _s.Length;

            public FormatException Error(string message) => new FormatException($"Invalid JSON at position {_i}: {message}");

            public void SkipWhitespace()
            {
                while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t' || _s[_i] == '\n' || _s[_i] == '\r')) _i++;
            }

            public object? ReadValue(int depth)
            {
                if (depth > MaxDepth) throw Error("nested too deeply");
                if (AtEnd) throw Error("unexpected end");
                var c = _s[_i];
                switch (c)
                {
                    case '{': return ReadObject(depth);
                    case '[': return ReadArray(depth);
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw Error($"unexpected character '{c}'");
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) throw Error($"expected {word}");
                _i += word.Length;
            }

            private Dictionary<string, object?> ReadObject(int depth)
            {
                var obj = new Dictionary<string, object?>();
                _i++;
                SkipWhitespace();
                if (!AtEnd && _s[_i] == '}') { _i++; return obj; }
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || _s[_i] != '"') throw Error("expected property name");
                    var key = ReadString();
                    SkipWhitespace();
                    if (AtEnd || _s[_i] != ':') throw Error("expected ':'");
                    _i++;
                    SkipWhitespace();
                    obj[key] = ReadValue(depth + 1);
                    SkipWhitespace();
                    if (AtEnd) throw Error("unexpected end in object");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == '}') { _i++; return obj; }
                    throw Error("expected ',' or '}'");
                }
            }

            private List<object?> ReadArray(int depth)
            {
                var list = new List<object?>();
                _i++;
                SkipWhitespace();
                if (!AtEnd && _s[_i] == ']') { _i++; return list; }
                while (true)
                {
                    SkipWhitespace();
                    list.Add(ReadValue(depth + 1));
                    SkipWhitespace();
                    if (AtEnd) throw Error("unexpected end in array");
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == ']') { _i++; return list; }
                    throw Error("expected ',' or ']'");
                }
            }

            private string ReadString()
            {
                _i++; // opening quote
                StringBuilder? sb = null;
                var start = _i;
                while (true)
                {
                    if (AtEnd) throw Error("unterminated string");
                    var c = _s[_i];
                    if (c == '"')
                    {
                        string result;
                        if (sb == null) result = _s.Substring(start, _i - start);
                        else { sb.Append(_s, start, _i - start); result = sb.ToString(); }
                        _i++;
                        return result;
                    }
                    if (c == '\\')
                    {
                        sb ??= new StringBuilder();
                        sb.Append(_s, start, _i - start);
                        _i++;
                        if (AtEnd) throw Error("bad escape");
                        var e = _s[_i++];
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (_i + 4 > _s.Length) throw Error("bad unicode escape");
                                sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                _i += 4;
                                break;
                            default: throw Error($"bad escape '\\{e}'");
                        }
                        start = _i;
                        continue;
                    }
                    if (c < 0x20) throw Error("control character in string");
                    _i++;
                }
            }

            private object ReadNumber()
            {
                var start = _i;
                if (_s[_i] == '-') _i++;
                var isInteger = true;
                while (!AtEnd)
                {
                    var c = _s[_i];
                    if (c >= '0' && c <= '9') { _i++; continue; }
                    if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { isInteger = false; _i++; continue; }
                    break;
                }
                var text = _s.Substring(start, _i - start);
                if (isInteger && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                throw Error($"bad number '{text}'");
            }
        }
    }
}
