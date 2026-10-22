// ALog.Comparison: ALog comparison harness (arc §8c BEN). See README.md.
//   ALog.Comparison run --runs R --agent NAME --out DIR [--seed S]   all cells, one process each (use run.sh)
//   ALog.Comparison cell <lib> <scenario> <workdir>                  one cell in this process, prints "RESULT {json}"
//   ALog.Comparison micro [BenchmarkDotNet args]                     BenchmarkDotNet (MemoryDiagnoser, ThreadingDiagnoser)
//   ALog.Comparison versions                                         package versions as JSON
using System.Text.Json;

namespace AlogComparison;

/// <summary>Command-line entry.</summary>
public static class Program
{
    /// <summary>Dispatches the sub-command.</summary>
    public static int Main(string[] args)
    {
        if (args.Length == 0)
            return Usage();
        switch (args[0])
        {
            case "run":
                return Runner.Run(args[1..]);
            case "cell" when args.Length == 4:
                return Cell.Run(args[1], args[2], args[3]);
            case "micro":
                Micro.Run(args[1..]);
                return 0;
            case "versions":
                Console.Out.WriteLine(JsonSerializer.Serialize(Libs.Versions()));
                return 0;
            default:
                return Usage();
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: ALog.Comparison run --runs R --agent NAME --out DIR [--seed S] | cell <lib> <scenario> <workdir> | micro [...] | versions");
        Console.Error.WriteLine("libs: " + string.Join(", ", Libs.All) + "; scenarios: " + string.Join(", ", Scenario.All));
        return 2;
    }
}
