using FluentValidation;

namespace NightTales.Application.Features.Publishing.Channels.Commands.ConnectYouTube;

/// <summary>
/// The shape check of <c>POST /channels/{platform}/connect</c> (06 README D3).
/// </summary>
/// <remarks>
/// It is deliberately <b>not</b> the membership check. An unknown but plausible platform — <c>tiktok</c>
/// today, before 07 ships it — is a resource the API does not serve, and the frozen answer is the
/// handler's 404 <c>not_found</c>, not a 400 that would leak the platform catalogue to a probe. The rule
/// here only refuses a path segment that could not be a platform at all, so a hostile or mistyped
/// segment is rejected before the handler reads the caller, the clock or the database.
/// </remarks>
public sealed class ConnectYouTubeValidator : AbstractValidator<ConnectYouTubeCommand>
{
    /// <summary>The longest segment <c>PlatformExtensions</c> can ever declare a wire value for.</summary>
    public const int MaxPlatformLength = 32;

    /// <summary>Builds the shape rule.</summary>
    public ConnectYouTubeValidator()
    {
        RuleFor(command => command.Platform)
            .NotEmpty()
            .MaximumLength(MaxPlatformLength)
            .WithMessage($"A platform is 1-{MaxPlatformLength} characters; the served value is 'youtube'.");
    }
}
