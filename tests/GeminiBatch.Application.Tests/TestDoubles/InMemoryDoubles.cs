using System.Collections.Concurrent;
using GeminiBatch.Application.Abstractions;
using GeminiBatch.Application.Models;

namespace GeminiBatch.Application.Tests.TestDoubles;

public sealed class RecordingStorage : IImageStorage
{
    public ConcurrentQueue<(string Temp, string? BaseName)> Calls { get; } = new();

    public Task<string> SaveAsync(string tempFilePath, string? desiredBaseName, CancellationToken ct)
    {
        Calls.Enqueue((tempFilePath, desiredBaseName));
        if (File.Exists(tempFilePath)) File.Delete(tempFilePath);
        return Task.FromResult(Path.Combine("out", $"{desiredBaseName ?? "image"}.png"));
    }
}

public sealed class RecordingProcessor : IImageProcessor
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public Task StripMetadataAsync(string filePath, CancellationToken ct)
    {
        Calls.Enqueue(filePath);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryManifest : IJobManifest
{
    private readonly ConcurrentDictionary<string, string> _entries = new();

    public bool Loaded { get; private set; }
    public IReadOnlyDictionary<string, string> Entries => _entries;

    public InMemoryManifest(params string[] preCompletedKeys)
    {
        foreach (var key in preCompletedKeys) _entries[key] = "pre-existing";
    }

    public Task LoadAsync(CancellationToken ct)
    {
        Loaded = true;
        return Task.CompletedTask;
    }

    public bool IsCompleted(string key) => _entries.ContainsKey(key);

    public Task MarkCompletedAsync(string key, string savedPath, CancellationToken ct)
    {
        _entries[key] = savedPath;
        return Task.CompletedTask;
    }
}

/// <summary>Synchronous IProgress so tests see every update in order without a SynchronizationContext.</summary>
public sealed class CapturingProgress : IProgress<JobUpdate>
{
    public ConcurrentQueue<JobUpdate> Updates { get; } = new();

    public void Report(JobUpdate value) => Updates.Enqueue(value);

    public IEnumerable<JobUpdate> For(Guid jobId) => Updates.Where(u => u.Id == jobId);
}

/// <summary>Records the order in which the collaborators were invoked for a single job.</summary>
public sealed class CallOrder
{
    public ConcurrentQueue<string> Steps { get; } = new();
    public void Add(string step) => Steps.Enqueue(step);
}
