using NightTales.Domain.Stories.Gates;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Stories.Gates;

/// <summary>
/// The gate profile (02.8.2 plan §3.3): the <c>default</c> values, the <c>strict</c> overlay reaching them, and
/// the wire names of <see cref="GateMode"/> that <c>gate_results</c> stores.
/// </summary>
public sealed class AutopilotGateProfileTests
{
    [Fact]
    public void Profile_Defaults_MatchPlanSection33()
    {
        var story = AutopilotGateProfile.Defaults.Story;
        var video = AutopilotGateProfile.Defaults.Video;

        story.MinMeanReviewScore.ShouldBe(3.5m);
        story.MinDimensionScore.ShouldBe(3.0m);
        story.MaxRevisions.ShouldBe(1);
        story.MaxSourceSimilarity.ShouldBe(0.05m);
        story.MaxPastEpisodeSimilarity.ShouldBe(0.10m);
        story.AllowedContentWarnings.ShouldBe(["violence", "death", "crime", "supernatural", "disturbing"]);

        video.DurationTolerance.ShouldBe(0.05);
        video.LoudnessMinLufs.ShouldBe(-16.0);
        video.LoudnessMaxLufs.ShouldBe(-14.0);
    }

    [Fact]
    public void Profile_Overlay_StrictReachesThePlanValues()
    {
        // README §7 / plan §3.3: strict = default with 4.0 / 3.5 / 0.03 / 0.05 overlaid.
        var strict = AutopilotGateProfile.Resolve(new AutopilotGateProfilePatch(
            Story: new StoryGateThresholdsPatch(
                MinMeanReviewScore: 4.0m,
                MinDimensionScore: 3.5m,
                MaxSourceSimilarity: 0.03m,
                MaxPastEpisodeSimilarity: 0.05m)));

        strict.Story.MinMeanReviewScore.ShouldBe(4.0m);
        strict.Story.MinDimensionScore.ShouldBe(3.5m);
        strict.Story.MaxSourceSimilarity.ShouldBe(0.03m);
        strict.Story.MaxPastEpisodeSimilarity.ShouldBe(0.05m);

        // Every key the patch does not name inherits from the default profile.
        strict.Story.MaxRevisions.ShouldBe(1);
        strict.Story.AllowedContentWarnings.ShouldBe(AutopilotGateProfile.Defaults.Story.AllowedContentWarnings);
        strict.Video.ShouldBe(AutopilotGateProfile.Defaults.Video);
    }

    [Fact]
    public void Profile_Overlay_NullKeepsEveryMember()
    {
        AutopilotGateProfile.Resolve(null).ShouldBe(AutopilotGateProfile.Defaults);
        AutopilotGateProfile.Defaults.Overlay(null).ShouldBe(AutopilotGateProfile.Defaults);
        AutopilotGateProfile.Defaults.Story.Overlay(null).ShouldBe(StoryGateThresholds.Defaults);
        AutopilotGateProfile.Defaults.Video.Overlay(null).ShouldBe(VideoGateThresholds.Defaults);

        // An empty patch (every member absent) is the same as no patch.
        AutopilotGateProfile.Defaults
            .Overlay(new AutopilotGateProfilePatch())
            .ShouldBe(AutopilotGateProfile.Defaults);
    }

    [Fact]
    public void Profile_Overlay_EmptyWarningListIsAValue()
    {
        // An empty list is "allow no warning at all", not "inherit": the gate then refuses any declaration.
        var noWarnings = AutopilotGateProfile.Defaults.Overlay(new AutopilotGateProfilePatch(
            Story: new StoryGateThresholdsPatch(AllowedContentWarnings: [])));

        noWarnings.Story.AllowedContentWarnings.ShouldBeEmpty();

        var result = StoryGateRules.Evaluate(GateTestData.StoryInput(), noWarnings);
        result.Failures.ShouldBe([StoryGateCheckIds.ContentWarningsAllowed]);
    }

    [Fact]
    public void Profile_Overlay_VideoKeysAreIndependent()
    {
        var tightened = AutopilotGateProfile.Defaults.Overlay(new AutopilotGateProfilePatch(
            Video: new VideoGateThresholdsPatch(LoudnessMinLufs: -15, LoudnessMaxLufs: -13)));

        tightened.Video.LoudnessMinLufs.ShouldBe(-15.0);
        tightened.Video.LoudnessMaxLufs.ShouldBe(-13.0);
        tightened.Video.DurationTolerance.ShouldBe(0.05);
        tightened.Story.ShouldBe(AutopilotGateProfile.Defaults.Story);

        // The tightened band is still applied by the rule: −16 LUFS sits on the default band's floor and is now
        // below the tightened one.
        var result = VideoGateRules.Evaluate(
            GateTestData.VideoInput(report: GateTestData.Report(integratedLufs: -16)),
            tightened);
        result.Failures.ShouldBe([VideoGateCheckIds.Loudness]);
        GateTestData.Find(result.Checks, VideoGateCheckIds.Loudness).Threshold.ShouldBe((decimal?)(-15));
    }

    [Fact]
    public void GateMode_WireNames()
    {
        GateMode.Live.ToWire().ShouldBe("live");
        GateMode.DryRun.ToWire().ShouldBe("dry_run");
    }

    [Fact]
    public void Profile_Defaults_ModerationAndRealWorldMatchPlanSection33()
    {
        var moderation = AutopilotGateProfile.Defaults.Moderation;

        moderation.BlockedCategories.ShouldBe(
            ["sexual/minors", "self-harm/instructions", "illicit/violent", "hate/threatening"]);
        moderation.MaxScore.Count.ShouldBe(7);
        moderation.MaxScore["harassment"].ShouldBe(0.5);
        moderation.MaxScore["hate"].ShouldBe(0.3);
        moderation.MaxScore["illicit"].ShouldBe(0.5);
        moderation.MaxScore["self-harm"].ShouldBe(0.3);
        moderation.MaxScore["sexual"].ShouldBe(0.5);
        moderation.MaxScore["violence"].ShouldBe(0.6);
        moderation.MaxScore["violence/graphic"].ShouldBe(0.3);

        var realWorld = AutopilotGateProfile.Defaults.RealWorld;
        realWorld.RequireIncidentCoOccurrence.ShouldBeTrue("a lone year or date is not a signal");
        realWorld.IncidentWords.Count.ShouldBe(23, "the twelve vi and eleven en words of the task file");
        realWorld.IncidentWords.ShouldContain("vụ án");
        realWorld.IncidentWords.ShouldContain("suicide");
    }

    [Fact]
    public void Profile_Overlay_ModerationScoresMergeKeyByKey()
    {
        var tightened = AutopilotGateProfile.Defaults.Overlay(new AutopilotGateProfilePatch(
            Moderation: new ModerationThresholdsPatch(
                BlockedCategories: ["sexual/minors"],
                MaxScore: new Dictionary<string, double>(StringComparer.Ordinal) { ["violence"] = 0.2 })));

        tightened.Moderation.BlockedCategories.ShouldBe(["sexual/minors"]);
        tightened.Moderation.MaxScore["violence"].ShouldBe(0.2, "a named entry replaces the inherited one");
        tightened.Moderation.MaxScore["hate"].ShouldBe(0.3, "an entry the patch does not name inherits");
        tightened.Moderation.MaxScore.Count.ShouldBe(7);
        tightened.Story.ShouldBe(AutopilotGateProfile.Defaults.Story);
        tightened.Video.ShouldBe(AutopilotGateProfile.Defaults.Video);
        tightened.RealWorld.ShouldBe(AutopilotGateProfile.Defaults.RealWorld);
    }

    [Fact]
    public void Profile_Overlay_RealWorldPatchReachesBothMembers()
    {
        var changed = AutopilotGateProfile.Defaults.Overlay(new AutopilotGateProfilePatch(
            RealWorld: new RealWorldOptionsPatch(RequireIncidentCoOccurrence: false, IncidentWords: ["án mạng"])));

        changed.RealWorld.RequireIncidentCoOccurrence.ShouldBeFalse();
        changed.RealWorld.IncidentWords.ShouldBe(["án mạng"]);
        changed.Moderation.ShouldBe(AutopilotGateProfile.Defaults.Moderation);
    }

    [Fact]
    public void Profile_Overlay_EmptyIncidentWordListIsAValue()
    {
        var noWords = AutopilotGateProfile.Defaults.Overlay(new AutopilotGateProfilePatch(
            RealWorld: new RealWorldOptionsPatch(IncidentWords: [])));

        noWords.RealWorld.IncidentWords.ShouldBeEmpty();
        noWords.RealWorld.RequireIncidentCoOccurrence.ShouldBeTrue("an unnamed key still inherits");
    }
}
