namespace NightTales.Infrastructure.Telemetry;

/// <summary>
/// The user a background unit of work runs for, visible to the log enricher (09.9.3-T1): a job or an outbox
/// delivery has no <c>HttpContext</c>, so <c>ISystemContext.For(userId)</c> publishes the id here for the
/// duration of the work and <see cref="UserHashEnricher"/> hashes it into <c>userId</c> like a request's
/// <c>sub</c>. It flows with the logical call context, so concurrent jobs never see each other's user.
/// </summary>
public static class AmbientActor
{
    private static readonly AsyncLocal<Guid?> Holder = new();

    /// <summary>The user the current logical flow runs for, or <see langword="null"/>.</summary>
    public static Guid? Current => Holder.Value;

    /// <summary>Publishes the user the current flow runs for.</summary>
    /// <param name="userId">The owner of the work.</param>
    internal static void Set(Guid userId) => Holder.Value = userId;

    /// <summary>Clears the user when the work is over.</summary>
    internal static void Clear() => Holder.Value = null;
}
