using Shouldly;
using Xunit;

namespace NightTales.Architecture.Tests;

/// <summary>
/// 13.3.1-T1 rules for the deletion engine: the tenant-filter bypass lives in <c>DataDeletionJob</c> alone (K12,
/// 09.1.2-T1 allow-list), and the engine neither sends mail nor writes <c>audit_logs</c> around <c>IAuditWriter</c> (BD15).
/// </summary>
public sealed class DeletionEngineRules
{
    private const string JobType = "NightTales.Application.Features.Compliance.Deletion.Jobs.DataDeletionJob";

    private static readonly string[] EngineNamespaces =
    [
        "NightTales.Application.Features.Compliance.Deletion",
        "NightTales.Infrastructure.Compliance.Deletion",
    ];

    [Fact]
    public void DataDeletionJob_IsTheOnlyIgnoreQueryFiltersSiteInThisFeature()
    {
        // Non-vacuity: the job really calls it, so a scanner regression cannot make the rule pass on nothing.
        CallSiteRules.FindCallsInType(JobType).ShouldContain(site => site.CalledMember == "IgnoreQueryFilters");

        var violations = EngineTypes()
            .Where(type => FullName(type) != JobType)
            .SelectMany(type => CallSiteRules.FindCallsInType(FullName(type)).Select(site => (type, site)))
            .Where(pair => pair.site.CalledMember == "IgnoreQueryFilters")
            .Select(pair => $"{FullName(pair.type)}.{pair.site.Method}")
            .ToList();

        violations.ShouldBeEmpty($"{nameof(DataDeletionJob_IsTheOnlyIgnoreQueryFiltersSiteInThisFeature)}: {string.Join("; ", violations)}");
        AllowLists.IgnoreQueryFiltersTypeNames.ShouldContain(JobType, "the allow-list entry stays unchanged");
    }

    [Fact]
    public void DeletionEngine_WritesAuditThroughIAuditWriter_AndSendsNoMail()
    {
        var typeNames = EngineTypes().Select(FullName).Distinct().ToList();
        typeNames.ShouldNotBeEmpty();

        var calls = typeNames.SelectMany(CallSiteRules.FindCallsInType).ToList();

        calls.ShouldContain(
            site => site.DeclaringType.EndsWith("IAuditWriter", StringComparison.Ordinal) && site.CalledMember == "WriteAsync",
            "the completion audit row goes through IAuditWriter");

        var violations = calls
            .Where(site => site.DeclaringType.Contains("Smtp", StringComparison.OrdinalIgnoreCase)
                || site.DeclaringType.Contains("MailKit", StringComparison.OrdinalIgnoreCase)
                || site.DeclaringType.EndsWith("IEmailSender", StringComparison.Ordinal)
                || site.CalledMember is "get_AuditLogs" or "get_EmailMessages")
            .Select(site => $"{site.OutermostType}.{site.Method} -> {site.DeclaringType}.{site.CalledMember}")
            .Distinct()
            .ToList();

        violations.ShouldBeEmpty($"{nameof(DeletionEngine_WritesAuditThroughIAuditWriter_AndSendsNoMail)}: {string.Join("; ", violations)}");
    }

    private static IEnumerable<Type> EngineTypes() =>
        ArchitectureModel.SourceAssemblies
            .SelectMany(assembly =>
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (System.Reflection.ReflectionTypeLoadException exception)
                {
                    return exception.Types.OfType<Type>();
                }
            })
            .Where(type => !type.IsNested
                && type.Namespace is { } ns
                && EngineNamespaces.Any(prefix => ns == prefix || ns.StartsWith(prefix + ".", StringComparison.Ordinal)));

    private static string FullName(Type type) => $"{type.Namespace}.{type.Name}";
}
