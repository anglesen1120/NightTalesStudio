using System.Diagnostics.CodeAnalysis;

namespace NightTales.Application.Abstractions.Text;

/// <summary>
/// The outcome of one text call (02 README D7): exactly one of the sealed cases. Every case carries the
/// model that served (or would have served) the call, the usage when the provider reported it — failures
/// included (D14) — and the provider's request id (O8).
/// </summary>
/// <typeparam name="T">The output document type.</typeparam>
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "The D7/D21 contract names the factories TextCallResult<T>.Ok(...) etc.; the type argument is the call site's document type.")]
public abstract record TextCallResult<T>
    where T : class
{
    private protected TextCallResult(string model, TokenUsage? usage, string? requestId)
    {
        Model = model;
        Usage = usage;
        RequestId = requestId;
    }

    /// <summary>The model of the call, from configuration.</summary>
    public string Model { get; }

    /// <summary>The reported usage; <see langword="null"/> when the response carried none.</summary>
    public TokenUsage? Usage { get; }

    /// <summary>The provider's <c>x-request-id</c>, or <see langword="null"/> when unavailable.</summary>
    public string? RequestId { get; }

    /// <summary>The output parsed as <typeparamref name="T"/>.</summary>
    public static TextCallResult<T> Ok(T draft, string model, TokenUsage? usage, string? requestId) =>
        new TextCallOk<T>(draft, model, usage, requestId);

    /// <summary>The output was truncated or did not match the schema; <paramref name="ruleIds"/> name why.</summary>
    public static TextCallResult<T> InvalidOutput(string model, TokenUsage? usage, string? requestId, IReadOnlyList<string> ruleIds) =>
        new TextCallInvalidOutput<T>(ruleIds, model, usage, requestId);

    /// <summary>The provider refused or filtered the content.</summary>
    public static TextCallResult<T> Moderated(string model, TokenUsage? usage, string? requestId, IReadOnlyList<string> categories) =>
        new TextCallModerated<T>(categories, model, usage, requestId);

    /// <summary>The provider account is out of funds or at its spend limit.</summary>
    public static TextCallResult<T> ProviderFundsExhausted(string model, string? requestId) =>
        new TextCallFundsExhausted<T>(model, requestId);

    /// <summary>The provider refused the request with a non-retryable status.</summary>
    public static TextCallResult<T> ProviderError(string model, int status, string? requestId) =>
        new TextCallProviderError<T>(status, model, requestId);

    /// <summary>The provider timed out or failed (5xx) past the retry budget.</summary>
    public static TextCallResult<T> ProviderUnavailable(string model, string? requestId) =>
        new TextCallUnavailable<T>(model, requestId);
}

/// <summary>The output parsed as <typeparamref name="T"/> and carried no refusal.</summary>
/// <typeparam name="T">The output document type.</typeparam>
public sealed record TextCallOk<T> : TextCallResult<T>
    where T : class
{
    internal TextCallOk(T draft, string model, TokenUsage? usage, string? requestId)
        : base(model, usage, requestId) => Draft = draft;

    /// <summary>The parsed output.</summary>
    public T Draft { get; }
}

/// <summary>The output was truncated (<c>max_output_tokens_exceeded</c>) or malformed (<c>schema_invalid</c>).</summary>
/// <typeparam name="T">The output document type.</typeparam>
public sealed record TextCallInvalidOutput<T> : TextCallResult<T>
    where T : class
{
    /// <summary>The rule id for a stop at <c>max_output_tokens</c>.</summary>
    public const string MaxOutputTokensExceeded = "max_output_tokens_exceeded";

    /// <summary>The rule id for JSON that does not parse as the output type.</summary>
    public const string SchemaInvalid = "schema_invalid";

    internal TextCallInvalidOutput(IReadOnlyList<string> ruleIds, string model, TokenUsage? usage, string? requestId)
        : base(model, usage, requestId) => RuleIds = ruleIds;

    /// <summary>Why the output is invalid; rule ids only, never output text.</summary>
    public IReadOnlyList<string> RuleIds { get; }
}

/// <summary>The provider refused (a refusal part) or filtered (<c>content_filter</c>) the content.</summary>
/// <typeparam name="T">The output document type.</typeparam>
public sealed record TextCallModerated<T> : TextCallResult<T>
    where T : class
{
    internal TextCallModerated(IReadOnlyList<string> categories, string model, TokenUsage? usage, string? requestId)
        : base(model, usage, requestId) => Categories = categories;

    /// <summary>The filtered categories when the provider named them; empty otherwise.</summary>
    public IReadOnlyList<string> Categories { get; }
}

/// <summary>The provider account has no funds left (spend limit or credit balance).</summary>
/// <typeparam name="T">The output document type.</typeparam>
public sealed record TextCallFundsExhausted<T> : TextCallResult<T>
    where T : class
{
    internal TextCallFundsExhausted(string model, string? requestId)
        : base(model, null, requestId)
    {
    }
}

/// <summary>The provider refused the request: another 4xx, or an unmodelled failure (<c>Status</c> 0).</summary>
/// <typeparam name="T">The output document type.</typeparam>
public sealed record TextCallProviderError<T> : TextCallResult<T>
    where T : class
{
    internal TextCallProviderError(int status, string model, string? requestId)
        : base(model, null, requestId) => Status = status;

    /// <summary>The HTTP status, or 0 for an unexpected exception.</summary>
    public int Status { get; }
}

/// <summary>The provider timed out or answered 5xx/429 after the single retry.</summary>
/// <typeparam name="T">The output document type.</typeparam>
public sealed record TextCallUnavailable<T> : TextCallResult<T>
    where T : class
{
    internal TextCallUnavailable(string model, string? requestId)
        : base(model, null, requestId)
    {
    }
}
