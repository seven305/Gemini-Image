using System.Globalization;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using GeminiBatch.Application.Abstractions;
using GeminiBatch.Domain;

namespace GeminiBatch.Infrastructure.Prompts;

public sealed partial class TextPromptSource : IPromptSource
{
    private const string ImagePromptColumn = "image prompt";
    private const string IdColumn = "#";
    private const string PromptColumn = "prompt";

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
            Delimiter = DetectDelimiter(path),
        };

        using var reader = OpenShared(path);
        using var csv = new CsvReader(reader, config);

        var jobs = new List<PromptJob>();
        csv.Read();
        csv.ReadHeader();

        // "Image Prompt" + "Section" + optional "#" id (the client layout; the id names the image, other columns are
        // ignored), or the older "prompt" + optional "filename" layout.
        var headers = csv.HeaderRecord?.Select(h => h.Trim().ToLowerInvariant()).ToHashSet() ?? [];
        var promptColumn = headers.Contains(ImagePromptColumn) ? ImagePromptColumn
            : headers.Contains(PromptColumn) ? PromptColumn
            : throw new InvalidDataException("CSV must have an 'Image Prompt' column.");
        var isClientLayout = promptColumn == ImagePromptColumn;

        while (csv.Read())
        {
            var prompt = csv.GetField(promptColumn);
            if (string.IsNullOrWhiteSpace(prompt)) continue;

            var fileName = isClientLayout
                ? csv.TryGetField<string>(IdColumn, out var id) ? FileNameFromId(id) : null
                : csv.TryGetField<string>("filename", out var f) ? f : null;
            var section = csv.TryGetField<string>("section", out var s) ? s : null;
            jobs.Add(new PromptJob
            {
                Prompt = prompt.Trim(),
                DesiredFileName = string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim(),
                Section = string.IsNullOrWhiteSpace(section) ? null : section.Trim(),
            });
        }

        return jobs;
    }

    /// <summary>
    /// The image name for a client "#" id: a dash goes between a letter prefix and its number ("A2" -> "A-2"); any
    /// other id ("C-SPECIAL-1") is used as-is. Blank -> null (the image gets the default name).
    /// </summary>
    private static string? FileNameFromId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        id = id.Trim();
        var match = LettersThenDigits().Match(id);
        return match.Success ? $"{match.Groups[1].Value}-{match.Groups[2].Value}" : id;
    }

    [GeneratedRegex(@"^([A-Za-z]+)(\d+)$")]
    private static partial Regex LettersThenDigits();

    /// <summary>
    /// Picks the delimiter from the header line: "Filename | Prompt" and tab/semicolon files are accepted as well as
    /// commas. Only the header is inspected because prompts themselves routinely contain commas.
    /// </summary>
    private static string DetectDelimiter(string path)
    {
        string? header;
        using (var reader = OpenShared(path))
        {
            while ((header = reader.ReadLine()) is not null && string.IsNullOrWhiteSpace(header)) { }
        }
        header ??= string.Empty;
        foreach (var candidate in new[] { "|", "	", ";" })
        {
            if (header.Contains(candidate, StringComparison.Ordinal))
                return candidate;
        }
        return ",";
    }

    /// <summary>FileShare.ReadWrite so a CSV left open in Excel (an exclusive-ish lock) can still be read.</summary>
    private static StreamReader OpenShared(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
}
