using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging;
using NightTales.Application.Features.Autopilot.Ideas;
using Shouldly;
using Xunit;

namespace NightTales.Application.UnitTests.Logging;

/// <summary>
/// Guards the Application assembly's <c>LoggerMessage</c> event-id block against a copy-paste collision
/// (02.8.1-T2 review F5): the ids 6340-6344 were declared twice — by <c>ProposeBibleUpdateHandler</c> and by the
/// two idea checkers — so a log query or an alert by event id matched two unrelated message sets.
/// </summary>
/// <remarks>
/// The scan covers the two features that share the 63xx block, Autopilot and SeriesBible, because the collision
/// this test was written for was between them. The unrelated 6021/6022 collision in the Story feature pre-dates
/// this branch, is outside this lane's scope, and is deliberately not renumbered here.
/// </remarks>
public sealed class LoggerMessageEventIdTests
{
    private static readonly string[] ScannedNamespaces =
    [
        "NightTales.Application.Features.Autopilot",
        "NightTales.Application.Features.SeriesBible",
    ];

    /// <summary>No two <c>[LoggerMessage]</c> methods of the scanned features may carry the same event id.</summary>
    [Fact]
    public void FeatureLoggerMessageEventIds_AreUnique()
    {
        var collisions = typeof(IdeaPlanner).Assembly
            .GetTypes()
            .Where(type => type.Namespace is not null && IsScanned(type.Namespace))
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly))
            .Select(method => (Method: method, Event: method.GetCustomAttribute<LoggerMessageAttribute>()))
            .Where(entry => entry.Event is not null)
            .GroupBy(entry => entry.Event!.EventId)
            .Where(group => group.Count() > 1)
            .Select(group => "EventId " + group.Key.ToString(CultureInfo.InvariantCulture)
                + ": " + string.Join(", ", group.Select(entry => entry.Method.DeclaringType?.FullName + "." + entry.Method.Name)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        collisions.ShouldBeEmpty(
            "LoggerMessage EventIds must be unique; a collision makes a log query or an alert match two message sets:\n"
            + string.Join("\n", collisions));
    }

    private static bool IsScanned(string @namespace) =>
        ScannedNamespaces.Any(scanned =>
            @namespace == scanned || @namespace.StartsWith(scanned + ".", StringComparison.Ordinal));
}
