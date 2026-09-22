namespace GeminiBatch.Application.Models;

/// <param name="TempFilePath">Downloaded image on disk; the caller owns it from here on.</param>
/// <param name="SuggestedFileName">Base name (no extension) to use when the job has no DesiredFileName.</param>
public sealed record GeneratedImage(string TempFilePath, string SuggestedFileName);
