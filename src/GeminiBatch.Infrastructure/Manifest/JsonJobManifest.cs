using System.Collections.Concurrent;
using System.Text.Json;
using GeminiBatch.Application;
using GeminiBatch.Application.Abstractions;

namespace GeminiBatch.Infrastructure.Manifest;

/// <summary>
/// JSON dictionary of manifest key → completion record, stored in the output folder (<see cref="OutputLocation.ManifestPath"/>,
/// resolved on every load so a folder picked between runs gets its own manifest). Writes are atomic (temp file + move).
/// </summary>
public sealed class JsonJobManifest : IJobManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly OutputLocation _location;
    private string _path;
    private readonly ConcurrentDictionary<string, ManifestEntry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonJobManifest(OutputLocation location)
    {
        _location = location;
        _path = location.ManifestPath;
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        _entries.Clear();
        _path = _location.ManifestPath;
        if (!File.Exists(_path)) return;

        await using var stream = File.OpenRead(_path);
        var loaded = await JsonSerializer.DeserializeAsync<Dictionary<string, ManifestEntry>>(stream, JsonOptions, ct).ConfigureAwait(false);
        if (loaded is null) return;

        foreach (var (key, entry) in loaded)
            _entries[key] = entry;
    }

    public bool IsCompleted(string key) => _entries.ContainsKey(key);

    public async Task MarkCompletedAsync(string key, string savedPath, CancellationToken ct)
    {
        _entries[key] = new ManifestEntry(savedPath, DateTimeOffset.UtcNow);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var snapshot = _entries.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var tempPath = _path + ".tmp";

            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, ct).ConfigureAwait(false);
            }

            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public sealed record ManifestEntry(string SavedPath, DateTimeOffset CompletedAtUtc);
}
