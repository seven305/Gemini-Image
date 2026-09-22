using GeminiBatch.Application.Abstractions;
using GeminiBatch.Application.Options;
using GeminiBatch.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace GeminiBatch.Infrastructure.Gemini;

/// <summary>
/// Launches one persistent browser context per account and hands back a session that owns it.
/// The session disposes the context; a failure mid-launch closes it here so no profile lock leaks.
/// </summary>
public sealed class PlaywrightGeminiSessionFactory : IGeminiSessionFactory
{
    private readonly PlaywrightBrowserLauncher _launcher;
    private readonly GeminiSessionOptions _options;
    private readonly BatchOptions _batchOptions;
    private readonly ILoggerFactory _loggerFactory;

    public PlaywrightGeminiSessionFactory(
        PlaywrightBrowserLauncher launcher,
        IOptions<GeminiSessionOptions> options,
        IOptions<BatchOptions> batchOptions,
        ILoggerFactory loggerFactory)
    {
        _launcher = launcher;
        _options = options.Value;
        _batchOptions = batchOptions.Value;
        _loggerFactory = loggerFactory;
    }

    public async Task<IGeminiSession> CreateAsync(GeminiAccount account, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var context = await _launcher.LaunchAsync(account, _batchOptions.Headful, ct).ConfigureAwait(false);
        try
        {
            var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync().ConfigureAwait(false);
            return new PlaywrightGeminiSession(
                account, context, page, _options, _batchOptions,
                _loggerFactory.CreateLogger<PlaywrightGeminiSession>());
        }
        catch
        {
            try { await context.CloseAsync().ConfigureAwait(false); } catch (PlaywrightException) { }
            throw;
        }
    }
}
