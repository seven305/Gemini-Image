using Microsoft.Playwright;

namespace GeminiBatch.Infrastructure.Gemini;

/// <summary>
/// The ONLY place that knows what the Gemini web UI looks like: URLs, accessible names, custom-element
/// tags and the CDN URL shape. Every value here comes from SPIKE_FINDINGS.md (live ARIA snapshots, not
/// guesses). When Gemini ships a UI change, this is the file to fix — nothing else touches the DOM.
///
/// Locators are role+name because they were the only handles that survived across snapshots; Angular
/// <c>_ngcontent</c> hashes change every deploy and must never be used. All names are English — an
/// account whose Google language is not English breaks every locator (spike finding #3).
/// </summary>
public static class GeminiSelectors
{
    // ---- URLs -------------------------------------------------------------------------------

    /// <summary>Opening this URL always starts a fresh conversation.</summary>
    public const string AppUrl = "https://gemini.google.com/app";
    public const string Origin = "https://gemini.google.com";

    /// <summary>Any redirect here means the profile is signed out or Google wants re-verification.</summary>
    public const string GoogleAccountsHost = "accounts.google.com";

    /// <summary>Google session cookies; at least one must be present on the Gemini origin when signed in.</summary>
    public static readonly string[] SessionCookieNames = ["SID", "__Secure-1PSID", "__Secure-3PSID"];

    /// <summary>Marker of the CDN URL Gemini fetches for a generated image (<c>lh3.googleusercontent.com/rd-gg-dl/{id}=s1024-rj?alr=yes</c>).</summary>
    public const string GeneratedImageCdnMarker = "googleusercontent.com/rd-gg-dl/";

    /// <summary>Google image-serving size suffix that returns the original (1408×768) instead of the 1024 preview.</summary>
    public const string CdnFullSizeSuffix = "=s0";

    // ---- Mode / model -------------------------------------------------------------------------

    /// <summary>
    /// Image generation is a *tool* toggled from the "Upload &amp; tools" menu, not a model. The mode picker
    /// stays on its default ("3.6 Flash"); the UI never names the underlying image model.
    /// </summary>
    public const string ImageToolName = "Create image";

    // ---- Page state ---------------------------------------------------------------------------

    /// <summary>Whole-page root for diagnostic ARIA snapshots — the same view the selectors were derived from.</summary>
    public static ILocator Root(IPage page) => page.Locator("body");

    /// <summary>Quill editor. Same accessible name whether or not the image tool is on (only the placeholder changes).</summary>
    public static ILocator PromptBox(IPage page) =>
        page.GetByRole(AriaRole.Textbox, new() { Name = "Enter a prompt for Gemini" });

    /// <summary>Signed-out Gemini still renders the prompt box, so this button is the real signed-out signal.</summary>
    public static ILocator SignInButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true });

    /// <summary>"Keep in mind" first-visit notice. Appears once per fresh profile; click if present.</summary>
    public static ILocator DismissNoticeButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Got it", Exact = true });

    // ---- Image tool ---------------------------------------------------------------------------

    /// <summary>The "+" button left of the prompt; opens the "Menu options" menu.</summary>
    public static ILocator ToolsMenuButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Upload & tools" });

    public static ILocator CreateImageMenuItem(IPage page) =>
        page.GetByRole(AriaRole.Menuitemcheckbox, new() { Name = ImageToolName });

    /// <summary>Chip shown under the prompt once the image tool is on (text "Images"). Proves the toggle took.</summary>
    public static ILocator ImageModeChip(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Deselect Images" });

    // ---- Submit / streaming -------------------------------------------------------------------

    /// <summary>Only exists once the textbox is non-empty.</summary>
    public static ILocator SendButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Send message" });

    /// <summary>Replaces the send button while the response streams. Its disappearance is "turn complete".</summary>
    public static ILocator StopResponseButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Stop response" });

    // ---- Result -------------------------------------------------------------------------------

    /// <summary>The most recent model turn. With one prompt per conversation this is the only one.</summary>
    public static ILocator LatestResponse(IPage page) =>
        page.Locator("model-response").Last;

    /// <summary>
    /// <c>&lt;generated-image&gt; &lt;img class="image animate loaded" src="blob:…"&gt;</c>. The <c>loaded</c>
    /// class is added once the preview is decoded; it can appear while "Stop response" is still shown.
    /// </summary>
    public static ILocator LatestGeneratedImage(IPage page) =>
        LatestResponse(page).Locator("generated-image img.loaded").First;

    /// <summary><c>gem-icon-button[data-test-id="download-generated-image-button"]</c>. Triggers a real browser download.</summary>
    public static ILocator DownloadFullSizeButton(IPage page) =>
        page.GetByRole(AriaRole.Button, new() { Name = "Download full size image" }).First;

    // ---- Pure helpers -------------------------------------------------------------------------

    public static bool IsLoginRedirect(string url) =>
        url.Contains(GoogleAccountsHost, StringComparison.OrdinalIgnoreCase);

    public static bool IsSessionCookie(string cookieName) =>
        Array.IndexOf(SessionCookieNames, cookieName) >= 0;

    /// <summary>True for the CDN response Gemini itself fetches to display a freshly generated image.</summary>
    public static bool IsGeneratedImageResponse(string url, string? contentType) =>
        url.Contains(GeneratedImageCdnMarker, StringComparison.OrdinalIgnoreCase)
        && contentType is not null
        && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>…/rd-gg-dl/{id}=s1024-rj?alr=yes</c> → <c>…/rd-gg-dl/{id}=s0</c>. Requesting that through the
    /// authenticated context returns the full 1408×768 JPEG (spike run #13). This relies on Google's
    /// image-serving URL convention, not a Gemini contract; if the shape changes, callers fall back to
    /// the captured preview body.
    /// </summary>
    public static string ToFullSizeCdnUrl(string previewUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previewUrl);
        var noQuery = previewUrl.Split('?', 2)[0];
        var sizeSuffix = noQuery.LastIndexOf('=');
        var idStart = noQuery.LastIndexOf('/');
        // Only strip a size token that sits inside the last path segment.
        var basePart = sizeSuffix > idStart ? noQuery[..sizeSuffix] : noQuery;
        return basePart + CdnFullSizeSuffix;
    }
}
