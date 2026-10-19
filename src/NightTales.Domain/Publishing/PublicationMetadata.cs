namespace NightTales.Domain.Publishing;

/// <summary>
/// The YouTube metadata a publication is created with (06 README D6): the title, the description, the tags, the
/// <em>effective</em> privacy and the optional scheduled instant, as the create command hands them to
/// <see cref="Publication.Create"/>.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="Privacy"/> is the effective value, never the submitted one (06 README D12): the handler
/// replaces the owner's choice with <c>private</c> when the video is not audit-approved yet and stamps the
/// submitted value into <c>platform_payload.requestedPrivacy</c> instead. The aggregate stores what it is given,
/// so the pre-audit override lives in exactly one place.
/// </para>
/// <para>
/// <paramref name="Description"/> already carries the render's music attribution (C8): the handler appends it
/// through <see cref="YouTubeMetadataRules.AppendAttribution"/> <em>before</em> it validates, because the 5,000
/// byte cap counts the bytes of the description that is actually uploaded (F08).
/// </para>
/// </remarks>
/// <param name="Title">The video title; 1–100 characters, no <c>&lt;</c>/<c>&gt;</c> (G4).</param>
/// <param name="Description">The description, attribution included; 1–5,000 UTF-8 bytes (G4).</param>
/// <param name="Tags">The tags, in the order the client sent them; at most 15, G4-counted length 500 (F09).</param>
/// <param name="Privacy">The effective visibility: <c>public</c>, <c>unlisted</c> or <c>private</c> (G6).</param>
/// <param name="PublishAt">
/// The instant the owner asked for, or <see langword="null"/> for "as soon as it is processed"; at least
/// <c>YouTubeOptions.PublishLeadMinutes</c> ahead when it is set (06 README D6).
/// </param>
public sealed record PublicationMetadata(
    string Title,
    string Description,
    IReadOnlyList<string> Tags,
    string Privacy,
    DateTimeOffset? PublishAt);
