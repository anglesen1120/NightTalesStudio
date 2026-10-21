namespace NightTales.Infrastructure.Persistence.Outbox;

/// <summary>
/// The dispatcher configuration bound from the <c>Outbox</c> section (09.2.4 plan §7, 09 README D11).
/// </summary>
public sealed class OutboxDispatcherOptions
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string SectionName = "Outbox";

    /// <summary>The poll interval when <see cref="PollIntervalSeconds"/> is not set.</summary>
    public const int DefaultPollIntervalSeconds = 5;

    /// <summary>The batch size when <see cref="BatchSize"/> is not set.</summary>
    public const int DefaultBatchSize = 20;

    /// <summary>The lease when <see cref="LeaseMinutes"/> is not set.</summary>
    public const int DefaultLeaseMinutes = 5;

    /// <summary>The attempt limit when <see cref="MaxAttempts"/> is not set.</summary>
    public const int DefaultMaxAttempts = 10;

    /// <summary>How long an idle dispatcher sleeps between two claims (<c>Outbox:PollIntervalSeconds</c>).</summary>
    public int PollIntervalSeconds { get; set; } = DefaultPollIntervalSeconds;

    /// <summary>How many due rows one claim takes (<c>Outbox:BatchSize</c>) — the loop is not delayed while a batch was full.</summary>
    public int BatchSize { get; set; } = DefaultBatchSize;

    /// <summary>
    /// How long a claimed row is invisible to every dispatcher (<c>Outbox:LeaseMinutes</c>). It is the
    /// crash window: a Worker that dies mid-message leaves the row to be claimed again once the lease is
    /// over, which is what makes delivery at-least-once.
    /// </summary>
    public int LeaseMinutes { get; set; } = DefaultLeaseMinutes;

    /// <summary>
    /// How many times a message may fail before it becomes a dead letter (<c>Outbox:MaxAttempts</c>): the
    /// <see cref="MaxAttempts"/>-th failure sets <c>failed_at</c> and increments
    /// <c>outbox_dead_letters_total</c>.
    /// </summary>
    public int MaxAttempts { get; set; } = DefaultMaxAttempts;

    /// <summary>The delay the loop uses between two claims of an empty batch.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);
}
