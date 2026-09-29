namespace NightTales.Application.Abstractions.Messaging;

/// <summary>
/// Opts a message out of the default-deny authorization check (09 README D4).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AllowAnonymousRequestAttribute : Attribute;
