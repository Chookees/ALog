namespace OriginNoPdb;

/// <summary>Throws from an assembly built without a PDB, so neither origin path has a file or a line.</summary>
public static class Thrower
{
    /// <summary>Throws <see cref="InvalidOperationException"/> with the message <c>nopdb</c>.</summary>
    /// <remarks>Does not return. The missing PDB is the point: the stack frame has a method and no file.</remarks>
    public static void Throw()
        => throw new InvalidOperationException("nopdb");
}
