using Microsoft.Extensions.Logging;
using NightTales.MockMpt.Fixtures;
using NightTales.MockMpt.Images;
using NightTales.RenderNode.Contract;

namespace NightTales.MockMpt.Failures;

/// <summary>
/// What one submit resolved to (11.1.2-T2 Design): the failure mode, the <c>images</c> report and which layer
/// decided. Names only — it never carries script, terms or prompt text (S7).
/// </summary>
/// <param name="Mode">The failure behaviour; <see cref="FailMode.None"/> is the normal path.</param>
/// <param name="Image">The report shape of the <c>images</c> block.</param>
/// <param name="Source">The precedence layer: <c>keyword</c>, <c>env</c>, <c>rate</c> or <c>none</c>.</param>
public sealed record InjectionPlan(FailMode Mode, ImageReportMode Image, string Source);

/// <summary>
/// The precedence layer of failure injection (11.1.2-T2 Design), evaluated once per submit:
/// (1) a trigger keyword, (2) <c>MOCK_FAIL_MODE</c>, (3) <c>MOCK_FAIL_RATE</c>'s deterministic draw, (4) normal
/// success. <c>MOCK_IMAGE_REPORT</c> is orthogonal: it shapes the <c>images</c> block of every submit unless a
/// keyword (or the <c>image_moderated</c> alias) overrides it. The resolved plan is logged once as
/// <c>{task_id, mode, source}</c>.
/// </summary>
public sealed partial class FailureInjector
{
    /// <summary>The plan source when a keyword decided.</summary>
    public const string SourceKeyword = "keyword";

    /// <summary>The plan source when <c>MOCK_FAIL_MODE</c> decided.</summary>
    public const string SourceEnv = "env";

    /// <summary>The plan source when <c>MOCK_FAIL_RATE</c>'s draw decided.</summary>
    public const string SourceRate = "rate";

    /// <summary>The plan source when nothing injected a failure.</summary>
    public const string SourceNone = "none";

    private readonly MockMptOptions _options;
    private readonly FixtureSet _fixtures;
    private readonly ILogger<FailureInjector> _logger;

    public FailureInjector(MockMptOptions options, FixtureSet fixtures, ILogger<FailureInjector> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fixtures);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _fixtures = fixtures;
        _logger = logger;
    }

    /// <summary>Resolves the plan of one submit and logs it once.</summary>
    public InjectionPlan Resolve(Guid taskId, Guid renderJobId, MptSubmitFields mpt)
    {
        ArgumentNullException.ThrowIfNull(mpt);

        var hits = FailKeywords.Scan(mpt, _fixtures);
        FailMode mode;
        string source;
        if (hits.Any)
        {
            mode = hits.Mode ?? FailMode.None;
            source = SourceKeyword;
        }
        else if (_options.FailMode.IsInjected)
        {
            mode = _options.FailMode;
            source = SourceEnv;
        }
        else if (FailRate.Select(_options.FailRate, renderJobId) is { } drawn)
        {
            mode = drawn;
            source = SourceRate;
        }
        else
        {
            mode = FailMode.None;
            source = SourceNone;
        }

        var plan = new InjectionPlan(mode, ResolveImage(taskId, hits, mode), source);
        if (source == SourceNone)
        {
            LogPlanNone(taskId, plan.Mode.Name, plan.Source);
        }
        else
        {
            LogPlan(taskId, plan.Mode.Name, plan.Source, plan.Image.Name);
        }

        return plan;
    }

    private ImageReportMode ResolveImage(Guid taskId, KeywordHits hits, FailMode mode)
    {
        if (hits.FailImage)
        {
            // `__fail_image__` is `moderated_all` for this submit, and wins over `__block_image__`.
            return ImageReportMode.ModeratedAll;
        }

        var configured = _options.ImageReport;
        if (hits.BlockedSlots.Count > 0)
        {
            var slots = new HashSet<int>(hits.BlockedSlots);
            if (configured.Kind == ImageReportKind.ModeratedSlots && configured.Slots is { } fromEnv)
            {
                slots.UnionWith(fromEnv);
            }

            return new ImageReportMode(ImageReportKind.ModeratedSlots, slots);
        }

        if (mode.Kind == FailKind.ImageModerated)
        {
            // The CR-4 alias: exactly `MOCK_IMAGE_REPORT=moderated_all` for this submit. An explicit,
            // different report value is overridden and the log says so.
            if (configured.Kind != ImageReportKind.Ok && configured.Kind != ImageReportKind.ModeratedAll)
            {
                LogImageOverride(taskId, configured.Name);
            }

            return ImageReportMode.ModeratedAll;
        }

        return configured;
    }

    [LoggerMessage(
        EventId = 1111,
        Level = LogLevel.Information,
        Message = "mock-mpt.inject: task={TaskId} mode={Mode} source={Source} image={Image}")]
    private partial void LogPlan(Guid taskId, string mode, string source, string image);

    [LoggerMessage(
        EventId = 1112,
        Level = LogLevel.Debug,
        Message = "mock-mpt.inject: task={TaskId} mode={Mode} source={Source}")]
    private partial void LogPlanNone(Guid taskId, string mode, string source);

    [LoggerMessage(
        EventId = 1113,
        Level = LogLevel.Information,
        Message = "mock-mpt.inject.image-override: task={TaskId} mode=image_moderated overrides image={Image}")]
    private partial void LogImageOverride(Guid taskId, string image);
}
