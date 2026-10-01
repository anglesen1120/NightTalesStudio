using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;

namespace NightTales.Architecture.Tests;

/// <summary>
/// The audit-contract probes of 09.3.6-T1: each type deliberately breaks exactly one rule of
/// <see cref="AuditAttributeTests"/>, so every rule is proven to fire instead of passing vacuously on an
/// empty set. They live in the test assembly and are never registered with a Mediator, so nothing but the
/// architecture rules ever sees them.
/// </summary>
/// <remarks>
/// An instance of each is created by <see cref="AuditAttributeTests"/>, which is why none of them takes a
/// constructor argument: the rules inspect types, and holding instances keeps the probes from being dead
/// code.
/// </remarks>
internal sealed record ProbeCommandWithoutAttribute : ICommand<ErrorOr<int>>;

/// <summary>Carries an action without the mandatory <c>domain.</c> prefix of
/// <see cref="AuditedAttribute.ActionPattern"/>.</summary>
[Audited("story_approved", Category.Episodes, Visibility.User)]
internal sealed record ProbeBareActionCommand : ICommand<ErrorOr<int>>, IAuditData
{
    /// <inheritdoc />
    public AuditData ToAuditData() => new(null, null, null, AuditData.NoSummary);
}

/// <summary>One half of the duplicate-action pair (with <see cref="ProbeDuplicateActionCommandB"/>).</summary>
[Audited("probe.duplicate", Category.Episodes, Visibility.User)]
internal sealed record ProbeDuplicateActionCommandA : ICommand<ErrorOr<int>>, IAuditData
{
    /// <inheritdoc />
    public AuditData ToAuditData() => new(null, null, null, AuditData.NoSummary);
}

/// <summary>The other half of the duplicate-action pair.</summary>
[Audited("probe.duplicate", Category.Support, Visibility.Internal)]
internal sealed record ProbeDuplicateActionCommandB : ICommand<ErrorOr<int>>, IAuditData
{
    /// <inheritdoc />
    public AuditData ToAuditData() => new(null, null, null, AuditData.NoSummary);
}

/// <summary>An exemption with no reason at all.</summary>
[NotAudited(" ")]
internal sealed record ProbeEmptyReasonCommand : ICommand<ErrorOr<int>>;

/// <summary>An exemption whose reason is longer than <see cref="NotAuditedAttribute.ReasonMaxLength"/>.</summary>
[NotAudited("probe reason that is deliberately longer than the two hundred character ceiling the architecture test enforces, so that the length rule is proven to fire as well as the rule that a reason must not be blank, which this sentence satisfies")]
internal sealed record ProbeLongReasonCommand : ICommand<ErrorOr<int>>;

/// <summary>An <c>[Audited]</c> command that projects nothing, so the behaviour would write no row.</summary>
[Audited("probe.no_projection", Category.Episodes, Visibility.User)]
internal sealed record ProbeAuditedWithoutProjectionCommand : ICommand<ErrorOr<int>>;

/// <summary>An exemption that still projects a summary nothing could ever write.</summary>
[NotAudited("the handler writes its own row")]
internal sealed record ProbeNotAuditedWithProjectionCommand : ICommand<ErrorOr<int>>, IAuditData
{
    /// <inheritdoc />
    public AuditData ToAuditData() => new(null, null, null, AuditData.NoSummary);
}

/// <summary>Both attributes on one command: exactly one is required.</summary>
[Audited("probe.both", Category.Episodes, Visibility.User)]
[NotAudited("and exempt as well")]
internal sealed record ProbeBothAttributesCommand : ICommand<ErrorOr<int>>, IAuditData
{
    /// <inheritdoc />
    public AuditData ToAuditData() => new(null, null, null, AuditData.NoSummary);
}

/// <summary>An audit attribute on a query — slot 6 is commands-only (09 README D4).</summary>
[Audited("probe.on_query", Category.Support, Visibility.Internal)]
internal sealed record ProbeAuditedQuery : IQuery<ErrorOr<int>>;
