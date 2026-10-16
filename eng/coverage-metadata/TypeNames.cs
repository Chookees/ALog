using System.Reflection.Metadata;

namespace CoverageMetadata;

/// <summary>
/// Turns metadata type handles into stable display names.
/// Nested types are walked outward with a fixed depth of 16.
/// The sibling <see cref="TypeNameProvider"/> decodes signatures.
/// BB-09 uses these names so an attribute is identified by its resolved type,
/// not by the spelling in source.
/// </summary>
internal static class TypeNames
{
    // A nested type deeper than this is rejected. Real logs do not nest that far.
    private const int MaxDepth = 16;

    // A single identifier longer than this is rejected rather than truncated.
    private const int MaxNameChars = 500;

    /// <summary>
    /// Builds the namespace-qualified name of a type definition, including nesting.
    /// </summary>
    /// <param name="reader">Metadata reader that owns <paramref name="handle"/>. Must not be null.</param>
    /// <param name="handle">Type definition. Nil is rejected.</param>
    /// <returns>The display name, outermost type first, separated by dots.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="reader"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the handle is nil, a name is empty or longer than 500 characters,
    /// or nesting exceeds 16.
    /// </exception>
    /// <remarks>Reads metadata only. No allocation beyond the returned string. Not thread-safe with a shared reader.</remarks>
    internal static string DefinitionName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (handle.IsNil)
        {
            throw new InvalidOperationException("type definition handle is nil.");
        }

        var names = new string[MaxDepth];
        var depth = 0;
        var current = handle;
        var namespaceName = "";
        for (var step = 0; step < MaxDepth; step++)
        {
            var type = reader.GetTypeDefinition(current);
            names[depth] = RequireName(reader.GetString(type.Name), "type");
            depth++;
            var declaring = type.GetDeclaringType();
            if (declaring.IsNil)
            {
                namespaceName = reader.GetString(type.Namespace);
                break;
            }

            current = declaring;
            if (step == MaxDepth - 1)
            {
                throw new InvalidOperationException("nested type depth exceeds 16.");
            }
        }

        return Join(namespaceName, names, depth);
    }

    /// <summary>
    /// Builds the name of a type reference, following a nested resolution scope.
    /// </summary>
    /// <param name="reader">Metadata reader that owns <paramref name="handle"/>. Must not be null.</param>
    /// <param name="handle">Type reference. Nil is rejected.</param>
    /// <returns>The display name. The namespace comes from the outermost reference.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="reader"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the handle is nil, a name is empty or too long, or nesting exceeds 16.
    /// </exception>
    /// <remarks>Reads metadata only. No I/O. Not thread-safe with a shared reader.</remarks>
    internal static string ReferenceName(MetadataReader reader, TypeReferenceHandle handle)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (handle.IsNil)
        {
            throw new InvalidOperationException("type reference handle is nil.");
        }

        var names = new string[MaxDepth];
        var depth = 0;
        var current = handle;
        var namespaceName = "";
        for (var step = 0; step < MaxDepth; step++)
        {
            var type = reader.GetTypeReference(current);
            names[depth] = RequireName(reader.GetString(type.Name), "type reference");
            depth++;
            if (type.ResolutionScope.Kind != HandleKind.TypeReference)
            {
                namespaceName = reader.GetString(type.Namespace);
                break;
            }

            current = (TypeReferenceHandle)type.ResolutionScope;
            if (step == MaxDepth - 1)
            {
                throw new InvalidOperationException("nested type reference depth exceeds 16.");
            }
        }

        return Join(namespaceName, names, depth);
    }

    /// <summary>
    /// Resolves the parent of an attribute constructor to a type name.
    /// </summary>
    /// <param name="reader">Metadata reader. Must not be null.</param>
    /// <param name="handle">Type definition, type reference, or type specification.</param>
    /// <param name="names">Signature decoder used when the parent is a type specification. Must not be null.</param>
    /// <returns>The resolved type name.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="reader"/> or <paramref name="names"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the handle kind is not a type.</exception>
    /// <remarks>
    /// A type specification is decoded by <paramref name="names"/>. That decoder calls back into
    /// itself for nested specifications and stops at depth 8. No I/O.
    /// </remarks>
    internal static string EntityName(MetadataReader reader, EntityHandle handle, TypeNameProvider names)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(names);
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            return DefinitionName(reader, (TypeDefinitionHandle)handle);
        }

        if (handle.Kind == HandleKind.TypeReference)
        {
            return ReferenceName(reader, (TypeReferenceHandle)handle);
        }

        if (handle.Kind == HandleKind.TypeSpecification)
        {
            return reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(names, null);
        }

        throw new InvalidOperationException("attribute parent handle is " + handle.Kind + ".");
    }

    /// <summary>
    /// Rejects an empty or oversized metadata string.
    /// </summary>
    /// <param name="value">String read from the metadata heap.</param>
    /// <param name="role">What the string names, used in the exception text.</param>
    /// <returns><paramref name="value"/> when it is usable.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the string is empty or longer than 500 characters.</exception>
    /// <remarks>No I/O. Pure.</remarks>
    private static string RequireName(string value, string role)
    {
        if (value.Length == 0 || value.Length > MaxNameChars)
        {
            throw new InvalidOperationException(role + " name length is out of range.");
        }

        return value;
    }

    /// <summary>
    /// Joins a namespace and a nested-name stack into one display name.
    /// </summary>
    /// <param name="namespaceName">Namespace of the outermost type. Empty for the global namespace.</param>
    /// <param name="innerFirst">Names collected from the inside outward. Index 0 is the innermost.</param>
    /// <param name="depth">How many entries in <paramref name="innerFirst"/> are set. 1 to 16.</param>
    /// <returns>The dotted name.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="depth"/> is outside 1 to 16.</exception>
    /// <remarks>Allocates the returned string. Pure.</remarks>
    private static string Join(string namespaceName, string[] innerFirst, int depth)
    {
        if (depth < 1 || depth > MaxDepth)
        {
            throw new InvalidOperationException("type name depth is out of range.");
        }

        var parts = new string[depth];
        for (var index = 0; index < depth; index++)
        {
            parts[index] = innerFirst[depth - 1 - index];
        }

        var body = string.Join('.', parts);
        if (namespaceName.Length == 0)
        {
            return body;
        }

        return namespaceName + "." + body;
    }
}
