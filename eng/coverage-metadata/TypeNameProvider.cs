using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace CoverageMetadata;

/// <summary>
/// Decodes method and type signatures into display strings for BB-09.
/// The framework decoder calls back into this type for nested signatures.
/// That callback is capped at 8 levels because a signature is a tree and an
/// iterative decoder would have to reimplement the blob format.
/// </summary>
internal sealed class TypeNameProvider : ISignatureTypeProvider<string, object?>
{
    // Nested type specifications stop here. Attribute types in this repo are not that deep.
    private const int MaxSpecDepth = 8;

    private readonly MetadataReader reader;
    private int specDepth;

    /// <summary>
    /// Creates a provider bound to one assembly's metadata reader.
    /// </summary>
    /// <param name="reader">Reader kept for definition and reference lookups. Must not be null.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="reader"/> is null.</exception>
    /// <remarks>Does not copy the reader. The caller must not use the provider after disposing the reader.</remarks>
    internal TypeNameProvider(MetadataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        this.reader = reader;
    }

    /// <summary>
    /// Maps a primitive type code to its runtime type name.
    /// </summary>
    /// <param name="typeCode">Primitive code from a signature blob.</param>
    /// <returns>A name such as <c>System.Int32</c>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the code is not one of the known primitives.</exception>
    /// <remarks>Pure. No allocation beyond the returned interned literal, except for the unknown-code path which throws.</remarks>
    public string GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
        var name = PrimitiveName(typeCode);
        if (name.Length == 0)
        {
            throw new InvalidOperationException("unknown primitive type code " + typeCode + ".");
        }

        return name;
    }

    /// <summary>
    /// Names a type definition referenced from a signature.
    /// </summary>
    /// <param name="reader">Reader supplied by the decoder. Must be the same reader this provider was built with.</param>
    /// <param name="handle">Type definition handle.</param>
    /// <param name="rawTypeKind">ECMA type kind. A nil handle combined with 0xFF is rejected.</param>
    /// <returns>The display name from <see cref="TypeNames.DefinitionName"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="reader"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the handle cannot be named.</exception>
    /// <remarks>Reads metadata. No I/O.</remarks>
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (!ReferenceEquals(reader, this.reader))
        {
            throw new InvalidOperationException("signature reader does not match the scanner.");
        }

        if (rawTypeKind == 0xFF && handle.IsNil)
        {
            throw new InvalidOperationException("type definition handle is nil.");
        }

        return TypeNames.DefinitionName(reader, handle);
    }

    /// <summary>
    /// Names a type reference from a signature.
    /// </summary>
    /// <param name="reader">Reader supplied by the decoder.</param>
    /// <param name="handle">Type reference handle.</param>
    /// <param name="rawTypeKind">ECMA type kind. A nil handle combined with 0xFF is rejected.</param>
    /// <returns>The display name from <see cref="TypeNames.ReferenceName"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="reader"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the handle cannot be named.</exception>
    /// <remarks>Reads metadata. No I/O.</remarks>
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (rawTypeKind == 0xFF && handle.IsNil)
        {
            throw new InvalidOperationException("type reference handle is nil.");
        }

        return TypeNames.ReferenceName(reader, handle);
    }

    /// <summary>
    /// Names a single-dimensional zero-based array.
    /// </summary>
    /// <param name="elementType">Element type name. Must not be null or empty.</param>
    /// <returns><paramref name="elementType"/> plus <c>[]</c>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="elementType"/> is null or empty.</exception>
    /// <remarks>Pure.</remarks>
    public string GetSZArrayType(string elementType)
    {
        if (string.IsNullOrEmpty(elementType))
        {
            throw new ArgumentException("array element type is empty.", nameof(elementType));
        }

        return elementType + "[]";
    }

    /// <summary>
    /// Names a multi-dimensional array.
    /// </summary>
    /// <param name="elementType">Element type name. Must not be null or empty.</param>
    /// <param name="shape">Rank and bounds. Rank must be from 1 to 32.</param>
    /// <returns>The element name followed by brackets and commas for the rank.</returns>
    /// <exception cref="ArgumentException">Thrown when the element type is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the rank is outside 1 to 32.</exception>
    /// <remarks>Pure. A rank above 32 is rejected so the bracket string stays bounded.</remarks>
    public string GetArrayType(string elementType, ArrayShape shape)
    {
        if (string.IsNullOrEmpty(elementType))
        {
            throw new ArgumentException("array element type is empty.", nameof(elementType));
        }

        if (shape.Rank < 1 || shape.Rank > 32)
        {
            throw new InvalidOperationException("array rank is out of range.");
        }

        return elementType + "[" + new string(',', shape.Rank - 1) + "]";
    }

    /// <summary>
    /// Names a managed pointer (by-ref) type.
    /// </summary>
    /// <param name="elementType">Referenced type name. Must not be empty.</param>
    /// <returns><paramref name="elementType"/> plus <c>&amp;</c>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="elementType"/> is empty.</exception>
    /// <remarks>Pure.</remarks>
    public string GetByReferenceType(string elementType)
    {
        if (string.IsNullOrEmpty(elementType))
        {
            throw new ArgumentException("by-ref element type is empty.", nameof(elementType));
        }

        return elementType + "&";
    }

    /// <summary>
    /// Names an unmanaged pointer type.
    /// </summary>
    /// <param name="elementType">Pointee type name. Must not be empty.</param>
    /// <returns><paramref name="elementType"/> plus <c>*</c>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="elementType"/> is empty.</exception>
    /// <remarks>Pure.</remarks>
    public string GetPointerType(string elementType)
    {
        if (string.IsNullOrEmpty(elementType))
        {
            throw new ArgumentException("pointer element type is empty.", nameof(elementType));
        }

        return elementType + "*";
    }

    /// <summary>
    /// Names a generic instantiation.
    /// </summary>
    /// <param name="genericType">Open type name. Must not be empty.</param>
    /// <param name="typeArguments">Closed arguments. At most 16 are accepted.</param>
    /// <returns>The type name with arguments in angle brackets.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="genericType"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when there are more than 16 arguments.</exception>
    /// <remarks>Allocates the joined string. Pure.</remarks>
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
    {
        if (string.IsNullOrEmpty(genericType))
        {
            throw new ArgumentException("generic type name is empty.", nameof(genericType));
        }

        if (typeArguments.Length > 16)
        {
            throw new InvalidOperationException("generic arity exceeds 16.");
        }

        var parts = new string[typeArguments.Length];
        for (var index = 0; index < typeArguments.Length; index++)
        {
            parts[index] = typeArguments[index];
        }

        return genericType + "<" + string.Join(',', parts) + ">";
    }

    /// <summary>
    /// Names a generic method parameter.
    /// </summary>
    /// <param name="genericContext">Unused context supplied by the decoder. May be null.</param>
    /// <param name="index">Zero-based parameter index. Negative and values above 32 are rejected.</param>
    /// <returns>A token such as <c>!!0</c>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="index"/> is outside 0 to 32.</exception>
    /// <remarks>Pure. The context is accepted because the interface requires it.</remarks>
    public string GetGenericMethodParameter(object? genericContext, int index)
    {
        if (index < 0 || index > 32)
        {
            throw new InvalidOperationException("generic method parameter index is out of range.");
        }

        return "!!" + index.ToString() + ContextMark(genericContext);
    }

    /// <summary>
    /// Names a generic type parameter.
    /// </summary>
    /// <param name="genericContext">Unused context supplied by the decoder. May be null.</param>
    /// <param name="index">Zero-based parameter index. Negative and values above 32 are rejected.</param>
    /// <returns>A token such as <c>!0</c>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="index"/> is outside 0 to 32.</exception>
    /// <remarks>Pure.</remarks>
    public string GetGenericTypeParameter(object? genericContext, int index)
    {
        if (index < 0 || index > 32)
        {
            throw new InvalidOperationException("generic type parameter index is out of range.");
        }

        return "!" + index.ToString() + ContextMark(genericContext);
    }

    /// <summary>
    /// Names a modified type (required or optional modifier).
    /// </summary>
    /// <param name="modifier">Modifier type name.</param>
    /// <param name="unmodifiedType">Underlying type name. Must not be empty.</param>
    /// <param name="isRequired">True for a required modifier, false for an optional one.</param>
    /// <returns>The underlying type. Modifiers do not change member identity for this check.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="unmodifiedType"/> is empty.</exception>
    /// <remarks>
    /// The modifier is checked so a null modifier still fails, then dropped.
    /// Two members that differ only by a modopt stay one identity, which is what the diff needs.
    /// </remarks>
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
    {
        if (string.IsNullOrEmpty(unmodifiedType))
        {
            throw new ArgumentException("modified type name is empty.", nameof(unmodifiedType));
        }

        if (string.IsNullOrEmpty(modifier))
        {
            var role = isRequired ? "required modifier" : "optional modifier";
            throw new ArgumentException(role + " is empty.", nameof(modifier));
        }

        return unmodifiedType;
    }

    /// <summary>
    /// Names a pinned local type. Method signatures do not use this; it exists for the interface.
    /// </summary>
    /// <param name="elementType">Pinned type name. Must not be empty.</param>
    /// <returns><paramref name="elementType"/> plus <c> pinned</c>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="elementType"/> is empty.</exception>
    /// <remarks>Pure.</remarks>
    public string GetPinnedType(string elementType)
    {
        if (string.IsNullOrEmpty(elementType))
        {
            throw new ArgumentException("pinned type is empty.", nameof(elementType));
        }

        return elementType + " pinned";
    }

    /// <summary>
    /// Names a function-pointer signature.
    /// </summary>
    /// <param name="signature">Decoded function pointer. Parameter count above 32 is rejected.</param>
    /// <returns>The fixed token <c>function-pointer</c> plus the parameter count.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the signature has more than 32 parameters.</exception>
    /// <remarks>The full pointer shape is not needed to tell overloads apart in this repo. Pure.</remarks>
    public string GetFunctionPointerType(MethodSignature<string> signature)
    {
        if (signature.ParameterTypes.Length > 32)
        {
            throw new InvalidOperationException("function pointer has more than 32 parameters.");
        }

        return "function-pointer/" + signature.ParameterTypes.Length.ToString();
    }

    /// <summary>
    /// Decodes a nested type specification, stopping at depth 8.
    /// </summary>
    /// <param name="reader">Reader that owns <paramref name="handle"/>.</param>
    /// <param name="genericContext">Context forwarded to the decoder. May be null.</param>
    /// <param name="handle">Type specification handle.</param>
    /// <param name="rawTypeKind">ECMA type kind. A nil handle combined with 0xFF is rejected.</param>
    /// <returns>The decoded type name.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="reader"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when specification nesting exceeds 8.</exception>
    /// <remarks>
    /// Calls <see cref="TypeSpecification.DecodeSignature"/>, which calls back into this provider.
    /// The depth field is the bound. Not thread-safe.
    /// </remarks>
    public string GetTypeFromSpecification(
        MetadataReader reader,
        object? genericContext,
        TypeSpecificationHandle handle,
        byte rawTypeKind)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (rawTypeKind == 0xFF && handle.IsNil)
        {
            throw new InvalidOperationException("type specification handle is nil.");
        }

        if (specDepth >= MaxSpecDepth)
        {
            throw new InvalidOperationException("type specification depth exceeds 8.");
        }

        specDepth++;
        try
        {
            return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        }
        finally
        {
            specDepth--;
        }
    }

    /// <summary>
    /// Returns the display name for one primitive code, or empty when the code is unknown.
    /// </summary>
    /// <param name="typeCode">Primitive code from a signature.</param>
    /// <returns>A framework type name, or empty when <paramref name="typeCode"/> is not mapped.</returns>
    /// <exception cref="None.">This helper does not throw. The caller throws.</exception>
    /// <remarks>Pure. The empty return is the unknown-code signal so the caller can name the code in the exception.</remarks>
    private static string PrimitiveName(PrimitiveTypeCode typeCode)
    {
        switch (typeCode)
        {
            case PrimitiveTypeCode.Boolean:
                return "System.Boolean";
            case PrimitiveTypeCode.Byte:
                return "System.Byte";
            case PrimitiveTypeCode.Char:
                return "System.Char";
            case PrimitiveTypeCode.Double:
                return "System.Double";
            case PrimitiveTypeCode.Int16:
                return "System.Int16";
            case PrimitiveTypeCode.Int32:
                return "System.Int32";
            case PrimitiveTypeCode.Int64:
                return "System.Int64";
            case PrimitiveTypeCode.IntPtr:
                return "System.IntPtr";
            case PrimitiveTypeCode.Object:
                return "System.Object";
            case PrimitiveTypeCode.SByte:
                return "System.SByte";
            case PrimitiveTypeCode.Single:
                return "System.Single";
            case PrimitiveTypeCode.String:
                return "System.String";
            case PrimitiveTypeCode.TypedReference:
                return "System.TypedReference";
            case PrimitiveTypeCode.UInt16:
                return "System.UInt16";
            case PrimitiveTypeCode.UInt32:
                return "System.UInt32";
            case PrimitiveTypeCode.UInt64:
                return "System.UInt64";
            case PrimitiveTypeCode.UIntPtr:
                return "System.UIntPtr";
            case PrimitiveTypeCode.Void:
                return "System.Void";
            default:
                return "";
        }
    }

    /// <summary>
    /// Returns an empty suffix. The context is read so the parameter is not unused.
    /// </summary>
    /// <param name="genericContext">Decoder context. Null and non-null both produce an empty suffix.</param>
    /// <returns>Always empty. Identity does not depend on the decoder context object.</returns>
    /// <remarks>Pure. Exists so a null context is an explicit, tested input rather than an ignored parameter.</remarks>
    private static string ContextMark(object? genericContext)
    {
        _ = genericContext;
        return "";
    }
}
