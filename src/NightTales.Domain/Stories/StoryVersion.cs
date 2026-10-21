using NightTales.Domain.Common;
using NightTales.Domain.Stories.Content;

namespace NightTales.Domain.Stories;

/// <summary>
/// One immutable story version of an episode (00 overview §6 <c>story_versions</c>). 09.2.2-T1 owns the base
/// columns of plan §3; 02.2.2-T1 adds the CR-4 provenance columns (<see cref="ParentVersionId"/>,
/// <see cref="GenerationRunId"/>, <see cref="FactSheetId"/>, <see cref="SchemaVersion"/>), the factory and the
/// D3 derivation. Story content is append-only, so it stops at <see cref="ICreatedAt"/>. The explicit
/// exception is <see cref="RefreshModerationVerdict"/>, which refreshes the audit report at approval
/// without rewriting content; only <c>Features/Admin</c> (09.9.1) may delete a row.
/// </summary>
public sealed class StoryVersion : Entity, IUserOwned, ICreatedAt
{
    /// <summary>The <c>schema_version</c> every new row carries until a <c>story.v2</c> exists.</summary>
    public const string CurrentSchemaVersion = "story.v1";

    private readonly List<string> _keywords = [];

    private StoryVersion()
    {
    }

    public Guid UserId { get; private set; }

    public Guid EpisodeId { get; private set; }

    public int Version { get; private set; }

    public StoryVersionSource Source { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Body { get; private set; } = string.Empty;

    /// <summary>
    /// The keywords extracted for this version (plan §3 <c>keywords text[]</c>). The row is append-only,
    /// so the list is read-only: only the factory writes <c>_keywords</c>, which EF maps through field
    /// access. The view is built per access because EF replaces the field when it materializes a row.
    /// </summary>
    public IReadOnlyList<string> Keywords => _keywords.AsReadOnly();

    public int WordCount { get; private set; }

    public string? Model { get; private set; }

    public string? PromptVersion { get; private set; }

    /// <summary>The <c>content</c> JSON document (<c>story.v1</c>, plus the backend-built fact sources).</summary>
    public string Content { get; private set; } = string.Empty;

    /// <summary>The <c>audit_report</c> JSON document (<c>audit.v1</c>, 02 D22).</summary>
    public string? AuditReport { get; private set; }

    public decimal? SimilarityScore { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The version this one was revised from (02.6.2); null until a revision writes one.</summary>
    public Guid? ParentVersionId { get; private set; }

    /// <summary>The run that produced the version; null for <see cref="StoryVersionSource.Edited"/> (02.3.1).</summary>
    public Guid? GenerationRunId { get; private set; }

    /// <summary>The approved fact sheet the writer saw, for a true-crime episode.</summary>
    public Guid? FactSheetId { get; private set; }

    /// <summary>The <c>content</c> document's schema, <c>story.v1</c> today.</summary>
    public string SchemaVersion { get; private set; } = CurrentSchemaVersion;

    /// <summary>Refreshes the approval-time audit report without changing the append-only story content.</summary>
    public void RefreshModerationVerdict(string auditReportJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditReportJson);
        AuditReport = auditReportJson;
    }

    /// <summary>
    /// Derives the D3 fields — <c>body</c>, <c>keywords</c>, <c>word_count</c> — from a draft. The factory uses
    /// exactly this, so what is stored is what the validator counted.
    /// </summary>
    /// <param name="content">The draft as the writer returned it.</param>
    /// <returns>The derived body, keywords and word count.</returns>
    public static DerivedStoryFields Derive(StoryContentV1 content) => DerivedStoryFields.From(content);

    /// <summary>
    /// Creates the next version of an episode. The caller computes <paramref name="version"/> as
    /// <c>max(version) + 1</c> inside the insert transaction under the episode row lock (02.2.2 plan §4 U6);
    /// the unique <c>(episode_id, version)</c> index turns a lost race into an error, never a duplicate.
    /// </summary>
    /// <param name="userId">The owner, from the loaded episode.</param>
    /// <param name="episodeId">The episode the version belongs to.</param>
    /// <param name="version">The 1-based version number.</param>
    /// <param name="source">How the version came to be.</param>
    /// <param name="content">The draft; title, body, keywords and word count are derived from it.</param>
    /// <param name="contentJson">
    /// The stored <c>content</c> document: the draft plus the <c>schema_version</c> and <c>fact_sources</c> the
    /// persistence layer adds (02 D3).
    /// </param>
    /// <param name="model">The model id that wrote it, when a model did.</param>
    /// <param name="promptVersion">The D5 composite prompt version, when a model wrote it.</param>
    /// <param name="generationRunId">The producing run; must be null for <see cref="StoryVersionSource.Edited"/>.</param>
    /// <param name="parentVersionId">The version this one was revised from, when it was.</param>
    /// <param name="factSheetId">The fact sheet the writer saw, when there was one.</param>
    /// <param name="auditReportJson">The <c>audit.v1</c> document (02 D22), when the job wrote one.</param>
    /// <param name="similarityScore">The source-similarity score in [0, 1), when computed.</param>
    /// <param name="now">The caller's clock reading, stored as <see cref="CreatedAt"/>.</param>
    /// <returns>The new version, to be added to the context; its story content is never modified again.</returns>
    /// <exception cref="ArgumentException">An id is empty, the version is below 1, the JSON is empty or an edit names a run.</exception>
    public static StoryVersion Create(
        Guid userId,
        Guid episodeId,
        int version,
        StoryVersionSource source,
        StoryContentV1 content,
        string contentJson,
        string? model,
        string? promptVersion,
        Guid? generationRunId,
        Guid? parentVersionId,
        Guid? factSheetId,
        string? auditReportJson,
        decimal? similarityScore,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentJson);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);

        if (userId == Guid.Empty || episodeId == Guid.Empty)
        {
            throw new ArgumentException("A story version needs an owner and an episode.", nameof(userId));
        }

        if (source == StoryVersionSource.Edited && generationRunId is not null)
        {
            throw new ArgumentException("An edited version has no generation run.", nameof(generationRunId));
        }

        var derived = Derive(content);
        var created = new StoryVersion
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            EpisodeId = episodeId,
            Version = version,
            Source = source,
            Title = content.Title,
            Body = derived.Body,
            WordCount = derived.WordCount,
            Model = model,
            PromptVersion = promptVersion,
            Content = contentJson,
            AuditReport = auditReportJson,
            SimilarityScore = similarityScore,
            ParentVersionId = parentVersionId,
            GenerationRunId = generationRunId,
            FactSheetId = factSheetId,
            CreatedAt = now,
        };

        created._keywords.AddRange(derived.Keywords);

        return created;
    }
}
