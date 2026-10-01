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
    public void Image_prompt_csv_maps_the_section_and_ignores_the_other_columns()
    {
        var path = WriteCsv(
            "#,Section,Blend Ratio,Woman Type,Flag Type,Image Prompt\n" +
            "A2,A,Mixed — Cash & Wealth Signals,N,OBJECT,\"A kitchen, warm light. A mug reading \"\"Hallo\"\".\"\n" +
            "B1, B ,Cash Dominant,N,FABRIC,An older man\n" +
            "X1,,Other,N,N,No section\n" +
            ",\n");

        var jobs = new TextPromptSource().FromCsv(path);

        Assert.Equal(3, jobs.Count);
        Assert.Equal("A kitchen, warm light. A mug reading \"Hallo\".", jobs[0].Prompt);
        Assert.Equal(["A", "B", null], jobs.Select(j => j.Section));
        Assert.All(jobs, j => Assert.Null(j.DesiredFileName));
    }

    [Fact]
    public void Csv_left_open_for_writing_by_another_program_still_loads()
    {
        var path = WriteCsv("Section,Image Prompt\nA,A cat\n");
        using var heldOpen = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); // like Excel

        var jobs = new TextPromptSource().FromCsv(path);

        Assert.Equal("A cat", Assert.Single(jobs).Prompt);
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
