namespace NightTales.TestKit.OpenAI;

/// <summary>
/// What a Worker host needs to boot the OpenAI text adapter (02.2.1-T2): <c>OpenAi:ApiKey</c> is required in
/// every environment. The value is a throwaway placeholder for a run that never reaches the provider — no
/// test host calls OpenAI (live calls are gated by <c>OPENAI_LIVE_TESTS=1</c>) — so it is deliberately not
/// key-shaped and no secret is committed.
/// </summary>
public static class OpenAiTestSettings
{
    /// <summary>The configuration key of the API key.</summary>
    public const string ApiKeyKey = "OpenAi:ApiKey";

    /// <summary>The placeholder key a harness host is given.</summary>
    public const string ApiKey = "harness-openai-api-key-placeholder";

    /// <summary>The settings a Worker harness host adds.</summary>
    public static IReadOnlyDictionary<string, string?> WorkerSettings { get; } =
        new Dictionary<string, string?>(StringComparer.Ordinal) { [ApiKeyKey] = ApiKey };
}
