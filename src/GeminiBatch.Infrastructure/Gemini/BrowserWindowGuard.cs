using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace GeminiBatch.Infrastructure.Gemini;

/// <summary>
/// Chrome treats a minimized window as hidden: no paints, no animation frames, so Playwright's visible/stable waits
/// and Gemini's image decode stall until they time out. Covered windows are fine (Playwright launches with
/// --disable-backgrounding-occluded-windows); only the minimized state needs undoing. Browser-level, not Gemini DOM.
/// </summary>
internal static class BrowserWindowGuard
{
    /// <summary>Restores the page's window if it is minimized. Never throws: worst case the step times out as before.</summary>
    public static async Task RestoreIfMinimizedAsync(ICDPSession cdp, ILogger logger, string accountId)
    {
        try
        {
            var window = await cdp.SendAsync("Browser.getWindowForTarget").ConfigureAwait(false);
            if (window is not { } w) return;

            var state = w.GetProperty("bounds").GetProperty("windowState").GetString();
            if (state != "minimized") return;

            await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = w.GetProperty("windowId").GetInt32(),
                ["bounds"] = new Dictionary<string, object> { ["windowState"] = "normal" },
            }).ConfigureAwait(false);
            logger.LogWarning("Browser window for {AccountId} was minimized; restored it so the page keeps rendering", accountId);
        }
        catch (Exception ex) when (ex is PlaywrightException or KeyNotFoundException or InvalidOperationException or JsonException)
        {
            logger.LogDebug(ex, "Could not check the window state for {AccountId}", accountId);
        }
    }
}
