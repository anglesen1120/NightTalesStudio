using System.Text;
using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Money;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Rendering;
using NightTales.Application.Abstractions.Stories;
using NightTales.Application.Abstractions.Usage;
using NightTales.Application.Common;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Catalog;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;
using NightTales.Domain.Episodes;
using NightTales.Domain.Rendering;
using NightTales.Domain.Rendering.Images;
using NightTales.Domain.Rendering.Music;
using NightTales.Domain.Rendering.Subtitles;
using NightTales.Domain.Series;
using NightTales.Domain.Series.Bible;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Keywords;

namespace NightTales.Application.Features.Rendering.Commands.RenderEpisode;

/// <summary>
/// Queues one render job for one approved episode (09 README BD5), in the transaction the D5 pipeline opened
/// and behind the episode row lock, so the state read, the approved-story read, the lint, the plan, the spend
/// check and the job insert of two concurrent calls cannot interleave. The steps run in the BD5 order and each
/// one short-circuits: nothing paid or dispatched happens before every precondition passed.
/// </summary>
/// <remarks>
/// <para>
/// The approved story version is read from the episode's own <c>approved_story_version_id</c> under that lock,
/// never from <c>current_story_version_id</c> and never through a stale pointer (02 D21/T28, SEC-4): a null
/// pointer (a reopen in flight) is <c>episode_invalid_state</c> and
/// <see cref="IStoryRenderInputProvider.GetAsync"/> is then never called. Another user's episode id and an
/// unknown id both answer 404 (D10), never 403.
/// </para>
/// <para>
/// The stored <c>render-params.v1</c> is content-free by construction: it carries the catalog ids, the plan
/// counts and the subtitle preset, and no script text, keyword or prompt (05 D11, P6). Neither this handler
/// nor its errors carry content: the lint maps <c>ValidationIssue</c>s to
/// <c>{ segment, index, rule }</c> rows (<see cref="Errors.KeywordsFlagged"/>, 03 D10) and never logs them.
/// </para>
/// <para>
/// The reservation and the job insert share the unit of work's transaction (D5): a partial-unique-index
/// violation (a second, concurrent render of the same episode under a different key) surfaces as the existing
/// 409 <c>concurrency_conflict</c> mapping of <c>EfUnitOfWork</c>, and a failure between the two rolls both
/// back, so no orphan reservation survives.
/// </para>
/// </remarks>
/// <param name="db">The tracked write model; the pipeline saves and commits it.</param>
/// <param name="storyInput">The approved version's persisted text and ordered scene keywords (02 D21).</param>
/// <param name="nameCorpus">The episode's real-person corpus, read only after the episode is authorised (03 D11).</param>
/// <param name="imageCostEstimator">The worst-case cost of the planned images (03 D8).</param>
/// <param name="priceCatalog">The price version in force at queue time (02 D14).</param>
/// <param name="budgetGuard">The platform spend cap (02 D1).</param>
/// <param name="creditLedger">The credit reservation (metering-only in phase P).</param>
/// <param name="recentMusic">The series' latest picks, so the selector avoids repeating them (05 D8).</param>
/// <param name="subtitles">The loaded subtitle preset catalog; its revision becomes the preset (05 D6/D11).</param>
/// <param name="images">The <c>Images</c> section: the placeholder tokens of the reserve bound (03 D8).</param>
/// <param name="clock">The command's clock reading.</param>
public sealed class RenderEpisodeCommandHandler(
    IAppDbContext db,
    IStoryRenderInputProvider storyInput,
    INameCorpusProvider nameCorpus,
    IImageCostEstimator imageCostEstimator,
    IPriceCatalog priceCatalog,
    IBudgetGuard budgetGuard,
    ICreditLedger creditLedger,
    IRecentMusicQuery recentMusic,
    SubtitlePresetCatalog subtitles,
    ImagesOptions images,
    IClock clock)
    : ICommandHandler<RenderEpisodeCommand, ErrorOr<RenderJobAcceptedDto>>
{
    /// <summary>
    /// The longest NFC-normalised <c>video_script</c>, in code points (04 V1, CR-3). It is the same D3 limit
    /// the submit mapper enforces before dispatch (<see cref="RenderSubmitMapper.MaxScriptRunes"/>), and it is
    /// checked here so an over-long story is a 400 on the queue call instead of a failed job later.
    /// </summary>
    public const int MaxScriptCodePoints = RenderSubmitMapper.MaxScriptRunes;

    /// <summary>The <c>validation_failed</c> field naming the story body (D7: the camelCase wire field).</summary>
    public const string ScriptField = "script";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<RenderJobAcceptedDto>> Handle(
        RenderEpisodeCommand command,
        CancellationToken cancellationToken)
    {
        // Step 1. The row lock first, then the tenant-filtered load (another user's id and a missing id are
        // both 404 — never 403, D10).
        await db.LockEpisodeForUpdateAsync(command.EpisodeId, cancellationToken).ConfigureAwait(false);

        var episode = await db.Episodes
            .SingleOrDefaultAsync(candidate => candidate.Id == command.EpisodeId, cancellationToken)
            .ConfigureAwait(false);
        if (episode is null)
        {
            return Errors.NotFound();
        }

        // Only an approved story can be rendered; the state is checked before the If-Match, so a draft episode
        // answers 409 rather than 412 whichever tag the client sent (BD5 step 1, D16).
        if (episode.Status != EpisodeStatus.StoryApproved)
        {
            return Error.Conflict(
                ErrorCodes.EpisodeInvalidState,
                "A render can only be queued for an episode whose story is approved.");
        }

        // Step 1, strong exact comparison (D7): the route filter already answered 428 for a missing header, so
        // this covers a stale tag and a direct handler call.
        if (!ETags.Matches(command.IfMatch, ETags.Episode(episode.Version)))
        {
            return Error.Conflict(
                ErrorCodes.EpisodeConflict,
                "The episode changed since the client last read it.");
        }

        // SEC-4 (02 D21/T28): the approved pointer is read under the lock taken above, and it alone selects the
        // version. A null pointer is a reopen race, not a reason to fall back to current_story_version_id, and
        // IStoryRenderInputProvider is not called at all in that case (02.3.2-T1 L115).
        if (episode.ApprovedStoryVersionId is not { } storyVersionId)
        {
            return Error.Conflict(
                ErrorCodes.EpisodeInvalidState,
                "The episode has no approved story version to render.");
        }

        // The series context of steps 5 and 8 (language, aspect, voice, style template, topic). Tenant
        // filtered like the episode read; no LockSeriesForShareAsync is needed, because nothing on this path
        // gates on Topic the way the fact-sheet command's true-crime gate does.
        var series = await db.Series
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == episode.SeriesId, cancellationToken)
            .ConfigureAwait(false);
        if (series is null)
        {
            return Errors.NotFound();
        }

        // Step 2. The exact approved version, resolved by the pointer above — never the current version.
        var story = await storyInput.GetAsync(storyVersionId, cancellationToken).ConfigureAwait(false);
        if (story.IsError)
        {
            return story.Errors;
        }

        // Step 3. The D3 script bound, in code points after NFC normalisation (04 V1, CR-3). The lower bound
        // belongs to the 02 approval gate, which only approves a body of 1-20000 code points. Noncharacters are
        // replaced first: ICU throws on one, and the token count of Step 5 normalises the same text (SEC2).
        var body = ReplaceNoncharacters(story.Value.Body ?? string.Empty);
        if (CountScriptCodePoints(body) > MaxScriptCodePoints)
        {
            return Errors.ValidationFailed(
                ScriptField,
                $"The story body must be at most {MaxScriptCodePoints} code points after NFC normalisation.");
        }

        // Step 4. The corpus read happens only now that the episode is loaded and authorised in this scope; an
        // empty corpus is not evidence of a clean episode (03 D11). Every issue Lint returns is blocking.
        var corpus = await nameCorpus.LoadAsync(episode.Id, cancellationToken).ConfigureAwait(false);
        var segments = SanitiseKeywords(story.Value.Segments);
        var issues = RenderKeywordLinter.Lint(segments, corpus);
        if (issues.Count > 0)
        {
            return Errors.KeywordsFlagged(ToFlags(issues));
        }

        // Step 5. The image plan. The rates of ImagePlanOptions are the documented defaults, because
        // Story:Length:Rate:{language} is not bound to configuration anywhere (03 D3/D8); ImagesOptions owns
        // the clip length and the shares but not those two rates, so the plan takes the D3 defaults as a whole.
        var plan = RenderImagePlanner.Plan(new RenderImagePlanInput(
            segments,
            StoryText.CountTokens(body),
            series.Language,
            episode.LengthTier,
            ImagePlanOptions.Defaults));
        if (plan.Terms.Count == 0)
        {
            // No keyword survived, so no image can be rendered: 409 keywords_flagged with the single
            // no_scenes row (03.2.2-T2, D10).
            return Errors.NoScenes();
        }

        var now = clock.UtcNow;

        // Step 6. The price version the render runs under, then the worst-case bound of its images (03 D8).
        var priceVersion = await priceCatalog.GetActiveAsync(now, cancellationToken).ConfigureAwait(false);
        if (priceVersion.IsError)
        {
            // Fail closed: with no active rates there is no bound to check and no cost to reserve
            // (the price_catalog_empty code is not an HTTP code, so a 500 would otherwise be the answer).
            return Error.Failure(
                ErrorCodes.ServiceUnavailable,
                "The price catalogue has no active version; the render cannot be queued.");
        }

        var bound = await imageCostEstimator
            .WorstCaseAsync(series.Aspect, plan.MaxTerms, priceVersion.Value.Version, cancellationToken)
            .ConfigureAwait(false);

        // Step 7. The platform spend cap, under the platform-wide advisory lock the guard takes itself.
        var budget = await budgetGuard.CheckAsync(bound, cancellationToken).ConfigureAwait(false);
        if (budget.IsError)
        {
            return budget.Errors;
        }

        // Step 8. The music pick: the series' latest picks through IRecentMusicQuery, the global catalog as
        // candidates, and the deterministic selector on top (05 D8). A null choice is no music at all, and the
        // render proceeds (05 D11).
        var recent = await recentMusic
            .GetRecentTrackIdsAsync(episode.SeriesId, MusicSelector.DefaultAvoidRecent, cancellationToken)
            .ConfigureAwait(false);
        var tracks = await db.MusicTracks
            .AsNoTracking()
            .Where(track => track.Active)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var choice = MusicSelector.Pick(new MusicSelectionInput(series.Topic, recent, episode.Id), tracks);

        // Step 9. The stored non-content settings. The attribution comes from the picked catalog row and is
        // non-null exactly for a CC-BY track (05 D8, T9).
        var template = await db.StyleTemplates
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == series.StyleTemplateId, cancellationToken)
            .ConfigureAwait(false);
        if (template is null)
        {
            // Fail closed: the series points at a template that no longer exists, so no prompt and no
            // render-params can be built. The series owner fixes it; a retry of this request cannot.
            // An inactive template still renders: its prompt version and render settings are stored, and
            // Active only gates new selection.
            return Error.Conflict(
                ErrorCodes.EpisodeInvalidState,
                "The series' style template is unavailable; the render cannot be prepared.");
        }

        var aspectWire = series.Aspect == Aspect.Landscape
            ? SubtitlePresetCatalog.LandscapeAspect
            : SubtitlePresetCatalog.PortraitAspect;
        var stylePromptVersion = StylePromptVersion.For(template);

        var renderParams = RenderSubmitMapper.BuildParams(new RenderParamsInput(
            storyVersionId,
            aspectWire,
            CatalogWireNames.Spelling(series.Language),
            series.VoiceId,
            series.StyleTemplateId,
            stylePromptVersion,
            ImagePlanSummary.From(plan, stylePromptVersion),
            new SubtitlesParams(true, $"{aspectWire}@{subtitles.Revision}"),
            choice,
            choice is null
                ? null
                : tracks
                    .FirstOrDefault(track => string.Equals(track.TrackId, choice.TrackId, StringComparison.Ordinal))
                    ?.Attribution,
            plan.ClipSeconds));

        // Step 10. The reservation, keyed by the job id so the ingest can settle exactly it (W2 CR-2, B10
        // WT50). render_jobs stores no token columns and the phase-P ledger meters cost only, so the token
        // members of UsageBound are the documented placeholder estimate of the planned images (03 D8). The row
        // stores the same bound as worst_case_usd (09.6.3-T1), so the open-bounds reader keeps counting this
        // render against the platform cap while it is open.
        var renderJobId = Guid.CreateVersion7();
        var usageBound = new UsageBound(
            bound.WorstCaseUsd,
            plan.MaxTerms * ImagesOptions.PromptTextTokens,
            images.PlaceholderTokensPerImage);

        var reservation = await creditLedger
            .ReserveAsync(episode.UserId, CreditOperation.Render, renderJobId, usageBound, cancellationToken)
            .ConfigureAwait(false);
        if (reservation.IsError)
        {
            return reservation.Errors;
        }

        // Steps 10-11. The job and the episode's transition in the same unit of work as the reservation: its
        // insert is what the partial unique index guards (a second active job is 409 concurrency_conflict).
        var queued = RenderJob.Create(
            renderJobId,
            episode.UserId,
            episode.Id,
            storyVersionId,
            reservation.Value.Value,
            bound.WorstCaseUsd,
            renderParams,
            attempt: 1,
            now);
        if (queued.IsError)
        {
            return queued.Errors;
        }

        db.RenderJobs.Add(queued.Value);

        var rendering = episode.RequestRender(now);
        if (rendering.IsError)
        {
            return rendering.Errors;
        }

        // Step 12. Nothing more: RenderJob.Create raised RenderRequested, and the interceptor writes its
        // ids-only outbox row in this SaveChanges (D11).

        // Step 13. The 202 body; the endpoint composes Location: /render-jobs/{id} from it.
        command.Outcome = new RenderJobOutcome(renderJobId);

        return new RenderJobAcceptedDto(renderJobId, "queued");
    }

    /// <summary>
    /// Maps the linter's blocking issues to the D10 <c>errors[]</c> rows, reading the keyword index the linter
    /// carries in <see cref="ValidationIssue.Evidence"/> back out (03.2.2-T1, CR-18b). No issue text, keyword
    /// or name leaves this method: a row is the D22 segment address, the index and the rule id.
    /// </summary>
    /// <param name="issues">The blocking issues of one lint, in segment and then keyword order.</param>
    /// <returns>One row per issue; a missing index carrier becomes 0.</returns>
    private static KeywordFlag[] ToFlags(IReadOnlyList<ValidationIssue> issues)
    {
        var flags = new KeywordFlag[issues.Count];
        for (var index = 0; index < issues.Count; index++)
        {
            var issue = issues[index];
            _ = RenderKeywordLinter.TryGetKeywordIndex(issue, out var keywordIndex);
            flags[index] = new KeywordFlag(issue.Segment, keywordIndex, issue.RuleId);
        }

        return flags;
    }

    /// <summary>
    /// The code points of <paramref name="body"/> after the NFC normalisation of D3/04 V1, so the limit is
    /// counted on the text the submit sends.
    /// </summary>
    /// <remarks>
    /// An unpaired UTF-16 surrogate unit and every noncharacter (<see cref="BibleOpNormalizer.IsNoncharacter"/>)
    /// are replaced with a space first: ICU refuses to normalise them and throws (verified on Linux with the
    /// 02.6.3 gate), and a space — not a deletion — keeps the parts of a word apart, exactly as the corpus
    /// reader of 03.2.2-T1 replaces them.
    /// </remarks>
    /// <param name="body">The stored video script.</param>
    /// <returns>The count of Unicode scalar values of the normalised script.</returns>
    private static int CountScriptCodePoints(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return 0;
        }

        var normalised = RemoveRefusedCodePoints(body).Normalize(NormalizationForm.FormC);
        var count = 0;
        foreach (var _ in normalised.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// The segments with every noncharacter replaced by a space in each keyword, so the linter and the planner never
    /// normalise one; ICU throws on a noncharacter (SEC2).
    /// </summary>
    /// <param name="segments">The approved version's ordered scene keywords.</param>
    /// <returns>The same segments with each keyword sanitised.</returns>
    private static List<SegmentKeywords> SanitiseKeywords(IReadOnlyList<SegmentKeywords> segments) =>
        segments.Select(segment => segment with
        {
            Keywords = segment.Keywords.Select(ReplaceNoncharacters).ToList(),
        }).ToList();

    /// <summary>
    /// <paramref name="text"/> with every noncharacter replaced by a space. Unpaired surrogates stay: the Domain
    /// normaliser already replaces them, and a space there would split a token that counts as one today (SEC2).
    /// Returns the input itself when there is no noncharacter.
    /// </summary>
    /// <param name="text">The text to sanitise.</param>
    /// <returns>The text without a noncharacter.</returns>
    private static string ReplaceNoncharacters(string text)
    {
        StringBuilder? builder = null;
        for (var index = 0; index < text.Length; index++)
        {
            var width = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
                ? 2
                : 1;
            var codePoint = width == 2 ? char.ConvertToUtf32(text[index], text[index + 1]) : text[index];
            if (BibleOpNormalizer.IsNoncharacter(codePoint))
            {
                builder ??= new StringBuilder(text.Length).Append(text, 0, index);
                builder.Append(' ');
            }
            else
            {
                builder?.Append(text, index, width);
            }

            index += width - 1;
        }

        return builder?.ToString() ?? text;
    }

    /// <summary>
    /// <paramref name="text"/> with every unpaired surrogate unit and every noncharacter replaced by a space,
    /// so <see cref="string.Normalize(NormalizationForm)"/> can normalise it. Returns the input itself when
    /// there is nothing to replace, so the common path allocates nothing.
    /// </summary>
    /// <param name="text">The story body.</param>
    /// <returns>The text without an unpaired surrogate or a noncharacter.</returns>
    private static string RemoveRefusedCodePoints(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= text.Length
                    || !char.IsLowSurrogate(text[index + 1])
                    || BibleOpNormalizer.IsNoncharacter(char.ConvertToUtf32(character, text[index + 1])))
                {
                    return Replace(text, index);
                }

                index++;
                continue;
            }

            if (char.IsLowSurrogate(character) || BibleOpNormalizer.IsNoncharacter(character))
            {
                return Replace(text, index);
            }
        }

        return text;
    }

    /// <summary>
    /// <paramref name="text"/> with the refused units from <paramref name="start"/> on replaced by a space,
    /// stepping over a well-formed surrogate pair as one unit. Only reached when
    /// <see cref="RemoveRefusedCodePoints"/> found a refused unit.
    /// </summary>
    /// <param name="text">The story body.</param>
    /// <param name="start">The index of the first refused unit.</param>
    /// <returns>The replacement text.</returns>
    private static string Replace(string text, int start)
    {
        var builder = new StringBuilder(text.Length);
        builder.Append(text, 0, start);

        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                if (BibleOpNormalizer.IsNoncharacter(char.ConvertToUtf32(character, text[index + 1])))
                {
                    builder.Append(' ');
                }
                else
                {
                    builder.Append(character).Append(text[index + 1]);
                }

                index++;
            }
            else if (char.IsSurrogate(character) || BibleOpNormalizer.IsNoncharacter(character))
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
