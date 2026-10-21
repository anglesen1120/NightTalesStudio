namespace NightTales.RenderNode.Server;

/// <summary>
/// The <c>errors[].field</c> rule of 05.4.1-T2 §1 (§10 T69): a caller key name is echoed only when it matches
/// <c>^[a-z_]{1,64}$</c>; anything else becomes <c>&lt;redacted&gt;</c> with the parent field kept, so a
/// misnamed 2 MiB key can never be echoed back and no request can be reflected into a response.
/// </summary>
internal static class AdapterFieldNames
{
    /// <summary>The replacement for a key name that must not be echoed.</summary>
    internal const string Redacted = "<redacted>";

    /// <summary>The field a body-level violation is reported against (there is no caller key).</summary>
    internal const string Body = "body";

    /// <summary>Builds <c>parent.key</c>, echoing <paramref name="key"/> only when it is echoable.</summary>
    internal static string For(string parent, string key) =>
        string.Concat(parent, ".", IsEchoable(key) ? key : Redacted);

    /// <summary>
    /// Maps a <c>System.Text.Json</c> exception path to a field: the last path segment without an array index,
    /// attributed to <paramref name="parent"/>. A path with no usable segment is the parent field itself.
    /// </summary>
    internal static string FromJsonPath(string? path, string parent)
    {
        if (string.IsNullOrEmpty(path))
        {
            return parent;
        }

        var tail = path.StartsWith('$') ? path[1..] : path;
        if (tail.StartsWith('.'))
        {
            tail = tail[1..];
        }

        var bracket = tail.IndexOf('[');
        if (bracket >= 0)
        {
            tail = tail[..bracket];
        }

        var dot = tail.LastIndexOf('.');
        if (dot >= 0)
        {
            tail = tail[(dot + 1)..];
        }

        return tail.Length == 0 ? parent : For(parent, tail);
    }

    /// <summary>The <c>^[a-z_]{1,64}$</c> test, without a regex so a hostile key name cannot cost a backtrack.</summary>
    internal static bool IsEchoable(string name)
    {
        if (name.Length is < 1 or > 64)
        {
            return false;
        }

        foreach (var character in name)
        {
            if (character is not ((>= 'a' and <= 'z') or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
