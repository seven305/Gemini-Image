using System.Diagnostics;
using GeminiBatch.Application.Abstractions;
using GeminiBatch.Application.Exceptions;
using GeminiBatch.Application.Models;
using GeminiBatch.Application.Options;
using GeminiBatch.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace GeminiBatch.Infrastructure.Gemini;

/// <summary>
/// One headful Chromium profile driving gemini.google.com for one account. Every prompt gets a fresh
/// conversation (a follow-up in the same chat is treated by Gemini as an *edit* of the previous image).
/// All DOM knowledge is delegated to <see cref="GeminiSelectors"/>; this class only sequences waits.
///
/// Waits are explicit (locator state + timeout) — never fixed sleeps. Playwright calls take no
/// CancellationToken, so the long ones are wrapped in <c>Task.WaitAsync(ct)</c>; the abandoned Playwright
/// task then fails harmlessly when the context is closed.
/// </summary>
public sealed class PlaywrightGeminiSession : IGeminiSession
{
    private readonly GeminiAccount _account;
    private readonly IBrowserContext _context;
    private readonly GeminiSessionOptions _options;
    private readonly BatchOptions _batchOptions;
    private readonly ILogger<PlaywrightGeminiSession> _logger;
    private readonly string _tempDir;
    private readonly string _diagnosticsDir;
    private IPage _page;
    private bool _disposed;

    public PlaywrightGeminiSession(
        GeminiAccount account,
        IBrowserContext context,
        IPage page,
        GeminiSessionOptions options,
        BatchOptions batchOptions,
        ILogger<PlaywrightGeminiSession> logger)
    {
        _account = account;
        _context = context;
        _page = page;
        _options = options;
        _batchOptions = batchOptions;
        _logger = logger;
        _tempDir = Path.Combine(Path.GetTempPath(), "geminibatch");
        _diagnosticsDir = Path.Combine(Path.GetFullPath(options.DiagnosticsFolder), SanitizeForPath(account.Id));
    }

    public string AccountId => _account.Id;

    public async Task EnsureReadyAsync(CancellationToken ct)
    {
        var step = "ensure-ready";
        try
        {
            await OpenNewChatAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Session ready for {AccountId} at {Url}", _account.Id, _page.Url);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await CaptureDiagnosticsAsync(step).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<GeneratedImage> GenerateAsync(string prompt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var total = Stopwatch.StartNew();
        var step = "start";
        try
        {
            step = "new-chat";
            await OpenNewChatAsync(ct).ConfigureAwait(false);

            step = "enable-image-tool";
            await EnableImageToolAsync(ct).ConfigureAwait(false);

            // Subscribe before submitting so the CDN fetch for this turn cannot be missed (Net mode).
            using var capture = new CdnCapture(_page);

            step = "submit";
            await SubmitPromptAsync(prompt, ct).ConfigureAwait(false);
            capture.MarkSubmitted();

            step = "wait-generation";
            await WaitForGenerationAsync(ct).ConfigureAwait(false);

            step = "download";
            var image = _options.DownloadMode == GeminiDownloadMode.Ui
                ? await DownloadViaUiAsync(ct).ConfigureAwait(false)
                : await DownloadViaCdnAsync(capture, ct).ConfigureAwait(false);

            _logger.LogInformation("Generated image in {Elapsed:F1}s -> {TempPath} ({Size} KB)",
                total.Elapsed.TotalSeconds, image.TempFilePath, new FileInfo(image.TempFilePath).Length / 1024);
            return image;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Generation failed at step {Step} after {Elapsed:F1}s", step, total.Elapsed.TotalSeconds);
            await CaptureDiagnosticsAsync(step).ConfigureAwait(false);
            throw;
        }
    }

    // ---- Steps ------------------------------------------------------------------------------

    /// <summary>Navigate to /app (always a fresh conversation), assert the account is usable, dismiss one-off notices.</summary>
    private async Task OpenNewChatAsync(CancellationToken ct)
    {
        await EnsurePageAsync(ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();

        await _page.GotoAsync(GeminiSelectors.AppUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded })
            .WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await GeminiSelectors.PromptBox(_page)
                .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = Ms(_options.NavigationTimeoutSeconds) })
                .WaitAsync(ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            if (GeminiSelectors.IsLoginRedirect(_page.Url))
                throw new AccountUnavailableException(AccountUnavailableReason.Challenged,
                    $"Redirected to a Google sign-in/verification page: {_page.Url}", ex);

            throw new AccountUnavailableException(AccountUnavailableReason.SurfaceUnavailable,
                $"Gemini prompt box did not appear within {_options.NavigationTimeoutSeconds}s at {_page.Url}. " +
                "The page may be blocked, down, or the UI changed (check GeminiSelectors).", ex);
        }

        var state = await GeminiSignInCheck.ProbeAsync(_context, _page).ConfigureAwait(false);
        if (!state.IsSignedIn)
        {
            var reason = GeminiSelectors.IsLoginRedirect(state.Url) ? AccountUnavailableReason.Challenged : AccountUnavailableReason.SignedOut;
            throw new AccountUnavailableException(reason,
                $"Account {_account.Id} is not signed in (sessionCookie={state.HasSessionCookie}, signInButton={state.SignInButtonVisible}, url={state.Url}). " +
                "Use Login… to re-authenticate this profile.");
        }

        await DismissNoticeIfPresentAsync().ConfigureAwait(false);
        _logger.LogDebug("New chat ready in {Elapsed} ms", sw.ElapsedMilliseconds);
    }

    /// <summary>Recreate the page if a previous attempt lost it (e.g. the UI-download crash closes the tab).</summary>
    private async Task EnsurePageAsync(CancellationToken ct)
    {
        if (!_page.IsClosed) return;
        _logger.LogWarning("Page for {AccountId} is closed; opening a new one in the same context", _account.Id);
        try
        {
            _page = await _context.NewPageAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (PlaywrightException ex)
        {
            throw new GeminiSessionException("Browser is gone (crashed or closed); the session cannot recover.", ex);
        }
    }

    private async Task DismissNoticeIfPresentAsync()
    {
        try
        {
            var gotIt = GeminiSelectors.DismissNoticeButton(_page);
            if (await gotIt.CountAsync().ConfigureAwait(false) > 0 && await gotIt.IsVisibleAsync().ConfigureAwait(false))
            {
                await gotIt.ClickAsync(new() { Timeout = 5_000 }).ConfigureAwait(false);
                _logger.LogDebug("Dismissed first-visit notice");
            }
        }
        catch (PlaywrightException ex)
        {
            // Purely cosmetic; the image tool check below will surface anything that actually blocks us.
            _logger.LogDebug(ex, "Could not dismiss first-visit notice");
        }
    }

    private async Task EnableImageToolAsync(CancellationToken ct)
    {
        var chip = GeminiSelectors.ImageModeChip(_page);
        if (await chip.CountAsync().ConfigureAwait(false) > 0)
        {
            _logger.LogDebug("Image tool already enabled");
            return;
        }

        await GeminiSelectors.ToolsMenuButton(_page).ClickAsync().WaitAsync(ct).ConfigureAwait(false);
        var item = GeminiSelectors.CreateImageMenuItem(_page);
        await item.WaitForAsync(new() { State = WaitForSelectorState.Visible }).WaitAsync(ct).ConfigureAwait(false);
        await item.ClickAsync().WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await chip.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = Ms(_options.ActionTimeoutSeconds) })
                .WaitAsync(ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new GeminiSessionException($"Clicked '{GeminiSelectors.ImageToolName}' but the image-mode chip never appeared.", ex);
        }
        _logger.LogDebug("Image tool enabled");
    }

    private async Task SubmitPromptAsync(string prompt, CancellationToken ct)
    {
        var box = GeminiSelectors.PromptBox(_page);
        await box.ClickAsync().WaitAsync(ct).ConfigureAwait(false);
        await box.FillAsync(prompt).WaitAsync(ct).ConfigureAwait(false);

        var send = GeminiSelectors.SendButton(_page);
        await send.WaitForAsync(new() { State = WaitForSelectorState.Visible }).WaitAsync(ct).ConfigureAwait(false);
        await send.ClickAsync().WaitAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Submitted prompt: {PromptPreview}", Preview(prompt));
    }

    /// <summary>
    /// Turn complete = "Stop response" gone AND the generated <c>img.loaded</c> visible. The image can show
    /// up while the turn is still streaming, so both are required (spike finding #7).
    /// </summary>
    private async Task WaitForGenerationAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var stop = GeminiSelectors.StopResponseButton(_page);

        try
        {
            await stop.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = Ms(_options.ActionTimeoutSeconds) })
                .WaitAsync(ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Either the turn finished before we looked, or the submit did not register; the image wait decides.
            _logger.LogWarning("'Stop response' never appeared within {Seconds}s; continuing", _options.ActionTimeoutSeconds);
        }

        var generationTimeout = _batchOptions.GenerationTimeoutSeconds > 0 ? _batchOptions.GenerationTimeoutSeconds : 600;
        await stop.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = Ms(generationTimeout) })
            .WaitAsync(ct).ConfigureAwait(false);
        _logger.LogDebug("Response streaming finished after {Elapsed:F1}s", sw.Elapsed.TotalSeconds);

        try
        {
            await GeminiSelectors.LatestGeneratedImage(_page)
                .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = Ms(_options.PostResponseImageWaitSeconds) })
                .WaitAsync(ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new GeminiSessionException(
                "Gemini finished the turn without producing an image (refused prompt, quota, or UI change). See diagnostics.", ex);
        }
        _logger.LogInformation("Image generated in {Elapsed:F1}s", sw.Elapsed.TotalSeconds);
    }

    // ---- Download ---------------------------------------------------------------------------

    /// <summary>
    /// Net mode: take the CDN response Gemini fetched for this turn, re-request it at full size through the
    /// authenticated context, fall back to the captured preview body if that fails.
    /// </summary>
    private async Task<GeneratedImage> DownloadViaCdnAsync(CdnCapture capture, CancellationToken ct)
    {
        var response = await capture.WaitForLatestAsync(TimeSpan.FromSeconds(_options.CdnCaptureWaitSeconds), ct).ConfigureAwait(false)
            ?? throw new GeminiSessionException(
                "Image rendered but no generated-image CDN response was captured; cannot download in Net mode (URL shape may have changed).");

        _logger.LogDebug("Captured {Count} CDN response(s); using {Url}", capture.Count, Truncate(response.Url, 100));

        byte[] bytes;
        string? contentType;
        var fullUrl = GeminiSelectors.ToFullSizeCdnUrl(response.Url);
        try
        {
            var full = await _context.APIRequest.GetAsync(fullUrl).WaitAsync(ct).ConfigureAwait(false);
            var fullType = full.Headers.GetValueOrDefault("content-type");
            if (full.Ok && fullType is not null && fullType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                bytes = await full.BodyAsync().WaitAsync(ct).ConfigureAwait(false);
                contentType = fullType;
                _logger.LogDebug("Full-size CDN fetch OK ({Size} KB)", bytes.Length / 1024);
            }
            else
            {
                _logger.LogWarning("Full-size CDN fetch returned {Status} {ContentType}; falling back to preview body", full.Status, fullType);
                (bytes, contentType) = await ReadCapturedBodyAsync(response, ct).ConfigureAwait(false);
            }
        }
        catch (PlaywrightException ex)
        {
            _logger.LogWarning(ex, "Full-size CDN fetch failed; falling back to preview body");
            (bytes, contentType) = await ReadCapturedBodyAsync(response, ct).ConfigureAwait(false);
        }

        if (bytes.Length == 0)
            throw new GeminiSessionException("Downloaded image body was empty.");

        var tempPath = await WriteTempAsync(bytes, ExtensionFor(contentType), ct).ConfigureAwait(false);
        return new GeneratedImage(tempPath, $"gemini_{DateTime.Now:yyyyMMdd_HHmmss}");
    }

    private static async Task<(byte[] Bytes, string? ContentType)> ReadCapturedBodyAsync(IResponse response, CancellationToken ct)
    {
        var body = await response.BodyAsync().WaitAsync(ct).ConfigureAwait(false);
        return (body, response.Headers.GetValueOrDefault("content-type"));
    }

    /// <summary>Ui mode: click "Download full size image" and catch the browser download (bit-exact, but see the crash note in options).</summary>
    private async Task<GeneratedImage> DownloadViaUiAsync(CancellationToken ct)
    {
        await GeminiSelectors.LatestGeneratedImage(_page).HoverAsync().WaitAsync(ct).ConfigureAwait(false);

        var download = await _page.RunAndWaitForDownloadAsync(
                () => GeminiSelectors.DownloadFullSizeButton(_page).ClickAsync(),
                new() { Timeout = Ms(_options.ActionTimeoutSeconds * 2) })
            .WaitAsync(ct).ConfigureAwait(false);

        var suggested = download.SuggestedFilename;
        var extension = Path.GetExtension(suggested);
        if (string.IsNullOrEmpty(extension)) extension = ".jpg";

        Directory.CreateDirectory(_tempDir);
        var tempPath = Path.Combine(_tempDir, $"{Guid.NewGuid():N}{extension}");
        await download.SaveAsAsync(tempPath).WaitAsync(ct).ConfigureAwait(false);

        var baseName = Path.GetFileNameWithoutExtension(suggested);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = $"gemini_{DateTime.Now:yyyyMMdd_HHmmss}";
        _logger.LogDebug("UI download saved (suggested name {Suggested})", suggested);
        return new GeneratedImage(tempPath, baseName);
    }

    private async Task<string> WriteTempAsync(byte[] bytes, string extension, CancellationToken ct)
    {
        Directory.CreateDirectory(_tempDir);
        var tempPath = Path.Combine(_tempDir, $"{Guid.NewGuid():N}{extension}");
        await File.WriteAllBytesAsync(tempPath, bytes, ct).ConfigureAwait(false);
        return tempPath;
    }

    // ---- Diagnostics / lifecycle ------------------------------------------------------------

    private async Task CaptureDiagnosticsAsync(string step)
    {
        try
        {
            if (_page.IsClosed)
            {
                _logger.LogWarning("No diagnostics for step {Step}: page is closed (browser crash?)", step);
                return;
            }

            Directory.CreateDirectory(_diagnosticsDir);
            var stamp = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{step}";
            var shotPath = Path.Combine(_diagnosticsDir, stamp + ".png");
            await _page.ScreenshotAsync(new() { Path = shotPath, Timeout = 10_000 }).ConfigureAwait(false);

            // The ARIA snapshot is what the selectors are built from; it is the fastest way to spot UI churn.
            var aria = await GeminiSelectors.Root(_page).AriaSnapshotAsync(new() { Timeout = 10_000 }).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(_diagnosticsDir, stamp + ".aria.yml"), aria).ConfigureAwait(false);

            _logger.LogWarning("Diagnostics for step {Step}: {Path}", step, shotPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not capture diagnostics for step {Step}", step);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            await _context.CloseAsync().ConfigureAwait(false); // flushes the persistent profile
        }
        catch (PlaywrightException ex)
        {
            _logger.LogDebug(ex, "Context close failed (browser already gone?) for {AccountId}", _account.Id);
        }

        try
        {
            await _context.DisposeAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException) { }

        _logger.LogInformation("Session disposed for {AccountId}", _account.Id);
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private static float Ms(int seconds) => (float)TimeSpan.FromSeconds(Math.Max(1, seconds)).TotalMilliseconds;

    private static string ExtensionFor(string? contentType)
    {
        var type = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        return type switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".jpg",
        };
    }

    private static string Preview(string prompt) => prompt.Length <= 60 ? prompt : prompt[..60] + "…";
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string SanitizeForPath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>Records the generated-image CDN responses seen on the page from subscription until disposal.</summary>
    private sealed class CdnCapture : IDisposable
    {
        private readonly IPage _page;
        private readonly List<IResponse> _responses = [];
        private readonly Lock _lock = new();
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _submitted;

        public CdnCapture(IPage page)
        {
            _page = page;
            _page.Response += OnResponse;
        }

        public int Count { get { lock (_lock) return _responses.Count; } }

        /// <summary>Only responses after the prompt was sent belong to this turn (earlier ones are sidebar/history thumbnails).</summary>
        public void MarkSubmitted()
        {
            lock (_lock)
            {
                _submitted = true;
                _responses.Clear();
            }
        }

        private void OnResponse(object? sender, IResponse response)
        {
            var contentType = response.Headers.GetValueOrDefault("content-type");
            if (!GeminiSelectors.IsGeneratedImageResponse(response.Url, contentType)) return;

            lock (_lock)
            {
                if (!_submitted) return;
                _responses.Add(response);
            }
            _first.TrySetResult();
        }

        /// <summary>The most recent matching response, waiting up to <paramref name="wait"/> for the first one to arrive.</summary>
        public async Task<IResponse?> WaitForLatestAsync(TimeSpan wait, CancellationToken ct)
        {
            if (!_first.Task.IsCompleted)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(wait);
                try
                {
                    await _first.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return null;
                }
            }

            lock (_lock) return _responses.Count == 0 ? null : _responses[^1];
        }

        public void Dispose() => _page.Response -= OnResponse;
    }
}
