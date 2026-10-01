using NightTales.Application.Features.Email;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Email;

/// <summary>Which of the two files of a template pair one resource is (09 README BD9).</summary>
public enum EmailTemplateFormat
{
    /// <summary>The <c>.txt</c> body, sent as <c>text/plain</c>; it also carries the subject.</summary>
    Text,

    /// <summary>The <c>.html</c> body, sent as <c>text/html</c>.</summary>
    Html,
}

/// <summary>
/// One embedded template file (09 README BD9, 09.2.6-T3):
/// <c>Templates/Email/&lt;name&gt;.&lt;locale&gt;.{txt,html}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The subject convention.</b> The <c>.txt</c> file's first line is <c>Subject: &lt;text&gt;</c>; the
/// renderer takes it from there and drops it from the body. The file-name pattern is the one BD9 fixes
/// (<c>&lt;name&gt;.&lt;locale&gt;.{txt,html}</c>), so the subject cannot live in a third file without
/// changing that contract, and keeping it inside the text file keeps it next to the body it belongs to, per
/// locale, with the same <c>{{key}}</c> placeholders [INFERENCE 09.2.6-T3].
/// </para>
/// <para>
/// The record is what the validator and the renderer exchange, so a test can validate a hand-built set —
/// including the broken ones the boot must refuse — without an assembly of embedded resources.
/// </para>
/// </remarks>
/// <param name="Name">The catalog template name.</param>
/// <param name="Locale">The locale of this file.</param>
/// <param name="Format">Whether this is the text or the html file.</param>
/// <param name="Content">The file's whole content.</param>
public sealed record EmailTemplateResource(
    string Name,
    Language Locale,
    EmailTemplateFormat Format,
    string Content)
{
    /// <summary>The file name as the pattern spells it, for example <c>password_reset.vi.txt</c>.</summary>
    public string FileName => FileNameOf(Name, Locale, Format);

    /// <summary>The file name of one template, locale and format.</summary>
    /// <param name="name">The catalog template name.</param>
    /// <param name="locale">The locale.</param>
    /// <param name="format">The file format.</param>
    public static string FileNameOf(string name, Language locale, EmailTemplateFormat format) =>
        string.Concat(name, ".", EmailTemplateCatalog.LocaleName(locale), ".", ExtensionOf(format));

    /// <summary>The file-name extension of one format.</summary>
    /// <param name="format">The file format.</param>
    public static string ExtensionOf(EmailTemplateFormat format) =>
        format == EmailTemplateFormat.Html ? "html" : "txt";
}

/// <summary>
/// A template set that cannot be shipped (09 README BD9, 09.2.6-T3): a missing or orphan file, a
/// placeholder the catalog does not declare, a duplicate or an unparsable file name.
/// </summary>
/// <remarks>
/// The message always names the file (and the key), because the failure is fixed by editing exactly that
/// file; the composition root throws it, so a host with a broken template set never starts.
/// </remarks>
/// <param name="message">The reason, naming the file and — when there is one — the offending key.</param>
public sealed class EmailTemplateException(string message) : Exception(message);
