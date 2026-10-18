namespace NightTales.Application.Features.VideoReview;

/// <summary>
/// The retention window of an approved episode's assets (09.7.1-T1 step 3, WT56):
/// <c>Assets:RetentionDays</c>, the value <c>ApproveVideoCommandHandler</c> re-stamps over the ingest's
/// <c>Assets:UnapprovedRetentionDays</c> stamp.
/// </summary>
/// <remarks>
/// <para>
/// The section is <c>Assets</c>, shared with <c>RenderIngestOptions</c>, which reads its sibling key
/// <c>UnapprovedRetentionDays</c> from the same section: each options class binds the keys it owns, so the
/// two windows stay independently tunable — 09.6.3-T2 keeps an abandoned render for its own window, and this
/// one is the promise made about a cut the owner has accepted.
/// </para>
/// <para>
/// The Application layer holds the values and the checks (<see cref="AssetRetentionOptionsValidator"/>) but
/// not the binding: it may not reference <c>Microsoft.Extensions.Options</c>, so every host binds the section,
/// runs the validator at start-up and exposes the bound value as itself for the handlers that take it
/// directly.
/// </para>
/// <para>
/// Every value has an in-code default, so a host that carries no <c>Assets:RetentionDays</c> key still runs
/// (the key is what an operator tunes, not what makes an approve work), and an impossible one fails the boot
/// instead of shortening a promise at the first approve.
/// </para>
/// </remarks>
public sealed class AssetRetentionOptions
{
    /// <summary>The configuration section both asset retention windows are bound from (WT56).</summary>
    public const string SectionName = "Assets";

    /// <summary>The default retention window of an approved asset, in days (09.7.1-T1 §Notes: 30).</summary>
    public const int DefaultRetentionDays = 30;

    /// <summary>How many days an approved episode's assets are kept (default 30).</summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>The retention stamp an asset of an approved episode carries.</summary>
    public TimeSpan Retention => TimeSpan.FromDays(RetentionDays);
}
