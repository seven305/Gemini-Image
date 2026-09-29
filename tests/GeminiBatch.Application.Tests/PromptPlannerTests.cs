using GeminiBatch.Application.Options;
using GeminiBatch.Application.Tests.TestDoubles;
using GeminiBatch.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace GeminiBatch.Application.Tests;

public sealed class PromptPlannerTests
{
    private static PromptPlanner Planner(Action<BatchOptions>? configure = null)
    {
        var options = new BatchOptions { RandomizePrompts = true, MaxImagesPerAccount = 1 };
        configure?.Invoke(options);
        return new PromptPlanner(Microsoft.Extensions.Options.Options.Create(options));
    }

    private static GeminiAccount Account(string id, bool enabled = true, string? email = null) =>
        new() { Id = id, Email = email ?? $"{id}@example.com", UserDataDir = id, Enabled = enabled };

    private static GeminiAccount[] Accounts(int count) =>
        Enumerable.Range(1, count).Select(i => Account($"acc{i}")).ToArray();

    private static PromptJob[] Prompts(params string[] texts) => texts.Select(t => new PromptJob { Prompt = t }).ToArray();

    [Fact]
    public void Every_account_gets_one_job_with_a_prompt_from_the_input_even_when_prompts_are_fewer()
    {
        var jobs = Planner().Plan(Prompts("cat", "dog"), Accounts(5));

        Assert.Equal(5, jobs.Count);
        Assert.All(jobs, j => Assert.Contains(j.Prompt, new[] { "cat", "dog" }));
        Assert.Equal(5, jobs.Select(j => j.ManifestKey).Distinct().Count());
    }

    [Theory]
    [InlineData(2, 6)]
    [InlineData(0, 3)] // no cap still means one turn per account for planning
    public void Job_count_is_accounts_times_images_per_account(int imagesPerAccount, int expected)
    {
        var jobs = Planner(o => o.MaxImagesPerAccount = imagesPerAccount).Plan(Prompts("a", "b"), Accounts(3));

        Assert.Equal(expected, jobs.Count);
    }

    [Fact]
    public void Accounts_that_will_not_run_are_not_counted()
    {
        var quarantined = Account("q");
        quarantined.Quarantine("test");
        var accounts = new[] { Account("a"), Account("off", enabled: false), Account("dup", email: "A@example.com"), quarantined, Account("b") };

        Assert.Equal(2, Planner().Plan(Prompts("x"), accounts).Count);
    }

    [Fact]
    public void Same_prompts_and_accounts_give_the_same_plan_so_a_restart_resumes_exactly()
    {
        var prompts = Prompts("one", "two", "three", "four");

        var first = Planner().Plan(prompts, Accounts(12));
        var second = Planner().Plan(Prompts("one", "two", "three", "four"), Accounts(12));

        Assert.Equal(first.Select(j => j.ManifestKey), second.Select(j => j.ManifestKey));
        Assert.Equal(first.Select(j => j.Prompt), second.Select(j => j.Prompt));
    }

    [Fact]
    public void Prompts_are_actually_mixed()
    {
        var prompts = Prompts(Enumerable.Range(1, 20).Select(i => $"prompt {i}").ToArray());

        var jobs = Planner().Plan(prompts, Accounts(50));

        Assert.True(jobs.Select(j => j.Prompt).Distinct().Count() > 5);
        Assert.NotEqual(prompts.Select(p => p.Prompt).Take(20), jobs.Select(j => j.Prompt).Take(20)); // not just input order
    }

    [Fact]
    public void Disabled_randomization_returns_the_prompts_unchanged()
    {
        var prompts = Prompts("a", "b");

        var jobs = Planner(o => o.RandomizePrompts = false).Plan(prompts, Accounts(10));

        Assert.Same(prompts, jobs);
    }

    [Fact]
    public void Repeated_file_name_prompts_keep_the_name_and_get_numbered_keys()
    {
        var prompts = new[] { new PromptJob { Prompt = "castle", DesiredFileName = "castle" } };

        var jobs = Planner().Plan(prompts, Accounts(3));

        Assert.All(jobs, j => Assert.Equal("castle", j.DesiredFileName));
        Assert.Equal(["castle", "castle#2", "castle#3"], jobs.Select(j => j.ManifestKey).ToArray());
    }

    [Fact]
    public void First_occurrence_keeps_the_plain_key_later_ones_are_numbered()
    {
        var first = new PromptJob { Prompt = "sunset" };
        var second = new PromptJob { Prompt = "sunset", Occurrence = 2 };

        Assert.Equal(ManifestKeys.FromPrompt("sunset"), first.ManifestKey);
        Assert.Equal(ManifestKeys.FromPrompt("sunset") + "#2", second.ManifestKey);
    }

    [Fact]
    public async Task Planned_batch_runs_every_account_once_and_a_rerun_skips_them_all()
    {
        var accounts = Accounts(5);
        var manifest = new InMemoryManifest();
        var options = new BatchOptions
        {
            MaxImagesPerAccount = 1, RandomizePrompts = true, StartupStaggerMs = 0,
            MinInterPromptDelayMs = 0, MaxInterPromptDelayMs = 0, RetryBaseDelaySeconds = 0,
        };
        BatchProcessor Processor(ScriptedSessionFactory factory) => new(factory, new RecordingStorage(), new RecordingProcessor(), manifest,
            Microsoft.Extensions.Options.Options.Create(options), NullLogger<BatchProcessor>.Instance);
        var planner = new PromptPlanner(Microsoft.Extensions.Options.Options.Create(options));

        var factory = new ScriptedSessionFactory();
        var result = await Processor(factory).RunAsync(planner.Plan(Prompts("cat", "dog"), accounts), accounts, 1, new CapturingProgress(), CancellationToken.None);

        Assert.Equal((5, 5, 0, 0), (result.Total, result.Completed, result.Skipped, result.Failed));
        Assert.Equal(accounts.Select(a => a.Id), factory.LaunchOrder);

        var rerun = await Processor(new ScriptedSessionFactory())
            .RunAsync(planner.Plan(Prompts("cat", "dog"), Accounts(5)), Accounts(5), 1, new CapturingProgress(), CancellationToken.None);

        Assert.Equal((5, 0, 5, 0), (rerun.Total, rerun.Completed, rerun.Skipped, rerun.Failed));
    }
}
