namespace NightTales.Application.Abstractions.Publishing;

/// <summary>
/// The channel of the account that granted access (06 README D9): the <c>channels.list?mine=true</c>
/// answer the callback stores as the connection's external identity.
/// </summary>
/// <param name="Id">The channel id (<c>UC…</c>), the connection's <c>external_account_id</c>.</param>
/// <param name="Title">The channel title, shown on <c>/channels</c>.</param>
public sealed record GoogleChannel(string Id, string Title);
