using FluentValidation;
using NightTales.Domain.Research;

namespace NightTales.Application.Features.Research.Sources.Commands.AddSource;

/// <summary>
/// Shape validation of <c>POST /episodes/{id}/sources</c> (02.5.1 T1 Design): exactly one of <c>url</c> and
/// <c>noteText</c>, and the length bounds that turn an oversized value into a 400 instead of a 500. Every other URL
/// rule lives in <see cref="SourceUrl.Parse"/>, so the Api and the fetcher cannot diverge.
/// </summary>
public sealed class AddSourceValidator : AbstractValidator<AddSourceCommand>
{
    public AddSourceValidator(SourcesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        RuleFor(command => command.Url)
            .Must((command, _) => HasText(command.Url) != HasText(command.NoteText))
            .WithMessage("source_exactly_one_required");

        RuleFor(command => command.Url)
            .Must(url => url is null || url.Trim().Length <= SourceUrl.MaxLength)
            .WithMessage("url_too_long");

        RuleFor(command => command.NoteText)
            .Must(note => note is null || note.Length <= options.MaxNoteChars)
            .WithMessage("note_too_long");
    }

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);
}
