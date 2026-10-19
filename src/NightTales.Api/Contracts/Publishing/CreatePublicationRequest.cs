namespace NightTales.Api.Contracts.Publishing;

/// <summary>
/// The body of <c>POST /api/v1/episodes/{id}/publications</c> (06.2.1-T1 task §API, 06 README D6): the metadata
/// YouTube is given for the episode, and which connected channel publishes it.
/// </summary>
/// <remarks>
/// <para>
/// <c>autopilotAuthorizationId</c> is deliberately <b>not</b> a member: 02.8.3 calls the command in-process, so
/// the public route accepts no caller-supplied authorization and every publication created here is a manual one
/// (C1/[INFERENCE]). The owner comes from the session, the platform from the connection, and the veto window is
/// manual-only — none of the three is a body member.
/// </para>
/// <para>
/// <c>privacy</c> is the owner's <em>requested</em> value: when the video is not audit-approved yet the handler
/// commits <c>private</c> and keeps the requested value in <c>platform_payload.requestedPrivacy</c> (D12), and
/// the 201 answers the enforced value. The <c>publishAt</c> instant must be at least
/// <c>YouTubeOptions.PublishLeadMinutes</c> ahead (D6). The shape of every member is checked by the command
/// validator and the strings by <c>YouTubeMetadataRules</c>, so a bad body is a coded 400
/// <c>validation_failed</c> under the offending member.
/// </para>
/// </remarks>
/// <param name="ChannelConnectionId">The caller's own connected channel that publishes the episode.</param>
/// <param name="Title">The video title, 1–100 characters with no <c>&lt;</c>/<c>&gt;</c>.</param>
/// <param name="Description">The description, 1–5,000 UTF-8 bytes once the render's attribution is appended.</param>
/// <param name="Tags">
/// The tags, at most 15, YouTube-counted length 500; may be empty. A body that omits the member (or sends
/// <c>null</c>) means "no tags": the route turns it into the empty list the command takes, because YouTube
/// accepts a video without tags.
/// </param>
/// <param name="Privacy">The requested visibility: <c>public</c>, <c>unlisted</c> or <c>private</c>.</param>
/// <param name="PublishAt">
/// The instant to publish at, or <see langword="null"/> for "as soon as it is processed"; at least the publish
/// lead time ahead when it is set.
/// </param>
public sealed record CreatePublicationRequest(
    Guid ChannelConnectionId,
    string Title,
    string Description,
    IReadOnlyList<string>? Tags,
    string Privacy,
    DateTimeOffset? PublishAt);
