using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace NightTales.Api.Http;

/// <summary>
/// Completes the generated <c>ProblemDetails</c> component with the two members of the 09 README D7
/// error contract that its CLR type cannot express: <c>code</c> and <c>traceId</c> (09.9.5-T1).
/// </summary>
/// <remarks>
/// <para>
/// Every error response of the Api is written by <see cref="ProblemDetailsWriter"/> as the
/// <c>ProblemBody</c> shape — <c>type</c>, <c>title</c>, <c>status</c>, <c>code</c> and
/// <c>traceId</c> — but the document's <c>ProblemDetails</c> component is generated from
/// <c>Microsoft.AspNetCore.Mvc.ProblemDetails</c>, whose members stop at <c>detail</c>/<c>instance</c>.
/// Without this transformer the published contract would understate the wire format: a client
/// generated from the document (10.1.4) would have no typed access to the stable error code and the
/// trace id, which are the two members a caller acts on.
/// </para>
/// <para>
/// The transformer runs when the schema is registered and before the framework's final pass moves it
/// into <c>components.schemas</c>, so the added members travel with the component and every
/// <c>$ref</c> to it (see the "Customize schema reuse" note of the OpenAPI documentation).
/// </para>
/// </remarks>
public sealed class ProblemDetailsSchemaTransformer : IOpenApiSchemaTransformer
{
    /// <summary>The component name of the error body — also the CLR type name behind it.</summary>
    public const string ComponentName = "ProblemDetails";

    /// <summary>
    /// The D7 members the CLR type does not declare. Both are always present in a body this Api
    /// writes, so both are added to <c>required</c>.
    /// </summary>
    private static readonly string[] WireOnlyMembers = ["code", "traceId"];

    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        if (context.JsonTypeInfo.Type.Name != ComponentName)
        {
            return Task.CompletedTask;
        }

        schema.Properties ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        schema.Required ??= new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in WireOnlyMembers)
        {
            schema.Properties[member] = new OpenApiSchema { Type = JsonSchemaType.String };
            schema.Required.Add(member);
        }

        return Task.CompletedTask;
    }
}
