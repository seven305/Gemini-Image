using GeminiBatch.Domain;

namespace GeminiBatch.Application.Abstractions;

/// <summary>
/// Builds an account roster from a CSV the operator supplies. Only the email column is read; each email
/// maps to a browser profile directory. Passwords / 2FA secrets in the file are ignored — sign-in stays
/// manual (the human signs each profile in once via the headful window).
/// </summary>
public interface ICsvAccountRoster
{
    /// <summary>
    /// Reads accounts from <paramref name="csvPath"/>. The first column is the email; a header row and
    /// blank lines are skipped. Throws if the file is missing or contains no usable email.
    /// </summary>
    IReadOnlyList<GeminiAccount> Read(string csvPath);
}
