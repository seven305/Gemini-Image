using GeminiBatch.Infrastructure.Accounts;

namespace GeminiBatch.Infrastructure.Tests;

public sealed class TotpGeneratorTests
{
    // RFC 6238 appendix B SHA-1 seed "12345678901234567890" in base32.
    private const string RfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(20000000000L, "65353130")]
    public void Matches_rfc6238_test_vectors(long unixSeconds, string expected)
    {
        var (code, _) = TotpGenerator.Compute(RfcSecret, DateTimeOffset.FromUnixTimeSeconds(unixSeconds), digits: 8);

        Assert.Equal(expected, code);
    }

    [Fact]
    public void Six_digit_code_is_the_low_digits_and_keeps_leading_zeros()
    {
        var (code, timeStep) = TotpGenerator.Compute(RfcSecret, DateTimeOffset.FromUnixTimeSeconds(1111111109));

        Assert.Equal("081804", code);
        Assert.Equal(1111111109L / 30, timeStep);
    }

    [Fact]
    public void Accepts_google_style_key_with_spaces_and_lowercase()
    {
        var spaced = "gezd gnbv gy3t qojq gezd gnbv gy3t qojq";
        var now = DateTimeOffset.FromUnixTimeSeconds(59);

        Assert.Equal(TotpGenerator.Compute(RfcSecret, now).Code, TotpGenerator.Compute(spaced, now).Code);
    }

    [Fact]
    public void Invalid_key_throws_format_exception()
    {
        Assert.Throws<FormatException>(() => TotpGenerator.DecodeBase32("not base32!"));
    }

    [Fact]
    public void Remaining_in_step_counts_down_to_the_next_window()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), TotpGenerator.RemainingInStep(DateTimeOffset.FromUnixTimeSeconds(60)));
        Assert.Equal(TimeSpan.FromSeconds(1), TotpGenerator.RemainingInStep(DateTimeOffset.FromUnixTimeSeconds(89)));
    }
}
