using NightTales.Application.Features.SeriesBible;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.SeriesBible;

/// <summary>
/// 02.6.3-T2 (plan §7.1 step 5, §"Config keys"): the <c>Bible:Proposal</c> defaults the Application layer reads.
/// They are the plan's numbers and they are the same on every host, so an <c>appsettings</c> file that omits the
/// section still caps a model batch exactly as the plan says; the caps only ever drop ops, never fail a run.
/// </summary>
public sealed class BibleProposalJobOptionsTests
{
    [Fact]
    public void Defaults_AreThePlansCaps()
    {
        var options = new BibleProposalJobOptions();

        options.Enabled.ShouldBeTrue("the feature ships on (phase P flag)");
        options.MaxOps.ShouldBe(10);
        options.MaxCharacterOps.ShouldBe(3);
        options.MaxPlaceOps.ShouldBe(3);
        options.MaxRuleOps.ShouldBe(2);
    }

    [Fact]
    public void SectionName_IsTheKeyTheHostsBind()
    {
        BibleProposalJobOptions.SectionName.ShouldBe("Bible:Proposal");

        // The job reads the section the options class names, so the constants must agree with the defaults the
        // binder would leave in place.
        BibleProposalJobOptions.DefaultMaxOps.ShouldBe(10);
        BibleProposalJobOptions.DefaultMaxCharacterOps.ShouldBe(3);
        BibleProposalJobOptions.DefaultMaxPlaceOps.ShouldBe(3);
        BibleProposalJobOptions.DefaultMaxRuleOps.ShouldBe(2);
    }

    [Fact]
    public void PerKindCaps_FitUnderTheTotalCap()
    {
        var options = new BibleProposalJobOptions();

        (options.MaxCharacterOps + options.MaxPlaceOps + options.MaxRuleOps)
            .ShouldBeLessThanOrEqualTo(options.MaxOps, "one batch can never exceed the total cap");
    }
}
