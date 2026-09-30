using NightTales.Domain.Common;

namespace NightTales.Domain.Catalog;

/// <summary>
/// A curated voice of the 04 catalog (00 overview §6 <c>voices</c>). Not an <see cref="Entity"/>: the
/// primary key is the canonical VieNeu preset name, a <c>string</c> (09 W3 CR-41), and the table is not
/// user-owned. The 09.2.5-T1 seeder upserts rows through <see cref="Create"/>.
/// </summary>
public sealed class Voice : ICreatedAt
{
    private Voice()
    {
    }

    /// <summary>The canonical voice id (04 D9 rules: NFC, 1–64 code points, no aliases).</summary>
    public string Id { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public Language Language { get; private set; }

    public VoiceGender Gender { get; private set; }

    public List<string> Tags { get; private set; } = [];

    public string? PreviewStorageKey { get; private set; }

    public bool Active { get; private set; }

    /// <summary>Stamped by <c>AuditableSaveChangesInterceptor</c> on insert (plan §3 preamble).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Creates a catalog row; the 09.2.5-T1 seeder upserts by <paramref name="id"/>.</summary>
    public static Voice Create(
        string id,
        string displayName,
        Language language,
        VoiceGender gender,
        IReadOnlyCollection<string>? tags = null,
        string? previewStorageKey = null,
        bool active = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        return new Voice
        {
            Id = id,
            DisplayName = displayName,
            Language = language,
            Gender = gender,
            Tags = tags is null ? [] : [.. tags],
            PreviewStorageKey = previewStorageKey,
            Active = active,
        };
    }

    /// <summary>
    /// Rewrites the catalog-owned columns of an existing row (09.2.5 plan §3.2); the 09.2.5-T1 seeder
    /// upserts by <see cref="Id"/>. <see cref="PreviewStorageKey"/> is deliberately not part of the dev
    /// catalog of this task (04.2.2-T2 adds it with the full voice file).
    /// </summary>
    public void UpdateFromCatalog(
        string displayName,
        Language language,
        VoiceGender gender,
        IReadOnlyCollection<string>? tags,
        bool active)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        DisplayName = displayName;
        Language = language;
        Gender = gender;
        Tags = tags is null ? [] : [.. tags];
        Active = active;
    }
}
