namespace GeminiBatch.Domain;

public sealed class GeminiAccount
{
    public required string Id { get; init; }
    public required string Email { get; init; }
    public required string UserDataDir { get; init; }
    public ProxySettings? Proxy { get; init; }
    public bool Enabled { get; init; } = true;

    // Runtime-only state: never persisted. Reset by constructing a fresh account list.
    public bool IsQuarantined { get; private set; }
    public string? QuarantineReason { get; private set; }

    public void Quarantine(string reason)
    {
        IsQuarantined = true;
        QuarantineReason = reason;
    }

    public override string ToString() => $"{Id} ({Email})";
}
