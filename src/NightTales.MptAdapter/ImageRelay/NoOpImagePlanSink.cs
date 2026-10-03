using NightTales.RenderNode.Contract;
using NightTales.RenderNode.Server;

namespace NightTales.MptAdapter.ImageRelay;

/// <summary>
/// The <see cref="IImagePlanSink"/> when no relay owns plans: <c>ImageRelay:Enabled = false</c> (emergency
/// profile, MPT talks to OpenAI directly, 03.4.3-T1) or a build without the relay host. It keeps nothing and
/// reports no <c>images</c> block, so the terminal status simply omits it.
/// </summary>
internal sealed class NoOpImagePlanSink : IImagePlanSink
{
    public void Register(Guid taskId, ImagePlanV1 plan)
    {
    }

    public void Release(Guid taskId)
    {
    }

    public ImageReportV1? BuildReport(Guid taskId) => null;
}
