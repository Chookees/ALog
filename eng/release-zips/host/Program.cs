/// <summary>
/// Publish entry point for the Full zip host. The binary is never shipped in the zip.
/// Exists only so <c>dotnet publish -r</c> materializes RID-specific dependency DLLs.
/// </summary>
internal static class Program
{
    /// <summary>
    /// No-op main. Publish collects referenced assemblies; this method is discarded from the zip.
    /// </summary>
    /// <param name="args">Unused command-line arguments.</param>
    /// <returns>Always 0.</returns>
    private static int Main(string[] args) => 0;
}
