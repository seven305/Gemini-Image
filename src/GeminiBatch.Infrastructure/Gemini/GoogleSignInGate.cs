namespace GeminiBatch.Infrastructure.Gemini;

/// <summary>
/// App-wide cap on concurrent Google sign-ins (<see cref="GeminiSessionOptions.MaxConcurrentSignIns"/>). With no cap
/// every worker signs its own account in in parallel; a cap of 1 queues them one at a time, which is gentler on
/// Google's bot detection when all accounts share one IP. Workers whose profile is already signed in never enter it.
/// </summary>
public sealed class GoogleSignInGate : IDisposable
{
    private static readonly IDisposable NoOp = new Releaser(null);
    private readonly SemaphoreSlim? _semaphore;

    /// <param name="maxConcurrent">Sign-ins allowed at once; 0 or less means no limit.</param>
    public GoogleSignInGate(int maxConcurrent)
    {
        if (maxConcurrent > 0)
            _semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    /// <summary>Waits for the gate; dispose the result to release it.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        if (_semaphore is null)
        {
            ct.ThrowIfCancellationRequested();
            return NoOp;
        }

        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_semaphore);
    }

    public void Dispose() => _semaphore?.Dispose();

    private sealed class Releaser(SemaphoreSlim? semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (semaphore is not null && Interlocked.Exchange(ref _released, 1) == 0)
                semaphore.Release();
        }
    }
}
