namespace GeminiBatch.Application.Options;

public sealed class BatchOptions
{
    public const string SectionName = "Batch";

    public int DefaultConcurrency { get; set; } = 5;

    /// <summary>Default output folder; the operator can pick another in the UI. The resume manifest lives inside it.</summary>
    public string OutputFolder { get; set; } = "output";
    public int MaxRetriesPerJob { get; set; } = 3;
    public double RetryBaseDelaySeconds { get; set; } = 5;
    public int AccountFailureThreshold { get; set; } = 3;
    public int GenerationTimeoutSeconds { get; set; } = 180;
    public int MinInterPromptDelayMs { get; set; } = 1000;
    public int MaxInterPromptDelayMs { get; set; } = 3000;

    /// <summary>Worker i waits about i × this (plus jitter) before launching, so sessions don't hit Gemini in lockstep. 0 = off.</summary>
    public int StartupStaggerMs { get; set; } = 4000;

    /// <summary>How many times a worker relaunches a session whose browser died before quarantining its account.</summary>
    public int MaxSessionRestarts { get; set; } = 1;

    public bool StripMetadata { get; set; } = true;
    public bool Headful { get; set; } = true;

    /// <summary>Use the offline fake session instead of the real browser (testing without Google accounts).</summary>
    public bool UseFakeSession { get; set; }
}
