namespace GeminiBatch.Infrastructure.Gemini;

/// <summary>
/// App-wide "one Google sign-in at a time". Parallel sign-ins of several accounts from one IP are the
/// pattern most likely to trigger CAPTCHAs or account locks, so batch workers queue here. Workers whose
/// profile is already signed in never enter it.
/// </summary>
public sealed class GoogleSignInGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>Waits for the gate; dispose the result to release it.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                semaphore.Release();
        }
    }
}
