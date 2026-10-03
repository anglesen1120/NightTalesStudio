using NightTales.Domain.Common;

namespace NightTales.Domain.Research;

/// <summary>
/// The unit of work the fetcher leases (02 D10, 02.5.1 plan §3.1). The state machine is
/// <c>Queued → Leased → Done | Failed</c>, with <c>Leased → Queued</c> on an expired lease.
/// </summary>
public sealed class FetchTask : Entity, IUserOwned
{
    private FetchTask()
    {
    }

    public Guid UserId { get; private set; }

    public Guid EpisodeId { get; private set; }

    /// <summary><see langword="null"/> for <see cref="FetchTaskKind.FindSources"/>.</summary>
    public Guid? SourceId { get; private set; }

    /// <summary>The <c>find_sources</c> run (02.7); <see langword="null"/> for <see cref="FetchTaskKind.FetchSource"/>.</summary>
    public Guid? GenerationRunId { get; private set; }

    public FetchTaskKind Kind { get; private set; }

    public string? Url { get; private set; }

    /// <summary>Drives the one-lease-per-registrable-domain rule.</summary>
    public string? RegistrableDomain { get; private set; }

    public FetchTaskStatus Status { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>The earliest instant the task may be leased (the backoff between attempts).</summary>
    public DateTimeOffset NotBefore { get; private set; }

    public DateTimeOffset? LeasedAt { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    public string? LeasedBy { get; private set; }

    public string? ErrorCode { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>Creates the queued task of a url source (<c>attempts = 0</c>, <c>not_before = now</c>).</summary>
    /// <param name="id">The UUIDv7 id.</param>
    /// <param name="userId">The owner.</param>
    /// <param name="episodeId">The episode.</param>
    /// <param name="sourceId">The source it fetches.</param>
    /// <param name="url">The normalised URL the fetcher downloads.</param>
    /// <param name="registrableDomain">The registrable domain of <paramref name="url"/>.</param>
    /// <param name="now">The command's clock reading.</param>
    public static FetchTask CreateFetchSource(
        Guid id,
        Guid userId,
        Guid episodeId,
        Guid sourceId,
        string url,
        string registrableDomain,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(registrableDomain);

        return new FetchTask
        {
            Id = id,
            UserId = userId,
            EpisodeId = episodeId,
            SourceId = sourceId,
            Kind = FetchTaskKind.FetchSource,
            Url = url,
            RegistrableDomain = registrableDomain,
            Status = FetchTaskStatus.Queued,
            Attempts = 0,
            NotBefore = now,
            CreatedAt = now,
        };
    }

    /// <summary>Whether a leased task's lease has run out at <paramref name="now"/>.</summary>
    /// <param name="now">The instant to compare.</param>
    public bool IsLeaseExpired(DateTimeOffset now) => Status == FetchTaskStatus.Leased && LeaseUntil <= now;

    /// <summary><c>Queued</c> to <c>Leased</c>; counts an attempt.</summary>
    /// <param name="workerId">The fetcher instance id.</param>
    /// <param name="now">The lease instant.</param>
    /// <param name="lease">The lease length.</param>
    public void Lease(string workerId, DateTimeOffset now, TimeSpan lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        Require(FetchTaskStatus.Queued, nameof(Lease));

        Status = FetchTaskStatus.Leased;
        LeasedBy = workerId;
        LeasedAt = now;
        LeaseUntil = now + lease;
        Attempts++;
    }

    /// <summary>Extends the lease of a leased task.</summary>
    /// <param name="now">The heartbeat instant.</param>
    /// <param name="lease">The new lease length from <paramref name="now"/>.</param>
    public void Heartbeat(DateTimeOffset now, TimeSpan lease)
    {
        Require(FetchTaskStatus.Leased, nameof(Heartbeat));

        LeaseUntil = now + lease;
    }

    /// <summary><c>Leased</c> to <c>Done</c>.</summary>
    /// <param name="now">The finish instant.</param>
    public void Complete(DateTimeOffset now)
    {
        Require(FetchTaskStatus.Leased, nameof(Complete));

        Status = FetchTaskStatus.Done;
        FinishedAt = now;
    }

    /// <summary><c>Leased</c> to <c>Failed</c> with a D8 job outcome code.</summary>
    /// <param name="errorCode">The job outcome code.</param>
    /// <param name="now">The finish instant.</param>
    public void Fail(string errorCode, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        Require(FetchTaskStatus.Leased, nameof(Fail));

        Status = FetchTaskStatus.Failed;
        ErrorCode = errorCode;
        FinishedAt = now;
    }

    /// <summary>An expired lease goes back to <c>Queued</c>, not leasable before <paramref name="now"/> plus <paramref name="backoff"/>.</summary>
    /// <param name="now">The sweep instant.</param>
    /// <param name="backoff">The wait before the next lease.</param>
    public void Requeue(DateTimeOffset now, TimeSpan backoff)
    {
        Require(FetchTaskStatus.Leased, nameof(Requeue));
        if (!IsLeaseExpired(now))
        {
            throw new InvalidOperationException("The lease is still active; only an expired lease can be requeued.");
        }

        Status = FetchTaskStatus.Queued;
        LeasedBy = null;
        LeasedAt = null;
        LeaseUntil = null;
        NotBefore = now + backoff;
    }

    /// <summary>Withdraws a queued or leased task.</summary>
    /// <param name="now">The cancel instant.</param>
    public void Cancel(DateTimeOffset now)
    {
        if (Status is not (FetchTaskStatus.Queued or FetchTaskStatus.Leased))
        {
            throw new InvalidOperationException($"A task in status '{Status}' cannot be cancelled.");
        }

        Status = FetchTaskStatus.Cancelled;
        FinishedAt = now;
    }

    private void Require(FetchTaskStatus expected, string operation)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"{operation} needs a {expected} task, but it is {Status}.");
        }
    }
}
