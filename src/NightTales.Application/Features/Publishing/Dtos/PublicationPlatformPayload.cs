using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightTales.Application.Features.Publishing.Dtos;

/// <summary>
/// The <c>platform_payload</c> keys this layer reads (06 README D6, YR2): the two 06.2.1-T1 owns —
/// <c>preAudit</c>, the flag that tells 06.2.2-T2 the stored privacy is not the submitted one yet, and
/// <c>requestedPrivacy</c>, the value the owner asked for and the client rejected before Google approved it —
/// and <c>quotaReservedDay</c>, the day the upload job's unit is reserved on, which 06.2.6-T1's cancel reads to
/// release it.
/// </summary>
/// <remarks>
/// <para>
/// The column is <c>jsonb</c> and 06.2.3-T2 / 06.2.5-T1 add their own keys to it (the upload's video id, the
/// go-live state), so the two sides read and write <em>keys</em> and never a whole document: a row whose
/// payload was written by a later task still answers <see cref="RequestedPrivacyKey"/> and
/// <see cref="QuotaReservedDayKey"/> here.
/// </para>
/// <para>
/// Absent, malformed or of the wrong JSON kind reads back as "no pre-audit payload" / "no reserved day", never as
/// an error: the flag is a hint for the next transition, not a contract the row must satisfy (the 02
/// autopilot-settings precedent, TL ruling #3).
/// </para>
/// </remarks>
internal static class PublicationPlatformPayload
{
    /// <summary>The <c>true</c> flag that marks a row whose privacy the pre-audit override replaced.</summary>
    internal const string PreAuditKey = "preAudit";

    /// <summary>The privacy the owner submitted, kept for the release after approval.</summary>
    internal const string RequestedPrivacyKey = "requestedPrivacy";

    /// <summary>
    /// The quota day (<c>yyyy-MM-dd</c>, the Pacific day) the row holds a reserved unit on, written by the upload
    /// job when it reserves and <em>removed</em> by it when it releases or finishes (06.2.3-T2, 06.2.6-T1). Its
    /// presence is what makes a cancel release a unit: a row that never reached the reserve step carries no key.
    /// </summary>
    /// <remarks>
    /// The writer removes the key rather than storing <c>null</c>, so a JSON <c>null</c> under it reads as absent —
    /// a row with nothing reserved — and never costs a warning line (see <see cref="ReadQuotaReservedDay"/>).
    /// </remarks>
    internal const string QuotaReservedDayKey = "quotaReservedDay";

    /// <summary>The one accepted spelling of the key's value.</summary>
    private const string QuotaReservedDayFormat = "yyyy-MM-dd";

    /// <summary>Serializes the pre-audit payload of a row whose effective privacy is <c>private</c>.</summary>
    /// <param name="requestedPrivacy">The submitted privacy wire value (<c>public</c>, <c>unlisted</c>, <c>private</c>).</param>
    /// <returns>The JSON the <c>platform_payload</c> column stores.</returns>
    internal static string WritePreAudit(string requestedPrivacy) =>
        JsonSerializer.Serialize(new PreAuditPayload(true, requestedPrivacy));

    /// <summary>Reads the pre-audit payload of a stored row.</summary>
    /// <param name="json">The <c>platform_payload</c> column, or <see langword="null"/>.</param>
    /// <returns>The payload, or <see langword="null"/> when the row carries no pre-audit flag.</returns>
    internal static PreAuditPayload? ReadPreAudit(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(PreAuditKey, out var preAudit)
                || preAudit.ValueKind != JsonValueKind.True)
            {
                return null;
            }

            return new PreAuditPayload(
                true,
                document.RootElement.TryGetProperty(RequestedPrivacyKey, out var requested)
                    && requested.ValueKind == JsonValueKind.String
                        ? requested.GetString()
                        : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the quota day a row holds a unit on (06.2.6-T1): the value of <see cref="QuotaReservedDayKey"/> as an
    /// ISO day, or <see langword="null"/> when the key is not there. A payload written by a later task still
    /// answers here, because only that key is read and every other key is left untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Absent, blank, malformed, non-object, non-string and unparseable all read back as "no reserved day", never as
    /// an error: a cancel must not fail because a hand-edited row carries a payload nobody can read (06.2.6-T1 task
    /// §Failure handling).
    /// </para>
    /// <para>
    /// <b>One parse, one answer.</b> The day and the "present but unusable" flag come out of the same read, so a
    /// caller parses the payload exactly once per cancel and can never pair a day from one parse with a warning
    /// from another. A JSON <c>null</c> under the key — the value 06.2.3-T2 never writes, because it removes the
    /// key — is plain <em>absent</em>: no day, no flag, no warning line.
    /// </para>
    /// </remarks>
    /// <param name="json">The <c>platform_payload</c> column, or <see langword="null"/>.</param>
    /// <returns>
    /// The reserved Pacific quota day (or <see langword="null"/> when there is nothing reserved) and whether the
    /// key was present but unusable — the flag the caller logs a skipped release with, and never set for an absent
    /// key.
    /// </returns>
    internal static (DateOnly? Day, bool Unreadable) ReadQuotaReservedDay(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, false);
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, true);
            }

            if (!document.RootElement.TryGetProperty(QuotaReservedDayKey, out var reservedDay)
                || reservedDay.ValueKind == JsonValueKind.Null)
            {
                return (null, false);
            }

            return reservedDay.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(
                    reservedDay.GetString(),
                    QuotaReservedDayFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var day)
                    ? (day, false)
                    : (null, true);
        }
        catch (JsonException)
        {
            return (null, true);
        }
    }

    /// <summary>
    /// The stored shape: <c>preAudit</c> is always <c>true</c> (the row exists only on the override path) and
    /// <c>requestedPrivacy</c> is the submitted value, or <see langword="null"/> on a payload that lost it.
    /// </summary>
    /// <param name="PreAudit">Whether the stored privacy was replaced by the pre-audit override.</param>
    /// <param name="RequestedPrivacy">The submitted privacy, or <see langword="null"/>.</param>
    internal sealed record PreAuditPayload(
        [property: JsonPropertyName(PreAuditKey)] bool PreAudit,
        [property: JsonPropertyName(RequestedPrivacyKey)] string? RequestedPrivacy);
}
