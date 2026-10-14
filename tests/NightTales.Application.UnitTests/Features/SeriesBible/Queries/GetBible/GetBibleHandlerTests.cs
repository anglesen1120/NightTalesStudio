using ErrorOr;
using System.Collections;
using System.Diagnostics.Metrics;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.SeriesBible;
using NightTales.Application.Features.SeriesBible.Queries.GetBible;
using NightTales.Domain.Common;
using NightTales.Domain.Series.Bible;
using NightTales.TestKit.Time;
using Shouldly;
using Xunit;

// The feature namespace `…Features.SeriesBible` shadows the bible entity's name inside this folder, so the
// entity, the aggregate and the two Series enums it is built from are aliased (as the story read rig does).
using SeriesAggregate = NightTales.Domain.Series.Series;
using SeriesBibleEntity = NightTales.Domain.Series.Bible.SeriesBible;
using SeriesDraft = NightTales.Domain.Series.SeriesDraft;
using Topic = NightTales.Domain.Series.Topic;

namespace NightTales.Application.UnitTests.Features.SeriesBible.Queries.GetBible;

/// <summary>
/// <c>GET /api/v1/series/{id}/bible</c> (02.6.3-T1, plan §4 U1): the series' one bible row with its version,
/// its <c>"sb-&lt;version&gt;"</c> ETag and its content, and the two failures it keeps apart — an unknown or
/// another user's series (404) from one of the caller's own whose bible row is missing (500
/// <c>series_bible_missing</c>), which this read never repairs.
/// </summary>
public sealed class GetBibleHandlerTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-04-05T06:07:08Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Handle_BibleRow_ReturnsTheContentItsVersionAndItsEtag()
    {
        var rig = new Rig();
        rig.Bible.ReplaceContent(Rig.Content(), new BibleLimits(), rig.Clock);

        var result = await rig.Handler.Handle(new GetBibleQuery(rig.Series.Id), CancellationToken.None);

        result.IsError.ShouldBeFalse();
        var dto = result.Value;
        dto.SeriesId.ShouldBe(rig.Series.Id);
        dto.Version.ShouldBe(2);
        dto.ETag.ShouldBe("\"sb-2\"");
        dto.Content.Narrator!.Persona.ShouldBe("Persona");
        dto.Content.RecurringCharacters!.ShouldHaveSingleItem().Name.ShouldBe("Linh");
        dto.Proposal.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_SeriesCreatedButNeverEdited_ReturnsTheDefaultBible()
    {
        var rig = new Rig();

        var result = await rig.Handler.Handle(new GetBibleQuery(rig.Series.Id), CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Value.Version.ShouldBe(1);
        result.Value.ETag.ShouldBe("\"sb-1\"");
        result.Value.Content.Narrator!.Persona.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_SeriesWithoutBibleRow_IsSeriesBibleMissing()
    {
        var rig = new Rig(withBible: false);

        var result = await rig.Handler.Handle(new GetBibleQuery(rig.Series.Id), CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(ErrorCodes.SeriesBibleMissing);
    }

    [Fact]
    public async Task Handle_SeriesWithoutBibleRow_CountsOneMissingAlert_AndAWellFormedReadCountsNone()
    {
        // The failure table promises an alert metric beside the 500, so the counter is observed, not assumed.
        long counted = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == SeriesBibleMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => counted += measurement);
        listener.Start();

        using (var metrics = new SeriesBibleMetrics())
        {
            var missing = new Rig(withBible: false, metrics: metrics);
            var error = await missing.Handler.Handle(new GetBibleQuery(missing.Series.Id), CancellationToken.None);
            error.FirstError.Code.ShouldBe(ErrorCodes.SeriesBibleMissing);
            counted.ShouldBe(1);

            counted = 0;
            var present = new Rig(metrics: metrics);
            var read = await present.Handler.Handle(new GetBibleQuery(present.Series.Id), CancellationToken.None);
            read.IsError.ShouldBeFalse();
            counted.ShouldBe(0, "a read that finds its row raises no alert");
        }
    }

    [Fact]
    public async Task Handle_UnknownOrForeignSeries_IsNotFound()
    {
        // The tenant filter of the read model hides another user's series, so both cases arrive here as a
        // series id the query does not know.
        var rig = new Rig(withSeries: false);

        var result = await rig.Handler.Handle(new GetBibleQuery(Guid.CreateVersion7()), CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorType.NotFound);
    }

    [Fact]
    public async Task Handle_NullQuery_Throws()
    {
        var rig = new Rig();

        await Should.ThrowAsync<ArgumentNullException>(
            async () => await rig.Handler.Handle(null!, CancellationToken.None));
    }

    private sealed class Rig
    {
        internal Rig(bool withSeries = true, bool withBible = true, SeriesBibleMetrics? metrics = null)
        {
            Clock = new FakeClock(Now);
            Series = SeriesAggregate.Create(
                Guid.CreateVersion7(),
                new SeriesDraft("A series", Topic.Ghost, Language.Vi, default, "voice-vi", Guid.CreateVersion7(), null),
                Now);
            Bible = SeriesBibleEntity.CreateDefault(Series.Id, Series.UserId, Clock);
            var db = new FakeReadDb(withSeries ? [Series] : [], withBible ? [Bible] : []);
            Handler = new GetBibleHandler(db, metrics ?? new SeriesBibleMetrics());
        }

        internal FakeClock Clock { get; }

        internal SeriesAggregate Series { get; }

        internal SeriesBibleEntity Bible { get; }

        internal GetBibleHandler Handler { get; }

        /// <summary>A valid <c>bible.v1</c> body: the empty default is refused by the validator.</summary>
        internal static SeriesBibleContent Content() => new(
            SeriesBibleContent.V1,
            new BibleNarrator("Narrator", "Persona", "Notes"),
            [new BibleCharacter("ch1", "Linh", "A character.", null)],
            [new BiblePlace("pl1", "The pier", "A place.")],
            [new BibleRule("rl1", "Always name the year.")],
            []);
    }

    /// <summary>The read-model fake of the story read rig: the two sets this handler queries.</summary>
    private sealed class FakeReadDb(
        IReadOnlyList<SeriesAggregate> series,
        IReadOnlyList<SeriesBibleEntity> bibles) : IReadDb
    {
        public IQueryable<TEntity> Query<TEntity>()
            where TEntity : class
        {
            if (typeof(TEntity) == typeof(SeriesAggregate))
            {
                return new AsyncQuery<TEntity>(((IEnumerable<TEntity>)(object)series).AsQueryable());
            }

            if (typeof(TEntity) == typeof(SeriesBibleEntity))
            {
                return new AsyncQuery<TEntity>(((IEnumerable<TEntity>)(object)bibles).AsQueryable());
            }

            throw new InvalidOperationException("Unexpected entity query.");
        }
    }

    /// <summary>
    /// The minimal <see cref="IAsyncQueryProvider"/> the existing handler-level rigs use, so the handler's
    /// <c>FirstOrDefaultAsync</c> and <c>AnyAsync</c> run against an in-memory set instead of a database.
    /// </summary>
    private sealed class AsyncQuery<T>(IQueryable<T> query)
        : IOrderedQueryable<T>, IAsyncEnumerable<T>, IAsyncQueryProvider
    {
        public Type ElementType => typeof(T);

        public Expression Expression => query.Expression;

        public IQueryProvider Provider => this;

        public IEnumerator<T> GetEnumerator() => query.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            foreach (var item in query)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            await Task.CompletedTask;
        }

        public IQueryable CreateQuery(Expression expression) =>
            new AsyncQuery<T>(query.Provider.CreateQuery<T>(expression));

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
            new AsyncQuery<TElement>(query.Provider.CreateQuery<TElement>(expression));

        public object? Execute(Expression expression) => query.Provider.Execute(expression);

        public TResult Execute<TResult>(Expression expression) => query.Provider.Execute<TResult>(expression);

        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resultType = typeof(TResult).GetGenericArguments()[0];
            var result = typeof(IQueryProvider)
                .GetMethod(nameof(IQueryProvider.Execute), 1, [typeof(Expression)])!
                .MakeGenericMethod(resultType)
                .Invoke(query.Provider, [expression]);
            return (TResult)typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType)
                .Invoke(null, [result])!;
        }
    }
}
