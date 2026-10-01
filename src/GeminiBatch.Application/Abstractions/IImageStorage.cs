namespace GeminiBatch.Application.Abstractions;

public interface IImageStorage
{
    /// <summary>
    /// Persist <paramref name="tempFilePath"/> into the output folder (or its <paramref name="subfolder"/>, e.g. the
    /// prompt's section; null = the folder itself) under a collision-safe name derived from
    /// <paramref name="desiredBaseName"/> (no extension; null = implementation default).
    /// Consumes the temp file (move or copy); callers delete it afterwards if it still exists.
    /// Returns the final full path. Never overwrites an existing file.
    /// </summary>
    Task<string> SaveAsync(string tempFilePath, string? desiredBaseName, string? subfolder, CancellationToken ct);
}
