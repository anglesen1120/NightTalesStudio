namespace NightTales.RenderNode.Server;

/// <summary>
/// The closed <c>errors[].rule</c> vocabulary of the submit validator. The frozen contract fixes the
/// envelope, the 400 code and the first rule (<c>request_invalid</c> for a body that is not JSON, has a
/// duplicate or unknown member, or misses one); the rest name the 05 D3 validation groups — key set, types and
/// ranges, fixed values — which is what makes a 400 actionable without ever quoting a caller's value.
/// </summary>
public static class SubmitRules
{
    /// <summary>The body is not usable as a <c>render-submit.v1</c> envelope (malformed, duplicate or unknown member, missing member).</summary>
    public const string RequestInvalid = "request_invalid";

    /// <summary>Group 1: a key the contract does not define (<c>mpt</c> or <c>image_plan</c>).</summary>
    public const string UnknownKey = "unknown_key";

    /// <summary>Group 1: a defined key is absent.</summary>
    public const string MissingKey = "missing_key";

    /// <summary>Group 2: the value is not of the type the field defines.</summary>
    public const string WrongType = "wrong_type";

    /// <summary>Group 2: the value is of the right type but outside its range or enumeration.</summary>
    public const string OutOfRange = "out_of_range";

    /// <summary>Group 3: the value must be the one fixed literal of 05 D3.</summary>
    public const string FixedValue = "fixed_value";
}
