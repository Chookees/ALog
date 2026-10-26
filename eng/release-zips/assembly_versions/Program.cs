using System.Diagnostics;
using System.Reflection;

/// <summary>
/// Prints FileVersion and AssemblyInformationalVersion for each DLL path on the command line.
/// Used by the release-zip script so version gates do not need extra Python packages.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Loads each path and writes one line: path|FileVersion=...|InformationalVersion=...
    /// </summary>
    /// <param name="args">Absolute or relative DLL paths. Empty is an error.</param>
    /// <returns>0 on success; 1 when args are empty or a path cannot be read.</returns>
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: AssemblyVersions <dll> [dll...]");
            return 1;
        }

        foreach (var path in args)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"missing: {path}");
                return 1;
            }

            var fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion ?? string.Empty;
            string informational;
            try
            {
                var asm = Assembly.LoadFrom(Path.GetFullPath(path));
                informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion ?? string.Empty;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"informational read failed for {path}: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }

            Console.WriteLine($"{path}|FileVersion={fileVersion}|InformationalVersion={informational}");
        }

        return 0;
    }
}
