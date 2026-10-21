using ErrorOr;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NightTales.Application.Abstractions.Persistence;
using NightTales.Application.Abstractions.Storage;
using NightTales.Application.Common.Errors;
using NightTales.Application.Features.Catalog.Dtos;
using NightTales.Domain.Catalog;
using NightTales.Domain.Common;

namespace NightTales.Application.Features.Catalog.Queries.ListVoices;

/// <summary>
/// <c>GET /api/v1/voices</c> (09.4.2 plan §4 UC1, §5): the active voices of the catalog, filtered by
/// language and projected in the order the route's ETag is computed over.
/// </summary>
/// <remarks>
/// <para>
/// One query, no tracking and no <c>Include</c> (D8/Q1): the catalog is a closed, bounded set (04 D9),
/// so the whole answer is one projection and the ordering by <see cref="Voice.Id"/> makes the ETag
/// stable across requests. An inactive voice is a retired voice: it never appears here, while a series
/// that already names it stays valid (04 D9).
/// </para>
/// <para>
/// The preview URL is composed by <see cref="IPublicAssetUrl"/> and never stored: the row carries a key
/// of the public bucket, and a host without a public bucket answers <see langword="null"/> for every
/// row instead of failing (plan §3).
/// </para>
/// </remarks>
/// <param name="readDb">The no-tracking read model (D8).</param>
/// <param name="publicAssetUrl">Composes the public preview URL from the stored key.</param>
public sealed class ListVoicesHandler(IReadDb readDb, IPublicAssetUrl publicAssetUrl)
    : IQueryHandler<ListVoicesQuery, ErrorOr<IReadOnlyList<VoiceListItemDto>>>
{
    /// <inheritdoc />
    public async ValueTask<ErrorOr<IReadOnlyList<VoiceListItemDto>>> Handle(
        ListVoicesQuery query,
        CancellationToken cancellationToken)
    {
        Language? language = null;
        if (query.Language is not null)
        {
            // The validator of this folder already refused an unknown value on the route; repeating the
            // check keeps a handler that is called without the pipeline from filtering by garbage.
            if (!CatalogWireNames.TryParseLanguage(query.Language, out var parsed))
            {
                return Errors.ValidationFailed(
                    "language",
                    "The language filter is not a known language (vi, en).");
            }

            language = parsed;
        }

        var rows = await readDb.Query<Voice>()
            .Where(voice => voice.Active && (language == null || voice.Language == language))
            .OrderBy(voice => voice.Id)
            .Select(voice => new Row(
                voice.Id,
                voice.DisplayName,
                voice.Language,
                voice.Gender,
                voice.Tags,
                voice.PreviewStorageKey))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(row => new VoiceListItemDto(
                row.Id,
                row.DisplayName,
                CatalogWireNames.Spelling(row.Language),
                CatalogWireNames.Spelling(row.Gender),
                row.Tags,
                publicAssetUrl.Build(row.PreviewStorageKey)?.AbsoluteUri))
            .ToList();
    }

    /// <summary>The projected catalog row, before the URL and the wire spelling are applied.</summary>
    private sealed record Row(
        string Id,
        string DisplayName,
        Language Language,
        VoiceGender Gender,
        List<string> Tags,
        string? PreviewStorageKey);
}
