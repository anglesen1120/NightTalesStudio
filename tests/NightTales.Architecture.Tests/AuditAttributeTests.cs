using System.Reflection;
using System.Text.RegularExpressions;
using NightTales.Application.Abstractions.Audit;
using Shouldly;
using Xunit;

namespace NightTales.Architecture.Tests;

/// <summary>
/// The audit contract of 09.3.6-T1 (09 README BD15, plan §3): every <c>ICommand</c> in
/// <c>NightTales.Application</c> carries exactly one of <see cref="AuditedAttribute"/> /
/// <see cref="NotAuditedAttribute"/>; an exemption reason is non-empty and short; an audited action
/// matches the frozen <c>domain.verb</c> pattern and is unique; an audited command projects an
/// <c>IAuditData</c>; and no audit attribute sits on a non-command — the rule scans every type of the
/// Application assembly, queries included, because slot 6 is commands-only (D4).
/// </summary>
/// <remarks>
/// The rules are proven to fire by <see cref="AuditContractProbes"/>: each probe breaks exactly one rule
/// and lives in this test assembly, so a rule that silently stopped matching fails here instead of
/// passing on an empty set. The test names are the ones the task file's Tests table fixes.
/// </remarks>
public sealed partial class AuditAttributeTests
{
    /// <summary>The Mediator command interface, matched by generic definition (09.1.2-T1's naming).</summary>
    private const string CommandInterface = "Mediator.ICommand`1";

    /// <summary>
    /// Every type of the Application assembly, ordered. The attribute rules scan the whole assembly, not
    /// only its commands, so an <c>[Audited]</c> that lands on a query or a handler is caught too.
    /// </summary>
    private static readonly IReadOnlyList<Type> ApplicationTypes =
        [.. TypesOf(ArchitectureModel.Require(ArchitectureModel.Application))];

    /// <summary>The commands of the Application assembly — the subset the command rules apply to.</summary>
    private static readonly IReadOnlyList<Type> ApplicationCommands =
        [.. ApplicationTypes.Where(IsCommand)];

    /// <summary>
    /// One instance of every probe. Holding instances (rather than only types) keeps the fixtures from
    /// being dead code, and the rules below read the types back off them.
    /// </summary>
    private static readonly IReadOnlyList<object> Probes =
    [
        new ProbeCommandWithoutAttribute(),
        new ProbeBareActionCommand(),
        new ProbeDuplicateActionCommandA(),
        new ProbeDuplicateActionCommandB(),
        new ProbeEmptyReasonCommand(),
        new ProbeLongReasonCommand(),
        new ProbeAuditedWithoutProjectionCommand(),
        new ProbeNotAuditedWithProjectionCommand(),
        new ProbeBothAttributesCommand(),
        new ProbeAuditedQuery(),
    ];

    private static readonly IReadOnlyList<Type> ProbeTypes = [.. Probes.Select(probe => probe.GetType())];

    [Fact]
    public void EveryCommand_HasExactlyOneAuditAttribute()
    {
        var violations = MissingOrConflicting(ApplicationCommands);

        violations.ShouldBeEmpty(
            $"{nameof(EveryCommand_HasExactlyOneAuditAttribute)}: {string.Join("; ", violations)}");
    }

    [Fact]
    public void NotAudited_ReasonIsNonEmptyAndShort()
    {
        var violations = ReasonViolations(ApplicationCommands);
        violations.ShouldBeEmpty($"{nameof(NotAudited_ReasonIsNonEmptyAndShort)}: {string.Join("; ", violations)}");

        var probeViolations = ReasonViolations(ProbeTypes);
        probeViolations.ShouldContain(violation => violation.Contains(nameof(ProbeEmptyReasonCommand), StringComparison.Ordinal));
        probeViolations.ShouldContain(violation => violation.Contains(nameof(ProbeLongReasonCommand), StringComparison.Ordinal));
        probeViolations.ShouldContain(violation => violation.Contains(nameof(ProbeNotAuditedWithProjectionCommand), StringComparison.Ordinal));
    }

    [Fact]
    public void Audited_ActionMatchesDomainVerbPattern()
    {
        var violations = ActionViolations(ApplicationCommands);
        violations.ShouldBeEmpty($"{nameof(Audited_ActionMatchesDomainVerbPattern)}: {string.Join("; ", violations)}");

        ActionViolations(ProbeTypes)
            .ShouldContain(violation => violation.Contains(nameof(ProbeBareActionCommand), StringComparison.Ordinal));
    }

    [Fact]
    public void Audited_ActionsAreUnique()
    {
        var violations = ActionViolations(ApplicationCommands);
        violations.ShouldBeEmpty($"{nameof(Audited_ActionsAreUnique)}: {string.Join("; ", violations)}");

        ActionViolations(ProbeTypes)
            .ShouldContain(violation => violation.Contains("probe.duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void AuditedCommand_MustProjectAuditData()
    {
        var violations = ProjectionViolations(ApplicationCommands);
        violations.ShouldBeEmpty($"{nameof(AuditedCommand_MustProjectAuditData)}: {string.Join("; ", violations)}");

        ProjectionViolations(ProbeTypes)
            .ShouldContain(violation => violation.Contains(nameof(ProbeAuditedWithoutProjectionCommand), StringComparison.Ordinal));
    }

    [Fact]
    public void AuditedAttribute_IsNotOnAQuery()
    {
        var violations = NonCommandViolations(ApplicationTypes);
        violations.ShouldBeEmpty($"{nameof(AuditedAttribute_IsNotOnAQuery)}: {string.Join("; ", violations)}");

        // The set is the assembly, not the filtered command list: it holds the Application queries too, so
        // a real query that grows an [Audited] is inside this rule's reach.
        ApplicationTypes.ShouldContain(
            type => type.Namespace != null && type.Namespace.Contains(".Queries", StringComparison.Ordinal),
            "the rule scans the assembly's queries as well as its commands");

        NonCommandViolations(ProbeTypes)
            .ShouldContain(violation => violation.Contains(nameof(ProbeAuditedQuery), StringComparison.Ordinal));
    }

    /// <summary>
    /// The negative fixture the task file names: a command with neither attribute must be caught, and the
    /// violation must name the command so the build tells its author what to add.
    /// </summary>
    [Fact]
    public void ProbeCommand_WithoutAttribute_IsCaught()
    {
        MissingOrConflicting(ProbeTypes)
            .ShouldContain(violation => violation.Contains(nameof(ProbeCommandWithoutAttribute), StringComparison.Ordinal));

        MissingOrConflicting(ProbeTypes)
            .ShouldContain(violation => violation.Contains(nameof(ProbeBothAttributesCommand), StringComparison.Ordinal));
    }

    /// <summary>
    /// All three arguments are required (the pre-BD15 attribute carried no visibility, so a missing one
    /// must be a compile error rather than a silent default).
    /// </summary>
    [Fact]
    public void AuditedAttribute_RequiresEveryArgument()
    {
        var constructor = typeof(AuditedAttribute).GetConstructors().ShouldHaveSingleItem();

        constructor.GetParameters().Select(parameter => parameter.Name).ShouldBe(["action", "category", "visibility"]);
        constructor.GetParameters().ShouldAllBe(parameter => !parameter.IsOptional && !parameter.HasDefaultValue);

        typeof(AuditedAttribute)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod?.IsPublic == true)
            .ShouldBeEmpty("no settable property may supply a default argument");
    }

    /// <summary>A violation for every command that carries neither attribute, or both.</summary>
    private static List<string> MissingOrConflicting(IEnumerable<Type> types)
    {
        var violations = new List<string>();

        foreach (var type in types.Where(IsCommand))
        {
            var audited = Audited(type);
            var notAudited = NotAudited(type);

            if (audited is null && notAudited is null)
            {
                violations.Add($"{type.FullName} implements ICommand<> but carries neither [Audited] nor [NotAudited]");
            }
            else if (audited is not null && notAudited is not null)
            {
                violations.Add($"{type.FullName} carries both [Audited] and [NotAudited]; exactly one is required");
            }
        }

        return violations;
    }

    /// <summary>A violation for every broken <c>[NotAudited]</c> reason, and for an exempt command that
    /// still projects audited data nothing would ever write.</summary>
    private static List<string> ReasonViolations(IEnumerable<Type> types)
    {
        var violations = new List<string>();

        foreach (var type in types.Where(IsCommand))
        {
            if (NotAudited(type) is not { } notAudited)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(notAudited.Reason))
            {
                violations.Add($"{type.FullName} is [NotAudited] with a blank reason; a reviewer must be able to check it");
            }
            else if (notAudited.Reason.Length > NotAuditedAttribute.ReasonMaxLength)
            {
                violations.Add(
                    $"{type.FullName} has a {notAudited.Reason.Length}-character [NotAudited] reason; "
                    + $"the maximum is {NotAuditedAttribute.ReasonMaxLength}");
            }

            if (typeof(IAuditData).IsAssignableFrom(type))
            {
                violations.Add($"{type.FullName} is [NotAudited] but implements IAuditData, whose projection nothing would write");
            }
        }

        return violations;
    }

    /// <summary>A violation for every action outside <see cref="AuditedAttribute.ActionPattern"/>, and for
    /// every action two commands share (the action is the frozen wire <c>messageKey</c>).</summary>
    private static List<string> ActionViolations(IEnumerable<Type> types)
    {
        var violations = new List<string>();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var type in types.Where(IsCommand))
        {
            if (Audited(type) is not { } audited)
            {
                continue;
            }

            if (!ActionPattern().IsMatch(audited.Action))
            {
                violations.Add(
                    $"{type.FullName} has [Audited(\"{audited.Action}\")], which does not match "
                    + $"{AuditedAttribute.ActionPattern}");
            }

            if (owners.TryGetValue(audited.Action, out var owner))
            {
                violations.Add($"action '{audited.Action}' is declared by both {owner} and {type.FullName}");
            }
            else
            {
                owners[audited.Action] = type.FullName ?? type.Name;
            }
        }

        return violations;
    }

    /// <summary>A violation for every <c>[Audited]</c> command that projects nothing: the behaviour would
    /// write no row at all, which is the silent half of the contract.</summary>
    private static List<string> ProjectionViolations(IEnumerable<Type> types)
    {
        var violations = new List<string>();

        foreach (var type in types.Where(IsCommand))
        {
            if (Audited(type) is not null && !typeof(IAuditData).IsAssignableFrom(type))
            {
                violations.Add($"{type.FullName} is [Audited] but implements no IAuditData, so no row would ever be written");
            }
        }

        return violations;
    }

    /// <summary>A violation for every <c>[Audited]</c> that sits on a type which is not a command: slot 6
    /// runs for commands only (09 README D4) and the failure-handling table calls this an error.</summary>
    private static List<string> NonCommandViolations(IEnumerable<Type> types)
    {
        var violations = new List<string>();

        foreach (var type in types)
        {
            if (Audited(type) is not null && !IsCommand(type))
            {
                violations.Add($"{type.FullName} carries [Audited] but implements no ICommand<>; slot 6 is commands-only");
            }
        }

        return violations;
    }

    private static bool IsCommand(Type type) =>
        type.GetInterfaces().Any(implemented => implemented.IsGenericType
            && string.Equals(implemented.GetGenericTypeDefinition().FullName, CommandInterface, StringComparison.Ordinal));

    private static IEnumerable<Type> TypesOf(Assembly assembly) =>
        assembly.GetTypes().OrderBy(type => type.FullName, StringComparer.Ordinal);

    private static AuditedAttribute? Audited(Type type) =>
        type.GetCustomAttribute<AuditedAttribute>(inherit: false);

    private static NotAuditedAttribute? NotAudited(Type type) =>
        type.GetCustomAttribute<NotAuditedAttribute>(inherit: false);

    /// <summary>
    /// The frozen action pattern, compiled from <see cref="AuditedAttribute.ActionPattern"/> so the rule and
    /// the contract can never drift.
    /// </summary>
    [GeneratedRegex(AuditedAttribute.ActionPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ActionPattern();
}
