using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace NightTales.Infrastructure.Persistence.Conventions;

/// <summary>
/// Declares that an enum property is stored as snake_case text and gets a <c>CHECK</c> constraint
/// listing the allowed values (09 README D8, C8). <c>builder.Property(e =&gt; e.Status).HasEnumCheck();</c>
/// </summary>
public static class EnumCheckExtensions
{
    public static PropertyBuilder<TEnum> HasEnumCheck<TEnum>(this PropertyBuilder<TEnum> property)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(property);

        property.HasConversion(new EnumToSnakeCaseStringConverter<TEnum>());
        property.HasColumnType("text");
        property.HasAnnotation(EnumCheckAnnotationNames.EnumCheck, typeof(TEnum));

        return property;
    }

    /// <summary>
    /// The nullable form of <see cref="HasEnumCheck{TEnum}(PropertyBuilder{TEnum})"/> for an optional
    /// enum column (for example <c>style_templates.topic_hint</c> or <c>autopilot_runs.outcome</c>).
    /// A null value satisfies the generated <c>CHECK</c>, because <c>NULL IN (…)</c> is never false.
    /// </summary>
    public static PropertyBuilder<TEnum?> HasEnumCheck<TEnum>(this PropertyBuilder<TEnum?> property)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(property);

        property.HasConversion((ValueConverter)new EnumToSnakeCaseStringConverter<TEnum>());
        property.HasColumnType("text");
        property.HasAnnotation(EnumCheckAnnotationNames.EnumCheck, typeof(TEnum));

        return property;
    }
}

/// <summary>Annotation names used by the Night Tales conventions.</summary>
internal static class EnumCheckAnnotationNames
{
    public const string EnumCheck = "NightTales:EnumCheck";
}
