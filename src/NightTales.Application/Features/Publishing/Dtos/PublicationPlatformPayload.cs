using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightTales.Application.Features.Publishing.Dtos;

/// <summary>
/// The two <c>platform_payload</c> keys 06.2.1-T1 owns (06 README D6, YR2): <c>preAudit</c>, the flag that
/// tells 06.2.2-T2 the stored privacy is not the submitted one yet, and <c>requestedPrivacy</c>, the value the
/// owner asked for and the client rejected before Google approved it.
/// </summary>
/// <remarks>
/// <para>
/// The column is <c>jsonb</c> and 06.2.3-T2 / 06.2.5-T1 add their own keys to it (the upload's video id, the
/// go-live state), so the two sides read and write <em>keys</em> and never a whole document: a row whose
/// payload was written by a later task still answers <see cref="RequestedPrivacyKey"/> here.
/// </para>
/// <para>
/// Absent, malformed or of the wrong JSON kind reads back as "no pre-audit payload", never as an error: the
/// flag is a hint for the next transition, not a contract the row must satisfy (the 02 autopilot-settings
/// precedent, TL ruling #3).
/// </para>
/// </remarks>
internal static class PublicationPlatformPayload
{
    /// <summary>The <c>true</c> flag that marks a row whose privacy the pre-audit override replaced.</summary>
    internal const string PreAuditKey = "preAudit";

    /// <summary>The privacy the owner submitted, kept for the release after approval.</summary>
    internal const string RequestedPrivacyKey = "requestedPrivacy";

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
    /// The stored shape: <c>preAudit</c> is always <c>true</c> (the row exists only on the override path) and
    /// <c>requestedPrivacy</c> is the submitted value, or <see langword="null"/> on a payload that lost it.
    /// </summary>
    /// <param name="PreAudit">Whether the stored privacy was replaced by the pre-audit override.</param>
    /// <param name="RequestedPrivacy">The submitted privacy, or <see langword="null"/>.</param>
    internal sealed record PreAuditPayload(
        [property: JsonPropertyName(PreAuditKey)] bool PreAudit,
        [property: JsonPropertyName(RequestedPrivacyKey)] string? RequestedPrivacy);
}
