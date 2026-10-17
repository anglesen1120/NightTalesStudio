using ErrorOr;
using Mediator;

namespace NightTales.Application.Features.Research.Sources.Commands.AddSource;

/// <summary>
/// <c>POST /api/v1/episodes/{episodeId}/sources</c> (02.5.1 T1 Design): the order of the checks is the contract,
/// because the acceptance checks assert the codes —
/// 404 → state gate 409 → URL syntax 400 → blocklist 400 → episode cap 409 → 24 h cap 429 → duplicate 409 → insert.
/// </summary>
/// <remarks>
/// The episode row lock is taken first, so the counts and the insert of two parallel adds cannot interleave. A
/// rejected URL is never persisted. Nothing here logs the URL, the note or a title (02 D19).
/// </remarks>
/// <param name="adder">
/// The shared add path; the candidate add command (02.7.2-T2) runs the same checks under its own episode lock, so
/// the two routes can never drift apart.
/// </param>
public sealed class AddSourceHandler(ISourceAdder adder) : ICommandHandler<AddSourceCommand, ErrorOr<SourceDto>>
{
    /// <inheritdoc />
    public ValueTask<ErrorOr<SourceDto>> Handle(AddSourceCommand command, CancellationToken cancellationToken) =>
        adder.AddAsync(command.EpisodeId, command.Url, command.NoteText, cancellationToken);
}
