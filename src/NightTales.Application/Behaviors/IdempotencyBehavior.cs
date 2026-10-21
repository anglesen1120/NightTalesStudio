using System.Buffers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using ErrorOr;
using Mediator;
using Microsoft.Extensions.Logging;
using NightTales.Application.Abstractions;
using NightTales.Application.Abstractions.Idempotency;
using NightTales.Application.Abstractions.Messaging;
using NightTales.Application.Common.Errors;
using NightTales.Domain.Common;

namespace NightTales.Application.Behaviors;

/// <summary>
/// Claims an <c>IIdempotentCommand</c>'s key inside the UnitOfWork transaction and replays the stored
/// success value without running the handler, so a retry or a racing twin executes the command exactly
/// once (09 README D5/D6).
/// </summary>
/// <remarks>
/// <para>
/// A claim and the command's effects share one transaction: an error result rolls the claim back with
/// everything else, and a crash before the commit leaves no key row — the retry executes once. The
/// claim itself waits on the unique <c>(user_id, key)</c> index, so a twin of an in-flight request
/// blocks until the first commits and then replays its response.
/// </para>
/// <para>
/// The request hash is canonical: the message type's full name, then the message serialized with
/// camelCase names, sorted properties, no indentation and the <c>IdempotencyKey</c> member removed, so
/// the key is never part of the body it identifies.
/// </para>
/// </remarks>
public sealed partial class IdempotencyBehavior<TMessage, TResponse>(
    IIdempotencyStore store,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<IdempotencyBehavior<TMessage, TResponse>> logger)
    : IPipelineBehavior<TMessage, TResponse>
    // Mediator requires IMessage on every behaviour; IIdempotentCommand is the narrower, semantic half
    // (09.1.3-T1 declares the marker on its own, 09.1.3 plan §3).
    where TMessage : IMessage, IIdempotentCommand
    where TResponse : IErrorOr
{
    private const string KeyReusedDescription = "This Idempotency-Key was already used for a different request body.";

    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        // The endpoint filter requires the key where it is required (09.1.4-T1); an anonymous scope owns
        // no key row (idempotency_keys.user_id is NOT NULL) and the authorization behaviour has already
        // refused it unless the message opted out, so both cases run the handler once and store nothing.
        if (string.IsNullOrEmpty(message.IdempotencyKey) || currentUser.Id is not { } userId)
        {
            return await next(message, cancellationToken).ConfigureAwait(false);
        }

        var claim = await store
            .ClaimAsync(userId, message.IdempotencyKey, RequestHash(message), clock.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        switch (claim)
        {
            case IdempotencyClaim.Fresh claimed:
                return await ExecuteAsync(message, claimed.ClaimId, next, cancellationToken).ConfigureAwait(false);

            case IdempotencyClaim.Replay replay:
                return Replay(replay);

            case IdempotencyClaim.Mismatch:
                return KeyReused();

            default:
                throw new InvalidOperationException($"Unhandled idempotency claim '{claim.GetType().Name}'.");
        }
    }

    /// <summary>Runs the handler and stores its success value in the same transaction.</summary>
    private async ValueTask<TResponse> ExecuteAsync(
        TMessage message,
        Guid claimId,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        var response = await next(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsError)
        {
            // An error result is not stored: UnitOfWorkBehavior rolls the claim back with the effects.
            // The value is boxed (IErrorOr alone has no Value), so the serializer takes its runtime type.
            var valueType = ErrorOrValueFactory<TResponse>.ValueType;
            var json = JsonSerializer.Serialize(ErrorOrValueFactory<TResponse>.ValueOf(response), Options);
            await store.CompleteAsync(claimId, valueType.FullName ?? valueType.Name, json, cancellationToken)
                .ConfigureAwait(false);
        }

        return response;
    }

    /// <summary>
    /// Returns the stored success value as if the handler had produced it. A stored type that no longer
    /// matches the current success type (a rename across a deploy) and a payload that does not
    /// deserialize to it are both answered as a reused key, with a warning (09.1.3-T2 [DECISION]).
    /// </summary>
    private TResponse Replay(IdempotencyClaim.Replay replay)
    {
        var valueType = ErrorOrValueFactory<TResponse>.ValueType;
        var expectedType = valueType.FullName ?? valueType.Name;
        var messageName = typeof(TMessage).Name;

        if (!string.Equals(replay.ResponseType, expectedType, StringComparison.Ordinal))
        {
            LogResponseTypeMismatch(messageName, replay.ResponseType, expectedType);
            return KeyReused();
        }

        object? value;
        try
        {
            value = JsonSerializer.Deserialize(replay.ResponseJson, valueType, Options);
        }
        catch (JsonException exception)
        {
            LogResponseDeserializeFailed(messageName, exception.Message);
            return KeyReused();
        }

        if (value is null)
        {
            LogResponseDeserializeFailed(messageName, "null");
            return KeyReused();
        }

        return ErrorOrValueFactory<TResponse>.From(value);
    }

    private static TResponse KeyReused() =>
        ErrorOrFactory<TResponse>.From([Error.Failure(ErrorCodes.IdempotencyKeyReused, KeyReusedDescription)]);

    /// <summary>
    /// The canonical request hash (D6): the SHA-256 hex of the message type's full name, a newline, and
    /// the message's canonical JSON.
    /// </summary>
    private static string RequestHash(TMessage message)
    {
        var typeName = typeof(TMessage).FullName ?? typeof(TMessage).Name;
        var payload = string.Concat(typeName, "\n", CanonicalJson(message));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>
    /// The message's JSON with its properties in ordinal order, camelCase and no indentation, so the hash
    /// of one body is the same on every node and in every deploy.
    /// </summary>
    private static string CanonicalJson(TMessage message)
    {
        if (JsonSerializer.SerializeToNode(message, Options) is not JsonObject body)
        {
            // A message that serializes to a scalar carries no body: its type name is the whole payload.
            return string.Empty;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            foreach (var property in body.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Key);
                if (property.Value is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// The canonical serializer: camelCase (the web defaults) and no indentation, plus a modifier that
    /// drops the interface's <c>IdempotencyKey</c> member wherever a message implements it.
    /// </summary>
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RemoveIdempotencyKey } };

        return options;
    }

    /// <summary>
    /// Removes the <see cref="IIdempotentCommand.IdempotencyKey"/> property from every message that
    /// implements the marker, whatever JSON name it declares: the key identifies the request, it is not
    /// part of the body the request hash covers.
    /// </summary>
    private static void RemoveIdempotencyKey(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type is null || !typeof(IIdempotentCommand).IsAssignableFrom(typeInfo.Type))
        {
            return;
        }

        for (var index = typeInfo.Properties.Count - 1; index >= 0; index--)
        {
            if (typeInfo.Properties[index].AttributeProvider is MemberInfo member
                && string.Equals(member.Name, nameof(IIdempotentCommand.IdempotencyKey), StringComparison.Ordinal))
            {
                typeInfo.Properties.RemoveAt(index);
            }
        }
    }

    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Warning,
        Message = "idempotency.replay_type_mismatch message={Message} storedType={StoredType} expectedType={ExpectedType}")]
    private partial void LogResponseTypeMismatch(string message, string storedType, string expectedType);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "idempotency.replay_deserialize_failed message={Message} error={Error}")]
    private partial void LogResponseDeserializeFailed(string message, string error);
}
