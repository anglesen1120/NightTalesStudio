using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Shouldly;
using Xunit;

namespace NightTales.Architecture.Tests;

/// <summary>A <c>call</c>/<c>callvirt</c> site, attributed to the outermost declaring type.</summary>
/// <param name="OutermostType">Simple name of the outermost declaring type (a lambda is attributed to it).</param>
/// <param name="OutermostNamespace">Namespace of the outermost declaring type.</param>
/// <param name="Method">Name of the method whose body contains the call.</param>
/// <param name="CalledMember">Name of the called member.</param>
/// <param name="DeclaringType">Declaring type of the called member, or a marker when it is a generic specification.</param>
internal sealed record CallSite(
    string OutermostType,
    string OutermostNamespace,
    string Method,
    string CalledMember,
    string DeclaringType);

/// <summary>
/// Call-site rules that need the IL body: <c>IgnoreQueryFilters</c> (D10) and <c>AddDbContextPool</c>
/// (09.2.1). A lambda compiles to a compiler-generated nested type, so the scan attributes a hit to
/// the outermost declaring type (09.1.2-T1). Generic calls carry a <c>MethodSpec</c> token, which is
/// unwrapped before the member is read.
/// </summary>
public sealed class CallSiteRules
{
    private const ushort Call = 0x28;
    private const ushort CallVirt = 0x6F;
    private const ushort NewObj = 0x73;
    private const ushort LdFtn = 0xFE06;
    private const ushort LdVirtFtn = 0xFE07;
    private const ushort LdToken = 0xD0;
    private const string IgnoreQueryFilters = "IgnoreQueryFilters";
    private const string AddDbContextPool = "AddDbContextPool";

    /// <summary>The opcodes that name a member: the set the member-name rules scan for.</summary>
    private static readonly ushort[] MemberResolvingOpcodes = [Call, CallVirt, LdFtn, LdVirtFtn];

    /// <summary>
    /// The same set plus the two opcodes that name a <em>type</em>: <c>newobj</c> catches
    /// <c>new FileStream(...)</c> and <c>ldtoken</c> catches <c>typeof(FileInfo)</c>.
    /// </summary>
    private static readonly ushort[] AllMemberResolvingOpcodes =
        [Call, CallVirt, LdFtn, LdVirtFtn, NewObj, LdToken];

    private static readonly Dictionary<ushort, OpCode> OpCodesByValue = BuildOpCodeTable();

    [Fact]
    public void IgnoreQueryFilters_OnlyInAllowList()
    {
        var violations = new List<string>();
        foreach (var site in FindCalls(IgnoreQueryFilters))
        {
            if (!IsIgnoreQueryFiltersAllowed(site))
            {
                violations.Add(
                    $"IgnoreQueryFilters called from {site.OutermostNamespace}.{site.OutermostType}.{site.Method} "
                    + $"(target {site.DeclaringType}); allowed only under {AllowLists.IgnoreQueryFiltersNamespacePrefix} "
                    + $"or for [{string.Join(", ", AllowLists.IgnoreQueryFiltersTypeNames)}]");
            }
        }

        violations.ShouldBeEmpty(
            $"{nameof(IgnoreQueryFilters_OnlyInAllowList)}: {string.Join("; ", violations)}");
    }

    [Fact]
    public void AddDbContextPool_NeverUsed()
    {
        var violations = FindCalls(AddDbContextPool)
            .Select(site => $"{site.OutermostNamespace}.{site.OutermostType}.{site.Method} calls {site.DeclaringType}.AddDbContextPool")
            .ToList();

        violations.ShouldBeEmpty(
            $"{nameof(AddDbContextPool_NeverUsed)}: AddDbContextPool is banned (09.2.1); found {string.Join("; ", violations)}");
    }

    private static bool IsIgnoreQueryFiltersAllowed(CallSite site) =>
        site.OutermostNamespace == AllowLists.IgnoreQueryFiltersNamespacePrefix
        || site.OutermostNamespace.StartsWith(
            AllowLists.IgnoreQueryFiltersNamespacePrefix + ".", StringComparison.Ordinal)
        || AllowLists.IgnoreQueryFiltersTypeNames.Contains(
            site.OutermostNamespace.Length == 0
                ? site.OutermostType
                : site.OutermostNamespace + "." + site.OutermostType);

    private static List<CallSite> FindCalls(string memberName)
    {
        var sites = new List<CallSite>();
        foreach (var assembly in ArchitectureModel.SourceAssemblies)
        {
            CollectFromAssembly(assembly, memberName, sites);
        }

        return sites;
    }

    /// <summary>
    /// Every call site inside the type with <paramref name="typeFullName"/>, across the source
    /// assemblies — including the sites of its lambdas, which are attributed to the outermost declaring
    /// type. <c>newobj</c> and <c>ldtoken</c> are included, so a constructor call and a type token are
    /// reported as well as a method call.
    /// </summary>
    /// <param name="typeFullName">The full name of the type to scan, for example <c>NightTales.Infrastructure.Prompts.PromptCatalog</c>.</param>
    /// <returns>The call sites, in metadata order.</returns>
    internal static List<CallSite> FindCallsInType(string typeFullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeFullName);

        var sites = new List<CallSite>();
        foreach (var assembly in ArchitectureModel.SourceAssemblies)
        {
            Collect(assembly, _ => true, AllMemberResolvingOpcodes, typeFullName, sites);
        }

        return sites;
    }

    private static void CollectFromAssembly(Assembly assembly, string memberName, List<CallSite> sites) =>
        Collect(
            assembly,
            called => string.Equals(called, memberName, StringComparison.Ordinal),
            MemberResolvingOpcodes,
            null,
            sites);

    private static void Collect(
        Assembly assembly,
        Func<string, bool> memberFilter,
        ushort[] resolvingOpcodes,
        string? typeFullName,
        List<CallSite> sites)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var outermost = reader.GetTypeDefinition(OutermostDeclaringType(reader, typeHandle));
            var outermostType = reader.GetString(outermost.Name);
            var outermostNamespace = reader.GetString(outermost.Namespace);

            if (typeFullName is not null
                && !string.Equals(Join(outermostNamespace, outermostType), typeFullName, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                var relativeVirtualAddress = method.RelativeVirtualAddress;
                if (relativeVirtualAddress == 0)
                {
                    continue;
                }

                var methodName = reader.GetString(method.Name);
                var il = peReader.GetMethodBody(relativeVirtualAddress).GetILBytes();
                if (il is null)
                {
                    continue;
                }

                ScanBody(reader, il, resolvingOpcodes, (called, declaringType) =>
                {
                    if (memberFilter(called))
                    {
                        sites.Add(new CallSite(outermostType, outermostNamespace, methodName, called, declaringType));
                    }
                });
            }
        }
    }

    private static TypeDefinitionHandle OutermostDeclaringType(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var current = handle;
        while (true)
        {
            var declaring = reader.GetTypeDefinition(current).GetDeclaringType();
            if (declaring.IsNil)
            {
                return current;
            }

            current = declaring;
        }
    }

    private static void ScanBody(
        MetadataReader reader,
        byte[] il,
        ushort[] resolvingOpcodes,
        Action<string, string> onCall)
    {
        var position = 0;
        while (position < il.Length)
        {
            var first = il[position++];
            ushort value;
            if (first == 0xFE)
            {
                if (position >= il.Length)
                {
                    return;
                }

                value = (ushort)(0xFE00 | il[position++]);
            }
            else
            {
                value = first;
            }

            if (!OpCodesByValue.TryGetValue(value, out var opCode))
            {
                return;
            }

            var resolvesMember = Array.IndexOf(resolvingOpcodes, value) >= 0;
            if (resolvesMember && position + sizeof(int) <= il.Length)
            {
                var token = BitConverter.ToInt32(il, position);
                var target = ResolveCalledMember(reader, token);
                if (target is not null)
                {
                    onCall(target.Value.Name, target.Value.DeclaringType);
                }
            }

            if (opCode.OperandType == OperandType.InlineSwitch)
            {
                if (position + sizeof(int) > il.Length)
                {
                    return;
                }

                var count = BitConverter.ToInt32(il, position);
                position += sizeof(int) + (sizeof(int) * count);
            }
            else
            {
                position += OperandSize(opCode.OperandType);
            }
        }
    }

    /// <summary>
    /// Resolves the member behind a <c>call</c> token. An instantiated generic method is a
    /// <see cref="HandleKind.MethodSpecification"/>, which wraps the underlying method, and an
    /// <c>ldtoken</c> of a type resolves to the type's own full name.
    /// </summary>
    private static (string Name, string DeclaringType)? ResolveCalledMember(MetadataReader reader, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.IsNil)
        {
            return null;
        }

        if (handle.Kind == HandleKind.MethodSpecification)
        {
            handle = reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        }

        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
                {
                    var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
                    return (reader.GetString(reference.Name), DescribeDeclaringType(reader, reference.Parent));
                }

            case HandleKind.MethodDefinition:
                {
                    var definition = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                    return (reader.GetString(definition.Name), DescribeDeclaringType(reader, definition.GetDeclaringType()));
                }

            case HandleKind.TypeReference:
                {
                    var reference = reader.GetTypeReference((TypeReferenceHandle)handle);
                    var name = Join(reader.GetString(reference.Namespace), reader.GetString(reference.Name));
                    return (name, name);
                }

            case HandleKind.TypeDefinition:
                {
                    var definition = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
                    var name = Join(reader.GetString(definition.Namespace), reader.GetString(definition.Name));
                    return (name, name);
                }

            default:
                return null;
        }
    }

    private static string DescribeDeclaringType(MetadataReader reader, EntityHandle parent)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeReference:
                {
                    var reference = reader.GetTypeReference((TypeReferenceHandle)parent);
                    return Join(reader.GetString(reference.Namespace), reader.GetString(reference.Name));
                }

            case HandleKind.TypeDefinition:
                {
                    var definition = reader.GetTypeDefinition((TypeDefinitionHandle)parent);
                    return Join(reader.GetString(definition.Namespace), reader.GetString(definition.Name));
                }

            default:
                return "<generic specification>";
        }
    }

    private static string Join(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;

    private static int OperandSize(OperandType operandType) => operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineI or OperandType.ShortInlineVar or OperandType.ShortInlineBrTarget => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineMethod
            or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
            or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        _ => 4,
    };

    private static Dictionary<ushort, OpCode> BuildOpCodeTable()
    {
        var table = new Dictionary<ushort, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opCode)
            {
                table[unchecked((ushort)opCode.Value)] = opCode;
            }
        }

        return table;
    }
}
