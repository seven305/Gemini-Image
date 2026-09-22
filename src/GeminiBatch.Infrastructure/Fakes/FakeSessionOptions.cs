namespace GeminiBatch.Infrastructure.Fakes;

/// <summary>
/// Knobs for the Phase 1 fake session. Bind from the "Fake" section, or override via environment
/// variables (e.g. GEMINIBATCH_Fake__FailureRate=0.3) to exercise retry and quarantine paths.
/// </summary>
public sealed class FakeSessionOptions
{
    public const string SectionName = "Fake";

    /// <summary>Probability (0..1) that any single GenerateAsync call throws a transient failure.</summary>
    public double FailureRate { get; set; }

    public int MinDelayMs { get; set; } = 500;
    public int MaxDelayMs { get; set; } = 1500;

    /// <summary>Accounts whose GenerateAsync always fails — drives the quarantine path deterministically.</summary>
    public string[] AlwaysFailAccountIds { get; set; } = [];

    /// <summary>Accounts whose EnsureReadyAsync throws — simulates a signed-out / broken profile.</summary>
    public string[] ReadyFailAccountIds { get; set; } = [];
}
