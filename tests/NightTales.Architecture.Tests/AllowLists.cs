namespace NightTales.Architecture.Tests;

/// <summary>
/// The single place to edit when a legitimate new exception is approved (09.1.2-T1, README D1/D10).
/// Reviewed by the Tech Lead through <c>.github/CODEOWNERS</c>.
/// </summary>
internal static class AllowLists
{
    /// <summary>Namespaces whose types may call <c>IgnoreQueryFilters</c> (README D10).</summary>
    internal const string IgnoreQueryFiltersNamespacePrefix = "NightTales.Application.Features.Admin";

    /// <summary>
    /// Extra types outside <see cref="IgnoreQueryFiltersNamespacePrefix"/> that may bypass the
    /// tenant filter. Matched as fully-qualified names (<c>namespace.Type</c>) so a same-named type
    /// in another namespace does not inherit the bypass.
    /// </summary>
    internal static readonly IReadOnlySet<string> IgnoreQueryFiltersTypeNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "NightTales.Application.Features.Assets.Jobs.RetentionSweepJob",
            "NightTales.Application.Features.Compliance.Deletion.Jobs.DataDeletionJob",
            "NightTales.Worker.Jobs.CreditReconciliationJob",
            "NightTales.Infrastructure.Persistence.Queries.SystemWorkQuery",
        };

    /// <summary>Job method parameters that may be <c>string</c> (README D12).</summary>
    internal static readonly IReadOnlySet<string> JobStringParameterNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "idempotencyKey",
            "recurringId",
        };

    internal static readonly IReadOnlySet<string> DomainAllowedReferences =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "ErrorOr",
        };

    /// <summary>README D1 plus the 11 CR-3 addition of <c>NightTales.RenderNode.Contract</c>.</summary>
    /// <remarks>
    /// The <c>Mediator.Abstractions</c> package id ships an assembly named <c>Mediator</c>, so both
    /// names are listed: the rule matches assembly names, not package ids.
    /// </remarks>
    internal static readonly IReadOnlySet<string> ApplicationAllowedReferences =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "ErrorOr",
            "FluentValidation",
            "FluentValidation.DependencyInjectionExtensions",
            "Mediator",
            "Mediator.Abstractions",
            "Microsoft.EntityFrameworkCore",
            // 09.2.6-T3 (09 README W2-O4): `ISmtpEmailSender` takes the composed `MimeMessage` of one
            // e-mail, so the port and the job that composes it carry MimeKit; the SMTP client itself
            // (MailKit) stays an Infrastructure dependency.
            "MimeKit",
            "NightTales.Domain",
            "NightTales.RenderNode.Contract",
        };

    internal static readonly IReadOnlySet<string> FetcherCoreAllowedReferences =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "NightTales.Domain",
        };

    internal static readonly IReadOnlySet<string> FetcherAllowedReferences =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "NightTales.Fetcher.Core",
        };

    /// <summary>
    /// 11 README D4: the contract library is BCL-only, because the node binary and the mock load it without the
    /// backend. An empty set means "the framework baseline and nothing else".
    /// </summary>
    internal static readonly IReadOnlySet<string> RenderNodeContractAllowedReferences =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// 11 README D3/D5: the core carries the contract, <c>ErrorOr</c> (the seam result type) and the ASP.NET Core
    /// framework — never Domain, Application or Infrastructure (11 CR-3).
    /// </summary>
    internal static readonly IReadOnlySet<string> RenderNodeServerAllowedReferences =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "ErrorOr",
            "NightTales.RenderNode.Contract",
        };

    internal static readonly IReadOnlyList<string> RenderNodeServerAllowedPrefixes =
    [
        "Microsoft.AspNetCore.",
        "Microsoft.Extensions.",
    ];

    /// <summary>
    /// 11 README D3: <c>NightTales.MockMpt</c> (tools/mock-mpt) may reference only the server core and the
    /// contract — never Domain, Application or Infrastructure, so the fake node loads without the backend.
    /// The bare <c>Microsoft.AspNetCore</c> meta-assembly and <c>Microsoft.Win32.Primitives</c> are what the
    /// Web SDK host pulls in (the core is a library and does not); both are framework assemblies, not
    /// backend code.
    /// </summary>
    internal static readonly IReadOnlySet<string> MockMptAllowedReferences =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "ErrorOr",
            "Microsoft.AspNetCore",
            "Microsoft.Win32.Primitives",
            "NightTales.RenderNode.Contract",
            "NightTales.RenderNode.Server",
        };

    internal static readonly IReadOnlyList<string> MockMptAllowedPrefixes = RenderNodeServerAllowedPrefixes;

    /// <summary>02 CR-19 lets the Fetcher use any <c>Microsoft.Extensions.*</c> package.</summary>
    internal static readonly IReadOnlyList<string> FetcherAllowedPrefixes =
        ["Microsoft.Extensions."];

    internal static readonly IReadOnlyList<string> NoExtraPrefixes = [];

    /// <summary>
    /// Framework references every project may carry without appearing in a project's allow-list
    /// (README D1: <c>System.*</c>, <c>Microsoft.Extensions.*.Abstractions</c>, <c>netstandard</c>).
    /// </summary>
    internal static bool IsBaselineReference(string name) =>
        name is "netstandard" or "mscorlib" or "System" or "Microsoft.CSharp" or "WindowsBase"
        || name.StartsWith("System.", StringComparison.Ordinal)
        || (name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
            && name.EndsWith(".Abstractions", StringComparison.Ordinal));
}
