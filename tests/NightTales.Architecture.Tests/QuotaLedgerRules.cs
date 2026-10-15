using System.Reflection;
using Shouldly;
using Xunit;

namespace NightTales.Architecture.Tests;

/// <summary>
/// The upload quota ledger port (06.3.1-T1, plan §3) lives in Application and names no Google SDK type, so the
/// provider stays behind the Infrastructure adapter and Application keeps no Google reference.
/// </summary>
public sealed class QuotaLedgerRules
{
    private const string LedgerTypeName = "NightTales.Application.Abstractions.Publishing.IQuotaLedger";
    private const string ReservationTypeName = "NightTales.Application.Abstractions.Publishing.QuotaReservation";
    private const string GoogleNamespaceRoot = "Google.";

    [Fact]
    public void QuotaLedger_LivesInApplicationWithoutGoogleTypes()
    {
        var application = ArchitectureModel.Require(ArchitectureModel.Application);
        var ledger = application.GetType(LedgerTypeName, throwOnError: true)!;
        var reservation = application.GetType(ReservationTypeName, throwOnError: true)!;

        ledger.Assembly.ShouldBe(application);
        reservation.Assembly.ShouldBe(application);

        var offenders = new List<string>();
        var seen = new HashSet<Type>();

        foreach (var method in ledger.GetMethods())
        {
            Collect(method.ReturnType, $"{ledger.Name}.{method.Name} return", offenders, seen);
            foreach (var parameter in method.GetParameters())
            {
                Collect(parameter.ParameterType, $"{ledger.Name}.{method.Name} parameter {parameter.Name}", offenders, seen);
            }
        }

        foreach (var property in reservation.GetProperties())
        {
            Collect(property.PropertyType, $"{reservation.Name}.{property.Name}", offenders, seen);
        }

        offenders.ShouldBeEmpty();
        application.GetReferencedAssemblies()
            .Where(reference => reference.Name?.StartsWith(GoogleNamespaceRoot, StringComparison.Ordinal) == true)
            .Select(reference => reference.Name)
            .ShouldBeEmpty();
    }

    private static void Collect(Type type, string site, List<string> offenders, HashSet<Type> seen)
    {
        if (!seen.Add(type))
        {
            return;
        }

        if (IsGoogleType(type))
        {
            offenders.Add($"{site}: {type.FullName}");
        }

        if (type.HasElementType)
        {
            Collect(type.GetElementType()!, site, offenders, seen);
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                Collect(argument, site, offenders, seen);
            }
        }
    }

    private static bool IsGoogleType(Type type) =>
        (type.Namespace ?? string.Empty).StartsWith(GoogleNamespaceRoot, StringComparison.Ordinal)
        || type.Assembly.GetName().Name?.StartsWith(GoogleNamespaceRoot, StringComparison.Ordinal) == true;
}
