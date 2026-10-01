namespace NightTales.Domain.Catalog;

/// <summary>
/// One image prompt of a scene (03 README D1): the text OpenAI receives, the same text without the
/// negative clause for the moderation checker, and the SHA-256 the relay attributes a call by.
/// </summary>
/// <param name="Text">The styled template with the term substituted, then <see cref="NegativeClause.Current"/>.</param>
/// <param name="ModerationText">The styled template alone; the moderation checker (02.8.2-T2) never sees the clause.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the UTF-8 <paramref name="Text"/> (03 README D5).</param>
public sealed record ScenePrompt(string Text, string ModerationText, string Sha256);
