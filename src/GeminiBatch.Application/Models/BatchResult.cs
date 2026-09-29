namespace GeminiBatch.Application.Models;

public enum BatchStopReason
{
    /// <summary>Every job reached a terminal state with at least one account still healthy.</summary>
    Finished,

    /// <summary>Every account was quarantined; jobs left over were failed.</summary>
    AllAccountsQuarantined,
}

public sealed record QuarantinedAccount(string Id, string Email, string Reason);

public sealed record BatchResult(int Total, int Completed, int Skipped, int Failed)
{
    /// <summary>Jobs never processed (e.g. cancelled before they were picked up).</summary>
    public int Remaining => Total - Completed - Skipped - Failed;

    public BatchStopReason StopReason { get; init; } = BatchStopReason.Finished;

    /// <summary>Accounts set aside during this run (failed sign-in, auth wall, repeated failures) and why.</summary>
    public IReadOnlyList<QuarantinedAccount> QuarantinedAccounts { get; init; } = [];
}
