using NightTales.Application.Abstractions.Rendering;
using NightTales.Domain.Stories.Keywords;

namespace NightTales.Infrastructure.UnitTests.Stories;

/// <summary>
/// An <see cref="INameCorpusProvider"/> for the story-generation harnesses that do not exercise the real-person
/// name rule: every episode answers with <see cref="NameCorpus.Empty"/>, which is the same answer the real provider
/// gives for a fiction episode or one with no research text (03 D11).
/// </summary>
internal sealed class EmptyNameCorpusProvider : INameCorpusProvider
{
    /// <inheritdoc />
    public ValueTask<NameCorpus> LoadAsync(Guid episodeId, CancellationToken ct) => ValueTask.FromResult(NameCorpus.Empty);
}
