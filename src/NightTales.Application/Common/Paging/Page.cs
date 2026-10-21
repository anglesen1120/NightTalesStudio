namespace NightTales.Application.Common.Paging;

/// <summary>
/// One cursor page of 09 README D14: the items of the page and the cursor that reads the next one, or
/// <see langword="null"/> <see cref="NextCursor"/> when this was the last page.
/// </summary>
/// <remarks>
/// The serialized shape is the frozen <c>{ items, nextCursor }</c> of D14 — the member names come from
/// the host's camelCase JSON policy, so the record carries them as written.
/// </remarks>
/// <typeparam name="T">The projection of one row, for example the series list item.</typeparam>
/// <param name="Items">The page's rows, at most the requested limit.</param>
/// <param name="NextCursor">The encoded cursor of the last row, or <see langword="null"/> at the end.</param>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);
