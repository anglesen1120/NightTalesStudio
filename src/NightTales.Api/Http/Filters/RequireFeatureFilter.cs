using ErrorOr;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NightTales.Api.Http;
using NightTales.Application.Abstractions.Flags;
using NightTales.Application.Common.Errors;

namespace NightTales.Api.Http.Filters;

/// <summary>
/// The route guard of 09 README BD13: a route whose feature is switched off answers exactly like a route
/// that does not exist — <b>404 <c>feature_disabled</c></b>, never 403 — so the surface never confirms
/// that a disabled feature is there.
/// </summary>
/// <remarks>
/// <para>
/// The filter resolves <see cref="IFeatureFlags"/> from the request's own services, so the read goes
/// through the host's 30 s cache and a disabled flag costs one row read per window per instance.
/// <see cref="IFeatureFlags.IsEnabledAsync"/> is fail-closed: a missing row, an invalid key and a database
/// that cannot answer all reach the same 404 instead of a 500.
/// </para>
/// <para>
/// The body is produced by <see cref="ProblemDetailsWriter.ToProblem"/> — the 09.1.4-T1 mapping every
/// handler error uses — so the shape is <c>{ type, title, status: 404, code, traceId }</c> with the
/// request's trace id, and no second error writer exists (D7).
/// </para>
/// <para>
/// When the flag is on the request continues unchanged: the filter never reads or replaces the body.
/// Guarded routes belong to the consumers (02.7, 02.8.3, 06); this task adds none.
/// </para>
/// </remarks>
public static class FeatureFilters
{
    /// <summary>
    /// Builds the endpoint filter that hides a route while <paramref name="key"/> is off.
    /// </summary>
    /// <param name="key">The flag key; the shape is <see cref="FeatureFlagKey.Pattern"/>.</param>
    /// <returns>The filter to hand to <c>AddEndpointFilter</c>, or to <see cref="RequireFeature"/>.</returns>
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> RequireFeature(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return async (context, next) =>
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(next);

            var flags = context.HttpContext.RequestServices.GetRequiredService<IFeatureFlags>();

            if (await flags
                .IsEnabledAsync(key, context.HttpContext.RequestAborted)
                .ConfigureAwait(false))
            {
                return await next(context).ConfigureAwait(false);
            }

            return ProblemDetailsWriter.ToProblem(
                [Error.NotFound(code: ErrorCodes.FeatureDisabled)],
                context.HttpContext);
        };
    }
}

/// <summary>Applies the feature guard to a route (09 README BD13).</summary>
public static class RequireFeatureExtensions
{
    /// <summary>
    /// Hides this route while <paramref name="key"/> is off: attach it to the routes of the feature the
    /// flag switches (the consumers of BD1, not this task).
    /// </summary>
    /// <param name="builder">The route to guard.</param>
    /// <param name="key">The flag key, for example <c>youtube_publish</c>.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static RouteHandlerBuilder RequireFeature(this RouteHandlerBuilder builder, string key)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEndpointFilter(FeatureFilters.RequireFeature(key));
    }
}
