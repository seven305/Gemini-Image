using System.Collections.Concurrent;
using GeminiBatch.Application.Abstractions;
using GeminiBatch.Domain;
using GeminiBatch.Infrastructure.Processing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeminiBatch.Infrastructure.Fakes;

public sealed class FakeGeminiSessionFactory : IGeminiSessionFactory
{
    private readonly IOptions<FakeSessionOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, int> _sessionsPerAccount = new(StringComparer.OrdinalIgnoreCase);

    public FakeGeminiSessionFactory(IOptions<FakeSessionOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _loggerFactory = loggerFactory;
    }

    public Task<IGeminiSession> CreateAsync(GeminiAccount account, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sessionNumber = _sessionsPerAccount.AddOrUpdate(account.Id, 1, (_, n) => n + 1);
        IGeminiSession session = new FakeGeminiSession(account, _options.Value, sessionNumber, _loggerFactory.CreateLogger<FakeGeminiSession>());
        return Task.FromResult(session);
    }
}
