namespace NightTales.Domain.Publishing;

/// <summary>
/// Who consented to a publication (06 README D7/D8, YR9): enforced by <c>ck_publications_consent_source</c> and
/// templated per source (the consent, veto-notice and consented-confirmation e-mails say who acted), so the C#
/// member, the column and the template picker cannot drift.
/// </summary>
/// <remarks>
/// <see cref="Publication.ConsentSource"/> stores the <em>wire string</em> of this enum
/// (<see cref="ConsentSourceExtensions.ToWire"/>) rather than the enum itself, because the aggregate's
/// <c>consent_source</c> column is text and, unlike <see cref="PublicationStatus"/>, is not mapped through
/// <c>HasEnumCheck</c>.
/// </remarks>
public enum ConsentSource
{
    /// <summary>The owner consented on the consent route (06 README D7).</summary>
    User,

    /// <summary>
    /// The autopilot consent job consented because the veto window closed without a veto (06 README D8,
    /// 06.2.2-T3).
    /// </summary>
    Autopilot,
}

/// <summary>
/// The wire spelling of <see cref="ConsentSource"/> (06 README D7/D8): the value
/// <see cref="Publication.ConsentSource"/> stores, <c>ck_publications_consent_source</c> checks and the
/// template picker keys on, so the three can never drift apart.
/// </summary>
public static class ConsentSourceExtensions
{
    /// <summary>The stored value of <see cref="ConsentSource.User"/>.</summary>
    private const string UserWire = "user";

    /// <summary>The stored value of <see cref="ConsentSource.Autopilot"/>.</summary>
    private const string AutopilotWire = "autopilot";

    /// <summary>The wire value of <paramref name="source"/>.</summary>
    /// <param name="source">A consent source.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a declared consent source.</exception>
    public static string ToWire(this ConsentSource source) => source switch
    {
        ConsentSource.User => UserWire,
        ConsentSource.Autopilot => AutopilotWire,
        _ => throw new ArgumentOutOfRangeException(
            nameof(source),
            source,
            "No wire value is declared for this consent source."),
    };
}
