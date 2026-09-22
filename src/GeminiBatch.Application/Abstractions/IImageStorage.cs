namespace GeminiBatch.Application.Abstractions;

public interface IImageStorage
{
    /// <summary>
    /// Persist <paramref name="tempFilePath"/> into the output folder under a collision-safe name
    /// derived from <paramref name="desiredBaseName"/> (no extension; null = implementation default).
    /// Consumes the temp file (move or copy); callers delete it afterwards if it still exists.
    /// Returns the final full path. Never overwrites an existing file.
    /// </summary>
    Task<string> SaveAsync(string tempFilePath, string? desiredBaseName, CancellationToken ct);
}
