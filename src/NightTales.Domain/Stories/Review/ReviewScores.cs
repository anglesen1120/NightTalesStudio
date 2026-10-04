namespace NightTales.Domain.Stories.Review;

/// <summary>
/// The seven D16 scores of a <c>review.v1</c> (02 README D16, <c>prompts/fixtures/rubric.md</c>), 1–5 each.
/// Every key is required on the wire; <see cref="FactualCare"/> is <see langword="null"/> outside true crime.
/// </summary>
/// <param name="Hook">The opening 20 seconds.</param>
/// <param name="Pacing">The beat order and tempo.</param>
/// <param name="Atmosphere">Dread without gore.</param>
/// <param name="NaturalLanguage">Written for the ear.</param>
/// <param name="Originality">Premise, mechanism and twist.</param>
/// <param name="FactualCare">True-crime care for facts, or <see langword="null"/> for any other topic.</param>
/// <param name="LengthFit">The fit to the tier's band.</param>
public sealed record ReviewScores(
    int Hook,
    int Pacing,
    int Atmosphere,
    int NaturalLanguage,
    int Originality,
    int? FactualCare,
    int LengthFit)
{
    /// <summary>The lowest score.</summary>
    public const int Min = 1;

    /// <summary>The highest score.</summary>
    public const int Max = 5;

    /// <summary>The dimension names in the D22 order, as they appear on the wire.</summary>
    public static IReadOnlyList<string> Dimensions { get; } =
        ["hook", "pacing", "atmosphere", "natural_language", "originality", "factual_care", "length_fit"];

    /// <summary>The scored dimensions as <c>(wire name, value)</c> pairs; <c>factual_care</c> is skipped when it is <see langword="null"/>.</summary>
    /// <returns>The pairs in the D22 order.</returns>
    public IEnumerable<(string Name, int Value)> Scored()
    {
        yield return ("hook", Hook);
        yield return ("pacing", Pacing);
        yield return ("atmosphere", Atmosphere);
        yield return ("natural_language", NaturalLanguage);
        yield return ("originality", Originality);
        if (FactualCare is { } factual)
        {
            yield return ("factual_care", factual);
        }

        yield return ("length_fit", LengthFit);
    }
}
