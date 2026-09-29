namespace GeminiBatch.Domain;

/// <summary>
/// Google sign-in secrets for one account, read from the operator's CSV roster. Held in memory only —
/// never persisted, serialized or logged (<see cref="ToString"/> is redacted for that reason).
/// </summary>
/// <param name="Password">The Google account password.</param>
/// <param name="RecoveryEmail">Answers the "Confirm your recovery email" challenge, if Google asks.</param>
/// <param name="TotpSecret">Base32 authenticator key (spaces allowed), used to compute 2-Step Verification codes.</param>
public sealed record AccountCredentials(string Password, string? RecoveryEmail, string? TotpSecret)
{
    public bool HasTotp => !string.IsNullOrWhiteSpace(TotpSecret);

    public override string ToString() =>
        $"AccountCredentials {{ Password = ***, RecoveryEmail = {(RecoveryEmail is null ? "none" : "***")}, TotpSecret = {(HasTotp ? "***" : "none")} }}";
}
