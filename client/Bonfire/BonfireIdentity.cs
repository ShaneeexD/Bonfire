using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Bonfire.Json;

namespace Bonfire
{
    /// <summary>
    /// Creates and reads signed player identity tokens (standard HS256 JWTs). A trusted backend that knows the
    /// server's <c>IDENTITY_SECRET</c> issues a token per player; the client passes it in
    /// <see cref="BonfireOptions.IdentityToken"/>, and the server then treats the player id as verified
    /// (<c>$uid</c> in rules).
    /// </summary>
    /// <remarks>
    /// <see cref="Create(string, string, TimeSpan, string?)"/> needs the secret, so call it only in code you control, such as a game
    /// server, server mod, bot or website backend. <b>Never ship the secret in a client mod</b>: anyone could extract it and
    /// sign tokens for any player. Any JWT library can make compatible tokens:
    /// <c>{"sub": playerId, "exp": unixSeconds}</c>, optional <c>"room"</c>, signed with HS256.
    /// </remarks>
    public static class BonfireIdentity
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Creates a token proving that the holder is <paramref name="playerId"/> for <paramref name="validFor"/>.</summary>
        /// <param name="secret">The server's IDENTITY_SECRET.</param>
        /// <param name="playerId">The player's id, e.g. a Steam id or your own account id. Must be a valid key (no / . # $ [ ]).</param>
        /// <param name="validFor">How long the token can be used to connect (the server caps this at IDENTITY_MAX_DAYS, 30 by default).
        /// Connections that are already open stay open after it expires.</param>
        /// <param name="room">Optional room name or glob ("lobby-*") the token is limited to.</param>
        public static string Create(string secret, string playerId, TimeSpan validFor, string? room = null)
        {
            var now = DateTime.UtcNow;
            return Create(secret, playerId, now, now + validFor, room);
        }

        /// <summary>Creates a token with explicit issue and expiry times (UTC).</summary>
        public static string Create(string secret, string playerId, DateTime issuedAtUtc, DateTime expiresAtUtc, string? room = null)
        {
            if (string.IsNullOrEmpty(secret)) throw new ArgumentException("the identity secret is required", nameof(secret));
            if (string.IsNullOrEmpty(playerId)) throw new ArgumentException("the player id is required", nameof(playerId));
            var err = Internal.Paths.CheckKey(playerId);
            if (err != null) throw new ArgumentException("the player id must be usable as a path key: " + err, nameof(playerId));

            var claims = new Dictionary<string, object?>
            {
                ["sub"] = playerId,
                ["iat"] = UnixSeconds(issuedAtUtc),
                ["exp"] = UnixSeconds(expiresAtUtc),
            };
            if (!string.IsNullOrEmpty(room)) claims["room"] = room;
            var header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
            var payload = Base64Url(Encoding.UTF8.GetBytes(BonfireJson.Serialize(claims)));
            byte[] signature;
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
                signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(header + "." + payload));
            return header + "." + payload + "." + Base64Url(signature);
        }

        /// <summary>
        /// Reads the player id from a token <b>without</b> checking its signature (only the server can do that).
        /// Returns null if the token isn't readable.
        /// </summary>
        public static string? ReadPlayerId(string token) => ReadClaim(token, "sub") as string;

        /// <summary>Reads the expiry from a token without checking its signature; null if missing or unreadable.</summary>
        public static DateTime? ReadExpiry(string token) =>
            ReadClaim(token, "exp") is long exp ? Epoch.AddSeconds(exp) : (DateTime?)null;

        private static object? ReadClaim(string token, string name)
        {
            try
            {
                var parts = token.Split('.');
                if (parts.Length != 3) return null;
                return BonfireJson.Parse(Encoding.UTF8.GetString(FromBase64Url(parts[1]))) is Dictionary<string, object?> d && d.TryGetValue(name, out var v) ? v : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static long UnixSeconds(DateTime utc) => (long)Math.Floor((utc.ToUniversalTime() - Epoch).TotalSeconds);

        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] FromBase64Url(string s)
        {
            var b = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(b + new string('=', (4 - b.Length % 4) % 4));
        }
    }
}
