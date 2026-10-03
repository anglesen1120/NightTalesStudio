using System.Collections.Concurrent;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;

namespace NightTales.MockMpt.Images;

/// <summary>
/// The mock's <see cref="IImagePlanSink"/> (11 README D5, 05 D4): it remembers the plan the core registered and
/// answers an <c>images.v1</c> report — by default one in which every planned slot succeeded, otherwise the
/// report <see cref="IImageReportSelector"/> selected for the task (11.1.2-T2). No OpenAI, no relay, no network.
/// </summary>
/// <remarks>
/// The report generator and the <c>MOCK_IMAGE_REPORT</c> value semantics (moderated, skipped, funds, none) are
/// 03.4.3-T2's; the mode→report selection and the env wiring are 11.1.2-T2's. The <c>ok</c> shape is what
/// <c>tests/NightTales.MockMpt.Tests</c> pins.
/// </remarks>
public sealed class MockImageReport : IImagePlanSink, IImageReportSelector
{
    private const string QualitySent = "medium";
    private const string Size = "1536x1024";
    private const string Model = "gpt-image-2";
    private const int TextTokensPerCall = 92;
    private const int OutputTokensPerCall = 1056;
    private const string ModerationBlocked = "moderation_blocked";
    private const string UpstreamError = "upstream_error";
    private const string InsufficientQuota = "insufficient_quota";

    private readonly ConcurrentDictionary<Guid, ImagePlanV1> _plans = new();
    private readonly ConcurrentDictionary<Guid, ImageReportMode> _modes = new();

    /// <inheritdoc />
    public void Register(Guid taskId, ImagePlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _plans[taskId] = plan;
    }

    /// <inheritdoc />
    public void Release(Guid taskId)
    {
        _plans.TryRemove(taskId, out _);
        _modes.TryRemove(taskId, out _);
    }

    /// <inheritdoc />
    public void UseReport(Guid taskId, ImageReportMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        _modes[taskId] = mode;
    }

    /// <inheritdoc />
    public ImageReportV1? BuildReport(Guid taskId)
    {
        if (!_plans.TryGetValue(taskId, out var plan))
        {
            return null;
        }

        var mode = _modes.TryGetValue(taskId, out var selected) ? selected : ImageReportMode.Ok;
        if (mode.Kind == ImageReportKind.None)
        {
            return null;
        }

        var planned = plan.MaxImages;
        var failed = new List<ImageFailureV1>();
        var called = planned;
        var succeeded = planned;
        var unattempted = 0;

        switch (mode.Kind)
        {
            case ImageReportKind.ModeratedSlots:
                // The blocked slots fail moderation and the relay's spare prompts replace them: every planned
                // slot still succeeds, one extra call per blocked slot.
                failed.AddRange(Moderated(mode.Slots, planned));
                called = planned + failed.Count;
                break;

            case ImageReportKind.Skipped:
                var skipped = (int)Math.Round(mode.Share * planned, MidpointRounding.AwayFromZero);
                for (var slot = planned - skipped; slot < planned; slot++)
                {
                    failed.Add(new ImageFailureV1(slot, 500, UpstreamError));
                }

                succeeded = planned - skipped;
                break;

            case ImageReportKind.ModeratedAll:
                failed.AddRange(Moderated(Enumerable.Range(0, planned).ToHashSet(), planned));
                succeeded = 0;
                break;

            case ImageReportKind.Funds:
                failed.Add(new ImageFailureV1(0, 429, InsufficientQuota));
                called = 1;
                succeeded = 0;
                unattempted = planned - 1;
                break;
        }

        var usage = new List<ImageUsageV1>(succeeded);
        for (var slot = 0; slot < succeeded; slot++)
        {
            usage.Add(new ImageUsageV1(
                Slot: slot,
                RequestId: $"req_mock_{slot}",
                TextTokens: TextTokensPerCall,
                ImageTokens: 0,
                OutputTokens: OutputTokensPerCall,
                Estimated: false));
        }

        return new ImageReportV1(
            RenderNodeSchemas.Images,
            Planned: planned,
            Needed: planned,
            Called: called,
            Succeeded: succeeded,
            Failed: failed,
            Unattempted: unattempted,
            QualitySent: QualitySent,
            QualityEchoed: [QualitySent],
            Size: Size,
            Model: Model,
            Usage: usage,
            RelayRejections: new ImageRelayRejectionsV1(0, 0, 0, 0));
    }

    private static IEnumerable<ImageFailureV1> Moderated(IReadOnlySet<int>? slots, int planned) =>
        (slots ?? new HashSet<int>())
            .Where(slot => slot >= 0 && slot < planned)
            .Order()
            .Select(slot => new ImageFailureV1(slot, 400, ModerationBlocked));
}
