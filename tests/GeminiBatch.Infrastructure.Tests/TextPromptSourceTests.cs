using GeminiBatch.Infrastructure.Prompts;

namespace GeminiBatch.Infrastructure.Tests;

public sealed class TextPromptSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "geminibatch-prompt-tests", Guid.NewGuid().ToString("N"));

    private string WriteCsv(string content)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "prompts.csv");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Pipe_delimited_csv_maps_filenames_and_keeps_commas_in_prompts()
    {
        var path = WriteCsv(
            "Filename | Prompt\n" +
            "cat | A cat, sitting on a mat\n" +
            "\n" +
            " | A dog without a file name\n");

        var jobs = new TextPromptSource().FromCsv(path);

        Assert.Equal(2, jobs.Count);
        Assert.Equal("cat", jobs[0].DesiredFileName);
        Assert.Equal("A cat, sitting on a mat", jobs[0].Prompt);
        Assert.Null(jobs[1].DesiredFileName);
        Assert.Equal("A dog without a file name", jobs[1].Prompt);
    }

    [Fact]
    public void Comma_delimited_csv_maps_filenames()
    {
        var path = WriteCsv("filename,prompt\nsunset,\"A sunset, over water\"\n,Just a prompt\n");

        var jobs = new TextPromptSource().FromCsv(path);

        Assert.Equal(["sunset", null], jobs.Select(j => j.DesiredFileName));
        Assert.Equal(["A sunset, over water", "Just a prompt"], jobs.Select(j => j.Prompt));
    }

    [Fact]
    public void Csv_without_prompt_column_is_rejected()
    {
        var path = WriteCsv("name|text\nx|y\n");

        Assert.Throws<InvalidDataException>(() => new TextPromptSource().FromCsv(path));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
