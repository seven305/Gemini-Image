namespace GeminiBatch.Domain;

/// <summary>Proxy for a browser context. <paramref name="Server"/> is e.g. "http://host:port" or "socks5://host:port".</summary>
public sealed record ProxySettings(string Server, string? Username, string? Password);
