using System.Security.Cryptography;
using System.Text;

namespace Kithara.Features.Auth;

/// <summary>Host-owned registration OTP generation and verification (AUTH-INVITE).</summary>
public static class InviteOtp
{
    private const int OtpBytes = 24;
    private const int SaltBytes = 16;
    private const int Iterations = 100_000;

    /// <summary>High-entropy URL-safe OTP (plaintext — return once, never persist).</summary>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(OtpBytes);
        return Base64Url(bytes);
    }

    public static string Hash(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var derived = Pbkdf2(plaintext, salt);
        return $"{Base64Url(salt)}.{Base64Url(derived)}";
    }

    public static bool Verify(string? storedHash, string plaintext)
    {
        if (string.IsNullOrWhiteSpace(storedHash) || string.IsNullOrWhiteSpace(plaintext))
        {
            return false;
        }

        var parts = storedHash.Split('.', 2);
        if (parts.Length != 2
            || !TryDecodeBase64Url(parts[0], out var salt)
            || !TryDecodeBase64Url(parts[1], out var expected))
        {
            return false;
        }

        var actual = Pbkdf2(plaintext, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Pbkdf2(string plaintext, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(plaintext),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            32);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecodeBase64Url(string value, out byte[] bytes)
    {
        bytes = [];
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2:
                    padded += "==";
                    break;
                case 3:
                    padded += "=";
                    break;
            }

            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
