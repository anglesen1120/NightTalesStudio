namespace NightTales.Api.Http;

/// <summary>
/// Endpoint metadata naming the route parameter that holds an episode id (09.9.3-T1): the request-log
/// enricher adds <c>episodeId</c> to a completion line only for an endpoint that declares it, so a series or
/// any other resource's <c>{id}</c> is never mislabelled as an episode.
/// </summary>
/// <param name="Name">The route parameter's name, e.g. <c>id</c>.</param>
public sealed record EpisodeIdRouteParameter(string Name)
{
    /// <summary>Marks the endpoint's <paramref name="name"/> route parameter as an episode id.</summary>
    /// <typeparam name="TBuilder">The endpoint convention builder type.</typeparam>
    /// <param name="builder">The endpoint or group builder.</param>
    /// <param name="name">The route parameter's name.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder Declare<TBuilder>(TBuilder builder, string name = "id")
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return builder.WithMetadata(new EpisodeIdRouteParameter(name));
    }

    /// <summary>The episode id of the request's endpoint, when it declares one and the route carries it.</summary>
    /// <param name="http">The request.</param>
    /// <returns>The route value as text, or <see langword="null"/>.</returns>
    public static string? ValueOf(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var declared = http.GetEndpoint()?.Metadata.GetMetadata<EpisodeIdRouteParameter>();

        return declared is not null && http.Request.RouteValues.TryGetValue(declared.Name, out var value)
            ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }
}
