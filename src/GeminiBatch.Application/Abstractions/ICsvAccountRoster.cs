using GeminiBatch.Domain;

namespace GeminiBatch.Application.Abstractions;

/// <summary>
/// Builds an account roster from a CSV the operator supplies: <c>email, password, recovery email, 2FA key, proxy</c>.
/// Each email maps to a browser profile directory; the secrets become the account's in-memory credentials for
/// automated sign-in (never persisted) and the optional proxy URL its browser proxy. A row without a password
/// works only while its profile is still signed in; otherwise the batch skips that account.
/// </summary>
public interface ICsvAccountRoster
{
    /// <summary>
    /// Reads accounts from <paramref name="csvPath"/>. The first column is the email; a header row and
    /// blank lines are skipped; missing trailing columns are allowed. Throws if the file is missing, contains no
    /// usable email, or has a malformed proxy URL.
    /// </summary>
    IReadOnlyList<GeminiAccount> Read(string csvPath);
}
