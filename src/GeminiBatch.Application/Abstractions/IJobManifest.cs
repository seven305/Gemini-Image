namespace GeminiBatch.Application.Abstractions;

/// <summary>Records completed jobs by <see cref="Domain.PromptJob.ManifestKey"/> so a re-run can skip them.</summary>
public interface IJobManifest
{
    Task LoadAsync(CancellationToken ct);
    bool IsCompleted(string key);
    Task MarkCompletedAsync(string key, string savedPath, CancellationToken ct);
}
