using System.Collections.Concurrent;
using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;

namespace NightTales.MockMpt.Images;

/// <summary>
/// The mock's <see cref="IImagePlanSink"/> (11 README D5, 05 D4): it remembers the plan the core registered and
/// answers an <c>images.v1</c> report in which every planned slot succeeded — no OpenAI, no relay, no network.
/// </summary>
/// <remarks>
/// The report generator and the <c>MOCK_IMAGE_REPORT</c> value semantics (moderated, skipped, funds, none) are
/// 03.4.3-T2's; this type is the default binding the mock starts with, and its shape is what
/// <c>tests/NightTales.MockMpt.Tests</c> pins.
/// </remarks>
public sealed class MockImageReport : IImagePlanSink
{
    private const string QualitySent = "medium";
    private const string Size = "1536x1024";
    private const string Model = "gpt-image-2";
    private const int TextTokensPerCall = 92;
    private const int OutputTokensPerCall = 1056;

    private readonly ConcurrentDictionary<Guid, ImagePlanV1> _plans = new();

    /// <inheritdoc />
    public void Register(Guid taskId, ImagePlanV1 plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _plans[taskId] = plan;
    }

    /// <inheritdoc />
    public void Release(Guid taskId) => _plans.TryRemove(taskId, out _);

    /// <inheritdoc />
    public ImageReportV1? BuildReport(Guid taskId)
    {
        if (!_plans.TryGetValue(taskId, out var plan))
        {
            return null;
        }

        var planned = plan.MaxImages;
        var usage = new List<ImageUsageV1>(planned);
        for (var slot = 0; slot < planned; slot++)
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
            Called: planned,
            Succeeded: planned,
            Failed: [],
            Unattempted: 0,
            QualitySent: QualitySent,
            QualityEchoed: [QualitySent],
            Size: Size,
            Model: Model,
            Usage: usage,
            RelayRejections: new ImageRelayRejectionsV1(0, 0, 0, 0));
    }
}
