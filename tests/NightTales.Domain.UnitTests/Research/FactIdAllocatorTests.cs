using NightTales.Application.Common.Errors;
using NightTales.Domain.Research;
using Shouldly;
using Xunit;

namespace NightTales.Domain.UnitTests.Research;

/// <summary>
/// The id rules of one edited fact-sheet version (02.5.4-T1, README F12): a kept id survives verbatim and in the
/// given order, a new fact continues from the episode's watermark, a retired id is never handed out again, and a
/// body that names an unknown or repeated id is the catalogue's 400 with the failing field named.
/// </summary>
/// <remarks>
/// Pure unit tests: the current version's ids and the watermark are arguments, nothing is read or written.
/// </remarks>
public sealed class FactIdAllocatorTests
{
    [Fact]
    public void Allocate_NewFacts_ContinueFromTheWatermarkInInputOrder()
    {
        var result = FactIdAllocator.Allocate(["f1", "f2"], 2, [null, null]);

        result.IsError.ShouldBeFalse();
        result.Value.Ids.ShouldBe(["f3", "f4"]);
        result.Value.MaxFactId.ShouldBe(4);
    }

    [Fact]
    public void Allocate_KeptIds_AreReturnedVerbatimInTheGivenOrder()
    {
        var result = FactIdAllocator.Allocate(["f1", "f2", "f3"], 3, ["f3", "f1"]);

        result.IsError.ShouldBeFalse();
        result.Value.Ids.ShouldBe(["f3", "f1"]);
        result.Value.MaxFactId.ShouldBe(3, "keeping every id allocates nothing");
    }

    [Fact]
    public void Allocate_KeptAndNewIds_MixInInputOrder()
    {
        var result = FactIdAllocator.Allocate(["f1", "f2", "f5"], 5, [null, "f2", null]);

        result.IsError.ShouldBeFalse();
        result.Value.Ids.ShouldBe(["f6", "f2", "f7"]);
        result.Value.MaxFactId.ShouldBe(7);
    }

    [Fact]
    public void Allocate_AllKeptIds_IsTheIdentityOverTheBody()
    {
        var result = FactIdAllocator.Allocate(["f1", "f2"], 2, ["f1", "f2"]);

        result.IsError.ShouldBeFalse();
        result.Value.Ids.ShouldBe(["f1", "f2"]);
        result.Value.MaxFactId.ShouldBe(2);
    }

    [Fact]
    public void Allocate_DeletedFactId_IsNeverReused()
    {
        // f2 was deleted from the previous version: it is gone from currentIds but the watermark still counts
        // it, so the next new fact is f4 — an id the episode has already spent is never handed out again.
        var result = FactIdAllocator.Allocate(["f1", "f3"], 3, [null]);

        result.IsError.ShouldBeFalse();
        result.Value.Ids.ShouldBe(["f4"]);
        result.Value.MaxFactId.ShouldBe(4);
    }

    [Fact]
    public void Allocate_EmptyCurrentVersion_StillContinuesFromTheWatermark()
    {
        // Every fact was deleted: the version carries none, and the watermark is the only record of the ids the
        // episode has spent.
        var result = FactIdAllocator.Allocate([], 3, [null]);

        result.IsError.ShouldBeFalse();
        result.Value.Ids.ShouldBe(["f4"]);
        result.Value.MaxFactId.ShouldBe(4);
    }

    [Fact]
    public void Allocate_WatermarkAboveTheHighestVisibleId_IsHonoured()
    {
        // The generated draft of 02.5.3-T2 stores max_fact_id 40 while carrying one fact: id 40 is spent.
        var result = FactIdAllocator.Allocate(["f1"], 40, [null]);

        result.IsError.ShouldBeFalse();
        result.Value.Ids.ShouldBe(["f41"]);
        result.Value.MaxFactId.ShouldBe(41);
    }

    [Fact]
    public void Allocate_TwoDigitIds_AreNumberedNotSorted()
    {
        var result = FactIdAllocator.Allocate(["f9", "f10"], 10, [null]);

        result.IsError.ShouldBeFalse();
        result.Value.Ids.ShouldBe(["f11"]);
        result.Value.MaxFactId.ShouldBe(11);
    }

    [Fact]
    public void Allocate_FreshEpisode_StartsAtOne()
    {
        var result = FactIdAllocator.Allocate([], 0, [null, null]);

        result.IsError.ShouldBeFalse();
        result.Value.Ids.ShouldBe(["f1", "f2"]);
        result.Value.MaxFactId.ShouldBe(2);
    }

    [Fact]
    public void Allocate_EmptyBody_AllocatesNothing()
    {
        // The body may save an empty draft (approve refuses it later, F13); the watermark does not move.
        var result = FactIdAllocator.Allocate(["f1", "f2"], 7, []);

        result.IsError.ShouldBeFalse();
        result.Value.Ids.ShouldBeEmpty();
        result.Value.MaxFactId.ShouldBe(7, "the result is never lower than the input watermark");
    }

    [Fact]
    public void Allocate_UnknownId_Returns400WithTheFieldNamed()
    {
        var result = FactIdAllocator.Allocate(["f1"], 1, ["f2"]);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(FactIdAllocator.InvalidContentCode);
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Validation);
        result.FirstError.Metadata![FactIdAllocator.FieldMetadataKey].ShouldBe("facts[0].id");
    }

    [Fact]
    public void Allocate_UnknownId_NamesItsOwnIndex()
    {
        var result = FactIdAllocator.Allocate(["f1"], 1, ["f1", "f9"]);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(FactIdAllocator.InvalidContentCode);
        result.FirstError.Metadata![FactIdAllocator.FieldMetadataKey].ShouldBe("facts[1].id");
    }

    [Fact]
    public void Allocate_RetiredId_IsNotAcceptedBack()
    {
        // f2 was deleted earlier, so it is a valid number but no longer a fact: a client may not resurrect it.
        var result = FactIdAllocator.Allocate(["f1", "f3"], 3, ["f2"]);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(FactIdAllocator.InvalidContentCode);
        result.FirstError.Metadata![FactIdAllocator.FieldMetadataKey].ShouldBe("facts[0].id");
    }

    [Fact]
    public void Allocate_RepeatedId_Returns400AtTheSecondOccurrence()
    {
        var result = FactIdAllocator.Allocate(["f1", "f2"], 2, ["f1", "f1"]);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(FactIdAllocator.InvalidContentCode);
        result.FirstError.Metadata![FactIdAllocator.FieldMetadataKey].ShouldBe("facts[1].id");
    }

    [Fact]
    public void Allocate_NewFactBeforeAKeptId_KeepsBoth()
    {
        var result = FactIdAllocator.Allocate(["f1"], 1, [null, "f1"]);

        result.IsError.ShouldBeFalse();
        result.Value.Ids.ShouldBe(["f2", "f1"]);
        result.Value.MaxFactId.ShouldBe(2);
    }

    [Fact]
    public void Allocate_ManyKeptIds_KeepTheBodyOrder()
    {
        // A reorder is the common edit: the ids follow the body, not the previous version's order.
        var result = FactIdAllocator.Allocate(["f1", "f2", "f3", "f4"], 4, ["f4", "f3", "f2", "f1"]);

        result.IsError.ShouldBeFalse();
        result.Value.Ids.ShouldBe(["f4", "f3", "f2", "f1"]);
        result.Value.MaxFactId.ShouldBe(4);
    }

    [Fact]
    public void Allocate_InvalidContentCode_MatchesTheErrorCatalogue()
    {
        // The Domain may not reference Application/Common/Errors/ErrorCodes.cs (D1: ErrorOr is its only
        // package), so it spells the code itself. This test is the join between the two.
        FactIdAllocator.InvalidContentCode.ShouldBe(ErrorCodes.FactSheetInvalidContent);
        FactIdAllocator.FieldMetadataKey.ShouldBe(ErrorMetadata.Field);
    }

    [Fact]
    public void Allocate_NullArguments_Throw()
    {
        Should.Throw<ArgumentNullException>(() => FactIdAllocator.Allocate(null!, 1, ["f1"]));
        Should.Throw<ArgumentNullException>(() => FactIdAllocator.Allocate(["f1"], 1, null!));
    }

    [Fact]
    public void Allocate_NegativeWatermark_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => FactIdAllocator.Allocate([], -1, [null]));
    }

    [Fact]
    public void Allocate_WatermarkBelowTheHighestCurrentId_Throws()
    {
        // max_fact_id is the episode's, so it can never sit below an id the current version shows: that would
        // let the next new fact collide with a kept one.
        Should.Throw<ArgumentOutOfRangeException>(() => FactIdAllocator.Allocate(["f1", "f5"], 3, [null]));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("f0")]
    [InlineData("f01")]
    [InlineData("F1")]
    [InlineData("f-1")]
    [InlineData("")]
    [InlineData("x1")]
    public void Allocate_MalformedCurrentId_Throws(string currentId)
    {
        Should.Throw<ArgumentException>(() => FactIdAllocator.Allocate([currentId], 40, []));
    }
}
