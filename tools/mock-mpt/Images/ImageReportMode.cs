using System.Globalization;

namespace NightTales.MockMpt.Images;

/// <summary>The <c>MOCK_IMAGE_REPORT</c> values of 03.4.3-T2's table.</summary>
public enum ImageReportKind
{
    /// <summary><c>ok</c>: every planned slot succeeded.</summary>
    Ok,

    /// <summary><c>moderated:&lt;slots&gt;</c>: those slots fail moderation, spares are consumed, the task completes.</summary>
    ModeratedSlots,

    /// <summary><c>skipped:&lt;share&gt;</c>: a share of the slots fails upstream, the task completes.</summary>
    Skipped,

    /// <summary><c>moderated_all</c>: every slot is moderated; the task fails in <c>materials</c>.</summary>
    ModeratedAll,

    /// <summary><c>funds</c>: the first slot hits <c>429 insufficient_quota</c>, the rest is unattempted; the task fails in <c>materials</c>.</summary>
    Funds,

    /// <summary><c>none</c>: the <c>images</c> block is omitted.</summary>
    None,
}

/// <summary>
/// One parsed <c>MOCK_IMAGE_REPORT</c> value (or the report a keyword selected for one submit): it shapes the
/// <c>images</c> block of every submit, and only <see cref="ImageReportKind.ModeratedAll"/> and
/// <see cref="ImageReportKind.Funds"/> also force the failed <c>materials</c> terminal state.
/// </summary>
/// <param name="Kind">Which report the mock builds.</param>
/// <param name="Slots">The moderated slot indexes for <see cref="ImageReportKind.ModeratedSlots"/>.</param>
/// <param name="Share">The failed share in <c>[0,1]</c> for <see cref="ImageReportKind.Skipped"/>.</param>
public sealed record ImageReportMode(ImageReportKind Kind, IReadOnlySet<int>? Slots = null, double Share = 0)
{
    /// <summary>The <c>ok</c> default.</summary>
    public static ImageReportMode Ok { get; } = new(ImageReportKind.Ok);

    /// <summary>The <c>moderated_all</c> report (the <c>image_moderated</c> alias and <c>__fail_image__</c>).</summary>
    public static ImageReportMode ModeratedAll { get; } = new(ImageReportKind.ModeratedAll);

    /// <summary>True for the values that end the task as <c>failed</c> in <c>materials</c>.</summary>
    public bool ForcesFailure => Kind is ImageReportKind.ModeratedAll or ImageReportKind.Funds;

    /// <summary>The wire spelling, as written in <c>MOCK_IMAGE_REPORT</c> and the log.</summary>
    public string Name => Kind switch
    {
        ImageReportKind.ModeratedSlots => "moderated:" + string.Join(',', (Slots ?? new HashSet<int>()).Order()),
        ImageReportKind.Skipped => "skipped:" + Share.ToString("0.####", CultureInfo.InvariantCulture),
        ImageReportKind.ModeratedAll => "moderated_all",
        ImageReportKind.Funds => "funds",
        ImageReportKind.None => "none",
        _ => "ok",
    };

    /// <summary>Parses the environment value; blank means <c>ok</c>. An unknown or malformed value is an error.</summary>
    public static bool TryParse(string? raw, out ImageReportMode mode, out string? error)
    {
        mode = Ok;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var value = raw.Trim();
        switch (value)
        {
            case "ok":
                return true;
            case "moderated_all":
                mode = ModeratedAll;
                return true;
            case "funds":
                mode = new ImageReportMode(ImageReportKind.Funds);
                return true;
            case "none":
                mode = new ImageReportMode(ImageReportKind.None);
                return true;
        }

        if (value.StartsWith("moderated:", StringComparison.Ordinal) && TryParseSlots(value["moderated:".Length..], out var slots))
        {
            mode = new ImageReportMode(ImageReportKind.ModeratedSlots, slots);
            return true;
        }

        if (value.StartsWith("skipped:", StringComparison.Ordinal)
            && double.TryParse(value["skipped:".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var share)
            && share is >= 0 and <= 1)
        {
            mode = new ImageReportMode(ImageReportKind.Skipped, Share: share);
            return true;
        }

        error =
            $"MOCK_IMAGE_REPORT='{value}' is not accepted; accepted: ok, moderated:<slots> (e.g. moderated:3,7), "
            + "skipped:<share 0-1>, moderated_all, funds, none";
        return false;
    }

    private static bool TryParseSlots(string text, out IReadOnlySet<int> slots)
    {
        var parsed = new HashSet<int>();
        slots = parsed;
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var slot))
            {
                return false;
            }

            parsed.Add(slot);
        }

        return parsed.Count > 0;
    }
}
