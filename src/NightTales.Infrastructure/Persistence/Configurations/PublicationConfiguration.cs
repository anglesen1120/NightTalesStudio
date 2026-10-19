using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using NightTales.Domain.Episodes;
using NightTales.Domain.Publishing;
using NightTales.Domain.Series.Autopilot;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>publications</c> table (06.2.1-T1, 06 README D6/D18/D20): one row per platform post of one episode —
/// the metadata YouTube is given, the consent that allowed it, and where the upload stands.
/// </summary>
/// <remarks>
/// <para>
/// <c>status</c> is the D8 enum pair: snake_case text with <c>ck_publications_status</c> emitted by
/// <c>HasEnumCheck</c>. <c>consent_source</c> is the one exception — the aggregate keeps the wire string
/// (<c>user</c>/<c>autopilot</c>), not the enum — so it is a nullable <c>text</c> with the hand-written
/// <c>ck_publications_consent_source</c> beside it, exactly as <c>platform_quota_usage</c> hand-writes its own.
/// <c>tags</c> is the <c>text[]</c> primitive collection with D20's <c>'{}'</c> default, and
/// <c>platform_payload</c> is the <c>jsonb</c> document of <see cref="Publication.PlatformPayloadJson"/> — the
/// uploaded session, the pre-audit stamp and 07's TikTok settings — kept as opaque text like
/// <c>render_jobs.quality_report</c>.
/// </para>
/// <para>
/// <b>Two columns have no CLR member to map.</b> <c>Publication.Platform</c> and
/// <c>Publication.SelfDeclaredMadeForKids</c> are computed constants of the aggregate (<c>"youtube"</c> and
/// <c>false</c>, D2/D12), so this file ignores both and they arrive as shadow properties named with the column's
/// own spelling — never <c>Platform</c>/<c>SelfDeclaredMadeForKids</c>, which are the computed members — so the
/// D20 columns exist, <c>ux_publications_active_episode_platform</c> can
/// index <c>platform</c>, and a later 07 or 06.2.x task can promote either to a real property without a
/// migration. <c>platform</c> is <c>NOT NULL</c> and D20 gives it no store default, so
/// <see cref="PublicationPlatformGenerator"/> supplies the value on insert; <c>self_declared_made_for_kids</c>
/// carries D20's <c>DEFAULT false</c> and is never written, because every row is <c>false</c>.
/// </para>
/// <para>
/// The four indexes are README D18's. <c>ux_publications_active_episode_platform</c> is the partial unique index
/// that makes "one live publication per episode and platform" a storage fact and is also the arbiter the create
/// command translates to 409 <c>publication_invalid_state</c>; its <c>WHERE status NOT IN ('failed','cancelled')</c>
/// predicate is what lets a terminal row's episode be published again (D6). The
/// <c>(status, next_attempt_at)</c> index is the upload queue's claim order and is filtered to the four
/// non-terminal-before-upload states, so the poller never walks the published ones;
/// <c>ix_publications_channel_connection_id</c> serves D15's cascade-cancel and 06.4.3's scrub of one connection's
/// rows; <c>(user_id, created_at DESC)</c> serves the owner's own listing.
/// </para>
/// <para>
/// The foreign keys are D20's, with one structural difference the security rule demands (NTS-922-SEC-001,
/// 09 W3 CR-46): the episode is reached through its alternate key <c>(id, user_id)</c>, the same-owner composite
/// every other child table of an episode uses (<c>story_versions</c>, <c>render_jobs</c>, <c>source_candidates</c>),
/// so a publication can never name another tenant's episode while still cascading with the episode exactly as
/// D20's <c>ON DELETE CASCADE</c> requires. A connection refuses the delete (<c>RESTRICT</c>) because D15 cancels
/// its publications first; a removed authorization just drops the reference (<c>SET NULL</c>).
/// </para>
/// <para>
/// <c>Version</c> is the <c>xmin</c> row version (09 README D8, N8) mapped by <c>AggregateRootConvention</c> — it
/// is what <c>ETags.Publication</c> publishes as <c>"pu-&lt;xmin&gt;"</c> — the tenant filter is
/// <c>TenantFilterConvention</c>'s because the aggregate is <see cref="NightTales.Domain.Common.IUserOwned"/>, the
/// audit timestamps are <c>AuditTimestampConvention</c>'s, and the id's <c>ValueGeneratedNever</c> is
/// <c>EntityIdConvention</c>'s: this file declares none of the five.
/// </para>
/// </remarks>
internal sealed class PublicationConfiguration : IEntityTypeConfiguration<Publication>
{
    /// <summary>The values <c>ck_publications_platform</c> accepts: 06's platform, and 07's once it ships.</summary>
    internal const string PlatformCheck = "platform IN ('youtube','tiktok')";

    /// <summary>The values <c>ck_publications_privacy</c> accepts (G6).</summary>
    internal const string PrivacyCheck = "privacy IN ('public','unlisted','private')";

    /// <summary>The values <c>ck_publications_consent_source</c> accepts; a <c>NULL</c> source is unconsented and passes.</summary>
    internal const string ConsentSourceCheck = "consent_source IN ('user','autopilot')";

    /// <summary>The partial predicate of <c>ux_publications_active_episode_platform</c>: only live rows compete.</summary>
    internal const string ActiveRowFilter = "status NOT IN ('failed','cancelled')";

    /// <summary>The states the upload queue claims, in the order D18 lists them.</summary>
    internal const string QueueableStatuses =
        "status IN ('awaiting_consent','scheduled','uploading','processing')";

    /// <summary>The one active publication per <c>(episode_id, platform)</c> (README D6/D18).</summary>
    internal const string ActiveEpisodePlatformIndexName = "ux_publications_active_episode_platform";

    /// <summary>The upload queue's claim order: the oldest attempt of a queueable row first (README D18).</summary>
    internal const string StatusNextAttemptIndexName = "ix_publications_status_next_attempt_at";

    /// <summary>The connection's publication scan: D15's cascade-cancel and 06.4.3's scrub (README D18).</summary>
    internal const string ChannelConnectionIndexName = "ix_publications_channel_connection_id";

    /// <summary>The owner's own listing, newest first (README D18).</summary>
    internal const string UserCreatedAtIndexName = "ix_publications_user_id_created_at";

    /// <summary>
    /// The shadow property behind the <c>platform</c> column. It is spelled like the column and not like the
    /// computed <c>Publication.Platform</c> member, so the two can never be confused for one another.
    /// </summary>
    internal const string PlatformPropertyName = "platform";

    /// <summary>
    /// The shadow property behind the <c>self_declared_made_for_kids</c> column (D12: always <c>false</c> for a
    /// synthetic-media channel, which is why the aggregate computes it and D20 defaults it).
    /// </summary>
    internal const string SelfDeclaredMadeForKidsPropertyName = "self_declared_made_for_kids";

    public void Configure(EntityTypeBuilder<Publication> builder)
    {
        builder.ToTable(
            "publications",
            table =>
            {
                table.HasCheckConstraint("ck_publications_platform", PlatformCheck);
                table.HasCheckConstraint("ck_publications_privacy", PrivacyCheck);
                table.HasCheckConstraint("ck_publications_consent_source", ConsentSourceCheck);
            });

        builder.HasKey(publication => publication.Id);

        // UUIDv7 comes from `Publication.Create` and is pinned by the aggregate, never by the database (D3/D8).
        builder.Property(publication => publication.Id).ValueGeneratedNever();

        builder.Property(publication => publication.UserId).IsRequired();
        builder.Property(publication => publication.EpisodeId).IsRequired();
        builder.Property(publication => publication.ChannelConnectionId).IsRequired();

        builder.Property(publication => publication.Status).HasEnumCheck();

        builder.Property(publication => publication.Title).IsRequired();
        builder.Property(publication => publication.Description).IsRequired();

        // `tags` — the D20 default keeps a bare insert at the empty array; `Publication.Create` always writes the
        // metadata's own list, so the default is a schema statement, not a code path.
        builder.Property(publication => publication.Tags)
            .HasColumnType("text[]")
            .IsRequired()
            .HasDefaultValueSql("'{}'::text[]");

        builder.Property(publication => publication.Privacy).IsRequired();

        builder.Property(publication => publication.ContainsSyntheticMedia).IsRequired();

        // `platform_payload` (`jsonb`, nullable): the snake-case convention would spell the property
        // `platform_payload_json`, so the D20 column name is pinned. The document is opaque here — the DTO mapper
        // of 06.2.1-T1's Application slice is what reads `preAudit`/`requestedPrivacy` out of it.
        builder.Property(publication => publication.PlatformPayloadJson)
            .HasColumnName("platform_payload")
            .HasColumnType("jsonb");

        // The two computed constants (D2/D12) are not writable state, so EF is told to leave them alone and the
        // D20 columns are mapped through the shadow properties below (their names are the column's own spelling,
        // because a shadow property named `Platform` would resolve to the computed CLR member instead).
        builder.Ignore(publication => publication.Platform);
        builder.Ignore(publication => publication.SelfDeclaredMadeForKids);

        // `platform` (D2): computed by the aggregate, required by D20 without a store default, and part of the
        // partial unique index — so the column is filled on insert by the generator below.
        builder.Property<string>(PlatformPropertyName)
            .HasColumnType("text")
            .IsRequired()
            .ValueGeneratedOnAdd()
            .HasValueGenerator<PublicationPlatformGenerator>();

        // `self_declared_made_for_kids` (D12): false for every row, so nothing ever writes it and D20's default
        // is what the column carries.
        builder.Property<bool>(SelfDeclaredMadeForKidsPropertyName)
            .HasColumnType("boolean")
            .IsRequired()
            .HasDefaultValue(false);

        // One live publication per (episode, platform): the create's arbiter and the only row a later
        // consent/cancel may act on. A failed or cancelled row leaves the index, so the episode can be
        // published again (D6).
        builder.HasIndex(nameof(Publication.EpisodeId), PlatformPropertyName)
            .HasDatabaseName(ActiveEpisodePlatformIndexName)
            .IsUnique()
            .HasFilter(ActiveRowFilter);

        // The upload queue's due-row scan (D18). The filter keeps the index to the rows a tick can claim.
        builder.HasIndex(publication => new { publication.Status, publication.NextAttemptAt })
            .HasDatabaseName(StatusNextAttemptIndexName)
            .HasFilter(QueueableStatuses);

        builder.HasIndex(publication => publication.ChannelConnectionId)
            .HasDatabaseName(ChannelConnectionIndexName);

        builder.HasIndex(publication => new { publication.UserId, publication.CreatedAt })
            .HasDatabaseName(UserCreatedAtIndexName)
            .IsDescending(false, true);

        // NTS-922-SEC-001 (09 W3 CR-46): the same-owner composite key every child table of an episode uses, so the
        // episode's hard delete still cascades this row (D20's ON DELETE CASCADE) while a publication can never
        // point at another user's episode.
        builder.HasOne<Episode>().WithMany()
            .HasForeignKey(publication => new { publication.EpisodeId, publication.UserId })
            .HasPrincipalKey(episode => new { episode.Id, episode.UserId })
            .OnDelete(DeleteBehavior.Cascade);

        // D20: the account's row goes with the account; the connection refuses the delete, because D15 cancels the
        // connection's publications first; a removed autopilot authorization only drops the reference.
        //
        // [DEVIATION from the repo's child-table convention, per the task SQL and the s66 contract] `users` is
        // CASCADE here, where the retained records of 09 (render_jobs, series_bibles, user_consents) are RESTRICT so
        // that 13.3.2's deletion engine deletes and counts them itself. The task §Data SQL says ON DELETE CASCADE for
        // users, and the contract repeats it, so the schema follows that; if the orchestrator prefers the engine to
        // own the delete, switch this to Restrict before the migration is generated (the episode composite below
        // still cascades, so an accounted-for account delete is unaffected either way).
        builder.HasOne<AppUser>().WithMany().HasForeignKey(publication => publication.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ChannelConnection>().WithMany()
            .HasForeignKey(publication => publication.ChannelConnectionId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SeriesAutopilotAuthorization>().WithMany()
            .HasForeignKey(publication => publication.AutopilotAuthorizationId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>
/// The stored value of <c>publications.platform</c> (06.2.1-T1, 06 README D2): the one platform 06 can publish
/// to, written on insert because the column is <c>NOT NULL</c> with no store default and the aggregate computes
/// its value rather than storing it.
/// </summary>
/// <remarks>
/// The value comes from <see cref="PlatformExtensions.ToWire"/>, the same source the
/// <c>ck_publications_platform</c> values and every other platform column use, so the routed value, the stored
/// value and the CHECK cannot drift. 07 promotes the column to real row state when a second platform exists.
/// </remarks>
internal sealed class PublicationPlatformGenerator : ValueGenerator<string>
{
    /// <inheritdoc />
    public override bool GeneratesTemporaryValues => false;

    /// <inheritdoc />
    public override string Next(EntityEntry entry) => Platform.YouTube.ToWire();
}
