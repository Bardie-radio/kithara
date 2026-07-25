using System.Security.Cryptography;
using System.Text;

namespace Kithara.Features.Streaming;

/// <summary>STREAM-TOK-001 — constant-time listen-token compare.</summary>
public static class ListenTokenComparer
{
    /// <summary>
    /// UTF-8 byte compare via <see cref="CryptographicOperations.FixedTimeEquals"/>.
    /// Null/empty either side or unequal lengths → <c>false</c> (no early string.Equals path).
    /// </summary>
    public static bool FixedTimeEquals(string? presented, string? expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(expected);
        if (a.Length != b.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
