using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;

namespace NightTales.Infrastructure.Telemetry;

/// <summary>The salt of <see cref="UserHashEnricher"/>, resolved once at startup.</summary>
/// <param name="Salt">The HMAC key (a secret outside Development).</param>
public sealed record UserHashEnricherOptions(string Salt);

/// <summary>
/// Adds <c>userId</c> to every log event written inside an authenticated request (09.9.3-T1): the first 16
/// characters of base64url(HMAC-SHA256(salt, sub)). It is stable across instances so a user's lines can be
/// correlated, and is neither reversible nor the raw id or e-mail.
/// </summary>
/// <param name="options">The resolved salt.</param>
/// <param name="accessor">The ambient request, when there is one.</param>
public sealed class UserHashEnricher(UserHashEnricherOptions options, IHttpContextAccessor accessor) : ILogEventEnricher
{
    /// <summary>The fixed salt used in Development only.</summary>
    public const string DevelopmentSalt = "nts-development-log-salt";

    /// <summary>The log property name.</summary>
    public const string PropertyName = "userId";

    private const int HashLength = 16;

    private readonly byte[] _key = Encoding.UTF8.GetBytes(options.Salt);

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        var user = accessor.HttpContext?.User;
        var subject = user?.FindFirstValue("sub") ?? user?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(subject))
        {
            return;
        }

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(PropertyName, Hash(subject)));
    }

    /// <summary>Hashes one subject with this enricher's salt.</summary>
    /// <param name="subject">The raw user id.</param>
    /// <returns>The 16-character base64url digest prefix.</returns>
    public string Hash(string subject)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);

        var digest = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(subject));

        return Convert.ToBase64String(digest).Replace('+', '-').Replace('/', '_').TrimEnd('=')[..HashLength];
    }
}
