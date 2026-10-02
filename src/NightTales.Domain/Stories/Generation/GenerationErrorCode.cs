using System.Text.Json;
using System.Text.Json.Serialization;
using ErrorOr;

namespace NightTales.Domain.Stories.Generation;

/// <summary>
/// The code a generation run ended with (02 D8 job-outcome codes plus the two platform pause reasons of
/// <see cref="GenerationPauseReason"/>): a lowercase snake_case token, never an HTTP code and never free text.
/// </summary>
/// <remarks>
/// The well-known members are the story codes the Domain itself needs; every other D8 code (for example
/// <c>fact_sheet_invalid_output</c>) is built with <see cref="Create"/>. The outbox stores it as a plain JSON
/// string.
/// </remarks>
[JsonConverter(typeof(GenerationErrorCodeJsonConverter))]
public sealed record GenerationErrorCode
{
    private const int MaxLength = 64;

    private GenerationErrorCode(string value)
    {
        Value = value;
    }

    /// <summary>Story run outcome: schema-invalid JSON twice (D8).</summary>
    public static GenerationErrorCode StoryInvalidOutput { get; } = new("story_invalid_output");

    /// <summary>Story run outcome: refusal, <c>content_filter</c> or a flagged brief (D8, D12).</summary>
    public static GenerationErrorCode StoryModerated { get; } = new("story_moderated");

    /// <summary>Story run outcome: provider error or unavailable after the retry budget (D8).</summary>
    public static GenerationErrorCode StoryGenerationFailed { get; } = new("story_generation_failed");

    /// <summary>Story run outcome: cancelled by the system, an admin or account deletion (D8, TL T20).</summary>
    public static GenerationErrorCode StoryGenerationCancelled { get; } = new("story_generation_cancelled");

    /// <summary>Platform pause: our own daily/monthly USD cap (D1).</summary>
    public static GenerationErrorCode SpendCapReached { get; } = new("spend_cap_reached");

    /// <summary>Platform pause: the provider account cannot be charged (12.8.3).</summary>
    public static GenerationErrorCode ProviderFundsExhausted { get; } = new("provider_funds_exhausted");

    /// <summary>The wire value, for example <c>story_moderated</c>.</summary>
    public string Value { get; }

    /// <summary>Builds a code from its wire value.</summary>
    /// <param name="value">A lowercase snake_case token of at most 64 characters.</param>
    /// <returns>The code, or a validation error when the token is empty, too long or not snake_case.</returns>
    public static ErrorOr<GenerationErrorCode> Create(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || !IsSnakeCase(value))
        {
            return Error.Validation(
                "generation_error_code_invalid",
                "A generation error code is a lowercase snake_case token of at most 64 characters.");
        }

        return new GenerationErrorCode(value);
    }

    /// <summary>The code a run paused for <paramref name="reason"/> carries.</summary>
    /// <param name="reason">The pause reason.</param>
    /// <returns>The matching platform code.</returns>
    public static GenerationErrorCode FromPause(GenerationPauseReason reason) => reason switch
    {
        GenerationPauseReason.SpendCapReached => SpendCapReached,
        GenerationPauseReason.ProviderFundsExhausted => ProviderFundsExhausted,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown pause reason."),
    };

    /// <inheritdoc />
    public override string ToString() => Value;

    private static bool IsSnakeCase(string value)
    {
        if (value[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Writes the code as its plain JSON string and reads it back through <see cref="Create"/>.</summary>
    private sealed class GenerationErrorCodeJsonConverter : JsonConverter<GenerationErrorCode>
    {
        public override GenerationErrorCode Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            var created = Create(reader.GetString());
            return created.IsError
                ? throw new JsonException(created.FirstError.Description)
                : created.Value;
        }

        public override void Write(Utf8JsonWriter writer, GenerationErrorCode value, JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentNullException.ThrowIfNull(value);
            writer.WriteStringValue(value.Value);
        }
    }
}
