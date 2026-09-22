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
/// Fans a list of prompt jobs out over N workers, one per enabled account. Each worker owns a single
/// <see cref="IGeminiSession"/> for its lifetime. Per-job retries use exponential backoff with jitter;
/// an account is quarantined after <see cref="BatchOptions.AccountFailureThreshold"/> consecutive job
/// failures and the batch carries on with whatever accounts remain.
/// </summary>
public sealed class BatchProcessor
{
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

        var eligible = accounts.Where(a => a.Enabled && !a.IsQuarantined).ToList();
        if (eligible.Count == 0)
            throw new InvalidOperationException("No enabled, non-quarantined accounts are available.");

        var workerCount = Math.Clamp(concurrency, 1, eligible.Count);
        var counters = new Counters();

        var channel = Channel.CreateBounded<PromptJob>(new BoundedChannelOptions(workerCount * 2)
        {
            SingleWriter = true,
            SingleReader = workerCount == 1,
        });

        // The producer must stop blocking on a full channel once every worker has died, otherwise the
        // batch would hang forever. Workers cancel this token when the last one exits.
        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var activeWorkers = workerCount;

        _logger.LogInformation("Starting batch: {JobCount} jobs across {WorkerCount} workers", jobs.Count, workerCount);

        var producer = Task.Run(() => ProduceAsync(jobs, channel.Writer, progress, counters, producerCts.Token), CancellationToken.None);

        var workers = eligible.Take(workerCount).Select(account => Task.Run(async () =>
        {
            try
            {
                await WorkerAsync(account, channel.Reader, progress, counters, ct).ConfigureAwait(false);
            }
            finally
            {
                if (Interlocked.Decrement(ref activeWorkers) == 0)
                    producerCts.Cancel();
            }
        }, CancellationToken.None)).ToArray();

        try
        {
            await Task.WhenAll(workers.Append(producer)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("Batch cancelled. Completed={Completed} Skipped={Skipped} Failed={Failed}",
                counters.Completed, counters.Skipped, counters.Failed);
            throw;
        }

        // Anything still Pending here was never picked up because every worker died. It is not a
        // cancellation, so report it as a failure rather than leaving it silently unprocessed.
        foreach (var job in jobs.Where(j => j.Status == JobStatus.Pending))
        {
            job.Status = JobStatus.Failed;
            job.LastError = "No healthy accounts remaining.";
            counters.IncrementFailed();
            progress.Report(JobUpdate.From(job));
        }

        var result = new BatchResult(jobs.Count, counters.Completed, counters.Skipped, counters.Failed);
        _logger.LogInformation("Batch finished: {@Result}", result);
        return result;
    }

    private async Task ProduceAsync(
        IReadOnlyList<PromptJob> jobs,
        ChannelWriter<PromptJob> writer,
        IProgress<JobUpdate> progress,
        Counters counters,
        CancellationToken ct)
    {
        try
        {
            foreach (var job in jobs)
            {
                if (_manifest.IsCompleted(job.ManifestKey))
                {
                    job.Status = JobStatus.Skipped;
                    counters.IncrementSkipped();
                    progress.Report(JobUpdate.From(job));
                    _logger.LogInformation("Skipping job {JobId}: manifest key {ManifestKey} already completed", job.Id, job.ManifestKey);
                    continue;
                }

                await writer.WriteAsync(job, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Either the user cancelled (propagated by the workers) or all workers died; both end the batch.
        }
        finally
        {
            writer.Complete();
        }
    }

    private async Task WorkerAsync(
        GeminiAccount account,
        ChannelReader<PromptJob> reader,
        IProgress<JobUpdate> progress,
        Counters counters,
        CancellationToken ct)
    {
        using var accountScope = _logger.BeginScope(new Dictionary<string, object?> { ["AccountId"] = account.Id });

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
            account.Quarantine($"Session creation failed: {ex.Message}");
            _logger.LogError(ex, "Account {AccountId} quarantined: session creation failed", account.Id);
            return;
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
            catch (Exception ex)
            {
                account.Quarantine($"Session not ready: {ex.Message}");
                _logger.LogError(ex, "Account {AccountId} quarantined: session failed to become ready", account.Id);
                return;
            }

            var consecutiveFailures = 0;

            await foreach (var job in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                job.AssignedAccountId = account.Id;
                bool succeeded;
                using (_logger.BeginScope(new Dictionary<string, object?> { ["JobId"] = job.Id }))
                {
                    succeeded = await ProcessJobAsync(job, session, progress, ct).ConfigureAwait(false);
                }

                if (succeeded)
                {
                    consecutiveFailures = 0;
                    counters.IncrementCompleted();
                }
                else
                {
                    consecutiveFailures++;
                    counters.IncrementFailed();

                    if (consecutiveFailures >= _options.AccountFailureThreshold)
                    {
                        account.Quarantine($"{consecutiveFailures} consecutive job failures");
                        _logger.LogWarning("Account {AccountId} quarantined after {Failures} consecutive job failures; worker exiting",
                            account.Id, consecutiveFailures);
                        return;
                    }
                }

                await InterPromptDelayAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Runs one job through the retry pipeline. Returns true on success, false once retries are exhausted.</summary>
    private async Task<bool> ProcessJobAsync(PromptJob job, IGeminiSession session, IProgress<JobUpdate> progress, CancellationToken ct)
    {
        var context = ResilienceContextPool.Shared.Get(ct);
        context.Properties.Set(JobKey, job);
        context.Properties.Set(ProgressKey, progress);

        try
        {
            await _retryPipeline.ExecuteAsync(
                async ctx => await AttemptAsync(job, session, progress, ctx.CancellationToken).ConfigureAwait(false),
                context).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Put the job back to Pending so a re-run picks it up; it is not in the manifest.
            job.Status = JobStatus.Pending;
            progress.Report(JobUpdate.From(job));
            throw;
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.LastError = ex.Message;
            progress.Report(JobUpdate.From(job));
            _logger.LogError(ex, "Job {JobId} failed after {Attempts} attempt(s)", job.Id, job.Attempts);
            return false;
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>A single generate → save → strip → manifest attempt. Any exception here is judged by the retry policy.</summary>
    private async ValueTask AttemptAsync(PromptJob job, IGeminiSession session, IProgress<JobUpdate> progress, CancellationToken ct)
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
            savedPath = await _storage.SaveAsync(image.TempFilePath, job.DesiredFileName ?? image.SuggestedFileName, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(image.TempFilePath);
        }

        if (_options.StripMetadata)
            await _imageProcessor.StripMetadataAsync(savedPath, ct).ConfigureAwait(false);

        await _manifest.MarkCompletedAsync(job.ManifestKey, savedPath, ct).ConfigureAwait(false);

        job.SavedPath = savedPath;
        job.LastError = null;
        SetStatus(job, JobStatus.Completed, progress);
        _logger.LogInformation("Job {JobId} completed -> {SavedPath}", job.Id, savedPath);
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

    private static bool IsTransient(Exception ex) => ex is not (OperationCanceledException or PermanentJobException);

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

    private sealed class Counters
    {
        private int _completed, _skipped, _failed;
        public int Completed => Volatile.Read(ref _completed);
        public int Skipped => Volatile.Read(ref _skipped);
        public int Failed => Volatile.Read(ref _failed);
        public void IncrementCompleted() => Interlocked.Increment(ref _completed);
        public void IncrementSkipped() => Interlocked.Increment(ref _skipped);
        public void IncrementFailed() => Interlocked.Increment(ref _failed);
    }
}
