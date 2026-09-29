using System.Collections.Concurrent;
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
            StartupStaggerMs = 0,
            MaxSessionRestarts = 1,
            MaxImagesPerAccount = 0, // no rotation: these tests exercise one account per worker; rotation has its own tests
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

    private const string NoAccountsLeft = "No accounts left to run this job (all used or quarantined).";

    private static BatchOptions RotatingOptions(int imagesPerAccount = 1) => FastOptions(o => o.MaxImagesPerAccount = imagesPerAccount);

    private static GeminiAccount[] Accounts(int count) =>
        Enumerable.Range(0, count).Select(i => Account(((char)('a' + i)).ToString())).ToArray();

    private static GeminiAccount Account(string id, bool enabled = true) =>
        new() { Id = id, Email = $"{id}@example.com", UserDataDir = id, Enabled = enabled };

    private static PromptJob Job(string prompt, string? fileName = null) => new() { Prompt = prompt, DesiredFileName = fileName };

    private static void AssertCounts(BatchResult result, int total, int completed, int skipped, int failed) =>
        Assert.Equal((total, completed, skipped, failed), (result.Total, result.Completed, result.Skipped, result.Failed));

    [Fact]
    public async Task Jobs_already_in_manifest_are_skipped_without_touching_the_session()
    {
        var done = Job("already done");
        var fresh = Job("new prompt");
        var manifest = new InMemoryManifest(done.ManifestKey);
        var factory = new ScriptedSessionFactory();
        var progress = new CapturingProgress();

        var result = await Build(factory, manifest: manifest).RunAsync([done, fresh], [Account("a")], 1, progress, CancellationToken.None);

        AssertCounts(result, 2, 1, 1, 0);
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

        AssertCounts(result, 1, 1, 0, 0);
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

        AssertCounts(result, 1, 0, 0, 1);
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

        AssertCounts(result, 1, 0, 0, 1);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.Equal("policy blocked", job.LastError);
    }

    [Fact]
    public async Task Account_is_quarantined_after_threshold_its_last_job_is_requeued_and_batch_continues()
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

        // The first failure stays failed; the one that trips the threshold is handed to "good" instead.
        Assert.Equal(1, result.Failed);
        Assert.Equal(7, result.Completed);
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
        AssertCounts(result, 10, 0, 0, 10);
        Assert.All(jobs, j => Assert.Equal(NoAccountsLeft, j.LastError));
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

        AssertCounts(result, 1, 1, 0, 0);
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

    // --- Phase 3: quarantine, requeue, double-save guards, cancellation, pacing ---

    [Fact]
    public async Task AccountUnavailable_is_not_retried_quarantines_at_once_and_the_job_moves_to_a_healthy_account()
    {
        var jobs = Enumerable.Range(1, 4).Select(i => Job($"prompt {i}")).ToList();
        var bad = Account("bad");
        var good = Account("good");
        var factory = new ScriptedSessionFactory()
            .OnGenerate("bad", ScriptedSessionFactory.AlwaysUnavailable(AccountUnavailableReason.Challenged))
            .OnReady("good", ct => Task.Delay(100, ct)); // "bad" is ready first, so it certainly pulls a job

        var result = await Build(factory).RunAsync(jobs, [bad, good], 2, new CapturingProgress(), CancellationToken.None);

        AssertCounts(result, 4, 4, 0, 0);
        Assert.Equal(BatchStopReason.Finished, result.StopReason);
        Assert.True(bad.IsQuarantined);
        Assert.Contains("Challenged", bad.QuarantineReason);
        Assert.Equal(1, factory.Sessions.Single(s => s.AccountId == "bad").Calls); // terminal: no retries
        Assert.All(jobs, j => Assert.Equal("good", j.AssignedAccountId));
        var quarantined = Assert.Single(result.QuarantinedAccounts);
        Assert.Equal("bad", quarantined.Id);
    }

    [Fact]
    public async Task When_every_account_is_quarantined_the_batch_stops_gracefully_with_a_reason()
    {
        var jobs = Enumerable.Range(1, 5).Select(i => Job($"prompt {i}")).ToList();
        var factory = new ScriptedSessionFactory()
            .OnGenerate("a", ScriptedSessionFactory.AlwaysUnavailable(AccountUnavailableReason.Challenged))
            .OnGenerate("b", ScriptedSessionFactory.AlwaysUnavailable(AccountUnavailableReason.SignedOut));

        var run = Build(factory).RunAsync(jobs, [Account("a"), Account("b")], 2, new CapturingProgress(), CancellationToken.None);
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))));
        var result = await run;

        AssertCounts(result, 5, 0, 0, 5);
        Assert.Equal(BatchStopReason.AllAccountsQuarantined, result.StopReason);
        Assert.Equal(["a", "b"], result.QuarantinedAccounts.Select(q => q.Id).Order().ToArray());
        Assert.All(jobs, j => Assert.Equal(NoAccountsLeft, j.LastError));
        Assert.All(factory.Sessions, s => Assert.True(s.Disposed));
    }

    [Fact]
    public async Task Lost_browser_session_is_relaunched_once_and_the_job_completes()
    {
        var job = Job("survives a crash");
        var calls = 0;
        var factory = new ScriptedSessionFactory().OnGenerate("a", (p, c, ct) =>
            Interlocked.Increment(ref calls) == 1
                ? throw new AccountUnavailableException(AccountUnavailableReason.SessionLost, "browser crashed")
                : ScriptedSessionFactory.Succeed(p, c, ct));
        var account = Account("a");

        var result = await Build(factory).RunAsync([job], [account], 1, new CapturingProgress(), CancellationToken.None);

        AssertCounts(result, 1, 1, 0, 0);
        Assert.False(account.IsQuarantined);
        Assert.Equal(2, factory.Sessions.Count);
        Assert.All(factory.Sessions, s => Assert.True(s.Disposed));
        Assert.Equal(2, job.Attempts);
    }

    [Fact]
    public async Task Session_lost_beyond_the_restart_budget_quarantines_the_account()
    {
        var job = Job("keeps crashing");
        var factory = new ScriptedSessionFactory()
            .OnGenerate("a", ScriptedSessionFactory.AlwaysUnavailable(AccountUnavailableReason.SessionLost));
        var account = Account("a");

        var result = await Build(factory).RunAsync([job], [account], 1, new CapturingProgress(), CancellationToken.None);

        AssertCounts(result, 1, 0, 0, 1);
        Assert.True(account.IsQuarantined);
        Assert.Contains("lost 2 time(s)", account.QuarantineReason);
        Assert.Equal(2, factory.Sessions.Count); // first launch + MaxSessionRestarts(1)
        Assert.Equal(BatchStopReason.AllAccountsQuarantined, result.StopReason);
    }

    [Fact]
    public async Task PermanentJobException_does_not_count_toward_quarantine()
    {
        var jobs = Enumerable.Range(1, 4).Select(i => Job($"prompt {i}")).ToList();
        var factory = new ScriptedSessionFactory().OnGenerate("a", (p, c, ct) =>
            c <= 3 ? throw new PermanentJobException("refused") : ScriptedSessionFactory.Succeed(p, c, ct));
        var account = Account("a");
        var options = FastOptions(o => { o.MaxRetriesPerJob = 0; o.AccountFailureThreshold = 2; });

        var result = await Build(factory, options).RunAsync(jobs, [account], 1, new CapturingProgress(), CancellationToken.None);

        AssertCounts(result, 4, 1, 0, 3);
        Assert.False(account.IsQuarantined);
    }

    [Fact]
    public async Task Duplicate_manifest_keys_in_one_batch_are_generated_and_saved_once()
    {
        var jobs = new[] { Job("same"), Job("same"), Job("first", fileName: "name"), Job("second", fileName: "name") };
        var manifest = new InMemoryManifest();
        var factory = new ScriptedSessionFactory();

        var result = await Build(factory, manifest: manifest)
            .RunAsync(jobs, [Account("a"), Account("b")], 2, new CapturingProgress(), CancellationToken.None);

        AssertCounts(result, 4, 2, 2, 0);
        Assert.Equal(JobStatus.Skipped, jobs[1].Status);
        Assert.Equal(JobStatus.Skipped, jobs[3].Status);
        Assert.All(manifest.MarkCounts.Values, n => Assert.Equal(1, n));
        Assert.Equal(2, factory.Sessions.Sum(s => s.Calls));
    }

    [Fact]
    public async Task Key_completed_elsewhere_while_generating_is_not_saved_again()
    {
        var job = Job("raced");
        var manifest = new InMemoryManifest();
        var storage = new RecordingStorage();
        string? temp = null;
        var factory = new ScriptedSessionFactory().OnGenerate("a", async (p, c, ct) =>
        {
            manifest.CompleteExternally(job.ManifestKey);
            var image = await ScriptedSessionFactory.Succeed(p, c, ct);
            temp = image.TempFilePath;
            return image;
        });

        var result = await Build(factory, storage: storage, manifest: manifest)
            .RunAsync([job], [Account("a")], 1, new CapturingProgress(), CancellationToken.None);

        AssertCounts(result, 1, 0, 1, 0);
        Assert.Equal(JobStatus.Skipped, job.Status);
        Assert.Empty(storage.Calls);
        Assert.Empty(manifest.MarkCounts);
        Assert.False(File.Exists(temp), "temp file should be cleaned up");
    }

    [Fact]
    public async Task Cancel_during_generation_saves_nothing_and_leaves_the_job_pending()
    {
        var job = Job("interrupted");
        var manifest = new InMemoryManifest();
        var storage = new RecordingStorage();
        using var cts = new CancellationTokenSource();
        var factory = new ScriptedSessionFactory().OnGenerate("a", async (_, _, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Build(factory, storage: storage, manifest: manifest).RunAsync([job], [Account("a")], 1, new CapturingProgress(), cts.Token));

        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Empty(storage.Calls);
        Assert.Empty(manifest.Entries);
    }

    [Fact]
    public async Task Cancel_after_the_file_is_saved_still_commits_the_manifest_entry()
    {
        var job = Job("almost done");
        var manifest = new InMemoryManifest();
        using var cts = new CancellationTokenSource();
        var processor = new CancellingProcessor(cts);

        try
        {
            await Build(new ScriptedSessionFactory(), processor: processor, manifest: manifest)
                .RunAsync([job, Job("next")], [Account("a")], 1, new CapturingProgress(), cts.Token);
        }
        catch (OperationCanceledException) { }

        Assert.False(processor.TokenWasCancellable, "commit must not be cancellable once the file is saved");
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(1, manifest.MarkCounts[job.ManifestKey]);
    }

    [Fact]
    public async Task Accounts_sharing_an_email_or_profile_directory_get_one_worker()
    {
        var accounts = new[]
        {
            Account("a"),
            new GeminiAccount { Id = "a-2", Email = "A@example.com", UserDataDir = "a-2" }, // same Google account
            new GeminiAccount { Id = "c", Email = "c@example.com", UserDataDir = "a" },    // same profile dir
        };
        var factory = new ScriptedSessionFactory();

        var result = await Build(factory).RunAsync([Job("x"), Job("y")], accounts, 3, new CapturingProgress(), CancellationToken.None);

        AssertCounts(result, 2, 2, 0, 0);
        Assert.Equal("a", Assert.Single(factory.Sessions).AccountId);
    }

    [Fact]
    public async Task Workers_start_staggered()
    {
        var readyAt = new ConcurrentDictionary<string, DateTime>();
        var factory = new ScriptedSessionFactory();
        foreach (var id in new[] { "a", "b", "c" })
        {
            factory.OnReady(id, _ => { readyAt[id] = DateTime.UtcNow; return Task.CompletedTask; });
            // Slow jobs, so work is still queued when the staggered workers start (idle workers never launch a browser).
            factory.OnGenerate(id, async (p, c, ct) => { await Task.Delay(500, ct); return await ScriptedSessionFactory.Succeed(p, c, ct); });
        }
        var options = FastOptions(o => o.StartupStaggerMs = 150);

        await Build(factory, options).RunAsync(
            Enumerable.Range(1, 3).Select(i => Job($"p{i}")).ToList(),
            [Account("a"), Account("b"), Account("c")], 3, new CapturingProgress(), CancellationToken.None);

        var times = readyAt.Values.Order().ToArray();
        Assert.Equal(3, times.Length);
        Assert.True(times[2] - times[0] >= TimeSpan.FromMilliseconds(280), $"spread was {(times[2] - times[0]).TotalMilliseconds} ms");
    }

    // --- Account rotation: workers draw the next unused account from the roster after each account's turn ---

    [Fact]
    public async Task Concurrency_1_uses_one_account_per_image_in_roster_order_one_browser_at_a_time()
    {
        var jobs = Enumerable.Range(1, 3).Select(i => Job($"p{i}")).ToList();
        var factory = new ScriptedSessionFactory();

        var result = await Build(factory, RotatingOptions()).RunAsync(jobs, Accounts(5), 1, new CapturingProgress(), CancellationToken.None);

        AssertCounts(result, 3, 3, 0, 0);
        Assert.Equal(BatchStopReason.Finished, result.StopReason);
        Assert.Equal(3, result.AccountsUsed);
        Assert.Equal(["a", "b", "c"], factory.LaunchOrder.ToArray()); // d and e never launched
        Assert.Equal(1, factory.MaxActiveSessions);
        Assert.All(factory.Sessions, s => { Assert.Equal(1, s.Calls); Assert.True(s.Disposed); });
        Assert.Equal(["a", "b", "c"], jobs.Select(j => j.AssignedAccountId!).ToArray());
    }

    [Fact]
    public async Task When_every_account_has_had_its_turn_leftover_jobs_fail_with_AccountsExhausted()
    {
        var jobs = Enumerable.Range(1, 5).Select(i => Job($"p{i}")).ToList();
        var accounts = Accounts(3);

        var result = await Build(new ScriptedSessionFactory(), RotatingOptions()).RunAsync(jobs, accounts, 1, new CapturingProgress(), CancellationToken.None);

        AssertCounts(result, 5, 3, 0, 2);
        Assert.Equal(BatchStopReason.AccountsExhausted, result.StopReason);
        Assert.Equal(3, result.AccountsUsed);
        Assert.Empty(result.QuarantinedAccounts);
        Assert.DoesNotContain(accounts, a => a.IsQuarantined); // used up is not the same as quarantined
        Assert.Equal(2, jobs.Count(j => j.LastError == NoAccountsLeft));
    }

    [Fact]
    public async Task Quarantined_account_hands_its_job_to_the_next_account_in_the_roster()
    {
        var jobs = new[] { Job("p1"), Job("p2") };
        var accounts = Accounts(4);
        var factory = new ScriptedSessionFactory()
            .OnGenerate("a", ScriptedSessionFactory.AlwaysUnavailable(AccountUnavailableReason.Challenged));

        var result = await Build(factory, RotatingOptions()).RunAsync(jobs, accounts, 1, new CapturingProgress(), CancellationToken.None);

        AssertCounts(result, 2, 2, 0, 0);
        Assert.True(accounts[0].IsQuarantined);
        Assert.Equal(["a", "b", "c"], factory.LaunchOrder.ToArray());
        Assert.Equal(["b", "c"], jobs.Select(j => j.AssignedAccountId!).Order().ToArray());
        Assert.Equal("a", Assert.Single(result.QuarantinedAccounts).Id);
    }

    [Fact]
    public async Task Concurrency_2_rotates_accounts_with_at_most_two_browsers_open()
    {
        var jobs = Enumerable.Range(1, 4).Select(i => Job($"p{i}")).ToList();
        var factory = new ScriptedSessionFactory();
        foreach (var id in new[] { "a", "b", "c", "d" })
            factory.OnGenerate(id, async (p, c, ct) => { await Task.Delay(20, ct); return await ScriptedSessionFactory.Succeed(p, c, ct); });

        var result = await Build(factory, RotatingOptions()).RunAsync(jobs, Accounts(6), 2, new CapturingProgress(), CancellationToken.None);

        AssertCounts(result, 4, 4, 0, 0);
        Assert.Equal(4, result.AccountsUsed);
        Assert.Equal(4, factory.Sessions.Count);
        Assert.Equal(2, factory.MaxActiveSessions);
    }

    [Fact]
    public async Task Image_cap_above_one_lets_an_account_generate_that_many_before_rotating()
    {
        var jobs = Enumerable.Range(1, 5).Select(i => Job($"p{i}")).ToList();
        var factory = new ScriptedSessionFactory();

        var result = await Build(factory, RotatingOptions(imagesPerAccount: 2)).RunAsync(jobs, Accounts(5), 1, new CapturingProgress(), CancellationToken.None);

        AssertCounts(result, 5, 5, 0, 0);
        Assert.Equal(["a", "b", "c"], factory.LaunchOrder.ToArray());
        Assert.Equal([2, 2, 1], factory.Sessions.OrderBy(s => s.AccountId).Select(s => s.Calls).ToArray());
    }

    [Fact]
    public async Task No_image_cap_keeps_one_account_for_the_whole_batch()
    {
        var jobs = Enumerable.Range(1, 4).Select(i => Job($"p{i}")).ToList();
        var factory = new ScriptedSessionFactory();

        var result = await Build(factory, RotatingOptions(imagesPerAccount: 0)).RunAsync(jobs, Accounts(5), 1, new CapturingProgress(), CancellationToken.None);

        AssertCounts(result, 4, 4, 0, 0);
        Assert.Equal(1, result.AccountsUsed);
        Assert.Equal(4, Assert.Single(factory.Sessions).Calls);
    }

    private sealed class CancellingProcessor(CancellationTokenSource cts) : IImageProcessor
    {
        public bool TokenWasCancellable { get; private set; }

        public Task StripMetadataAsync(string filePath, CancellationToken ct)
        {
            TokenWasCancellable = ct.CanBeCanceled;
            cts.Cancel(); // Stop pressed right after the file landed on disk
            return Task.CompletedTask;
        }
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
