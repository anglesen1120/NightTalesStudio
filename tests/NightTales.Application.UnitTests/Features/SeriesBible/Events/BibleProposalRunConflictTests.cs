using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Features.SeriesBible.Events;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Features.SeriesBible.Events;

/// <summary>
/// The write-race recognition of the <c>StoryApproved</c> proposal path (02.6.3-T2 plan §7.1 step 7): the
/// series-level active-run index refusing the run insert is a deferral the handler schedules two minutes out,
/// while a refusal naming any other constraint is a defect this layer must not hide (it propagates).
/// </summary>
public sealed class BibleProposalRunConflictTests
{
    [Fact]
    public void IsSeriesLevelRunInProgress_SeriesIndex_IsADeferral() =>
        BibleProposalRunConflict
            .IsSeriesLevelRunInProgress(new DbUpdateException(
                "The save failed.",
                new StubDbException("23505", BibleProposalRunConflict.ActiveSeriesRunIndex)))
            .ShouldBeTrue();

    [Fact]
    public void IsSeriesLevelRunInProgress_NestedRefusal_IsADeferral() =>
        BibleProposalRunConflict
            .IsSeriesLevelRunInProgress(new DbUpdateException(
                "The save failed.",
                new InvalidOperationException(
                    "wrapped",
                    new StubDbException("23505", BibleProposalRunConflict.ActiveSeriesRunIndex))))
            .ShouldBeTrue();

    [Fact]
    public void IsSeriesLevelRunInProgress_EpisodeIndex_IsNotADeferral() =>
        BibleProposalRunConflict
            .IsSeriesLevelRunInProgress(new DbUpdateException(
                "The save failed.",
                new StubDbException("23505", "ux_generation_runs_active_episode")))
            .ShouldBeFalse("that index belongs to an episode-level kind, which this handler never inserts");

    [Fact]
    public void IsSeriesLevelRunInProgress_AnotherSqlState_IsNotADeferral() =>
        BibleProposalRunConflict
            .IsSeriesLevelRunInProgress(new DbUpdateException(
                "The save failed.",
                new StubDbException("23503", BibleProposalRunConflict.ActiveSeriesRunIndex)))
            .ShouldBeFalse("a foreign-key refusal is not the active-run race");

    [Fact]
    public void IsSeriesLevelRunInProgress_NoConstraintName_IsNotADeferral() =>
        BibleProposalRunConflict
            .IsSeriesLevelRunInProgress(new DbUpdateException("The save failed.", new StubDbException("23505", null)))
            .ShouldBeFalse();

    [Fact]
    public void IsSeriesLevelRunInProgress_NoDatabaseException_IsNotADeferral() =>
        BibleProposalRunConflict
            .IsSeriesLevelRunInProgress(new DbUpdateException("The save failed."))
            .ShouldBeFalse();

    [Fact]
    public void IsSeriesLevelRunInProgress_Null_Throws() =>
        Should.Throw<ArgumentNullException>(() => BibleProposalRunConflict.IsSeriesLevelRunInProgress(null!));

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
