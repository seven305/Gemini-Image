using GeminiBatch.Application.Abstractions;
using GeminiBatch.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace GeminiBatch.Infrastructure.Gemini;

/// <summary>
/// Opens an account's profile headful and polls until a human has signed in (spike finding: polling for
/// the session cookie is a far better handoff than Playwright's Inspector pause). The browser is closed
/// afterwards so the profile is flushed and unlocked for the batch to use.
/// </summary>
public sealed class PlaywrightAccountLoginService : IAccountLoginService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Chrome writes cookies lazily; closing immediately can lose the session.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);

    private readonly PlaywrightBrowserLauncher _launcher;
    private readonly GeminiSessionOptions _options;
    private readonly ILogger<PlaywrightAccountLoginService> _logger;

    public PlaywrightAccountLoginService(
        PlaywrightBrowserLauncher launcher,
        IOptions<GeminiSessionOptions> options,
        ILogger<PlaywrightAccountLoginService> logger)
    {
        _launcher = launcher;
        _options = options.Value;
        _logger = logger;
    }

    public async Task LoginInteractiveAsync(GeminiAccount account, TimeSpan timeout, IProgress<string>? status, CancellationToken ct)
    {
        var context = await _launcher.LaunchAsync(account, headful: true, ct).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync().ConfigureAwait(false);
            await page.GotoAsync(GeminiSelectors.AppUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct).ConfigureAwait(false);

            status?.Report($"Sign in to {account.Email} in the browser window (including 2FA). Waiting…");
            _logger.LogInformation("Waiting for manual sign-in on account {AccountId} (up to {Timeout})", account.Id, timeout);

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (page.IsClosed)
                    throw new InvalidOperationException("The login window was closed before sign-in completed.");

                var state = await GeminiSignInCheck.ProbeAsync(context, page).ConfigureAwait(false);
                if (state.IsSignedIn)
                {
                    status?.Report($"Signed in as {account.Email}. Saving profile…");
                    _logger.LogInformation("Sign-in detected for account {AccountId}", account.Id);
                    await Task.Delay(SettleDelay, ct).ConfigureAwait(false);
                    await CloseQuietlyAsync(context).ConfigureAwait(false);
                    return;
                }

                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }

            await CloseQuietlyAsync(context).ConfigureAwait(false);
            throw new TimeoutException($"No sign-in detected for {account.Id} within {timeout.TotalMinutes:F0} minutes.");
        }
    }

    private static async Task CloseQuietlyAsync(IBrowserContext context)
    {
        try { await context.CloseAsync().ConfigureAwait(false); } catch (PlaywrightException) { }
    }
}
