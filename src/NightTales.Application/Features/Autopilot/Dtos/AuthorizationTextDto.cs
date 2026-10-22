namespace NightTales.Application.Features.Autopilot.Dtos;

/// <summary>
/// One rendered authorization text as <c>GET /series/{id}/autopilot</c> shows it (02.8.3 plan §3.7): the
/// sentence the owner reads, the locale it was rendered in, and the digest
/// <c>POST …/autopilot/authorization</c> expects back.
/// </summary>
/// <remarks>
/// The text is re-rendered from the stored settings on every read by
/// <c>AuthorizationTextRenderer</c> — the single render point — so what this DTO carries is exactly what the
/// accept route compares against, and a client that echoes <see cref="TextHash"/> with
/// <see cref="TextVersion"/> accepts the text it displayed.
/// </remarks>
/// <param name="TextVersion">The template version, <c>AuthorizationTextRenderer.CurrentTextVersion</c> today.</param>
/// <param name="Text">The rendered sentence, NFC-normalised, every placeholder substituted.</param>
/// <param name="TextHash">The lowercase SHA-256 hex of <paramref name="Text"/>.</param>
/// <param name="Locale">The wire locale the text was rendered in, <c>vi</c> or <c>en</c>.</param>
public sealed record AuthorizationTextDto(string TextVersion, string Text, string TextHash, string Locale);
