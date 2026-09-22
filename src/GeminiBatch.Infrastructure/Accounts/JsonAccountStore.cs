using System.Text.Json;
using GeminiBatch.Application.Abstractions;
using GeminiBatch.Domain;
using Microsoft.Extensions.Options;

namespace GeminiBatch.Infrastructure.Accounts;

public sealed class AccountStoreOptions
{
    public const string SectionName = "Accounts";

    public string Path { get; set; } = "accounts.json";
}

/// <summary>Loads accounts from a JSON array. Maps through a DTO so the Domain type stays free of serialization concerns.</summary>
public sealed class JsonAccountStore : IAccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;

    public JsonAccountStore(IOptions<AccountStoreOptions> options)
    {
        _path = System.IO.Path.GetFullPath(options.Value.Path);
    }

    public async Task<IReadOnlyList<GeminiAccount>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
            throw new FileNotFoundException($"Accounts file not found. Expected it at: {_path}", _path);

        await using var stream = File.OpenRead(_path);
        var records = await JsonSerializer.DeserializeAsync<List<AccountRecord>>(stream, JsonOptions, ct).ConfigureAwait(false)
                      ?? [];

        return records.Select(r => new GeminiAccount
        {
            Id = r.Id ?? throw new InvalidDataException("Account is missing 'id'."),
            Email = r.Email ?? string.Empty,
            UserDataDir = r.UserDataDir ?? System.IO.Path.Combine("profiles", r.Id),
            Proxy = r.Proxy?.Server is { Length: > 0 } server ? new ProxySettings(server, r.Proxy.Username, r.Proxy.Password) : null,
            Enabled = r.Enabled ?? true,
        }).ToList();
    }

    private sealed class AccountRecord
    {
        public string? Id { get; set; }
        public string? Email { get; set; }
        public string? UserDataDir { get; set; }
        public ProxyRecord? Proxy { get; set; }
        public bool? Enabled { get; set; }
    }

    private sealed class ProxyRecord
    {
        public string? Server { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
    }
}
