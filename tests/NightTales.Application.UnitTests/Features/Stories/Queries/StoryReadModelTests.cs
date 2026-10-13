using System.Collections;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common;
using NightTales.Application.Features.Stories;
using NightTales.Application.Features.Stories.Queries.GetCurrentStory;
using NightTales.Application.Features.Stories.Queries.GetStoryVersion;
using NightTales.Application.Features.Stories.Queries.GetStoryVersions;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Stories;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Quality;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Stories.Queries;

/// <summary>Handler-level read-model tests using the existing hand-written EF async-query fake pattern.</summary>
public sealed class StoryReadModelTests
{
    [Fact]
    public async Task GetStoryVersions_OrdersDescending_AndFlagsCurrentAndApproved()
    {
        var rig = new Rig();
        var first = rig.AddVersion(1);
        var second = rig.AddVersion(2);
        var third = rig.AddVersion(3);
        rig.Pointers(third.Id, first.Id);
        var result = await rig.History.Handle(new(rig.Episode.Id), CancellationToken.None);
        result.IsError.ShouldBeFalse();
        result.Value.Select(row => row.Version).ShouldBe([3, 2, 1]);
        result.Value.Where(row => row.IsCurrent).Select(row => row.VersionId).ShouldBe([third.Id]);
        result.Value.Where(row => row.Approved).Select(row => row.VersionId).ShouldBe([first.Id]);
        result.Value.Single(row => row.VersionId == second.Id).Source.ShouldBe("edited");
    }

    [Fact]
    public async Task GetStoryVersions_DoesNotLoadContentOrAuditReport()
    {
        var rig = new Rig();
        rig.AddVersion(1, content: "broken JSON", audit: "broken JSON");
        var result = await rig.History.Handle(new(rig.Episode.Id), CancellationToken.None);
        result.IsError.ShouldBeFalse();
        result.Value.Count.ShouldBe(1);
        var members = new MemberNames();
        members.Visit(rig.Db.VersionExpression);
        members.Names.ShouldNotContain(nameof(StoryVersion.Content));
        members.Names.ShouldNotContain(nameof(StoryVersion.AuditReport));
        members.Names.ShouldNotContain(nameof(StoryVersion.Body));
        members.Names.ShouldContain(nameof(StoryVersion.WordCount));
    }

    [Fact]
    public async Task GetStoryVersions_BoundsNewestHistory()
    {
        var rig = new Rig();
        for (var version = 1; version <= GetStoryVersionsHandler.MaximumHistoryVersions + 1; version++)
        {
            rig.AddVersion(version);
        }

        var result = await rig.History.Handle(new(rig.Episode.Id), CancellationToken.None);
        result.Value.Count.ShouldBe(GetStoryVersionsHandler.MaximumHistoryVersions);
        result.Value[0].Version.ShouldBe(GetStoryVersionsHandler.MaximumHistoryVersions + 1);
        result.Value[^1].Version.ShouldBe(2);
    }

    [Fact]
    public async Task GetStoryVersion_UnknownVersionId_ReturnsNotFound()
    {
        var rig = new Rig();
        var result = await rig.Detail.Handle(new(rig.Episode.Id, Guid.NewGuid()), CancellationToken.None);
        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.NotFound);
    }

    [Fact]
    public async Task GetStoryVersion_VersionOfAnotherEpisode_ReturnsNotFound()
    {
        var rig = new Rig();
        var other = rig.AddVersion(1, episodeId: Guid.NewGuid());
        var result = await rig.Detail.Handle(new(rig.Episode.Id, other.Id), CancellationToken.None);
        result.IsError.ShouldBeTrue();
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.NotFound);
    }

    [Fact]
    public async Task StoryQueries_ForeignEpisode_ReturnSameNotFound()
    {
        var rig = new Rig();
        rig.Db.TenantId = Guid.NewGuid();
        var history = await rig.History.Handle(new(rig.Episode.Id), CancellationToken.None);
        var current = await rig.Current.Handle(new(rig.Episode.Id), CancellationToken.None);
        var detail = await rig.Detail.Handle(new(rig.Episode.Id, Guid.NewGuid()), CancellationToken.None);
        history.IsError.ShouldBeTrue();
        current.IsError.ShouldBeTrue();
        detail.IsError.ShouldBeTrue();
        history.FirstError.ShouldBe(current.FirstError);
        history.FirstError.ShouldBe(detail.FirstError);
    }

    [Fact]
    public async Task GetStoryVersion_ProjectsQualityHints_ObjectiveFirst()
    {
        var rig = new Rig();
        var row = rig.AddVersion(1, audit: """
            {"local":{"issues":[
              {"rule":"Q-HOOK-01","severity":"advisory","kind":"taste","segment":"hook","messageKey":"stored.key","evidence":"taste"},
              {"rule":"Q-KW-01","severity":"blocking","kind":"objective","segment":"p:3","evidence":"objective","repair_direction":"add keywords","measured":0,"threshold":1}]},
             "model_review":{"issues":[{"id":"model-1","severity":"blocking","kind":"objective","evidence":"not a local hint"}]}}
            """);
        var result = await rig.Detail.Handle(new(rig.Episode.Id, row.Id), CancellationToken.None);
        var hints = result.Value.QualityHints;
        hints.Count.ShouldBe(2);
        hints[0].RuleId.ShouldBe("Q-KW-01");
        hints[0].MessageKey.ShouldBe(StoryQualityChecker.MessageKeys[StoryQualityChecker.KeywordCountPerSegment]);
        hints[0].Measured.ShouldBe(0m);
        hints[0].Threshold.ShouldBe(1m);
        hints[1].MessageKey.ShouldBe("stored.key");
        hints[1].Measured.ShouldBeNull();
        result.Value.AuditReport!.Value.GetProperty("model_review").GetProperty("issues").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task GetCurrentStory_NoStoryVersion_ReturnsNull()
    {
        var rig = new Rig();
        var result = await rig.Current.Handle(new(rig.Episode.Id), CancellationToken.None);
        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeNull();
        rig.Db.VersionQueries.ShouldBe(0);
    }

    [Fact]
    public async Task GetCurrentStory_EtagMatchesD9Format()
    {
        var rig = new Rig();
        var row = rig.AddVersion(1);
        rig.Pointers(row.Id, null);
        var result = await rig.Current.Handle(new(rig.Episode.Id), CancellationToken.None);
        result.Value!.Etag.ShouldBe($"\"sv-{row.Id}\"");
        var detail = await rig.Detail.Handle(new(rig.Episode.Id, row.Id), CancellationToken.None);
        ((IHasETag)detail.Value).ETag.ShouldBe(result.Value.Etag);
    }

    [Fact]
    public async Task GetCurrentStory_ApprovedEpisode_ExposesApprovedStoryVersionId()
    {
        var rig = new Rig();
        var row = rig.AddVersion(1);
        rig.Pointers(row.Id, row.Id);
        var result = await rig.Current.Handle(new(rig.Episode.Id), CancellationToken.None);
        result.Value!.ApprovedStoryVersionId.ShouldBe(row.Id);
        rig.Pointers(row.Id, null);
        var reopened = await rig.Current.Handle(new(rig.Episode.Id), CancellationToken.None);
        reopened.Value!.ApprovedStoryVersionId.ShouldBeNull();
    }

    [Fact]
    public async Task QualityHints_MapEveryRuleIdToAMessageKey()
    {
        var rig = new Rig();
        var audit = JsonSerializer.Serialize(new
        {
            local = new
            {
                issues = StoryQualityChecker.MessageKeys.Keys.Select(rule => new
                {
                    rule,
                    severity = "advisory",
                    kind = "objective",
                    segment = "global",
                    evidence = "stored",
                }),
            },
        });
        var row = rig.AddVersion(1, audit: audit);
        var result = await rig.Detail.Handle(new(rig.Episode.Id, row.Id), CancellationToken.None);
        result.Value.QualityHints.Count.ShouldBe(StoryQualityChecker.MessageKeys.Count);
        foreach (var hint in result.Value.QualityHints)
        {
            hint.MessageKey.ShouldBe(StoryQualityChecker.MessageKeys[hint.RuleId]);
        }
    }

    [Fact]
    public async Task StoryVersionDetail_AuditReportRoundTripsUnchanged()
    {
        const string audit = """{"schema_version":"audit.v1","extra":{"nested":[1,null,"việt"]},"local":{"issues":[]}}""";
        const string content = """{"schema_version":"legacy","fact_sources":[{"url":"https://source.example/fact"}],"title":"stored"}""";
        var rig = new Rig();
        var row = rig.AddVersion(1, content: content, audit: audit);
        var result = await rig.Detail.Handle(new(rig.Episode.Id, row.Id), CancellationToken.None);
        result.Value.AuditReport!.Value.GetRawText().ShouldBe(audit);
        result.Value.Content!.Value.GetRawText().ShouldBe(content);
        result.Value.CreatedBy.ShouldBe("user");
    }

    [Fact]
    public async Task GetCurrentStory_ReviewSlice_CountsIssuesBySeverity()
    {
        var rig = new Rig();
        var row = rig.AddVersion(1, audit: """
            {"local":{"issues":[{"rule":"Q-HOOK-01","severity":"blocking","kind":"objective"}]},
             "model_review":{"scores":{"hook":4,"factual_care":null},"stale":true,
               "issues":[{"severity":"blocking"},{"severity":"advisory"},{"severity":"advisory"}]}}
            """);
        rig.Pointers(row.Id, null);
        var result = await rig.Current.Handle(new(rig.Episode.Id), CancellationToken.None);
        var review = result.Value!.Review!;
        review.IssueCounts.Blocking.ShouldBe(1);
        review.IssueCounts.Advisory.ShouldBe(2);
        review.Stale.ShouldBeTrue();
        review.Scores!.Value.GetRawText().ShouldBe("{\"hook\":4,\"factual_care\":null}");
    }

    [Theory]
    [InlineData("{broken", "{broken")]
    [InlineData("42", "[]")]
    public async Task StoryRead_MalformedDocuments_FailSafeAndLogIdsOnly(string content, string audit)
    {
        var rig = new Rig();
        var row = rig.AddVersion(1, content: content, audit: audit);
        rig.Pointers(row.Id, null);
        var result = await rig.Current.Handle(new(rig.Episode.Id), CancellationToken.None);
        result.IsError.ShouldBeFalse();
        result.Value!.Content.ShouldBeNull();
        result.Value.AuditReport.ShouldBeNull();
        result.Value.QualityHints.ShouldBeEmpty();
        result.Value.Review.ShouldBeNull();
        result.Value.Body.ShouldBe(row.Body);
        rig.Log.Messages.Count.ShouldBe(2);
        foreach (var message in rig.Log.Messages)
        {
            message.ShouldContain(rig.Episode.Id.ToString());
            message.ShouldContain(row.Id.ToString());
            message.ShouldNotContain(row.Body);
        }
    }

    [Theory]
    [InlineData("{broken", "{\"model_review\":{\"stale\":false}}", true, false)]
    [InlineData("{\"title\":\"stored\"}", "[]", false, true)]
    public async Task StoryRead_UnreadableField_DoesNotEraseReadableField(
        string content, string audit, bool contentUnreadable, bool auditUnreadable)
    {
        var rig = new Rig();
        var row = rig.AddVersion(1, content: content, audit: audit);
        rig.Pointers(row.Id, null);
        var result = await rig.Current.Handle(new(rig.Episode.Id), CancellationToken.None);
        (result.Value!.Content is null).ShouldBe(contentUnreadable);
        (result.Value.AuditReport is null).ShouldBe(auditUnreadable);
        (result.Value.Review is null).ShouldBe(auditUnreadable);
        if (!auditUnreadable)
        {
            result.Value.Review!.Stale.ShouldBeFalse();
        }

        rig.Log.Messages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task StoryRead_ReviewWithoutStaleFlag_FailsSafeToStale()
    {
        var rig = new Rig();
        var row = rig.AddVersion(1, audit: "{\"model_review\":{\"scores\":null}}");
        rig.Pointers(row.Id, null);
        var result = await rig.Current.Handle(new(rig.Episode.Id), CancellationToken.None);
        result.Value!.Review!.Stale.ShouldBeTrue();
        result.Value.Review.Scores.ShouldBeNull();
        result.Value.Review.IssueCounts.Blocking.ShouldBe(0);
        result.Value.Review.IssueCounts.Advisory.ShouldBe(0);
    }

    [Fact]
    public async Task StoryRead_RevisionDriftIssue_ExposesHasDrift()
    {
        var rig = new Rig();
        var row = rig.AddVersion(1, audit: """
            {"model_review":{"stale":false,"issues":[]},
             "revision":{"applied":true,"review_after":{"issues":[
               {"type":"revision_drift","severity":"advisory","kind":"objective","segment":"p:3"}]}}}
            """);
        rig.Pointers(row.Id, null);
        var result = await rig.Current.Handle(new(rig.Episode.Id), CancellationToken.None);
        result.Value!.Review!.HasDrift.ShouldBeTrue();
        result.Value.Review.IssueCounts.Advisory.ShouldBe(0);
    }

    [Fact]
    public async Task StoryRead_RevisionDriftDetailWithAnyFlag_ExposesHasDrift()
    {
        var rig = new Rig();
        var row = rig.AddVersion(1, audit: """
            {"model_review":{"stale":false},
             "revision":{"applied":true,"drift":{"segment_count_changed":false,"changed_segments":["p:3"],"added_findings":[]}}}
            """);
        rig.Pointers(row.Id, null);
        var result = await rig.Current.Handle(new(rig.Episode.Id), CancellationToken.None);
        result.Value!.Review!.HasDrift.ShouldBeTrue();
    }

    [Fact]
    public async Task StoryRead_RevisionWithoutDrift_ExposesNoDrift()
    {
        var rig = new Rig();
        var row = rig.AddVersion(1, audit: """
            {"model_review":{"stale":false},
             "revision":{"applied":true,"drift":{"segment_count_changed":false,"changed_segments":[],"added_findings":[]}}}
            """);
        rig.Pointers(row.Id, null);
        var result = await rig.Current.Handle(new(rig.Episode.Id), CancellationToken.None);
        result.Value!.Review!.HasDrift.ShouldBeFalse();
    }

    [Theory]
    [InlineData("{\"model_review\":{\"stale\":false},\"revision\":5}")]
    [InlineData("{\"model_review\":{\"stale\":false},\"revision\":{\"review_after\":{\"issues\":\"nope\"},\"drift\":{\"changed_segments\":7}}}")]
    [InlineData("{\"model_review\":{\"stale\":false}}")]
    public async Task StoryRead_MissingOrMalformedRevision_FailsSafeToNoDrift(string audit)
    {
        var rig = new Rig();
        var row = rig.AddVersion(1, audit: audit);
        rig.Pointers(row.Id, null);
        var result = await rig.Current.Handle(new(rig.Episode.Id), CancellationToken.None);
        result.Value!.Review!.HasDrift.ShouldBeFalse();
    }

    [Fact]
    public async Task StoryRead_UnknownLocalRuleWithoutKey_DoesNotInventMessageKey()
    {
        var rig = new Rig();
        var row = rig.AddVersion(1, audit: "{\"local\":{\"issues\":[{\"rule\":\"legacy-unknown\"}]}}");
        rig.Pointers(row.Id, null);
        var result = await rig.Current.Handle(new(rig.Episode.Id), CancellationToken.None);
        result.Value!.QualityHints.ShouldBeEmpty();
        result.Value.AuditReport.ShouldNotBeNull();
        rig.Log.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task StoryRead_UncataloguedPipelineIssues_KeepAuditAndHintOrderWithoutLogging()
    {
        var rig = new Rig();
        var audit = JsonSerializer.Serialize(new
        {
            local = new
            {
                issues = new[]
                {
                    new { rule = StoryQualityChecker.KeywordCountPerSegment },
                    new { rule = StoryDraftValidator.LengthAdvisory },
                    new { rule = "Q-HOOK-01" },
                    new { rule = "similarity_unavailable" },
                },
            },
        });
        var row = rig.AddVersion(1, audit: audit);
        rig.Pointers(row.Id, null);

        for (var read = 0; read < 2; read++)
        {
            var result = await rig.Current.Handle(new(rig.Episode.Id), CancellationToken.None);
            var hints = result.Value!.QualityHints;
            hints.Count.ShouldBe(2);
            hints[0].RuleId.ShouldBe(StoryQualityChecker.KeywordCountPerSegment);
            hints[1].RuleId.ShouldBe("Q-HOOK-01");
            foreach (var hint in hints)
            {
                hint.MessageKey.ShouldBe(StoryQualityChecker.MessageKeys[hint.RuleId]);
            }

            result.Value.AuditReport!.Value.GetRawText().ShouldBe(audit);
            rig.Log.Messages.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task StoryRead_MissingAudit_ReturnsNoHintsOrReview()
    {
        var rig = new Rig();
        var row = rig.AddVersion(1);
        rig.Pointers(row.Id, null);
        var result = await rig.Current.Handle(new(rig.Episode.Id), CancellationToken.None);
        result.Value!.Content.ShouldNotBeNull();
        result.Value.AuditReport.ShouldBeNull();
        result.Value.QualityHints.ShouldBeEmpty();
        result.Value.Review.ShouldBeNull();
        rig.Log.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task StoryRead_Cancellation_Propagates()
    {
        var rig = new Rig();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await rig.Current.Handle(new(rig.Episode.Id), cancellation.Token));
    }

    private sealed class Rig
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-05T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        internal Episode Episode { get; } = Episode.Create(Guid.NewGuid(), Guid.NewGuid(), "title", "brief", LengthTier.Standard, Now);
        internal FakeReadDb Db { get; }
        internal CapturingLogger<GetCurrentStoryHandler> Log { get; } = new();
        internal GetStoryVersionsHandler History { get; }
        internal GetStoryVersionHandler Detail { get; }
        internal GetCurrentStoryHandler Current { get; }

        internal Rig()
        {
            Db = new FakeReadDb(Episode);
            History = new GetStoryVersionsHandler(Db);
            Detail = new GetStoryVersionHandler(Db, NullLogger<GetStoryVersionHandler>.Instance);
            Current = new GetCurrentStoryHandler(Db, Log);
        }

        internal StoryVersion AddVersion(int version, string content = "{\"title\":\"stored\"}", string? audit = null, Guid? episodeId = null)
        {
            var segment = new StorySegmentV1("A stored story body", ["forest"], []);
            var draft = new StoryContentV1("title", segment, [segment], segment, "description", [], [], null!);
            var row = StoryVersion.Create(Episode.UserId, episodeId ?? Episode.Id, version,
                StoryVersionSource.Edited, draft, content, null, null, null, null, null, audit, 0.021m, Now.AddSeconds(version));
            Db.Versions.Add(row);
            return row;
        }

        internal void Pointers(Guid? current, Guid? approved)
        {
            typeof(Episode).GetProperty(nameof(Episode.CurrentStoryVersionId))!.SetValue(Episode, current);
            typeof(Episode).GetProperty(nameof(Episode.ApprovedStoryVersionId))!.SetValue(Episode, approved);
        }
    }

    private sealed class FakeReadDb(Episode episode) : IReadDb
    {
        internal Guid TenantId { get; set; } = episode.UserId;
        internal List<StoryVersion> Versions { get; } = [];
        internal Expression? VersionExpression { get; private set; }
        internal int VersionQueries { get; private set; }

        public IQueryable<TEntity> Query<TEntity>() where TEntity : class
        {
            IEnumerable<TEntity> rows;
            Action<Expression>? capture = null;
            if (typeof(TEntity) == typeof(Episode))
            {
                rows = (IEnumerable<TEntity>)(object)new[] { episode };
            }
            else if (typeof(TEntity) == typeof(StoryVersion))
            {
                VersionQueries++;
                rows = (IEnumerable<TEntity>)(object)Versions;
                capture = expression => VersionExpression = expression;
            }
            else
            {
                throw new InvalidOperationException("Unexpected entity query.");
            }

            return new AsyncQuery<TEntity>(rows.Where(row => row is not IUserOwned owned || owned.UserId == TenantId).AsQueryable(), capture);
        }
    }

    private sealed class MemberNames : ExpressionVisitor
    {
        internal HashSet<string> Names { get; } = [];
        protected override Expression VisitMember(MemberExpression node)
        {
            Names.Add(node.Member.Name);
            return base.VisitMember(node);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    // Same minimal IAsyncQueryProvider pattern as NodeSelectorTests; capture projected member expressions.
    private sealed class AsyncQuery<T>(IQueryable<T> query, Action<Expression>? capture)
        : IOrderedQueryable<T>, IAsyncEnumerable<T>, IAsyncQueryProvider
    {
        public Type ElementType => typeof(T);
        public Expression Expression => query.Expression;
        public IQueryProvider Provider => this;
        public IEnumerator<T> GetEnumerator() => query.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            capture?.Invoke(Expression);
            foreach (var item in query)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            await Task.CompletedTask;
        }

        public IQueryable CreateQuery(Expression expression) => new AsyncQuery<T>(query.Provider.CreateQuery<T>(expression), capture);
        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
            new AsyncQuery<TElement>(query.Provider.CreateQuery<TElement>(expression), capture);
        public object? Execute(Expression expression) => query.Provider.Execute(expression);
        public TResult Execute<TResult>(Expression expression) => query.Provider.Execute<TResult>(expression);
        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            capture?.Invoke(expression);
            var resultType = typeof(TResult).GetGenericArguments()[0];
            var result = typeof(IQueryProvider).GetMethod(nameof(IQueryProvider.Execute), 1, [typeof(Expression)])!
                .MakeGenericMethod(resultType).Invoke(query.Provider, [expression]);
            return (TResult)typeof(Task).GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType).Invoke(null, [result])!;
        }
    }
}
