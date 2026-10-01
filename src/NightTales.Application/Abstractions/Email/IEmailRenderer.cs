using NightTales.Domain.Common;

namespace NightTales.Application.Abstractions.Email;

/// <summary>
/// The rendered bodies of one e-mail (09 README BD9, 09.2.6-T3): what the job puts into the
/// <c>MimeMessage</c>.
/// </summary>
/// <param name="Subject">The rendered subject; the template keeps it next to its bodies (see the template contract).</param>
/// <param name="TextBody">The <c>text/plain</c> body, with the substituted values verbatim.</param>
/// <param name="HtmlBody">The <c>text/html</c> body, with every substituted value HTML-encoded.</param>
public sealed record RenderedEmail(string Subject, string TextBody, string HtmlBody);

/// <summary>
/// Renders one template of one locale with the values the job collected (09 README BD9, 09.2.6-T3):
/// the producer's allow-listed parameters plus, for a token-bearing template, the values its
/// <c>IEmailTokenMinter</c> produced at send time.
/// </summary>
/// <remarks>
/// <para>
/// The renderer is pure: it reads the embedded <c>{{key}}</c> template and returns the bodies, so the
/// token a minter just created is used here and never persisted (09 README §B10 WT17). Values are
/// HTML-encoded in the HTML body and verbatim in the text body, so a display name that contains markup
/// cannot inject it.
/// </para>
/// <para>
/// A value the template does not declare, and a placeholder the caller did not provide, are both
/// programming errors: the first cannot happen because <c>QueueAsync</c> validates the allow-list, the
/// second because <c>EmailTemplateProvider</c> validated at composition time that every placeholder of
/// every shipped file is a declared key. Both throw instead of rendering an empty string.
/// </para>
/// </remarks>
public interface IEmailRenderer
{
    /// <summary>Renders one template.</summary>
    /// <param name="templateName">A catalog template name.</param>
    /// <param name="locale">The locale; both files of the pair are rendered from the same values.</param>
    /// <param name="values">Every value the job collected for this send.</param>
    /// <returns>The subject and the two bodies.</returns>
    RenderedEmail Render(string templateName, Language locale, IReadOnlyDictionary<string, string> values);
}
