namespace NightTales.Application.Features.Autopilot.Dtos;

/// <summary>
/// The current authorization of a series on the wire (02.8.3 plan §3.7): which revision is live, the template
/// version it was rendered from, the digest the owner accepted and when.
/// </summary>
/// <remarks>
/// The rendered text itself is <see cref="AuthorizationTextDto"/>, not part of this record: a row keeps the
/// digest and never the words (00 overview §6, plan §3.7), and the text the owner may re-read is re-rendered
/// from the stored settings.
/// </remarks>
/// <param name="Version">The 1-based version of the live acceptance.</param>
/// <param name="TextVersion">The template version the text was rendered from, e.g. <c>autopilot-auth.v1</c>.</param>
/// <param name="TextHash">The lowercase SHA-256 hex of the accepted text.</param>
/// <param name="AcceptedAt">When the owner accepted it.</param>
public sealed record AuthorizationDto(int Version, string TextVersion, string TextHash, DateTimeOffset AcceptedAt);
