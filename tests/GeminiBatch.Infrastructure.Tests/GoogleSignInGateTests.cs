using GeminiBatch.Infrastructure.Gemini;

namespace GeminiBatch.Infrastructure.Tests;

public sealed class GoogleSignInGateTests
{
    [Fact]
    public async Task Second_caller_waits_until_the_first_releases()
    {
        using var gate = new GoogleSignInGate(1);

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
        using var gate = new GoogleSignInGate(1);
        using var held = await gate.EnterAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.EnterAsync(cts.Token));
    }

    [Fact]
    public async Task Double_dispose_releases_only_once()
    {
        using var gate = new GoogleSignInGate(1);

        var first = await gate.EnterAsync(CancellationToken.None);
        first.Dispose();
        first.Dispose();

        using var second = await gate.EnterAsync(CancellationToken.None);
        var third = gate.EnterAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(third.IsCompleted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task No_limit_lets_every_caller_in_at_once(int maxConcurrent)
    {
        using var gate = new GoogleSignInGate(maxConcurrent);

        var turns = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => gate.EnterAsync(CancellationToken.None)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var turn in turns) turn.Dispose();
    }

    [Fact]
    public async Task Limit_of_two_admits_two_and_queues_the_third()
    {
        using var gate = new GoogleSignInGate(2);

        var first = await gate.EnterAsync(CancellationToken.None);
        using var second = await gate.EnterAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var third = gate.EnterAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(third.IsCompleted);

        first.Dispose();
        using var turn = await third.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
