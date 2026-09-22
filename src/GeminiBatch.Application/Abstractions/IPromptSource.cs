using GeminiBatch.Domain;

namespace GeminiBatch.Application.Abstractions;

public interface IPromptSource
{
    /// <summary>CSV with a required "prompt" column and an optional "filename" column.</summary>
    IReadOnlyList<PromptJob> FromCsv(string path);

    /// <summary>One prompt per line; blank lines and lines starting with '#' are ignored.</summary>
    IReadOnlyList<PromptJob> FromLines(string text);
}
