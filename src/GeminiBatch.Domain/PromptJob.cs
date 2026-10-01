namespace GeminiBatch.Domain;

public sealed class PromptJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Prompt { get; init; }

    /// <summary>Optional base file name (no extension). Also used as the manifest key when present.</summary>
    public string? DesiredFileName { get; init; }

    /// <summary>Optional category (the prompts CSV "Section" column): the image is saved in this sub-folder of the output folder.</summary>
    public string? Section { get; init; }

    /// <summary>
    /// Resume key: the desired file name if given, otherwise a deterministic hash of the prompt. A section prefixes
    /// the key ("A/…"), so the same prompt in two sections is two jobs.
    /// </summary>
    public string ManifestKey
    {
        get
        {
            var key = string.IsNullOrWhiteSpace(DesiredFileName) ? ManifestKeys.FromPrompt(Prompt) : DesiredFileName.Trim();
            return string.IsNullOrWhiteSpace(Section) ? key : $"{Section.Trim()}/{key}";
        }
    }

    public JobStatus Status { get; set; } = JobStatus.Pending;
    public int Attempts { get; set; }
    public string? AssignedAccountId { get; set; }
    public string? SavedPath { get; set; }
    public string? LastError { get; set; }
}
