using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NightTales.Infrastructure.Persistence.Migrations;

namespace NightTales.Api.IntegrationTests.Migrations;

/// <summary>
/// The destructive-operation scan behind <c>Migrations_ContainNoDestructiveOperations</c>: read every
/// compiled migration's <c>Up</c> operations, and report the ones that drop or narrow something unless
/// the migration class carries <see cref="ContractMigrationAttribute"/> (NFR-09-6: expand → migrate →
/// contract).
/// </summary>
/// <remarks>
/// <para>
/// It reads the operation tree, not the generated SQL: no database and no <c>dotnet ef</c> invocation is
/// needed, so the check runs in the unit job as well as in the migration job.
/// </para>
/// <para>
/// Detected: <c>DropTable</c>, <c>DropColumn</c>, and an <c>AlterColumn</c> that becomes <c>NOT NULL</c>,
/// shortens a maximum length, bounds a string that had none, shrinks numeric precision or scale, or
/// changes the CLR type. Not detected: a hand-written <c>AlterColumn</c> that did not record its previous
/// shape (<c>OldColumn</c> is null), an added <c>NOT NULL</c> column without a default, and
/// index/constraint drops — the first is covered by the review checklist, the other two free no data.
/// </para>
/// </remarks>
internal static class MigrationGuard
{
    /// <summary>Scans every concrete migration type of <paramref name="assembly"/>.</summary>
    internal static IReadOnlyList<string> ScanAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var migrations = assembly.GetTypes()
            .Where(type => !type.IsAbstract && type.IsSubclassOf(typeof(Migration)))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .Select(type => (Migration)Activator.CreateInstance(type, nonPublic: true)!)
            .ToArray();

        return FindViolations(migrations);
    }

    /// <summary>
    /// Returns one line per destructive operation that is not excused by a
    /// <see cref="ContractMigrationAttribute"/> on its migration. An empty result is the pass.
    /// </summary>
    internal static IReadOnlyList<string> FindViolations(IEnumerable<Migration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);

        var violations = new List<string>();

        foreach (var migration in migrations)
        {
            var destructive = DestructiveOperations(migration.UpOperations);
            if (destructive.Count == 0)
            {
                continue;
            }

            var contract = migration.GetType().GetCustomAttribute<ContractMigrationAttribute>();
            if (contract is not null)
            {
                continue;
            }

            violations.AddRange(destructive.Select(operation =>
                $"{migration.GetType().Name} {operation}: expand-only migrations (NFR-09-6) drop or narrow "
                + "only in a contract step — add [ContractMigration(\"why the previous release is over\")] "
                + "to that migration if this is one"));
        }

        return violations;
    }

    /// <summary>The destructive operations of one <c>Up</c> body, each described in a few words.</summary>
    internal static IReadOnlyList<string> DestructiveOperations(IReadOnlyList<MigrationOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var findings = new List<string>();

        foreach (var operation in operations)
        {
            switch (operation)
            {
                case DropTableOperation drop:
                    findings.Add($"drops table {drop.Name}");
                    break;

                case DropColumnOperation drop:
                    findings.Add($"drops column {drop.Table}.{drop.Name}");
                    break;

                case AlterColumnOperation alter when Narrows(alter, out var reason):
                    findings.Add($"narrows column {alter.Table}.{alter.Name} ({reason})");
                    break;

                default:
                    break;
            }
        }

        return findings;
    }

    private static bool Narrows(AlterColumnOperation alter, [NotNullWhen(true)] out string? reason)
    {
        reason = null;
        var before = alter.OldColumn;

        // EF records the previous shape of a generated AlterColumn. A hand-written one without it cannot
        // be judged here — the checklist's "do not narrow by hand" line covers that case.
        if (before is null)
        {
            return false;
        }

        if (before.IsNullable && !alter.IsNullable)
        {
            reason = "nullable -> NOT NULL";
            return true;
        }

        var beforeLength = before.MaxLength;
        var afterLength = alter.MaxLength;

        if (beforeLength is { } was && afterLength is { } now && now < was)
        {
            reason = $"max length {was} -> {now}";
            return true;
        }

        // Bounding a string that had no maximum length (`text` -> `varchar(120)`) is the same narrowing
        // with nothing to shorten: a bounded column cannot hold every string the unbounded one held, so
        // rows that fit schema N-1 can fail the migration. EF writes no MaxLength for the unbounded side,
        // which makes a null `before.MaxLength` the only signal there is.
        if (beforeLength is null && afterLength is { } capped)
        {
            reason = $"max length unbounded -> {capped}";
            return true;
        }

        if (before.Precision is { } wasPrecision && alter.Precision is { } nowPrecision && nowPrecision < wasPrecision)
        {
            reason = $"precision {wasPrecision} -> {nowPrecision}";
            return true;
        }

        if (before.Scale is { } wasScale && alter.Scale is { } nowScale && nowScale < wasScale)
        {
            reason = $"scale {wasScale} -> {nowScale}";
            return true;
        }

        if (before.ClrType != alter.ClrType)
        {
            reason = $"type {before.ClrType.Name} -> {alter.ClrType.Name}";
            return true;
        }

        return false;
    }
}
