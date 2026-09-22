namespace GeminiBatch.Application.Abstractions;

public interface IImageProcessor
{
    /// <summary>Remove EXIF/XMP/etc. from the file in place.</summary>
    Task StripMetadataAsync(string filePath, CancellationToken ct);
}
