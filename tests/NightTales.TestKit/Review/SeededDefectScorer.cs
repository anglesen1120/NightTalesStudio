using NightTales.Domain.Stories.Review;

namespace NightTales.TestKit.Review;

/// <summary>
/// The measured outcome of one seeded-defect run (02.6.1-T2, Design §Scorer): how many of the planted objective
/// defects the review model found, how often it blocked a clean draft, and the per-type and cost signals the TL
/// reads. Pure data; the scorer is the only writer.
/// </summary>
/// <param name="Drafts">The number of drafts scored.</param>
/// <param name="CleanDrafts">The number of drafts that must review clean.</param>
/// <param name="SeededDefects">The number of planted objective defects across the seeded drafts.</param>
/// <param name="DetectedDefects">The number of planted defects an issue of the same type and segment matched.</param>
/// <param name="Recall"><c>DetectedDefects / SeededDefects</c>; the D15 gate is <c>&gt;= 0.80</c>.</param>
/// <param name="CleanDraftsWithBlocking">The number of clean drafts whose review carried at least one <c>blocking</c> issue.</param>
/// <param name="FalseBlockRate"><c>CleanDraftsWithBlocking / CleanDrafts</c>; the D15 gate is <c>&lt;= 0.10</c>.</param>
/// <param name="RecallByType">Recall per seeded defect type, in ordinal type-name order; exposes a type the prompt systematically misses.</param>
/// <param name="UnseededBlockingTypes">The distinct blocking issue types the model reported on clean drafts, in ordinal order.</param>
/// <param name="CostUsd">The measured USD cost of the run.</param>
public sealed record SeededDefectReport(
    int Drafts,
    int CleanDrafts,
    int SeededDefects,
    int DetectedDefects,
    double Recall,
    int CleanDraftsWithBlocking,
    double FalseBlockRate,
    IReadOnlyDictionary<string, double> RecallByType,
    IReadOnlyList<string> UnseededBlockingTypes,
    decimal CostUsd);

/// <summary>
/// The 02.6.1-T2 seeded-defect scorer (Design §Scorer): pure, no I/O, unit-testable without the API. It turns a list
/// of <see cref="SeededDraft"/>s and the <see cref="StoryReviewV1"/> reviews the model returned for them into a
/// <see cref="SeededDefectReport"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Detection rule.</b> A planted defect is detected when the review of its own draft carries an issue whose
/// <c>type</c> equals the seeded <c>type</c> and whose <c>segment</c> equals the seeded <c>segment</c> (ordinal
/// string equality on the wire values). Severity does not matter for recall: a <c>blocking</c> or an <c>advisory</c>
/// issue counts the same, because recall asks "did the reviewer see it", not "did it over-block".
/// </para>
/// <para>
/// <b>False-blocking.</b> <c>falseBlockRate</c> counts only drafts with <c>clean: true</c> whose review carries at
/// least one issue with <c>severity = blocking</c>; an advisory issue on a clean draft is not a false block.
/// </para>
/// <para>
/// <b>Unseeded blocking types.</b> The Design says "blocking types the model invents on clean drafts". The list is
/// therefore the distinct <c>type</c> values of the <c>blocking</c> issues reported on the clean drafts, in ordinal
/// order. It is a precision signal for the TL (a clean draft should carry no blocking issue at all); a blocking type
/// on a <i>seeded</i> draft is excluded, because there the same type may be a planted defect or a near-miss for it.
/// </para>
/// <para>
/// <b>Determinism.</b> Zero clean drafts (false-blocking undefined) or zero seeded defects (recall undefined) throws
/// <see cref="ArgumentException"/> rather than returning NaN; a review/draft count mismatch throws too, since
/// <c>reviews[i]</c> must be the review of <c>drafts[i]</c>.
/// </para>
/// </remarks>
public static class SeededDefectScorer
{
    /// <summary>The severity text that counts a clean draft as falsely blocked.</summary>
    public const string Blocking = "blocking";

    /// <summary>Scores one run of the seeded-defect set.</summary>
    /// <param name="drafts">The drafts that were reviewed, in order.</param>
    /// <param name="reviews">The reviews the model returned; <c>reviews[i]</c> is the review of <c>drafts[i]</c>.</param>
    /// <param name="costUsd">The measured USD cost of the run.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentNullException">A list is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The lists differ in length, there are no drafts, no clean drafts or no seeded defects.</exception>
    public static SeededDefectReport Score(
        IReadOnlyList<SeededDraft> drafts,
        IReadOnlyList<StoryReviewV1> reviews,
        decimal costUsd)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        ArgumentNullException.ThrowIfNull(reviews);

        if (drafts.Count != reviews.Count)
        {
            throw new ArgumentException(
                $"reviews[i] must be the review of drafts[i], but there are {drafts.Count} drafts and {reviews.Count} reviews.",
                nameof(reviews));
        }

        if (drafts.Count == 0)
        {
            throw new ArgumentException("The seeded-defect set is empty, so recall and false-blocking are undefined.", nameof(drafts));
        }

        var cleanDrafts = 0;
        var seededDefects = 0;
        foreach (var draft in drafts)
        {
            if (draft.Clean)
            {
                cleanDrafts++;
            }
            else
            {
                seededDefects += draft.SeedDefects.Count;
            }
        }

        if (cleanDrafts == 0)
        {
            throw new ArgumentException("The set has no clean draft, so the false-block rate is undefined.", nameof(drafts));
        }

        if (seededDefects == 0)
        {
            throw new ArgumentException("The set has no seeded defect, so recall is undefined.", nameof(drafts));
        }

        var detectedDefects = 0;
        var cleanDraftsWithBlocking = 0;
        var seededByType = new Dictionary<string, int>(StringComparer.Ordinal);
        var detectedByType = new Dictionary<string, int>(StringComparer.Ordinal);
        var unseededBlockingTypes = new SortedSet<string>(Comparer<string>.Create(static (left, right) => string.CompareOrdinal(left, right)));

        for (var i = 0; i < drafts.Count; i++)
        {
            var draft = drafts[i];
            var issues = reviews[i].Issues;

            if (draft.Clean)
            {
                var blocking = false;
                foreach (var issue in issues)
                {
                    if (!string.Equals(issue.Severity, Blocking, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    blocking = true;
                    unseededBlockingTypes.Add(issue.Type);
                }

                if (blocking)
                {
                    cleanDraftsWithBlocking++;
                }

                continue;
            }

            foreach (var defect in draft.SeedDefects)
            {
                seededByType[defect.Type] = seededByType.GetValueOrDefault(defect.Type) + 1;

                if (IsDetected(issues, defect))
                {
                    detectedDefects++;
                    detectedByType[defect.Type] = detectedByType.GetValueOrDefault(defect.Type) + 1;
                }
            }
        }

        var recallByType = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var (type, seeded) in seededByType)
        {
            recallByType[type] = (double)detectedByType.GetValueOrDefault(type) / seeded;
        }

        return new SeededDefectReport(
            drafts.Count,
            cleanDrafts,
            seededDefects,
            detectedDefects,
            (double)detectedDefects / seededDefects,
            cleanDraftsWithBlocking,
            (double)cleanDraftsWithBlocking / cleanDrafts,
            recallByType,
            [.. unseededBlockingTypes],
            costUsd);
    }

    private static bool IsDetected(IReadOnlyList<ReviewIssueV1> issues, SeededDefect defect)
    {
        foreach (var issue in issues)
        {
            if (string.Equals(issue.Type, defect.Type, StringComparison.Ordinal)
                && string.Equals(issue.Segment, defect.Segment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
