using System.Collections.Concurrent;
using GeminiBatch.Application.Abstractions;
using GeminiBatch.Application.Models;
using GeminiBatch.Domain;

namespace GeminiBatch.Application.Tests.TestDoubles;

/// <summary>Per-account behaviour script. Each call to GenerateAsync consumes one outcome; the last outcome repeats.</summary>
public delegate Task<GeneratedImage> GenerateBehaviour(string prompt, int callNumber, CancellationToken ct);

public sealed class ScriptedSessionFactory : IGeminiSessionFactory
{
    private readonly ConcurrentDictionary<string, GenerateBehaviour> _behaviours = new();
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task>> _readyBehaviours = new();

    public ConcurrentBag<ScriptedSession> Sessions { get; } = new();

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
        var session = new ScriptedSession(account.Id, generate, ready);
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
}

public sealed class ScriptedSession : IGeminiSession
{
    private readonly GenerateBehaviour _generate;
    private readonly Func<CancellationToken, Task> _ready;
    private int _calls;

    public ScriptedSession(string accountId, GenerateBehaviour generate, Func<CancellationToken, Task> ready)
    {
        AccountId = accountId;
        _generate = generate;
        _ready = ready;
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
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
