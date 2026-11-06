using System;
using System.Security.Cryptography;
using System.Text;

namespace Bonfire.Internal
{
    internal static class Ids
    {
        private const string Base32 = "abcdefghijklmnopqrstuvwxyz234567";

        /// <summary>Same derivation as the server: first 16 base32 chars of SHA-256(room + "\n" + token).</summary>
        public static string ClientIdFor(string room, string token)
        {
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(Encoding.UTF8.GetBytes(room + "\n" + token));
            var sb = new StringBuilder(16);
            int bits = 0, value = 0;
            foreach (var b in hash)
            {
                value = (value << 8) | b;
                bits += 8;
                while (bits >= 5 && sb.Length < 16)
                {
                    sb.Append(Base32[(value >> (bits - 5)) & 31]);
                    bits -= 5;
                }
                if (sb.Length >= 16) break;
            }
            return sb.ToString();
        }

        public static string NewToken()
        {
            var bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private const string PushChars = "-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz";
        private static readonly object PushLock = new object();
        private static long _lastPushTime;
        private static readonly byte[] LastRandom = new byte[12];

        /// <summary>A 20-character key that sorts by creation time (Firebase push-id style).</summary>
        public static string PushId(DateTime utcNow)
        {
            lock (PushLock)
            {
                var now = (long)(utcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                var duplicate = now == _lastPushTime;
                _lastPushTime = now;
                var chars = new char[20];
                for (var i = 7; i >= 0; i--)
                {
                    chars[i] = PushChars[(int)(now % 64)];
                    now /= 64;
                }
                if (!duplicate)
                {
                    using var rng = RandomNumberGenerator.Create();
                    rng.GetBytes(LastRandom);
                    for (var i = 0; i < 12; i++) LastRandom[i] %= 64;
                }
                else
                {
                    var i = 11;
                    for (; i >= 0 && LastRandom[i] == 63; i--) LastRandom[i] = 0;
                    if (i >= 0) LastRandom[i]++;
                }
                for (var i = 0; i < 12; i++) chars[8 + i] = PushChars[LastRandom[i]];
                return new string(chars);
            }
        }
    }
}
