namespace GeminiBatch.Infrastructure.Gemini;

public enum GeminiDownloadMode
{
    /// <summary>
    /// Capture the CDN response Gemini fetches for the image and re-request it at full size through the
    /// authenticated context. 100% crash-free in the spike; full 1408×768 but CDN-re-encoded (~190 KB).
    /// </summary>
    Net,

    /// <summary>
    /// Click "Download full size image" and use Playwright's Download event. Bit-exact original (~940 KB)
    /// but crashed Chrome/Edge 153 ~60% of the time with Playwright 1.62 (see SPIKE_FINDINGS.md).
    /// </summary>
    Ui,
}

/// <summary>Browser/session knobs for the real Playwright session. Bound from the "Gemini" section.</summary>
public sealed class GeminiSessionOptions
{
    public const string SectionName = "Gemini";

    /// <summary>
    /// Playwright browser channel: "chrome", "msedge", or null/empty for the bundled Chromium.
    /// Bundled Chromium could not be spawned on the dev box (spike), so the default is the installed Chrome.
    /// </summary>
    public string? Channel { get; set; } = "chrome";

    public GeminiDownloadMode DownloadMode { get; set; } = GeminiDownloadMode.Net;

    /// <summary>Screenshots + ARIA snapshots on failure land here, per account.</summary>
    public string DiagnosticsFolder { get; set; } = "diagnostics";

    /// <summary>Page load until the prompt box is visible. Gemini never goes network-idle; don't wait for it.</summary>
    public int NavigationTimeoutSeconds { get; set; } = 60;

    /// <summary>Default timeout for clicks/fills/menu waits.</summary>
    public int ActionTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// After "Stop response" disappears, how long to keep waiting for the image to finish decoding. Keeps a
    /// refused prompt (text answer, no image) from burning the whole generation timeout on every retry.
    /// </summary>
    public int PostResponseImageWaitSeconds { get; set; } = 20;

    /// <summary>After the turn completes, how long to wait for the CDN image response if it has not arrived yet (Net mode).</summary>
    public int CdnCaptureWaitSeconds { get; set; } = 15;

    /// <summary>Initial browser window size as "width,height". Null keeps Chrome's remembered size.</summary>
    public string? WindowSize { get; set; } = "1280,900";

    /// <summary>Base folder under which each CSV-rostered account gets its own persistent profile directory.</summary>
    public string ProfilesRoot { get; set; } = "profiles";
}
