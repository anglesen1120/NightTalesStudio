using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series.Bible;
using NightTales.Infrastructure.Bible;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>series_bibles</c> table (02.6.3 plan §3.3, 00 §6): the one remembered document of a series, its
/// pending proposal and the version both are read and written under.
/// </summary>
/// <remarks>
/// <para>
/// A series owns exactly one row: the unique index <c>UX_series_bibles_series_id</c> is what makes that true in
/// the database, and the relationship to the series itself is declared by
/// <see cref="SeriesConfiguration"/> with the same-owner composite FK every child table uses
/// (NTS-922-SEC-001), so a bible of user B can never point at a series of user A.
/// </para>
/// <para>
/// <see cref="SeriesBible.Version"/> is a plain <c>int</c>, the concurrency token the ETag names
/// (<c>"sb-&lt;version&gt;"</c>, D9) — never <c>xmin</c>, and no second concurrency mechanism: the commands take
/// the row lock and compare the version themselves.
/// </para>
/// <para>
/// <c>content</c> and <c>proposed_content</c> are the canonical documents of <see cref="BibleJson"/>, written and
/// read through <see cref="BibleJsonContext"/> (source-generated, snake_case, no relaxed escaping): each property
/// carries its own converter, so no model-wide convention has to know these two document shapes (CODE-9). Each
/// also carries its own <see cref="ValueComparer{T}"/>, because the content is an immutable record whose lists are
/// compared by reference by default — without it a replaced document would look unchanged and the UPDATE would be
/// skipped (the fact-sheet precedent of 02.5.4-T1).
/// </para>
/// <para>
/// Every value is supplied by <see cref="SeriesBible"/>'s factories and commands, so every property is
/// <c>ValueGeneratedNever</c>; <c>version</c> additionally has the plan's <c>default 1</c> for the backfill
/// <c>INSERT</c> that creates the row of an existing series.
/// </para>
/// </remarks>
internal sealed class SeriesBibleConfiguration : IEntityTypeConfiguration<SeriesBible>
{
    /// <summary>The unique index that makes "one bible per series" a database rule.</summary>
    internal const string SeriesIdIndex = "UX_series_bibles_series_id";

    /// <summary>The index behind the tenant filter (09 README D10).</summary>
    internal const string UserIdIndex = "IX_series_bibles_user_id";

    /// <summary>
    /// The comparer of the <c>content</c> document: EF's default comparer for an <see cref="SeriesBibleContent"/>
    /// is reference equality, and its list members alone would compare by reference even as a value, so a save
    /// that replaced the content would not be seen as a change. The canonical JSON is the document's value, so the
    /// comparison — and the snapshot EF takes of it — is taken over that string.
    /// </summary>
    private static readonly ValueComparer<SeriesBibleContent> ContentComparer =
        new ValueComparer<SeriesBibleContent>(
            (left, right) => Canonical(left) == Canonical(right),
            content => Canonical(content).GetHashCode(StringComparison.Ordinal),
            content => Copy(content));

    /// <summary>
    /// The comparer of the <c>proposed_content</c> document, for exactly the same reason: staging a proposal (or a
    /// clear of one) must be visible to change tracking.
    /// </summary>
    private static readonly ValueComparer<BibleProposal> ProposalComparer =
        new ValueComparer<BibleProposal>(
            (left, right) => Canonical(left) == Canonical(right),
            proposal => Canonical(proposal).GetHashCode(StringComparison.Ordinal),
            proposal => Copy(proposal));

    public void Configure(EntityTypeBuilder<SeriesBible> builder)
    {
        builder.ToTable("series_bibles");
        builder.HasKey(bible => bible.Id);

        builder.Property(bible => bible.Version).HasDefaultValue(SeriesBible.InitialVersion).IsRequired();
        builder.Property(bible => bible.CreatedAt).HasColumnType("timestamptz");
        builder.Property(bible => bible.UpdatedAt).HasColumnType("timestamptz");

        builder.Property(bible => bible.Content)
            .HasConversion(new SeriesBibleContentJsonConverter())
            .HasColumnType("jsonb")
            .IsRequired()
            .Metadata.SetValueComparer(ContentComparer);

        // A series without a pending proposal stores SQL NULL, so the converter only ever sees a document.
        builder.Property(bible => bible.ProposedContent)
            .HasConversion(new SeriesBibleProposalJsonConverter())
            .HasColumnType("jsonb")
            .Metadata.SetValueComparer(ProposalComparer);

        // The episode whose approval produced the proposal. It is the one FK of this table that is not a
        // same-owner composite: ON DELETE SET NULL must be able to clear it when the episode goes, and a
        // composite (proposed_from_episode_id, user_id) could not, because user_id is NOT NULL. The column is
        // written only by the proposal job, which loads the episode under the tenant filter.
        builder.HasOne<Episode>().WithMany()
            .HasForeignKey(bible => bible.ProposedFromEpisodeId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<AppUser>().WithMany().HasForeignKey(bible => bible.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(bible => bible.SeriesId).HasDatabaseName(SeriesIdIndex).IsUnique();
        builder.HasIndex(bible => bible.UserId).HasDatabaseName(UserIdIndex);

        // The domain supplies every value (UUIDv7 id, the clock's timestamps); only `version` has the plan's
        // `default 1`, which is what the backfill INSERT of an existing series relies on.
        foreach (var property in builder.Metadata.GetProperties())
        {
            property.ValueGenerated = property.Name == nameof(SeriesBible.Version)
                ? ValueGenerated.OnAdd
                : ValueGenerated.Never;
        }
    }

    /// <summary>The canonical JSON of a content document, or the empty string for a missing one.</summary>
    private static string Canonical(SeriesBibleContent? content) =>
        content is null ? string.Empty : BibleJson.Serialize(content);

    /// <summary>The canonical JSON of a proposal document, or the empty string for a missing one.</summary>
    private static string Canonical(BibleProposal? proposal) =>
        proposal is null ? string.Empty : BibleJson.SerializeProposal(proposal);

    /// <summary>A value-equal copy of a content document: what EF keeps as the snapshot of the loaded value.</summary>
    private static SeriesBibleContent Copy(SeriesBibleContent? content) =>
        content is null ? SeriesBibleContent.Empty : BibleJson.DeserializeContent(BibleJson.Serialize(content));

    /// <summary>A value-equal copy of a proposal document.</summary>
    private static BibleProposal Copy(BibleProposal? proposal) =>
        proposal is null
            ? null!
            : BibleJson.DeserializeProposal(BibleJson.SerializeProposal(proposal))
              ?? throw new InvalidOperationException("A serialised proposal document is never null.");
}

/// <summary>
/// Writes and reads <c>series_bibles.content</c> as the canonical <c>bible.v1</c> document of
/// <see cref="BibleJson"/>, through the source-generated <see cref="BibleJsonContext"/> (02.6.3 plan §3.3).
/// </summary>
internal sealed class SeriesBibleContentJsonConverter : ValueConverter<SeriesBibleContent, string>
{
    /// <summary>Creates the converter with the bible's canonical settings.</summary>
    public SeriesBibleContentJsonConverter()
        : base(content => Serialize(content), json => Parse(json))
    {
    }

    private static string Serialize(SeriesBibleContent content) =>
        JsonSerializer.Serialize(content, BibleJsonContext.Default.SeriesBibleContent);

    private static SeriesBibleContent Parse(string json) =>
        JsonSerializer.Deserialize(json, BibleJsonContext.Default.SeriesBibleContent)
        ?? throw new InvalidOperationException("A persisted bible document is never null: the column is NOT NULL.");
}

/// <summary>
/// Writes and reads <c>series_bibles.proposed_content</c> as the canonical proposal document
/// (<c>{ops, based_on_version, proposed_at}</c>) of <see cref="BibleJson"/>.
/// </summary>
internal sealed class SeriesBibleProposalJsonConverter : ValueConverter<BibleProposal?, string>
{
    /// <summary>Creates the converter with the bible's canonical settings.</summary>
    public SeriesBibleProposalJsonConverter()
        : base(proposal => Serialize(proposal), json => Parse(json))
    {
    }

    /// <summary>
    /// The canonical document of a proposal. EF never converts a null (<c>ConvertsNulls</c> is false: a
    /// series without a pending proposal keeps a SQL NULL), so the null branch is an unreachable guard.
    /// </summary>
    private static string Serialize(BibleProposal? proposal) =>
        proposal is null
            ? throw new InvalidOperationException("A persisted proposal document is never null: the column is NULL or a document.")
            : JsonSerializer.Serialize(proposal, BibleJsonContext.Default.BibleProposal);

    private static BibleProposal Parse(string json) =>
        JsonSerializer.Deserialize(json, BibleJsonContext.Default.BibleProposal)
        ?? throw new InvalidOperationException("A persisted proposal document is never null: the column is NULL or a document.");
}
