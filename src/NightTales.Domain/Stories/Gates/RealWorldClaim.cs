namespace NightTales.Domain.Stories.Gates;

/// <summary>
/// One entry of <c>audit_report.model_review.real_world_claims[]</c> reduced to its D22 segment address
/// (02 README D16/D22, 02.8.2-T2). The claim's own <c>evidence</c> is deliberately <em>not</em> carried: the
/// detector quotes our own draft only, and the review model's wording is neither stored in a hit nor logged
/// (D19/D22, F31).
/// </summary>
/// <param name="Segment">
/// The D22 address the reviewer attached the claim to: <c>title</c>, <c>hook</c>, <c>p:&lt;index&gt;</c>,
/// <c>ending</c> or <c>meta</c>. The claim is a hit whatever the address: an address that does not resolve to a
/// segment of the evaluated version yields the hit with empty evidence instead of being dropped, because a
/// reviewer that asserted a real-world basis still asserted one.
/// </param>
/// <remarks>
/// 02.6.1-T1 already has a <see cref="Review.RealWorldClaim"/> — the stored document's claim, which also carries
/// the reviewer's own <c>evidence</c> — and it stays the shape of <c>story_versions.audit_report</c>. This record
/// is the detector's input and deliberately keeps the address only, so the review's wording can never travel into
/// a hit (D19/D22). A file that maps one onto the other imports both namespaces, so it must alias or fully
/// qualify the name.
/// </remarks>
public sealed record RealWorldClaim(string Segment);
