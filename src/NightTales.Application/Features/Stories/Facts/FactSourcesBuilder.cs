using NightTales.Application.Abstractions.Research;
using NightTales.Domain.Common;
using NightTales.Domain.Research;
using NightTales.Domain.Stories.Content;

namespace NightTales.Application.Features.Stories.Facts;

/// <summary>
/// Builds the <c>fact_sources</c> array a stored <c>story_versions.content</c> carries (02 README D3, 02.5.5-T1 D2):
/// the backend resolves the draft's fact refs through the approved sheet and the persisted
/// <c>fact_sheets.source_aliases</c> map to the episode's <c>story_sources</c> rows. Nothing the model wrote selects a
/// source: a ref the sheet cannot resolve is reported, never built, so no model-written URL or source id can reach the
/// stored document (F12). It is pure and static, reads nothing and has no side effects.
/// </summary>
public static class FactSourcesBuilder
{
    /// <summary>The title a <c>user_note</c> source is listed under, per content language (the note text never is).</summary>
    /// <param name="language">The series content language.</param>
    /// <returns>The label the stored <c>fact_sources</c> entry shows for an owner's own note.</returns>
    public static string UserNoteTitle(Language language) => language == Language.Vi ? "ghi chú của bạn" : "your note";

    /// <summary>Resolves the draft's fact refs to the sources the story's facts rest on.</summary>
    /// <param name="content">The validated draft; <c>hook</c>, <c>paragraphs[]</c> and <c>ending</c> are scanned.</param>
    /// <param name="sheet">The approved sheet the run started on, its aliases and its id map.</param>
    /// <param name="sources">The episode's <c>story_sources</c> metadata, by id; absent ids are skipped and reported.</param>
    /// <param name="userNoteLabel">The label a user-note source is listed under (<see cref="UserNoteTitle"/>).</param>
    /// <returns>The built refs plus the unresolved refs and the missing sources, none of which fail the run.</returns>
    public static FactSourcesResult Build(
        StoryContentV1 content,
        ApprovedFactSheet sheet,
        IReadOnlyDictionary<Guid, FactSourceMetadata> sources,
        string userNoteLabel)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(userNoteLabel);

        var facts = new Dictionary<string, FactForWriter>(StringComparer.Ordinal);
        foreach (var fact in sheet.Facts)
        {
            facts[fact.Id] = fact;
        }

        var refs = new List<FactSourceRef>();
        var unknown = new List<string>();
        var skipped = new List<Guid>();
        var seenRefs = new HashSet<string>(StringComparer.Ordinal);
        var seenSources = new HashSet<Guid>();

        foreach (var factRef in FactRefs(content))
        {
            if (string.IsNullOrEmpty(factRef))
            {
                continue;
            }

            if (!facts.TryGetValue(factRef, out var fact))
            {
                if (seenRefs.Add(factRef))
                {
                    unknown.Add(factRef);
                }

                continue;
            }

            foreach (var alias in fact.SourceAliases)
            {
                // An alias the sheet's map does not know is unreachable: the guard rejects such a sheet before the
                // run starts, so there is no fact ref to report here and it is never resolved against the model's text.
                if (!sheet.AliasToSourceId.TryGetValue(alias, out var sourceId) || !seenSources.Add(sourceId))
                {
                    continue;
                }

                if (!sources.TryGetValue(sourceId, out var source))
                {
                    skipped.Add(sourceId);
                    continue;
                }

                refs.Add(source.Kind == StorySourceKind.UserNote
                    ? new FactSourceRef(sourceId, userNoteLabel, null)
                    : new FactSourceRef(sourceId, source.Title ?? source.Url ?? string.Empty, source.Url));
            }
        }

        return new FactSourcesResult(refs, unknown, skipped);
    }

    private static IEnumerable<string> FactRefs(StoryContentV1 content)
    {
        foreach (var factRef in content.Hook.FactRefs ?? [])
        {
            yield return factRef;
        }

        foreach (var paragraph in content.Paragraphs ?? [])
        {
            foreach (var factRef in paragraph.FactRefs ?? [])
            {
                yield return factRef;
            }
        }

        foreach (var factRef in content.Ending.FactRefs ?? [])
        {
            yield return factRef;
        }
    }
}
