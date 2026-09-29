using System.Buffers.Binary;
using System.Security.Cryptography;

namespace GeminiBatch.Infrastructure.Accounts;

/// <summary>
/// RFC 6238 time-based one-time passwords (HMAC-SHA1, 30 s step, 6 digits) — what Google Authenticator
/// computes from the base32 key Google shows at 2-Step Verification setup.
/// </summary>
public static class TotpGenerator
{
    public const int StepSeconds = 30;

    /// <summary>The code for <paramref name="now"/> plus the time step it belongs to (to avoid reusing a code).</summary>
    public static (string Code, long TimeStep) Compute(string base32Secret, DateTimeOffset now, int digits = 6)
    {
        var key = DecodeBase32(base32Secret);
        var timeStep = now.ToUnixTimeSeconds() / StepSeconds;

        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, timeStep);
        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(key, counter, hash);

        var offset = hash[^1] & 0x0F;
        var binary = (BinaryPrimitives.ReadInt32BigEndian(hash[offset..]) & 0x7FFFFFFF) % (int)Math.Pow(10, digits);
        return (binary.ToString().PadLeft(digits, '0'), timeStep);
    }

    /// <summary>Time left in the current step, so a caller can wait out a code that is about to expire.</summary>
    public static TimeSpan RemainingInStep(DateTimeOffset now) =>
        TimeSpan.FromSeconds(StepSeconds - now.ToUnixTimeSeconds() % StepSeconds);

    /// <summary>RFC 4648 base32; case-insensitive, ignores spaces, dashes and '=' padding.</summary>
    public static byte[] DecodeBase32(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var output = new List<byte>(input.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var raw in input)
        {
            if (raw is ' ' or '-' or '=') continue;
            var value = alphabet.IndexOf(char.ToUpperInvariant(raw));
            if (value < 0)
                throw new FormatException("The 2FA key is not valid base32.");

            // At most 12 unread bits are ever pending (7 + 5); masking keeps the int from overflowing.
            buffer = ((buffer << 5) | value) & 0xFFF;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(buffer >> bits));
            }
        }

        if (output.Count == 0)
            throw new FormatException("The 2FA key is empty.");
        return [.. output];
    }
}
