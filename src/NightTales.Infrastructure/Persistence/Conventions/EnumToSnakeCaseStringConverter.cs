using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace NightTales.Infrastructure.Persistence.Conventions;

/// <summary>
/// Converts an enum member to its snake_case text form and back (09 README D8, C8). The same
/// conversion feeds the <c>CHECK</c> constraints emitted by <see cref="EnumCheckConvention"/>, so
/// the stored values and the allowed values can never drift apart.
/// </summary>
public sealed class EnumToSnakeCaseStringConverter<TEnum> : ValueConverter<TEnum, string>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<string, TEnum> BySnakeCase = Enum
        .GetValues<TEnum>()
        .ToDictionary(
            value => EnumSnakeCase.Convert(value.ToString()),
            value => value,
            StringComparer.Ordinal);

    public EnumToSnakeCaseStringConverter()
        : base(value => ToSnakeCase(value), value => FromSnakeCase(value))
    {
    }

    private static string ToSnakeCase(TEnum value) => EnumSnakeCase.Convert(value.ToString());

    private static TEnum FromSnakeCase(string value) => BySnakeCase[value];
}

/// <summary>Shared snake_case conversion for enum member names.</summary>
internal static class EnumSnakeCase
{
    public static string Convert(string enumMemberName) => JsonNamingPolicy.SnakeCaseLower.ConvertName(enumMemberName);

    public static IReadOnlyList<string> ConvertAll(Type enumType) =>
        [.. Enum.GetNames(enumType).Select(Convert).Order(StringComparer.Ordinal)];
}
