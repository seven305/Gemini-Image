using System.Collections.Concurrent;
using GeminiBatch.Application.Abstractions;
using GeminiBatch.Application.Exceptions;
using GeminiBatch.Application.Models;
using GeminiBatch.Domain;

namespace GeminiBatch.Application.Tests.TestDoubles;

/// <summary>Per-account behaviour script. Each call to GenerateAsync consumes one outcome; the last outcome repeats.</summary>
public delegate Task<GeneratedImage> GenerateBehaviour(string prompt, int callNumber, CancellationToken ct);

public sealed class ScriptedSessionFactory : IGeminiSessionFactory
{
    private readonly ConcurrentDictionary<string, GenerateBehaviour> _behaviours = new();
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task>> _readyBehaviours = new();

    private int _active, _maxActive;

    public ConcurrentBag<ScriptedSession> Sessions { get; } = new();

    /// <summary>Account ids in the order their sessions were created.</summary>
    public ConcurrentQueue<string> LaunchOrder { get; } = new();

    /// <summary>Most sessions (browsers) that were open at the same time.</summary>
    public int MaxActiveSessions => Volatile.Read(ref _maxActive);

    public ScriptedSessionFactory OnGenerate(string accountId, GenerateBehaviour behaviour)
    {
        _behaviours[accountId] = behaviour;
        return this;
    }

    public ScriptedSessionFactory OnReady(string accountId, Func<CancellationToken, Task> behaviour)
    {
        _readyBehaviours[accountId] = behaviour;
        return this;
    }

    public Task<IGeminiSession> CreateAsync(GeminiAccount account, CancellationToken ct)
    {
        var generate = _behaviours.GetValueOrDefault(account.Id) ?? Succeed;
        var ready = _readyBehaviours.GetValueOrDefault(account.Id) ?? (_ => Task.CompletedTask);
        var session = new ScriptedSession(account.Id, generate, ready, () => Interlocked.Decrement(ref _active));
        var active = Interlocked.Increment(ref _active);
        int max;
        while (active > (max = Volatile.Read(ref _maxActive)) && Interlocked.CompareExchange(ref _maxActive, active, max) != max) { }
        LaunchOrder.Enqueue(account.Id);
        Sessions.Add(session);
        return Task.FromResult<IGeminiSession>(session);
    }

    public static Task<GeneratedImage> Succeed(string prompt, int call, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "geminibatch-tests", $"{Guid.NewGuid():N}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        File.WriteAllBytes(temp, [0x89, 0x50, 0x4E, 0x47]);
        return Task.FromResult(new GeneratedImage(temp, "img"));
    }

    /// <summary>Throws a transient error for the first <paramref name="failures"/> calls, then succeeds.</summary>
    public static GenerateBehaviour FailThenSucceed(int failures) => (prompt, call, ct) =>
        call <= failures
            ? throw new InvalidOperationException($"transient #{call}")
            : Succeed(prompt, call, ct);

    public static GenerateBehaviour AlwaysFail => (_, call, _) => throw new InvalidOperationException($"always fails #{call}");

    public static GenerateBehaviour AlwaysUnavailable(AccountUnavailableReason reason) =>
        (_, call, _) => throw new AccountUnavailableException(reason, $"{reason} #{call}");
}

public sealed class ScriptedSession : IGeminiSession
{
    private readonly GenerateBehaviour _generate;
    private readonly Func<CancellationToken, Task> _ready;
    private readonly Action _onDispose;
    private int _calls;

    public ScriptedSession(string accountId, GenerateBehaviour generate, Func<CancellationToken, Task> ready, Action onDispose)
    {
        AccountId = accountId;
        _generate = generate;
        _ready = ready;
        _onDispose = onDispose;
    }

    public string AccountId { get; }
    public int Calls => Volatile.Read(ref _calls);
    public bool Disposed { get; private set; }
    public ConcurrentQueue<string> Prompts { get; } = new();

    public Task EnsureReadyAsync(CancellationToken ct) => _ready(ct);

    public Task<GeneratedImage> GenerateAsync(string prompt, CancellationToken ct)
    {
        Prompts.Enqueue(prompt);
        var call = Interlocked.Increment(ref _calls);
        return _generate(prompt, call, ct);
    }

    public ValueTask DisposeAsync()
    {
        if (!Disposed) _onDispose();
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
