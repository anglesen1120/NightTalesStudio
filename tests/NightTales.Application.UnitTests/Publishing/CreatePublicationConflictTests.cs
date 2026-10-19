using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using NightTales.Application.Features.Publishing.Commands.CreatePublication;
using NightTales.Domain.Publishing;
using NightTales.Infrastructure.Persistence;
using NightTales.TestKit.Auth;
using NightTales.TestKit.Database;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Publishing;

/// <summary>
/// The flush recogniser of the create command (06.2.1-T1 task §Application step 8, 06 README D6/D18): which
/// provider refusal the handler turns into 409 <c>publication_invalid_state</c> and which two it turns into 404
/// <c>not_found</c> — plus the reason the names it matches are the ones the model and the migration produce, so a
/// rename cannot silently turn either answer into a 500.
/// </summary>
/// <remarks>
/// These tests need no database: the recogniser reads the provider's <see cref="DbException"/> through the BCL,
/// and the model the migration is generated from is built in-process from the production options (the same ones
/// <see cref="TestDbContextFactory"/> hands the tests' contexts). The live catalog's half of the same contract
/// is <c>AddPublicationsMigrationTests</c>.
/// </remarks>
public sealed class CreatePublicationConflictTests
{
    [Fact]
    public void ActiveIndexRefusal_IsTheDuplicateCreate()
    {
        CreatePublicationConflict
            .IsActivePublicationViolation(
                new DbUpdateException(
                    "The save failed.",
                    new StubDbException("23505", CreatePublicationConflict.ActiveEpisodePlatformIndex)))
            .ShouldBeTrue();

        // The pipeline may wrap the provider failure once more: the walk covers every inner exception.
        CreatePublicationConflict
            .IsActivePublicationViolation(
                new InvalidOperationException(
                    "wrapped",
                    new DbUpdateException(
                        "The save failed.",
                        new StubDbException("23505", CreatePublicationConflict.ActiveEpisodePlatformIndex))))
            .ShouldBeTrue();

        // Any other constraint under the same SQLSTATE is a defect this layer must not hide.
        CreatePublicationConflict
            .IsActivePublicationViolation(
                new DbUpdateException(
                    "The save failed.",
                    new StubDbException("23505", "ix_publications_user_id_created_at")))
            .ShouldBeFalse("that index is not unique, so a refusal naming it is a defect");

        // The same name under another SQLSTATE is not the unique violation either.
        CreatePublicationConflict
            .IsActivePublicationViolation(
                new DbUpdateException(
                    "The save failed.",
                    new StubDbException("23503", CreatePublicationConflict.ActiveEpisodePlatformIndex)))
            .ShouldBeFalse();

        // Npgsql records no constraint name on every exception shape.
        CreatePublicationConflict
            .IsActivePublicationViolation(
                new DbUpdateException("The save failed.", new StubDbException("23505", null)))
            .ShouldBeFalse();
    }

    [Fact]
    public void EpisodeKeyRefusal_IsTheVanishedEpisode()
    {
        CreatePublicationConflict
            .IsEpisodeForeignKeyViolation(
                new DbUpdateException(
                    "The save failed.",
                    new StubDbException("23503", CreatePublicationConflict.EpisodeForeignKey)))
            .ShouldBeTrue();

        CreatePublicationConflict
            .IsEpisodeForeignKeyViolation(
                new InvalidOperationException(
                    "wrapped",
                    new DbUpdateException(
                        "The save failed.",
                        new StubDbException("23503", CreatePublicationConflict.EpisodeForeignKey))))
            .ShouldBeTrue();

        // The connection key is a different 404 and must not answer for the episode.
        CreatePublicationConflict
            .IsEpisodeForeignKeyViolation(
                new DbUpdateException(
                    "The save failed.",
                    new StubDbException("23503", CreatePublicationConflict.ChannelConnectionForeignKey)))
            .ShouldBeFalse("only the episode key answers not_found for the episode");

        CreatePublicationConflict
            .IsEpisodeForeignKeyViolation(
                new DbUpdateException(
                    "The save failed.",
                    new StubDbException("23505", CreatePublicationConflict.EpisodeForeignKey)))
            .ShouldBeFalse("the same constraint name under another SQLSTATE is not a foreign-key refusal");
    }

    [Fact]
    public void ConnectionKeyRefusal_IsTheVanishedConnection()
    {
        CreatePublicationConflict
            .IsChannelConnectionForeignKeyViolation(
                new DbUpdateException(
                    "The save failed.",
                    new StubDbException("23503", CreatePublicationConflict.ChannelConnectionForeignKey)))
            .ShouldBeTrue();

        CreatePublicationConflict
            .IsChannelConnectionForeignKeyViolation(
                new InvalidOperationException(
                    "wrapped",
                    new DbUpdateException(
                        "The save failed.",
                        new StubDbException("23503", CreatePublicationConflict.ChannelConnectionForeignKey))))
            .ShouldBeTrue();

        CreatePublicationConflict
            .IsChannelConnectionForeignKeyViolation(
                new DbUpdateException(
                    "The save failed.",
                    new StubDbException("23503", CreatePublicationConflict.EpisodeForeignKey)))
            .ShouldBeFalse("only the connection key answers not_found for the connection");

        CreatePublicationConflict
            .IsChannelConnectionForeignKeyViolation(
                new DbUpdateException(
                    "The save failed.",
                    new StubDbException("23505", CreatePublicationConflict.ChannelConnectionForeignKey)))
            .ShouldBeFalse("the same constraint name under another SQLSTATE is not a foreign-key refusal");

        CreatePublicationConflict
            .IsChannelConnectionForeignKeyViolation(
                new DbUpdateException("The save failed.", new StubDbException("23503", null)))
            .ShouldBeFalse();
    }

    [Fact]
    public void NullException_Throws() =>
        Should.Throw<ArgumentNullException>(
            () => CreatePublicationConflict.IsChannelConnectionForeignKeyViolation(null!));

    [Fact]
    public void Names_AreTheForeignKeysAndIndexTheModelDeclares()
    {
        // The three predicates match on string literals. The design-time model is what the migration is
        // generated from, and the runtime model drops the configuration that is only there for the schema
        // (EpisodeIdeaModelTests states the same for a CHECK and an index' sort direction), so the design-time
        // model is the one to pin the literals against. No connection is opened: the model comes from the
        // options alone.
        var options = TestDbContextFactory.AppOptions(
            "Host=127.0.0.1;Port=1;Database=nts_model_only;Username=none;Password=none");

        using var context = new AppDbContext(options, new TestCurrentUser());

        var publication = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Publication));
        publication.ShouldNotBeNull("the model must map the publications row (06 README D6)");

        var foreignKeys = publication!.GetForeignKeys()
            .Select(key => key.GetConstraintName())
            .ToList();

        foreignKeys.ShouldContain(CreatePublicationConflict.EpisodeForeignKey);
        foreignKeys.ShouldContain(CreatePublicationConflict.ChannelConnectionForeignKey);

        publication.GetIndexes()
            .Select(index => index.GetDatabaseName())
            .ShouldContain(CreatePublicationConflict.ActiveEpisodePlatformIndex);
    }

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
