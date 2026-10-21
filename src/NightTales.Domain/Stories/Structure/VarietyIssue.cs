#pragma warning disable CA1716 // `Structure` is the planned namespace of the variety guard (02.8.4-T1 plan); the .NET-only API is never consumed from Visual Basic.
namespace NightTales.Domain.Stories.Structure;

/// <summary>
/// One mismatch between a written story's structure and the plan it was written under (02.8.4-T1,
/// <see cref="VarietyGuard.VerifyWritten"/>): the closed rule id, the structure dimension and the value at fault.
/// </summary>
/// <remarks>
/// <see cref="Rule"/> is <see cref="VarietyGuard.IssueForbiddenUsed"/> (the 02.8.2 gate fails on it) or
/// <see cref="VarietyGuard.IssueRequiredMissing"/> (advisory: the writer maps free strings to vocabulary terms and
/// may land on <c>other:&lt;text&gt;</c>). <see cref="Dimension"/> is one of the four structure dimensions, never
/// <c>narrator_mode</c>. <see cref="Detail"/> names the tag value; a caller that logs records counts and codes only
/// (D19/D13) and never the detail verbatim.
/// </remarks>
/// <param name="Rule">The closed rule id: <see cref="VarietyGuard.IssueForbiddenUsed"/> or <see cref="VarietyGuard.IssueRequiredMissing"/>.</param>
/// <param name="Dimension">The structure dimension the issue belongs to.</param>
/// <param name="Detail">The tag value at fault (the written value for a forbidden hit, the required value when it is missing).</param>
public sealed record VarietyIssue(string Rule, string Dimension, string Detail);
