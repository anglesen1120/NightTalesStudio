using System.Collections.Immutable;
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
/// Call-site rules that need the IL body: <c>IgnoreQueryFilters</c> (D10), <c>AddDbContextPool</c>
/// (09.2.1) and <c>new HttpClient</c> (11 README F13). A lambda compiles to a compiler-generated nested
/// type, so the scan attributes a hit to the outermost declaring type (09.1.2-T1). Generic calls carry a
/// <c>MethodSpec</c> token, which is unwrapped before the member is read.
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
    private const string Constructor = ".ctor";
    private const string HttpClientType = "System.Net.Http.HttpClient";

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

    /// <summary>
    /// A <c>new HttpClient(...)</c> may be constructed only by the approved producers (11 README F13/D13):
    /// the pinned-TLS render-node connection factory and the pre-existing breached-password range checker.
    /// A node client built anywhere else would skip the pin, redirect, proxy and timeout rules the factory
    /// owns, so this stays an allow-list rather than a style rule.
    /// </summary>
    [Fact]
    public void HttpClient_IsOnlyConstructedByTheApprovedProducers()
    {
        var sites = FindNewObjects(HttpClientType);

        // Non-vacuity: the factory must actually construct the client, or a scanner regression would let
        // the rule pass while guarding nothing (11 README F13 names the factory as the producer).
        sites.ShouldContain(
            site => FullName(site) == "NightTales.Infrastructure.Rendering.RenderNodeConnectionFactory",
            "the pinned-TLS render-node connection factory is the HttpClient producer of 11 README F13");

        var violations = sites
            .Where(site => !AllowLists.HttpClientProducerTypeNames.Contains(FullName(site)))
            .Select(site => $"{FullName(site)}.{site.Method} constructs a new {HttpClientType}")
            .ToList();

        violations.ShouldBeEmpty(
            $"{nameof(HttpClient_IsOnlyConstructedByTheApprovedProducers)}: only "
            + $"{string.Join(", ", AllowLists.HttpClientProducerTypeNames)} may construct a new {HttpClientType} "
            + $"(11 README F13); found {string.Join("; ", violations)}");
    }

    /// <summary>
    /// <c>story_versions</c> is append-only (02.2.2 plan §3.2, FR-02-3): no call site outside
    /// <c>Features/Admin</c> (09.9.1) may <c>Update</c>, <c>Attach</c>, <c>Remove</c> or set-update/delete a
    /// <c>StoryVersion</c>, nothing outside it may assign <c>EntityEntry.State</c> at all (the way a row
    /// would be forced to <c>Modified</c>/<c>Deleted</c>), and the untyped paths that name no entity —
    /// <c>DbContext.Update/Attach/Remove</c>, <c>PropertyEntry.CurrentValue</c>, <c>PropertyValues.SetValues</c> —
    /// are banned outside it too, except for the explicit
    /// <see cref="AllowLists.StoryVersionUntypedMutationTypeNames"/>. The persistence model backs this at
    /// runtime with <c>AfterSaveBehavior.Throw</c> on every property.
    /// </summary>
    /// <remarks>
    /// The scan sees the generic instantiation (<c>DbSet&lt;StoryVersion&gt;.Update</c>,
    /// <c>ExecuteDelete&lt;StoryVersion&gt;</c>); a call through the non-generic <c>Update(object)</c> overload
    /// names no entity, which is why the <c>State</c> setter and the untyped calls are banned outright instead.
    /// </remarks>
    [Fact]
    public void StoryVersion_NoUpdatePathOutsideAdmin()
    {
        const string storyVersion = "NightTales.Domain.Stories.StoryVersion";
        string[] mutating =
        [
            "Update", "UpdateRange", "Remove", "RemoveRange", "Attach", "AttachRange",
            "ExecuteUpdate", "ExecuteUpdateAsync", "ExecuteDelete", "ExecuteDeleteAsync",
        ];

        var sites = new List<CallSite>();
        foreach (var assembly in ArchitectureModel.SourceAssemblies)
        {
            Collect(
                assembly,
                called => called is "set_State" or "Add" or "set_CurrentValue" or "SetValues"
                    || Array.IndexOf(mutating, called) >= 0,
                MemberResolvingOpcodes,
                null,
                sites);
        }

        // Non-vacuity: the scanner must resolve generic EF types at all, or a decoder regression would let the
        // rule pass while guarding nothing. The handlers add entities to `DbSet<T>` sets.
        sites.ShouldContain(
            site => site.CalledMember == "Add"
                && site.DeclaringType.StartsWith("Microsoft.EntityFrameworkCore.DbSet`1<NightTales.Domain.", StringComparison.Ordinal),
            "the IL scan must name the entity of a DbSet<T> call");

        // Non-vacuity of the untyped checks: the audit interceptor really assigns PropertyEntry.CurrentValue.
        sites.ShouldContain(site => IsPropertyEntryValueSetter(site), "the IL scan must see PropertyEntry.CurrentValue assignments");

        var violations = sites
            .Where(site => site.CalledMember != "Add")
            .Where(site => IsEntityEntryStateSetter(site)
                || site.DeclaringType.Contains(storyVersion, StringComparison.Ordinal))
            .Where(site => !IsInNamespace(site.OutermostNamespace, AllowLists.StoryVersionMutationNamespacePrefix))
            .Select(site => $"{FullName(site)}.{site.Method} calls {site.DeclaringType}.{site.CalledMember}")
            .ToList();

        violations.AddRange(sites
            .Where(site => IsUntypedMutation(site, mutating))
            .Where(site => !IsInNamespace(site.OutermostNamespace, AllowLists.StoryVersionMutationNamespacePrefix)
                && !AllowLists.StoryVersionUntypedMutationTypeNames.Contains(FullName(site)))
            .Select(site => $"{FullName(site)}.{site.Method} calls {site.DeclaringType}.{site.CalledMember} (untyped)"));

        violations.ShouldBeEmpty(
            $"{nameof(StoryVersion_NoUpdatePathOutsideAdmin)}: story_versions is append-only; only "
            + $"{AllowLists.StoryVersionMutationNamespacePrefix} (and {string.Join(", ", AllowLists.StoryVersionUntypedMutationTypeNames)} "
            + "for the untyped paths) may mutate it (02.2.2 plan §3.2); found "
            + string.Join("; ", violations));
    }

    /// <summary>
    /// Whether the site is an untyped EF write that names no entity: the non-generic <c>DbContext.Update/Attach/Remove</c>
    /// family, <c>PropertyEntry.CurrentValue</c> or <c>PropertyValues.SetValues</c>.
    /// </summary>
    private static bool IsUntypedMutation(CallSite site, string[] mutating) =>
        (site.DeclaringType == "Microsoft.EntityFrameworkCore.DbContext"
            && Array.IndexOf(mutating, site.CalledMember) >= 0
            && !site.CalledMember.StartsWith("Execute", StringComparison.Ordinal))
        || IsPropertyEntryValueSetter(site)
        || (site.CalledMember == "SetValues"
            && site.DeclaringType.StartsWith("Microsoft.EntityFrameworkCore.ChangeTracking.PropertyValues", StringComparison.Ordinal));

    /// <summary>EF declares <c>CurrentValue</c> on <c>MemberEntry</c> and overrides it on <c>PropertyEntry</c>; either binding writes a value.</summary>
    private static bool IsPropertyEntryValueSetter(CallSite site) =>
        site.CalledMember == "set_CurrentValue"
        && (site.DeclaringType.StartsWith("Microsoft.EntityFrameworkCore.ChangeTracking.PropertyEntry", StringComparison.Ordinal)
            || site.DeclaringType.StartsWith("Microsoft.EntityFrameworkCore.ChangeTracking.MemberEntry", StringComparison.Ordinal));

    /// <summary>
    /// Whether the site assigns EF's <c>EntityEntry.State</c> (declared on <c>EntityEntry</c> or one of its generic
    /// forms) — not an unrelated type's own <c>State</c> property.
    /// </summary>
    private static bool IsEntityEntryStateSetter(CallSite site) =>
        site.CalledMember == "set_State"
        && site.DeclaringType.StartsWith("Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry", StringComparison.Ordinal);

    private static bool IsIgnoreQueryFiltersAllowed(CallSite site) =>
        IsInNamespace(site.OutermostNamespace, AllowLists.IgnoreQueryFiltersNamespacePrefix)
        || AllowLists.IgnoreQueryFiltersTypeNames.Contains(FullName(site));

    /// <summary>Exact-or-dot-boundary namespace match: <c>Features.Admin</c> and <c>Features.Admin.X</c>, never <c>Features.AdminTools</c>.</summary>
    private static bool IsInNamespace(string ns, string prefix) =>
        ns == prefix || ns.StartsWith(prefix + ".", StringComparison.Ordinal);

    /// <summary>The site's declaring type as <c>namespace.Type</c>, the form the allow-lists match.</summary>
    private static string FullName(CallSite site) => Join(site.OutermostNamespace, site.OutermostType);

    /// <summary>
    /// Every <c>newobj</c> site that constructs an instance of <paramref name="typeFullName"/>, across the
    /// source assemblies. Only <c>newobj</c> is scanned: a <c>call</c> to a constructor (for example from a
    /// derived type's initialiser) is not a construction and is deliberately out of scope.
    /// </summary>
    private static List<CallSite> FindNewObjects(string typeFullName)
    {
        var sites = new List<CallSite>();
        foreach (var assembly in ArchitectureModel.SourceAssemblies)
        {
            Collect(
                assembly,
                called => string.Equals(called, Constructor, StringComparison.Ordinal),
                [NewObj],
                null,
                sites);
        }

        return [.. sites.Where(site => string.Equals(site.DeclaringType, typeFullName, StringComparison.Ordinal))];
    }

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

        // The type arguments of an instantiated generic method (`ExecuteDelete<StoryVersion>`) are appended to
        // the declaring type as `[arg,…]`, so a rule can tell which entity a generic EF call targets.
        var instantiation = string.Empty;
        if (handle.Kind == HandleKind.MethodSpecification)
        {
            var specification = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
            instantiation = "[" + string.Join(",", specification.DecodeSignature(new SignatureNames(), null)) + "]";
            handle = specification.Method;
        }

        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
                {
                    var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
                    return (reader.GetString(reference.Name), DescribeDeclaringType(reader, reference.Parent) + instantiation);
                }
            case HandleKind.MethodDefinition:
                {
                    var definition = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                    return (
                        reader.GetString(definition.Name),
                        DescribeDeclaringType(reader, definition.GetDeclaringType()) + instantiation);
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

            case HandleKind.TypeSpecification:
                return reader.GetTypeSpecification((TypeSpecificationHandle)parent)
                    .DecodeSignature(new SignatureNames(), null);

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

    /// <summary>
    /// Renders a signature blob as readable names (<c>Microsoft.EntityFrameworkCore.DbSet`1&lt;NightTales.Domain.Stories.StoryVersion&gt;</c>)
    /// so a rule can see which entity a generic EF call targets.
    /// </summary>
    private sealed class SignatureNames : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetTypeFromDefinition(MetadataReader metadataReader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var definition = metadataReader.GetTypeDefinition(handle);
            return Join(metadataReader.GetString(definition.Namespace), metadataReader.GetString(definition.Name));
        }

        public string GetTypeFromReference(MetadataReader metadataReader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var reference = metadataReader.GetTypeReference(handle);
            return Join(metadataReader.GetString(reference.Namespace), metadataReader.GetString(reference.Name));
        }

        public string GetTypeFromSpecification(
            MetadataReader metadataReader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) =>
            metadataReader.GetTypeSpecification(handle).DecodeSignature(new SignatureNames(), genericContext);

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            genericType + "<" + string.Join(",", typeArguments) + ">";

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPinnedType(string elementType) => elementType;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetFunctionPointerType(MethodSignature<string> signature) => "<function pointer>";

        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    }
}
