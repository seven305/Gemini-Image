using GeminiBatch.Application.Options;
using Microsoft.Extensions.Options;

namespace GeminiBatch.Application;

/// <summary>
/// The output folder for the current run: starts as <see cref="BatchOptions.OutputFolder"/> and the operator can
/// change it between runs. Images (in per-section sub-folders) and the resume manifest live under it, so each output
/// folder resumes on its own. Change it only while no batch is running.
/// </summary>
public sealed class OutputLocation
{
    public const string ManifestFileName = "manifest.json";

    private string _root;

    public OutputLocation(IOptions<BatchOptions> options)
    {
        _root = Path.GetFullPath(options.Value.OutputFolder);
    }

    /// <summary>Full path of the output folder.</summary>
    public string Root
    {
        get => _root;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _root = Path.GetFullPath(value);
        }
    }

    public string ManifestPath => Path.Combine(Root, ManifestFileName);
}
