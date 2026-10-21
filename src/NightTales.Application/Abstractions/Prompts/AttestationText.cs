namespace NightTales.Application.Abstractions.Prompts;

/// <summary>
/// One language's true-crime attestation clause text and the version the release manifest declares for
/// its file (02.3.3-T1, 02 README T29).
/// </summary>
/// <remarks>
/// The text is the wording the owner ticks against; <see cref="Version"/> is what a stored tick is
/// compared to, so a wording change ships as a new file, a new manifest version (<c>ta.v1</c> →
/// <c>ta.v2</c>) and a new key, and every tick recorded against the old text stops being accepted.
/// </remarks>
/// <param name="Version">The manifest's version marker for the file, for example <c>ta.v1</c>.</param>
/// <param name="Text">The clause text exactly as it ships, with no placeholder substituted.</param>
public sealed record AttestationText(string Version, string Text);
