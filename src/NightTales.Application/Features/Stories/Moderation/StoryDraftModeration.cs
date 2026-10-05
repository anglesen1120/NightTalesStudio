using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions.Moderation;
using NightTales.Application.Features.Stories.Generation;
using NightTales.Domain.Compliance;
using NightTales.Domain.Stories.Quality;

namespace NightTales.Application.Features.Stories.Moderation;

/// <summary>Unmetered D12 draft moderation for human edits and approval re-checks, outside a generation scope.</summary>
public sealed partial class StoryDraftModeration(
    IContentModerator moderator,
    IModerationEventWriter events,
    ILogger<StoryDraftModeration> logger)
{
    // The episode row is locked by the caller. Keep provider retries well below the 30 s DB command timeout.
    private const int ProviderBudgetSeconds = 10;
    private const int MaxCategoryCount = 32;
    private const int MaxCategoryLength = 64;

    /// <summary>Returns a fresh, persistable verdict, or null when the provider cannot supply one.</summary>
    public async ValueTask<JsonObject?> CheckAsync(
        Guid userId,
        Guid episodeId,
        string title,
        string body,
        string? description,
        IReadOnlyList<string> tags,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ct.ThrowIfCancellationRequested();
        var texts = new List<string>(3 + tags.Count);
        AddText(title);
        AddText(body);
        AddText(description);
        foreach (var tag in tags)
        {
            AddText(tag);
        }

        ModerationResult result;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(ProviderBudgetSeconds));
        try
        {
            var pending = moderator.CheckAsync(texts, ModerationStage.Draft, deadline.Token);
            result = pending.IsCompletedSuccessfully
                ? pending.Result
                : await pending.AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            deadline.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception exception)
        {
            ct.ThrowIfCancellationRequested();
            // A provider deadline can throw OCE even while the caller's token is live. It is an outage, not a pass.
            LogUnavailable(logger, episodeId, exception);
            return null;
        }

        if (result is null || result.Categories is null)
        {
            LogInvalidVerdict(logger, episodeId);
            return null;
        }

        // Match the pipeline: categories without a flag are still flagged; a flag without categories fails closed.
        var flagged = result.Flagged || result.Categories.Count > 0;
        var categories = NormaliseCategories(result.Categories, flagged);

        var blocking = categories.Where(StoryQualityChecker.BlockingModerationCategories.Contains).ToArray();
        var verdict = new JsonObject
        {
            ["flagged"] = flagged,
            ["categories"] = ArrayOf(categories),
            ["blocking"] = ArrayOf(blocking),
            ["stale"] = false,
            ["unavailable"] = false,
        };
        if (!TextSafety.IsPersistableJson(verdict.ToJsonString()))
        {
            LogInvalidVerdict(logger, episodeId);
            return null;
        }

        if (flagged)
        {
            // D22: draft-stage evidence has no stored-version excerpt. The writer owns its independent transaction.
            // Its independent 10 s write timeout plus our 10 s provider budget keeps lock-held I/O below 30 s.
            // Do not abandon the evidence write: D22 requires it to survive caller cancellation/rollback.
            await events.WriteAsync(
                userId, episodeId, ModerationSource.OpenaiText, ModerationStage.Draft, categories, null, ct)
                .ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        return verdict;

        void AddText(string? text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                texts.Add(text);
            }
        }
    }

    private static string[] NormaliseCategories(IReadOnlyList<string> rawCategories, bool flagged)
    {
        // Reject the raw count too: duplicate names must not hide an oversized provider payload.
        if (rawCategories.Count > MaxCategoryCount)
        {
            return [StoryQualityChecker.UnspecifiedModerationCategory];
        }

        var categories = new HashSet<string>(StringComparer.Ordinal);
        var corrupted = false;
        foreach (var raw in rawCategories)
        {
            var cleaned = TextSafety.Clean(raw);
            corrupted |= !string.Equals(raw, cleaned, StringComparison.Ordinal);
            var category = cleaned.Trim();
            if (category.Length > MaxCategoryLength)
            {
                // Never truncate a name (including a surrogate pair) or silently discard a blocker.
                return [StoryQualityChecker.UnspecifiedModerationCategory];
            }

            categories.Add(string.IsNullOrWhiteSpace(category)
                ? StoryQualityChecker.UnspecifiedModerationCategory
                : category);
        }

        if (corrupted || (flagged && categories.Count == 0))
        {
            categories.Add(StoryQualityChecker.UnspecifiedModerationCategory);
        }

        // The corruption sentinel is also subject to the persisted count cap.
        return categories.Count > MaxCategoryCount
            ? [StoryQualityChecker.UnspecifiedModerationCategory]
            : [.. categories];
    }

    /// <summary>Missing, malformed, stale or unavailable verdicts require a fresh check before approval.</summary>
    public static bool NeedsRecheck(JsonNode? moderation) =>
        !TryRead(moderation, out _, out var stale, out var unavailable) || stale || unavailable;

    /// <summary>Returns D12 blockers, or an unspecified blocker for unavailable or malformed evidence.</summary>
    public static IReadOnlyList<string> BlockingCategoriesOf(JsonNode? moderation) =>
        TryRead(moderation, out var blocking, out _, out var unavailable) && !unavailable
            ? blocking
            : [StoryQualityChecker.UnspecifiedModerationCategory];

    private static bool TryRead(
        JsonNode? node,
        out string[] blocking,
        out bool stale,
        out bool unavailable)
    {
        blocking = [];
        stale = false;
        unavailable = false;
        if (node is not JsonObject moderation
            || !ReadFlag(moderation["flagged"], out var flagged)
            || !ReadFlag(moderation["stale"], out stale)
            || !ReadFlag(moderation["unavailable"], out unavailable)
            || !ReadCategories(moderation["categories"], out var categories)
            || !ReadCategories(moderation["blocking"], out var recordedBlocking))
        {
            return false;
        }

        // Never drop malformed category elements, trust a forged empty blocking array, or treat contradictory flags as clean.
        if ((!flagged && categories.Length > 0) || (flagged && categories.Length == 0))
        {
            return false;
        }

        blocking = categories.Where(StoryQualityChecker.BlockingModerationCategories.Contains).ToArray();
        return new HashSet<string>(blocking, StringComparer.OrdinalIgnoreCase)
            .SetEquals(recordedBlocking);
    }

    private static bool ReadFlag(JsonNode? node, out bool flag)
    {
        flag = false;
        return node is JsonValue value && value.TryGetValue(out flag);
    }

    private static bool ReadCategories(JsonNode? node, out string[] categories)
    {
        categories = [];
        if (node is not JsonArray array || array.Count > MaxCategoryCount)
        {
            return false;
        }

        var values = new string[array.Count];
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonValue value
                || !value.TryGetValue<string>(out var text)
                || string.IsNullOrWhiteSpace(text)
                || text.Length > MaxCategoryLength
                || !string.Equals(text, TextSafety.Clean(text).Trim(), StringComparison.Ordinal)
                || !TextSafety.IsPersistableJson(value.ToJsonString()))
            {
                return false;
            }

            values[index] = text;
        }

        categories = values;
        return true;
    }

    private static JsonArray ArrayOf(IEnumerable<string> values) =>
        new([.. values.Select(value => (JsonNode)JsonValue.Create(value)!)]);

    [LoggerMessage(EventId = 6011, Level = LogLevel.Warning, Message = "Draft moderation is unavailable for episode {EpisodeId}.")]
    private static partial void LogUnavailable(ILogger logger, Guid episodeId, Exception exception);

    [LoggerMessage(EventId = 6012, Level = LogLevel.Warning, Message = "Draft moderation returned an invalid verdict for episode {EpisodeId}.")]
    private static partial void LogInvalidVerdict(ILogger logger, Guid episodeId);
}
