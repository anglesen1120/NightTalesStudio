using System.Net;
using NightTales.Application.Abstractions.Email;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Email;

/// <summary>
/// Renders one template pair with the values of one send (09 README BD9, 09.2.6-T3): pure, synchronous and
/// free of state, so the same values always produce the same message.
/// </summary>
/// <remarks>
/// <para>
/// Substituted values are HTML-encoded in the html body and verbatim in the text body, so a display name
/// that carries markup cannot inject it. A value the template does not declare, and a placeholder the
/// caller did not provide, both throw: the first is impossible through <c>IEmailSender</c> (the allow-list
/// is enforced when the message is queued) and the second is a bug in the code that queued it, and neither
/// should degrade into a message with a literal <c>{{key}}</c> in it.
/// </para>
/// <para>
/// The subject comes from the text file's first line (see <see cref="EmailTemplateResource"/>): its
/// placeholders are rendered with the same values, which is how a subject can name the user or the
/// deadline without a second source of truth.
/// </para>
/// </remarks>
/// <param name="provider">The validated template set.</param>
internal sealed class EmailRenderer(EmailTemplateProvider provider) : IEmailRenderer
{
    /// <inheritdoc />
    public RenderedEmail Render(string templateName, Language locale, IReadOnlyDictionary<string, string> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateName);
        ArgumentNullException.ThrowIfNull(values);

        var text = Substitute(
            provider.Get(templateName, locale, EmailTemplateFormat.Text),
            values,
            encodeForHtml: false);

        var html = Substitute(
            provider.Get(templateName, locale, EmailTemplateFormat.Html),
            values,
            encodeForHtml: true);

        return new RenderedEmail(SubjectOf(text), BodyOf(text), html);
    }

    /// <summary>
    /// Replaces every <c>{{key}}</c> of one file. The text file's subject line is part of the content, so
    /// the subject's placeholders are rendered with the bodies'.
    /// </summary>
    /// <param name="resource">The file being rendered.</param>
    /// <param name="values">Every value the send collected.</param>
    /// <param name="encodeForHtml">Whether the value must be HTML-encoded (the html body).</param>
    /// <exception cref="EmailTemplateException">A placeholder has no value.</exception>
    private static string Substitute(
        EmailTemplateResource resource,
        IReadOnlyDictionary<string, string> values,
        bool encodeForHtml) =>
        EmailTemplateProvider.PlaceholderPattern.Replace(resource.Content, match =>
        {
            var key = match.Groups["key"].Value;

            if (!values.TryGetValue(key, out var value))
            {
                throw new EmailTemplateException(
                    $"E-mail template file '{resource.FileName}' needs a value for '{{{{{key}}}}}' that this send did not collect.");
            }

            return encodeForHtml ? WebUtility.HtmlEncode(value) : value;
        });

    /// <summary>The rendered subject of a text file.</summary>
    /// <param name="renderedText">The rendered text file; the composition-time validation proved it has one.</param>
    private static string SubjectOf(string renderedText) =>
        EmailTemplateProvider.SubjectOf(renderedText)
        ?? throw new EmailTemplateException(
            $"The rendered text body has no '{EmailTemplateProvider.SubjectPrefix} <text>' line, which the template validation requires.");

    /// <summary>The rendered text body: everything after the subject line.</summary>
    /// <param name="renderedText">The rendered text file.</param>
    private static string BodyOf(string renderedText)
    {
        var newline = renderedText.IndexOf('\n');

        return newline < 0
            ? string.Empty
            : renderedText[(newline + 1)..].TrimStart('\r', '\n');
    }
}
