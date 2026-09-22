using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using GeminiBatch.Application.Abstractions;
using GeminiBatch.Domain;

namespace GeminiBatch.Infrastructure.Prompts;

public sealed class TextPromptSource : IPromptSource
{
    public IReadOnlyList<PromptJob> FromLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        return text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .Select(line => new PromptJob { Prompt = line })
            .ToList();
    }

    public IReadOnlyList<PromptJob> FromCsv(string path)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            PrepareHeaderForMatch = args => args.Header.Trim().ToLowerInvariant(),
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim,
        };

        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, config);

        var jobs = new List<PromptJob>();
        csv.Read();
        csv.ReadHeader();
        if (csv.HeaderRecord is null || !csv.HeaderRecord.Any(h => string.Equals(h.Trim(), "prompt", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("CSV must have a 'prompt' column.");

        while (csv.Read())
        {
            var prompt = csv.GetField("prompt");
            if (string.IsNullOrWhiteSpace(prompt)) continue;

            var fileName = csv.TryGetField<string>("filename", out var f) ? f : null;
            jobs.Add(new PromptJob
            {
                Prompt = prompt.Trim(),
                DesiredFileName = string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim(),
            });
        }

        return jobs;
    }
}
