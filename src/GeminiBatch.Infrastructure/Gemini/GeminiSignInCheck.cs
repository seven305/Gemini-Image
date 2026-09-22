using Microsoft.Playwright;

namespace GeminiBatch.Infrastructure.Gemini;

/// <summary>
/// Signed-in = on the Gemini origin, a Google session cookie present, and no "Sign in" button.
/// Signed-out Gemini still renders the prompt box, so the box alone proves nothing (spike).
/// </summary>
internal static class GeminiSignInCheck
{
    public static async Task<SignInState> ProbeAsync(IBrowserContext context, IPage page)
    {
        var url = page.Url;
        if (GeminiSelectors.IsLoginRedirect(url))
            return new SignInState(false, false, false, url);
        if (!url.StartsWith(GeminiSelectors.Origin, StringComparison.OrdinalIgnoreCase))
            return new SignInState(false, false, false, url);

        var cookies = await context.CookiesAsync([GeminiSelectors.Origin]).ConfigureAwait(false);
        var hasSessionCookie = cookies.Any(c => GeminiSelectors.IsSessionCookie(c.Name));
        var signInButtonVisible = await GeminiSelectors.SignInButton(page).CountAsync().ConfigureAwait(false) > 0;

        return new SignInState(hasSessionCookie && !signInButtonVisible, hasSessionCookie, signInButtonVisible, url);
    }

    public sealed record SignInState(bool IsSignedIn, bool HasSessionCookie, bool SignInButtonVisible, string Url);
}
