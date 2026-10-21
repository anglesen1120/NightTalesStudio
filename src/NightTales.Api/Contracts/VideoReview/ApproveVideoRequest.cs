namespace NightTales.Api.Contracts.VideoReview;

/// <summary>
/// The body of <c>POST /api/v1/episodes/{id}/video/approve</c> (09.7.1 plan §Design): the owner's optional
/// note.
/// </summary>
/// <remarks>
/// The body itself is optional — an empty request or <c>{}</c> is a valid approval — and it carries the note
/// only: the route binds the episode and the command owns the transition, so a client can never name a status
/// or an episode. The note is user text, so it is never copied into an audit row or a log line (BD15).
/// </remarks>
/// <param name="Note">The optional note, at most 500 characters.</param>
public sealed record ApproveVideoRequest(string? Note = null);
