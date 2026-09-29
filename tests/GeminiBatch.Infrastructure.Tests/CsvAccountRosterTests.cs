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
    public void Reads_emails_and_credentials_skipping_header_and_blank_lines()
    {
        var path = WriteCsv(
            "Mail,Mail pass,Recovery mail,2FA key\n" +
            "alice@gmail.com,SECRET_PASS,recover@x.xyz,abcd efgh\n" +
            "\n" +
            "bob@gmail.com,OTHER_PASS,r2@x.xyz,ijkl mnop\n");

        var accounts = NewRoster().Read(path);

        Assert.Equal(2, accounts.Count);
        Assert.Equal(["alice@gmail.com", "bob@gmail.com"], accounts.Select(a => a.Email));
        Assert.All(accounts, a => Assert.True(a.Enabled));
        Assert.Equal(new Domain.AccountCredentials("SECRET_PASS", "recover@x.xyz", "abcd efgh"), accounts[0].Credentials);
        Assert.Equal("OTHER_PASS", accounts[1].Credentials!.Password);
    }

    [Fact]
    public void Missing_optional_columns_are_allowed_and_no_password_means_manual_sign_in()
    {
        var path = WriteCsv("alice@gmail.com,pw\nbob@gmail.com\ncarol@gmail.com,pw,,KEY\n");

        var accounts = NewRoster().Read(path);

        Assert.Equal(new Domain.AccountCredentials("pw", null, null), accounts[0].Credentials);
        Assert.Null(accounts[1].Credentials);
        Assert.Equal(new Domain.AccountCredentials("pw", null, "KEY"), accounts[2].Credentials);
    }

    [Fact]
    public void Credentials_never_appear_in_ToString()
    {
        var path = WriteCsv("alice@gmail.com,SECRET_PASS,recover@x.xyz,TOTPKEY\n");

        var account = NewRoster().Read(path).Single();

        var text = account.Credentials!.ToString() + account;
        Assert.DoesNotContain("SECRET_PASS", text);
        Assert.DoesNotContain("recover@x.xyz", text);
        Assert.DoesNotContain("TOTPKEY", text);
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
    public void Proxy_column_parses_server_and_decoded_credentials()
    {
        var path = WriteCsv(
            "alice@gmail.com,pw,,,http://user:p%40ss@host:8080\n" +
            "bob@gmail.com,pw,,,socks5://10.0.0.1:1080\n" +
            "carol@gmail.com,pw,,,\n");

        var accounts = NewRoster().Read(path);

        Assert.Equal(new Domain.ProxySettings("http://host:8080", "user", "p@ss"), accounts[0].Proxy);
        Assert.Equal(new Domain.ProxySettings("socks5://10.0.0.1:1080", null, null), accounts[1].Proxy);
        Assert.Null(accounts[2].Proxy);
        Assert.DoesNotContain("p@ss", accounts[0].ToString());
    }

    [Fact]
    public void Malformed_proxy_fails_the_load_without_echoing_it()
    {
        var path = WriteCsv("alice@gmail.com,pw,,,ftp://user:SECRET@host\n");

        var ex = Assert.Throws<InvalidDataException>(() => NewRoster().Read(path));

        Assert.Contains("alice@gmail.com", ex.Message);
        Assert.DoesNotContain("SECRET", ex.Message);
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
