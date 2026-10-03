namespace NightTales.MockMpt.Images;

/// <summary>
/// The mock-side seam through which failure injection chooses the <c>images</c> report of one task
/// (11.1.2-T2 Hooks): the server core registers the plan through <c>IImagePlanSink</c>, the backend selects the
/// report mode at submit and the plan sink builds the report at the terminal tick.
/// </summary>
public interface IImageReportSelector
{
    /// <summary>Selects the report mode for <paramref name="taskId"/>; the default is <see cref="ImageReportMode.Ok"/>.</summary>
    void UseReport(Guid taskId, ImageReportMode mode);
}
