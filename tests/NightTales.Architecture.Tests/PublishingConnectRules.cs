using System.Reflection;
using System.Text.RegularExpressions;
using NightTales.Application.Abstractions.Audit;
using Shouldly;
using Xunit;

namespace NightTales.Architecture.Tests;

/// <summary>
/// 06.1.2-T1's audit shape: both commands of the connect flow are exempt from the pipeline's
/// <c>AuditBehavior</c> because <c>CompleteYouTubeConnectHandler</c> writes the one
/// <c>publishing.channel_connected</c> row itself, on the success path only.
/// </summary>
/// <remarks>
/// <para>
/// The attribute rules themselves are 09.3.6-T1's (<see cref="AuditAttributeTests"/>); this file pins the
/// two 06 commands by name and the three properties that keep the exemption reviewable: each carries
/// <see cref="NotAuditedAttribute"/> and never <see cref="AuditedAttribute"/>; the callback answers the
/// callback answers <c>ErrorOr&lt;ConnectYouTubeOutcome&gt;</c> whose value arm is the closed outcome set
/// (the endpoint's envelope split maps the error arm to a constant); and the handler reaches
/// <c>IAuditWriter</c> from exactly one call site.
/// </para>
/// <para>
/// The runtime half — <b>one</b> row on the success path and <b>none</b> on any of the failure outcomes —
/// cannot be seen in metadata, so it is asserted over the real host in
/// <c>NightTales.Api.IntegrationTests.Publishing.ConnectChannelTests</c>
/// (<c>Callback_ValidState_RedirectsAndConnectsChannel</c> plus the three post-claim failure cases).
/// </para>
/// </remarks>
public sealed class PublishingConnectRules
{
    private const string ConnectCommandTypeName =
        "NightTales.Application.Features.Publishing.Channels.Commands.ConnectYouTube.ConnectYouTubeCommand";

    private const string CompleteCommandTypeName =
        "NightTales.Application.Features.Publishing.Channels.Commands.CompleteYouTubeConnect.CompleteYouTubeConnectCommand";

    private const string OutcomeTypeName =
        "NightTales.Application.Features.Publishing.Channels.Commands.CompleteYouTubeConnect.ConnectYouTubeOutcome";

    private const string HandlerTypeName =
        "NightTales.Application.Features.Publishing.Channels.Commands.CompleteYouTubeConnect.CompleteYouTubeConnectHandler";

    private const string AuditWriterTypeName = "NightTales.Application.Abstractions.Audit.IAuditWriter";

    /// <summary>The one action the connect flow writes (06 README D3, the acceptance's audit check).</summary>
    private const string ConnectedAction = "publishing.channel_connected";

    /// <summary>The five outcomes the Api maps to the two fixed redirects; the contract fixes this order.</summary>
    private static readonly string[] ExpectedOutcomes =
        ["Connected", "StateInvalid", "Denied", "ExchangeFailed", "ChannelAlreadyConnected"];

    private static readonly Assembly Application = ArchitectureModel.Require(ArchitectureModel.Application);

    [Fact]
    public void ConnectCommands_AreBothNotAudited()
    {
        foreach (var typeName in new[] { ConnectCommandTypeName, CompleteCommandTypeName })
        {
            var command = Application.GetType(typeName, throwOnError: true)!;

            command.GetCustomAttribute<AuditedAttribute>(inherit: false)
                .ShouldBeNull(
                    $"{command.Name} is exempt because the handler writes the row itself; a pipeline action would "
                    + "mean a second row with a different argument in the same transaction");

            var exemption = command.GetCustomAttribute<NotAuditedAttribute>(inherit: false)
                .ShouldNotBeNull($"{command.Name} must carry [NotAudited] with a reason a reviewer can check");

            exemption.Reason.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void CompleteCommand_AnswersTheClosedOutcomeSetAndNeverAnError()
    {
        var command = Application.GetType(CompleteCommandTypeName, throwOnError: true)!;
        var answer = command.GetInterfaces()
            .Where(type => type.IsGenericType && type.GetGenericTypeDefinition().Name == "ICommand`1")
            .Select(type => type.GetGenericArguments()[0])
            .Single();

        // The envelope is ErrorOr<ConnectYouTubeOutcome> on purpose: the error arm is what the Api's envelope
        // split maps to a constant redirect, and the value arm is the closed outcome set. Either way the type
        // the endpoint switches on is the enum - never a raw wire code the Location could interpolate.
        var outcome = answer.IsGenericType && answer.GetGenericTypeDefinition().Name == "ErrorOr`1"
            ? answer.GetGenericArguments()[0]
            : answer;

        answer.Name.ShouldStartWith(
            "ErrorOr",
            Case.Sensitive,
            "the callback answers an envelope, so a failure of the claim's own unit has a code to map");
        outcome.FullName.ShouldBe(OutcomeTypeName);

        Enum.GetName(outcome, 0).ShouldBe(
            ExpectedOutcomes[0],
            "the connected outcome is the enum's zero member, so a default-constructed outcome never reads as a failure");
        Enum.GetNames(outcome).ShouldBe(
            ExpectedOutcomes,
            ignoreOrder: true,
            customMessage: "the outcome set is closed: a new failure would need its own wire value and its own row assertion");
    }

    [Fact]
    public void CompleteHandler_WritesTheAuditRowFromExactlyOneCallSite()
    {
        var writes = CallSiteRules.FindCallsInType(HandlerTypeName)
            .Where(site => string.Equals(site.DeclaringType, AuditWriterTypeName, StringComparison.Ordinal))
            .ToList();

        writes.Count.ShouldBe(
            1,
            $"{HandlerTypeName} writes the one {ConnectedAction} row from one place — the success path. Found: "
            + $"{string.Join("; ", writes.Select(site => $"{site.Method} -> {site.CalledMember}"))}");
        writes[0].CalledMember.ShouldBe("WriteAsync");
    }

    [Fact]
    public void ConnectedAction_IsDeclaredOnceAndMatchesTheFrozenPattern()
    {
        Regex.IsMatch(ConnectedAction, AuditedAttribute.ActionPattern)
            .ShouldBeTrue($"'{ConnectedAction}' must match the frozen action pattern of 09.3.6-T1");

        var declarations = Application.GetTypes()
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(field => field.IsLiteral
                && field.FieldType == typeof(string)
                && string.Equals((string?)field.GetRawConstantValue(), ConnectedAction, StringComparison.Ordinal))
            .ToList();

        declarations.Count.ShouldBe(
            1,
            $"'{ConnectedAction}' is a cross-task contract (the activity allow-list and the ops queries key on it): "
            + "exactly one const declares it, so no caller and no test re-spells the literal");
    }
}
