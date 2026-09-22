using GeminiBatch.Domain;

namespace GeminiBatch.Application.Models;

public sealed record JobUpdate(
    Guid Id,
    JobStatus Status,
    int Attempts,
    string? AccountId,
    string? SavedPath,
    string? Error)
{
    public static JobUpdate From(PromptJob job) =>
        new(job.Id, job.Status, job.Attempts, job.AssignedAccountId, job.SavedPath, job.LastError);
}
