using GeminiBatch.Domain;

namespace GeminiBatch.Application.Abstractions;

public interface IGeminiSessionFactory
{
    Task<IGeminiSession> CreateAsync(GeminiAccount account, CancellationToken ct);
}
