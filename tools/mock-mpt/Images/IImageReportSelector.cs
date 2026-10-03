namespace NightTales.MockMpt.Images;

/// <summary>
/// The mock-side seam through which failure injection chooses the <c>images</c> report of one task
/// (11.1.2-T2 Hooks): the server core registers the plan through <c>IImagePlanSink</c>, the backend selects the
/// report mode at submit and the plan sink builds the report at the terminal tick.
/// </summary>
public interface IImageReportSelector
{
    /// <summary>
    /// Selects the report mode for <paramref name="taskId"/> and hands over the submit's clip bound,
    /// <c>ceil(MOCK_VIDEO_SECONDS ÷ video_clip_duration)</c>: the second term of 03.4.3-T2's
    /// <c>needed_guess = min(planned, bound)</c>. The default mode is <see cref="ImageReportMode.Ok"/>.
    /// </summary>
    void UseReport(Guid taskId, ImageReportMode mode, int clipBound);
}
