using GeminiBatch.Application.Options;
using GeminiBatch.Domain;
using Microsoft.Extensions.Options;

namespace GeminiBatch.Application;

/// <summary>
/// Turns the operator's prompts into the batch's jobs. With <see cref="BatchOptions.RandomizePrompts"/> every account
/// turn gets a randomly picked prompt (repeats allowed): 5 prompts over 50 accounts is 50 images. The random seed is
/// derived from the prompts and the accounts, so re-planning the same batch after a crash or Stop yields the same jobs
/// and manifest keys, and the resume skips exactly what was already generated.
/// </summary>
public sealed class PromptPlanner
{
    private readonly BatchOptions _options;

    public PromptPlanner(IOptions<BatchOptions> options)
    {
        _options = options.Value;
    }

    public IReadOnlyList<PromptJob> Plan(IReadOnlyList<PromptJob> prompts, IReadOnlyList<GeminiAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(accounts);
        if (!_options.RandomizePrompts || prompts.Count == 0)
            return prompts;

        var eligible = AccountEligibility.Select(accounts);
        var turns = eligible.Count * Math.Max(1, _options.MaxImagesPerAccount);
        var random = new Random(Seed(prompts, eligible));
        var uses = new Dictionary<PromptJob, int>(ReferenceEqualityComparer.Instance);

        var jobs = new List<PromptJob>(turns);
        for (var i = 0; i < turns; i++)
        {
            var template = prompts[random.Next(prompts.Count)];
            var occurrence = uses[template] = uses.GetValueOrDefault(template) + 1;
            jobs.Add(new PromptJob
            {
                Prompt = template.Prompt,
                DesiredFileName = template.DesiredFileName,
                Occurrence = occurrence,
            });
        }
        return jobs;
    }

    /// <summary>Stable across processes (string.GetHashCode is not): FNV-1a over the prompts and the account emails.</summary>
    private static int Seed(IReadOnlyList<PromptJob> prompts, IReadOnlyList<GeminiAccount> accounts)
    {
        var material = string.Join('\n', prompts.Select(p => $"{p.DesiredFileName}|{p.Prompt}"))
            + "\n--\n" + string.Join('\n', accounts.Select(a => a.Email.Trim().ToLowerInvariant()));
        var hash = Convert.ToUInt64(ManifestKeys.FromPrompt(material), 16);
        return (int)(hash ^ (hash >> 32));
    }
}
