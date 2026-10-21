using System.Reflection;

namespace NightTales.TestKit.Builders;

/// <summary>
/// Materializes a domain shell for a builder. The shell's factory arrives with its behaviour owner
/// (09.4.1-T1 for <c>Series</c>, 09.5.1-T1 for <c>Episode</c>), so until then a builder constructs the
/// instance through the private EF constructor and fills every column through the change tracker — the
/// same tracked path production uses, including <c>AuditableSaveChangesInterceptor</c>.
/// </summary>
internal static class ShellInstance
{
    public static T Create<T>()
        where T : class
        => (T)Activator.CreateInstance(typeof(T), nonPublic: true)!;

    /// <summary>Writes one mapped property of <paramref name="entity"/> (private setters included).</summary>
    public static void Set<T>(Microsoft.EntityFrameworkCore.DbContext context, T entity, string propertyName, object? value)
        where T : class
        => context.Entry(entity).Property(propertyName).CurrentValue = value;
}
