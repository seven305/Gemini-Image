using GeminiBatch.Domain;

namespace GeminiBatch.Application.Abstractions;

/// <summary>
/// First-run / re-auth flow: open the account's browser profile so a human can sign in (incl. 2FA),
/// then wait until the session is established. Nothing here automates the login itself.
/// </summary>
public interface IAccountLoginService
{
    /// <summary>
    /// Returns once the profile is signed in. Throws <see cref="TimeoutException"/> if nobody signs in
    /// within <paramref name="timeout"/>, or <see cref="OperationCanceledException"/> on cancel.
    /// </summary>
    Task LoginInteractiveAsync(GeminiAccount account, TimeSpan timeout, IProgress<string>? status, CancellationToken ct);
}
