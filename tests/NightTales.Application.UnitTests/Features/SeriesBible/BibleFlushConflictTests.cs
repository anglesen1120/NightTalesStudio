using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Features.SeriesBible;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.SeriesBible;

/// <summary>
/// The write-race recognition of a bible save (02.6.3-T1, plan §4 U2): a serialisation failure or a
/// deadlock is the 412 <c>bible_conflict</c> the client's stale ETag gets, while any other refusal (a
/// constraint the layer must not hide) is left to propagate.
/// </summary>
public sealed class BibleFlushConflictTests
{
    [Theory]
    [InlineData("40001")] // serialization_failure
    [InlineData("40P01")] // deadlock_detected
    public void IsSerialisationFailure_DatabaseRace_IsAConflict(string sqlState) =>
        BibleFlushConflict
            .IsSerialisationFailure(new DbUpdateException("The save failed.", new StubDbException(sqlState)))
            .ShouldBeTrue();

    [Fact]
    public void IsSerialisationFailure_NestedRace_IsAConflict() =>
        BibleFlushConflict
            .IsSerialisationFailure(new DbUpdateException(
                "The save failed.",
                new InvalidOperationException("wrapped", new StubDbException("40001"))))
            .ShouldBeTrue();

    [Fact]
    public void IsSerialisationFailure_AnotherSqlState_IsNotAConflict() =>
        BibleFlushConflict
            .IsSerialisationFailure(new DbUpdateException("Unique violation.", new StubDbException("23505")))
            .ShouldBeFalse();

    [Fact]
    public void IsSerialisationFailure_NoDatabaseException_IsNotAConflict() =>
        BibleFlushConflict.IsSerialisationFailure(new DbUpdateException("The save failed.")).ShouldBeFalse();

    [Fact]
    public void IsSerialisationFailure_Null_Throws() =>
        Should.Throw<ArgumentNullException>(() => BibleFlushConflict.IsSerialisationFailure(null!));

    /// <summary>A provider exception that carries only the SQLSTATE the recogniser reads.</summary>
    private sealed class StubDbException(string sqlState) : DbException("The provider refused the statement.")
    {
        public override string? SqlState { get; } = sqlState;
    }
}
