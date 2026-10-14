using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Episodes;
using NightTales.Domain.Research;
using NightTales.Domain.Stories.Generation;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>fact_sheets</c> table (02.5.3 plan §3, 02 README D21): the versioned, deterministic fact drafts of
/// one episode, one row per version.
/// </summary>
/// <remarks>
/// <para>
/// The same-owner foreign key to the episode is the composite <c>(episode_id, user_id)</c> (09 W3 CR-46, see
/// <see cref="SeriesConfiguration"/>) and cascades, so a deleted episode takes its drafts with it (D19). The
/// alternate key <c>(id, user_id)</c> is what the three deferred fact-sheet foreign keys of 02 CR-29 target
/// (<c>generation_runs.input_fact_sheet_id</c>, <c>generation_runs.result_fact_sheet_id</c> and
/// <c>story_versions.fact_sheet_id</c>); they are declared on those two configurations, each with
/// <c>ON DELETE SET NULL</c> over the sheet column only.
/// </para>
/// <para>
/// <c>status</c> is the plan's <c>smallint</c> and not the D8 snake_case text pair: the closed set is
/// <see cref="FactSheetStatus"/> (<c>0/1/2</c>) plus <c>ck_fact_sheets_status</c>, and the partial unique
/// <c>ux_fact_sheets_episode_approved</c> — "at most one approved row per episode" (plan §3 invariant 4, T54)
/// — is <c>WHERE status = 1</c>, which is what makes the approve-and-supersede transaction safe.
/// </para>
/// <para>
/// <c>facts</c> and <c>source_aliases</c> are the canonical JSON documents of
/// <see cref="FactSheetJson"/>: each property carries its own
/// <see cref="FactSheetFactsJsonConverter"/>/<see cref="FactSheetAliasesJsonConverter"/> conversion, so the
/// stored bytes are exactly the bytes <c>content_sha256</c> hashes and no model-wide convention has to know
/// these two document shapes. Each of the two also carries its own <see cref="ValueComparer{T}"/> (02.5.4-T1),
/// because the edit and approve paths replace those documents and reference equality would hide the change.
/// </para>
/// <para>
/// The id is <c>Guid.CreateVersion7()</c> and every other value is supplied by
/// <see cref="FactSheet.CreateDraft"/>, so every property is <c>ValueGeneratedNever</c>. There is no
/// <c>xmin</c> concurrency token: the plan fixes the ETag at <c>fs-&lt;id&gt;</c> (the row id, D9), and
/// <see cref="FactSheet"/> is an <see cref="NightTales.Domain.Common.Entity"/> like <c>StorySource</c> and
/// <c>GenerationRun</c>, so no <c>AggregateRootConvention</c> token applies.
/// </para>
/// </remarks>
internal sealed class FactSheetConfiguration : IEntityTypeConfiguration<FactSheet>
{
    /// <summary>The partial unique index predicate: exactly the approved <see cref="FactSheetStatus"/>.</summary>
    internal const string ApprovedStatusFilter = "\"status\" = 1";

    /// <summary>The <c>ck_fact_sheets_status</c> value set: draft, approved, superseded.</summary>
    internal const string StatusCheck = "\"status\" IN (0, 1, 2)";

    /// <summary>
    /// The element-wise comparer of the <c>facts</c> document: EF's default comparer for the list is reference
    /// equality, so without this a replaced document would look unchanged. The comparison walks every member of
    /// each <see cref="FactItem"/> by value, because the record's own equality compares its two list members by
    /// reference. Change-tracking state only — it changes no column, no index and no snapshot entry.
    /// </summary>
    private static readonly ValueComparer<IReadOnlyList<FactItem>> FactsComparer =
        new ValueComparer<IReadOnlyList<FactItem>>(
            (left, right) => FactsEqual(left, right),
            facts => FactsHash(facts),
            facts => CopyFacts(facts));

    /// <summary>
    /// The pair-wise comparer of the <c>source_aliases</c> document, deliberately insensitive to the order the
    /// map enumerates in (the canonical JSON writes insertion order, the mapper may hand over another one).
    /// </summary>
    private static readonly ValueComparer<IReadOnlyDictionary<string, Guid>> SourceAliasesComparer =
        new ValueComparer<IReadOnlyDictionary<string, Guid>>(
            (left, right) => SourceAliasesEqual(left, right),
            aliases => SourceAliasesHash(aliases),
            aliases => CopySourceAliases(aliases));

    public void Configure(EntityTypeBuilder<FactSheet> builder)
    {
        builder.ToTable("fact_sheets");
        builder.HasKey(sheet => sheet.Id);

        builder.Property(sheet => sheet.Version).IsRequired();
        // smallint + CHECK rather than the D8 text pair: plan §3 fixes the storage of `status`.
        builder.Property(sheet => sheet.Status).HasConversion<short>().HasColumnType("smallint").IsRequired();
        builder.Property(sheet => sheet.InputTruncated).IsRequired();
        builder.Property(sheet => sheet.MaxFactId).IsRequired();
        builder.Property(sheet => sheet.ContentSha256).HasColumnType("char(64)").IsRequired();
        builder.Property(sheet => sheet.PromptVersion).HasMaxLength(512).IsRequired();
        builder.Property(sheet => sheet.CreatedAt).HasColumnType("timestamptz");
        builder.Property(sheet => sheet.UpdatedAt).HasColumnType("timestamptz");
        builder.Property(sheet => sheet.ApprovedAt).HasColumnType("timestamptz");

        // The canonical documents (see the class remarks): each conversion is declared on its own property, so
        // the shared conventions stay free of the two document types (CODE-9: a model-wide conversion for
        // `IReadOnlyList<FactItem>` would silently capture any future entity with that shape).
        //
        // Each converted property also carries its own ValueComparer: the default comparer for a reference type
        // is reference equality, so a command that replaces the document in place would not be seen as a change
        // and the UPDATE would be skipped. A comparer is runtime change-tracking state only — it is not part of
        // the model snapshot and changes no migration.
        builder.Property(sheet => sheet.Facts)
            .HasConversion(new FactSheetFactsJsonConverter())
            .HasColumnType("jsonb")
            .IsRequired()
            .Metadata.SetValueComparer(FactsComparer);
        builder.Property(sheet => sheet.SourceAliases)
            .HasConversion(new FactSheetAliasesJsonConverter())
            .HasColumnType("jsonb")
            .IsRequired()
            .Metadata.SetValueComparer(SourceAliasesComparer);

        // NTS-922-SEC-001 (09 W3 CR-46): the alternate key (id, user_id) the three CR-29 fact-sheet FKs
        // target, and the same-owner FK of this table (see SeriesConfiguration).
        builder.HasAlternateKey(sheet => new { sheet.Id, sheet.UserId });
        builder.HasOne<Episode>().WithMany()
            .HasForeignKey(sheet => new { sheet.EpisodeId, sheet.UserId })
            .HasPrincipalKey(episode => new { episode.Id, episode.UserId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<GenerationRun>().WithMany()
            .HasForeignKey(sheet => new { sheet.GenerationRunId, sheet.UserId })
            .HasPrincipalKey(run => new { run.Id, run.UserId })
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(sheet => sheet.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(sheet => sheet.UserId).OnDelete(DeleteBehavior.Restrict);

        // One row per (episode, version): the version counter the create path reads and the D9 ETag the
        // update path matches.
        builder.HasIndex(sheet => new { sheet.EpisodeId, sheet.Version })
            .HasDatabaseName("ux_fact_sheets_episode_version")
            .IsUnique();

        // At most one approved row per episode (plan §3 invariant 4, T54). The filter is the numeric status
        // because the column is a smallint, not the D8 text pair.
        builder.HasIndex(sheet => sheet.EpisodeId)
            .HasDatabaseName("ux_fact_sheets_episode_approved")
            .IsUnique()
            .HasFilter(ApprovedStatusFilter);

        // The provenance lookups (`fact_sheet_conflict` 412, the D21 writer read): EF names the composite
        // (generation_run_id, user_id) index itself; this single-column one is the story_versions precedent.
        builder.HasIndex(sheet => sheet.GenerationRunId).HasDatabaseName("ix_fact_sheets_generation_run_id");

        builder.ToTable(table =>
        {
            table.HasCheckConstraint("ck_fact_sheets_status", StatusCheck);
            table.HasCheckConstraint(
                "ck_fact_sheets_sha256",
                "\"content_sha256\" ~ '^[0-9a-f]{64}$'");
        });

        // The domain factory supplies every value (UUIDv7 id, the version, the hash, the run id).
        foreach (var property in builder.Metadata.GetProperties())
        {
            property.ValueGenerated = ValueGenerated.Never;
        }
    }

    /// <summary>
    /// Compares two facts documents element by element, in output order. <see cref="FactItem"/> is a record whose
    /// <c>SourceIds</c> and <c>SourceAliases</c> are lists, and a record's generated equality compares those
    /// members by reference, so <c>SequenceEqual</c> over the records would call a reloaded document different
    /// from the snapshot EF took of it and every save would rewrite the column (D9 forbids that: the stored bytes
    /// are what <c>content_sha256</c> hashes). Every member is therefore compared by value.
    /// </summary>
    private static bool FactsEqual(IReadOnlyList<FactItem>? left, IReadOnlyList<FactItem>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!FactEqual(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Compares two facts member by member, both inner lists included.</summary>
    private static bool FactEqual(FactItem? left, FactItem? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left is not null
            && right is not null
            && string.Equals(left.Id, right.Id, StringComparison.Ordinal)
            && string.Equals(left.Statement, right.Statement, StringComparison.Ordinal)
            && ListEqual(left.SourceIds, right.SourceIds)
            && ListEqual(left.SourceAliases, right.SourceAliases)
            && left.SourceOverlap == right.SourceOverlap
            && left.Confidence == right.Confidence
            && left.Origin == right.Origin;
    }

    /// <summary>Element-wise list equality with the element type's default comparer.</summary>
    private static bool ListEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Hashes a facts document over the very members <see cref="FactsEqual"/> compares, in the same order, so two
    /// facts that compare equal also hash equal.
    /// </summary>
    private static int FactsHash(IReadOnlyList<FactItem>? facts)
    {
        if (facts is null)
        {
            return 0;
        }

        var hash = new HashCode();
        foreach (var fact in facts)
        {
            if (fact is null)
            {
                hash.Add(0);
                continue;
            }

            hash.Add(fact.Id, StringComparer.Ordinal);
            hash.Add(fact.Statement, StringComparer.Ordinal);
            ListHash(ref hash, fact.SourceIds);
            ListHash(ref hash, fact.SourceAliases);
            hash.Add(fact.SourceOverlap);
            hash.Add(fact.Confidence);
            hash.Add(fact.Origin);
        }

        return hash.ToHashCode();
    }

    /// <summary>Feeds <paramref name="values"/> into <paramref name="hash"/> in order.</summary>
    private static void ListHash<T>(ref HashCode hash, IReadOnlyList<T>? values)
    {
        if (values is null)
        {
            hash.Add(0);
            return;
        }

        foreach (var value in values)
        {
            hash.Add(value);
        }
    }

    /// <summary>
    /// Copies the facts document for the change-tracking snapshot: a new list of copies of the facts, each
    /// owning its own copies of the two inner lists, so the snapshot cannot be reached by a later edit of the
    /// document (the <see cref="FactItem"/> record itself is immutable).
    /// </summary>
    private static List<FactItem> CopyFacts(IReadOnlyList<FactItem>? facts)
    {
        if (facts is null)
        {
            return [];
        }

        var copies = new List<FactItem>(facts.Count);
        foreach (var fact in facts)
        {
            copies.Add(fact is null
                ? null!
                : fact with { SourceIds = [.. fact.SourceIds], SourceAliases = [.. fact.SourceAliases] });
        }

        return copies;
    }

    /// <summary>Compares two alias maps pair by pair, ignoring the order they enumerate in.</summary>
    private static bool SourceAliasesEqual(
        IReadOnlyDictionary<string, Guid>? left,
        IReadOnlyDictionary<string, Guid>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var sourceId) || sourceId != pair.Value)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Hashes an alias map order-insensitively (XOR of the per-entry hashes), so equal maps agree on the hash
    /// however they enumerate.
    /// </summary>
    private static int SourceAliasesHash(IReadOnlyDictionary<string, Guid>? aliases)
    {
        if (aliases is null)
        {
            return 0;
        }

        var hash = 0;
        foreach (var pair in aliases)
        {
            hash ^= HashCode.Combine(pair.Key, pair.Value);
        }

        return hash;
    }

    /// <summary>Copies the alias map into an ordinal-keyed dictionary for the change-tracking snapshot.</summary>
    private static Dictionary<string, Guid> CopySourceAliases(
        IReadOnlyDictionary<string, Guid>? aliases) =>
        aliases is null
            ? new Dictionary<string, Guid>(StringComparer.Ordinal)
            : new Dictionary<string, Guid>(aliases, StringComparer.Ordinal);
}
