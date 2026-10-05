using System.Text.Json.Serialization;
using ErrorOr;
using Mediator;
using NightTales.Application.Abstractions.Audit;
using NightTales.Domain.Audit;
using NightTales.Domain.Stories;

namespace NightTales.Application.Features.Stories.Commands.ApproveStory;

/// <summary>Approves the exact reviewed story version under its strong ETag.</summary>
[Audited("episode.story_approved", Category.Episodes, Visibility.User)]
public sealed record ApproveStoryCommand(
    Guid EpisodeId,
    string IfMatch,
    AttestationInput? Attestation,
    ApprovalSource Source = ApprovalSource.User) : ICommand<ErrorOr<ApprovedStoryDto>>, IAuditData
{
    /// <summary>The accepted version; null for failures and idempotent approvals.</summary>
    [JsonIgnore]
    public Guid? ApprovedVersionId { get; set; }

    /// <inheritdoc />
    public AuditData? ToAuditData() => ApprovedVersionId is not { } versionId
        ? null
        : new AuditData(null, "episode", EpisodeId, new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["episode_id"] = EpisodeId,
            ["story_version_id"] = versionId,
            ["approval_source"] = Source == ApprovalSource.Autopilot ? "autopilot" : "user",
        });
}
