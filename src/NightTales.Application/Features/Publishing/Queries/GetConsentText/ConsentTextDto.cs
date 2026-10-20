namespace NightTales.Application.Features.Publishing.Queries.GetConsentText;

/// <summary>
/// One publication-consent text as the wire serves it (06 README D7, 10 CR-17):
/// <paramref name="Version"/> is the live <c>YouTube:ConsentTextVersion</c> the client echoes back in
/// <c>POST /api/v1/publications/{id}/consent</c>, and <paramref name="Text"/> is the embedded <c>pc.v1</c>
/// markdown of the requested locale, rendered as-is.
/// </summary>
/// <param name="Version">The configured consent-text version, for example <c>pc.v1</c>.</param>
/// <param name="Text">The embedded consent text (Vietnamese or English), markdown, never empty.</param>
public sealed record ConsentTextDto(string Version, string Text);
