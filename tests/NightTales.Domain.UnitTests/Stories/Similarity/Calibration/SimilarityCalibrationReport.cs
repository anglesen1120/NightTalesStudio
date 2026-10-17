using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NightTales.Domain.Common;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Similarity;
using StoryText = NightTales.Domain.Stories.Similarity.StoryText;

#pragma warning disable CA1716 // `Class` is the fixture's wire name (sample-set.v1 `class`); this harness is test-only and never consumed from Visual Basic.

namespace NightTales.Domain.UnitTests.Stories.Similarity.Calibration;

/// <summary>One class, language and shingle size of the calibration sweep, with its score distribution.</summary>
/// <param name="Class">The sample class (for example <c>original</c>).</param>
/// <param name="Language">The story or fact language (<c>vi</c> or <c>en</c>).</param>
/// <param name="N">The shingle size.</param>
/// <param name="Mean">The mean score of the class at this n, rounded to six decimals.</param>
/// <param name="P50">The lower median score, rounded to six decimals.</param>
/// <param name="Min">The minimum score.</param>
/// <param name="Max">The maximum score.</param>
public sealed record CalibrationCell(string Class, string Language, int N, double Mean, double P50, double Min, double Max);

/// <summary>The threshold set the decision rule chose, and the previous set when one was recorded.</summary>
/// <param name="NVi">The Vietnamese shingle size.</param>
/// <param name="NEn">The English shingle size.</param>
/// <param name="WarnAbove">The warn threshold.</param>
/// <param name="BlockAbove">The block threshold.</param>
/// <param name="CopiedFactThreshold">The copied-fact threshold.</param>
/// <param name="PerfBudgetMs">The scoring latency budget in milliseconds.</param>
public sealed record CalibrationChoice(int NVi, int NEn, double WarnAbove, double BlockAbove, double CopiedFactThreshold, int PerfBudgetMs);

/// <summary>The measured p95 of the T1 performance test, recorded as an input of the calibration.</summary>
/// <param name="MeasuredP95Ms">The measured p95 in milliseconds.</param>
/// <param name="Source">Where the measurement came from.</param>
public sealed record CalibrationPerf(double MeasuredP95Ms, string Source);

/// <summary>One <c>verbatim_paragraph</c> sample's score at the chosen shingle size.</summary>
/// <param name="Id">The sample id.</param>
/// <param name="Language">The story language.</param>
/// <param name="Score">The score at the chosen n.</param>
public sealed record DilutionDiagnostic(string Id, string Language, double Score);

/// <summary>The committed calibration evidence: every cell, the chosen thresholds and the residual statement.</summary>
/// <param name="GeneratedAt">The UTC date of the run (<c>yyyy-MM-dd</c>).</param>
/// <param name="SampleCount">The number of samples in the fixture.</param>
/// <param name="Grid">The shingle sizes swept.</param>
/// <param name="Cells">One cell per class, language and shingle size, in stable order.</param>
/// <param name="Chosen">The thresholds the decision rule chose.</param>
/// <param name="Previous">The thresholds of the previous report, when one exists.</param>
/// <param name="Perf">The measured p95 that produced the performance budget.</param>
/// <param name="TranslatedCopyResidual">The cross-language residual statement.</param>
/// <param name="Dilution">The <c>verbatim_paragraph</c> diagnostics at the chosen n.</param>
/// <param name="Warnings">Fixture or decision-rule warnings; empty when the sample set separates cleanly.</param>
/// <param name="RerunTriggers">The changes that make a re-run mandatory.</param>
public sealed record CalibrationReport(
    string GeneratedAt,
    int SampleCount,
    IReadOnlyList<int> Grid,
    IReadOnlyList<CalibrationCell> Cells,
    CalibrationChoice Chosen,
    CalibrationChoice? Previous,
    CalibrationPerf Perf,
    string TranslatedCopyResidual,
    IReadOnlyList<DilutionDiagnostic> Dilution,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> RerunTriggers);

/// <summary>The fixture's <c>source</c> object: one synthetic source text.</summary>
public sealed record SimilaritySource(string Id, string Text);

/// <summary>The fixture's <c>story</c> object.</summary>
public sealed record CalibrationStory(string Title, string Hook, IReadOnlyList<string> Paragraphs, string Ending);

/// <summary>One labelled fixture sample.</summary>
public sealed record SimilaritySample(
    string Id,
    string Language,
    string Class,
    SimilaritySource Source,
    CalibrationStory? Story,
    string? Fact);

/// <summary>The whole committed fixture.</summary>
public sealed record SimilaritySampleSet(string Schema, IReadOnlyList<SimilaritySample> Samples);

/// <summary>One sample's score for one shingle size.</summary>
internal sealed record SampleScore(string SampleId, string Language, string Class, int N, double Score);

/// <summary>
/// The offline calibration harness of 02.5.6-T2: it loads the committed sample set, sweeps the shingle
/// grid through the production scorer and applies the deterministic decision rule of the task file.
/// </summary>
internal static class SimilarityCalibrator
{
    /// <summary>The fixture schema this harness and the committed fixture agree on.</summary>
    internal const string Schema = "similarity-samples.v1";

    /// <summary>The shingle sizes swept for every class and language.</summary>
    internal static readonly IReadOnlyList<int> Grid = [6, 8, 10, 12, 14];

    /// <summary>The sample classes with a <c>story</c> member, in contract order.</summary>
    internal static readonly IReadOnlyList<string> StoryClassNames =
    [
        "original", "shared_collocation", "light_paraphrase", "heavy_paraphrase", "quoted_passage",
        "verbatim_paragraph", "verbatim_full", "translated_copy",
    ];

    /// <summary>The sample classes with a <c>fact</c> member, in contract order.</summary>
    internal static readonly IReadOnlyList<string> FactClassNames = ["fact_copied", "fact_paraphrased"];

    /// <summary>All sample classes, in contract order.</summary>
    internal static readonly IReadOnlyList<string> ClassNames = [.. StoryClassNames, .. FactClassNames];

    /// <summary>The classes the warn threshold is calibrated on: everything that must not be flagged.</summary>
    internal static readonly IReadOnlyList<string> SpecificityClassNames =
        ["original", "shared_collocation", "light_paraphrase", "quoted_passage"];

    /// <summary>The classes whose maximum forms A in rule 3 (owner amendment, 2026-10-17): the four non-copy classes plus heavy_paraphrase.</summary>
    internal static readonly IReadOnlyList<string> BlockMaxClassNames =
        ["original", "shared_collocation", "light_paraphrase", "quoted_passage", "heavy_paraphrase"];

    /// <summary>The two content languages, in the order cells are reported.</summary>
    internal static readonly IReadOnlyList<string> LanguageNames = ["vi", "en"];

    private static readonly string[] RerunTriggerTexts =
    [
        "Any change to TextNormalizer or Shingler: normalisation or windowing moves every score.",
        "A change to the D4 token rule: a token is one whitespace-separated run after NFC normalisation.",
        "A change to Similarity:MinTokens: the sample set is only scorable above it.",
        "A new content language: the grid has no calibration for it.",
        "Any edit to prompts/fixtures/similarity/sample-set.v1.json: the evidence no longer matches the fixture.",
    ];

    /// <summary>The choices before 02.5.6-T2: the shingle defaults and the live config defaults.</summary>
    internal static readonly CalibrationChoice Baseline = new(10, 8, 0.05, 0.15, 0.15, 500);

    /// <summary>
    /// The <c>previous</c> block of a new report: the committed <c>previous</c> while the new choice equals the committed
    /// choice, the committed <c>chosen</c> when the choice moved, and <see cref="Baseline"/> when nothing is committed.
    /// </summary>
    internal static CalibrationChoice ResolvePrevious(CalibrationReport? committed, CalibrationChoice newChosen)
    {
        if (committed is null)
        {
            return Baseline;
        }

        return committed.Chosen == newChosen ? (committed.Previous ?? Baseline) : committed.Chosen;
    }

    private static readonly JsonSerializerOptions ReportJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions FixtureJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The repository root, found by walking up from the test output to <c>NightTales.sln</c>.</summary>
    /// <returns>The absolute repository root.</returns>
    internal static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NightTales.sln")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException("Could not locate the repository root (NightTales.sln) above the test output.")
            : directory.FullName;
    }

    /// <summary>The committed fixture path.</summary>
    internal static string FixturePath =>
        Path.Combine(RepositoryRoot(), "prompts", "fixtures", "similarity", "sample-set.v1.json");

    /// <summary>The committed report path.</summary>
    internal static string ReportPath => Path.Combine(
        RepositoryRoot(), "tests", "NightTales.Domain.UnitTests", "Stories", "Similarity", "Calibration",
        "similarity-calibration-report.json");

    /// <summary>Loads and validates the committed fixture.</summary>
    /// <returns>The sample set.</returns>
    internal static SimilaritySampleSet LoadFixture()
    {
        var path = FixturePath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The similarity calibration fixture is missing.", path);
        }

        return ParseSampleSet(File.ReadAllText(path));
    }

    /// <summary>Parses and validates a fixture document.</summary>
    /// <param name="json">The fixture JSON.</param>
    /// <returns>The sample set.</returns>
    internal static SimilaritySampleSet ParseSampleSet(string json)
    {
        var set = JsonSerializer.Deserialize<SimilaritySampleSet>(json, FixtureJson)
            ?? throw new InvalidOperationException("The similarity calibration fixture is not a JSON object.");
        Validate(set);
        return set;
    }

    /// <summary>Reads a committed calibration report.</summary>
    /// <param name="json">The report JSON.</param>
    /// <returns>The report.</returns>
    internal static CalibrationReport ParseReport(string json) =>
        JsonSerializer.Deserialize<CalibrationReport>(json, ReportJson)
            ?? throw new InvalidOperationException("The similarity calibration report is not a JSON object.");

    /// <summary>Serialises a report with the committed layout (camelCase, indented, trailing newline).</summary>
    /// <param name="report">The report.</param>
    /// <returns>The JSON text.</returns>
    internal static string SerializeReport(CalibrationReport report) =>
        JsonSerializer.Serialize(report, ReportJson) + "\n";

    /// <summary>Projects a story sample onto the production <see cref="StoryContentV1"/> the scorer reads.</summary>
    /// <param name="sample">A story sample.</param>
    /// <returns>The story content.</returns>
    internal static StoryContentV1 ToContent(SimilaritySample sample)
    {
        var story = sample.Story
            ?? throw new InvalidOperationException($"Sample '{sample.Id}' has no story to project.");
        return new StoryContentV1(
            story.Title,
            new StorySegmentV1(story.Hook, [], []),
            story.Paragraphs.Select(static paragraph => new StorySegmentV1(paragraph, [], [])).ToArray(),
            new StorySegmentV1(story.Ending, [], []),
            Description: string.Empty,
            Tags: [],
            ContentWarnings: [],
            Structure: new StoryStructureV1("unknown", "unspecified", "unknown", "unknown", "third_person"));
    }

    /// <summary>The scored story text of a story sample.</summary>
    /// <param name="sample">A story sample.</param>
    /// <returns>The story text.</returns>
    internal static StoryText StoryTextOf(SimilaritySample sample) => StoryText.From(ToContent(sample));

    /// <summary>The single corpus text of a sample, with an id derived deterministically from the sample.</summary>
    /// <param name="sample">Any sample.</param>
    /// <returns>The corpus text.</returns>
    internal static CorpusText CorpusOf(SimilaritySample sample) =>
        new(SourceGuid(sample), sample.Source.Text);

    /// <summary>The production options for one language at one shingle size.</summary>
    /// <param name="language">The language code.</param>
    /// <param name="n">The shingle size.</param>
    /// <returns>The options, with the production defaults for warn, block and minimum tokens.</returns>
    internal static SimilarityOptions Options(string language, int n)
    {
        var settings = new SimilaritySettings();
        settings.N[language] = n;
        return SimilarityOptions.For(language == "vi" ? Language.Vi : Language.En, settings);
    }

    /// <summary>Scores every sample for every shingle size in the grid.</summary>
    /// <param name="sampleSet">The fixture.</param>
    /// <returns>One score per sample and n, in fixture order within each language and n.</returns>
    internal static IReadOnlyList<SampleScore> ScoreSamples(SimilaritySampleSet sampleSet)
    {
        var scores = new List<SampleScore>(sampleSet.Samples.Count * Grid.Count);
        foreach (var language in LanguageNames)
        {
            var samples = sampleSet.Samples.Where(sample => sample.Language == language).ToArray();
            foreach (var n in Grid)
            {
                var options = Options(language, n);
                foreach (var sample in samples)
                {
                    var corpus = new[] { CorpusOf(sample) };
                    double score;
                    if (FactClassNames.Contains(sample.Class, StringComparer.Ordinal))
                    {
                        var fact = sample.Fact
                            ?? throw new InvalidOperationException($"Fact sample '{sample.Id}' has no fact statement.");
                        score = SimilarityScorer.ScoreFact(fact, corpus, options);
                    }
                    else
                    {
                        var story = StoryTextOf(sample);
                        if (!SimilarityScorer.IsScorable(story, corpus, options))
                        {
                            throw new InvalidOperationException($"Story sample '{sample.Id}' is not scorable at n={n}.");
                        }

                        score = SimilarityScorer.Score(story, corpus, options).Score;
                    }

                    scores.Add(new SampleScore(sample.Id, language, sample.Class, n, Round6(score)));
                }
            }
        }

        return scores;
    }

    /// <summary>The score-distribution cells of a scored sweep, in stable order.</summary>
    /// <param name="scores">The scored samples.</param>
    /// <returns>One cell per class, language and n.</returns>
    internal static IReadOnlyList<CalibrationCell> BuildCells(IReadOnlyList<SampleScore> scores)
    {
        var cells = new List<CalibrationCell>(ClassNames.Count * LanguageNames.Count * Grid.Count);
        foreach (var language in LanguageNames)
        {
            foreach (var className in ClassNames)
            {
                foreach (var n in Grid)
                {
                    var values = scores
                        .Where(score => score.Language == language && score.Class == className && score.N == n)
                        .Select(static score => score.Score)
                        .OrderBy(static value => value)
                        .ToArray();
                    if (values.Length == 0)
                    {
                        throw new InvalidOperationException($"No '{className}' samples for '{language}' at n={n}.");
                    }

                    cells.Add(new CalibrationCell(
                        className, language, n, Round6(values.Average()), values[(values.Length - 1) / 2], values[0], values[^1]));
                }
            }
        }

        return cells;
    }

    /// <summary>The full sweep over the committed fixture.</summary>
    /// <param name="sampleSet">The fixture.</param>
    /// <returns>The cells.</returns>
    internal static IReadOnlyList<CalibrationCell> Sweep(SimilaritySampleSet sampleSet) => BuildCells(ScoreSamples(sampleSet));

    /// <summary>
    /// Applies the decision rule of 02.5.6-T2 to a fixture and a measured performance p95. The result is
    /// deterministic: identical arguments give an identical report, and only <paramref name="generatedAt"/> differs
    /// between runs.
    /// </summary>
    /// <param name="sampleSet">The fixture.</param>
    /// <param name="perf">The measured p95 of the T1 performance test.</param>
    /// <param name="previous">The previous report's choices, or null on the first run.</param>
    /// <param name="generatedAt">The UTC date of the run (<c>yyyy-MM-dd</c>).</param>
    /// <returns>The report to commit.</returns>
    internal static CalibrationReport Build(
        SimilaritySampleSet sampleSet, CalibrationPerf perf, CalibrationChoice? previous, string generatedAt)
    {
        var scores = ScoreSamples(sampleSet);
        var cells = BuildCells(scores);
        var warnings = new List<string>();

        var nVi = ChooseN(cells, "vi", warnings);
        var nEn = ChooseN(cells, "en", warnings);

        var warnRaw = SpecificityClassNames
            .SelectMany(className => LanguageNames.Select(language => Max(cells, className, language, ChosenN(language, nVi, nEn))))
            .Max();
        var specificityMax = BlockMaxClassNames
            .SelectMany(className => LanguageNames.Select(language => Max(cells, className, language, ChosenN(language, nVi, nEn))))
            .Max();
        var verbatimMin = LanguageNames.Min(language => Min(cells, "verbatim_full", language, ChosenN(language, nVi, nEn)));

        // D1: a language without a qualifying n keeps both defaults; they are never derived from data.
        decimal warn;
        decimal block;
        if (LanguageNames.Any(language => !HasCandidate(cells, language)))
        {
            warnings.Add("No n qualifies for at least one language; WarnAbove and BlockAbove kept at the defaults 0.05 / 0.15.");
            warn = 0.05m;
            block = 0.15m;
        }
        else
        {
            warn = Math.Max(0.02m, RoundUp(warnRaw, 0.01m));
            block = MidpointBlockAbove(specificityMax, verbatimMin);
            if (block < warn + 0.05m)
            {
                warnings.Add(
                    $"BlockAbove would be {block.ToString("0.##", CultureInfo.InvariantCulture)}, below WarnAbove + 0.05; kept 0.15. The sample set does not separate the classes.");
                block = 0.15m;
            }
        }

        var maxParaphrased = LanguageNames.Max(language => Max(cells, "fact_paraphrased", language, ChosenN(language, nVi, nEn)));
        var minCopied = LanguageNames.Min(language => Min(cells, "fact_copied", language, ChosenN(language, nVi, nEn)));
        decimal fact;
        if (maxParaphrased >= minCopied)
        {
            warnings.Add(
                "The fact classes overlap (max fact_paraphrased >= min fact_copied); kept the live Similarity:FactOverlapBlock default of 0.15.");
            fact = 0.15m;
        }
        else
        {
            fact = RoundUp((maxParaphrased + minCopied) / 2d, 0.05m);
        }

        // CODE-4: production flags only overlaps above the threshold, so a fact at exactly minCopied would be missed.
        if ((double)fact >= minCopied)
        {
            warnings.Add(
                $"CopiedFactThreshold {fact.ToString("0.##", CultureInfo.InvariantCulture)} is not below min fact_copied {minCopied.ToString("F6", CultureInfo.InvariantCulture)}; a copied fact at the threshold would not be flagged.");
        }

        var chosen = new CalibrationChoice(
            nVi, nEn, (double)warn, (double)block, (double)fact, (int)Math.Ceiling(2 * perf.MeasuredP95Ms));

        var dilution = scores
            .Where(score => score.Class == "verbatim_paragraph" && score.N == ChosenN(score.Language, nVi, nEn))
            .Select(static score => new DilutionDiagnostic(score.SampleId, score.Language, score.Score))
            .ToArray();
        foreach (var diagnostic in dilution)
        {
            if (diagnostic.Score < chosen.BlockAbove)
            {
                warnings.Add(
                    $"Expected dilution: verbatim_paragraph sample '{diagnostic.Id}' scores "
                    + diagnostic.Score.ToString("F6", CultureInfo.InvariantCulture)
                    + " below BlockAbove; the score is a share of the whole story, not of the paragraph.");
            }
        }

        var residual = FormattableString.Invariant(
            $"en source -> vi story mean {Mean(scores, "translated_copy", "vi", nVi):F6}; vi source -> en story mean {Mean(scores, "translated_copy", "en", nEn):F6}. Cross-language translation copying is not detected (F18); mitigated by facts-only writer input (02.5.5).");

        return new CalibrationReport(
            generatedAt,
            sampleSet.Samples.Count,
            Grid,
            cells,
            chosen,
            previous,
            perf,
            residual,
            dilution,
            warnings,
            RerunTriggerTexts);
    }

    private static void Validate(SimilaritySampleSet set)
    {
        if (!string.Equals(set.Schema, Schema, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The calibration fixture schema is '{set.Schema}', expected '{Schema}'.");
        }

        if (set.Samples is null || set.Samples.Count == 0)
        {
            throw new InvalidOperationException("The calibration fixture has no samples.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sample in set.Samples)
        {
            if (string.IsNullOrWhiteSpace(sample.Id) || !ids.Add(sample.Id))
            {
                throw new InvalidOperationException($"The calibration fixture has an empty or duplicate id '{sample.Id}'.");
            }

            if (!LanguageNames.Contains(sample.Language, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Sample '{sample.Id}' has unknown language '{sample.Language}'.");
            }

            if (!ClassNames.Contains(sample.Class, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Sample '{sample.Id}' has unknown class '{sample.Class}'.");
            }

            if (sample.Source is null
                || string.IsNullOrWhiteSpace(sample.Source.Id)
                || string.IsNullOrWhiteSpace(sample.Source.Text))
            {
                throw new InvalidOperationException($"Sample '{sample.Id}' has no source text.");
            }

            var factSample = FactClassNames.Contains(sample.Class, StringComparer.Ordinal);
            if (factSample)
            {
                if (string.IsNullOrWhiteSpace(sample.Fact))
                {
                    throw new InvalidOperationException($"Fact sample '{sample.Id}' has no fact statement.");
                }

                if (sample.Story is not null)
                {
                    throw new InvalidOperationException($"Fact sample '{sample.Id}' must not carry a story.");
                }
            }
            else
            {
                if (sample.Story is null || string.IsNullOrWhiteSpace(sample.Story.Title)
                    || string.IsNullOrWhiteSpace(sample.Story.Hook)
                    || string.IsNullOrWhiteSpace(sample.Story.Ending)
                    || sample.Story.Paragraphs is null || sample.Story.Paragraphs.Count == 0)
                {
                    throw new InvalidOperationException($"Story sample '{sample.Id}' is incomplete.");
                }

                if (sample.Fact is not null)
                {
                    throw new InvalidOperationException($"Story sample '{sample.Id}' must not carry a fact.");
                }
            }
        }
    }

    private static int ChooseN(IReadOnlyList<CalibrationCell> cells, string language, List<string> warnings)
    {
        var candidates = Grid
            .Where(n => Min(cells, "verbatim_full", language, n) >= 0.9)
            .ToArray();
        if (candidates.Length == 0)
        {
            var fallback = language == "vi" ? 10 : 8;
            warnings.Add(
                $"No n in the grid keeps min(verbatim_full) at or above 0.9 for '{language}'; kept the default n={fallback}.");
            return fallback;
        }

        var specificity = candidates.ToDictionary(
            static n => n,
            n => SpecificityClassNames.Max(className => Max(cells, className, language, n)));
        var best = specificity.Values.Min();
        return specificity.Where(pair => pair.Value == best).Select(static pair => pair.Key).Max();
    }

    private static bool HasCandidate(IReadOnlyList<CalibrationCell> cells, string language) =>
        Grid.Any(n => Min(cells, "verbatim_full", language, n) >= 0.9);

    /// <summary>
    /// Owner rule 3 (2026-10-17): BlockAbove is the midpoint of A (the max specificity score) and B (the min
    /// verbatim_full score) at the chosen n, rounded down to the next 0.05.
    /// </summary>
    internal static decimal MidpointBlockAbove(double specificityMax, double verbatimMin) =>
        RoundDown((specificityMax + verbatimMin) / 2d, 0.05m);

    private static int ChosenN(string language, int nVi, int nEn) => language == "vi" ? nVi : nEn;

    private static CalibrationCell Cell(IReadOnlyList<CalibrationCell> cells, string className, string language, int n) =>
        cells.FirstOrDefault(cell => cell.Class == className && cell.Language == language && cell.N == n)
        ?? throw new InvalidOperationException($"No calibration cell for '{className}' '{language}' n={n}.");

    private static double Min(IReadOnlyList<CalibrationCell> cells, string className, string language, int n) =>
        Cell(cells, className, language, n).Min;

    private static double Max(IReadOnlyList<CalibrationCell> cells, string className, string language, int n) =>
        Cell(cells, className, language, n).Max;

    private static double Mean(IReadOnlyList<SampleScore> scores, string className, string language, int n)
    {
        var values = scores
            .Where(score => score.Class == className && score.Language == language && score.N == n)
            .Select(static score => score.Score)
            .OrderBy(static value => value)
            .ToArray();
        return values.Length == 0 ? 0 : Round6(values.Average());
    }

    private static decimal RoundUp(double value, decimal step) => Math.Ceiling(ToDecimal(value) / step) * step;

    private static decimal RoundDown(double value, decimal step) => Math.Floor(ToDecimal(value) / step) * step;

    private static decimal ToDecimal(double value) =>
        decimal.Parse(value.ToString("F6", CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture);

    private static double Round6(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private static Guid SourceGuid(SimilaritySample sample)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sample.Id + "|" + sample.Source.Id));
        return new Guid(hash.AsSpan(0, 16));
    }
}
