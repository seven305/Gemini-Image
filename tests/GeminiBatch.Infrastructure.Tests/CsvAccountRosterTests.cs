using GeminiBatch.Infrastructure.Accounts;
using GeminiBatch.Infrastructure.Gemini;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GeminiBatch.Infrastructure.Tests;

public sealed class CsvAccountRosterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "geminibatch-csv-tests", Guid.NewGuid().ToString("N"));

    private CsvAccountRoster NewRoster(string profilesRoot = "profiles")
    {
        var options = Options.Create(new GeminiSessionOptions { ProfilesRoot = profilesRoot });
        return new CsvAccountRoster(options, NullLogger<CsvAccountRoster>.Instance);
    }

    private string WriteCsv(string content)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "accounts.csv");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Reads_emails_skips_header_and_blank_lines_and_ignores_other_columns()
    {
        var path = WriteCsv(
            "Mail,Mail pass,Recovery mail,2FA key\n" +
            "alice@gmail.com,SECRET_PASS,recover@x.xyz,abcd efgh\n" +
            "\n" +
            "bob@gmail.com,OTHER_PASS,r2@x.xyz,ijkl mnop\n");

        var accounts = NewRoster().Read(path);

        Assert.Equal(2, accounts.Count);
        Assert.Equal(["alice@gmail.com", "bob@gmail.com"], accounts.Select(a => a.Email));
        // The account model has no field for a password or 2FA secret, so the ignored columns cannot leak.
        Assert.All(accounts, a => Assert.True(a.Enabled));
    }

    [Fact]
    public void Derives_profile_dir_from_email_local_part_under_profiles_root()
    {
        var path = WriteCsv("alice@gmail.com,p,r,k\n");

        var account = NewRoster("myprofiles").Read(path).Single();

        Assert.Equal("alice", account.Id);
        Assert.Equal(Path.Combine("myprofiles", "alice"), account.UserDataDir);
    }

    [Fact]
    public void Disambiguates_duplicate_local_parts()
    {
        var path = WriteCsv("alice@gmail.com,p,r,k\nalice@outlook.com,p,r,k\n");

        var accounts = NewRoster().Read(path);

        Assert.Equal(["alice", "alice-2"], accounts.Select(a => a.Id));
    }

    [Fact]
    public void Header_only_file_throws()
    {
        var path = WriteCsv("Mail,Mail pass,Recovery mail,2FA key\n");

        Assert.Throws<InvalidDataException>(() => NewRoster().Read(path));
    }

    [Fact]
    public void Missing_file_throws()
    {
        Assert.Throws<FileNotFoundException>(() => NewRoster().Read(Path.Combine(_dir, "nope.csv")));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
