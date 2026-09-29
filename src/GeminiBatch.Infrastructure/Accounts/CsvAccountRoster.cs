using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using GeminiBatch.Application.Abstractions;
using GeminiBatch.Domain;
using GeminiBatch.Infrastructure.Gemini;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GeminiBatch.Infrastructure.Accounts;

/// <summary>
/// Reads an account roster from a CSV laid out as <c>email, password, recovery email, 2FA key, proxy</c>
/// (only the email is required). The secrets become the account's in-memory
/// <see cref="AccountCredentials"/> for automated sign-in; they are never written anywhere or logged.
/// The proxy is a URL such as <c>http://user:pass@host:port</c> or <c>socks5://host:port</c>.
/// Each email is mapped to its own profile directory under <see cref="GeminiSessionOptions.ProfilesRoot"/>.
/// </summary>
public sealed class CsvAccountRoster : ICsvAccountRoster
{
    private readonly string _profilesRoot;
    private readonly ILogger<CsvAccountRoster> _logger;

    public CsvAccountRoster(IOptions<GeminiSessionOptions> options, ILogger<CsvAccountRoster> logger)
    {
        _profilesRoot = options.Value.ProfilesRoot;
        _logger = logger;
    }

    public IReadOnlyList<GeminiAccount> Read(string csvPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csvPath);
        if (!File.Exists(csvPath))
            throw new FileNotFoundException($"CSV not found: {csvPath}", csvPath);

        // No header assumption: rows that are not an email (the header, blank lines) are skipped below.
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            MissingFieldFound = null,
            BadDataFound = null,
            IgnoreBlankLines = true,
            TrimOptions = TrimOptions.Trim,
            DetectColumnCountChanges = false,
        };

        var accounts = new List<GeminiAccount>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // FileShare.ReadWrite so a CSV left open in Excel (an exclusive-ish lock) can still be read.
        using var stream = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, config);

        while (csv.Read())
        {
            var email = Field(csv, 0);
            if (email is null || !LooksLikeEmail(email))
                continue; // header row or blank line

            var id = UniqueId(email, usedIds);
            accounts.Add(new GeminiAccount
            {
                Id = id,
                Email = email,
                UserDataDir = Path.Combine(_profilesRoot, id),
                Enabled = true,
                Credentials = ReadCredentials(csv),
                Proxy = ReadProxy(Field(csv, 4), email),
            });
        }

        if (accounts.Count == 0)
            throw new InvalidDataException($"No account emails found in '{csvPath}'. Expected the email in the first column.");

        _logger.LogInformation("Loaded {Count} account(s) from CSV roster {Path} ({WithPassword} with a password, {WithTotp} with a 2FA key, {WithProxy} with a proxy)",
            accounts.Count, csvPath, accounts.Count(a => a.Credentials is not null), accounts.Count(a => a.Credentials?.HasTotp == true),
            accounts.Count(a => a.Proxy is not null));
        return accounts;
    }

    /// <summary>Columns 1–3: password, recovery email, 2FA key. No password means the account can't be signed in automatically.</summary>
    private static AccountCredentials? ReadCredentials(CsvReader csv)
    {
        var password = Field(csv, 1);
        if (password is null) return null;
        return new AccountCredentials(password, Field(csv, 2), Field(csv, 3));
    }

    private static readonly string[] ProxySchemes = ["http", "https", "socks4", "socks5"];

    /// <summary>
    /// Column 4: <c>scheme://[user:pass@]host[:port]</c> → <see cref="ProxySettings"/>. User info is
    /// URL-decoded, so a password containing '@' or ':' is written percent-encoded. A value that doesn't
    /// parse fails the whole load rather than silently running that account without its proxy.
    /// </summary>
    private static ProxySettings? ReadProxy(string? value, string email)
    {
        if (value is null) return null;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !ProxySchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(uri.Host))
        {
            // The value itself may hold a password, so it is not echoed back.
            throw new InvalidDataException(
                $"The proxy for {email} is not a valid proxy URL. Expected e.g. http://user:pass@host:port or socks5://host:port.");
        }

        var server = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        if (string.IsNullOrEmpty(uri.UserInfo))
            return new ProxySettings(server, null, null);

        var parts = uri.UserInfo.Split(':', 2);
        return new ProxySettings(
            server,
            Uri.UnescapeDataString(parts[0]),
            parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null);
    }

    private static string? Field(CsvReader csv, int index) =>
        csv.TryGetField<string>(index, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static bool LooksLikeEmail(string value)
    {
        var at = value.IndexOf('@');
        return at > 0 && at < value.Length - 1 && !value.Contains(' ');
    }

    private static string UniqueId(string email, HashSet<string> used)
    {
        var local = email.Split('@', 2)[0];
        var baseId = Sanitize(local);
        var id = baseId;
        for (var n = 2; !used.Add(id); n++)
            id = $"{baseId}-{n}";
        return id;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim('.', ' ', '_').ToLowerInvariant();
        return cleaned.Length == 0 ? "account" : cleaned;
    }
}
