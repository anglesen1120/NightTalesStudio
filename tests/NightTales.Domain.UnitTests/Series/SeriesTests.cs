using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Events;
using Shouldly;
using Xunit;

// The test namespace ends in `Series`, which shadows the aggregate's name inside it, so the entity is
// aliased (09.4.1-T1).
using SeriesAggregate = NightTales.Domain.Series.Series;

namespace NightTales.Domain.UnitTests.Series;

/// <summary>
/// The behaviour of the <c>series</c> aggregate (09.4.1 plan §3, 09 README D16): the two 409s it owns,
/// the idempotent archive and the events the outbox stores.
/// </summary>
/// <remarks>
/// Pure unit tests: no database, no clock, no context. The catalog and wire checks (does the voice
/// exist, does the template match the topic, is the title the right length) belong to the Application
/// handlers and their validator, so they are asserted there.
/// </remarks>
public sealed class SeriesTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 1, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Series_Update_TopicWhileAutopilotEnabled_ReturnsTopicLocked()
    {
        var series = Create();
        EnableAutopilot(series);

        var result = series.Update(Patch(topic: Topic.TrueCrime), FixedNow.AddMinutes(1));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("series_topic_locked");
        result.FirstError.Code.ShouldBe(SeriesAggregate.TopicLockedCode);
        series.Topic.ShouldBe(Topic.Ghost, "a refused update must leave the row as it was");

        // The same change is accepted once autopilot is off: the lock is about autopilot, not the topic.
        DisableAutopilot(series);
        series.Update(Patch(topic: Topic.TrueCrime), FixedNow.AddMinutes(2)).IsError.ShouldBeFalse();
        series.Topic.ShouldBe(Topic.TrueCrime);
    }

    [Fact]
    public void Series_Archive_Twice_RaisesEventOnce()
    {
        var series = Create();

        series.Archive(FixedNow).IsError.ShouldBeFalse();

        var archived = series.DomainEvents.OfType<SeriesArchived>().ToArray();
        archived.Length.ShouldBe(1);
        archived[0].SeriesId.ShouldBe(series.Id);
        archived[0].UserId.ShouldBe(series.UserId);
        archived[0].OccurredAt.ShouldBe(FixedNow);
        series.ArchivedAt.ShouldBe(FixedNow);

        series.Archive(FixedNow.AddHours(3)).IsError.ShouldBeFalse();

        series.DomainEvents.OfType<SeriesArchived>().Count().ShouldBe(1, "archiving is idempotent");
        series.ArchivedAt.ShouldBe(FixedNow, "a repeated archive must not move the stamp");
    }

    [Fact]
    public void Series_UpdateArchived_ReturnsSeriesArchived()
    {
        var series = Create();
        series.Archive(FixedNow);

        var result = series.Update(Patch(title: "Tên khác"), FixedNow.AddMinutes(1));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("series_archived");
        result.FirstError.Code.ShouldBe(SeriesAggregate.ArchivedCode);
        series.Title.ShouldBe("Chuỗi thử nghiệm");
    }

    [Fact]
    public void Series_Create_RaisesSeriesCreatedWithTheOwner()
    {
        var userId = Guid.NewGuid();

        var series = SeriesAggregate.Create(
            userId,
            new SeriesDraft("  Chuỗi mới  ", Topic.TrueCrime, Language.En, Aspect.Portrait, "voice-en", Guid.NewGuid(), "ghost.vi.v1"),
            FixedNow);

        series.UserId.ShouldBe(userId);
        series.Title.ShouldBe("Chuỗi mới", "the stored title is the trimmed one");
        series.Language.ShouldBe(Language.En);
        series.ArchivedAt.ShouldBeNull();
        series.CreatedAt.ShouldBe(FixedNow);
        series.UpdatedAt.ShouldBe(FixedNow);

        var created = series.DomainEvents.OfType<SeriesCreated>().ToArray();
        created.Length.ShouldBe(1);
        created[0].SeriesId.ShouldBe(series.Id);
        created[0].UserId.ShouldBe(userId);
        created[0].OccurredAt.ShouldBe(FixedNow);
    }

    [Fact]
    public void Series_Update_SameTopicWhileAutopilotEnabled_Allowed()
    {
        // The lock is a topic *change*, not the member being present: a patch that names the topic
        // the series already has (a client that always sends it) is a no-op, not a 409.
        var series = Create();
        EnableAutopilot(series);

        var result = series.Update(Patch(topic: Topic.Ghost, title: "Đổi tên"), FixedNow.AddMinutes(1));

        result.IsError.ShouldBeFalse();
        series.Topic.ShouldBe(Topic.Ghost);
        series.Title.ShouldBe("Đổi tên");
    }

    [Fact]
    public void Series_Update_ArchivedTakesPrecedenceOverTopicLock()
    {
        // Order pin: the archived refusal is checked before the autopilot topic lock, so an archived
        // autopilot series answers series_archived, not series_topic_locked.
        var series = Create();
        EnableAutopilot(series);
        series.Archive(FixedNow.AddMinutes(1));

        var result = series.Update(Patch(topic: Topic.Spiritual), FixedNow.AddMinutes(2));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(SeriesAggregate.ArchivedCode);
    }

    [Fact]
    public void Series_Update_ClearsStoryPresetAndKeepsAbsentFields()
    {
        var series = Create();

        series.Update(Patch(title: "Khác", storyPreset: "ghost.vi.v1"), FixedNow.AddMinutes(1)).IsError.ShouldBeFalse();
        series.StoryPreset.ShouldBe("ghost.vi.v1");
        series.Aspect.ShouldBe(Aspect.Landscape, "an absent member keeps the stored value");

        series.Update(Patch(clearStoryPreset: true), FixedNow.AddMinutes(2)).IsError.ShouldBeFalse();

        series.StoryPreset.ShouldBeNull("[DECISION] a null storyPreset clears the column");
        series.Title.ShouldBe("Khác", "the other members were not sent, so they kept their values");
    }

    [Fact]
    public void Series_Codes_MatchTheErrorCatalogue()
    {
        // The Domain cannot reference Application/Common/Errors/ErrorCodes.cs (D1: ErrorOr is its only
        // package), so it spells the two 409 codes itself; the HTTP status those codes map to is the
        // catalogue's. This test is the join between the two, and it is why a rename cannot slip
        // through unnoticed.
        SeriesAggregate.ArchivedCode.ShouldBe(ErrorCodes.SeriesArchived);
        SeriesAggregate.TopicLockedCode.ShouldBe(ErrorCodes.SeriesTopicLocked);
    }

    private static SeriesAggregate Create() => SeriesAggregate.Create(
        Guid.NewGuid(),
        new SeriesDraft(
            "Chuỗi thử nghiệm",
            Topic.Ghost,
            Language.Vi,
            Aspect.Landscape,
            "nts-test-voice-vi",
            Guid.NewGuid(),
            null),
        FixedNow);

    private static SeriesPatch Patch(
        string? title = null,
        Topic? topic = null,
        Aspect? aspect = null,
        string? voiceId = null,
        Guid? styleTemplateId = null,
        string? storyPreset = null,
        bool clearStoryPreset = false) =>
        new(
            title,
            topic,
            aspect,
            voiceId,
            styleTemplateId,
            clearStoryPreset ? null : storyPreset);

    /// <summary>
    /// Flips the autopilot flag the way the task that owns it will (02.8.3-T1 writes
    /// <c>autopilot_enabled</c>); the aggregate only reads it, so no lock test can be written without
    /// a way to set the column.
    /// </summary>
    private static void EnableAutopilot(SeriesAggregate series) => SetAutopilot(series, true);

    private static void DisableAutopilot(SeriesAggregate series) => SetAutopilot(series, false);

    private static void SetAutopilot(SeriesAggregate series, bool enabled) =>
        typeof(SeriesAggregate)
            .GetProperty(nameof(SeriesAggregate.AutopilotEnabled))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(series, [enabled]);
}
