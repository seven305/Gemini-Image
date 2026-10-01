using GeminiBatch.Domain;

namespace GeminiBatch.Application.Tests;

public sealed class PromptFeedTests
{
    private static PromptJob Prompt(string text, string? section = null) => new() { Prompt = text, Section = section };

    [Fact]
    public void Jobs_come_only_from_the_given_prompts_and_keep_their_section()
    {
        var feed = new PromptFeed([Prompt("cat", "A"), Prompt("dog", "B")], new Random(1));

        var jobs = Enumerable.Range(0, 50).Select(_ => feed.Next(_ => false)).ToList();

        Assert.All(jobs, j => Assert.Equal(j.Prompt == "cat" ? "A" : "B", j.Section));
        Assert.Contains(jobs, j => j.Prompt == "cat");
        Assert.Contains(jobs, j => j.Prompt == "dog");
        Assert.Equal(50, jobs.Select(j => j.ManifestKey).Distinct().Count());
    }

    [Fact]
    public void Occurrences_skip_keys_that_are_already_taken()
    {
        var feed = new PromptFeed([Prompt("cat")]);
        var taken = new HashSet<string> { Prompt("cat").ManifestKey, new PromptJob { Prompt = "cat", Occurrence = 2 }.ManifestKey };

        var first = feed.Next(taken.Contains);
        var second = feed.Next(taken.Contains);

        Assert.Equal(3, first.Occurrence);
        Assert.Equal(4, second.Occurrence);
    }

    [Fact]
    public void Identical_prompts_share_one_occurrence_sequence()
    {
        var feed = new PromptFeed([Prompt("cat"), Prompt("cat")]);

        var keys = Enumerable.Range(0, 10).Select(_ => feed.Next(_ => false).ManifestKey).ToList();

        Assert.Equal(10, keys.Distinct().Count());
    }

    [Fact]
    public void An_empty_prompt_list_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new PromptFeed([]));
}
