using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NightTales.Domain.Autopilot.Ideas;
using NightTales.Domain.Episodes;
using NightTales.Domain.Series;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>episode_ideas</c> table (02.8.1 plan §3.1, CR-4): the ideas one <c>ideas</c> run proposed for a
/// series — the four texts, the D3 structure block and the outline of the episode each idea would become.
/// </summary>
/// <remarks>
/// <para>
/// The three foreign keys are the same-owner composites of 09 W3 CR-46 (see <see cref="SeriesConfiguration"/>),
/// so the database itself refuses an idea of user B that points at user A's series, run or episode. The series
/// key cascades: an idea is a series-level row and plan §8 says the table is deleted with its series, the way
/// <c>series_bibles</c> is (D19). The run key cascades for the same reason <c>source_candidates</c>' run key
/// does — the row is the product of that paid call and its column is NOT NULL, so it cannot outlive its parent.
/// The episode key is nullable and <c>SET NULL</c>: the idea is the run log's record of the pick (plan §5), so
/// deleting the episode it became must not delete the idea, and a plain <c>SET NULL</c> — which would also try
/// to null the NOT NULL <c>user_id</c> — is replaced by the migration's raw SQL with the column-list form
/// <c>ON DELETE SET NULL (episode_id)</c>, as <c>InitialCore</c> and <c>02_AddSeriesBible</c> do for the
/// schema's other nullable same-owner keys.
/// </para>
/// <para>
/// <c>structure</c> (NOT NULL) and <c>novelty</c> (NULL until 02.8.1-T2 scores the row) are the canonical
/// documents of <see cref="IdeaJson"/> — snake_case, no indentation, the default (non-relaxed) encoder — and
/// each property carries its own <see cref="ValueConverter{TModel,TProvider}"/>, so no model-wide convention
/// has to know these two document shapes (CODE-9). Each also carries its own
/// <see cref="ValueComparer{T}"/>, because the payloads are immutable records whose lists compare by
/// reference by default: without it a replaced document would look unchanged and the UPDATE would be skipped
/// (the fact-sheet and series-bible precedents). The serialiser they call is the one <see cref="IdeaJson"/>
/// owns — the document plan §3.3 names <c>IdeaJsonContext</c> for, whose settings are these ones — so a
/// stored row and a round-tripped one cannot drift.
/// </para>
/// <para>
/// <c>status</c> is the D8 enum pair — snake_case text plus the generated <c>ck_episode_ideas_status</c>
/// listing <c>proposed</c>/<c>picked</c>/<c>rejected</c>/<c>used</c> (the plan's value set), exactly as
/// <c>generation_runs.kind</c> and <c>source_candidates.status</c> are mapped; the closed set makes the
/// width the plan's <c>varchar(16)</c> names unreachable, and the convention's <c>text</c> is what every
/// sibling enum column carries.
/// </para>
/// <para>
/// Every property is <c>ValueGeneratedNever</c>: <see cref="EpisodeIdea.CreateProposed"/> supplies each value
/// (a UUIDv7 id, the timestamps, <c>proposed</c>), and the audit interceptor re-stamps the two timestamps.
/// There is no <c>xmin</c> concurrency token: <see cref="EpisodeIdea"/> is an
/// <see cref="NightTales.Domain.Common.Entity"/>, not an aggregate root, so no <c>AggregateRootConvention</c>
/// token applies — the state machine of the class guards every transition instead.
/// </para>
/// </remarks>
internal sealed class EpisodeIdeaConfiguration : IEntityTypeConfiguration<EpisodeIdea>
{
    /// <summary>
    /// The list index (plan §3.1): the run log and 02.8.1-T2's corpus read a series' ideas by status,
    /// newest first.
    /// </summary>
    internal const string SeriesStatusCreatedIndex = "IX_episode_ideas_series_status_created";

    /// <summary>The partial unique index of plan §3.1: one idea per episode, whatever its status.</summary>
    internal const string EpisodeIndex = "UX_episode_ideas_episode";

    /// <summary>The traceability index of plan §3.1: the ideas one paid call produced (plan §5's run log).</summary>
    internal const string GenerationRunIndex = "IX_episode_ideas_generation_run";

    /// <summary>The predicate of <see cref="EpisodeIndex"/>: only rows that already became an episode.</summary>
    internal const string EpisodeIndexFilter = "\"episode_id\" IS NOT NULL";

    /// <summary>
    /// The four text widths, taken from <see cref="IdeaLimits"/>' plan defaults rather than restated: the
    /// column has to hold whatever the run persists, and the validator's configured limits may only tighten
    /// the plan's numbers, never widen the column.
    /// </summary>
    private static readonly IdeaLimits ColumnWidths = new();

    /// <summary>
    /// The comparer of the <c>structure</c> payload: EF's default comparer for an <see cref="IdeaStructure"/>
    /// is reference equality, and its <c>Beats</c> list would compare by reference even as a value, so a save
    /// that replaced the payload would not be seen as a change. The canonical JSON is the document's value,
    /// so the comparison — and the snapshot EF takes of it — is taken over that string.
    /// </summary>
    private static readonly ValueComparer<IdeaStructure> StructureComparer =
        new ValueComparer<IdeaStructure>(
            (left, right) => Canonical(left) == Canonical(right),
            structure => Canonical(structure).GetHashCode(StringComparison.Ordinal),
            structure => Copy(structure));

    /// <summary>
    /// The comparer of the <c>novelty</c> payload, for exactly the same reason: storing the measurements of
    /// the pick must be visible to change tracking. The property is nullable, so the comparer is the
    /// non-nullable one over the document and the null branch is EF's own (as for
    /// <c>series_bibles.proposed_content</c>).
    /// </summary>
    private static readonly ValueComparer<IdeaNoveltyDetail> NoveltyComparer =
        new ValueComparer<IdeaNoveltyDetail>(
            (left, right) => Canonical(left) == Canonical(right),
            novelty => Canonical(novelty).GetHashCode(StringComparison.Ordinal),
            novelty => Copy(novelty));

    public void Configure(EntityTypeBuilder<EpisodeIdea> builder)
    {
        builder.ToTable("episode_ideas");
        builder.HasKey(idea => idea.Id);

        builder.Property(idea => idea.Title).HasMaxLength(ColumnWidths.TitleChars);
        builder.Property(idea => idea.Logline).HasMaxLength(ColumnWidths.LoglineChars);
        builder.Property(idea => idea.Hook).HasMaxLength(ColumnWidths.HookChars);
        builder.Property(idea => idea.Setting).HasMaxLength(ColumnWidths.SettingChars);
        builder.Property(idea => idea.RejectedReason).HasMaxLength(EpisodeIdea.RejectedReasonMaxChars);
        // D8: snake_case text (proposed/picked/rejected/used) plus the generated ck_episode_ideas_status.
        builder.Property(idea => idea.Status).HasEnumCheck();
        builder.Property(idea => idea.Score).HasPrecision(5, 4);
        builder.Property(idea => idea.Similarity).HasPrecision(5, 4);
        builder.Property(idea => idea.CreatedAt).HasColumnType("timestamptz");
        builder.Property(idea => idea.UpdatedAt).HasColumnType("timestamptz");

        builder.Property(idea => idea.Structure)
            .HasConversion(new EpisodeIdeaStructureJsonConverter())
            .HasColumnType("jsonb")
            .IsRequired()
            .Metadata.SetValueComparer(StructureComparer);

        // A proposed idea has no measurements yet, so it stores SQL NULL and the converter only ever sees a
        // document (02.8.1-T2 fills the column).
        builder.Property(idea => idea.Novelty)
            .HasConversion(new EpisodeIdeaNoveltyJsonConverter())
            .HasColumnType("jsonb")
            .Metadata.SetValueComparer(NoveltyComparer);

        // 09 W3 CR-46: the same-owner keys, each onto the parent's (id, user_id) alternate key.
        builder.HasOne<Series>().WithMany()
            .HasForeignKey(idea => new { idea.SeriesId, idea.UserId })
            .HasPrincipalKey(series => new { series.Id, series.UserId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<GenerationRun>().WithMany()
            .HasForeignKey(idea => new { idea.GenerationRunId, idea.UserId })
            .HasPrincipalKey(run => new { run.Id, run.UserId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Episode>().WithMany()
            .HasForeignKey(idea => new { idea.EpisodeId, idea.UserId })
            .HasPrincipalKey(episode => new { episode.Id, episode.UserId })
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(idea => idea.UserId).OnDelete(DeleteBehavior.Restrict);

        // "The run log of a series, newest first" (plan §5) and 02.8.1-T2's corpus of pending ideas.
        builder.HasIndex(idea => new { idea.SeriesId, idea.Status, idea.CreatedAt })
            .HasDatabaseName(SeriesStatusCreatedIndex)
            .IsDescending(false, false, true);

        // "One idea per episode" (plan §3.1): the second call of MarkUsedAsync loses the race here, which the
        // command answers as 409 idea_invalid_state.
        builder.HasIndex(idea => idea.EpisodeId)
            .HasDatabaseName(EpisodeIndex)
            .IsUnique()
            .HasFilter(EpisodeIndexFilter);

        // Which paid call produced this idea (plan §3.1 traceability, plan §5's run-log projection).
        builder.HasIndex(idea => idea.GenerationRunId)
            .HasDatabaseName(GenerationRunIndex);

        // The domain factory supplies every value (UUIDv7 id, proposed status, no episode, no measurements).
        foreach (var property in builder.Metadata.GetProperties())
        {
            property.ValueGenerated = ValueGenerated.Never;
        }
    }

    /// <summary>
    /// The canonical JSON of a structure payload, or the empty string for a missing one: EF's comparer
    /// expressions take a nullable value (<c>series_bibles.content</c> is the same shape).
    /// </summary>
    private static string Canonical(IdeaStructure? structure) =>
        structure is null ? string.Empty : IdeaJson.Serialize(structure);

    /// <summary>The canonical JSON of a novelty payload, or the empty string for a missing one.</summary>
    private static string Canonical(IdeaNoveltyDetail? novelty) =>
        novelty is null ? string.Empty : IdeaJson.Serialize(novelty);

    /// <summary>A value-equal copy of a structure payload: what EF keeps as the snapshot of the loaded value.</summary>
    private static IdeaStructure Copy(IdeaStructure structure) =>
        IdeaJson.DeserializeStructure(IdeaJson.Serialize(structure));

    /// <summary>A value-equal copy of a novelty payload.</summary>
    private static IdeaNoveltyDetail Copy(IdeaNoveltyDetail? novelty) =>
        novelty is null
            ? null!
            : IdeaJson.DeserializeNovelty(IdeaJson.Serialize(novelty))
              ?? throw new InvalidOperationException("A serialised novelty document is never null.");
}

/// <summary>
/// Writes and reads <c>episode_ideas.structure</c> as the canonical document of <see cref="IdeaJson"/>
/// (02.8.1 plan §3.1, D3): the four vocabulary tags, the narrator mode, the beat outline and its signature.
/// </summary>
internal sealed class EpisodeIdeaStructureJsonConverter : ValueConverter<IdeaStructure, string>
{
    /// <summary>Creates the converter with the canonical settings of <see cref="IdeaJson"/>.</summary>
    public EpisodeIdeaStructureJsonConverter()
        : base(structure => Serialize(structure), json => Parse(json))
    {
    }

    private static string Serialize(IdeaStructure structure) => IdeaJson.Serialize(structure);

    private static IdeaStructure Parse(string json) => IdeaJson.DeserializeStructure(json);
}

/// <summary>
/// Writes and reads <c>episode_ideas.novelty</c> as the canonical document of <see cref="IdeaJson"/>
/// (02.8.1 plan §3.1): the lexical pass, the judge call and the corpus both measured against.
/// </summary>
internal sealed class EpisodeIdeaNoveltyJsonConverter : ValueConverter<IdeaNoveltyDetail?, string>
{
    /// <summary>Creates the converter with the canonical settings of <see cref="IdeaJson"/>.</summary>
    public EpisodeIdeaNoveltyJsonConverter()
        : base(novelty => Serialize(novelty), json => Parse(json))
    {
    }

    /// <summary>
    /// The canonical document of a novelty payload. EF never converts a null (<c>ConvertsNulls</c> is false: a
    /// proposed idea keeps a SQL NULL), so the null branch is an unreachable guard.
    /// </summary>
    private static string Serialize(IdeaNoveltyDetail? novelty) =>
        novelty is null
            ? throw new InvalidOperationException("A persisted novelty document is never null: the column is NULL or a document.")
            : IdeaJson.Serialize(novelty);

    private static IdeaNoveltyDetail? Parse(string json) => IdeaJson.DeserializeNovelty(json);
}
