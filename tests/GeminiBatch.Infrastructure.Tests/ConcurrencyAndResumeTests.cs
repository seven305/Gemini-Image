using GeminiBatch.Application;
using GeminiBatch.Application.Models;
using GeminiBatch.Application.Options;
using GeminiBatch.Domain;
using GeminiBatch.Infrastructure.Fakes;
using GeminiBatch.Infrastructure.Manifest;
using GeminiBatch.Infrastructure.Processing;
using GeminiBatch.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GeminiBatch.Infrastructure.Tests;

/// <summary>
/// Phase 3 guarantees on the real file-system pieces: collision-safe naming under contention, a manifest that
/// survives concurrent writers, and a cancelled batch that resumes without duplicates or overwrites.
/// </summary>
public sealed class ConcurrencyAndResumeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "geminibatch-phase3-tests", Guid.NewGuid().ToString("N"));

    private string OutputDir => Path.Combine(_dir, "output");

    private IOptions<BatchOptions> BatchOptions() => Options.Create(new BatchOptions
    {
        OutputFolder = OutputDir,
        ManifestPath = Path.Combine(OutputDir, "manifest.json"),
        MaxRetriesPerJob = 1,
        RetryBaseDelaySeconds = 0,
        MinInterPromptDelayMs = 0,
        MaxInterPromptDelayMs = 5,
        StartupStaggerMs = 0,
        MaxImagesPerAccount = 0, // 3 fake accounts finish all 20 jobs; rotation is covered in the Application tests
        StripMetadata = false,
    });

    private string TempImage()
    {
        var path = Path.Combine(_dir, "temp", $"{Guid.NewGuid():N}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        return path;
    }

    [Fact]
    public async Task Parallel_saves_with_one_name_never_collide_or_overwrite()
    {
        var storage = new FileSystemImageStorage(BatchOptions());
        Directory.CreateDirectory(OutputDir);
        var existing = Path.Combine(OutputDir, "cat.png");
        await File.WriteAllTextAsync(existing, "pre-existing");

        var temps = Enumerable.Range(0, 50).Select(_ => TempImage()).ToList();
        var saved = await Task.WhenAll(temps.Select(t => Task.Run(() => storage.SaveAsync(t, "cat", CancellationToken.None))));

        Assert.Equal(50, saved.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(existing, saved, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("pre-existing", await File.ReadAllTextAsync(existing));
        var expected = Enumerable.Range(2, 50).Select(n => $"cat_{n}.png").Order().ToArray();
        Assert.Equal(expected, saved.Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public async Task Concurrent_manifest_writes_are_all_persisted()
    {
        var manifest = new JsonJobManifest(BatchOptions());
        await manifest.LoadAsync(CancellationToken.None);

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            Task.Run(() => manifest.MarkCompletedAsync($"key{i}", $"file{i}.png", CancellationToken.None))));

        var reloaded = new JsonJobManifest(BatchOptions());
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.All(Enumerable.Range(0, 40), i => Assert.True(reloaded.IsCompleted($"key{i}")));
    }

    [Fact]
    public async Task Cancelled_batch_resumes_without_duplicates_or_overwrites()
    {
        const int jobCount = 20;
        var accounts = () => Enumerable.Range(1, 3)
            .Select(i => new GeminiAccount { Id = $"acc{i}", Email = $"acc{i}@example.com", UserDataDir = Path.Combine(_dir, $"acc{i}") })
            .ToList();
        var jobs = () => Enumerable.Range(1, jobCount)
            .Select(i => new PromptJob { Prompt = $"prompt {i}", DesiredFileName = $"img{i}" })
            .ToList();

        // Run 1: stopped after a handful of completions.
        using var cts = new CancellationTokenSource();
        var completedInRun1 = 0;
        var stopAfterFive = new InlineProgress(u =>
        {
            if (u.Status == JobStatus.Completed && Interlocked.Increment(ref completedInRun1) == 5)
                cts.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewProcessor().RunAsync(jobs(), accounts(), 3, stopAfterFive, cts.Token));

        var manifestAfterStop = await ReadManifestKeysAsync();
        Assert.Equal(Volatile.Read(ref completedInRun1), manifestAfterStop.Count);
        Assert.Equal(manifestAfterStop.Count, ImageFiles().Length); // no saved file without an entry, and vice versa

        // Run 2: fresh processor + manifest (as after an app restart).
        var result = await NewProcessor().RunAsync(jobs(), accounts(), 3, new InlineProgress(_ => { }), CancellationToken.None);

        Assert.Equal(manifestAfterStop.Count, result.Skipped);
        Assert.Equal(jobCount - manifestAfterStop.Count, result.Completed);
        Assert.Equal(0, result.Failed);
        var files = ImageFiles().Select(Path.GetFileNameWithoutExtension).Order().ToArray();
        Assert.Equal(Enumerable.Range(1, jobCount).Select(i => $"img{i}").Order().ToArray(), files); // no img3_2 etc.
        Assert.Equal(jobCount, (await ReadManifestKeysAsync()).Count);
    }

    private BatchProcessor NewProcessor()
    {
        var options = BatchOptions();
        var fake = Options.Create(new FakeSessionOptions { MinDelayMs = 5, MaxDelayMs = 25 });
        return new BatchProcessor(
            new FakeGeminiSessionFactory(fake, NullLoggerFactory.Instance),
            new FileSystemImageStorage(options),
            new NullImageProcessor(),
            new JsonJobManifest(options),
            options,
            NullLogger<BatchProcessor>.Instance);
    }

    private async Task<HashSet<string>> ReadManifestKeysAsync()
    {
        var manifest = new JsonJobManifest(BatchOptions());
        await manifest.LoadAsync(CancellationToken.None);
        return Enumerable.Range(1, 20).Select(i => $"img{i}").Where(manifest.IsCompleted).ToHashSet();
    }

    private string[] ImageFiles() =>
        Directory.Exists(OutputDir) ? Directory.GetFiles(OutputDir, "*.png") : [];

    /// <summary>Synchronous progress: runs the callback on the reporting thread (no SynchronizationContext in tests).</summary>
    private sealed class InlineProgress(Action<JobUpdate> onReport) : IProgress<JobUpdate>
    {
        public void Report(JobUpdate value) => onReport(value);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
