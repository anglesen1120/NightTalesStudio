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
    /// The only namespace allowed to update, remove or set the state of a <c>StoryVersion</c> (02.2.2-T1: the
    /// table is append-only; the admin surface of 09.9.1 is the single deliberate exception).
    /// </summary>
    internal const string StoryVersionMutationNamespacePrefix = "NightTales.Application.Features.Admin";

    /// <summary>
    /// The only <c>src</c> types outside <see cref="StoryVersionMutationNamespacePrefix"/> that may call the
    /// untyped EF mutation APIs (<c>DbContext.Update/Attach/Remove</c>, <c>PropertyEntry.CurrentValue</c>,
    /// <c>PropertyValues.SetValues</c>) the <c>StoryVersion</c> rule bans. Matched as fully-qualified names.
    /// <c>AuditableSaveChangesInterceptor</c> stamps <c>created_at</c>/<c>updated_at</c> through
    /// <c>PropertyEntry.CurrentValue</c> for every <c>IAuditable</c> entity; <c>AfterSaveBehavior.Throw</c> still
    /// rejects any update of a saved <c>story_versions</c> row.
    /// </summary>
    internal static readonly IReadOnlySet<string> StoryVersionUntypedMutationTypeNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "NightTales.Infrastructure.Persistence.AuditableSaveChangesInterceptor",
        };

    /// <summary>
    /// The only <c>src</c> types that may construct a <c>System.Net.Http.HttpClient</c> directly
    /// (11 README F13/D13): the pinned-TLS render-node connection factory, plus the pre-existing
    /// breached-password range checker inside the Identity registration. Every other transport goes
    /// through an <c>IHttpClientFactory</c> or through the factory, so a node client cannot be created
    /// without the pin/redirect/timeout rules (11.3.3-T1 step 9). The <c>tools/mock-mpt</c> entry is the
    /// dev-only mock's <c>--healthcheck</c> loopback self-probe (11.1.2-T1), not a render-node client. The
    /// <c>MptHttpClient</c> entry is the adapter's own loopback client to MPT on the node (11.3.5-T2, 11 README
    /// D6: no redirects, 2 s connect timeout), not a backend-to-node client. The <c>SafeHttpClientFactory</c> entry is the
    /// source fetcher's single client (02.5.2-T1): no proxy, no cookies, no automatic redirect or decompression, and a
    /// connect callback that resolves, validates and connects to the checked address (02 D11).
    /// </summary>
    internal static readonly IReadOnlySet<string> HttpClientProducerTypeNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "NightTales.Infrastructure.Rendering.RenderNodeConnectionFactory",
            "NightTales.Infrastructure.DependencyInjection.IdentityServiceCollectionExtensions",
            "NightTales.MockMpt.Health.MockHealthCheck",
            "NightTales.MptAdapter.MptHttpClient",
            "NightTales.Fetcher.Core.Research.Http.SafeHttpClientFactory",
        };


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
            "NightTales.Infrastructure.Pricing.UsageBudgetGuard",
            "NightTales.Infrastructure.Pricing.GenerationRunOpenBoundsReader",
            // 06.1.2-T2: the two locked reads of ChannelConnectionLocker. The by-id read carries the owner
            // predicate itself (an anonymous scope answers null) and the by-account read must see the live row
            // of a channel whoever owns it, because its caller is 06.1.2-T1's anonymous callback, which cannot
            // be tenant-pinned; the connect write checks row.UserId before anything is written.
            "NightTales.Infrastructure.Publishing.ChannelConnectionLocker",
            // 06.2.2-T2: the one locked read of the anonymous cancel-by-link route. Its statement carries the
            // tenancy itself (`id = @id AND user_id = @tokenOwner`) and the owner comes from the consume of the
            // single-use token, never from the request; the scope cannot be tenant-pinned because the route has no
            // principal and the unit of work builds the context before the handler runs.
            "NightTales.Infrastructure.Publishing.PublicationCancelLock",
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

    /// <summary>
    /// 02.5.2-T3 [DECISION T40]: Fetcher.Core is Domain plus what the moved extraction and HTTP code needs —
    /// <c>ErrorOr</c> (the fetch result type) and SmartReader with its AngleSharp parser. The forbidden set
    /// (EF Core, OpenAI, Hangfire, S3, Infrastructure, Application) is enforced separately, on the transitive closure, by
    /// <c>FetcherCore_HasNoEfCoreOrOpenAiReference</c>.
    /// </summary>
    internal static readonly IReadOnlySet<string> FetcherCoreAllowedReferences =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "AngleSharp",
            "ErrorOr",
            "NightTales.Domain",
            "SmartReader",
        };

    /// <summary>The Core takes any <c>Microsoft.Extensions.*</c> package (options, logging), like the host.</summary>
    internal static readonly IReadOnlyList<string> FetcherCoreAllowedPrefixes =
        ["Microsoft.Extensions."];

    /// <summary>
    /// 02.5.2-T3 [DECISION T40]: the host is Fetcher.Core plus Domain and <c>ErrorOr</c> (the types its composition root
    /// and fetch endpoint handler use) and OpenTelemetry (<c>AddOpenTelemetry().WithMetrics</c>, see
    /// <see cref="FetcherAllowedPrefixes"/>). The forbidden set (Infrastructure, Application, EF Core, OpenAI, Hangfire, S3)
    /// is enforced separately, on the transitive closure, by <c>FetcherHost_DependsOnDomainAndCoreOnly</c>.
    /// </summary>
    internal static readonly IReadOnlySet<string> FetcherAllowedReferences =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "ErrorOr",
            "NightTales.Domain",
            "NightTales.Fetcher.Core",
            "OpenTelemetry",
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
    /// 11 README D3: the adapter host adds the durable store (Microsoft.Data.Sqlite, MIT/E11) and Serilog to
    /// the core's set — still never Domain, Application or Infrastructure (11 CR-3). The adapter is the node's
    /// Kestrel host (11.3.5-T4), so like <see cref="MockMptAllowedReferences"/> it also pulls the bare
    /// <c>Microsoft.AspNetCore</c> meta-assembly and <c>Microsoft.Win32.Primitives</c>: framework, not backend code.
    /// </summary>
    internal static readonly IReadOnlySet<string> MptAdapterAllowedReferences =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "ErrorOr",
            "Microsoft.AspNetCore",
            "Microsoft.Data.Sqlite",
            "Microsoft.Win32.Primitives",
            "NightTales.RenderNode.Contract",
            "NightTales.RenderNode.Server",
        };

    internal static readonly IReadOnlyList<string> MptAdapterAllowedPrefixes =
    [
        "Microsoft.AspNetCore.",
        "Microsoft.Extensions.",
        "Serilog",
        "SQLitePCLRaw.",
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

    /// <summary>
    /// 02 CR-19 lets the Fetcher use any <c>Microsoft.Extensions.*</c> package; 02.5.2-T3 adds the
    /// <c>OpenTelemetry.*</c> packages (<c>OpenTelemetry</c> itself is in <see cref="FetcherAllowedReferences"/>).
    /// </summary>
    internal static readonly IReadOnlyList<string> FetcherAllowedPrefixes =
        ["Microsoft.Extensions.", "OpenTelemetry."];

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
