using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NightTales.Application.Common.Errors;

namespace NightTales.Api.Http.Filters;

/// <summary>
/// A JSON body that records the members it did not recognise (through <c>[JsonExtensionData]</c>), so a
/// route can refuse them instead of silently dropping them.
/// </summary>
public interface IRejectsUnknownMembers
{
    /// <summary>The names of the JSON members the body carried but the wire model does not declare.</summary>
    IReadOnlyCollection<string> UnknownMembers { get; }
}

/// <summary>
/// The per-endpoint strict binding of <c>PATCH /me</c> ([RULED — 09 README §B10 WT18, WT44]): an unknown JSON
/// member (<c>email</c>, <c>role</c>, a typo) is 400 <c>validation_failed</c> instead of a silent no-op.
/// </summary>
/// <remarks>
/// It is attached to the routes that ask for it and changes no serializer option, so every other endpoint
/// keeps ignoring unknown members.
/// </remarks>
public sealed class RejectUnknownMembersFilter : IEndpointFilter
{
    /// <summary>The most member names one response echoes.</summary>
    private const int MaxReportedMembers = 5;

    private const string Description = "The request body contains a member that is not accepted on this route.";

    /// <inheritdoc />
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        foreach (var argument in context.Arguments)
        {
            if (argument is IRejectsUnknownMembers { UnknownMembers.Count: > 0 } body)
            {
                var errors = body.UnknownMembers
                    .Take(MaxReportedMembers)
                    .Select(member => Errors.ValidationFailed(member, Description))
                    .ToList();

                return ValueTask.FromResult<object?>(ProblemDetailsWriter.ToProblem(errors, context.HttpContext));
            }
        }

        return next(context);
    }
}

/// <summary>Applies <see cref="RejectUnknownMembersFilter"/> to a route.</summary>
public static class RejectUnknownMembersExtensions
{
    /// <summary>Rejects unknown JSON body members on this route only.</summary>
    /// <param name="builder">The route to protect.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static RouteHandlerBuilder RejectUnknownMembers(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEndpointFilter<RejectUnknownMembersFilter>();
    }
}
