using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Domain.Stories.Gates;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Gates;

/// <summary>
/// <see cref="AutopilotGateOptions"/> and <see cref="AutopilotGateOptionsValidator"/> (02.8.2 plan §3.3): the
/// shipped defaults, the <c>strict</c>-over-<c>default</c> overlay, the fail-closed fallback of an absent or
/// unknown reference, and every value that must stop the boot instead of a gate.
/// </summary>
public sealed class AutopilotGateOptionsTests
{
    [Fact]
    public void Defaults_AreThePlanValues_AndDryRunIsTrue()
    {
        var options = new AutopilotGateOptions();

        options.DryRun.ShouldBeTrue("nothing is auto-approved until the owner flips the key");
        options.DryRunMaxEpisodes.ShouldBe(10);
        options.Mode.ShouldBe(GateMode.DryRun);
        options.Profiles.ShouldBeEmpty();
        AutopilotGateOptionsValidator.Validate(options).ShouldBeEmpty();
    }

    [Fact]
    public void Resolve_NoProfilesAtAll_IsTheShippedDefaultProfile()
    {
        var options = new AutopilotGateOptions();

        options.Resolve(profileRef: null).ShouldBe(AutopilotGateProfile.Defaults);
        options.Resolve("strict").ShouldBe(AutopilotGateProfile.Defaults, "an unknown ref is never a loose gate");
        options.ResolveName(null).ShouldBe(AutopilotGateOptions.DefaultProfileName);
        options.ResolveName("strict").ShouldBe(AutopilotGateOptions.DefaultProfileName);
    }

    [Fact]
    public void Resolve_StrictOverlaysOnlyItsOwnKeys()
    {
        var options = Configured();

        var strict = options.Resolve("strict");

        strict.Story.MinMeanReviewScore.ShouldBe(4.0m);
        strict.Story.MinDimensionScore.ShouldBe(3.5m);
        strict.Story.MaxSourceSimilarity.ShouldBe(0.03m);
        strict.Story.MaxPastEpisodeSimilarity.ShouldBe(0.05m);
        strict.Story.MaxRevisions.ShouldBe(1, "an unnamed key inherits from default");
        strict.Story.AllowedContentWarnings.ShouldBe(StoryGateThresholds.Defaults.AllowedContentWarnings);
        strict.Video.ShouldBe(VideoGateThresholds.Defaults);
        options.ResolveName("strict").ShouldBe("strict");
    }

    [Fact]
    public void Resolve_DefaultEntryOverridesTheShippedDefaults()
    {
        var options = Configured();
        options.Profiles[AutopilotGateOptions.DefaultProfileName] = new AutopilotGateProfileSection
        {
            Video = new VideoGateThresholdsSection { LoudnessMinLufs = -18, LoudnessMaxLufs = -12 },
        };

        var resolved = options.Resolve(null);

        resolved.Video.LoudnessMinLufs.ShouldBe(-18);
        resolved.Video.LoudnessMaxLufs.ShouldBe(-12);
        resolved.Video.DurationTolerance.ShouldBe(0.05, "the unnamed key still inherits");
        resolved.Story.ShouldBe(StoryGateThresholds.Defaults);
    }

    [Fact]
    public void Mode_IsLive_OnlyWhenDryRunIsOff()
    {
        var options = new AutopilotGateOptions { DryRun = false };

        options.Mode.ShouldBe(GateMode.Live);
    }

    [Fact]
    public void Validate_ProfilesWithoutDefault_IsRefused()
    {
        var options = new AutopilotGateOptions
        {
            Profiles =
            {
                ["strict"] = new AutopilotGateProfileSection
                {
                    Story = new StoryGateThresholdsSection { MinMeanReviewScore = 4.0m },
                },
            },
        };

        AutopilotGateOptionsValidator.Validate(options)
            .ShouldContain(problem => problem.Contains("default", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_EmptyProfile_IsRefused()
    {
        var options = new AutopilotGateOptions
        {
            Profiles = { ["default"] = new AutopilotGateProfileSection() },
        };

        AutopilotGateOptionsValidator.Validate(options)
            .ShouldContain(problem => problem.Contains("names no threshold", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.5, 3.0)]
    [InlineData(3.0, 6.0)]
    public void Validate_ScoreOutsideTheRubric_IsRefused(double mean, double dimension)
    {
        var options = new AutopilotGateOptions
        {
            Profiles =
            {
                ["default"] = new AutopilotGateProfileSection
                {
                    Story = new StoryGateThresholdsSection
                    {
                        MinMeanReviewScore = (decimal)mean,
                        MinDimensionScore = (decimal)dimension,
                    },
                },
            },
        };

        AutopilotGateOptionsValidator.Validate(options).ShouldNotBeEmpty();
    }

    [Fact]
    public void Validate_SimilarityOutsideZeroToOne_IsRefused()
    {
        var options = new AutopilotGateOptions
        {
            Profiles =
            {
                ["default"] = new AutopilotGateProfileSection
                {
                    Story = new StoryGateThresholdsSection { MaxSourceSimilarity = 1.2m },
                },
            },
        };

        AutopilotGateOptionsValidator.Validate(options).ShouldNotBeEmpty();
    }

    [Fact]
    public void Validate_NegativeDryRunLength_IsRefused()
    {
        AutopilotGateOptionsValidator.Validate(new AutopilotGateOptions { DryRunMaxEpisodes = -1 })
            .ShouldNotBeEmpty();
    }

    [Fact]
    public void Validate_InvertedLoudnessBand_IsRefusedOnTheResolvedProfile()
    {
        var options = new AutopilotGateOptions
        {
            Profiles =
            {
                ["default"] = new AutopilotGateProfileSection
                {
                    Video = new VideoGateThresholdsSection { LoudnessMinLufs = -12, LoudnessMaxLufs = -16 },
                },
            },
        };

        AutopilotGateOptionsValidator.Validate(options).ShouldNotBeEmpty();
    }

    [Fact]
    public void Validate_TheDefaultProfileItself_IsAccepted()
    {
        AutopilotGateOptionsValidator.Validate(Configured()).ShouldBeEmpty();
    }

    [Fact]
    public void Resolve_ModerationAndRealWorldSectionsReachTheProfile()
    {
        var options = new AutopilotGateOptions
        {
            Profiles =
            {
                [AutopilotGateOptions.DefaultProfileName] = new AutopilotGateProfileSection
                {
                    Moderation = new ModerationThresholdsSection
                    {
                        BlockedCategories = ["sexual/minors"],
                        MaxScore = new Dictionary<string, double>(StringComparer.Ordinal) { ["violence"] = 0.4 },
                    },
                    RealWorld = new RealWorldSection
                    {
                        RequireIncidentCoOccurrence = false,
                        IncidentWords = ["án mạng"],
                    },
                },
            },
        };

        var resolved = options.Resolve(profileRef: null);

        resolved.Moderation.BlockedCategories.ShouldBe(["sexual/minors"]);
        resolved.Moderation.MaxScore["violence"].ShouldBe(0.4, "a named ceiling replaces the inherited one");
        resolved.Moderation.MaxScore["hate"].ShouldBe(0.3, "an unnamed ceiling still inherits");
        resolved.RealWorld.RequireIncidentCoOccurrence.ShouldBeFalse();
        resolved.RealWorld.IncidentWords.ShouldBe(["án mạng"]);
        resolved.Story.ShouldBe(StoryGateThresholds.Defaults);
        AutopilotGateOptionsValidator.Validate(options).ShouldBeEmpty();
    }

    [Fact]
    public void ProfileSection_OnlyContentGateKeys_IsNotEmpty()
    {
        new AutopilotGateProfileSection().IsEmpty.ShouldBeTrue();
        new AutopilotGateProfileSection { Moderation = new ModerationThresholdsSection() }
            .IsEmpty.ShouldBeFalse("a profile that names only the moderation block is a deliberate overlay");
        new AutopilotGateProfileSection { RealWorld = new RealWorldSection() }
            .IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void Validate_BlankBlockedCategory_IsRefused()
    {
        AutopilotGateOptionsValidator.Validate(WithModeration(new ModerationThresholdsSection
        {
            BlockedCategories = ["sexual/minors", " "],
        }))
            .ShouldContain(problem => problem.Contains("BlockedCategories", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Validate_CategoryScoreOutsideZeroToOne_IsRefused(double score)
    {
        AutopilotGateOptionsValidator.Validate(WithModeration(new ModerationThresholdsSection
        {
            MaxScore = new Dictionary<string, double>(StringComparer.Ordinal) { ["violence"] = score },
        }))
            .ShouldNotBeEmpty();
    }

    [Fact]
    public void Validate_BlankMaxScoreCategory_IsRefused()
    {
        AutopilotGateOptionsValidator.Validate(WithModeration(new ModerationThresholdsSection
        {
            MaxScore = new Dictionary<string, double>(StringComparer.Ordinal) { ["  "] = 0.5 },
        }))
            .ShouldContain(problem => problem.Contains("MaxScore", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ResolvedProfileWithoutBlockedCategories_IsRefused()
    {
        // An empty list is a value, not an absent key: the profile then blocks nothing and only the ceilings
        // decide, which is not a configuration the shipped gate may run with.
        AutopilotGateOptionsValidator.Validate(WithModeration(new ModerationThresholdsSection
        {
            BlockedCategories = [],
        }))
            .ShouldContain(problem => problem.Contains("blocked category", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_BlankIncidentWord_IsRefused()
    {
        AutopilotGateOptionsValidator.Validate(WithRealWorld(new RealWorldSection
        {
            IncidentWords = ["án mạng", ""],
        }))
            .ShouldContain(problem => problem.Contains("IncidentWords", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RealWorldWithoutIncidentWords_IsAccepted()
    {
        // With the co-occurrence rule off the words are unused, so an empty list is a decision, not a typo.
        AutopilotGateOptionsValidator.Validate(WithRealWorld(new RealWorldSection
        {
            RequireIncidentCoOccurrence = false,
            IncidentWords = [],
        }))
            .ShouldBeEmpty();
    }

    private static AutopilotGateOptions WithModeration(ModerationThresholdsSection moderation) => new()
    {
        Profiles =
        {
            [AutopilotGateOptions.DefaultProfileName] = new AutopilotGateProfileSection { Moderation = moderation },
        },
    };

    private static AutopilotGateOptions WithRealWorld(RealWorldSection realWorld) => new()
    {
        Profiles =
        {
            [AutopilotGateOptions.DefaultProfileName] = new AutopilotGateProfileSection { RealWorld = realWorld },
        },
    };

    private static AutopilotGateOptions Configured() => new()
    {
        Profiles =
        {
            ["default"] = new AutopilotGateProfileSection
            {
                Video = new VideoGateThresholdsSection { LoudnessMinLufs = -16, LoudnessMaxLufs = -14 },
            },
            ["strict"] = new AutopilotGateProfileSection
            {
                Story = new StoryGateThresholdsSection
                {
                    MinMeanReviewScore = 4.0m,
                    MinDimensionScore = 3.5m,
                    MaxSourceSimilarity = 0.03m,
                    MaxPastEpisodeSimilarity = 0.05m,
                },
            },
        },
    };
}
