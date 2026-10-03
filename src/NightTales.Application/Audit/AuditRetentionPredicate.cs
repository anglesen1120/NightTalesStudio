using NightTales.Application.Features.Platform.Retention;

namespace NightTales.Application.Audit;

/// <summary>
/// Decides which audit rows have outlived their retention (09.7.3-T1): the first rule of
/// <see cref="AuditRetentionRules.Ordered"/> that matches a row owns it, and a rule whose
/// <see cref="AuditRetentionRule.Retain"/> is <see langword="null"/> keeps the row forever. The windows live
/// in the rule list only; nothing here restates a number.
/// </summary>
public static class AuditRetentionPredicate
{
    /// <summary>The ids of the candidates whose owning rule says they are older than its window.</summary>
    /// <param name="candidates">The page the store returned.</param>
    /// <param name="ordered">The rules, most specific first.</param>
    /// <param name="now">The sweep's clock reading.</param>
    public static IReadOnlyList<Guid> Expired(
        IReadOnlyList<AuditRetentionCandidate> candidates,
        IReadOnlyList<AuditRetentionRule> ordered,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(ordered);

        var expired = new List<Guid>();
        foreach (var candidate in candidates)
        {
            var rule = FirstMatch(ordered, candidate.Row);
            if (rule is { Retain: { } retain } && candidate.Row.CreatedAt < now - retain)
            {
                expired.Add(candidate.Id);
            }
        }

        return expired;
    }

    private static AuditRetentionRule? FirstMatch(IReadOnlyList<AuditRetentionRule> ordered, AuditRetentionRow row)
    {
        foreach (var rule in ordered)
        {
            if (rule.Matches(row))
            {
                return rule;
            }
        }

        return null;
    }
}
