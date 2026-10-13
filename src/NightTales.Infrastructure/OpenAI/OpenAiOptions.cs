using NightTales.Application.Abstractions.Text;

namespace NightTales.Infrastructure.OpenAI;

/// <summary>
/// The <c>OpenAi</c> section of the text adapter (02 README D7). The price rates live in
/// <c>OpenAi:Pricing</c> (<c>PricingOptions</c>, 02.2.4-T1) and are not repeated here.
/// </summary>
/// <remarks>
/// Models, effort and timeouts have in-code defaults (D7), so a host without the block still works; the
/// section only overrides. <see cref="ApiKey"/> is secret material: it comes from the secret store or the
/// environment (<c>OpenAi__ApiKey</c>), is never committed and is never logged.
/// </remarks>
public sealed class OpenAiOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "OpenAi";

    /// <summary>The pinned relay endpoint (01 DEC-9/CR-15): the only <see cref="BaseUrl"/> accepted outside Development.</summary>
    public const string PinnedBaseUrl = "https://api.aixingialaire.shop/v1";

    /// <summary>The base URL of the OpenAI-compatible endpoint.</summary>
    public string BaseUrl { get; set; } = PinnedBaseUrl;

    /// <summary>The API key; required in every environment.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The model per step; <c>gpt-6-luna</c> for all until 02.1.3 and 02.6.1-T2 decide.</summary>
    public Dictionary<TextStep, string> Models { get; set; } = DefaultModels();

    /// <summary>The <c>reasoning.effort</c> per step: <c>medium</c> for the writer, <c>low</c> for the rest (D7).</summary>
    public Dictionary<TextStep, string> Effort { get; set; } = DefaultEffort();

    /// <summary>The per-attempt timeout per step: writer 90 s, review 45 s, facts 60 s, others 30 s (D7).</summary>
    public Dictionary<TextStep, TimeSpan> RequestTimeout { get; set; } = DefaultTimeouts();

    /// <summary>The base of the jittered exponential backoff between the two attempts; a <c>Retry-After</c> longer than it wins.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The efforts the validator accepts.</summary>
    public static IReadOnlySet<string> AllowedEfforts { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "none", "minimal", "low", "medium", "high" };

    private static Dictionary<TextStep, string> DefaultModels() =>
        Enum.GetValues<TextStep>().ToDictionary(step => step, _ => "gpt-6-luna");

    private static Dictionary<TextStep, string> DefaultEffort() =>
        Enum.GetValues<TextStep>().ToDictionary(step => step, step => step == TextStep.Writer ? "medium" : "low");

    private static Dictionary<TextStep, TimeSpan> DefaultTimeouts() =>
        Enum.GetValues<TextStep>().ToDictionary(
            step => step,
            step => TimeSpan.FromSeconds(step switch
            {
                TextStep.Writer or TextStep.Revision => 90,
                TextStep.Review => 45,
                TextStep.Facts => 60,
                _ => 30,
            }));
}
