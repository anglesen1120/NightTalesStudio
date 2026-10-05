namespace NightTales.Application.Features.Stories.Approval;

/// <summary>
/// The attestation text version accepted by the approval gate, when required.
/// </summary>
/// <param name="AcceptedTextVersion">The accepted text version, or null when no attestation was required.</param>
public sealed record StoryApprovalGateResult(string? AcceptedTextVersion);
