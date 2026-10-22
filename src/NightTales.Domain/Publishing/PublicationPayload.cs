using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NightTales.Domain.Publishing;

/// <summary>
/// The key-oriented reader and writer of the <c>publications.platform_payload</c> document (06 README C11/D12, plan
/// §3.2): the keys the upload path owns are written and removed here, so their spelling, their value format and the
/// "remove a key, never store a <c>null</c> under it" rule exist once instead of in every caller.
/// </summary>
/// <remarks>
/// <para>
/// <b>One document, several owners.</b> 06.2.1-T1 writes <c>preAudit</c>/<c>requestedPrivacy</c>, 06.2.2-T2 writes
/// <c>vetoNoticeEmailId</c>, 06.2.4-T1 writes <c>upload</c> and 06.2.5-T1 reads <c>processing</c>, so every method here
/// touches <em>one</em> key and writes every other key back exactly as it found it. A document written by a later task
/// therefore still answers <see cref="ReadString"/> for an earlier key.
/// </para>
/// <para>
/// <b>Robust by construction, never throwing.</b> A <c>platform_payload</c> nobody can parse — only a hand-edited row
/// reaches that state — reads back as "no key" and is <em>replaced</em> by the next write, the same rule
/// <see cref="Publication.SetVetoNoticeEmailId"/> has always followed: an aggregate never fails a job over the shape of
/// a bookkeeping column. The one exception is a removal from an unparseable document, which returns the document
/// untouched: there is no readable key to remove, and rewriting it would destroy the only copy of whatever it holds.
/// </para>
/// <para>
/// <b>Nothing secret is ever serialised here that is not already ciphertext.</b> <see cref="UploadSessionEncKey"/>
/// holds the Data-Protection ciphertext of the resumable session URI (06 README D4), written by
/// <see cref="Publication.AttachUploadSession"/> from a value the caller has already protected; the plaintext URI never
/// reaches this class, a log line, an outbox payload or an exception message. The title, the description and the
/// tokens of <c>channel_connections</c> are never written into this document at all (plan §3.2, D17).
/// </para>
/// </remarks>
public static class PublicationPayload
{
    /// <summary>
    /// The protected resumable session URI of an upload in progress (plan §3.2, 06 README D4): ciphertext only, and
    /// only ever written before the first byte leaves the process, so the retry of a lost response resumes instead of
    /// uploading a second video.
    /// </summary>
    public const string UploadSessionEncKey = "uploadSessionEnc";

    /// <summary>
    /// The Pacific quota day (<c>yyyy-MM-dd</c>) of an outstanding reservation (plan §3.2, 06 README D10): written when
    /// the unit is reserved, <em>removed</em> when it is released or the upload finishes. 06.2.6-T1's cancel releases a
    /// unit only when this key is present, and a JSON <c>null</c> under it reads as absent — which is why the writers
    /// remove the key instead of nulling it.
    /// </summary>
    public const string QuotaReservedDayKey = "quotaReservedDay";

    /// <summary>
    /// The id of the open <see cref="PlatformIncident"/> holding the row (plan §3.2, YR24): present only while the row
    /// is held, so its presence is the "this row is held" test 06.2.3-T3's clear path selects on.
    /// </summary>
    public const string HeldByIncidentIdKey = "heldByIncidentId";

    /// <summary>
    /// The processing block <c>{ startedAt, deadlineAt, polls }</c> written by
    /// <see cref="Publication.MarkProcessing"/> for 06.2.5-T1's poller: <c>deadlineAt</c> is the instant its claim
    /// condition compares against, and <c>polls</c> the count of checks already spent on the video.
    /// </summary>
    public const string ProcessingKey = "processing";

    /// <summary>
    /// The raw Google <c>reason</c> of the last failure (plan §3.2), already sanitised by the caller to a short
    /// token (<c>quotaExceeded</c>, <c>invalidPublishAt</c>, …): diagnostic only, never a client-facing code — that is
    /// <c>publications.error_code</c>'s job (<see cref="PublicationErrorCodes"/>).
    /// </summary>
    public const string LastGoogleReasonKey = "lastGoogleReason";

    /// <summary>The <c>startedAt</c> key inside <see cref="ProcessingKey"/>.</summary>
    private const string ProcessingStartedAtKey = "startedAt";

    /// <summary>The <c>deadlineAt</c> key inside <see cref="ProcessingKey"/>.</summary>
    private const string ProcessingDeadlineAtKey = "deadlineAt";

    /// <summary>The <c>polls</c> key inside <see cref="ProcessingKey"/>.</summary>
    private const string ProcessingPollsKey = "polls";

    /// <summary>The one accepted spelling of <see cref="QuotaReservedDayKey"/>'s value (plan §3.2).</summary>
    private const string QuotaReservedDayFormat = "yyyy-MM-dd";

    /// <summary>
    /// The round-trip format of the two instants inside <see cref="ProcessingKey"/>: an offset-bearing, unambiguous
    /// value, normalised to UTC so two runs of the poller read one instant rather than two local renderings of it.
    /// </summary>
    private const string InstantFormat = "O";

    /// <summary>
    /// Sets one key of the document and leaves every other key as it was.
    /// </summary>
    /// <param name="json">The stored document, or <see langword="null"/>.</param>
    /// <param name="key">The key to write.</param>
    /// <param name="value">
    /// The key's string value, or <see langword="null"/>/blank to <em>remove</em> the key — the document never stores
    /// a <c>null</c> value, because every reader treats "key absent" as the only "nothing here" (<see
    /// cref="QuotaReservedDayKey"/>).
    /// </param>
    /// <returns>The document to store.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is <see langword="null"/> or blank.</exception>
    public static string? WithKey(string? json, string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return string.IsNullOrWhiteSpace(value)
            ? WithoutKey(json, key)
            : Set(json, key, JsonValue.Create(value));
    }

    /// <summary>
    /// Removes one key of the document and leaves every other key as it was.
    /// </summary>
    /// <param name="json">The stored document, or <see langword="null"/>.</param>
    /// <param name="key">The key to remove.</param>
    /// <returns>
    /// The document to store: the same text when there was nothing to remove (an absent key, an empty column, or a
    /// document nobody can parse), otherwise the document without the key.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is <see langword="null"/> or blank.</exception>
    public static string? WithoutKey(string? json, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        JsonObject payload;

        try
        {
            if (JsonNode.Parse(json) is not JsonObject parsed)
            {
                return json;
            }

            payload = parsed;
        }
        catch (JsonException)
        {
            return json;
        }

        return payload.Remove(key) ? payload.ToJsonString() : json;
    }

    /// <summary>Reads one string key of the document.</summary>
    /// <param name="json">The stored document, or <see langword="null"/>.</param>
    /// <param name="key">The key to read.</param>
    /// <returns>The value, or <see langword="null"/> when the key is absent, of another JSON kind, or unreadable.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is <see langword="null"/> or blank.</exception>
    public static string? ReadString(string? json, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return TryParseObject(json, out var payload)
            && payload[key] is JsonValue value
            && value.TryGetValue<string>(out var text)
                ? text
                : null;
    }

    /// <summary>Reads one <see cref="Guid"/> key of the document.</summary>
    /// <param name="json">The stored document, or <see langword="null"/>.</param>
    /// <param name="key">The key to read.</param>
    /// <returns>The value, or <see langword="null"/> when the key is absent, not a GUID, or unreadable.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is <see langword="null"/> or blank.</exception>
    public static Guid? ReadGuid(string? json, string key) =>
        ReadString(json, key) is { } text && Guid.TryParse(text, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    /// <summary>
    /// Reads the Pacific quota day a row holds a unit on (<see cref="QuotaReservedDayKey"/>).
    /// </summary>
    /// <param name="json">The stored document, or <see langword="null"/>.</param>
    /// <returns>
    /// The day, or <see langword="null"/> when the key is absent, a JSON <c>null</c> (which reads as absent, exactly as
    /// 06.2.6-T1's reader treats it), another JSON kind, another format, or an unreadable document.
    /// </returns>
    public static DateOnly? ReadQuotaDay(string? json) =>
        ReadString(json, QuotaReservedDayKey) is { } text
        && DateOnly.TryParseExact(text, QuotaReservedDayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;

    /// <summary>Formats a Pacific quota day as <see cref="QuotaReservedDayKey"/> stores it.</summary>
    /// <param name="day">The Pacific calendar date the unit was reserved on.</param>
    /// <returns>The value to store (<c>yyyy-MM-dd</c>).</returns>
    public static string FormatQuotaDay(DateOnly day) =>
        day.ToString(QuotaReservedDayFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes the <see cref="ProcessingKey"/> block of a row that just moved to <c>processing</c> (plan §3.2): the
    /// instant the upload finished, the instant 06.2.5-T1's poller gives up claiming the row, and a fresh poll count.
    /// </summary>
    /// <param name="json">The stored document, or <see langword="null"/>.</param>
    /// <param name="startedAt">When the video entered processing.</param>
    /// <param name="deadlineAt"><paramref name="startedAt"/> plus <c>YouTube:Poll:MaxMinutes</c>, as the caller computed it.</param>
    /// <param name="polls">The number of checks already spent on the video (0 on the transition).</param>
    /// <returns>The document to store.</returns>
    public static string? WithProcessing(string? json, DateTimeOffset startedAt, DateTimeOffset deadlineAt, int polls)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(polls);

        return Set(
            json,
            ProcessingKey,
            new JsonObject
            {
                [ProcessingStartedAtKey] = FormatInstant(startedAt),
                [ProcessingDeadlineAtKey] = FormatInstant(deadlineAt),
                [ProcessingPollsKey] = polls,
            });
    }

    /// <summary>Reads the <see cref="ProcessingKey"/> block of a row.</summary>
    /// <param name="json">The stored document, or <see langword="null"/>.</param>
    /// <returns>
    /// The block, or <see langword="null"/> when it is absent, of another JSON kind, missing either instant, or
    /// unreadable. A missing or unreadable <c>polls</c> reads as <c>0</c>: it is a counter, not a claim condition.
    /// </returns>
    public static Processing? ReadProcessing(string? json)
    {
        if (!TryParseObject(json, out var payload)
            || payload[ProcessingKey] is not JsonObject block
            || !TryReadInstant(block[ProcessingStartedAtKey], out var startedAt)
            || !TryReadInstant(block[ProcessingDeadlineAtKey], out var deadlineAt))
        {
            return null;
        }

        return new Processing(
            startedAt,
            deadlineAt,
            block[ProcessingPollsKey] is JsonValue polls && polls.TryGetValue<int>(out var count) ? count : 0);
    }

    /// <summary>Sets one key on a parsed-or-new document.</summary>
    /// <param name="json">The stored document, or <see langword="null"/>.</param>
    /// <param name="key">The key to write.</param>
    /// <param name="value">The value to write.</param>
    /// <returns>The document to store.</returns>
    private static string Set(string? json, string key, JsonNode? value)
    {
        var payload = ParseOrNew(json);
        payload[key] = value;

        return payload.ToJsonString();
    }

    /// <summary>Parses the stored document, answering an empty one for <see langword="null"/>, blank or garbage.</summary>
    /// <param name="json">The stored document, or <see langword="null"/>.</param>
    /// <returns>The object to write into.</returns>
    private static JsonObject ParseOrNew(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    /// <summary>Parses the stored document, answering <see langword="false"/> when it is not a readable object.</summary>
    /// <param name="json">The stored document, or <see langword="null"/>.</param>
    /// <param name="payload">The parsed object, when the document is one.</param>
    /// <returns>Whether <paramref name="payload"/> holds the document.</returns>
    private static bool TryParseObject(string? json, [NotNullWhen(true)] out JsonObject? payload)
    {
        payload = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            payload = JsonNode.Parse(json) as JsonObject;

            return payload is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Formats one instant of the processing block (UTC, round-trip).</summary>
    /// <param name="instant">The instant to store.</param>
    /// <returns>The value to store.</returns>
    private static string FormatInstant(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString(InstantFormat, CultureInfo.InvariantCulture);

    /// <summary>Reads one instant of the processing block.</summary>
    /// <param name="node">The stored node.</param>
    /// <param name="instant">The instant, when the node holds one.</param>
    /// <returns>Whether <paramref name="instant"/> was read.</returns>
    private static bool TryReadInstant(JsonNode? node, out DateTimeOffset instant)
    {
        instant = default;

        return node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && DateTimeOffset.TryParseExact(
                text,
                InstantFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out instant);
    }

    /// <summary>
    /// The stored shape of <see cref="ProcessingKey"/> (plan §3.2): when the video entered processing, the instant
    /// 06.2.5-T1's poller gives up claiming it, and how many checks it already spent.
    /// </summary>
    /// <param name="StartedAt">When the video entered processing (UTC).</param>
    /// <param name="DeadlineAt">When the poller stops claiming the row (UTC).</param>
    /// <param name="Polls">The number of checks already spent on the video.</param>
    public sealed record Processing(DateTimeOffset StartedAt, DateTimeOffset DeadlineAt, int Polls);
}
