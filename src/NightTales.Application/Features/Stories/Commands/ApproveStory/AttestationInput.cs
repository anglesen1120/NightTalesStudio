namespace NightTales.Application.Features.Stories.Commands.ApproveStory;

/// <summary>
/// The user's acceptance of a versioned story attestation.
/// </summary>
/// <param name="Accepted">Whether the user accepted the attestation.</param>
/// <param name="TextVersion">The attestation text version presented to the user.</param>
public sealed record AttestationInput(bool Accepted, string TextVersion);
