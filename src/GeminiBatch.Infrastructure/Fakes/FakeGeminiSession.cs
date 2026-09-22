using GeminiBatch.Application.Abstractions;
using GeminiBatch.Application.Models;
using GeminiBatch.Domain;
using Microsoft.Extensions.Logging;

namespace GeminiBatch.Infrastructure.Fakes;

/// <summary>Stand-in for the Playwright session: sleeps, optionally fails, and emits a 1x1 PNG.</summary>
public sealed class FakeGeminiSession : IGeminiSession
{
    // Smallest valid PNG: 1x1 RGBA transparent pixel.
    private static readonly byte[] PlaceholderPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x60, 0x00, 0x02, 0x00,
        0x00, 0x05, 0x00, 0x01, 0xE2, 0x26, 0x05, 0x9B, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44,
        0xAE, 0x42, 0x60, 0x82,
    ];

    private readonly GeminiAccount _account;
    private readonly FakeSessionOptions _options;
    private readonly ILogger<FakeGeminiSession> _logger;
    private readonly string _tempDir;

    public FakeGeminiSession(GeminiAccount account, FakeSessionOptions options, ILogger<FakeGeminiSession> logger)
    {
        _account = account;
        _options = options;
        _logger = logger;
        _tempDir = Path.Combine(Path.GetTempPath(), "geminibatch");
    }

    public string AccountId => _account.Id;

    public Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_options.ReadyFailAccountIds.Contains(_account.Id, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"[fake] account {_account.Id} is not signed in");

        _logger.LogDebug("[fake] session ready for {AccountId}", _account.Id);
        return Task.CompletedTask;
    }

    public async Task<GeneratedImage> GenerateAsync(string prompt, CancellationToken ct)
    {
        var min = Math.Max(0, _options.MinDelayMs);
        var max = Math.Max(min, _options.MaxDelayMs);
        await Task.Delay(Random.Shared.Next(min, max + 1), ct).ConfigureAwait(false);

        if (_options.AlwaysFailAccountIds.Contains(_account.Id, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"[fake] account {_account.Id} always fails");

        if (_options.FailureRate > 0 && Random.Shared.NextDouble() < _options.FailureRate)
            throw new InvalidOperationException("[fake] simulated transient failure");

        Directory.CreateDirectory(_tempDir);
        var tempPath = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(tempPath, PlaceholderPng, ct).ConfigureAwait(false);

        _logger.LogDebug("[fake] generated placeholder for prompt {PromptPreview}", Preview(prompt));
        return new GeneratedImage(tempPath, "gemini-image");
    }

    public ValueTask DisposeAsync()
    {
        _logger.LogDebug("[fake] session disposed for {AccountId}", _account.Id);
        return ValueTask.CompletedTask;
    }

    private static string Preview(string prompt) => prompt.Length <= 40 ? prompt : prompt[..40] + "…";
}
