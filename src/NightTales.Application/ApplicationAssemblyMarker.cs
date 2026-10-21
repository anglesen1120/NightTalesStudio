using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Scope = "namespaceanddescendants",
    Target = "~N:NightTales.Application.Features.Me",
    Justification = "'Me' is the feature folder 09.3.4-T1 Outputs and the 09.1.2 Features.*.(Commands|Queries).* architecture rule fix; the namespace is only used from C#.")]

namespace NightTales.Application;

public sealed class ApplicationAssemblyMarker;
