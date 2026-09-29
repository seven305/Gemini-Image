using GeminiBatch.Domain;
using Microsoft.Extensions.Logging;

namespace GeminiBatch.Application;

/// <summary>Which roster accounts actually get a turn in a run. Shared by the planner and the batch processor so they agree.</summary>
internal static class AccountEligibility
{
    /// <summary>
    /// Enabled, non-quarantined accounts in roster order, one per Google account and per profile directory: a profile
    /// can only be open in one browser, and two browsers on one account would look like exactly the traffic we avoid.
    /// </summary>
    public static List<GeminiAccount> Select(IReadOnlyList<GeminiAccount> accounts, ILogger? logger = null)
    {
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var eligible = new List<GeminiAccount>();

        foreach (var account in accounts.Where(a => a.Enabled && !a.IsQuarantined))
        {
            if (!emails.Add(account.Email.Trim()) || !profiles.Add(Path.GetFullPath(account.UserDataDir)))
            {
                logger?.LogWarning("Account {AccountId} not used: it shares an email or profile directory with an earlier account", account.Id);
                continue;
            }
            eligible.Add(account);
        }
        return eligible;
    }
}
