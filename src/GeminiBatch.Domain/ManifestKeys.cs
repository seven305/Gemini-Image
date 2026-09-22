using System.Text;

namespace GeminiBatch.Domain;

/// <summary>
/// Deterministic manifest keys. string.GetHashCode is randomized per process, so we use
/// 64-bit FNV-1a over the UTF-8 bytes of the trimmed prompt instead.
/// </summary>
public static class ManifestKeys
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    public static string FromPrompt(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var bytes = Encoding.UTF8.GetBytes(prompt.Trim());
        var hash = FnvOffsetBasis;
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= FnvPrime;
        }
        return hash.ToString("x16");
    }
}
