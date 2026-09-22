using GeminiBatch.Application.Abstractions;
using GeminiBatch.Domain;

namespace GeminiBatch.Infrastructure.Fakes;

/// <summary>Stands in for the browser login flow while <c>Batch:UseFakeSession</c> is on: there is nothing to sign in to.</summary>
public sealed class UnavailableAccountLoginService : IAccountLoginService
{
    public Task LoginInteractiveAsync(GeminiAccount account, TimeSpan timeout, IProgress<string>? status, CancellationToken ct) =>
        throw new NotSupportedException("Login is unavailable while the fake session is enabled. Set Batch:UseFakeSession=false to sign in to a real account.");
}
