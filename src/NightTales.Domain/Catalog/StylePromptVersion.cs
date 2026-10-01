namespace NightTales.Domain.Catalog;

/// <summary>
/// The attribution string of one template revision (03 README D1/F16), stored in
/// <c>render_jobs.params.style_prompt_version</c>: <c>style=&lt;name&gt;@&lt;8 hex&gt;;neg=v1</c>. A
/// template edit changes the hash, so a render can always be traced back to the text and clause that
/// produced it.
/// </summary>
public static class StylePromptVersion
{
    /// <summary>The version string of <paramref name="style"/>.</summary>
    /// <param name="style">The catalog template; only its name and text are read.</param>
    public static string For(StyleTemplate style)
    {
        ArgumentNullException.ThrowIfNull(style);

        return $"style={style.Name}@{ScenePromptBuilder.Sha256Hex(style.Template)[..8]};neg={NegativeClause.Version}";
    }
}
