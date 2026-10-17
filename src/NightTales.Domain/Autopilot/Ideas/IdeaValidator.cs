using System.Globalization;
using NightTales.Domain.Stories.Content;
using NightTales.Domain.Stories.Structure;

namespace NightTales.Domain.Autopilot.Ideas;

/// <summary>
/// The pure content rules of an episode idea (02.8.1 plan §3.2): what the strict <c>ideas.v1</c> schema
/// cannot express, checked per candidate so a violating candidate is dropped (02.8.1-T2's <c>invalid</c>
/// reason) rather than failing the whole run.
/// </summary>
/// <remarks>
/// <para>
/// The four text caps are the <c>varchar</c> widths of the table (see <see cref="IdeaLimits"/>), a text must
/// be non-blank, and no text may carry a markup marker, a list marker or an angle bracket. The beat list must
/// be inside the schema's 3–8 band, every name must be one of the six D3 beats, the outline must not repeat a
/// beat immediately and must carry at least one <c>twist</c> or <c>reveal</c>. The five structure values must
/// be non-empty. Nothing here scores, dedupes or policy-checks a candidate — those are 02.8.1-T2's and
/// 02.8.4-T1's steps.
/// </para>
/// <para>
/// Lengths are counted in Unicode code points, not UTF-16 units, because Postgres measures a
/// <c>varchar(n)</c> in characters and .NET <see cref="string.Length"/> counts surrogate pairs twice: a
/// 101-emoji title is 101 characters to the column but 202 to <see cref="string.Length"/>, and the rule must
/// refuse what the column would refuse and accept what it would accept.
/// </para>
/// <para>
/// The validator is pure and deterministic — no clock, no I/O, nothing mutated — so the same candidate always
/// yields the same list, in field order, which is what the run log and the tests key on.
/// </para>
/// </remarks>
public static class IdeaValidator
{
    /// <summary>The value exceeds its field's limit; the detail is the limit.</summary>
    public const string TooLong = "too_long";

    /// <summary>The field is absent or whitespace only.</summary>
    public const string Empty = "empty";

    /// <summary>The value carries an angle bracket (the D3/F36 metadata rule); the detail is <c>angle_bracket</c>.</summary>
    public const string InvalidStructure = "invalid_structure";

    /// <summary>The outline has fewer beats than its band allows; the detail is the minimum.</summary>
    public const string TooFew = "too_few";

    /// <summary>The outline has more beats than its band allows; the detail is the maximum.</summary>
    public const string TooMany = "too_many";

    /// <summary>A beat is not one of the six D3 beats; the detail is the value.</summary>
    public const string BeatInvalid = "beat_invalid";

    /// <summary>Two adjacent beats are the same; the detail is the repeated beat.</summary>
    public const string BeatRepeated = "beat_repeated";

    /// <summary>The outline carries neither a <c>twist</c> nor a <c>reveal</c>.</summary>
    public const string BeatMissingTurn = "beat_missing_turn";

    /// <summary>The configured <see cref="IdeaLimits"/> are unusable (a non-positive cap, an inverted beat band).</summary>
    public const string InvalidLimits = "invalid_limits";

    /// <summary>The field path of a violation of the limits rather than of a candidate.</summary>
    public const string LimitsField = "limits";

    /// <summary>The two beats an outline must carry at least one of.</summary>
    private static HashSet<string> TurnBeats { get; } = new(StringComparer.Ordinal)
    {
        "twist",
        "reveal",
    };

    /// <summary>
    /// Validates one candidate against <paramref name="limits"/> without throwing for a malformed candidate:
    /// a violating candidate is dropped by the caller, not repaired here.
    /// </summary>
    /// <param name="candidate">The candidate the model returned.</param>
    /// <param name="limits">The configured limits.</param>
    /// <returns>Every violation in field order; empty when the candidate is valid.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> or <paramref name="limits"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<IdeaFieldError> Validate(IdeaCandidate candidate, IdeaLimits limits)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(limits);

        var errors = new List<IdeaFieldError>();

        if (!limits.IsValid)
        {
            return [new IdeaFieldError(LimitsField, InvalidLimits, null)];
        }

        ValidateText(errors, "title", candidate.Title, limits.TitleChars);
        ValidateText(errors, "logline", candidate.Logline, limits.LoglineChars);
        ValidateText(errors, "hook", candidate.Hook, limits.HookChars);
        ValidateText(errors, "setting", candidate.Setting, limits.SettingChars);
        ValidateBeats(errors, candidate.Beats, limits);
        ValidateStructure(errors, candidate.Structure);

        return errors;
    }

    /// <summary>
    /// Validates a whole batch and returns the candidates that are valid, in batch order — the caller's path
    /// of 02.8.1 plan §4 U1 step 4, where an empty answer is the run's <c>ideas_invalid_output</c>.
    /// </summary>
    /// <param name="candidates">The candidates the model returned.</param>
    /// <param name="limits">The configured limits.</param>
    /// <returns>The valid candidates, in batch order; empty when every candidate has at least one violation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidates"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<IdeaCandidate> Valid(IReadOnlyList<IdeaCandidate> candidates, IdeaLimits limits)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var valid = new List<IdeaCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (Validate(candidate, limits).Count == 0)
            {
                valid.Add(candidate);
            }
        }

        return valid;
    }

    /// <summary>Validates one of the four text fields: non-blank, free of markup and angle brackets, then inside its cap.</summary>
    private static void ValidateText(List<IdeaFieldError> errors, string field, string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new IdeaFieldError(field, Empty, null));
            return;
        }

        if (value.Contains('<') || value.Contains('>'))
        {
            errors.Add(new IdeaFieldError(field, InvalidStructure, "angle_bracket"));
            return;
        }

        if (IdeaText.FindMarkup(value) is { } marker)
        {
            errors.Add(new IdeaFieldError(field, InvalidStructure, marker));
            return;
        }

        if (CodePointCount(value) > maxChars)
        {
            errors.Add(new IdeaFieldError(field, TooLong, Limit(maxChars)));
        }
    }

    /// <summary>Validates the outline: present, inside its band, every name a D3 beat, no immediate repeat, at least one turn.</summary>
    private static void ValidateBeats(List<IdeaFieldError> errors, IReadOnlyList<string>? beats, IdeaLimits limits)
    {
        if (beats is null || beats.Count == 0)
        {
            errors.Add(new IdeaFieldError("beats", Empty, null));
            return;
        }

        if (beats.Count < limits.MinBeats)
        {
            errors.Add(new IdeaFieldError("beats", TooFew, Limit(limits.MinBeats)));
            return;
        }

        if (beats.Count > limits.MaxBeats)
        {
            errors.Add(new IdeaFieldError("beats", TooMany, Limit(limits.MaxBeats)));
            return;
        }

        var hasTurn = false;
        for (var index = 0; index < beats.Count; index++)
        {
            var beat = beats[index];
            if (string.IsNullOrWhiteSpace(beat) || !StoryEnums.Beats.Contains(beat))
            {
                errors.Add(new IdeaFieldError(Field("beats", index), BeatInvalid, beat));
                continue;
            }

            if (index > 0 && string.Equals(beat, beats[index - 1], StringComparison.Ordinal))
            {
                errors.Add(new IdeaFieldError(Field("beats", index), BeatRepeated, beat));
            }

            if (TurnBeats.Contains(beat))
            {
                hasTurn = true;
            }
        }

        if (!hasTurn)
        {
            errors.Add(new IdeaFieldError("beats", BeatMissingTurn, "twist"));
        }
    }

    /// <summary>Validates the five structure values: each must be present and non-blank.</summary>
    private static void ValidateStructure(List<IdeaFieldError> errors, StoryStructureTags? structure)
    {
        if (structure is null)
        {
            errors.Add(new IdeaFieldError("structure", Empty, null));
            return;
        }

        AddStructureValue(errors, "structure.sub_genre", structure.SubGenre);
        AddStructureValue(errors, "structure.setting", structure.Setting);
        AddStructureValue(errors, "structure.opening", structure.Opening);
        AddStructureValue(errors, "structure.twist_type", structure.TwistType);
        AddStructureValue(errors, "structure.narrator_mode", structure.NarratorMode);
    }

    /// <summary>Adds <c>empty</c> when one structure value is absent or whitespace only.</summary>
    private static void AddStructureValue(List<IdeaFieldError> errors, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new IdeaFieldError(field, Empty, null));
        }
    }

    /// <summary>The number of Unicode code points, which is what a Postgres <c>varchar</c> length counts.</summary>
    private static int CodePointCount(string text)
    {
        var count = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                index++;
            }

            count++;
        }

        return count;
    }

    /// <summary>The JSON path of one array entry.</summary>
    private static string Field(string arrayField, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"{arrayField}[{index}]");

    /// <summary>The <c>Detail</c> of a limit violation: the limit itself, never the value.</summary>
    private static string Limit(int max) => max.ToString(CultureInfo.InvariantCulture);
}
