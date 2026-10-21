using ErrorOr;
using NightTales.Application.Abstractions.Publishing;
using NightTales.Domain.Common;
using NightTales.Domain.Publishing;

namespace NightTales.TestKit.Publishing;

/// <summary>
/// The hand-written <see cref="IPublicationNotices"/> of the unit level (06.2.2-T2, 09 README D13): it records every
/// call and answers whatever the test scripted, so the two handlers' contract with the notices — one call on the
/// path that writes, none on the idempotent repeat, the whole command failing when the notice does — is asserted
/// without an e-mail pipeline, a database row or a template catalog behind it.
/// </summary>
/// <remarks>
/// <para>
/// The success answer of the veto notice is a fresh UUIDv7 per call by default — the production sender's own
/// id — so a test that only cares about the flow needs to configure nothing, while
/// <see cref="VetoNoticeEmailMessageId"/> pins it when the recorded id is what the test asserts.
/// </para>
/// <para>
/// It is a stand-in for an <em>outbound</em> port, so "wrote nothing" is its whole success answer: the queued
/// message itself is the integration level's business (<c>PublicationNoticeTests</c>).
/// </para>
/// </remarks>
public sealed class FakePublicationNotices : IPublicationNotices
{
    private readonly List<VetoNoticeCall> _vetoes = [];
    private readonly List<ConsentedCall> _consents = [];

    /// <summary>Every veto notice the handlers queued, in call order.</summary>
    public IReadOnlyList<VetoNoticeCall> Vetoes => _vetoes;

    /// <summary>Every consent receipt the handlers queued, in call order.</summary>
    public IReadOnlyList<ConsentedCall> Consents => _consents;

    /// <summary>
    /// When set, every veto-notice call answers it instead of an id — the shape of a queue failure that must fail
    /// the whole create.
    /// </summary>
    public Error? VetoNoticeError { get; set; }

    /// <summary>
    /// When set, every consent-receipt call answers it instead of success — the shape of a queue failure that must
    /// fail the whole consent.
    /// </summary>
    public Error? ConsentedError { get; set; }

    /// <summary>The id the next veto notices answer; <see langword="null"/> answers a fresh UUIDv7 each call.</summary>
    public Guid? VetoNoticeEmailMessageId { get; set; }

    /// <inheritdoc />
    public Task<ErrorOr<Guid>> QueueVetoNoticeAsync(
        Publication publication,
        string channelDisplayName,
        string ownerDisplayName,
        Language locale,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);

        _vetoes.Add(new VetoNoticeCall(publication.Id, channelDisplayName, ownerDisplayName, locale));

        if (VetoNoticeError is { } error)
        {
            ErrorOr<Guid> failed = error;
            return Task.FromResult(failed);
        }

        return Task.FromResult<ErrorOr<Guid>>(VetoNoticeEmailMessageId ?? Guid.CreateVersion7());
    }

    /// <inheritdoc />
    public Task<ErrorOr<Success>> QueueConsentedAsync(
        Publication publication,
        ConsentSource source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);

        _consents.Add(new ConsentedCall(publication.Id, source));

        if (ConsentedError is { } error)
        {
            ErrorOr<Success> failed = error;
            return Task.FromResult(failed);
        }

        return Task.FromResult<ErrorOr<Success>>(Result.Success);
    }
}

/// <summary>One veto-notice call, as the fake saw it.</summary>
/// <param name="PublicationId">The publication announced.</param>
/// <param name="ChannelDisplayName">The channel title the caller passed.</param>
/// <param name="OwnerDisplayName">The owner's display name the caller passed.</param>
/// <param name="Locale">The render locale the caller passed.</param>
public sealed record VetoNoticeCall(
    Guid PublicationId,
    string ChannelDisplayName,
    string OwnerDisplayName,
    Language Locale);

/// <summary>One consent-receipt call, as the fake saw it.</summary>
/// <param name="PublicationId">The publication consented.</param>
/// <param name="Source">Who consented.</param>
public sealed record ConsentedCall(Guid PublicationId, ConsentSource Source);
