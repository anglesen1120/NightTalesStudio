using NightTales.Domain.Research;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Research;

/// <summary>
/// The alias reconciliation of one edited fact-sheet version (02.5.4-T1, CR-4/D21): an alias a previous version
/// gave a source is kept, a source cited for the first time takes the smallest free <c>s&lt;N&gt;</c> in first
/// citation order, and the result carries exactly the cited sources.
/// </summary>
/// <remarks>
/// Pure unit tests: both inputs are arguments. The map is what keeps a stored citation readable after the
/// episode's sources change, so the tests pin its content, its order and the fact that the caller's map is not
/// touched.
/// </remarks>
public sealed class FactSheetSourceAliasesTests
{
    private static readonly Guid Source1 = Guid.Parse("0197b000-0000-7000-8000-0000000000d1");
    private static readonly Guid Source2 = Guid.Parse("0197b000-0000-7000-8000-0000000000d2");
    private static readonly Guid Source3 = Guid.Parse("0197b000-0000-7000-8000-0000000000d3");

    [Fact]
    public void Reconcile_NoPreviousVersion_NumbersTheCitationsInOrder()
    {
        var reconciled = FactSheetSourceAliases.Reconcile(Empty(), [Source2, Source1]);

        reconciled.Count.ShouldBe(2);
        reconciled["s1"].ShouldBe(Source2);
        reconciled["s2"].ShouldBe(Source1);
        reconciled.Keys.ShouldBe(["s1", "s2"]);
    }

    [Fact]
    public void Reconcile_KnownSources_KeepTheirAlias()
    {
        var previous = Map(("s1", Source1), ("s2", Source2));

        var reconciled = FactSheetSourceAliases.Reconcile(previous, [Source2, Source1]);

        reconciled["s2"].ShouldBe(Source2, "the alias a stored citation carries must not move");
        reconciled["s1"].ShouldBe(Source1);
    }

    [Fact]
    public void Reconcile_OnlyCitedSources_AreReturned()
    {
        var previous = Map(("s1", Source1), ("s2", Source2), ("s3", Source3));

        var reconciled = FactSheetSourceAliases.Reconcile(previous, [Source1, Source3]);

        reconciled.Count.ShouldBe(2);
        reconciled.Keys.ShouldBe(["s1", "s3"]);
        reconciled.ShouldNotContainKey("s2");
    }

    [Fact]
    public void Reconcile_NewSource_TakesTheSmallestUnusedAlias()
    {
        // s1, s2 and s4 are spent by names the previous version used, so the new source fills the s3 gap
        // instead of taking s5.
        var previous = Map(("s1", Source1), ("s2", Source2), ("s4", Source3));
        var newSource = Guid.Parse("0197b000-0000-7000-8000-0000000000d4");

        var reconciled = FactSheetSourceAliases.Reconcile(previous, [Source1, Source3, newSource]);

        reconciled.Keys.ShouldBe(["s1", "s4", "s3"]);
        reconciled["s3"].ShouldBe(newSource);
    }

    [Fact]
    public void Reconcile_AnAliasThePreviousVersionUsed_IsNotRecycled()
    {
        // s2 belonged to a source this body no longer cites. It stays spent: the alias is the stable half of a
        // stored citation, so handing it to another source would make the two versions read differently.
        var previous = Map(("s1", Source1), ("s2", Source2));
        var newSource = Guid.Parse("0197b000-0000-7000-8000-0000000000d4");

        var reconciled = FactSheetSourceAliases.Reconcile(previous, [Source1, newSource]);

        reconciled.Keys.ShouldBe(["s1", "s3"]);
        reconciled["s3"].ShouldBe(newSource);
        reconciled.ShouldNotContainKey("s2");
    }

    [Fact]
    public void Reconcile_MixedKnownAndNew_SharesOneNumbering()
    {
        var previous = Map(("s1", Source1));

        var reconciled = FactSheetSourceAliases.Reconcile(previous, [Source2, Source1, Source3]);

        reconciled.Keys.ShouldBe(["s2", "s1", "s3"]);
        reconciled["s2"].ShouldBe(Source2);
        reconciled["s1"].ShouldBe(Source1);
        reconciled["s3"].ShouldBe(Source3);
    }

    [Fact]
    public void Reconcile_RepeatedCitation_IsCountedOnce()
    {
        var reconciled = FactSheetSourceAliases.Reconcile(Empty(), [Source1, Source1, Source2]);

        reconciled.Count.ShouldBe(2);
        reconciled.Keys.ShouldBe(["s1", "s2"]);
        reconciled["s1"].ShouldBe(Source1);
        reconciled["s2"].ShouldBe(Source2);
    }

    [Fact]
    public void Reconcile_UnknownAliasShape_DoesNotBlockS1()
    {
        // Only an alias of the form s<N> occupies a number: a hand-written key (none exists today) neither
        // blocks nor is renumbered, and it is returned only when its source is cited.
        var previous = Map(("note", Source1));

        var reconciled = FactSheetSourceAliases.Reconcile(previous, [Source1, Source2]);

        reconciled.Keys.ShouldBe(["note", "s1"]);
        reconciled["note"].ShouldBe(Source1);
        reconciled["s1"].ShouldBe(Source2);
    }

    [Fact]
    public void Reconcile_EmptyCitations_ReturnsAnEmptyMap()
    {
        var reconciled = FactSheetSourceAliases.Reconcile(Map(("s1", Source1)), []);

        reconciled.ShouldBeEmpty();
    }

    [Fact]
    public void Reconcile_IsPureAndDeterministic()
    {
        var previous = Map(("s1", Source1));

        var first = FactSheetSourceAliases.Reconcile(previous, [Source2, Source1]);
        var second = FactSheetSourceAliases.Reconcile(previous, [Source2, Source1]);

        first.Keys.ShouldBe(second.Keys);
        first["s1"].ShouldBe(second["s1"]);
        first["s2"].ShouldBe(second["s2"]);
        previous.Count.ShouldBe(1, "the previous version's map is read, never mutated");
        previous["s1"].ShouldBe(Source1);
    }

    [Fact]
    public void Reconcile_EveryCitedSourceGetsExactlyOneAlias()
    {
        var previous = Map(("s2", Source2), ("s5", Source3));
        var cited = new[] { Source3, Source1, Source2, Source1 };

        var reconciled = FactSheetSourceAliases.Reconcile(previous, cited);

        reconciled.Count.ShouldBe(3);
        foreach (var sourceId in cited.Distinct())
        {
            reconciled.Values.Count(value => value == sourceId).ShouldBe(1);
        }

        reconciled["s5"].ShouldBe(Source3);
        reconciled["s1"].ShouldBe(Source1);
        reconciled["s2"].ShouldBe(Source2);
    }

    [Fact]
    public void Reconcile_NullArguments_Throw()
    {
        Should.Throw<ArgumentNullException>(() => FactSheetSourceAliases.Reconcile(null!, [Source1]));
        Should.Throw<ArgumentNullException>(() => FactSheetSourceAliases.Reconcile(Empty(), null!));
    }

    private static Dictionary<string, Guid> Empty() => new(StringComparer.Ordinal);

    private static Dictionary<string, Guid> Map(params (string Alias, Guid SourceId)[] entries)
    {
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (alias, sourceId) in entries)
        {
            map[alias] = sourceId;
        }

        return map;
    }
}
