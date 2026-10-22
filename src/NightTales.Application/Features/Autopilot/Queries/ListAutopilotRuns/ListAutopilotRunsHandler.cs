using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Common.Paging;
using NightTales.Application.Features.Autopilot.Dtos;
using NightTales.Application.Features.Autopilot.Orchestration;
using NightTales.Domain.Series.Autopilot;

// The aggregate is aliased: `NightTales.Domain.Series` is both the namespace of the run and the name of the series
// aggregate, and this folder names both (the alias is the autopilot feature's convention, see `AutopilotSlotPlanner`).
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Application.Features.Autopilot.Queries.ListAutopilotRuns;

/// <summary>
/// <c>GET /api/v1/series/{id}/autopilot/runs</c> (02.8.3 plan §4 UC7, task §Routes): the keyset page
/// <c>WHERE (slot_at, id) &lt; (@c, @i) ORDER BY slot_at DESC, id DESC LIMIT @limit + 1</c>, whose extra row is what
/// tells the handler whether a next page exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership first, then the page.</b> The series is read through the D10 tenant filter before anything else, so a
/// foreign or unknown id answers 404 and never an empty page — a list route must not tell a caller that a series it
/// cannot see has no runs (09 README D10). A series with no runs answers <c>items: []</c>, never 404 (task §Failure
/// handling).
/// </para>
/// <para>
/// <b>The ordering pair is the key.</b> The comparison is the row-value comparison Npgsql translates against a
/// composite index, and the limit is read one row over, so a run inserted between two requests appears in neither
/// page nor twice. <see cref="Cursor"/>'s first member is the <c>slot_at</c> of the last row here, not its
/// <c>created_at</c>: the ordering is the slot, which is what the timeline shows.
/// </para>
/// <para>
/// <b>The wire values are spelled in memory</b> (the projection cannot translate an enum's wire form), and
/// <see cref="GateSummaryProjection"/> turns each row's stored <c>gates.v1</c> document into the summary the UI reads.
/// </para>
/// </remarks>
/// <param name="readDb">The no-tracking read model; the D10 tenant filter applies to it.</param>
public sealed class ListAutopilotRunsHandler(IReadDb readDb)
    : IQueryHandler<ListAutopilotRunsQuery, ErrorOr<AutopilotRunPageDto>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<AutopilotRunPageDto>> Handle(
        ListAutopilotRunsQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Limit is { } invalid && invalid is < ListAutopilotRunsQuery.MinLimit or > ListAutopilotRunsQuery.MaxLimit)
        {
            return Errors.ValidationFailed(
                "limit",
                $"The page size must be between {ListAutopilotRunsQuery.MinLimit} and {ListAutopilotRunsQuery.MaxLimit}.");
        }

        var limit = query.Limit ?? ListAutopilotRunsQuery.DefaultLimit;

        var seriesVisible = await readDb
            .Query<SeriesAggregate>()
            .AnyAsync(series => series.Id == query.SeriesId, cancellationToken)
            .ConfigureAwait(false);

        if (!seriesVisible)
        {
            return Errors.NotFound();
        }

        var runs = readDb.Query<AutopilotRun>().Where(run => run.SeriesId == query.SeriesId);

        if (query.Cursor is { } rawCursor)
        {
            if (!Cursor.TryDecode(rawCursor, out var cursor))
            {
                return Errors.ValidationFailed("cursor", "The cursor is not a valid page cursor.");
            }

            var beforeSlotAt = cursor.CreatedAt;
            var beforeId = cursor.Id;

            // A row-value comparison, not `slot_at < @c OR (slot_at = @c AND id < @i)`: Npgsql translates the tuple
            // comparison to `(slot_at, id) < (@c, @i)`, one range a composite index can seek (PERF-01, the
            // ListSeriesHandler rule).
            runs = runs.Where(run => ValueTuple.Create(run.SlotAt, run.Id)
                .CompareTo(ValueTuple.Create(beforeSlotAt, beforeId)) < 0);
        }

        var page = await runs
            .OrderByDescending(run => run.SlotAt)
            .ThenByDescending(run => run.Id)
            .Take(limit + 1)
            .Select(run => new Row(
                run.Id,
                run.SlotAt,
                run.StartAt,
                run.State,
                run.Outcome,
                run.PauseReason,
                run.Step,
                run.Attempt,
                run.RenderAttempts,
                run.WaitingFor,
                run.IdeaId,
                run.EpisodeId,
                run.PublicationId,
                run.GateResults,
                run.AuthorizationId,
                run.FinishedAt,
                run.LastTransitionAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = page
            .Take(limit)
            .Select(row => new AutopilotRunDto(
                row.Id,
                row.SlotAt,
                row.StartAt,
                AutopilotRunMetrics.Wire(row.State),
                row.Outcome is { } outcome ? AutopilotRunMetrics.Wire(outcome.ToString()) : null,
                row.PauseReason?.ToWire(),
                row.Step,
                row.Attempt,
                row.RenderAttempts,
                row.WaitingFor is { } waitingFor ? AutopilotRunMetrics.Wire(waitingFor.ToString()) : null,
                row.IdeaId,
                row.EpisodeId,
                row.PublicationId,
                GateSummaryProjection.From(row.GateResults),
                row.AuthorizationId,
                row.FinishedAt,
                row.LastTransitionAt))
            .ToList();

        var nextCursor = page.Count > limit && items.Count > 0
            ? new Cursor(items[^1].SlotAt, items[^1].Id).Encode()
            : null;

        return new AutopilotRunPageDto(items, nextCursor);
    }

    /// <summary>The projected row: the columns the DTO needs, before the wire spelling is computed in memory.</summary>
    private sealed record Row(
        Guid Id,
        DateTimeOffset SlotAt,
        DateTimeOffset StartAt,
        AutopilotRunState State,
        AutopilotRunOutcome? Outcome,
        AutopilotPauseReason? PauseReason,
        string? Step,
        int Attempt,
        int RenderAttempts,
        WaitingFor? WaitingFor,
        Guid? IdeaId,
        Guid? EpisodeId,
        Guid? PublicationId,
        string? GateResults,
        Guid? AuthorizationId,
        DateTimeOffset? FinishedAt,
        DateTimeOffset LastTransitionAt);
}
