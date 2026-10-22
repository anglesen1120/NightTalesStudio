namespace NightTales.Domain.Series.Autopilot;

/// <summary>
/// Why an accepted autopilot authorization stopped being the one the series runs on (02.8.3 plan §3.7):
/// stored in <c>series_autopilot_authorizations.revoked_reason</c> as its snake_case wire value, guarded by
/// the table's <c>ck_series_autopilot_authorizations_revoked_reason</c> check.
/// </summary>
/// <remarks>
/// <para>
/// One member per trigger of plan §3.7: <see cref="Disabled"/> when the owner turns autopilot off,
/// <see cref="TextChanged"/> when a covered settings field changes (<see cref="AutopilotAuthorizationCoverage"/>),
/// <see cref="TopicChanged"/> and <see cref="ChannelChanged"/> when the two columns the text names move,
/// <see cref="Superseded"/> when a newer authorization is accepted, and <see cref="OwnerRevoked"/> for the
/// explicit <c>DELETE …/authorization</c>.
/// </para>
/// <para>
/// Every member spells itself as its own snake_case name, so the generic
/// <c>EnumToSnakeCaseStringConverter</c> the persistence layer applies (see
/// <c>HasEnumCheck</c>) stores exactly the wire values below and the check constraint cannot drift from this
/// list.
/// </para>
/// </remarks>
public enum AutopilotAuthorizationRevokeReason
{
    /// <summary>Autopilot was turned off (wire <c>disabled</c>); the archive of a series does the same.</summary>
    Disabled,

    /// <summary>A covered field of the settings document changed (wire <c>text_changed</c>).</summary>
    TextChanged,

    /// <summary>The series topic changed (wire <c>topic_changed</c>).</summary>
    TopicChanged,

    /// <summary>The series' default channel connection changed (wire <c>channel_changed</c>).</summary>
    ChannelChanged,

    /// <summary>A newer authorization was accepted (wire <c>superseded</c>).</summary>
    Superseded,

    /// <summary>The owner revoked the authorization through the API (wire <c>owner_revoked</c>).</summary>
    OwnerRevoked,
}

/// <summary>
/// The closed wire vocabulary of <see cref="AutopilotAuthorizationRevokeReason"/> (02.8.3 plan §3.7, README
/// D-block wire strings): the six values the stored column and its check constraint carry.
/// </summary>
/// <remarks>
/// The Domain cannot use <c>System.Text.Json</c>'s snake_case policy without paying for it on every call, and
/// the persistence layer's converter is internal to Infrastructure, so the mapping is spelled out once here
/// and asserted against the plan's list by <c>SeriesAutopilotAuthorizationTests</c>.
/// </remarks>
public static class AutopilotAuthorizationRevokeReasons
{
    /// <summary>The wire value of <see cref="AutopilotAuthorizationRevokeReason.Disabled"/>.</summary>
    public const string Disabled = "disabled";

    /// <summary>The wire value of <see cref="AutopilotAuthorizationRevokeReason.TextChanged"/>.</summary>
    public const string TextChanged = "text_changed";

    /// <summary>The wire value of <see cref="AutopilotAuthorizationRevokeReason.TopicChanged"/>.</summary>
    public const string TopicChanged = "topic_changed";

    /// <summary>The wire value of <see cref="AutopilotAuthorizationRevokeReason.ChannelChanged"/>.</summary>
    public const string ChannelChanged = "channel_changed";

    /// <summary>The wire value of <see cref="AutopilotAuthorizationRevokeReason.Superseded"/>.</summary>
    public const string Superseded = "superseded";

    /// <summary>The wire value of <see cref="AutopilotAuthorizationRevokeReason.OwnerRevoked"/>.</summary>
    public const string OwnerRevoked = "owner_revoked";

    /// <summary>
    /// Every wire value, in the order the plan lists the triggers: the closed set
    /// <c>ck_series_autopilot_authorizations_revoked_reason</c> allows (the constraint itself lists the same six
    /// values in ordinal order, because <c>EnumCheckConvention</c> sorts them).
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
        [Disabled, TextChanged, TopicChanged, ChannelChanged, Superseded, OwnerRevoked];

    /// <summary>The stored form of one reason.</summary>
    /// <param name="reason">The reason to spell.</param>
    /// <returns>The snake_case wire value the column and its check constraint carry.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is not a declared member.</exception>
    public static string ToWire(this AutopilotAuthorizationRevokeReason reason) => reason switch
    {
        AutopilotAuthorizationRevokeReason.Disabled => Disabled,
        AutopilotAuthorizationRevokeReason.TextChanged => TextChanged,
        AutopilotAuthorizationRevokeReason.TopicChanged => TopicChanged,
        AutopilotAuthorizationRevokeReason.ChannelChanged => ChannelChanged,
        AutopilotAuthorizationRevokeReason.Superseded => Superseded,
        AutopilotAuthorizationRevokeReason.OwnerRevoked => OwnerRevoked,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown authorization revoke reason."),
    };

    /// <summary>Reads one stored wire value.</summary>
    /// <param name="wire">The stored <c>revoked_reason</c> text.</param>
    /// <param name="reason">The reason, when <paramref name="wire"/> names one.</param>
    /// <returns>Whether <paramref name="wire"/> is one of the six values.</returns>
    public static bool TryParse(string? wire, out AutopilotAuthorizationRevokeReason reason)
    {
        switch (wire)
        {
            case Disabled:
                reason = AutopilotAuthorizationRevokeReason.Disabled;

                return true;
            case TextChanged:
                reason = AutopilotAuthorizationRevokeReason.TextChanged;

                return true;
            case TopicChanged:
                reason = AutopilotAuthorizationRevokeReason.TopicChanged;

                return true;
            case ChannelChanged:
                reason = AutopilotAuthorizationRevokeReason.ChannelChanged;

                return true;
            case Superseded:
                reason = AutopilotAuthorizationRevokeReason.Superseded;

                return true;
            case OwnerRevoked:
                reason = AutopilotAuthorizationRevokeReason.OwnerRevoked;

                return true;
            default:
                reason = default;

                return false;
        }
    }
}
