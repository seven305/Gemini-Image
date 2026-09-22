namespace GeminiBatch.Application.Options;

public sealed class BatchOptions
{
    public const string SectionName = "Batch";

    public int DefaultConcurrency { get; set; } = 5;
    public string OutputFolder { get; set; } = "output";
    public int MaxRetriesPerJob { get; set; } = 3;
    public double RetryBaseDelaySeconds { get; set; } = 5;
    public int AccountFailureThreshold { get; set; } = 3;
    public int GenerationTimeoutSeconds { get; set; } = 180;
    public int MinInterPromptDelayMs { get; set; } = 1000;
    public int MaxInterPromptDelayMs { get; set; } = 3000;
    public bool StripMetadata { get; set; } = true;
    public bool Headful { get; set; } = true;
    public string ManifestPath { get; set; } = Path.Combine("output", "manifest.json");
}
