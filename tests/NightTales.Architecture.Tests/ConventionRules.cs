using System.Reflection;
using ArchUnitNET.xUnitV3;
using Shouldly;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace NightTales.Architecture.Tests;

/// <summary>
/// Convention rules: handler placement and sealing (D4), the file triple, <c>[OutboxEvent]</c> (D11),
/// ids-only jobs (D12), <c>IUserOwned</c> on episode/series children (D10) and thin endpoints.
/// Marker types are matched by name, so a rule whose marker does not exist yet passes.
/// </summary>
public sealed class ConventionRules
{
    private const string CommandHandlerInterface = "Mediator.ICommandHandler`2";
    private const string QueryHandlerInterface = "Mediator.IQueryHandler`2";
    private const string CommandInterface = "Mediator.ICommand`1";
    private const string QueryInterface = "Mediator.IQuery`1";
    private const string ValidatorBase = "FluentValidation.AbstractValidator`1";
    private const string BackgroundJobInterface = "IBackgroundJob";
    private const string DomainEventInterface = "IDomainEvent";
    private const string UserOwnedInterface = "IUserOwned";
    private const string EntityBase = "Entity";
    private const string OutboxEventAttributeName = "OutboxEventAttribute";
    private const string DomainNamespacePrefix = "NightTales.Domain";
    private const string ApplicationNamespacePrefix = "NightTales.Application";
    private const string FeatureNamespacePrefix = "NightTales.Application.Features.";

    [Fact]
    public void Handlers_AreSealed_AndLiveInFeatureFolders()
    {
        var violations = new List<string>();
        foreach (var type in ApplicationTypes())
        {
            if (!IsHandler(type))
            {
                continue;
            }

            if (!type.IsSealed)
            {
                violations.Add($"{type.FullName} implements a Mediator handler interface but is not sealed");
            }

            var ns = type.Namespace ?? string.Empty;
            if (!IsFeatureRequestFolder(ns))
            {
                violations.Add(
                    $"{type.FullName} lives in namespace '{ns}', which does not match "
                    + "NightTales.Application.Features.*.(Commands|Queries).*");
            }
        }

        violations.ShouldBeEmpty(
            $"{nameof(Handlers_AreSealed_AndLiveInFeatureFolders)}: {string.Join("; ", violations)}");
    }

    [Fact]
    public void EachRequestFolder_HasOneRequestAndOneHandler()
    {
        var violations = new List<string>();
        var folders = ApplicationTypes()
            .Where(type => type.Namespace is not null && IsFeatureRequestFolder(type.Namespace))
            .GroupBy(type => type.Namespace!, StringComparer.Ordinal);

        foreach (var folder in folders)
        {
            var requests = folder.Count(IsRequest);
            var handlers = folder.Count(IsHandler);
            var validators = folder.Count(IsValidator);

            if (requests != 1)
            {
                violations.Add($"'{folder.Key}' has {requests} request types (ICommand<>/IQuery<>); exactly one is required");
            }

            if (handlers != 1)
            {
                violations.Add($"'{folder.Key}' has {handlers} handler types; exactly one is required");
            }

            if (validators > 1)
            {
                violations.Add($"'{folder.Key}' has {validators} AbstractValidator<> types; at most one is allowed");
            }
        }

        violations.ShouldBeEmpty(
            $"{nameof(EachRequestFolder_HasOneRequestAndOneHandler)}: {string.Join("; ", violations)}");
    }

    [Fact]
    public void DomainEvents_HaveOutboxEventAttribute()
    {
        var violations = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var type in AllSourceTypes())
        {
            if (!ImplementsNamed(type, DomainEventInterface, DomainNamespacePrefix))
            {
                continue;
            }

            var name = ReadOutboxEventName(type);
            if (name is null)
            {
                violations.Add($"{type.FullName} implements IDomainEvent but has no [OutboxEvent(\"<name>.v<n>\")] attribute");
                continue;
            }

            if (!IsValidOutboxEventName(name))
            {
                violations.Add($"{type.FullName} has [OutboxEvent(\"{name}\")], which does not match ^[a-z_]+\\.v[0-9]+$");
            }

            if (names.TryGetValue(name, out var other))
            {
                violations.Add($"[OutboxEvent] name '{name}' is used by both {other} and {type.FullName}");
            }
            else
            {
                names[name] = type.FullName ?? type.Name;
            }
        }

        violations.ShouldBeEmpty(
            $"{nameof(DomainEvents_HaveOutboxEventAttribute)}: {string.Join("; ", violations)}");
    }

    [Fact]
    public void Jobs_TakeIdsOnly()
    {
        var violations = new List<string>();
        foreach (var type in AllSourceTypes())
        {
            if (type.IsInterface || !ImplementsNamed(type, BackgroundJobInterface, ApplicationNamespacePrefix))
            {
                continue;
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName)
                {
                    continue;
                }

                foreach (var parameter in method.GetParameters())
                {
                    if (!IsAllowedJobParameter(parameter))
                    {
                        violations.Add(
                            $"{type.FullName}.{method.Name} takes parameter '{parameter.ParameterType.Name} {parameter.Name}'; "
                            + "job methods may take only Guid, int, long, enums, DateTimeOffset, CancellationToken "
                            + "or a string named on the allow-list");
                    }
                }
            }
        }

        violations.ShouldBeEmpty($"{nameof(Jobs_TakeIdsOnly)}: {string.Join("; ", violations)}");
    }

    [Fact]
    public void EpisodeOrSeriesChildren_AreUserOwned()
    {
        var violations = new List<string>();
        foreach (var type in AllSourceTypes())
        {
            if (type.IsInterface || !DerivesFromEntity(type))
            {
                continue;
            }

            var hasChildId = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(property => property.PropertyType == typeof(Guid)
                    && (property.Name == "EpisodeId" || property.Name == "SeriesId"));

            if (hasChildId && !ImplementsNamed(type, UserOwnedInterface, DomainNamespacePrefix))
            {
                violations.Add($"{type.FullName} has a Guid EpisodeId/SeriesId property but does not implement IUserOwned");
            }
        }

        violations.ShouldBeEmpty(
            $"{nameof(EpisodeOrSeriesChildren_AreUserOwned)}: {string.Join("; ", violations)}");
    }

    [Fact]
    public void Endpoints_DoNotReferenceDbContexts()
    {
        var rule = Types()
            .That().ResideInNamespaceMatching(@"NightTales\.Api\.Endpoints.*")
            .Should().NotDependOnAnyTypesThat().HaveNameMatching("^IAppDbContext$|^IReadDb$")
            .WithoutRequiringPositiveResults();

        ArchRuleAssert.CheckRule(ArchitectureModel.Architecture, rule);
    }

    private static IEnumerable<Type> AllSourceTypes() =>
        ArchitectureModel.SourceAssemblies.SelectMany(SafeGetTypes);

    private static IEnumerable<Type> ApplicationTypes() =>
        SafeGetTypes(ArchitectureModel.Require(ArchitectureModel.Application));

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null).Cast<Type>();
        }
    }

    private static bool IsHandler(Type type) =>
        ImplementsExact(type, CommandHandlerInterface) || ImplementsExact(type, QueryHandlerInterface);

    private static bool IsRequest(Type type) =>
        ImplementsExact(type, CommandInterface) || ImplementsExact(type, QueryInterface);

    private static bool IsValidator(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType
                && string.Equals(current.GetGenericTypeDefinition().FullName, ValidatorBase, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool DerivesFromEntity(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.Name, EntityBase, StringComparison.Ordinal)
                && (current.Namespace ?? string.Empty).StartsWith(DomainNamespacePrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ImplementsExact(Type type, string interfaceFullName) =>
        type.GetInterfaces().Any(
            implemented => string.Equals(
                implemented.IsGenericType ? implemented.GetGenericTypeDefinition().FullName : implemented.FullName,
                interfaceFullName,
                StringComparison.Ordinal));

    private static bool ImplementsNamed(Type type, string interfaceName, string namespacePrefix) =>
        type.GetInterfaces().Any(
            implemented => string.Equals(implemented.Name, interfaceName, StringComparison.Ordinal)
                && (implemented.Namespace ?? string.Empty).StartsWith(namespacePrefix, StringComparison.Ordinal));

    private static bool IsFeatureRequestFolder(string ns)
    {
        if (!ns.StartsWith(FeatureNamespacePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var segments = ns.Split('.');
        return segments.Contains("Commands") || segments.Contains("Queries");
    }

    private static bool IsAllowedJobParameter(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(Guid)
            || type == typeof(int)
            || type == typeof(long)
            || type == typeof(DateTimeOffset)
            || type == typeof(CancellationToken))
        {
            return true;
        }

        if (type.IsEnum)
        {
            return true;
        }

        return type == typeof(string)
            && parameter.Name is not null
            && AllowLists.JobStringParameterNames.Contains(parameter.Name);
    }

    private static string? ReadOutboxEventName(Type type)
    {
        foreach (var attribute in type.GetCustomAttributesData())
        {
            if (!string.Equals(attribute.AttributeType.Name, OutboxEventAttributeName, StringComparison.Ordinal))
            {
                continue;
            }

            if (attribute.ConstructorArguments.Count > 0
                && attribute.ConstructorArguments[0].Value is string fromConstructor)
            {
                return fromConstructor;
            }

            foreach (var named in attribute.NamedArguments)
            {
                if (string.Equals(named.MemberName, "Name", StringComparison.Ordinal)
                    && named.TypedValue.Value is string fromProperty)
                {
                    return fromProperty;
                }
            }

            return null;
        }

        return null;
    }

    private static bool IsValidOutboxEventName(string name)
    {
        var separator = name.IndexOf(".v", StringComparison.Ordinal);
        if (separator <= 0 || separator != name.LastIndexOf(".v", StringComparison.Ordinal))
        {
            return false;
        }

        var prefix = name[..separator];
        var version = name[(separator + 2)..];
        return prefix.All(character => (character >= 'a' && character <= 'z') || character == '_')
            && version.Length > 0
            && version.All(character => character >= '0' && character <= '9');
    }
}
