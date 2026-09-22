using GeminiBatch.Application.Abstractions;

namespace GeminiBatch.Infrastructure.Processing;

/// <summary>Phase 1 placeholder; Phase 2 replaces this with a Magick.NET implementation.</summary>
public sealed class NullImageProcessor : IImageProcessor
{
    public Task StripMetadataAsync(string filePath, CancellationToken ct) => Task.CompletedTask;
}
