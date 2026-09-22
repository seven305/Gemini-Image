using GeminiBatch.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace GeminiBatch.Infrastructure.Gemini;

/// <summary>
/// Owns the single process-wide <see cref="IPlaywright"/> driver and launches one persistent Chromium
/// context per account (its own user-data-dir, optional proxy). A profile can only be open in one
/// browser at a time, so callers must never launch the same account twice concurrently.
/// </summary>
public sealed class PlaywrightBrowserLauncher : IAsyncDisposable, IDisposable
{
    private readonly GeminiSessionOptions _options;
    private readonly ILogger<PlaywrightBrowserLauncher> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IPlaywright? _playwright;

    public PlaywrightBrowserLauncher(IOptions<GeminiSessionOptions> options, ILogger<PlaywrightBrowserLauncher> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IBrowserContext> LaunchAsync(GeminiAccount account, bool headful, CancellationToken ct)
    {
        var playwright = await GetPlaywrightAsync(ct).ConfigureAwait(false);
        var userDataDir = Path.GetFullPath(account.UserDataDir);
        Directory.CreateDirectory(userDataDir);

        var launchOptions = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = !headful,
            Channel = string.IsNullOrWhiteSpace(_options.Channel) ? null : _options.Channel,
            AcceptDownloads = true,
            ViewportSize = ViewportSize.NoViewport,
            // Spike-verified flags: drop Chrome's "controlled by automated software" banner and the
            // navigator.webdriver hint. No further evasion — Google did not react to these in the spike.
            IgnoreDefaultArgs = ["--enable-automation"],
            Args = BuildArgs(),
            Timeout = (float)TimeSpan.FromSeconds(_options.NavigationTimeoutSeconds).TotalMilliseconds,
        };

        if (account.Proxy is { } proxy)
        {
            launchOptions.Proxy = new Proxy
            {
                Server = proxy.Server,
                Username = proxy.Username,
                Password = proxy.Password,
            };
        }

        _logger.LogInformation("Launching {Channel} for account {AccountId} (headful={Headful}, proxy={Proxy}) with profile {UserDataDir}",
            launchOptions.Channel ?? "chromium", account.Id, headful, account.Proxy?.Server ?? "none", userDataDir);

        ct.ThrowIfCancellationRequested();
        var context = await playwright.Chromium.LaunchPersistentContextAsync(userDataDir, launchOptions).ConfigureAwait(false);
        context.SetDefaultTimeout((float)TimeSpan.FromSeconds(_options.ActionTimeoutSeconds).TotalMilliseconds);
        context.SetDefaultNavigationTimeout((float)TimeSpan.FromSeconds(_options.NavigationTimeoutSeconds).TotalMilliseconds);
        return context;
    }

    private string[] BuildArgs()
    {
        var args = new List<string> { "--disable-blink-features=AutomationControlled" };
        if (!string.IsNullOrWhiteSpace(_options.WindowSize))
            args.Add($"--window-size={_options.WindowSize}");
        return [.. args];
    }

    private async Task<IPlaywright> GetPlaywrightAsync(CancellationToken ct)
    {
        if (_playwright is not null) return _playwright;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);
            return _playwright;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _playwright?.Dispose();
        _playwright = null;
        _initLock.Dispose();
    }
}
