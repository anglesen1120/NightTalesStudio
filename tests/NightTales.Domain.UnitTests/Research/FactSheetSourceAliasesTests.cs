using NightTales.Domain.Research;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Research;

/// <summary>
/// The alias reconciliation of one edited fact-sheet version (02.5.4-T1, CR-4/D21): an alias a previous version
/// gave a still-cited source is kept, an alias of a source this body dropped is free again, a source cited for
/// the first time takes the smallest free <c>s&lt;N&gt;</c> in first citation order, the scheme never issues
/// above <c>s10</c>, and the result carries exactly the cited sources.
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
        // s1, s2 and s4 all belong to sources this body still cites, so the new source fills the s3 gap instead
        // of taking s5.
        var previous = Map(("s1", Source1), ("s2", Source2), ("s4", Source3));
        var newSource = Guid.Parse("0197b000-0000-7000-8000-0000000000d4");

        var reconciled = FactSheetSourceAliases.Reconcile(previous, [Source1, Source2, Source3, newSource]);

        reconciled.Keys.ShouldBe(["s1", "s2", "s4", "s3"]);
        reconciled["s3"].ShouldBe(newSource);
    }

    [Fact]
    public void Reconcile_AliasOfADroppedSource_IsReusedByTheNextNewSource()
    {
        // s2 belonged to a source this body no longer cites, so it is free again: the model only ever reads
        // s1..s10 (README F12), so a dropped source must not shrink that space.
        var previous = Map(("s1", Source1), ("s2", Source2));
        var newSource = Guid.Parse("0197b000-0000-7000-8000-0000000000d4");

        var reconciled = FactSheetSourceAliases.Reconcile(previous, [Source1, newSource]);

        reconciled.Keys.ShouldBe(["s1", "s2"]);
        reconciled["s1"].ShouldBe(Source1, "a still-cited source keeps its alias");
        reconciled["s2"].ShouldBe(newSource, "the alias of the dropped source is free again");
    }

    [Fact]
    public void Reconcile_DeleteFirstSource_ThenAdd_ReusesS1()
    {
        // Deleting the source that held s1 frees that number for the source added right after, so the ten-alias
        // space cannot run out while at most ten distinct sources are cited.
        var previous = Map(("s1", Source1), ("s2", Source2));
        var newSource = Guid.Parse("0197b000-0000-7000-8000-0000000000d4");

        var reconciled = FactSheetSourceAliases.Reconcile(previous, [Source2, newSource]);

        reconciled.Keys.ShouldBe(["s2", "s1"]);
        reconciled["s2"].ShouldBe(Source2);
        reconciled["s1"].ShouldBe(newSource);
    }

    [Fact]
    public void Reconcile_TenDistinctSources_FillS1ToS10()
    {
        var sources = NewSources(FactSheetSourceAliases.MaxAliasNumber);

        var reconciled = FactSheetSourceAliases.Reconcile(Empty(), sources);

        reconciled.Count.ShouldBe(FactSheetSourceAliases.MaxAliasNumber);
        for (var index = 0; index < sources.Count; index++)
        {
            reconciled[$"s{index + 1}"].ShouldBe(sources[index]);
        }
    }

    [Fact]
    public void Reconcile_MoreThanTenDistinctSources_Throws()
    {
        // An eleventh distinct source cannot be named: the scheme refuses instead of issuing s11, which the model
        // must never see (README F12).
        var sources = NewSources(FactSheetSourceAliases.MaxAliasNumber + 1);

        Should.Throw<ArgumentOutOfRangeException>(() => FactSheetSourceAliases.Reconcile(Empty(), sources));
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

    /// <summary><paramref name="count"/> distinct source ids, never equal to the three pinned ones.</summary>
    private static IReadOnlyList<Guid> NewSources(int count) =>
    [
        .. Enumerable.Range(1, count).Select(number => Guid.Parse($"0197b000-0000-7000-8000-{number:d12}")),
    ];

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
