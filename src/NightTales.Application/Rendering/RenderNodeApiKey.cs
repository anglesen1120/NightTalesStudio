using System.Text.Json;

namespace NightTales.Application.Rendering;

/// <summary>
/// The secret-manager variable of one node's adapter key (11 README D13/D11):
/// <c>RENDER_NODE_&lt;NAME&gt;_API_KEY</c>, where <c>&lt;NAME&gt;</c> is the upper-snake form of the node
/// name.
/// </summary>
/// <remarks>
/// <para>
/// The name is derived in exactly one place because three callers must agree on it: the seeder verifies the
/// variable exists at start-up (exit 2 when it does not), the connection factory reads it into the
/// <c>x-api-key</c> header, and a test asserts the derivation (<c>mock</c> → <c>RENDER_NODE_MOCK_API_KEY</c>).
/// </para>
/// <para>
/// Only the variable's <em>name</em> is ever reported. The value never reaches a log, an exception message
/// or <c>audit_logs.data</c> (11 README S4).
/// </para>
/// </remarks>
public static class RenderNodeApiKey
{
    /// <summary>The prefix every node key variable shares (D13).</summary>
    public const string VariablePrefix = "RENDER_NODE_";

    /// <summary>The suffix every node key variable shares (D13).</summary>
    public const string VariableSuffix = "_API_KEY";

    /// <summary>
    /// The variable name of <paramref name="nodeName"/>: <c>RENDER_NODE_</c> + upper-snake of the name +
    /// <c>_API_KEY</c>.
    /// </summary>
    /// <param name="nodeName">The node's name as <c>worker_nodes.name</c> stores it.</param>
    public static string VariableName(string nodeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);

        return VariablePrefix + JsonNamingPolicy.SnakeCaseUpper.ConvertName(nodeName.Trim()) + VariableSuffix;
    }
}
