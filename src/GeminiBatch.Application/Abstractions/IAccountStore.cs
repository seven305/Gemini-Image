using GeminiBatch.Domain;

namespace GeminiBatch.Application.Abstractions;

public interface IAccountStore
{
    Task<IReadOnlyList<GeminiAccount>> LoadAsync(CancellationToken ct);
}
