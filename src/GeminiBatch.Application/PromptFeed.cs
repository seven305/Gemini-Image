using GeminiBatch.Domain;

namespace GeminiBatch.Application;

/// <summary>
/// The endless job source of the "until daily limit" mode: every call makes a job from a randomly picked prompt
/// (repeats allowed). Each job gets the next <see cref="PromptJob.Occurrence"/> of its prompt whose manifest key is
/// still free, so a new image is never mistaken for one finished in an earlier run. Thread-safe.
/// </summary>
public sealed class PromptFeed
{
    private readonly IReadOnlyList<PromptJob> _prompts;
    private readonly Random _random;
    // Last occurrence handed out per base key (the key of occurrence 1); identical prompts share one sequence.
    private readonly Dictionary<string, int> _lastOccurrence = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public PromptFeed(IReadOnlyList<PromptJob> prompts, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        if (prompts.Count == 0) throw new ArgumentException("At least one prompt is required.", nameof(prompts));
        _prompts = prompts;
        _random = random ?? new Random();
    }

    /// <param name="isTaken">True for a manifest key that is already completed (e.g. by an earlier run).</param>
    public PromptJob Next(Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(isTaken);
        lock (_lock)
        {
            var template = _prompts[_random.Next(_prompts.Count)];
            var baseKey = Create(template, 1).ManifestKey;
            var occurrence = _lastOccurrence.GetValueOrDefault(baseKey);

            PromptJob job;
            do job = Create(template, ++occurrence);
            while (isTaken(job.ManifestKey));

            _lastOccurrence[baseKey] = occurrence;
            return job;
        }
    }

    private static PromptJob Create(PromptJob template, int occurrence) => new()
    {
        Prompt = template.Prompt,
        DesiredFileName = template.DesiredFileName,
        Section = template.Section,
        Occurrence = occurrence,
    };
}
