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
/// Reads an account roster from a CSV whose first column is the account email. Every other column
/// (password, recovery mail, 2FA secret, …) is intentionally ignored and never stored — the app does
/// not automate sign-in, so it has no use for secrets. Each email is mapped to its own profile
/// directory under <see cref="GeminiSessionOptions.ProfilesRoot"/>.
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
            // Read ONLY column 0. Secrets in later columns are never pulled into memory here.
            var email = csv.GetField(0)?.Trim();
            if (string.IsNullOrWhiteSpace(email) || !LooksLikeEmail(email))
                continue; // header row or blank line

            var id = UniqueId(email, usedIds);
            accounts.Add(new GeminiAccount
            {
                Id = id,
                Email = email,
                UserDataDir = Path.Combine(_profilesRoot, id),
                Enabled = true,
            });
        }

        if (accounts.Count == 0)
            throw new InvalidDataException($"No account emails found in '{csvPath}'. Expected the email in the first column.");

        _logger.LogInformation("Loaded {Count} account(s) from CSV roster {Path}", accounts.Count, csvPath);
        return accounts;
    }

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
