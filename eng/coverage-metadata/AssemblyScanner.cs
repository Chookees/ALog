using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace CoverageMetadata;

/// <summary>
/// Values shared by one assembly scan.
/// Grouped so each collector stays within five parameters.
/// </summary>
internal sealed class ScanBag
{
    /// <summary>Gets the reader for the assembly being scanned. The caller owns its lifetime.</summary>
    internal required MetadataReader Reader { get; init; }

    /// <summary>Gets the simple assembly name already checked for length.</summary>
    internal required string AssemblyName { get; init; }

    /// <summary>Gets the signature decoder bound to <see cref="Reader"/>.</summary>
    internal required TypeNameProvider Names { get; init; }

    /// <summary>Gets the lines collected so far. Collectors append to this list.</summary>
    internal required List<string> Lines { get; init; }
}

/// <summary>
/// Lists members that carry a coverage-exclusion attribute.
/// BB-09 tracks two attributes, resolved from the constructor's declaring type:
/// <c>System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute</c> and
/// <c>System.CodeDom.Compiler.GeneratedCodeAttribute</c>.
/// Compiler-generated markers are not tracked. The source token scan is the only check for those.
/// The next file to read is <c>eng/coverage-exception-check.py</c>, which diffs two of these lists.
/// </summary>
internal static class AssemblyScanner
{
    // ALog's two assemblies are far below this. A larger table is treated as corrupt input.
    private const int MaxTypes = 100000;
    private const int MaxMembers = 100000;
    private const int MaxAttributes = 64;
    private const int MaxParameters = 32;
    private const int MaxOutput = 100000;
    private const int MaxAssemblyBytes = 30_000_000;

    /// <summary>
    /// Reads one assembly and returns one line per tracked attribute.
    /// </summary>
    /// <param name="path">Path to a PE assembly. Must exist and be at most 30 MB.</param>
    /// <returns>
    /// Lines of <c>assembly\tkind\tmember\tattribute</c>.
    /// Kinds are <c>type</c>, <c>method</c>, <c>property</c>, <c>event</c>, and <c>field</c>.
    /// An assembly with no tracked attributes returns an empty list.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the file is missing, too large, not a PE image, or its metadata cannot be named.
    /// </exception>
    /// <remarks>
    /// Opens the file, reads it, and closes it before returning. Does not write.
    /// The file must not be replaced while it is open.
    /// </remarks>
    internal static List<string> Scan(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("assembly path is empty.", nameof(path));
        }

        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > MaxAssemblyBytes)
        {
            throw new InvalidOperationException("assembly missing, empty, or larger than 30 MB: " + path);
        }

        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
        {
            throw new InvalidOperationException("assembly has no CLI metadata: " + path);
        }

        var reader = pe.GetMetadataReader();
        var assemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);
        if (assemblyName.Length == 0 || assemblyName.Length > 500)
        {
            throw new InvalidOperationException("assembly name length is out of range.");
        }

        var bag = new ScanBag
        {
            Reader = reader,
            AssemblyName = assemblyName,
            Names = new TypeNameProvider(reader),
            Lines = new List<string>(),
        };
        CollectTypes(bag);
        return bag.Lines;
    }

    /// <summary>
    /// Walks every type and the members that can carry an exclusion attribute.
    /// </summary>
    /// <param name="bag">Open scan. Must not be null.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="bag"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the type table exceeds 100000.</exception>
    /// <remarks>Reads metadata only. Nested types are their own rows, so this loop does not recurse.</remarks>
    private static void CollectTypes(ScanBag bag)
    {
        ArgumentNullException.ThrowIfNull(bag);
        var types = 0;
        foreach (var handle in bag.Reader.TypeDefinitions)
        {
            types++;
            if (types > MaxTypes)
            {
                throw new InvalidOperationException("type table exceeds 100000.");
            }

            var type = bag.Reader.GetTypeDefinition(handle);
            var typeName = TypeNames.DefinitionName(bag.Reader, handle);
            CollectAttributes(bag, type.GetCustomAttributes(), "type", typeName);
            CollectMethods(bag, type, typeName);
            CollectProperties(bag, type, typeName);
            CollectEvents(bag, type, typeName);
            CollectFields(bag, type, typeName);
        }
    }

    /// <summary>
    /// Lists tracked attributes on the methods of one type, including constructors.
    /// </summary>
    /// <param name="bag">Open scan.</param>
    /// <param name="type">Type whose methods are visited.</param>
    /// <param name="typeName">Display name of <paramref name="type"/>.</param>
    /// <exception cref="InvalidOperationException">Thrown when the method table or a signature is too large.</exception>
    /// <remarks>The member id ends with the parameter type list so overloads stay distinct.</remarks>
    private static void CollectMethods(ScanBag bag, TypeDefinition type, string typeName)
    {
        var seen = 0;
        foreach (var handle in type.GetMethods())
        {
            seen++;
            if (seen > MaxMembers)
            {
                throw new InvalidOperationException(typeName + " has more than 100000 methods.");
            }

            var method = bag.Reader.GetMethodDefinition(handle);
            var member = typeName + "." + MethodSignature(bag, method);
            CollectAttributes(bag, method.GetCustomAttributes(), "method", member);
        }
    }

    /// <summary>
    /// Lists tracked attributes on the properties of one type.
    /// </summary>
    /// <param name="bag">Open scan.</param>
    /// <param name="type">Type whose properties are visited.</param>
    /// <param name="typeName">Display name of the declaring type.</param>
    /// <exception cref="InvalidOperationException">Thrown when the property table exceeds 100000.</exception>
    /// <remarks>The attribute is read from the property row, not from its getter or setter.</remarks>
    private static void CollectProperties(ScanBag bag, TypeDefinition type, string typeName)
    {
        var seen = 0;
        foreach (var handle in type.GetProperties())
        {
            seen++;
            if (seen > MaxMembers)
            {
                throw new InvalidOperationException(typeName + " has more than 100000 properties.");
            }

            var property = bag.Reader.GetPropertyDefinition(handle);
            var member = typeName + "." + PropertySignature(bag, property);
            CollectAttributes(bag, property.GetCustomAttributes(), "property", member);
        }
    }

    /// <summary>
    /// Lists tracked attributes on the events of one type.
    /// </summary>
    /// <param name="bag">Open scan.</param>
    /// <param name="type">Type whose events are visited.</param>
    /// <param name="typeName">Display name of the declaring type. Empty is rejected.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="typeName"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the event table exceeds 100000.</exception>
    /// <remarks>The event name does not include the handler type.</remarks>
    private static void CollectEvents(ScanBag bag, TypeDefinition type, string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            throw new ArgumentException("type name is empty.", nameof(typeName));
        }

        var seen = 0;
        foreach (var handle in type.GetEvents())
        {
            seen++;
            if (seen > MaxMembers)
            {
                throw new InvalidOperationException(typeName + " has more than 100000 events.");
            }

            var ev = bag.Reader.GetEventDefinition(handle);
            var member = typeName + "." + RequireIdentifier(bag.Reader.GetString(ev.Name));
            CollectAttributes(bag, ev.GetCustomAttributes(), "event", member);
        }
    }

    /// <summary>
    /// Lists tracked attributes on the fields of one type.
    /// </summary>
    /// <param name="bag">Open scan.</param>
    /// <param name="type">Type whose fields are visited.</param>
    /// <param name="typeName">Display name of the declaring type. Empty is rejected.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="typeName"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the field table exceeds 100000.</exception>
    /// <remarks>A backing field is listed only when it itself carries a tracked attribute.</remarks>
    private static void CollectFields(ScanBag bag, TypeDefinition type, string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            throw new ArgumentException("type name is empty.", nameof(typeName));
        }

        var seen = 0;
        foreach (var handle in type.GetFields())
        {
            seen++;
            if (seen > MaxMembers)
            {
                throw new InvalidOperationException(typeName + " has more than 100000 fields.");
            }

            var field = bag.Reader.GetFieldDefinition(handle);
            var member = typeName + "." + RequireIdentifier(bag.Reader.GetString(field.Name));
            CollectAttributes(bag, field.GetCustomAttributes(), "field", member);
        }
    }

    /// <summary>
    /// Appends a line for each tracked attribute on one member.
    /// </summary>
    /// <param name="bag">Open scan.</param>
    /// <param name="attributes">Attributes of the member. At most 64 are accepted.</param>
    /// <param name="kind">One of type, method, property, event, field.</param>
    /// <param name="member">Display name of the member.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the member has more than 64 attributes or the output exceeds 100000 lines.
    /// </exception>
    /// <remarks>Untracked attributes are skipped. The attribute type is the resolved constructor parent.</remarks>
    private static void CollectAttributes(
        ScanBag bag,
        CustomAttributeHandleCollection attributes,
        string kind,
        string member)
    {
        var seen = 0;
        foreach (var handle in attributes)
        {
            seen++;
            if (seen > MaxAttributes)
            {
                throw new InvalidOperationException(member + " has more than 64 custom attributes.");
            }

            var attribute = bag.Reader.GetCustomAttribute(handle);
            var typeName = AttributeTypeName(bag, attribute);
            if (!IsTracked(typeName))
            {
                continue;
            }

            bag.Lines.Add(FormatLine(bag.AssemblyName, kind, member, typeName));
            if (bag.Lines.Count > MaxOutput)
            {
                throw new InvalidOperationException("more than 100000 coverage attributes.");
            }
        }
    }

    /// <summary>
    /// Resolves the declaring type of a custom-attribute constructor.
    /// </summary>
    /// <param name="bag">Open scan. Supplies the reader and the signature decoder.</param>
    /// <param name="attribute">Attribute whose constructor is a method definition or a member reference.</param>
    /// <returns>The full attribute type name, including the <c>Attribute</c> suffix.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the constructor handle is nil or an unexpected kind.</exception>
    /// <remarks>This is the resolved type name BB-09 requires. Source spelling is never consulted.</remarks>
    private static string AttributeTypeName(ScanBag bag, CustomAttribute attribute)
    {
        var ctor = attribute.Constructor;
        if (ctor.IsNil)
        {
            throw new InvalidOperationException("custom attribute constructor is nil.");
        }

        if (ctor.Kind == HandleKind.MethodDefinition)
        {
            var method = bag.Reader.GetMethodDefinition((MethodDefinitionHandle)ctor);
            return TypeNames.DefinitionName(bag.Reader, method.GetDeclaringType());
        }

        if (ctor.Kind == HandleKind.MemberReference)
        {
            var member = bag.Reader.GetMemberReference((MemberReferenceHandle)ctor);
            return TypeNames.EntityName(bag.Reader, member.Parent, bag.Names);
        }

        throw new InvalidOperationException("custom attribute constructor handle is " + ctor.Kind + ".");
    }

    /// <summary>
    /// Returns whether a resolved attribute type is one of the two coverage exclusions.
    /// </summary>
    /// <param name="typeName">Full type name from <see cref="AttributeTypeName"/>.</param>
    /// <returns>
    /// True only for <c>System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute</c>
    /// and <c>System.CodeDom.Compiler.GeneratedCodeAttribute</c>.
    /// </returns>
    /// <remarks>Pure. Ordinal and case-sensitive. An empty name is false.</remarks>
    private static bool IsTracked(string typeName)
    {
        if (typeName.Length == 0)
        {
            return false;
        }

        return typeName == "System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute"
            || typeName == "System.CodeDom.Compiler.GeneratedCodeAttribute";
    }

    /// <summary>
    /// Builds the method identity, including parameter types.
    /// </summary>
    /// <param name="bag">Open scan.</param>
    /// <param name="method">Method whose name and signature are read.</param>
    /// <returns><c>Name(Param,Param)</c>. Constructors keep the <c>.ctor</c> name.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the method has more than 32 parameters.</exception>
    /// <remarks>Allocates the signature string. A signature the decoder cannot read fails the scan.</remarks>
    private static string MethodSignature(ScanBag bag, MethodDefinition method)
    {
        var signature = method.DecodeSignature(bag.Names, null);
        if (signature.ParameterTypes.Length > MaxParameters)
        {
            throw new InvalidOperationException("method has more than 32 parameters.");
        }

        return RequireIdentifier(bag.Reader.GetString(method.Name)) + "(" + JoinParameters(signature.ParameterTypes) + ")";
    }

    /// <summary>
    /// Builds a property identity. Indexers include their parameter types.
    /// </summary>
    /// <param name="bag">Open scan.</param>
    /// <param name="property">Property whose name and signature are read.</param>
    /// <returns>The property name, or <c>Name(Param)</c> when it has indexer parameters.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the indexer has more than 32 parameters.</exception>
    /// <remarks>A property with no parameters is just the name.</remarks>
    private static string PropertySignature(ScanBag bag, PropertyDefinition property)
    {
        var name = RequireIdentifier(bag.Reader.GetString(property.Name));
        var signature = property.DecodeSignature(bag.Names, null);
        if (signature.ParameterTypes.Length == 0)
        {
            return name;
        }

        if (signature.ParameterTypes.Length > MaxParameters)
        {
            throw new InvalidOperationException("indexer has more than 32 parameters.");
        }

        return name + "(" + JoinParameters(signature.ParameterTypes) + ")";
    }

    /// <summary>
    /// Joins signature parameter types with commas.
    /// </summary>
    /// <param name="parameters">Parameter type names. The caller already capped the length at 32.</param>
    /// <returns>The comma-separated list. Empty when there are no parameters.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a parameter name is null.</exception>
    /// <remarks>Pure. The loop is bounded by the caller.</remarks>
    private static string JoinParameters(System.Collections.Immutable.ImmutableArray<string> parameters)
    {
        if (parameters.Length > MaxParameters)
        {
            throw new InvalidOperationException("parameter list exceeds 32.");
        }

        var parts = new string[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            if (string.IsNullOrEmpty(parameters[index]))
            {
                throw new InvalidOperationException("parameter type name is empty.");
            }

            parts[index] = parameters[index];
        }

        return string.Join(',', parts);
    }

    /// <summary>
    /// Rejects an empty, oversized, or tab-containing identifier.
    /// </summary>
    /// <param name="value">Name read from metadata.</param>
    /// <returns><paramref name="value"/> when it can sit in one output field.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the name is empty, longer than 500, or contains a tab.</exception>
    /// <remarks>Pure. Tabs are rejected because the output format is tab-separated.</remarks>
    private static string RequireIdentifier(string value)
    {
        if (value.Length == 0 || value.Length > 500 || value.Contains('\t'))
        {
            throw new InvalidOperationException("member name is empty, too long, or contains a tab.");
        }

        return value;
    }

    /// <summary>
    /// Formats one output line.
    /// </summary>
    /// <param name="assemblyName">Simple assembly name.</param>
    /// <param name="kind">Member kind.</param>
    /// <param name="member">Member display name.</param>
    /// <param name="attribute">Resolved attribute type name.</param>
    /// <returns>Four tab-separated fields.</returns>
    /// <exception cref="InvalidOperationException">Thrown when any field is empty or contains a tab.</exception>
    /// <remarks>Pure. The caller writes the line to stdout later.</remarks>
    private static string FormatLine(string assemblyName, string kind, string member, string attribute)
    {
        if (FieldBad(assemblyName) || FieldBad(kind) || FieldBad(member) || FieldBad(attribute))
        {
            throw new InvalidOperationException("metadata line field is empty or contains a tab.");
        }

        return assemblyName + "\t" + kind + "\t" + member + "\t" + attribute;
    }

    /// <summary>
    /// Returns whether a field cannot be written into the tab-separated line.
    /// </summary>
    /// <param name="value">One field. Null is bad.</param>
    /// <returns>True when the field is null, empty, or contains a tab.</returns>
    /// <remarks>Pure.</remarks>
    private static bool FieldBad(string value)
    {
        return string.IsNullOrEmpty(value) || value.Contains('\t');
    }
}
