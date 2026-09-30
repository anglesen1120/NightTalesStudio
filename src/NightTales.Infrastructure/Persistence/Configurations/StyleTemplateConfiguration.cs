using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NightTales.Domain.Catalog;
using NightTales.Infrastructure.Persistence.Conventions;

namespace NightTales.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>style_templates</c> table (09.2.2 plan §3): the curated 03 catalog, unique by <c>name</c>, and
/// every template must keep the <c>{term}</c> placeholder it is rendered with.
/// </summary>
internal sealed class StyleTemplateConfiguration : IEntityTypeConfiguration<StyleTemplate>
{
    public void Configure(EntityTypeBuilder<StyleTemplate> builder)
    {
        builder.ToTable("style_templates", table => table.HasCheckConstraint(
            "ck_style_templates_template",
            "position('{term}' in template) > 0"));
        builder.HasKey(template => template.Id);

        builder.Property(template => template.Name).IsRequired();
        builder.Property(template => template.Template).IsRequired();
        builder.Property(template => template.TopicHint).HasEnumCheck();

        builder.HasIndex(template => template.Name).IsUnique();
    }
}
