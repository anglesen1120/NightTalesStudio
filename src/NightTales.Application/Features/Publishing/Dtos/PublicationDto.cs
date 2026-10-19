using NightTales.Domain.Publishing;

// The class below spells the wire value of the stored consent source, and one of its members is named
// `ConsentSource`: these aliases name the enum's type and its wire constants where the member would shadow it,
// the same trick `Publication` uses for its computed `Platform` member.
using ConsentSourceType = NightTales.Domain.Publishing.ConsentSource;

namespace NightTales.Application.Features.Publishing.Dtos;

/// <summary>
/// One publication of an episode, as <c>POST</c>/<c>GET /episodes/{id}/publications</c> answers it
/// (06.2.1-T1 task §Design, 06 README D6): the row's own state, the metadata the upload will use, and the
/// instant every later consent/cancel/PATCH must echo as <c>If-Match</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>No secret, no pinning detail, no authorization id</b> (06 README D17): no token column, no
/// <c>oauth_client_id</c>, no <c>autopilot_authorization_id</c> — the connection is summarised by
/// <see cref="Channel"/>, and the raw <c>platform_payload</c> never leaves the mapper.
/// </para>
/// <para>
/// <see cref="Privacy"/> is the <b>effective</b> value the row stores, not the one the client sent: on a
/// pre-audit Google client a submitted <c>public</c> is stored (and answered) as <c>private</c>, and
/// <see cref="PreAudit"/> plus <see cref="RequestedPrivacy"/> are the two members that let the UI explain the
/// lock and show what was originally asked for (06 CR-11, YR20). Both are read straight off
/// <c>platform_payload</c>, are written once by the create and are never writable again — a PATCH to a
/// non-private value on a pre-audit row is refused with 409 <c>publication_privacy_locked</c> (06.2.2-T3).
/// </para>
/// <para>
/// The two strings that carry an enum are <b>wire values, not enum members</b> — <see cref="Status"/> is
/// <c>awaiting_consent</c>/<c>scheduled</c>/… and <see cref="ConsentSource"/> is <c>user</c>/<c>autopilot</c> —
/// for the same reason <c>ChannelConnectionDto</c> spells them: the JSON member is a string the column, the
/// CHECK constraint and the web page all compare against one form.
/// </para>
/// </remarks>
/// <param name="Id">The <c>publications.id</c>: what consent, cancel and PATCH address.</param>
/// <param name="EpisodeId">The episode this publication belongs to.</param>
/// <param name="Channel">The connection this publication uploads through, without its tokens.</param>
/// <param name="Status">The status wire value (<c>awaiting_consent</c>, <c>scheduled</c>, …).</param>
/// <param name="Title">The title YouTube will receive, unchanged from the submission.</param>
/// <param name="Description">The stored description: the submission with the render's music attribution appended.</param>
/// <param name="Tags">The submitted tags, in order.</param>
/// <param name="Privacy">The effective privacy (<c>public</c>, <c>unlisted</c>, <c>private</c>).</param>
/// <param name="PreAudit">Whether the pre-audit override replaced the submitted privacy; never writable after create.</param>
/// <param name="RequestedPrivacy">The privacy the owner submitted while the override holds it back, or <see langword="null"/>.</param>
/// <param name="PublishAt">The scheduled instant, or <see langword="null"/> for an immediate upload.</param>
/// <param name="VetoEndsAt">When the owner's veto window closes, or <see langword="null"/> for a manual create.</param>
/// <param name="ConsentAt">When the owner consented, or <see langword="null"/> while awaiting consent.</param>
/// <param name="ConsentSource">The consent source wire value (<c>user</c>/<c>autopilot</c>), or <see langword="null"/>.</param>
/// <param name="ExternalPostId">YouTube's video id, or <see langword="null"/> until 06.2.3-T2 uploads.</param>
/// <param name="ExternalUrl">The watch URL, or <see langword="null"/> until 06.2.3-T2 writes it.</param>
/// <param name="ErrorCode">The last platform failure's code, or <see langword="null"/>.</param>
/// <param name="NextAttemptAt">When the upload (or its next retry) is due, or <see langword="null"/>.</param>
/// <param name="Etag">The row's <c>ETag</c> (<c>"pu-&lt;xmin&gt;"</c>) every mutation must echo.</param>
public sealed record PublicationDto(
    Guid Id,
    Guid EpisodeId,
    PublicationChannelDto Channel,
    string Status,
    string Title,
    string Description,
    IReadOnlyList<string> Tags,
    string Privacy,
    bool PreAudit,
    string? RequestedPrivacy,
    DateTimeOffset? PublishAt,
    DateTimeOffset? VetoEndsAt,
    DateTimeOffset? ConsentAt,
    string? ConsentSource,
    string? ExternalPostId,
    string? ExternalUrl,
    string? ErrorCode,
    DateTimeOffset? NextAttemptAt,
    string Etag);

/// <summary>
/// The connection a publication uploads through, as the publication endpoints answer it (06.2.1-T1 task
/// §Design): an id and the display name — never the tokens, the pinning client or the scope (06 README D17).
/// </summary>
/// <remarks>
/// <see cref="DisplayName"/> is nullable although the task file's sketch spells it <c>string</c>: 06.4.3-T1's
/// purge nulls the identity of an <em>already revoked</em> connection, and D15 cancels that connection's
/// publications instead of deleting them, so a terminal publication row can outlive the display name it was
/// created with. The member is therefore declared as the storage is, exactly like
/// <c>ChannelConnectionDto.DisplayName</c> (the released sibling of this row).
/// </remarks>
/// <param name="Id">The <c>channel_connections.id</c>.</param>
/// <param name="DisplayName">The channel title, or <see langword="null"/> for a purged row.</param>
public sealed record PublicationChannelDto(Guid Id, string? DisplayName);

/// <summary>
/// The wire spelling of <see cref="PublicationStatus"/> and <see cref="ConsentSource"/> as the API contract
/// states them (06 README D6): lower case, one word, exactly the text the column stores and the CHECK
/// constraints admit.
/// </summary>
/// <remarks>
/// Hand-written and exhaustive for the reason <c>ChannelConnectionWire</c> is: the JSON members are strings, so
/// the serializer's enum converter never sees these values, and a member added to either enum without a wire
/// value throws here instead of shipping an unmapped string.
/// </remarks>
internal static class PublicationWire
{
    /// <summary>The wire value of <paramref name="status"/>.</summary>
    /// <param name="status">The stored status.</param>
    /// <returns><c>awaiting_consent</c>, <c>scheduled</c>, <c>uploading</c>, <c>processing</c>, <c>published</c>, <c>failed</c> or <c>cancelled</c>.</returns>
    internal static string Status(PublicationStatus status) => status switch
    {
        PublicationStatus.AwaitingConsent => "awaiting_consent",
        PublicationStatus.Scheduled => "scheduled",
        PublicationStatus.Uploading => "uploading",
        PublicationStatus.Processing => "processing",
        PublicationStatus.Published => "published",
        PublicationStatus.Failed => "failed",
        PublicationStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(
            nameof(status),
            status,
            "No wire value is declared for this status."),
    };

    /// <summary>The wire value of the stored consent source, or <see langword="null"/> when consent is not recorded yet.</summary>
    /// <param name="source">The stored consent source; the aggregate keeps the wire string, not the enum.</param>
    /// <returns><c>user</c>, <c>autopilot</c> or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The stored text is not a declared consent source.</exception>
    internal static string? ConsentSource(string? source) => source switch
    {
        null => null,
        _ when source == ConsentSourceType.User.ToWire() || source == ConsentSourceType.Autopilot.ToWire() => source,
        _ => throw new ArgumentOutOfRangeException(
            nameof(source),
            source,
            "No wire value is declared for this consent source."),
    };
}

/// <summary>
/// The three privacy wire values of 06 README D6, spelled once: the create command compares the submitted value
/// against them and stores the effective one, and the pre-audit override of a not-yet-approved Google client
/// replaces any of them with <see cref="Private"/> (YR2).
/// </summary>
/// <remarks>
/// The column is <c>text</c> with <c>ck_publications_privacy</c>, not an enum, so these constants — not an enum
/// member — are what the command and the domain factory exchange.
/// </remarks>
internal static class PublicationPrivacy
{
    /// <summary>Everybody can find the video.</summary>
    internal const string Public = "public";

    /// <summary>Only the link finds the video.</summary>
    internal const string Unlisted = "unlisted";

    /// <summary>Only the owner sees the video: the pre-audit override's value.</summary>
    internal const string Private = "private";
}
