using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using GeminiBatch.Application.Abstractions;
using GeminiBatch.Application.Exceptions;
using GeminiBatch.Application.Models;
using GeminiBatch.Application.Options;
using GeminiBatch.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace GeminiBatch.Application;

/// <summary>
/// Fans a list of prompt jobs out over N workers (N = concurrency). Each worker takes the next unused account from
/// the roster (in roster order), generates up to <see cref="BatchOptions.MaxImagesPerAccount"/> images with it, closes
/// that browser and takes the next account, until the queue drains or every account has had its turn. A worker owns
/// a single <see cref="IGeminiSession"/> at a time. Per-job retries use exponential backoff with jitter for transient
/// failures; an <see cref="AccountUnavailableException"/> (auth wall, CAPTCHA, dead browser) is terminal for the
/// account, not the job: the job goes back on the queue for a healthy worker. An account is also quarantined after
/// <see cref="BatchOptions.AccountFailureThreshold"/> consecutive job failures. The batch carries on with whatever
/// accounts remain; the manifest is checked before every save so no key is ever completed twice.
/// </summary>
public sealed class BatchProcessor
{
    private const string NoAccountsLeft = "No accounts left to run this job (all used or quarantined).";

    private static readonly ResiliencePropertyKey<PromptJob> JobKey = new("GeminiBatch.Job");
    private static readonly ResiliencePropertyKey<IProgress<JobUpdate>> ProgressKey = new("GeminiBatch.Progress");

    private readonly IGeminiSessionFactory _sessionFactory;
    private readonly IImageStorage _storage;
    private readonly IImageProcessor _imageProcessor;
    private readonly IJobManifest _manifest;
    private readonly BatchOptions _options;
    private readonly ILogger<BatchProcessor> _logger;
    private readonly ResiliencePipeline _retryPipeline;

    public BatchProcessor(
        IGeminiSessionFactory sessionFactory,
        IImageStorage storage,
        IImageProcessor imageProcessor,
        IJobManifest manifest,
        IOptions<BatchOptions> options,
        ILogger<BatchProcessor> logger)
    {
        _sessionFactory = sessionFactory;
        _storage = storage;
        _imageProcessor = imageProcessor;
        _manifest = manifest;
        _options = options.Value;
        _logger = logger;
        _retryPipeline = BuildRetryPipeline(_options);
    }

    public async Task<BatchResult> RunAsync(
        IReadOnlyList<PromptJob> jobs,
        IReadOnlyList<GeminiAccount> accounts,
        int concurrency,
        IProgress<JobUpdate> progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(progress);

        await _manifest.LoadAsync(ct).ConfigureAwait(false);

        var eligible = AccountEligibility.Select(accounts, _logger);
        if (eligible.Count == 0)
            throw new InvalidOperationException("No enabled, non-quarantined accounts are available.");

        var workerCount = Math.Clamp(concurrency, 1, eligible.Count);
        if (workerCount != concurrency)
            _logger.LogWarning("Concurrency {Requested} adjusted to {Workers} worker(s) ({Eligible} eligible account(s))",
                concurrency, workerCount, eligible.Count);
        var run = new RunState(eligible);

        // The whole batch is queued up front (it is small); workers put jobs back when their account dies.
        var keysInBatch = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            using var jobScope = BeginJobScope(job);
            if (_manifest.IsCompleted(job.ManifestKey))
            {
                SetStatus(job, JobStatus.Skipped, progress);
                run.CountSkipped();
                _logger.LogInformation("Skipping job {JobId}: manifest key {ManifestKey} already completed", job.Id, job.ManifestKey);
            }
            else if (!keysInBatch.Add(job.ManifestKey))
            {
                job.LastError = "Duplicate of an earlier prompt/file name in this batch.";
                SetStatus(job, JobStatus.Skipped, progress);
                run.CountSkipped();
                _logger.LogWarning("Skipping job {JobId}: manifest key {ManifestKey} appears earlier in this batch", job.Id, job.ManifestKey);
            }
            else
            {
                run.Enqueue(job);
            }
        }
        run.CompleteIfNothingOutstanding();

        _logger.LogInformation("Starting batch: {JobCount} jobs ({Queued} queued), {WorkerCount} workers drawing from {AccountCount} accounts, {MaxImages} image(s) per account",
            jobs.Count, run.Outstanding, workerCount, eligible.Count, _options.MaxImagesPerAccount > 0 ? _options.MaxImagesPerAccount : "unlimited");

        run.ActiveWorkers = workerCount;
        var workers = Enumerable.Range(0, workerCount).Select(index => Task.Run(async () =>
        {
            try
            {
                await WorkerAsync(index, run, progress, ct).ConfigureAwait(false);
            }
            finally
            {
                // With no worker left nobody can finish the outstanding jobs; stop waiting for them.
                if (Interlocked.Decrement(ref run.ActiveWorkers) == 0)
                    run.Writer.TryComplete();
            }
        }, CancellationToken.None)).ToArray();

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogSummary("cancelled", BuildResult(jobs, run, leftovers: 0));
            throw;
        }

        // Anything still queued was never picked up because the accounts ran out (used or quarantined). Report it as
        // a failure rather than leaving it silently unprocessed.
        var leftovers = 0;
        while (run.Reader.TryRead(out var leftover))
        {
            leftover.AssignedAccountId = null;
            leftover.LastError = NoAccountsLeft;
            SetStatus(leftover, JobStatus.Failed, progress);
            run.CountFailed();
            leftovers++;
        }

        var result = BuildResult(jobs, run, leftovers);
        LogSummary("finished", result);
        return result;
    }

    private async Task WorkerAsync(int index, RunState run, IProgress<JobUpdate> progress, CancellationToken ct)
    {
        await StartupStaggerAsync(index, ct).ConfigureAwait(false);

        // Wait for work before taking an account, so no browser is launched once the queue has drained.
        while (await run.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            if (!run.TryTakeAccount(out var account))
            {
                _logger.LogInformation("Worker {Worker} stopping: every account has had its turn", index);
                return;
            }
            await UseAccountAsync(account, run, progress, ct).ConfigureAwait(false);
        }
    }

    /// <summary>One account's turn: launch its browser (relaunching after a crash) and run jobs until its turn ends.</summary>
    private async Task UseAccountAsync(GeminiAccount account, RunState run, IProgress<JobUpdate> progress, CancellationToken ct)
    {
        using var accountScope = _logger.BeginScope(new Dictionary<string, object?> { ["AccountId"] = account.Id });
        _logger.LogInformation("Account {AccountId} starting its turn", account.Id);

        for (var restarts = 0; ; restarts++)
        {
            var exit = await RunSessionAsync(account, run, progress, ct).ConfigureAwait(false);
            if (exit != SessionExit.SessionLost)
                return;

            if (restarts >= _options.MaxSessionRestarts)
            {
                QuarantineAccount(account, $"Browser session lost {restarts + 1} time(s)");
                return;
            }
            _logger.LogWarning("Browser session for {AccountId} was lost; relaunching ({Restart}/{Max})",
                account.Id, restarts + 1, _options.MaxSessionRestarts);
        }
    }

    /// <summary>
    /// One session's lifetime: launch, get ready, then pull jobs until the account's image cap is reached, the queue
    /// drains, or the account/session dies.
    /// </summary>
    private async Task<SessionExit> RunSessionAsync(GeminiAccount account, RunState run, IProgress<JobUpdate> progress, CancellationToken ct)
    {
        IGeminiSession session;
        try
        {
            session = await _sessionFactory.CreateAsync(account, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session creation failed for {AccountId}", account.Id);
            QuarantineAccount(account, $"Session creation failed: {ex.Message}");
            return SessionExit.Quarantined;
        }

        await using (session.ConfigureAwait(false))
        {
            try
            {
                await session.EnsureReadyAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (AccountUnavailableException ex) when (ex.Reason == AccountUnavailableReason.SessionLost)
            {
                _logger.LogWarning(ex, "Browser session for {AccountId} was lost while getting ready", account.Id);
                return SessionExit.SessionLost;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session for {AccountId} failed to become ready", account.Id);
                QuarantineAccount(account, $"Session not ready: {ex.Message}");
                return SessionExit.Quarantined;
            }

            var consecutiveFailures = 0;
            var imagesDone = 0;

            await foreach (var job in run.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                using var jobScope = BeginJobScope(job);
                job.AssignedAccountId = account.Id;
                var (outcome, error) = await ProcessJobAsync(job, session, progress, ct).ConfigureAwait(false);

                switch (outcome)
                {
                    case JobOutcome.Completed:
                        consecutiveFailures = 0;
                        run.Finish(JobStatus.Completed);
                        if (_options.MaxImagesPerAccount > 0 && ++imagesDone >= _options.MaxImagesPerAccount)
                        {
                            // The next prompt runs in another account's browser, so no pacing delay here.
                            _logger.LogInformation("Account {AccountId} generated {Images} image(s); its turn is over", account.Id, imagesDone);
                            return SessionExit.AccountDone;
                        }
                        break;

                    case JobOutcome.Skipped:
                        run.Finish(JobStatus.Skipped);
                        continue; // nothing was sent to Gemini, so no pacing delay either

                    case JobOutcome.FailedPermanent:
                        // About the prompt, not the account: it does not count toward quarantine.
                        FailJob(job, error!, progress);
                        run.Finish(JobStatus.Failed);
                        break;

                    case JobOutcome.Failed:
                        consecutiveFailures++;
                        if (consecutiveFailures >= _options.AccountFailureThreshold)
                        {
                            // The failures point at the account, so this job gets another chance elsewhere.
                            Requeue(job, run, progress, $"Moved off account {account.Id}: {error!.Message}");
                            QuarantineAccount(account, $"{consecutiveFailures} consecutive job failures");
                            return SessionExit.Quarantined;
                        }
                        FailJob(job, error!, progress);
                        run.Finish(JobStatus.Failed);
                        break;

                    case JobOutcome.AccountLost:
                        var unavailable = (AccountUnavailableException)error!;
                        Requeue(job, run, progress, $"Moved off account {account.Id}: {unavailable.Message}");
                        if (unavailable.Reason == AccountUnavailableReason.SessionLost)
                        {
                            _logger.LogWarning(unavailable, "Browser session for {AccountId} was lost during job {JobId}", account.Id, job.Id);
                            return SessionExit.SessionLost;
                        }
                        QuarantineAccount(account, $"{unavailable.Reason}: {unavailable.Message}");
                        return SessionExit.Quarantined;
                }

                await InterPromptDelayAsync(ct).ConfigureAwait(false);
            }

            return SessionExit.QueueDrained;
        }
    }

    /// <summary>
    /// Runs one job through the retry pipeline. Never marks the job Failed itself: the worker decides between
    /// failing it and handing it to another account.
    /// </summary>
    private async Task<(JobOutcome Outcome, Exception? Error)> ProcessJobAsync(
        PromptJob job, IGeminiSession session, IProgress<JobUpdate> progress, CancellationToken ct)
    {
        // A requeued job, or a key completed since the batch started, must not be generated (and saved) again.
        if (_manifest.IsCompleted(job.ManifestKey))
        {
            SkipAlreadyCompleted(job, progress);
            return (JobOutcome.Skipped, null);
        }

        var context = ResilienceContextPool.Shared.Get(ct);
        context.Properties.Set(JobKey, job);
        context.Properties.Set(ProgressKey, progress);

        try
        {
            var saved = await _retryPipeline.ExecuteAsync(
                async ctx => await AttemptAsync(job, session, progress, ctx.CancellationToken).ConfigureAwait(false),
                context).ConfigureAwait(false);
            return (saved ? JobOutcome.Completed : JobOutcome.Skipped, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Put the job back to Pending so a re-run picks it up; it is not in the manifest.
            job.Status = JobStatus.Pending;
            progress.Report(JobUpdate.From(job));
            throw;
        }
        catch (AccountUnavailableException ex)
        {
            return (JobOutcome.AccountLost, ex);
        }
        catch (PermanentJobException ex)
        {
            return (JobOutcome.FailedPermanent, ex);
        }
        catch (Exception ex)
        {
            return (JobOutcome.Failed, ex);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>
    /// A single generate → save → strip → manifest attempt. Any exception here is judged by the retry policy.
    /// Returns false when the key turned out to be completed already (nothing saved).
    /// </summary>
    private async ValueTask<bool> AttemptAsync(PromptJob job, IGeminiSession session, IProgress<JobUpdate> progress, CancellationToken ct)
    {
        job.Attempts++;
        SetStatus(job, JobStatus.Running, progress);
        _logger.LogInformation("Job {JobId} attempt {Attempt}: generating", job.Id, job.Attempts);

        GeneratedImage image;
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            if (_options.GenerationTimeoutSeconds > 0)
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.GenerationTimeoutSeconds));

            try
            {
                image = await session.GenerateAsync(job.Prompt, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Generation exceeded {_options.GenerationTimeoutSeconds}s.");
            }
        }

        SetStatus(job, JobStatus.Downloading, progress);
        string savedPath;
        try
        {
            if (_manifest.IsCompleted(job.ManifestKey))
            {
                SkipAlreadyCompleted(job, progress);
                return false;
            }
            savedPath = await _storage.SaveAsync(image.TempFilePath, job.DesiredFileName ?? image.SuggestedFileName, job.Section, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(image.TempFilePath);
        }

        // The file is on disk now: finish the commit even if Stop was pressed, so there is never a saved image
        // without its manifest entry (a resume would generate it again as name_2). If the commit itself fails,
        // remove the file for the same reason before the retry policy takes over.
        try
        {
            if (_options.StripMetadata)
                await _imageProcessor.StripMetadataAsync(savedPath, CancellationToken.None).ConfigureAwait(false);

            await _manifest.MarkCompletedAsync(job.ManifestKey, savedPath, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(savedPath);
            throw;
        }

        job.SavedPath = savedPath;
        job.LastError = null;
        SetStatus(job, JobStatus.Completed, progress);
        _logger.LogInformation("Job {JobId} completed -> {SavedPath}", job.Id, savedPath);
        return true;
    }

    private void Requeue(PromptJob job, RunState run, IProgress<JobUpdate> progress, string reason)
    {
        job.AssignedAccountId = null;
        job.LastError = reason;

        // The job still counts as outstanding, so the queue cannot have been completed under us.
        if (!run.Writer.TryWrite(job))
        {
            _logger.LogError("Could not requeue job {JobId}; failing it", job.Id);
            SetStatus(job, JobStatus.Failed, progress);
            run.Finish(JobStatus.Failed);
            return;
        }

        SetStatus(job, JobStatus.Pending, progress);
        _logger.LogWarning("Job {JobId} requeued for another account: {Reason}", job.Id, reason);
    }

    private void FailJob(PromptJob job, Exception error, IProgress<JobUpdate> progress)
    {
        job.LastError = error.Message;
        SetStatus(job, JobStatus.Failed, progress);
        _logger.LogError(error, "Job {JobId} failed after {Attempts} attempt(s)", job.Id, job.Attempts);
    }

    private void SkipAlreadyCompleted(PromptJob job, IProgress<JobUpdate> progress)
    {
        SetStatus(job, JobStatus.Skipped, progress);
        _logger.LogInformation("Job {JobId}: manifest key {ManifestKey} already completed; not saving again", job.Id, job.ManifestKey);
    }

    private void QuarantineAccount(GeminiAccount account, string reason)
    {
        account.Quarantine(reason);
        _logger.LogWarning("Account {AccountId} quarantined: {Reason}", account.Id, reason);
    }

    private async Task StartupStaggerAsync(int workerIndex, CancellationToken ct)
    {
        var stagger = Math.Max(0, _options.StartupStaggerMs);
        if (stagger == 0 || workerIndex == 0) return;
        var delay = workerIndex * stagger + Random.Shared.Next(0, stagger / 2 + 1);
        _logger.LogDebug("Staggering start by {Delay} ms", delay);
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    private async Task InterPromptDelayAsync(CancellationToken ct)
    {
        var min = Math.Max(0, _options.MinInterPromptDelayMs);
        var max = Math.Max(min, _options.MaxInterPromptDelayMs);
        if (max == 0) return;
        var delay = Random.Shared.Next(min, max + 1);
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    private ResiliencePipeline BuildRetryPipeline(BatchOptions options)
    {
        var builder = new ResiliencePipelineBuilder();
        if (options.MaxRetriesPerJob <= 0)
            return builder.Build(); // pass-through: one attempt only

        return builder.AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = options.MaxRetriesPerJob,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromSeconds(Math.Max(0, options.RetryBaseDelaySeconds)),
            ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransient),
            OnRetry = args =>
            {
                var job = args.Context.Properties.GetValue(JobKey, null!);
                var progress = args.Context.Properties.GetValue(ProgressKey, null!);
                job.LastError = args.Outcome.Exception?.Message;
                SetStatus(job, JobStatus.Retrying, progress);
                _logger.LogWarning(args.Outcome.Exception,
                    "Job {JobId} attempt {Attempt} failed; retrying in {Delay}", job.Id, job.Attempts, args.RetryDelay);
                return default;
            },
        }).Build();
    }

    /// <summary>Timeouts, navigation and download hiccups are retried; a refused prompt or an unusable account are not.</summary>
    private static bool IsTransient(Exception ex) =>
        ex is not (OperationCanceledException or PermanentJobException or AccountUnavailableException);

    private IDisposable? BeginJobScope(PromptJob job) =>
        _logger.BeginScope(new Dictionary<string, object?> { ["JobId"] = job.Id });

    private static BatchResult BuildResult(IReadOnlyList<PromptJob> jobs, RunState run, int leftovers)
    {
        var used = run.UsedAccounts;
        var quarantined = used
            .Where(a => a.IsQuarantined)
            .Select(a => new QuarantinedAccount(a.Id, a.Email, a.QuarantineReason ?? "unknown"))
            .ToList();

        var stopReason = leftovers == 0 ? BatchStopReason.Finished
            : quarantined.Count == used.Count ? BatchStopReason.AllAccountsQuarantined
            : BatchStopReason.AccountsExhausted;

        return new BatchResult(jobs.Count, run.Completed, run.Skipped, run.Failed)
        {
            StopReason = stopReason,
            AccountsUsed = used.Count,
            QuarantinedAccounts = quarantined,
        };
    }

    private void LogSummary(string how, BatchResult result)
    {
        _logger.LogInformation(
            "Batch {How} ({StopReason}): total={Total} completed={Completed} skipped={Skipped} failed={Failed} remaining={Remaining} accountsUsed={AccountsUsed} quarantinedAccounts={Quarantined}",
            how, result.StopReason, result.Total, result.Completed, result.Skipped, result.Failed, result.Remaining, result.AccountsUsed, result.QuarantinedAccounts.Count);

        foreach (var account in result.QuarantinedAccounts)
            _logger.LogWarning("Quarantined account {AccountId}: {Reason}", account.Id, account.Reason);
    }

    private static void SetStatus(PromptJob job, JobStatus status, IProgress<JobUpdate> progress)
    {
        job.Status = status;
        progress.Report(JobUpdate.From(job));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private enum JobOutcome { Completed, Skipped, Failed, FailedPermanent, AccountLost }

    private enum SessionExit { QueueDrained, AccountDone, Quarantined, SessionLost }

    /// <summary>
    /// The shared job queue, the pool of accounts that haven't had their turn yet, plus counters. A job is
    /// "outstanding" from enqueue until it reaches a terminal state; requeueing keeps it outstanding. The queue
    /// completes when nothing is outstanding (or no worker is left).
    /// </summary>
    private sealed class RunState(IEnumerable<GeminiAccount> accounts)
    {
        private readonly Channel<PromptJob> _channel = Channel.CreateUnbounded<PromptJob>();
        private readonly ConcurrentQueue<GeminiAccount> _unusedAccounts = new(accounts);
        private readonly ConcurrentQueue<GeminiAccount> _usedAccounts = new();
        private int _completed, _skipped, _failed, _outstanding;

        public int ActiveWorkers;

        public ChannelReader<PromptJob> Reader => _channel.Reader;
        public ChannelWriter<PromptJob> Writer => _channel.Writer;

        public int Completed => Volatile.Read(ref _completed);
        public int Skipped => Volatile.Read(ref _skipped);
        public int Failed => Volatile.Read(ref _failed);
        public int Outstanding => Volatile.Read(ref _outstanding);
        public IReadOnlyList<GeminiAccount> UsedAccounts => [.. _usedAccounts];

        /// <summary>The next account in roster order that hasn't had its turn; false once the roster is used up.</summary>
        public bool TryTakeAccount([NotNullWhen(true)] out GeminiAccount? account)
        {
            if (!_unusedAccounts.TryDequeue(out account)) return false;
            _usedAccounts.Enqueue(account);
            return true;
        }

        public void Enqueue(PromptJob job)
        {
            Interlocked.Increment(ref _outstanding);
            _channel.Writer.TryWrite(job);
        }

        public void CompleteIfNothingOutstanding()
        {
            if (Outstanding == 0) _channel.Writer.TryComplete();
        }

        /// <summary>An outstanding job reached a terminal state.</summary>
        public void Finish(JobStatus terminal)
        {
            switch (terminal)
            {
                case JobStatus.Completed: Interlocked.Increment(ref _completed); break;
                case JobStatus.Skipped: Interlocked.Increment(ref _skipped); break;
                default: Interlocked.Increment(ref _failed); break;
            }

            if (Interlocked.Decrement(ref _outstanding) == 0)
                _channel.Writer.TryComplete();
        }

        /// <summary>A job that never became outstanding (skipped up front, or failed while draining the queue).</summary>
        public void CountSkipped() => Interlocked.Increment(ref _skipped);
        public void CountFailed() => Interlocked.Increment(ref _failed);
    }
}
