namespace NightTales.TestKit.Stories;

/// <summary>
/// What a Worker harness host needs to boot the story generation job (02.2.2-T3): the Worker now composes
/// <c>StoryQualityChecker</c> (the job runs it at generation time), whose <c>Story:Quality</c> thresholds are all
/// required and have no code default. The harness clears the Worker's <c>appsettings.json</c>, so it supplies the same
/// values the shipped file carries (the Api's thresholds, 02.3.4-T1). <c>Story:Job</c>, <c>Story:Sweeper</c> and
/// <c>Story:Review</c> have in-code defaults and need nothing here.
/// </summary>
public static class StoryTestSettings
{
    /// <summary>The settings a Worker harness host adds.</summary>
    public static IReadOnlyDictionary<string, string?> WorkerSettings { get; } =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Story:Quality:LengthTolerance"] = "0.15",
            ["Story:Quality:MinHookTokens"] = "3",
            ["Story:Quality:MaxHookSentences"] = "3",
            ["Story:Quality:KeywordsPerSegment:0"] = "1",
            ["Story:Quality:KeywordsPerSegment:1"] = "2",
            ["Story:Quality:KeywordsPerMinute"] = "6",
            ["Story:Quality:RepeatedSentenceMinTokens"] = "12",
            ["Story:Quality:MaxInternalShingleShare"] = "0.25",
            ["Story:Quality:NameHeuristic"] = "true",
        };
}
