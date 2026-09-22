using GeminiBatch.Application.Abstractions;
using GeminiBatch.Application.Options;
using Microsoft.Extensions.Options;

namespace GeminiBatch.Infrastructure.Storage;

/// <summary>
/// Saves images under <see cref="BatchOptions.OutputFolder"/> as name.ext, name_2.ext, name_3.ext...
/// A name is reserved by opening it with <see cref="FileMode.CreateNew"/>, which is atomic on every
/// platform, so concurrent workers (or other processes) can never overwrite each other.
/// </summary>
public sealed class FileSystemImageStorage : IImageStorage
{
    private const int MaxBaseNameLength = 100;
    private const string DefaultBaseName = "image";
    private const string DefaultExtension = ".png";

    private readonly string _outputFolder;
    private readonly SemaphoreSlim _reserveLock = new(1, 1);

    public FileSystemImageStorage(IOptions<BatchOptions> options)
    {
        _outputFolder = Path.GetFullPath(options.Value.OutputFolder);
    }

    public async Task<string> SaveAsync(string tempFilePath, string? desiredBaseName, CancellationToken ct)
    {
        if (!File.Exists(tempFilePath))
            throw new FileNotFoundException("Temp image not found.", tempFilePath);

        Directory.CreateDirectory(_outputFolder);

        var baseName = Sanitize(desiredBaseName);
        var extension = Path.GetExtension(tempFilePath);
        if (string.IsNullOrEmpty(extension)) extension = DefaultExtension;

        var (finalPath, reserved) = await ReserveAsync(baseName, extension, ct).ConfigureAwait(false);

        try
        {
            await using (reserved.ConfigureAwait(false))
            {
                var source = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await using (source.ConfigureAwait(false))
                {
                    await source.CopyToAsync(reserved, ct).ConfigureAwait(false);
                }
                await reserved.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // Do not leave a half-written reservation behind; the caller will retry and reserve a fresh name.
            try { File.Delete(finalPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }

        try { File.Delete(tempFilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }

        return finalPath;
    }

    private async Task<(string Path, FileStream Stream)> ReserveAsync(string baseName, string extension, CancellationToken ct)
    {
        await _reserveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var n = 1; ; n++)
            {
                var fileName = n == 1 ? $"{baseName}{extension}" : $"{baseName}_{n}{extension}";
                var candidate = Path.Combine(_outputFolder, fileName);
                try
                {
                    var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    return (candidate, stream);
                }
                catch (IOException) when (File.Exists(candidate))
                {
                    // Taken (by us earlier, another worker, or another process). Try the next suffix.
                }
            }
        }
        finally
        {
            _reserveLock.Release();
        }
    }

    private static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return DefaultBaseName;

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim('.', ' ', '_');
        if (cleaned.Length == 0) return DefaultBaseName;
        return cleaned.Length > MaxBaseNameLength ? cleaned[..MaxBaseNameLength] : cleaned;
    }
}
