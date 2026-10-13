using System.Data.Common;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Research.Facts;
using NightTales.Application.Features.Research.GenerateFactSheet;
using NightTales.Application.Features.Research.Jobs;
using NightTales.Domain.Common;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Commands.UpdateFactSheet;

/// <summary>
/// Saves the owner's corrected fact list as a new <c>draft</c> version (02.5.4-T1, plan §4.2, decisions
/// 3/8/11/12/13). The episode's row lock is the first statement, so two edits of the same draft serialise:
/// the second one reads the first one's new version and its <c>fs-</c> ETag no longer matches (412).
/// </summary>
/// <remarks>
/// <para>
/// The check order is the contract's (decision 8): the tenant-filtered episode and current-version loads (a
/// foreign episode, an unknown id and an episode without a sheet all answer 404
/// <c>fact_sheet_not_found</c>, never 403), the ETag (428 <c>if_match_required</c> / 412
/// <c>fact_sheet_conflict</c>), the F26 state gate (409 <c>episode_invalid_state</c>) and only then the body
/// (400 <c>fact_sheet_invalid_content</c>), so an invalid body never hides the reason a save is impossible.
/// </para>
/// <para>
/// Nothing is trusted from the request beyond the three members the client owns: the statement is re-validated
/// with the very rules the extraction applies (<see cref="FactStatementRules"/>), the ids come from
/// <see cref="FactIdAllocator"/> against the episode's watermark, the alias map from
/// <see cref="FactSheetSourceAliases"/> over the version that was read, and the overlap score and origin are
/// recomputed — never read from the body.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="clock">The command's clock reading.</param>
public sealed class UpdateFactSheetCommandHandler(IAppDbContext db, IClock clock)
    : ICommandHandler<UpdateFactSheetCommand, ErrorOr<FactSheetSummaryDto>>
{
    /// <summary>
    /// The most facts one version may hold: the <c>facts.v1</c> ceiling that <c>Facts:MaxFacts</c> may not exceed
    /// (<see cref="FactSheetExtractionOptions.MaxMaxFacts"/>), so an edit can never store more than a run could.
    /// </summary>
    private const int MaxFacts = FactSheetExtractionOptions.MaxMaxFacts;

    /// <summary>The most sources one fact may cite; the alias scheme allows <c>s1…s10</c>.</summary>
    private const int MaxSourceIdsPerFact = FactInputLimits.MaxSourcesCeiling;

    /// <summary>PostgreSQL's SQLSTATE for a unique-constraint violation.</summary>
    private const string UniqueViolation = "23505";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<FactSheetSummaryDto>> Handle(
        UpdateFactSheetCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Step 1: the row lock first, then the tenant-filtered loads (another user's id and a missing id are both
        // 404, and so is an episode that has no fact sheet yet — decision 9).
        await db.LockEpisodeForUpdateAsync(command.EpisodeId, cancellationToken).ConfigureAwait(false);

        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == command.EpisodeId, cancellationToken)
            .ConfigureAwait(false);
        if (episode is null)
        {
            return SheetNotFound();
        }

        var current = await CurrentSheetAsync(command.EpisodeId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return SheetNotFound();
        }

        // Step 2: 428 without the header, 412 for a stale, malformed or foreign tag. Only the current version is
        // addressable: a tag naming any other row of the episode is stale by definition (decision 4).
        if (string.IsNullOrWhiteSpace(command.IfMatch))
        {
            return IfMatchRequired();
        }

        if (!ETags.Matches(command.IfMatch, ETags.FactSheet(current.Id)))
        {
            return StaleTag();
        }

        // Step 3: draft and story_review only (F26); every other state gets the same code.
        if (!episode.CanEditStory)
        {
            return InvalidState();
        }

        // Step 4: the body. Every failure is one 400 fact_sheet_invalid_content with the failing field path and
        // never the input itself (D17/T51); an empty list is a valid body (it saves an empty draft).
        var content = command.Content;
        if (content is null)
        {
            return InvalidContent("body", "The body is not a fact-sheet document.");
        }

        var inputs = content.Facts ?? [];
        if (inputs.Count > MaxFacts)
        {
            return Error.Validation(
                ErrorCodes.FactSheetInvalidContent,
                $"A fact sheet holds at most {MaxFacts} facts.",
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [ErrorMetadata.Field] = "facts",
                    [ErrorMetadata.Limit] = MaxFacts,
                });
        }

        var sourceIdsOfEpisode = await db.StorySources
            .Where(source => source.EpisodeId == command.EpisodeId)
            .Select(source => source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var eligible = new HashSet<Guid>(sourceIdsOfEpisode);

        var statements = new List<string>(inputs.Count);
        var citedInFirstCitationOrder = new List<Guid>();
        var citedSeen = new HashSet<Guid>();
        var violations = new List<Error>();

        for (var index = 0; index < inputs.Count; index++)
        {
            var field = $"facts[{index}]";
            var input = inputs[index];
            var statementField = string.Concat(field, ".statement");
            var sourcesField = string.Concat(field, ".sourceIds");

            if (input is null)
            {
                violations.Add(InvalidContent(field, "Every fact must be an object."));
                statements.Add(string.Empty);
                continue;
            }

            if (FactStatementRules.TryNormalize(input.Statement, out var statement) is { } reason)
            {
                violations.Add(InvalidContent(statementField, $"The statement is not valid ({reason})."));
                statements.Add(string.Empty);
                continue;
            }

            statements.Add(statement);

            var sourceIds = input.SourceIds ?? [];
            if (sourceIds.Count == 0)
            {
                violations.Add(InvalidContent(sourcesField, "Every fact must cite at least one source."));
                continue;
            }

            if (sourceIds.Count > MaxSourceIdsPerFact)
            {
                violations.Add(Error.Validation(
                    ErrorCodes.FactSheetInvalidContent,
                    $"A fact may cite at most {MaxSourceIdsPerFact} sources.",
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        [ErrorMetadata.Field] = sourcesField,
                        [ErrorMetadata.Limit] = MaxSourceIdsPerFact,
                    }));
                continue;
            }

            var distinct = new HashSet<Guid>();
            var repeated = false;
            var unknown = false;
            foreach (var sourceId in sourceIds)
            {
                repeated |= !distinct.Add(sourceId);
                unknown |= !eligible.Contains(sourceId);
            }

            if (repeated)
            {
                violations.Add(InvalidContent(sourcesField, "A fact may cite a source only once."));
            }

            if (unknown)
            {
                violations.Add(InvalidContent(sourcesField, "A fact cites a source that is not on this episode."));
            }

            if (repeated || unknown)
            {
                continue;
            }

            foreach (var sourceId in sourceIds)
            {
                if (citedSeen.Add(sourceId))
                {
                    citedInFirstCitationOrder.Add(sourceId);
                }
            }
        }

        if (violations.Count > 0)
        {
            return violations;
        }

        // Step 5: the ids. A kept id must name a fact of the current version and appear once; a new fact gets the
        // next id after the watermark, so an id a deletion retired is never handed out again (F12).
        var allocated = FactIdAllocator.Allocate(
            [.. current.Facts.Select(fact => fact.Id)],
            current.MaxFactId,
            [.. inputs.Select(fact => fact.Id)]);
        if (allocated.IsError)
        {
            return allocated.Errors;
        }

        // Step 6: the version's alias map and the facts. A fact that keeps its statement and its sources is copied
        // verbatim (Origin and the overlap score survive); any other change makes it the user's own (decision 11),
        // and confidence is preserved unless the body overrides it (T53).
        var sourceAliases = FactSheetSourceAliases.Reconcile(current.SourceAliases, citedInFirstCitationOrder);
        var aliases = new Dictionary<Guid, string>(sourceAliases.Count);
        foreach (var pair in sourceAliases)
        {
            aliases[pair.Value] = pair.Key;
        }

        var storedById = new Dictionary<string, FactItem>(current.Facts.Count, StringComparer.Ordinal);
        foreach (var fact in current.Facts)
        {
            storedById[fact.Id] = fact;
        }

        var facts = new List<FactItem>(inputs.Count);
        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            var id = allocated.Value.Ids[index];
            var statement = statements[index];
            var sourceIds = input.SourceIds ?? [];

            FactItem? stored = null;
            if (input.Id is { } keptId)
            {
                storedById.TryGetValue(keptId, out stored);
            }

            var unchanged = stored is not null
                && string.Equals(stored.Statement, statement, StringComparison.Ordinal)
                && stored.SourceIds.SequenceEqual(sourceIds);
            var confidence = input.Confidence ?? stored?.Confidence ?? FactConfidence.High;

            facts.Add(unchanged
                ? stored! with { Confidence = confidence }
                : new FactItem(
                    id,
                    statement,
                    [.. sourceIds],
                    [.. sourceIds.Select(sourceId => aliases[sourceId])],
                    null,
                    confidence,
                    FactOrigin.User));
        }

        // Step 7: the new row and the event. The previous version is never mutated (D9): the ETag names the row.
        var now = clock.UtcNow;
        var created = FactSheet.CreateUserDraft(current, facts, sourceAliases, allocated.Value.MaxFactId, episode.UserId, now);
        db.FactSheets.Add(created);

        var raised = episode.RaiseFactSheetSaved(created.Id, created.Version, now);
        if (raised.IsError)
        {
            return raised.Errors;
        }

        // The flush is the only place this layer can see a lost race: the two unique indexes of the table and the
        // partial one of the approved row are refused inside the open transaction, and the Application layer cannot
        // name the provider exception, so a unique violation here is the 412 the ETag contract expects (decision 6).
        var conflict = await FlushAsync(cancellationToken).ConfigureAwait(false);
        if (conflict is not null)
        {
            return conflict.Value;
        }

        command.Outcome = new FactSheetSavedOutcome(created.Id, created.Version);
        return Summary(created);
    }

    /// <summary>The episode's current version: the row with the highest version, whatever its status (decision 3).</summary>
    private async Task<FactSheet?> CurrentSheetAsync(Guid episodeId, CancellationToken cancellationToken) =>
        await db.FactSheets
            .Where(sheet => sheet.EpisodeId == episodeId)
            .OrderByDescending(sheet => sheet.Version)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Flushes the pending changes inside the command's transaction; a unique refusal becomes 412.</summary>
    private async Task<Error?> FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return StaleTag();
        }
    }

    /// <summary>Whether the provider refused a unique constraint (SQLSTATE 23505) anywhere in the chain.</summary>
    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException database && database.SqlState == UniqueViolation)
            {
                return true;
            }
        }

        return false;
    }

    private static FactSheetSummaryDto Summary(FactSheet sheet) =>
        new(
            sheet.Id,
            sheet.Version,
            FactSheetWire.Status(sheet.Status),
            ETags.FactSheet(sheet.Id),
            sheet.Facts.Count,
            sheet.CreatedAt);

    private static Error SheetNotFound() =>
        Error.NotFound(ErrorCodes.FactSheetNotFound, "No fact sheet exists for this episode.");

    private static Error IfMatchRequired() =>
        Error.Failure(
            ErrorCodes.IfMatchRequired,
            "An If-Match header with the fact-sheet ETag you read is required.");

    private static Error StaleTag() =>
        Error.Failure(
            ErrorCodes.FactSheetConflict,
            "The fact sheet changed since it was read; read it again and retry.");

    private static Error InvalidState() =>
        Error.Conflict(ErrorCodes.EpisodeInvalidState, "A fact sheet cannot be edited in the episode's current state.");

    private static Error InvalidContent(string field, string description) =>
        Error.Validation(
            ErrorCodes.FactSheetInvalidContent,
            description,
            new Dictionary<string, object>(StringComparer.Ordinal) { [ErrorMetadata.Field] = field });
}
