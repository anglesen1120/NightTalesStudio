using System.Collections.Frozen;
using System.Reflection;
using System.Text.RegularExpressions;
using NightTales.Api.Http;
using NightTales.Application.Common.Errors;
using Shouldly;
using Xunit;

namespace NightTales.Api.IntegrationTests.Http;

/// <summary>
/// The closed-set invariants of the error catalogue (09.1.4-T1 Tests table): a code exists as exactly
/// one constant and in exactly one catalogue set, so no handler can answer a status the code must not
/// have.
/// </summary>
public sealed class ErrorCatalogueTests
{
    [Fact]
    public void ErrorCodes_EveryConstantHasCatalogueEntry_AndViceVersa()
    {
        var constants = ConstantValues();

        var status = ErrorCatalogue.Status.Keys.ToHashSet(StringComparer.Ordinal);
        var accepted = ErrorCatalogue.AcceptedBodyCodes;
        var nonHttp = ErrorCatalogue.NonHttpCodes;

        status.Overlaps(accepted).ShouldBeFalse(
            "a 202 body code must stay out of Status, or a handler that returns it as an error would answer a wrong status");
        status.Overlaps(nonHttp).ShouldBeFalse(
            "a job outcome or internal code must stay out of Status");
        accepted.Overlaps(nonHttp).ShouldBeFalse(
            "a 202 body code is not a job outcome");

        var catalogued = status
            .Concat(accepted)
            .Concat(nonHttp)
            .ToHashSet(StringComparer.Ordinal);

        var missing = constants.Except(catalogued).Order(StringComparer.Ordinal).ToArray();
        var extra = catalogued.Except(constants).Order(StringComparer.Ordinal).ToArray();

        missing.ShouldBeEmpty($"ErrorCodes constants without a catalogue entry ({missing.Length}): {string.Join(", ", missing)}");
        extra.ShouldBeEmpty($"catalogue entries without an ErrorCodes constant ({extra.Length}): {string.Join(", ", extra)}");

        catalogued.ShouldBe(constants, ignoreOrder: true);
    }

    [Fact]
    public void ErrorCodes_AreSnakeCaseAndUnique()
    {
        var fields = ConstantFields();

        var duplicates = fields
            .GroupBy(field => (string)field.GetRawConstantValue()!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} ({string.Join(", ", group.Select(field => field.Name))})")
            .ToArray();
        duplicates.ShouldBeEmpty($"two constants share one code: {string.Join("; ", duplicates)}");

        foreach (var field in fields)
        {
            var value = (string)field.GetRawConstantValue()!;

            Regex.IsMatch(value, "^[a-z][a-z0-9_]*$")
                .ShouldBeTrue($"{field.Name} = \"{value}\" is not snake_case (^[a-z][a-z0-9_]*$)");
            PascalCase(value)
                .ShouldBe(field.Name, $"the constant name must be the PascalCase of \"{value}\", so a lookup is never by memory");
        }
    }

    [Fact]
    public void ErrorCodes_EveryPublicStaticFieldIsAConstCode()
    {
        // The two tests above read only `const` fields, so a `static readonly string` would be a code
        // that no catalogue entry has to match — invisible to both. Only names on the (currently
        // empty) allow-list may be a public static non-constant.
        var offenders = typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => !NonConstantFieldAllowList.Contains(field.Name))
            .Where(field => !field.IsLiteral || field.FieldType != typeof(string))
            .Select(field => $"{field.Name} ({field.FieldType.Name}, {(field.IsLiteral ? "const" : "not const")})")
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "every public static field of ErrorCodes must be a `const string` code (or be named on the "
            + $"allow-list), otherwise the closed-set invariant silently ignores it ({offenders.Length}): "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void ErrorCodes_HasNoNestedTypes()
    {
        // A nested type could hold a second set of codes that no rule above ever sees.
        typeof(ErrorCodes)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Select(type => type.FullName)
            .ShouldBeEmpty();
    }

    /// <summary>Public static non-<c>const</c> fields of <see cref="ErrorCodes"/> that the closed-set rules may ignore.</summary>
    private static readonly FrozenSet<string> NonConstantFieldAllowList = FrozenSet<string>.Empty;

    private static HashSet<string> ConstantValues() => [.. ConstantFields().Select(field => (string)field.GetRawConstantValue()!)];

    private static FieldInfo[] ConstantFields() =>
        [.. typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false })
            .OrderBy(field => field.Name, StringComparer.Ordinal)];

    private static string PascalCase(string code) =>
        string.Concat(code
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..]));
}
