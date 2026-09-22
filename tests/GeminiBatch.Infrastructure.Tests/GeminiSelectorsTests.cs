using GeminiBatch.Infrastructure.Gemini;

namespace GeminiBatch.Infrastructure.Tests;

/// <summary>
/// Covers the non-DOM parts of <see cref="GeminiSelectors"/> — the CDN URL rewrite in particular, since a
/// silent change there is the difference between a full-size image and a preview (or nothing at all).
/// The locators themselves can only be verified against the live site.
/// </summary>
public sealed class GeminiSelectorsTests
{
    private const string PreviewUrl = "https://lh3.googleusercontent.com/rd-gg-dl/AAQ_wbGoDkcZzg-abc123=s1024-rj?alr=yes";

    [Fact]
    public void ToFullSizeCdnUrl_replaces_size_token_and_drops_query()
    {
        Assert.Equal(
            "https://lh3.googleusercontent.com/rd-gg-dl/AAQ_wbGoDkcZzg-abc123=s0",
            GeminiSelectors.ToFullSizeCdnUrl(PreviewUrl));
    }

    [Fact]
    public void ToFullSizeCdnUrl_appends_suffix_when_url_has_no_size_token()
    {
        Assert.Equal(
            "https://lh3.googleusercontent.com/rd-gg-dl/AAQ_wbGoDkcZzg=s0",
            GeminiSelectors.ToFullSizeCdnUrl("https://lh3.googleusercontent.com/rd-gg-dl/AAQ_wbGoDkcZzg"));
    }

    [Fact]
    public void ToFullSizeCdnUrl_ignores_an_equals_sign_that_is_not_in_the_last_segment()
    {
        Assert.Equal(
            "https://lh3.googleusercontent.com/a=b/rd-gg-dl/xyz=s0",
            GeminiSelectors.ToFullSizeCdnUrl("https://lh3.googleusercontent.com/a=b/rd-gg-dl/xyz"));
    }

    [Theory]
    [InlineData("https://lh3.googleusercontent.com/rd-gg-dl/abc=s1024-rj?alr=yes", "image/jpeg", true)]
    [InlineData("https://lh3.googleusercontent.com/rd-gg-dl/abc=s1024-rj", "text/html", false)]
    [InlineData("https://www.gstatic.com/bard-robin-zs/image_create_color.svg", "image/svg+xml", false)]
    [InlineData("blob:https://gemini.google.com/a08e90eb", "image/jpeg", false)]
    public void IsGeneratedImageResponse_matches_only_the_generated_image_cdn(string url, string contentType, bool expected)
    {
        Assert.Equal(expected, GeminiSelectors.IsGeneratedImageResponse(url, contentType));
    }

    [Fact]
    public void IsGeneratedImageResponse_is_false_without_a_content_type()
    {
        Assert.False(GeminiSelectors.IsGeneratedImageResponse(PreviewUrl, null));
    }

    [Theory]
    [InlineData("SID", true)]
    [InlineData("__Secure-1PSID", true)]
    [InlineData("__Secure-3PSID", true)]
    [InlineData("NID", false)]
    public void IsSessionCookie_recognises_the_google_session_cookies(string name, bool expected)
    {
        Assert.Equal(expected, GeminiSelectors.IsSessionCookie(name));
    }

    [Theory]
    [InlineData("https://accounts.google.com/v3/signin/identifier", true)]
    [InlineData("https://gemini.google.com/app", false)]
    public void IsLoginRedirect_detects_the_google_sign_in_host(string url, bool expected)
    {
        Assert.Equal(expected, GeminiSelectors.IsLoginRedirect(url));
    }
}
