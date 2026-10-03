using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using NightTales.Api.Http.Filters;

namespace NightTales.Api.Http;

/// <summary>
/// Publishes the <c>Idempotency-Key</c> header the way <see cref="RequireIdempotencyKeyFilter"/> enforces it:
/// required, a string of at most <see cref="RequireIdempotencyKeyFilter.MaxLength"/> characters (09 README D6).
/// </summary>
/// <remarks>
/// A <c>[FromHeader] string?</c> handler parameter is documented as optional and unbounded, which understates the
/// contract a generated client (10.1.4) relies on. Runtime behaviour is unchanged: a missing or malformed key is
/// still the filter's 400 <c>idempotency_key_required</c>.
/// </remarks>
public sealed class IdempotencyKeyOperationTransformer : IOpenApiOperationTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        if (operation.Parameters is null)
        {
            return Task.CompletedTask;
        }

        foreach (var parameter in operation.Parameters)
        {
            if (parameter is OpenApiParameter { In: ParameterLocation.Header } header
                && string.Equals(header.Name, RequireIdempotencyKeyFilter.HeaderName, StringComparison.OrdinalIgnoreCase))
            {
                header.Required = true;
                header.Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    MinLength = 1,
                    MaxLength = RequireIdempotencyKeyFilter.MaxLength,
                };
            }
        }

        return Task.CompletedTask;
    }
}
