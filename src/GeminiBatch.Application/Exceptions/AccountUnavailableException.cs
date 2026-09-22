namespace GeminiBatch.Application.Exceptions;

public enum AccountUnavailableReason
{
    /// <summary>Profile has no Google session, or Gemini shows the "Sign in" button.</summary>
    SignedOut,

    /// <summary>Redirected to a Google verification / challenge page (re-auth, CAPTCHA, "unusual activity").</summary>
    Challenged,

    /// <summary>The Gemini generation surface never appeared (blocked page, outage, or a UI change).</summary>
    SurfaceUnavailable,
}

/// <summary>
/// The account behind a session cannot be used right now. Never solved automatically: the worker
/// quarantines the account and a human re-authenticates the profile. Distinct from
/// <see cref="PermanentJobException"/>, which is about the job, not the account.
/// </summary>
public sealed class AccountUnavailableException : Exception
{
    public AccountUnavailableReason Reason { get; }

    public AccountUnavailableException(AccountUnavailableReason reason, string message) : base(message)
    {
        Reason = reason;
    }

    public AccountUnavailableException(AccountUnavailableReason reason, string message, Exception inner) : base(message, inner)
    {
        Reason = reason;
    }
}
