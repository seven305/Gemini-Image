using GeminiBatch.Domain;

namespace GeminiBatch.Application.Abstractions;

public interface IPromptSource
{
    /// <summary>
    /// CSV with an "Image Prompt" column and an optional "Section" column (the sub-folder the image is saved in);
    /// other columns are ignored. The older layout, a "prompt" column with an optional "filename" column, still loads.
    /// </summary>
    IReadOnlyList<PromptJob> FromCsv(string path);

    /// <summary>One prompt per line; blank lines and lines starting with '#' are ignored.</summary>
    IReadOnlyList<PromptJob> FromLines(string text);
}
