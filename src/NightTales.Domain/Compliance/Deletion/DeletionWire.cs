namespace NightTales.Domain.Compliance.Deletion;

/// <summary>The snake_case wire names of the deletion enums (table CHECKs, audit data, CLI NDJSON).</summary>
public static class DeletionWire
{
    public static string ToWire(this DeletionScope scope) => scope switch
    {
        DeletionScope.Account => "account",
        DeletionScope.Channel => "channel",
        DeletionScope.Episode => "episode",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
    };

    public static string ToWire(this DeletionReason reason) => reason switch
    {
        DeletionReason.UserRequest => "user_request",
        DeletionReason.InAppRevoke => "in_app_revoke",
        DeletionReason.GoogleRevoke => "google_revoke",
        DeletionReason.AccountDelete => "account_delete",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    public static bool TryParseScope(string? wire, out DeletionScope scope)
    {
        foreach (var candidate in Enum.GetValues<DeletionScope>())
        {
            if (string.Equals(candidate.ToWire(), wire, StringComparison.Ordinal))
            {
                scope = candidate;

                return true;
            }
        }

        scope = default;

        return false;
    }

    public static bool TryParseReason(string? wire, out DeletionReason reason)
    {
        foreach (var candidate in Enum.GetValues<DeletionReason>())
        {
            if (string.Equals(candidate.ToWire(), wire, StringComparison.Ordinal))
            {
                reason = candidate;

                return true;
            }
        }

        reason = default;

        return false;
    }
}
