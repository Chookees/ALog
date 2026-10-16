namespace CoverageMetadata;

/// <summary>
/// Command line for the BB-09 metadata reader.
/// Pass one to eight assembly paths. Each tracked attribute is printed as
/// <c>assembly\tkind\tmember\tattribute</c> on stdout, sorted.
/// A missing or unreadable assembly exits 1 so the CI job fails instead of skipping.
/// This project is not in <c>ALog.sln</c> and is not packed.
/// </summary>
internal static class Program
{
    // Eight is enough for the two libraries on base and head when a caller batches them.
    private const int MaxAssemblies = 8;

    /// <summary>
    /// Scans the assemblies named on the command line.
    /// </summary>
    /// <param name="args">Assembly paths. Null is rejected. One to eight paths are accepted.</param>
    /// <returns>0 when every assembly was read, 1 when a read failed, 2 when the arguments are wrong.</returns>
    /// <exception cref="None.">Read failures are caught and printed to stderr.</exception>
    /// <remarks>Reads each assembly. Writes the sorted list to stdout and errors to stderr.</remarks>
    public static int Main(string[] args)
    {
        if (args is null || args.Length < 1 || args.Length > MaxAssemblies)
        {
            Console.Error.WriteLine("usage: CoverageMetadata <assembly> [<assembly> ...]");
            return 2;
        }

        try
        {
            var lines = ScanAll(args);
            lines.Sort(StringComparer.Ordinal);
            WriteUnique(lines);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or BadImageFormatException or ArgumentException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Scans each path and concatenates the lines.
    /// </summary>
    /// <param name="args">One to eight assembly paths. The caller already checked the count.</param>
    /// <returns>Every tracked attribute line. Duplicates may still be present; <see cref="WriteUnique"/> drops them.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a path cannot be read. See <see cref="AssemblyScanner.Scan"/>.</exception>
    /// <remarks>Reads each file. The loop is bounded by <paramref name="args"/>, which is at most 8.</remarks>
    private static List<string> ScanAll(string[] args)
    {
        var lines = new List<string>();
        for (var index = 0; index < args.Length && index < MaxAssemblies; index++)
        {
            var scanned = AssemblyScanner.Scan(args[index]);
            if (lines.Count + scanned.Count > 100000)
            {
                throw new InvalidOperationException("more than 100000 coverage attributes.");
            }

            lines.AddRange(scanned);
        }

        return lines;
    }

    /// <summary>
    /// Prints sorted lines, skipping a line that repeats the one before it.
    /// </summary>
    /// <param name="lines">Lines already sorted with <see cref="StringComparer.Ordinal"/>.</param>
    /// <remarks>Writes stdout. A repeated member and attribute is one finding, so duplicates are dropped.</remarks>
    private static void WriteUnique(List<string> lines)
    {
        var previous = "";
        for (var index = 0; index < lines.Count && index < 100000; index++)
        {
            var line = lines[index];
            if (line == previous)
            {
                continue;
            }

            Console.WriteLine(line);
            previous = line;
        }
    }
}
