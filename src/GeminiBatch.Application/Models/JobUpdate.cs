using GeminiBatch.Domain;

namespace GeminiBatch.Application.Models;

/// <summary>
/// A job's current state. Prompt, Section and DesiredFileName let the UI add a row for a job it has not seen yet
/// (the "until daily limit" mode creates jobs while the batch runs).
/// </summary>
public sealed record JobUpdate(
    Guid Id,
    JobStatus Status,
    int Attempts,
    string? AccountId,
    string? SavedPath,
    string? Error)
{
    public string Prompt { get; init; } = string.Empty;
    public string? Section { get; init; }
    public string? DesiredFileName { get; init; }

    public static JobUpdate From(PromptJob job) =>
        new(job.Id, job.Status, job.Attempts, job.AssignedAccountId, job.SavedPath, job.LastError)
        {
            Prompt = job.Prompt,
            Section = job.Section,
            DesiredFileName = job.DesiredFileName,
        };
}
