using System;

namespace FreeSync
{
    /// <summary>An operation was refused by the server or could not be completed.</summary>
    public sealed class FreeSyncException : Exception
    {
        /// <summary>
        /// The error code, e.g. <c>permission_denied</c>, <c>rate_limited</c>, <c>write_quota</c>, <c>room_quota</c>,
        /// <c>bad_path</c>, <c>queue_full</c>, <c>disposed</c>, <c>quota</c>, <c>http_401</c>. See docs/PROTOCOL.md.
        /// </summary>
        public string Code { get; }

        /// <summary>Creates an exception with a code and message.</summary>
        public FreeSyncException(string code, string message) : base(message) => Code = code;

        /// <summary>True for errors caused by an exhausted daily quota (they clear at 00:00 UTC).</summary>
        public bool IsQuota => Code == "write_quota" || Code == "room_quota" || Code == "quota";
    }
}
