using GeminiBatch.Application.Abstractions;
using GeminiBatch.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeminiBatch.Infrastructure.Fakes;

public sealed class FakeGeminiSessionFactory : IGeminiSessionFactory
{
    private readonly IOptions<FakeSessionOptions> _options;
    private readonly ILoggerFactory _loggerFactory;

    public FakeGeminiSessionFactory(IOptions<FakeSessionOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _loggerFactory = loggerFactory;
    }

    public Task<IGeminiSession> CreateAsync(GeminiAccount account, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IGeminiSession session = new FakeGeminiSession(account, _options.Value, _loggerFactory.CreateLogger<FakeGeminiSession>());
        return Task.FromResult(session);
    }
}
