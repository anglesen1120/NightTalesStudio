using NightTales.Application.Features.Autopilot.Gates;
using NightTales.Domain.Common;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Gates;

/// <summary>
/// The <c>failedChecks</c> value of the owner-action e-mail (02.8.2 plan §5, task 02.8.2-T1): the failing check ids
/// in words, and — for a true-crime hold — the sentence the plan requires, in the recipient's language.
/// </summary>
public sealed class OwnerActionNoticeTextTests
{
    /// <summary>A true-crime hold carries the plan's sentence, in the language of the recipient.</summary>
    [Theory]
    [InlineData(Language.En, "the fact sheet and story wait for your approval")]
    [InlineData(Language.Vi, "bảng dữ kiện và truyện đang chờ bạn phê duyệt")]
    public void FailedChecks_TrueCrimeHold_CarriesTheHoldSentence(Language locale, string sentence)
    {
        var text = AutopilotNotificationService.FailedChecksText(
            OwnerActionReasons.TrueCrimeHold,
            [OwnerActionReasons.TrueCrimeHold],
            locale);

        text.ShouldContain(sentence);
        text.ShouldContain("true crime hold");
    }

    /// <summary>Every other stop lists the failing check ids, and none of them grows the hold sentence.</summary>
    [Fact]
    public void FailedChecks_OtherStops_ListTheFailuresOnly()
    {
        var text = AutopilotNotificationService.FailedChecksText(
            OwnerActionReasons.FailedChecks,
            ["self_review_mean_score", "similarity_sources"],
            Language.Vi);

        text.ShouldBe("self review mean score, similarity sources");
    }

    /// <summary>A stop that failed no check names the stop instead of leaving the line empty (a dry run).</summary>
    [Fact]
    public void FailedChecks_NoFailures_NamesTheReason()
    {
        var text = AutopilotNotificationService.FailedChecksText(
            OwnerActionReasons.DryRun,
            [],
            Language.En);

        text.ShouldBe("dry run");
    }
}
