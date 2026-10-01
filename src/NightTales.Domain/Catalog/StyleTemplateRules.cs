using ErrorOr;

namespace NightTales.Domain.Catalog;

/// <summary>
/// The template contract of 03 README D1, enforced before any <c>style_templates</c> row is written:
/// a well-formed slug name, exactly one <see cref="ScenePromptBuilder.Placeholder"/>, 60–600 characters
/// after trimming, no line break, no double quote and no negative clause (the builder appends that).
/// The table CHECK constraint from 09.2.2-T1 stays the last line of defence.
/// </summary>
/// <remarks>
/// The seed report (and the <c>nts-seed</c> exit code) needs only the code, so each failure is
/// <see cref="ErrorType.Validation"/> with the rule's code as its own code and a short description; no
/// metadata is attached, because the seven seed rows are not HTTP fields.
/// </remarks>
public static class StyleTemplateRules
{
    /// <summary>The name is not a 3–40 character lower-case slug.</summary>
    public const string NameInvalid = "style_name_invalid";

    /// <summary>The template does not hold <see cref="ScenePromptBuilder.Placeholder"/> exactly once.</summary>
    public const string TermCount = "style_term_count";

    /// <summary>The trimmed template is shorter than <see cref="TemplateMinLength"/> or longer than <see cref="TemplateMaxLength"/>.</summary>
    public const string Length = "style_length";

    /// <summary>The template contains a carriage return or a line feed.</summary>
    public const string LineBreak = "style_line_break";

    /// <summary>The template contains a double quote.</summary>
    public const string Quote = "style_quote";

    /// <summary>The template already carries the negative clause, or a word of it.</summary>
    public const string HasNegativeClause = "style_has_negative_clause";

    /// <summary>The shortest allowed name.</summary>
    public const int NameMinLength = 3;

    /// <summary>The longest allowed name.</summary>
    public const int NameMaxLength = 40;

    /// <summary>The shortest allowed template, after trimming.</summary>
    public const int TemplateMinLength = 60;

    /// <summary>The longest allowed template, after trimming.</summary>
    public const int TemplateMaxLength = 600;

    /// <summary>Validates one seed row; the first failing rule wins, in the D1 order.</summary>
    /// <param name="name">The template's natural key.</param>
    /// <param name="template">The style text; exactly one <c>{term}</c>, no clause.</param>
    public static ErrorOr<Success> Validate(string name, string template)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(template);

        if (!IsSlug(name))
        {
            return Error.Validation(NameInvalid, "The style template name must be a 3-40 character lower-case slug.");
        }

        if (CountTerms(template) != 1)
        {
            return Error.Validation(TermCount, $"The style template must contain exactly one {ScenePromptBuilder.Placeholder} placeholder.");
        }

        var length = template.Trim().Length;
        if (length is < TemplateMinLength or > TemplateMaxLength)
        {
            return Error.Validation(Length, "The style template must be 60-600 characters after trimming.");
        }

        if (template.Contains('\r') || template.Contains('\n'))
        {
            return Error.Validation(LineBreak, "The style template must not contain a line break.");
        }

        if (template.Contains('"'))
        {
            return Error.Validation(Quote, "The style template must not contain a double quote.");
        }

        if (HasClause(template))
        {
            return Error.Validation(HasNegativeClause, "The style template must not contain the negative clause; the builder appends it.");
        }

        return Result.Success;
    }

    /// <summary>Whether <paramref name="name"/> matches <c>^[a-z0-9]+(-[a-z0-9]+)*$</c> within the D1 length.</summary>
    private static bool IsSlug(string name)
    {
        if (name.Length is < NameMinLength or > NameMaxLength)
        {
            return false;
        }

        var segment = 0;
        foreach (var character in name)
        {
            if (character == '-')
            {
                if (segment == 0)
                {
                    return false;
                }

                segment = 0;
                continue;
            }

            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character))
            {
                return false;
            }

            segment++;
        }

        return segment > 0;
    }

    /// <summary>Counts the <c>{term}</c> occurrences, ordinal (the placeholder is ASCII).</summary>
    private static int CountTerms(string template)
    {
        var count = 0;
        var index = template.IndexOf(ScenePromptBuilder.Placeholder, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = template.IndexOf(ScenePromptBuilder.Placeholder, index + ScenePromptBuilder.Placeholder.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>Whether the template already carries the clause text or one of its tell-tale words.</summary>
    private static bool HasClause(string template) =>
        template.Contains(NegativeClause.Current, StringComparison.Ordinal)
        || template.Contains("no gore", StringComparison.OrdinalIgnoreCase)
        || template.Contains("no text", StringComparison.OrdinalIgnoreCase)
        || template.Contains("watermark", StringComparison.OrdinalIgnoreCase);
}
