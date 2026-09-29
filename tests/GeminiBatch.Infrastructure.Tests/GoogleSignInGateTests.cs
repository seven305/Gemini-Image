using GeminiBatch.Infrastructure.Gemini;

namespace GeminiBatch.Infrastructure.Tests;

public sealed class GoogleSignInGateTests
{
    [Fact]
    public async Task Second_caller_waits_until_the_first_releases()
    {
        using var gate = new GoogleSignInGate();

        var first = await gate.EnterAsync(CancellationToken.None);
        var second = gate.EnterAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        first.Dispose();
        using var turn = await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Waiting_caller_honours_cancellation()
    {
        using var gate = new GoogleSignInGate();
        using var held = await gate.EnterAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.EnterAsync(cts.Token));
    }

    [Fact]
    public async Task Double_dispose_releases_only_once()
    {
        using var gate = new GoogleSignInGate();

        var first = await gate.EnterAsync(CancellationToken.None);
        first.Dispose();
        first.Dispose();

        using var second = await gate.EnterAsync(CancellationToken.None);
        var third = gate.EnterAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(third.IsCompleted);
    }
}
