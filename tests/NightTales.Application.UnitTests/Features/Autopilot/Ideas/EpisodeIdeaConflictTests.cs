using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Features.Autopilot.Ideas;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.Autopilot.Ideas;

/// <summary>
/// The write-race recognition of <c>IdeaPlanner.MarkUsedAsync</c> (02.8.1-T1 plan §3.1, §4 U4): the partial
/// unique index that allows one idea per episode refusing the update is the same 409 the entity's own
/// transition answers, while a refusal naming any other constraint is a defect this layer must not hide
/// (it propagates).
/// </summary>
public sealed class EpisodeIdeaConflictTests
{
    [Fact]
    public void IsEpisodeUniqueViolation_EpisodeIndex_IsAConflict() =>
        EpisodeIdeaConflict
            .IsEpisodeUniqueViolation(new DbUpdateException(
                "The save failed.",
                new StubDbException("23505", EpisodeIdeaConflict.EpisodeUniqueIndex)))
            .ShouldBeTrue();

    [Fact]
    public void IsEpisodeUniqueViolation_NestedRefusal_IsAConflict() =>
        EpisodeIdeaConflict
            .IsEpisodeUniqueViolation(new DbUpdateException(
                "The save failed.",
                new InvalidOperationException(
                    "wrapped",
                    new StubDbException("23505", EpisodeIdeaConflict.EpisodeUniqueIndex))))
            .ShouldBeTrue();

    [Fact]
    public void IsEpisodeUniqueViolation_SeriesIndex_IsNotAConflict() =>
        EpisodeIdeaConflict
            .IsEpisodeUniqueViolation(new DbUpdateException(
                "The save failed.",
                new StubDbException("23505", "IX_episode_ideas_series_status_created")))
            .ShouldBeFalse("that index is not unique, so a refusal naming it is a defect");

    [Fact]
    public void IsEpisodeUniqueViolation_AnotherSqlState_IsNotAConflict() =>
        EpisodeIdeaConflict
            .IsEpisodeUniqueViolation(new DbUpdateException(
                "The save failed.",
                new StubDbException("23503", EpisodeIdeaConflict.EpisodeUniqueIndex)))
            .ShouldBeFalse("a foreign-key refusal is not the episode race");

    [Fact]
    public void IsEpisodeUniqueViolation_NoConstraintName_IsNotAConflict() =>
        EpisodeIdeaConflict
            .IsEpisodeUniqueViolation(new DbUpdateException("The save failed.", new StubDbException("23505", null)))
            .ShouldBeFalse();

    [Fact]
    public void IsEpisodeUniqueViolation_NoDatabaseException_IsNotAConflict() =>
        EpisodeIdeaConflict
            .IsEpisodeUniqueViolation(new DbUpdateException("The save failed."))
            .ShouldBeFalse();

    [Fact]
    public void IsEpisodeUniqueViolation_Null_Throws() =>
        Should.Throw<ArgumentNullException>(() => EpisodeIdeaConflict.IsEpisodeUniqueViolation(null!));

    /// <summary>A provider exception carrying only the SQLSTATE and the constraint Npgsql records in <c>Data</c>.</summary>
    private sealed class StubDbException : DbException
    {
        public StubDbException(string sqlState, string? constraintName)
            : base("The provider refused the statement.")
        {
            SqlState = sqlState;
            if (constraintName is not null)
            {
                Data["ConstraintName"] = constraintName;
            }
        }

        public override string? SqlState { get; }
    }
}
