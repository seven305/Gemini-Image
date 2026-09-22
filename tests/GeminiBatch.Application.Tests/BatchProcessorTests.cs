using GeminiBatch.Application.Abstractions;
using GeminiBatch.Application.Exceptions;
using GeminiBatch.Application.Models;
using GeminiBatch.Application.Options;
using GeminiBatch.Application.Tests.TestDoubles;
using GeminiBatch.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GeminiBatch.Application.Tests;

public sealed class BatchProcessorTests
{
    private static BatchOptions FastOptions(Action<BatchOptions>? configure = null)
    {
        var o = new BatchOptions
        {
            MaxRetriesPerJob = 3,
            RetryBaseDelaySeconds = 0,
            AccountFailureThreshold = 3,
            MinInterPromptDelayMs = 0,
            MaxInterPromptDelayMs = 0,
            GenerationTimeoutSeconds = 30,
            StripMetadata = true,
        };
        configure?.Invoke(o);
        return o;
    }

    private static BatchProcessor Build(
        ScriptedSessionFactory factory,
        BatchOptions? options = null,
        IImageStorage? storage = null,
        IImageProcessor? processor = null,
        IJobManifest? manifest = null) =>
        new(factory,
            storage ?? new RecordingStorage(),
            processor ?? new RecordingProcessor(),
            manifest ?? new InMemoryManifest(),
            Microsoft.Extensions.Options.Options.Create(options ?? FastOptions()),
            NullLogger<BatchProcessor>.Instance);

    private static GeminiAccount Account(string id, bool enabled = true) =>
        new() { Id = id, Email = $"{id}@example.com", UserDataDir = id, Enabled = enabled };

    private static PromptJob Job(string prompt, string? fileName = null) => new() { Prompt = prompt, DesiredFileName = fileName };

    [Fact]
    public async Task Jobs_already_in_manifest_are_skipped_without_touching_the_session()
    {
        var done = Job("already done");
        var fresh = Job("new prompt");
        var manifest = new InMemoryManifest(done.ManifestKey);
        var factory = new ScriptedSessionFactory();
        var progress = new CapturingProgress();

        var result = await Build(factory, manifest: manifest).RunAsync([done, fresh], [Account("a")], 1, progress, CancellationToken.None);

        Assert.Equal(new BatchResult(2, 1, 1, 0), result);
        Assert.Equal(JobStatus.Skipped, done.Status);
        Assert.Equal(JobStatus.Completed, fresh.Status);
        Assert.True(manifest.Loaded);

        var session = Assert.Single(factory.Sessions);
        Assert.Equal(["new prompt"], session.Prompts.ToArray());
        Assert.Equal(JobStatus.Skipped, Assert.Single(progress.For(done.Id)).Status);
    }

    [Fact]
    public async Task Transient_failures_are_retried_with_Retrying_updates_until_success()
    {
        var job = Job("flaky");
        var factory = new ScriptedSessionFactory().OnGenerate("a", ScriptedSessionFactory.FailThenSucceed(2));
        var progress = new CapturingProgress();

        var result = await Build(factory).RunAsync([job], [Account("a")], 1, progress, CancellationToken.None);

        Assert.Equal(new BatchResult(1, 1, 0, 0), result);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(3, job.Attempts);
        Assert.Null(job.LastError);

        var statuses = progress.For(job.Id).Select(u => u.Status).ToArray();
        Assert.Equal(
            [JobStatus.Running, JobStatus.Retrying, JobStatus.Running, JobStatus.Retrying, JobStatus.Running, JobStatus.Downloading, JobStatus.Completed],
            statuses);
        Assert.Contains(progress.For(job.Id), u => u.Status == JobStatus.Retrying && u.Error == "transient #1");
    }

    [Fact]
    public async Task Job_fails_after_retries_are_exhausted()
    {
        var job = Job("doomed");
        var factory = new ScriptedSessionFactory().OnGenerate("a", ScriptedSessionFactory.AlwaysFail);
        var progress = new CapturingProgress();
        var options = FastOptions(o => { o.MaxRetriesPerJob = 2; o.AccountFailureThreshold = 10; });

        var result = await Build(factory, options).RunAsync([job], [Account("a")], 1, progress, CancellationToken.None);

        Assert.Equal(new BatchResult(1, 0, 0, 1), result);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(3, job.Attempts); // 1 initial + 2 retries
        Assert.Equal("always fails #3", job.LastError);
        Assert.Equal(JobStatus.Failed, progress.For(job.Id).Last().Status);
    }

    [Fact]
    public async Task PermanentJobException_is_not_retried()
    {
        var job = Job("refused");
        var factory = new ScriptedSessionFactory()
            .OnGenerate("a", (_, _, _) => throw new PermanentJobException("policy blocked"));

        var result = await Build(factory).RunAsync([job], [Account("a")], 1, new CapturingProgress(), CancellationToken.None);

        Assert.Equal(new BatchResult(1, 0, 0, 1), result);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.Equal("policy blocked", job.LastError);
    }

    [Fact]
    public async Task Account_is_quarantined_after_threshold_and_batch_continues_on_the_other_account()
    {
        var jobs = Enumerable.Range(1, 8).Select(i => Job($"prompt {i}")).ToList();
        var bad = Account("bad");
        var good = Account("good");

        // "bad" fails every job outright; "good" is slowed down slightly so "bad" definitely gets to pull jobs.
        var factory = new ScriptedSessionFactory()
            .OnGenerate("bad", ScriptedSessionFactory.AlwaysFail)
            .OnGenerate("good", async (p, c, ct) => { await Task.Delay(5, ct); return await ScriptedSessionFactory.Succeed(p, c, ct); });
        var options = FastOptions(o => { o.MaxRetriesPerJob = 0; o.AccountFailureThreshold = 2; });

        var result = await Build(factory, options).RunAsync(jobs, [bad, good], 2, new CapturingProgress(), CancellationToken.None);

        Assert.True(bad.IsQuarantined, "bad account should be quarantined");
        Assert.Contains("2 consecutive", bad.QuarantineReason);
        Assert.False(good.IsQuarantined);

        Assert.Equal(2, result.Failed);
        Assert.Equal(6, result.Completed);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.Remaining);
        Assert.All(jobs.Where(j => j.Status == JobStatus.Failed), j => Assert.Equal("bad", j.AssignedAccountId));
        Assert.All(jobs.Where(j => j.Status == JobStatus.Completed), j => Assert.Equal("good", j.AssignedAccountId));
        Assert.All(factory.Sessions, s => Assert.True(s.Disposed));
    }

    [Fact]
    public async Task When_every_worker_dies_remaining_jobs_are_failed_not_hung()
    {
        var jobs = Enumerable.Range(1, 10).Select(i => Job($"prompt {i}")).ToList();
        var factory = new ScriptedSessionFactory().OnReady("a", _ => throw new InvalidOperationException("not signed in"));
        var account = Account("a");

        var run = Build(factory).RunAsync(jobs, [account], 1, new CapturingProgress(), CancellationToken.None);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(run, finished);
        var result = await run;
        Assert.True(account.IsQuarantined);
        Assert.Equal(new BatchResult(10, 0, 0, 10), result);
        Assert.All(jobs, j => Assert.Equal("No healthy accounts remaining.", j.LastError));
    }

    [Fact]
    public async Task Cancellation_stops_the_batch_and_disposes_sessions()
    {
        var jobs = Enumerable.Range(1, 20).Select(i => Job($"prompt {i}")).ToList();
        using var cts = new CancellationTokenSource();
        var factory = new ScriptedSessionFactory()
            .OnGenerate("a", async (p, c, ct) =>
            {
                if (c == 2) cts.Cancel();
                await Task.Delay(10, ct);
                return await ScriptedSessionFactory.Succeed(p, c, ct);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Build(factory).RunAsync(jobs, [Account("a")], 1, new CapturingProgress(), cts.Token));

        var session = Assert.Single(factory.Sessions);
        Assert.True(session.Disposed);
        Assert.Equal(1, jobs.Count(j => j.Status == JobStatus.Completed));
        Assert.Equal(19, jobs.Count(j => j.Status == JobStatus.Pending));
        Assert.DoesNotContain(jobs, j => j.Status is JobStatus.Running or JobStatus.Downloading);
    }

    [Fact]
    public async Task Success_path_calls_storage_then_processor_then_manifest_with_desired_file_name()
    {
        var job = Job("cat on a roof", fileName: "cat_roof");
        var order = new CallOrder();
        var storage = new OrderedStorage(order);
        var processor = new OrderedProcessor(order);
        var manifest = new OrderedManifest(order);
        var factory = new ScriptedSessionFactory();

        var result = await Build(factory, storage: storage, processor: processor, manifest: manifest)
            .RunAsync([job], [Account("a")], 1, new CapturingProgress(), CancellationToken.None);

        Assert.Equal(new BatchResult(1, 1, 0, 0), result);
        Assert.Equal(["storage", "processor", "manifest"], order.Steps.ToArray());
        Assert.Equal("cat_roof", storage.BaseName);
        Assert.Equal(Path.Combine("out", "cat_roof.png"), job.SavedPath);
        Assert.Equal(job.SavedPath, processor.Path);
        Assert.Equal(("cat_roof", job.SavedPath!), manifest.Marked);
        Assert.False(File.Exists(storage.TempPath), "temp file should be cleaned up");
    }

    [Fact]
    public async Task StripMetadata_false_skips_the_image_processor()
    {
        var processor = new RecordingProcessor();
        var factory = new ScriptedSessionFactory();

        await Build(factory, FastOptions(o => o.StripMetadata = false), processor: processor)
            .RunAsync([Job("x")], [Account("a")], 1, new CapturingProgress(), CancellationToken.None);

        Assert.Empty(processor.Calls);
    }

    [Fact]
    public async Task Concurrency_is_capped_at_the_number_of_enabled_accounts()
    {
        var jobs = Enumerable.Range(1, 6).Select(i => Job($"p{i}")).ToList();
        var factory = new ScriptedSessionFactory();
        var accounts = new[] { Account("a"), Account("b"), Account("disabled", enabled: false) };

        var result = await Build(factory).RunAsync(jobs, accounts, 10, new CapturingProgress(), CancellationToken.None);

        Assert.Equal(6, result.Completed);
        Assert.Equal(2, factory.Sessions.Count);
        Assert.DoesNotContain(factory.Sessions, s => s.AccountId == "disabled");
    }

    [Fact]
    public async Task No_eligible_accounts_throws()
    {
        var factory = new ScriptedSessionFactory();
        var quarantined = Account("q");
        quarantined.Quarantine("test");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Build(factory).RunAsync([Job("x")], [quarantined, Account("off", enabled: false)], 1, new CapturingProgress(), CancellationToken.None));
    }

    // --- ordered doubles for the success-path test ---

    private sealed class OrderedStorage(CallOrder order) : IImageStorage
    {
        public string? BaseName { get; private set; }
        public string? TempPath { get; private set; }

        public Task<string> SaveAsync(string tempFilePath, string? desiredBaseName, CancellationToken ct)
        {
            order.Add("storage");
            BaseName = desiredBaseName;
            TempPath = tempFilePath;
            return Task.FromResult(Path.Combine("out", $"{desiredBaseName}.png"));
        }
    }

    private sealed class OrderedProcessor(CallOrder order) : IImageProcessor
    {
        public string? Path { get; private set; }

        public Task StripMetadataAsync(string filePath, CancellationToken ct)
        {
            order.Add("processor");
            Path = filePath;
            return Task.CompletedTask;
        }
    }

    private sealed class OrderedManifest(CallOrder order) : IJobManifest
    {
        public (string Key, string Path)? Marked { get; private set; }

        public Task LoadAsync(CancellationToken ct) => Task.CompletedTask;
        public bool IsCompleted(string key) => false;

        public Task MarkCompletedAsync(string key, string savedPath, CancellationToken ct)
        {
            order.Add("manifest");
            Marked = (key, savedPath);
            return Task.CompletedTask;
        }
    }
}
