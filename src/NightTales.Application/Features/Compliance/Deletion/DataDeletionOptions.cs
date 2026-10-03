using NightTales.Domain.Compliance.Deletion;

namespace NightTales.Application.Features.Compliance.Deletion;

/// <summary>The <c>DataDeletion:*</c> configuration of the deletion engine (13.3.1 plan §7.1). Nothing here is required or secret.</summary>
public sealed class DataDeletionOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "DataDeletion";

    /// <summary>
    /// The scopes deferred while their owner has not landed (<c>channel</c>: 06.4.3-T1, <c>account</c>: 13.3.2-T1).
    /// Deliberate deviation from plan §7.1 (<c>["account"]</c>): no <c>channel</c> handler exists yet and the Worker
    /// must boot, so <c>channel</c> is deferred by default. 06.4.3-T1 MUST remove <c>channel</c> from this default
    /// when it registers the handler, so the start-up validator (K21) enforces it from then on.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultDeferredScopes = ["account", "channel"];

    /// <summary>Whether the hourly job is registered; <c>false</c> removes it from the scheduler.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Rows processed per run.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Minutes after which an in-progress claim is considered crashed and reclaimable.</summary>
    public int LeaseMinutes { get; set; } = 15;

    /// <summary>Attempts after which a failed row is no longer retried.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Hours ahead of <c>due_at</c> that the due-soon gauge starts counting a row.</summary>
    public int AlertLeadHours { get; set; } = 24;

    /// <summary>
    /// Wire names of the deferred scopes; <c>null</c> means <see cref="DefaultDeferredScopes"/>. Null (not a default
    /// array) so a configured list replaces the default instead of being appended to it.
    /// </summary>
    public IReadOnlyList<string>? DeferredScopes { get; set; }

    /// <summary>The scheduler guard for <c>deletions reapply</c>: only a declared scratch host sets it (11.6.1-T1).</summary>
    public bool AllowRestoreReapply { get; set; }

    /// <summary>The parsed deferred scopes; unknown names are skipped (validation rejects them at start).</summary>
    public IReadOnlySet<DeletionScope> DeferredScopeSet()
    {
        var set = new HashSet<DeletionScope>();

        foreach (var name in DeferredScopes ?? DefaultDeferredScopes)
        {
            if (DeletionWire.TryParseScope(name, out var scope))
            {
                set.Add(scope);
            }
        }

        return set;
    }

    /// <summary>The first reason this configuration is unusable, or <c>null</c>.</summary>
    public string? Validate()
    {
        if (BatchSize < 1)
        {
            return $"'{SectionName}:{nameof(BatchSize)}' must be at least 1.";
        }

        if (LeaseMinutes < 1)
        {
            return $"'{SectionName}:{nameof(LeaseMinutes)}' must be at least 1.";
        }

        if (MaxAttempts < 1)
        {
            return $"'{SectionName}:{nameof(MaxAttempts)}' must be at least 1.";
        }

        if (AlertLeadHours < 0)
        {
            return $"'{SectionName}:{nameof(AlertLeadHours)}' must not be negative.";
        }

        foreach (var name in DeferredScopes ?? [])
        {
            if (!DeletionWire.TryParseScope(name, out _))
            {
                return $"'{SectionName}:{nameof(DeferredScopes)}' names an unknown scope.";
            }
        }

        return null;
    }
}
