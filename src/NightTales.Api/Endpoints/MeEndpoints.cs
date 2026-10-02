using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NightTales.Api.Http;
using NightTales.Api.Http.Filters;
using NightTales.Application.Features.Auth.Mfa.Disable.Commands;
using NightTales.Application.Features.Auth.Mfa.RecoveryCodes.Commands;
using NightTales.Application.Features.Auth.Mfa.Setup.Commands;
using NightTales.Application.Features.Auth.Mfa.Verify.Commands;

namespace NightTales.Api.Endpoints;

/// <summary>
/// The <c>/me</c> route group's MFA routes (09.10.4-T1 Design, 09.3-auth plan §5): setup, confirmation,
/// recovery-code regeneration and disable.
/// </summary>
/// <remarks>
/// <para>
/// The endpoints are deliberately thin (09.1.2-T1): they bind the frozen body, send one message through
/// the D4 pipeline and turn the outcome into the documented response. What is theirs alone is the response
/// envelope, the two route guards and the fact that no response may be cached.
/// </para>
/// <para>
/// The two enrolment routes carry <see cref="RequireMfaEnrolmentScopeExtensions.RequireMfaEnrolmentScope"/>
/// — the <c>MfaEnrolment</c> policy that lets BD4's enrolment-only token in (WT34) — and
/// <c>RequireReauth(unlessEnrolmentToken: true)</c>, which is WT41: a recent re-authentication is required
/// even when MFA is off, unless the caller holds that token. The other two routes need a recent
/// re-authentication unconditionally.
/// </para>
/// <para>
/// Every response here carries <c>Cache-Control: no-store</c>: two of them contain a secret and the
/// others are security state, so no proxy may keep them (09 README BD16).
/// </para>
/// </remarks>
public static class MeEndpoints
{
    /// <summary>The group's prefix; the <c>/me</c> routes of 09.3.4-T1 join it later.</summary>
    public const string GroupPrefix = "/api/v1/me";

    /// <summary>The enrolment route (BD4).</summary>
    public const string MfaSetupRoute = GroupPrefix + "/mfa/setup";

    /// <summary>The confirmation route (BD4).</summary>
    public const string MfaVerifyRoute = GroupPrefix + "/mfa/verify";

    /// <summary>The recovery-code route (BD4).</summary>
    public const string MfaRecoveryCodesRoute = GroupPrefix + "/mfa/recovery-codes";

    /// <summary>The disable route (BD4).</summary>
    public const string MfaRoute = GroupPrefix + "/mfa";

    /// <summary>Maps the four MFA routes onto the host's route builder.</summary>
    /// <param name="routes">The host's endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var me = routes.MapGroup(GroupPrefix);

        // 09.9.4-T1 (BD12): the versioned API group carries the non-credentialed CORS policy.
        me.RequireCors(SecurityHeadersExtensions.SpaCorsPolicy);

        me.MapPost("/mfa/setup", SetupAsync)
            .RequireMfaEnrolmentScope()
            .RequireReauth(unlessEnrolmentToken: true)
            .Produces<MfaSetupResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        me.MapPost("/mfa/verify", VerifyAsync)
            .RequireMfaEnrolmentScope()
            .RequireReauth(unlessEnrolmentToken: true)
            .Produces<RecoveryCodesResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        me.MapPost("/mfa/recovery-codes", RegenerateRecoveryCodesAsync)
            .RequireReauth()
            .Produces<RecoveryCodesResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        me.MapDelete("/mfa", DisableAsync)
            .RequireReauth()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return routes;
    }

    /// <summary>
    /// <c>POST /api/v1/me/mfa/setup</c>: 200 with the pending secret and its <c>otpauth://</c> URI, or
    /// 401 <c>reauth_required</c>.
    /// </summary>
    private static async Task<IResult> SetupAsync(ISender sender, HttpContext http, CancellationToken ct)
    {
        var result = await sender.Send(new SetupMfaCommand(), ct).ConfigureAwait(false);

        if (result.IsError)
        {
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        NoStore(http);

        return Results.Json(
            new MfaSetupResponse(result.Value.Secret, result.Value.OtpauthUri),
            statusCode: StatusCodes.Status200OK);
    }

    /// <summary>
    /// <c>POST /api/v1/me/mfa/verify</c>: 200 with the ten recovery codes, 401 <c>invalid_mfa_code</c> for
    /// a code that does not match the pending secret, or 400 <c>validation_failed</c> for a malformed one.
    /// </summary>
    private static async Task<IResult> VerifyAsync(
        VerifyMfaCommand command,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await sender.Send(command, ct).ConfigureAwait(false);

        if (result.IsError)
        {
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        NoStore(http);

        return Results.Json(
            new RecoveryCodesResponse(result.Value),
            statusCode: StatusCodes.Status200OK);
    }

    /// <summary>
    /// <c>POST /api/v1/me/mfa/recovery-codes</c>: 200 with the ten new codes, or 401
    /// <c>reauth_required</c>.
    /// </summary>
    private static async Task<IResult> RegenerateRecoveryCodesAsync(
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await sender.Send(new RegenerateRecoveryCodesCommand(), ct).ConfigureAwait(false);

        if (result.IsError)
        {
            return ProblemDetailsWriter.ToProblem(result.Errors, http);
        }

        NoStore(http);

        return Results.Json(
            new RecoveryCodesResponse(result.Value),
            statusCode: StatusCodes.Status200OK);
    }

    /// <summary>
    /// <c>DELETE /api/v1/me/mfa</c>: 204, 403 <c>forbidden</c> for an owner or admin, or 401
    /// <c>reauth_required</c>.
    /// </summary>
    private static async Task<IResult> DisableAsync(ISender sender, HttpContext http, CancellationToken ct)
    {
        var result = await sender.Send(new DisableMfaCommand(), ct).ConfigureAwait(false);

        return result.IsError
            ? ProblemDetailsWriter.ToProblem(result.Errors, http)
            : Results.NoContent();
    }

    /// <summary>No response of this group may be cached: two of them carry a secret (09 README BD16).</summary>
    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";
}

/// <summary>
/// The frozen <c>POST /me/mfa/setup</c> body (09.3-auth plan §5, 10-web-ui 10.9.5): the pending secret,
/// returned exactly once, and the URI the SPA renders as a QR code.
/// </summary>
/// <param name="Secret">The base32 secret; never logged.</param>
/// <param name="OtpauthUri">The <c>otpauth://totp/…</c> URI, with <c>issuer=NightTales</c> (BD4).</param>
public sealed record MfaSetupResponse(string Secret, string OtpauthUri);

/// <summary>
/// The frozen <c>POST /me/mfa/verify</c> and <c>POST /me/mfa/recovery-codes</c> body (09.3-auth plan §5,
/// 10-web-ui 10.9.5): the ten codes, in plaintext, exactly once.
/// </summary>
/// <param name="RecoveryCodes">The ten single-use codes; never stored in plaintext (BD16).</param>
public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);
