using GeminiBatch.Application.Models;

namespace GeminiBatch.Application.Abstractions;

/// <summary>One logged-in Gemini browser session bound to a single account. Owned by one worker for its lifetime.</summary>
public interface IGeminiSession : IAsyncDisposable
{
    string AccountId { get; }

    /// <summary>Navigate, verify sign-in, select the image model, etc. Called once before the first prompt.</summary>
    Task EnsureReadyAsync(CancellationToken ct);

    /// <summary>
    /// Submit a prompt and download the resulting image to a temp file.
    /// Throw <see cref="Exceptions.PermanentJobException"/> for failures that must not be retried.
    /// </summary>
    Task<GeneratedImage> GenerateAsync(string prompt, CancellationToken ct);
}
