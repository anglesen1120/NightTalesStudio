using System.Text.RegularExpressions;
using NightTales.Application.Features.Email;
using NightTales.Domain.Common;

namespace NightTales.Infrastructure.Email;

/// <summary>
/// The template set of the platform (09 README BD9, 09.2.6-T3): it loads every
/// <c>&lt;name&gt;.&lt;locale&gt;.{txt,html}</c> file shipped beside the assembly, validates the whole set
/// against the catalog and serves one file at a time to the renderer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Validation is composition-time and it stops the boot</b>: every catalog entry must ship all four files
/// (<c>vi</c>/<c>en</c> × <c>txt</c>/<c>html</c>), every <c>{{key}}</c> of every file must be a key the
/// catalog declares for that template, a file without a catalog entry is an error, and the text file must
/// carry a <c>Subject:</c> line. That is what makes a template and the code that fills it unable to drift:
/// both ship in one build, so a mismatch is a boot failure and never a message with a literal
/// <c>{{displayName}}</c> in it.
/// </para>
/// <para>
/// The files are output files — <c>Content</c>/<c>CopyToOutputDirectory</c>, the mechanism the
/// <c>seed/</c> catalogs of 09.2.5-T1 use and the one CR-13 of this task's notes records — so a published
/// image carries them beside the assembly that renders with them, and the assembly that carries the
/// renderer is the assembly whose build copies the text.
/// </para>
/// </remarks>
public sealed class EmailTemplateProvider
{
    /// <summary>The folder of the template files, relative to the output directory.</summary>
    public const string TemplateFolder = "Email/Templates/Email";

    /// <summary>The line the text file starts with; see <see cref="EmailTemplateResource"/>.</summary>
    public const string SubjectPrefix = "Subject:";

    /// <summary>A template placeholder: <c>{{key}}</c>, keys as the catalog spells them.</summary>
    public static readonly Regex PlaceholderPattern = new(
        @"\{\{(?<key>[A-Za-z0-9_]+)\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<(string Name, Language Locale, EmailTemplateFormat Format), EmailTemplateResource> _files;

    /// <summary>Indexes and validates an explicit set of resources.</summary>
    /// <param name="resources">The set to validate; see <see cref="ValidateOrThrow"/> for the rules.</param>
    /// <exception cref="EmailTemplateException">The set cannot be shipped.</exception>
    public EmailTemplateProvider(IEnumerable<EmailTemplateResource> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var list = resources as IReadOnlyCollection<EmailTemplateResource> ?? [.. resources];
        ValidateOrThrow(list);

        _files = list.ToDictionary(
            resource => (resource.Name, resource.Locale, resource.Format),
            resource => resource);
    }

    /// <summary>The template set this build ships.</summary>
    /// <returns>The provider over the deployed template files, validated.</returns>
    /// <exception cref="EmailTemplateException">A file is missing, orphaned or unusable.</exception>
    public static EmailTemplateProvider FromDeployedFiles() => new(ReadDeployedFiles());

    /// <summary>
    /// Reads every template file of <see cref="TemplateFolder"/> from <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    /// <remarks>
    /// The files are copied into the output by this project's <c>Content</c> items — the same mechanism the
    /// <c>seed/</c> catalogs of 09.2.5-T1 use — so a published image carries them and the referencing host
    /// or test project finds them beside its own assembly. A missing folder is a packaging failure, and it
    /// fails here rather than by sending a message with a literal placeholder in it.
    /// </remarks>
    /// <returns>The resources, ordered by file name.</returns>
    /// <exception cref="EmailTemplateException">The folder is absent or a file name is unusable.</exception>
    public static IReadOnlyList<EmailTemplateResource> ReadDeployedFiles()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            TemplateFolder.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(directory))
        {
            throw new EmailTemplateException(
                $"The e-mail template folder '{directory}' is missing: every template of 09 README BD9 ships "
                + "as a file of Content/CopyToOutputDirectory beside the assembly that renders it.");
        }

        var resources = new List<EmailTemplateResource>();

        foreach (var path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            resources.Add(Parse(Path.GetFileName(path), File.ReadAllText(path)));
        }

        return resources;
    }

    /// <summary>
    /// Validates a whole template set: the catalog and the files must describe exactly the same templates,
    /// every file must use declared keys only, and every text file must carry its subject line.
    /// </summary>
    /// <param name="resources">The set to check.</param>
    /// <exception cref="EmailTemplateException">The first violation, naming the file it is about.</exception>
    public static void ValidateOrThrow(IReadOnlyCollection<EmailTemplateResource> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        ThrowOnDuplicateFiles(resources);
        ThrowOnMissingFiles(resources);
        ThrowOnOrphanFiles(resources);
        ThrowOnUndeclaredPlaceholders(resources);
        ThrowOnMissingSubject(resources);
    }

    /// <summary>One file of one template.</summary>
    /// <param name="template">The catalog template name.</param>
    /// <param name="locale">The locale.</param>
    /// <param name="format">The file format.</param>
    /// <returns>The resource, whose name a render error can quote.</returns>
    /// <exception cref="EmailTemplateException">The catalog declares the template but the file is absent.</exception>
    public EmailTemplateResource Get(string template, Language locale, EmailTemplateFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        return _files.TryGetValue((template, locale, format), out var resource)
            ? resource
            : throw new EmailTemplateException(
                $"E-mail template file '{EmailTemplateResource.FileNameOf(template, locale, format)}' is not copied beside this build's assembly.");
    }

    private static void ThrowOnDuplicateFiles(IReadOnlyCollection<EmailTemplateResource> resources)
    {
        var seen = new HashSet<(string Name, Language Locale, EmailTemplateFormat Format)>();

        foreach (var resource in resources)
        {
            if (!seen.Add((resource.Name, resource.Locale, resource.Format)))
            {
                throw new EmailTemplateException(
                    $"E-mail template file '{resource.FileName}' is present twice; one file per template, locale and format.");
            }
        }
    }

    private static void ThrowOnMissingFiles(IReadOnlyCollection<EmailTemplateResource> resources)
    {
        var present = resources
            .Select(resource => (resource.Name, resource.Locale, resource.Format))
            .ToHashSet();

        foreach (var (name, _) in EmailTemplateCatalog.All)
        {
            foreach (var locale in Enum.GetValues<Language>())
            {
                foreach (var format in Enum.GetValues<EmailTemplateFormat>())
                {
                    if (!present.Contains((name, locale, format)))
                    {
                        throw new EmailTemplateException(
                            $"E-mail template file '{EmailTemplateResource.FileNameOf(name, locale, format)}' is missing: "
                            + $"the catalog declares '{name}' and every template ships the vi/en txt/html quartet (09 README BD9).");
                    }
                }
            }
        }
    }

    private static void ThrowOnOrphanFiles(IReadOnlyCollection<EmailTemplateResource> resources)
    {
        foreach (var resource in resources)
        {
            if (EmailTemplateCatalog.Find(resource.Name) is null)
            {
                throw new EmailTemplateException(
                    $"E-mail template file '{resource.FileName}' has no entry in EmailTemplateCatalog: a file and its name ship together (09 README BD9).");
            }
        }
    }

    private static void ThrowOnUndeclaredPlaceholders(IReadOnlyCollection<EmailTemplateResource> resources)
    {
        foreach (var resource in resources)
        {
            var definition = EmailTemplateCatalog.Find(resource.Name)!;

            foreach (Match match in PlaceholderPattern.Matches(resource.Content))
            {
                var key = match.Groups["key"].Value;
                if (!definition.RenderKeys.Contains(key))
                {
                    throw new EmailTemplateException(
                        $"E-mail template file '{resource.FileName}' uses '{{{{{key}}}}}', which the catalog does not declare for "
                        + $"'{resource.Name}' (producer keys: {string.Join(", ", definition.ProducerKeys.Order(StringComparer.Ordinal))}).");
                }
            }
        }
    }

    private static void ThrowOnMissingSubject(IReadOnlyCollection<EmailTemplateResource> resources)
    {
        foreach (var resource in resources)
        {
            if (resource.Format != EmailTemplateFormat.Text)
            {
                continue;
            }

            if (!StartsWithSubject(resource.Content))
            {
                throw new EmailTemplateException(
                    $"E-mail template file '{resource.FileName}' must start with a '{SubjectPrefix} <text>' line: it carries the subject of both bodies [INFERENCE 09.2.6-T3].");
            }
        }
    }

    /// <summary>Whether a file starts with a non-empty <c>Subject:</c> line.</summary>
    /// <param name="content">The whole file content.</param>
    internal static bool StartsWithSubject(string content) =>
        SubjectOf(content) is { Length: > 0 };

    /// <summary>The subject line's text, or <see langword="null"/> when the file has none.</summary>
    /// <param name="content">The whole file content; the subject is its first line.</param>
    internal static string? SubjectOf(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var newline = content.IndexOf('\n');
        var firstLine = (newline < 0 ? content : content[..newline]).Trim();

        return firstLine.StartsWith(SubjectPrefix, StringComparison.Ordinal)
            ? firstLine[SubjectPrefix.Length..].Trim()
            : null;
    }

    private static EmailTemplateResource Parse(string fileName, string content)
    {
        // `<name>.<locale>.<format>`: a catalog key is a snake_case identifier and never contains a dot, so
        // the three segments are exactly the template, the locale and the file format.
        var parts = fileName.Split('.');

        if (parts.Length != 3
            || parts[0].Length == 0
            || !TryLocale(parts[1], out var locale)
            || !TryFormat(parts[2], out var format))
        {
            throw new EmailTemplateException(
                $"E-mail template file '{fileName}' must be named <name>.<locale>.{{txt,html}} with locale vi|en (09 README BD9).");
        }

        return new EmailTemplateResource(parts[0], locale, format, content);
    }

    private static bool TryLocale(string value, out Language locale)
    {
        foreach (var candidate in Enum.GetValues<Language>())
        {
            if (string.Equals(EmailTemplateCatalog.LocaleName(candidate), value, StringComparison.Ordinal))
            {
                locale = candidate;

                return true;
            }
        }

        locale = default;

        return false;
    }

    private static bool TryFormat(string value, out EmailTemplateFormat format)
    {
        foreach (var candidate in Enum.GetValues<EmailTemplateFormat>())
        {
            if (string.Equals(EmailTemplateResource.ExtensionOf(candidate), value, StringComparison.Ordinal))
            {
                format = candidate;

                return true;
            }
        }

        format = default;

        return false;
    }
}
