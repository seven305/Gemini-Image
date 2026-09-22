namespace GeminiBatch.Domain;

public sealed class PromptJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Prompt { get; init; }

    /// <summary>Optional base file name (no extension). Also used as the manifest key when present.</summary>
    public string? DesiredFileName { get; init; }

    /// <summary>Resume key: the desired file name if given, otherwise a deterministic hash of the prompt.</summary>
    public string ManifestKey => string.IsNullOrWhiteSpace(DesiredFileName)
        ? ManifestKeys.FromPrompt(Prompt)
        : DesiredFileName.Trim();

    public JobStatus Status { get; set; } = JobStatus.Pending;
    public int Attempts { get; set; }
    public string? AssignedAccountId { get; set; }
    public string? SavedPath { get; set; }
    public string? LastError { get; set; }
}
