namespace NightTales.Domain.Rendering.Images;

/// <summary>
/// The knobs of <see cref="RenderImagePlanner"/> (03 README D3): <c>Images:*</c> plus the
/// <c>Story:Length:Rate:{lang}</c> speaking rates, in tokens per minute.
/// </summary>
/// <param name="ClipSeconds">Seconds one image stays on screen (<c>video_clip_duration</c>, <c>60 ÷ K</c>).</param>
/// <param name="SpareShare">Free spare terms on top of the needed images, as a share of them.</param>
/// <param name="SparseShare">Below this share of the needed images the plan raises <c>scenes_sparse</c>.</param>
/// <param name="RateVi">Vietnamese syllables per minute.</param>
/// <param name="RateEn">English words per minute.</param>
public sealed record ImagePlanOptions(int ClipSeconds, double SpareShare, double SparseShare, int RateVi, int RateEn)
{
    /// <summary>The documented defaults: 10 s clips, 15 % spares, 70 % sparse line, 260 syllables and 200 words per minute.</summary>
    public static ImagePlanOptions Defaults { get; } = new(10, 0.15, 0.7, 260, 200);
}
