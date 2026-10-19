using FluentValidation;

namespace NightTales.Application.Features.Publishing.Commands.CreatePublication;

/// <summary>
/// The shape check of the create body (06.2.1-T1 task §Application step 5, 06 README D6).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> the YouTube rules of G4/F08/F09: those are <c>YouTubeMetadataRules.Validate</c>'s, run
/// by the handler after the music attribution is appended and against the <em>submitted</em> privacy, so the
/// limits live in one place and answer the one 400 <c>validation_failed</c> shape the client already reads
/// (<c>field</c> ∈ title, description, tags, privacy, publishAt).
/// </para>
/// <para>
/// Nor the ids: an unknown — or another account's — <c>channelConnectionId</c> is a resource this API does not
/// serve, and the frozen answer is the handler's 404 <c>not_found</c> from the tenant-filtered lookup, never a
/// 400 that would turn the lookup into an existence probe (09 README D10). An empty <c>Guid</c> is simply an id
/// no row carries, so it takes the same 404.
/// </para>
/// <para>
/// What is left is load-bearing: a JSON body may set <c>null</c> on any of these members, and the domain rules
/// cannot see a null (<c>EnumerateRunes</c>, <c>GetByteCount</c> and the tag walk all throw on one). Refusing
/// them here is what keeps a malformed body a 400 instead of a 500.
/// </para>
/// </remarks>
public sealed class CreatePublicationValidator : AbstractValidator<CreatePublicationCommand>
{
    /// <summary>Builds the shape rules.</summary>
    public CreatePublicationValidator()
    {
        RuleFor(command => command.Title)
            .NotNull()
            .WithMessage("A title is required.");

        RuleFor(command => command.Description)
            .NotNull()
            .WithMessage("A description is required.");

        RuleFor(command => command.Tags)
            .NotNull()
            .WithMessage("A tag list is required (it may be empty).");

        RuleFor(command => command.Privacy)
            .NotNull()
            .WithMessage("A privacy is required.");
    }
}
