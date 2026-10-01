using NightTales.Domain.Common;
using NightTales.Domain.Series;

namespace NightTales.Domain.Catalog;

/// <summary>
/// A curated style template of the 03 catalog (00 overview §6 <c>style_templates</c>). Platform data,
/// not user-owned; the template text must contain the <c>{term}</c> placeholder (a CHECK constraint).
/// </summary>
public sealed class StyleTemplate : Entity, ICreatedAt
{
    private StyleTemplate()
    {
    }

    /// <summary>The natural key the seeder upserts by.</summary>
    public string Name { get; private set; } = string.Empty;

    public string Template { get; private set; } = string.Empty;

    public string? PreviewStorageKey { get; private set; }

    /// <summary>Null means the template is usable for every topic.</summary>
    public Topic? TopicHint { get; private set; }

    public bool Active { get; private set; }

    /// <summary>Stamped by <c>AuditableSaveChangesInterceptor</c> on insert (plan §3 preamble).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Creates a catalog row; the 09.2.5-T1 seeder upserts by <paramref name="name"/>.
    /// </summary>
    /// <remarks>
    /// The 03.1.1-T2 contract (03 README D1) is enforced here, so no caller can build a row the seeder
    /// or the builder would reject. The seeder is the error boundary: it runs
    /// <see cref="StyleTemplateRules.Validate"/> per row first and raises
    /// <c>SeedValidationException("seed_style_template_invalid:&lt;name&gt;:&lt;code&gt;")</c>, so this
    /// factory's throw is a guard for direct callers (the test builders) and keeps its
    /// 09.2.2-T1 signature.
    /// </remarks>
    /// <exception cref="ArgumentException">A blank argument, or a name/template that breaks <see cref="StyleTemplateRules"/>.</exception>
    public static StyleTemplate Create(
        string name,
        string template,
        Topic? topicHint = null,
        string? previewStorageKey = null,
        bool active = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        var validation = StyleTemplateRules.Validate(name, template);
        if (validation.IsError)
        {
            throw new ArgumentException(
                $"The style template '{name}' is invalid: {validation.FirstError.Code}.",
                validation.FirstError.Code == StyleTemplateRules.NameInvalid ? nameof(name) : nameof(template));
        }

        return new StyleTemplate
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Template = template,
            TopicHint = topicHint,
            PreviewStorageKey = previewStorageKey,
            Active = active,
        };
    }

    /// <summary>
    /// Rewrites the catalog-owned columns of an existing row (09.2.5 plan §3.2); the 09.2.5-T1 seeder
    /// upserts by <see cref="Name"/>. The <c>{term}</c> placeholder is validated by the CHECK constraint
    /// of the table and by the seeder before any write.
    /// </summary>
    public void UpdateFromCatalog(string template, Topic? topicHint, string? previewStorageKey, bool active)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        Template = template;
        TopicHint = topicHint;
        PreviewStorageKey = previewStorageKey;
        Active = active;
    }
}
